// InStoreLink —— party / 招募 / 选曲界面补丁
//
// 这一层把"公网上的另一个玩家"塞进游戏本体的店内匹配流程里：
//   Client 构造            → 开始每 10 秒拉一次大厅的招募列表
//   RecvStartRecruit       → 歌曲没装就别显示这条招募（否则界面会崩）
//   MusicSelectProcess.*   → 让"店内マッチング"那一栏列出所有房间、能选、能进
//   FlushPendingRecruits   → 把轮询线程收到的房间在主线程喂给游戏（见 LinkRuntime）
//
// 上游 https://github.com/MuNET-OSS/NyanLink （MIT），本文件是它的等价重写。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using DB;
using HarmonyLib;
using MAI2.Util;
using Mai2.Mai2Cue;
using Manager;
using Manager.Party.Party;
using Monitor;
using PartyLink;
using Process;
using UnityEngine;

namespace InStoreLink
{
    // 这几个类型是 MusicSelectProcess 的嵌套类型（C# 5 没有 using static，用别名代替）
    using CombineMusicSelectData = Process.MusicSelectProcess.CombineMusicSelectData;
    using MusicSelectData = Process.MusicSelectProcess.MusicSelectData;
    using SubSequence = Process.MusicSelectProcess.SubSequence;

    public static class PatchesParty
    {
        private static MethodInfo _recvStartRecruit;
        private static MethodInfo _recvFinishRecruit;
        private static Client _gameClient;
        private static bool _reflectionWarned;
        private const float RefreshSeconds = 10f;   // 多久替"我的房间"续一次寿命（大厅 TTL 30s）
        /// <summary>同一个房间最多续报这么久；超过就停手，让大厅的 TTL 收掉它。</summary>
        private const float RefreshMaxSeconds = 600f;
        private static float _nextReconcile;
        private static float _nextRefresh;
        private static float _refreshSince = -1f;
        private static string _refreshKey;
        private static bool _refreshGaveUp;
        private static readonly Dictionary<string, float> _deliveredAt = new Dictionary<string, float>();
        /// <summary>被我们拒掉的房间（歌没装），别再每 2 秒重喂一遍。</summary>
        private static readonly ConcurrentDictionary<string, float> _rejected =
            new ConcurrentDictionary<string, float>();

