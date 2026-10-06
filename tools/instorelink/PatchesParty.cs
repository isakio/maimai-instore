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
            if (mine == null)
            {
                // 我没在招募。**把续报计时也一起清掉**：否则停了一会儿之后再开一间
                // 同身份（同 keychip + 同曲目 = 同一个 Identity）的房间时，_refreshKey
                // 还是相等的，计时不会重置 —— 要是上一间已经续过 10 分钟，这一开出来
                // 就立刻"续报超时收手"，房间 30 秒后在大厅里悄悄消失。
                _refreshKey = null;
                _refreshSince = -1f;
                _refreshGaveUp = false;
                return;
            }

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

        /// <summary>
        /// 房间集合的签名：每间的 Identity 排序后拼起来。只用来判断"该显示的房间集合变了没有"。
        /// </summary>
        private static string RoomSignatureOf(List<RecruitInfo> rooms)
        {
            if (rooms == null || rooms.Count == 0) return "";
            List<string> ids = new List<string>();
            foreach (RecruitInfo r in rooms)
            {
                if (r == null) continue;
                ids.Add(LinkRuntime.Identity(r));
            }
            ids.Sort(StringComparer.Ordinal);
            return string.Join("|", ids.ToArray());
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
        private static FieldInfo _socketField;
        private static bool _socketFieldWarned;

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
            string why = TakeFailureFor(__instance);
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

        /// <summary>
        /// "这次加入失败"只对应**某一条**连接。这里把 ConnectSocket 反查成我们的影子 socket，
        /// 再按那条流的 StreamKey 取失败原因 —— 不这么做的话，旧那一次的超时会把玩家
        /// 刚按的新一次连接掐掉（日志里实测到过：新连接发出 12ms 后被旧的超时收走）。
        ///
        /// 路径：ConnectSocket._socket（NFSocket）→ LinkRuntime.Redirect → 影子 socket。
        /// </summary>
        private static string TakeFailureFor(PartyLink.ConnectSocket socket)
        {
            if (socket == null || LinkRuntime.Client == null) return null;

            if (_socketField == null)
            {
                const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
                _socketField = typeof(PartyLink.SocketBase).GetField("_socket", Flags)
                            ?? typeof(PartyLink.ConnectSocket).GetField("_socket", Flags);
                if (_socketField == null && !_socketFieldWarned)
                {
                    _socketFieldWarned = true;
                    LinkLog.Error("拿不到 ConnectSocket._socket —— 加入失败时无法对应到具体哪条连接");
                }
            }
            if (_socketField == null) return null;

            NFSocket nf = _socketField.GetValue(socket) as NFSocket;
            if (nf == null) return null;
            LinkSocket shadow;
            if (!LinkRuntime.Redirect.TryGetValue(nf, out shadow) || shadow == null) return null;
            return LinkClient.TakeJoinFailure(shadow.StreamKey);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MusicSelectProcess), "OnStart")]
        public static bool PreMusicSelectOnStart(MusicSelectProcess __instance)
        {
            // 每次进选曲界面重置状态（上游同款：房间列表变了要重新刷新）
            LinkRuntime.RoomSignature = null;       // 进选曲界面重置：第一次站到那一栏时会重画一次
            LinkRuntime.SideMessageFlag = false;
            // 还要把"上次选中的那一间"松开。否则进了某间房再退出来，之后怎么按都还是那一间
            // （玩家实测：进第 2 间 → 退出 → 再按哪儿都连第 2 间）。
            ReleaseStickyRoom(__instance);
            return true;
        }

        /// <summary>
        /// 把"当前选中的那一间"松开：RecruitData 置空、IsConnectingMusic 复位，
        /// 让下一轮选择从零开始。
        ///
        /// **不要把 ConnectList 清空** —— 它记的是"上一次真正画出来的房间顺序"，
        /// 退出房间再进来时列表往往还没重建，一清掉 RecruitData getter 就没房间可取，
        /// 玩家表现就是"按了没反应"（踩过：清了它的那版 efade648，进第 2 间→退出→
        /// 再按第 1 间毫无反应）。要重新选，靠的是松开 RecruitData，不是丢列表。
        /// </summary>
        private static void ReleaseStickyRoom(MusicSelectProcess instance)
        {
            if (instance == null) return;
            try
            {
                // 日志用的这次读取单独兜一层：它走 RecruitData 的 getter（会经过我们的
                // PostRecruitData），在 OnStart 这种"音乐数据还没就绪"的时刻会抛 NRE ——
                // 之前它把下面的"松开"整个带崩了（日志里 6 次"松开…出错"，每次 release 全废）。
                try
                {
                    RecruitInfo before = instance.RecruitData;
                    if (before != null)
                        LinkLog.Info("松开上次选中的房间：" + LinkRuntime.Identity(before)
                                     + "（IsConnectingMusic=" + instance.IsConnectingMusic + "）");
                }
                catch (Exception ex) { LinkLog.Debug("读当前选中的房间时出错：" + ex.Message); }

                if (LinkRuntime.SetRecruitData != null)
                    LinkRuntime.SetRecruitData.Invoke(instance, new object[] { null });
                instance.IsConnectingMusic = false;
                LinkRuntime.LastRecruitId = null;
            }
            catch (Exception ex)
            {
                LinkLog.Debug("松开上次选中的房间时出错：" + ex.Message);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MusicSelectProcess), "PartyExec")]
        public static void PostPartyExec(MusicSelectProcess __instance)
        {
            IManager manager = LinkRuntime.PartyMan;
            if (manager == null) return;

            // 诊断：把"按了没反应"要看的那几个状态一次性打出来（组合变一次才打一行，
            // 免得像 1416 行/20 秒那样刷屏）。F=站在联机栏 C=IsConnectingMusic
            // R=有 RecruitData；H=房主 L=客户端 Q=请求中 N=已连接。
            // ★ 整段都用 Verbose 包住：这几行本来只在 Debug=true 时才会打，但签名串是
            //   每帧拼的 —— 玩家默认关着 Debug，等于每帧白造一堆字符串 + GC。
            if (LinkLog.Verbose)
            {
                try
                {
                    string flags = (manager.IsHost() ? "H" : "-") + (manager.IsClient() ? "L" : "-")
                                 + (manager.IsRequest() ? "Q" : "-") + (manager.IsConnect() ? "N" : "-");
                    string sig = (__instance.IsConnectionFolder() ? "F" : "-")
                               + (__instance.IsConnectingMusic ? "C" : "-")
                               + (__instance.RecruitData != null ? "R" : "-")
                               + " " + flags + " cur=" + __instance.CurrentMusicSelect
                               + " rooms=" + (LinkRuntime.ConnectList == null ? -1 : LinkRuntime.ConnectList.Count)
                               + " st=" + manager.GetCurrentStateID()
                               + " joined=" + (LinkRuntime.JoinedRoomId ?? "-");
                    if (sig != LinkRuntime.LastStateSig)
                    {
                        LinkRuntime.LastStateSig = sig;
                        LinkLog.Info("选曲状态 " + sig);
                    }
                }
                catch (Exception) { /* 诊断而已，别让它影响正事 */ }
            }

            // ── 按 BACK 从房间里退回房间列表之后，还能选别的房间 ──────────────────
            // 实测：进了第 2 间 → 按 BACK 回到房间列表（人已经出来了），再按第 1 间没反应。
            // 原因是本体那边联机还挂着（IsConnect 还是 true），而 PartyExec 在
            // IsHost/IsClient/IsRequest/IsConnect 时会**直接 return** —— 连"发起加入"
            // 那一段都走不到，按谁都不发起。
            // 这里补一步：已经连着一间、但玩家把光标挪到了**另一间** → 说明人已经从
            // 上一间退出来了，用本体自己的 CancelBothRecruitJoin() 把上一次联机取消掉
            // （本体 Client.CancelJoin 会把状态复位，之后 IsConnect 就是 false）。
            try
            {
                bool connected = manager.IsConnect();
                if (connected && !LinkRuntime.WasConnected)
                {
                    // 刚连上的那一帧，光标还停在这一间上 —— 这时记下来的就是"进的是哪一间"。
                    LinkRuntime.JoinedRoomId = __instance.RecruitData == null
                        ? null : LinkRuntime.Identity(__instance.RecruitData);
                }
                LinkRuntime.WasConnected = connected;

                if (connected && !string.IsNullOrEmpty(LinkRuntime.JoinedRoomId) &&
                    !manager.IsHost() && __instance.IsConnectionFolder() &&
                    LinkRuntime.ConnectList != null)
                {
                    int cur = __instance.CurrentMusicSelect;
                    if (cur >= 0 && cur < LinkRuntime.ConnectList.Count &&
                        LinkRuntime.ConnectList[cur] != null)
                    {
                        string target = LinkRuntime.Identity(LinkRuntime.ConnectList[cur]);
                        if (target != LinkRuntime.JoinedRoomId)
                        {
                            LinkLog.Info("人已退出上一间（" + LinkRuntime.JoinedRoomId
                                         + "），光标指到 " + target + "：取消上一次联机，重新允许选取");
                            manager.CancelBothRecruitJoin();
                            LinkRuntime.JoinedRoomId = null;
                            LinkRuntime.WasConnected = false;
                            ReleaseStickyRoom(__instance);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LinkLog.Debug("取消上一次联机时出错：" + ex.Message);
            }

            // 诊断：这一栏（店内联机）里光标能不能挪到第 2 间？
            // 玩家反馈"怎么按都是第一间"，先分清是"光标挪不动"还是"我们读错了位置"。
            int cursor = __instance.CurrentMusicSelect;
            if (LinkRuntime.LastCursor != cursor)
            {
                LinkRuntime.LastCursor = cursor;
                List<RecruitInfo> rows = LinkRuntime.ConnectList;
                LinkLog.Info("选曲光标 -> 第 " + (cursor + 1) + " 项（这一栏共 "
                             + (rows == null ? 0 : rows.Count) + " 间）");
            }

            // 房间列表有变化（曲目 ID 之和变了）→ **真的重画**歌曲列表。
            //
            // 为什么必须自己调 SetConnectData：歌曲列表（_connectCombineMusicDataList）只有游戏
            // 自己调 SetConnectData 时才会重建，而游戏只在"进选曲界面初始化"和"房间列表变空收尾"
            // 这两个时刻调它。于是"人已经站在这一栏里、房间才出现"这种最常见的情况（朋友后来才开房）
            // 永远不刷新 —— 玩家看到的是**那一栏一直是空的**（真机实测：15:32 进界面时没房间，
            // 15:41 房间才来，界面再也没变过，`connectList=1` 那个空占位格一直挂着）。
            //
            // 只在"人就在联机栏里"时重画（别去打扰普通分类的列表），而且只在**签名变化**时重画一次
            // （不会每帧重建、不会像以前那样把光标按一次弹回第 0 项）。不在栏里时故意不更新签名，
            // 这样等玩家走进这一栏时会自动重画一次，保证看到的是最新的房间。
            string roomSig = RoomSignatureOf(manager.GetRecruitListWithoutMe());
            if (__instance.IsConnectionFolder() && LinkRuntime.RoomSignature != roomSig)
            {
                LinkRuntime.RoomSignature = roomSig;
                LinkRuntime.LastRecruitId = null;      // 重建后让 PreIsConnectStart 重新对准一次
                if (LinkRuntime.SetConnectDataGame != null)
                {
                    try { LinkRuntime.SetConnectDataGame.Invoke(__instance, null); }
                    catch (Exception ex) { LinkLog.Debug("重画联机歌曲列表出错：" + ex.Message); }
                }
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
            // 人已经离开「店内マッチング」这一栏了 → 把上次选中的房间松开，
            // 免得下次进来还攥着上一间（玩家实测的"锁在第 2 间"）。
            // 判据不能用 ConnectList：它现在**不再被清空**（清了会导致退出后按了没反应），
            // 要看"此刻手里到底还攥着没有" —— IsConnectingMusic 真、或者我们自己写过
            // 房间数据（LastRecruitId）才需要松开。
            // ★ 这里别去读 RecruitData 的 getter：它在我们自己的 PostRecruitData 里会走
            //   IsConnectionFolder()/GetRecruitListWithoutMe()，OnStart 前后可能抛 NRE，
            //   一抛就是整个 PartyExec 后置补丁炸掉（每帧一次）。
            // ★ 必须**边沿触发**（在栏里 → 离开栏）：只看"当前不在栏里"的话，
            //   在外面浏览普通分类时（PreIsConnectStart 也会把 RecruitData 对准某个房间，
            //   和本体一致）就会"对准一次 / 松开一次"每帧来回抖。
            bool inConnectionFolder = __instance.IsConnectionFolder();
            if (LinkRuntime.WasInConnectionFolder && !inConnectionFolder &&
                (__instance.IsConnectingMusic || LinkRuntime.LastRecruitId != null))
                ReleaseStickyRoom(__instance);
            LinkRuntime.WasInConnectionFolder = inConnectionFolder;
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
            if (recruits.Count > 0)
            {
                // ★ 这里**不要**重建列表！ApplyConnectData 一重建，选曲光标就被弹回第 0 项
                // （玩家实测：按第 2 间之后光标立刻跳回第 1 间），于是读到的永远是第 1 间 ——
                // "怎么按都是第一间"就是这么来的（前后栽了两次）。
                // 列表由游戏自己调 SetConnectData 时重建（我们那层补丁负责），这里只需要
                // 按"玩家现在看到的顺序"（ConnectList）+ 当前光标取房间。
                //
                // ★ 也**不要**再拿 IsConnectingMusic 当闸门了：它的本体语义就是
                // "RecruitData != null"（见本体 SetConnectData），拿它当闸门的话，
                // 只要之前对准过一间（哪怕人已经从房间里退出来了），这里就永远不再更新
                // RecruitData —— 而本体的 IsConnectStart 又被我们整个接管了，于是
                // "进第 2 间 → 退出 → 选第 1 间"按了没反应。改回本体的做法：谁变对准谁
                // （本体第二分支也是这么干的：IP 变了就重新对准），只是不重建列表。
                List<RecruitInfo> shown = LinkRuntime.ConnectList;
                if (shown == null || shown.Count == 0 || !AllRoomsIn(shown, recruits)) shown = recruits;
                int index = __instance.CurrentMusicSelect;
                if (index < 0 || index >= shown.Count) index = 0;
                RecruitInfo recruit = shown[index];
                if (recruit == null) return false;   // 列表里可能有 null（本体列表不保证），别往 Identity 里传
                string id = LinkRuntime.Identity(recruit);
                // 已经对准这一间就别每帧重设（以前每帧写一遍，20 秒刷了 1400 多行日志）
                if (id == LinkRuntime.LastRecruitId) return false;
                LinkLog.Info("选曲界面拿到房间数据（光标 " + index + "/" + (shown.Count - 1) + "）："
                             + JsonUtility.ToJson(recruit));
                if (LinkRuntime.SetRecruitData != null)
                    LinkRuntime.SetRecruitData.Invoke(__instance, new object[] { recruit });
                LinkRuntime.LastRecruitId = id;
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

            // 一格都没翻译出来时也要放个占位，否则那一栏是空的、光标没地方停。
            // 注意判据用 connectList.Count 而不是 recruits.Count：大厅里**有房间但全部都装不了**
            // （歌没装 / 没谱面）时，recruits 非空、connectList 却是空的 —— 只看 recruits
            // 就会漏掉这种情况，那一格直接变空白。
            if (connectList.Count == 0)
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
