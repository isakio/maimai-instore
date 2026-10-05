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
        /// 主线程分发：把轮询线程排队的新房间/关房喂给游戏本体。
        /// 由 CommonMonitor.ViewUpdate 的 Postfix 每帧调用（见 PatchesNet）。
        /// </summary>
        public static void FlushPendingRecruits()
        {
            Client client = _gameClient;
            if (client == null) return;
            if (LinkRuntime.PendingStarts.IsEmpty && LinkRuntime.PendingFinishes.IsEmpty) return;

            if (_recvStartRecruit == null)
                _recvStartRecruit = typeof(Client).GetMethod("RecvStartRecruit",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            if (_recvFinishRecruit == null)
                _recvFinishRecruit = typeof(Client).GetMethod("RecvFinishRecruit",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            // 反射都拿不到的话，房间根本没法喂回游戏。这里直接返回、把队列留着，
            // 免得像"先出队再 break"那样把事件一条条丢掉。
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

            RecruitInfo info;
            while (LinkRuntime.PendingFinishes.TryDequeue(out info))
            {
                Packet packet = new Packet(info.IpAddress);
                packet.encode(new FinishRecruit(info));
                _recvFinishRecruit.Invoke(client, new object[] { packet });
            }
            while (LinkRuntime.PendingStarts.TryDequeue(out info))
            {
                Packet packet = new Packet(info.IpAddress);
                packet.encode(new StartRecruit(info));
                _recvStartRecruit.Invoke(client, new object[] { packet });
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
            List<RecruitInfo> list = manager.GetRecruitListWithoutMe();
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
            // 本体原本靠"对方的 IP 是不是本机"来判断，这里直接按"有没有房间"来判断
            if (!__instance.IsConnectingMusic && recruits != null && recruits.Count > 0)
            {
                RecruitInfo recruit = recruits[0];
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
            if (recruits != null)
            {
                foreach (RecruitInfo item in recruits)
                {
                    int musicId = item.MusicID;
                    var music = Singleton<DataManager>.Instance.GetMusic(musicId);
                    if (music == null) continue;

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
            }

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
