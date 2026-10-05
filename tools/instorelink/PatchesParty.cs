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
        private static float _nextReconcile;
        private static readonly Dictionary<string, float> _deliveredAt = new Dictionary<string, float>();

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

            List<RecruitInfo> have = manager.GetRecruitListWithoutMe();
            if (have == null) have = new List<RecruitInfo>();

            // 1) 大厅里有、游戏里没有 → 补进去
            foreach (KeyValuePair<string, RecruitInfo> kv in want)
            {
                RecruitInfo room = kv.Value;
                if (room == null) continue;
                // 自己的房间不用喂回自己
                if (IsMyOwnRoom(room)) continue;
                if (ContainsRoom(have, room)) continue;

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
                LinkLog.Error("对方选的歌（ID " + info.MusicID + "）你没装，这条招募被忽略。");
                if (info.MechaInfo != null && info.MechaInfo.UserNames != null)
                    LinkLog.Error("要和 " + string.Join(" / ", info.MechaInfo.UserNames) + " 联机，" +
                                  "请确认游戏版本和 option 包一致。");
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------ 选曲界面

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
            if (list == null) list = manager.GetRecruitListWithoutMe();
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
                // 取"当前光标对应的那个房间"：用 ConnectList 保证下标和显示顺序一致，
                // 它没准备好（空/没有）时退回游戏自己的列表。
                List<RecruitInfo> shown = LinkRuntime.ConnectList;
                if (shown == null || shown.Count == 0) shown = recruits;
                int index = __instance.CurrentMusicSelect;
                if (index < 0 || index >= shown.Count) index = 0;
                RecruitInfo recruit = shown[index];
                LinkLog.Info("选曲界面拿到房间数据：" + JsonUtility.ToJson(recruit));
                if (LinkRuntime.SetRecruitData != null)
                    LinkRuntime.SetRecruitData.Invoke(__instance, new object[] { recruit });
                ApplyConnectData(__instance, ____connectCombineMusicDataList, ____currentPlayerSubSequence);
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