        // ------------------------------------------------------------ 招募列表

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Client), MethodType.Constructor, typeof(string), typeof(PartyLink.Party.InitParam))]
        public static void PostClientCtor(Client __instance, string name, PartyLink.Party.InitParam initParam)
        {
            // 游戏本体建 party 客户端 = 我们可以开始轮询大厅了
            _gameClient = __instance;
            LinkLog.Info("本体 party 客户端已建立，开始轮询招募列表");
            LinkRuntime.StartRecruitPolling(__instance);
        }

        /// <summary>
        /// 主线程对账：拿大厅快照和游戏现在认得的房间比一比，缺的补、多的删。
        /// 由 CommonMonitor.ViewUpdate 的 Postfix 每帧调用（见 PatchesNet），内部限流 0.5 秒一次。
        ///
        /// 为什么不是"每轮把列表整体重喂一遍"（上游的做法）：游戏会把每条 StartRecruit
        /// 当成"有人开新房"来提示 —— 于是每 10 秒响一次提示音。这里改成只在游戏里
        /// **确实缺这个房间**时才补一次（并且 10 秒内不重复补，防止游戏还没收下就反复补）。
        /// </summary>
        public static void ReconcileRecruits()
        {
            Client client = _gameClient;
            if (client == null) return;

            // 限流：每帧做一次列表对账没必要
            if (UnityEngine.Time.time < _nextReconcile) return;
            _nextReconcile = UnityEngine.Time.time + 0.5f;

            if (_recvStartRecruit == null)
                _recvStartRecruit = typeof(Client).GetMethod("RecvStartRecruit",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            if (_recvFinishRecruit == null)
                _recvFinishRecruit = typeof(Client).GetMethod("RecvFinishRecruit",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            // 反射都拿不到的话，房间根本没法喂回游戏
            if (_recvStartRecruit == null || _recvFinishRecruit == null)
            {
                if (!_reflectionWarned)
                {
                    _reflectionWarned = true;
                    LinkLog.Error("拿不到 Client.RecvStartRecruit / RecvFinishRecruit —— "
                                  + "对方开的房间没法显示（游戏版本不匹配？）");
                }
                return;
            }

            IManager manager = LinkRuntime.PartyMan;
            if (manager == null) return;
            Dictionary<string, RecruitInfo> want = LinkRuntime.LastRecruits;
            if (want == null) return;

            // 大厅那边的房间只有 30 秒 TTL，而本体只在"开始招募"那一下发一次 StartRecruit
            // （实测 5 分钟里总共 6 次，不是周期广播）—— 不续报的话，开好房干等半分钟，
            // 大厅里那条房间就悄悄没了。主线程这边顺手隔 10 秒重报一次。
            if (UnityEngine.Time.time >= _nextRefresh)
            {
                _nextRefresh = UnityEngine.Time.time + RefreshSeconds;
                try { RefreshMyRecruit(); }
                catch (Exception ex) { LinkLog.Debug("房间续报出错：" + ex.Message); }
            }

            List<RecruitInfo> have = manager.GetRecruitListWithoutMe();
            if (have == null) have = new List<RecruitInfo>();

            // 大厅里已经没有的房间，把它留下的两本账一起清掉（以前 _deliveredAt 只在一半
            // 分支里清，被拒的房间那条记录就永远留在字典里了）
            PruneBookkeeping(want);

            // 1) 大厅里有、游戏里没有 → 补进去
            foreach (KeyValuePair<string, RecruitInfo> kv in want)
            {
                RecruitInfo room = kv.Value;
                if (room == null) continue;
                // 自己的房间不用喂回自己
                if (IsMyOwnRoom(room)) continue;
                if (ContainsRoom(have, room)) continue;
                // 刚才已经拒过的（歌没装）就别再喂了：喂进去也只会被 PreRecvStartRecruit
                // 弹回来，然后每 2 秒重来一次，白刷日志
                if (_rejected.ContainsKey(kv.Key)) continue;

                float last;
                if (_deliveredAt.TryGetValue(kv.Key, out last) &&
                    UnityEngine.Time.time - last < 2f) continue;       // 刚喂过，等游戏收下

                try
                {
                    Packet packet = new Packet(room.IpAddress);
                    packet.encode(new StartRecruit(room));
                    _recvStartRecruit.Invoke(client, new object[] { packet });
                    _deliveredAt[kv.Key] = UnityEngine.Time.time;
                    LinkLog.Debug("补一个房间进游戏：" + kv.Key);
                }
                catch (Exception ex)
                {
                    LinkLog.Error("补房间失败 " + kv.Key + "：" + ex.Message);
                }
            }

            // 2) 游戏里有、大厅里已经没有 → 从游戏里去掉
            foreach (RecruitInfo room in have)
            {
                if (room == null) continue;
                string id = LinkRuntime.Identity(room);
                if (want.ContainsKey(id)) continue;
                try
                {
                    Packet packet = new Packet(room.IpAddress);
                    packet.encode(new FinishRecruit(room));
                    _recvFinishRecruit.Invoke(client, new object[] { packet });
                    _deliveredAt.Remove(id);
                    LinkLog.Debug("房间已关，从游戏里去掉：" + id);
                }
                catch (Exception ex)
                {
                    LinkLog.Error("去掉房间失败 " + id + "：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 房主还在等的时候，隔 RefreshSeconds 秒把"我的房间"往大厅重报一次。
        ///
        /// 为什么需要：大厅的房间是 30 秒 TTL，而本体只在开始招募那一下发一次
        /// StartRecruit。不续报的话，开好房干等 30 秒，大厅里那条房间就没了 ——
        /// 后来的人（以及每 10 秒轮询一次的自己）都看不到了。
        /// 读本体状态必须在主线程，所以这一步挂在 ReconcileRecruits 里，不另起线程。
        /// </summary>
        private static void RefreshMyRecruit()
        {
            IManager manager = LinkRuntime.PartyMan;
            LinkClient client = LinkRuntime.Client;
            if (manager == null || client == null || LinkRuntime.Config == null) return;

            List<RecruitInfo> all = manager.GetRecruitList();     // 这一份是**含自己**的
            if (all == null || all.Count == 0) return;
            RecruitInfo mine = null;
            foreach (RecruitInfo r in all)
            {
                if (r != null && IsMyOwnRoom(r)) { mine = r; break; }
            }
            if (mine == null) return;                             // 我没在招募，没什么可续的

            // 保险：本体什么时候把"我的房间"从列表里去掉，我们没法 100% 确定。
            // 万一某条路径不发 FinishRecruit，续报就会让一条已经没用的房间永远挂在
            // 大厅里 —— 所以给一个上限，到点就停手，交回给 TTL。
            string key = LinkRuntime.Identity(mine);
            if (_refreshKey != key)
            {
                _refreshKey = key;                                // 换了房间，重新计时
                _refreshSince = UnityEngine.Time.time;
                _refreshGaveUp = false;
            }
            if (UnityEngine.Time.time - _refreshSince > RefreshMaxSeconds)
            {
                if (!_refreshGaveUp)
                {
                    _refreshGaveUp = true;
                    LinkLog.Warn("这个房间已经续报超过 " + (int)RefreshMaxSeconds
                                 + " 秒，停止续报，让大厅自己超时收回（防幽灵房间）");
                }
                return;
            }

            RecruitRecordOut record = new RecruitRecordOut();
            record.Keychip = client.Keychip;
            record.RecruitInfo = mine;
            string url = LinkLobby.Combine(LinkRuntime.Config.LobbyUrl, "/recruit/start");
            LinkLobby.PostAsync(url, JsonUtility.ToJson(record), delegate(string body, Exception err)
            {
                if (err != null) LinkLog.Debug("房间续报失败：" + err.Message);
            });
            LinkLog.Debug("房间续报（刷新大厅里的房间寿命）");
        }

        /// <summary>大厅里已经没有的房间，把它在 _deliveredAt / _rejected 里的记录一并清掉。</summary>
        private static void PruneBookkeeping(Dictionary<string, RecruitInfo> want)
        {
            if (_deliveredAt.Count > 0)
            {
                List<string> gone = null;
                foreach (string key in _deliveredAt.Keys)
                {
                    if (want.ContainsKey(key)) continue;
                    if (gone == null) gone = new List<string>();
                    gone.Add(key);
                }
                if (gone != null)
                {
                    foreach (string key in gone) _deliveredAt.Remove(key);
                }
            }

            foreach (string key in new List<string>(_rejected.Keys))
            {
                if (!want.ContainsKey(key))
                {
                    float ignored;
                    _rejected.TryRemove(key, out ignored);
                }
            }
        }

        private static bool ContainsRoom(List<RecruitInfo> list, RecruitInfo room)
        {
            string id = LinkRuntime.Identity(room);
            foreach (RecruitInfo r in list)
            {
                if (r == null) continue;
                if (LinkRuntime.Identity(r) == id) return true;
            }
            return false;
        }

        /// <summary>list 里的每个房间是否都还在 fresh 里（用来判断"显示快照"有没有过期）。</summary>
        private static bool AllRoomsIn(List<RecruitInfo> list, List<RecruitInfo> fresh)
        {
            if (fresh == null) return false;
            foreach (RecruitInfo r in list)
            {
                if (r == null) continue;
                if (!ContainsRoom(fresh, r)) return false;
            }
            return true;
        }

        /// <summary>
        /// 这个房间是不是我自己开的？（RecruitInfo.IpAddress 是游戏自己的 IpAddress 结构，
        /// 所以按原始字节比，别拿 uint 直接比 —— 类型不同，编译不过。）
        /// </summary>
        private static bool IsMyOwnRoom(RecruitInfo room)
        {
            if (LinkRuntime.Client == null) return false;
            try
            {
                byte[] mine = LinkStub.ToIp(LinkRuntime.Client.StubIp).GetAddressBytes();
                byte[] theirs = room.IpAddress.GetAddressBytes();
                if (theirs == null || theirs.Length != mine.Length) return false;
                for (int i = 0; i < theirs.Length; i++)
                {
                    if (theirs[i] != mine[i]) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Client), "RecvStartRecruit", typeof(Packet))]
        public static bool PreRecvStartRecruit(Packet packet)
        {
            StartRecruit start = packet.getParam<StartRecruit>();
            RecruitInfo info = start == null ? null : start.RecruitInfo;
            if (info == null) return false;

            LinkLog.Info("收到招募：" + JsonUtility.ToJson(info));

            // 歌没装的话，本体在后面的流程里会崩，这里直接拒掉并提示
            if (Singleton<DataManager>.Instance.GetMusic(info.MusicID) == null)
            {
                // 记一笔：大厅那边这条房间还在的话，对账每 2 秒会再喂一次 ——
                // 不记住就是无限重试 + 无限刷日志。房间从大厅消失时会自动清掉。
                _rejected[LinkRuntime.Identity(info)] = UnityEngine.Time.time;
                LinkLog.Error("对方选的歌（ID " + info.MusicID + "）你没装，这条招募被忽略。");
                if (info.MechaInfo != null && info.MechaInfo.UserNames != null)
                    LinkLog.Error("要和 " + string.Join(" / ", info.MechaInfo.UserNames) + " 联机，" +
                                  "请确认游戏版本和 option 包一致。");
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------ 选曲界面

        private static MethodInfo _socketError;
        private static bool _socketErrorWarned;

        /// <summary>
        /// 加入失败时，走**本体自己的**失败路径。
        ///
        /// 为什么不是"给那个 socket 触发 Completed"：`ConnectSocket.Execute_Connect` 只检查
        /// `_connectDone` 这个布尔（本体的 `ConnectCompletedEvent` 就只是把它置真），**不看
        /// `SocketError`**；而 `Party.Client.Execute_Connect` 又是先判 `isActive()`（= 状态机
        /// 到了 Active）再判 `isError()`。所以只要触发 Completed，游戏就认定"连上了"，
        /// 然后卡在联机选曲那边 —— 比不触发还糟。
        ///
        /// 正确做法是调 `SocketBase.error(message, no)`：它会置 `_isError` 并关掉 socket，
        /// 下一帧 `Party.Client.Execute_Connect` 就会走 `Client.error()` → 进本体的错误状态。
        ///
        /// 这个 Prefix 每帧都会被调用（连不上时状态机一直停在 Connect），所以：
        ///   有失败要报 → 报给本体，并**跳过原方法**（原方法会看 `_connectDone` 进 Active）
        ///   没有       → 原样放行
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PartyLink.ConnectSocket), "Execute_Connect")]
        public static bool PreConnectSocketExecute(PartyLink.ConnectSocket __instance)
        {
            string why = LinkClient.TakeJoinFailure();
            if (why == null) return true;

            if (_socketError == null)
            {
                // error 是 protected（family），只能反射拿
                _socketError = typeof(PartyLink.SocketBase).GetMethod("error",
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(int) }, null);
            }
            if (_socketError == null)
            {
                if (!_socketErrorWarned)
                {
                    _socketErrorWarned = true;
                    LinkLog.Error("拿不到 SocketBase.error —— 加入失败时只能退回旧行为"
                                  + "（界面停在连接中，但至少不会误判成连上）");
                }
                return true;                 // 放行原方法：它不会进 Active（_connectDone 一直是假）
            }

            try
            {
                _socketError.Invoke(__instance, new object[] { "InStoreLink：" + why, 0 });
                LinkLog.Info("已把加入失败交回本体处理：" + why);
            }
            catch (Exception ex)
            {
                LinkLog.Error("通知本体连接失败时出错：" + ex.Message);
                return true;
            }
            return false;                    // 跳过原方法，别让它把状态推到 Active
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MusicSelectProcess), "OnStart")]
        public static bool PreMusicSelectOnStart(MusicSelectProcess __instance)
        {
            // 每次进选曲界面重置状态（上游同款：房间列表变了要重新刷新）
            LinkRuntime.MusicIdSum = 0;
            LinkRuntime.SideMessageFlag = false;
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MusicSelectProcess), "PartyExec")]
        public static void PostPartyExec(MusicSelectProcess __instance)
        {
            IManager manager = LinkRuntime.PartyMan;
            if (manager == null) return;

            // 房间列表有变化（曲目 ID 之和变了）→ 让本体重画列表
            List<RecruitInfo> withoutMe = manager.GetRecruitListWithoutMe();
            int sum = 0;
            foreach (RecruitInfo r in withoutMe) sum += r.MusicID;
            if (LinkRuntime.MusicIdSum != sum)
            {
                LinkRuntime.MusicIdSum = sum;
                __instance.IsConnectingMusic = false;
            }

            if (__instance.IsConnectingMusic && __instance.RecruitData != null &&
                __instance.IsConnectionFolder())
            {
                // 房主选曲界面右侧那行"谁在等"的提示
                MechaInfo mecha = __instance.RecruitData.MechaInfo;
                List<string> names = new List<string>();
                if (mecha != null && mecha.UserNames != null && mecha.FumenDifs != null)
                {
                    for (int i = 0; i < mecha.UserNames.Length && i < mecha.FumenDifs.Length; i++)
                    {
                        if (mecha.FumenDifs[i] != -1) names.Add(mecha.UserNames[i]);
                    }
                }
                string text = "联机房间：" + string.Join(" / ", names.ToArray());
                if (__instance.MonitorArray != null)
                {
                    for (int i = 0; i < __instance.MonitorArray.Length; i++)
                    {
                        if (__instance.IsEntry(i)) __instance.MonitorArray[i].SetSideMessage(text);
                    }
                }
                LinkRuntime.SideMessageFlag = true;
            }
            else if (!__instance.IsConnectionFolder() && LinkRuntime.SideMessageFlag)
            {
                if (__instance.MonitorArray != null)
                {
                    for (int i = 0; i < __instance.MonitorArray.Length; i++)
                    {
                        if (__instance.IsEntry(i))
                            __instance.MonitorArray[i].SetSideMessage(CommonMessageID.Scroll_Music_Select.GetName());
                    }
                }
                LinkRuntime.SideMessageFlag = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MusicSelectProcess), "RecruitData", MethodType.Getter)]
        public static void PostRecruitData(MusicSelectProcess __instance, ref RecruitInfo __result)
        {
            // 站在"店内マッチング"里时，光标停在第几条就用第几个房间
            if (!__instance.IsConnectionFolder() || __result == null) return;
            IManager manager = LinkRuntime.PartyMan;
            if (manager == null) return;
            // 必须用"真正翻译进联机歌曲列表"的那份（ConnectList）：大厅里的房间如果有
            // 装不了的歌会被 ApplyConnectData 跳过，那时按原始列表取下标就会取错房间 ——
            // 显示的是 A 的歌、进去的是 B 的房间。
            List<RecruitInfo> list = LinkRuntime.ConnectList;
            List<RecruitInfo> fresh = manager.GetRecruitListWithoutMe();
            // ConnectList = 上一次真正画出来的顺序（可能是子集：装不了的歌会被跳过），
            // 但只要它还是"当前这批房间"的子集就可以用；跟当前房间对不上（房间换了、
            // 列表还没重建）就先用最新列表，免得中间那张卡片显示的还是旧房间。
            if (list == null || list.Count == 0 || !AllRoomsIn(list, fresh)) list = fresh;
            if (list == null) return;
            if (__instance.CurrentMusicSelect >= 0 && __instance.CurrentMusicSelect < list.Count)
                __result = list[__instance.CurrentMusicSelect];
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MusicSelectProcess), "IsConnectStart")]
        public static bool PreIsConnectStart(MusicSelectProcess __instance,
            List<CombineMusicSelectData> ____connectCombineMusicDataList,
            SubSequence[] ____currentPlayerSubSequence, ref bool __result)
        {
            __result = false;
            IManager manager = LinkRuntime.PartyMan;
            if (manager == null) return false;

            List<RecruitInfo> recruits = manager.GetRecruitListWithoutMe();
            if (recruits == null) recruits = new List<RecruitInfo>();
            // 本体原本靠"对方的 IP 是不是本机"来判断，这里直接按"有没有房间"来判断。
            // 注意：判断依据必须是**游戏现在真的有哪些房间**（recruits），不能拿 ConnectList ——
            // 那是"上一次显示过的顺序"，可能是空的/过期的（踩过：房间明明喂进去了，分类栏却一直空着）。
            if (!__instance.IsConnectingMusic && recruits.Count > 0)
            {
                // 关键顺序：**先按最新房间列表重建一遍"联机歌曲列表"**（顺便刷新 ConnectList 里
                // 真正的显示顺序），再按光标取房间。反过来做（先按旧快照取房间、再重建）会在
                // 房间里有两个以上时取错：旧快照只有 1 项，光标在第二行就被夹回第 0 项 →
                // 明明选了真朋友那一行，实际连的却是另一个房间（踩过）。
                ApplyConnectData(__instance, ____connectCombineMusicDataList, ____currentPlayerSubSequence);

                List<RecruitInfo> shown = LinkRuntime.ConnectList;
                if (shown == null || shown.Count == 0) shown = recruits;
                int index = __instance.CurrentMusicSelect;
                if (index < 0 || index >= shown.Count) index = 0;
                RecruitInfo recruit = shown[index];
                LinkLog.Info("选曲界面拿到房间数据（光标 " + index + "/" + (shown.Count - 1) + "）："
                             + JsonUtility.ToJson(recruit));
                if (LinkRuntime.SetRecruitData != null)
                    LinkRuntime.SetRecruitData.Invoke(__instance, new object[] { recruit });
                __result = true;
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MusicSelectProcess), "SetConnectData")]
        public static bool PreSetConnectData(MusicSelectProcess __instance,
            List<CombineMusicSelectData> ____connectCombineMusicDataList,
            SubSequence[] ____currentPlayerSubSequence)
        {
            ApplyConnectData(__instance, ____connectCombineMusicDataList, ____currentPlayerSubSequence);
            return false;
        }

        /// <summary>
        /// 把"大厅里的房间列表"翻译成游戏本体的联机歌曲列表。
        /// 上游 preSetConnectData 的等价实现。
        /// </summary>
        private static void ApplyConnectData(MusicSelectProcess instance,
            List<CombineMusicSelectData> connectList, SubSequence[] playerSubSequence)
        {
            if (connectList == null) return;
            connectList.Clear();
            if (LinkRuntime.SetConnectCategoryEnable != null)
                LinkRuntime.SetConnectCategoryEnable.Invoke(instance, new object[] { false });

            IManager manager = LinkRuntime.PartyMan;
            List<RecruitInfo> recruits = manager == null ? null : manager.GetRecruitListWithoutMe();
            List<RecruitInfo> shown = new List<RecruitInfo>();
            if (recruits != null)
            {
                foreach (RecruitInfo item in recruits)
                {
                    // 整条房间的翻译都兜一层：`GetNotesList()[musicId]` 是按 musicID 索引的，
                    // 歌不在这台机器的曲库/谱面表里时，可能是 null，也可能直接抛
                    // IndexOutOfRange / KeyNotFound —— 而这里是 Unity 主线程，
                    // 抛出去就是整局游戏崩掉（上游正是在这里崩的）。
                    // 兜住之后：跳过这一条房间，其余房间照常显示。
                    try
                    {
                        int musicId = item.MusicID;
                        var music = Singleton<DataManager>.Instance.GetMusic(musicId);
                        if (music == null)
                        {
                            LinkLog.Warn("曲目 " + musicId + " 不在本机曲库里，这个房间先不显示");
                            continue;
                        }

                        CombineMusicSelectData combine = new CombineMusicSelectData();
                        var notes = Singleton<NotesListManager>.Instance.GetNotesList()[musicId];
                        // 没有谱面列表就没法构歌单（上游这里直接空引用崩游戏，我们跳过这条房间）
                        if (notes == null || notes.NotesList == null)
                        {
                            LinkLog.Warn("曲目 " + musicId + " 没有谱面列表，这个房间先不显示");
                            continue;
                        }
                        if (musicId < 10000) combine.existStandardScore = true;
                        else if (musicId > 10000 && musicId < 20000) combine.existDeluxeScore = true;

                        for (int i = 0; i < 2; i++)
                            combine.musicSelectData.Add(new MusicSelectData(music, notes.NotesList, 0));
                        connectList.Add(combine);
                        // 下标映射就在这一行定下来：ConnectList 的第 n 项 = 光标第 n 格
                        shown.Add(item);

                        try
                        {
                            string thumbnail = music.thumbnailName;
                            for (int j = 0; j < instance.MonitorArray.Length; j++)
                            {
                                if (!instance.IsEntry(j)) continue;
                                instance.MonitorArray[j].SetRecruitInfo(thumbnail);
                                SoundManager.PlaySE(Cue.SE_INFO_NORMAL, j);
                            }
                        }
                        catch (Exception) { /* 上游也是这么裸着吞的 */ }

                        instance.IsConnectingMusic = true;
                    }
                    catch (Exception ex)
                    {
                        LinkLog.Warn("房间（曲目 " + item.MusicID + "）翻译失败，跳过：" + ex.Message);
                    }
                }
            }
            // 记下"这次真正显示出来的是哪些房间、什么顺序"，供 RecruitData getter 按光标取
            LinkRuntime.ConnectList = shown;

            // 一个房间都没有时也要放一格占位，否则那一栏是空的、光标没地方停
            if (recruits == null || recruits.Count == 0)
            {
                CombineMusicSelectData dummy = new CombineMusicSelectData();
                dummy.musicSelectData = new List<MusicSelectData>();
                dummy.musicSelectData.Add(null);
                dummy.musicSelectData.Add(null);
                dummy.isWaitConnectScore = true;
                connectList.Add(dummy);
                instance.IsConnectingMusic = false;
            }

            if (instance.MonitorArray == null || playerSubSequence == null) return;
            for (int i = 0; i < instance.MonitorArray.Length && i < playerSubSequence.Length; i++)
            {
                if (playerSubSequence[i] != SubSequence.Music) continue;
                instance.MonitorArray[i].SetDeployList(false);
                if (!instance.IsConnectionFolder(0)) continue;
                instance.ChangeBGM();
                if (!instance.IsEntry(i)) continue;
                instance.MonitorArray[i].SetVisibleButton(instance.IsConnectingMusic,
                    InputManager.ButtonSetting.Button04);
            }
        }
    }
}
