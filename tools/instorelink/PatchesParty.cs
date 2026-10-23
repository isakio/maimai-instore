// InStoreLink —— 招募 / 选曲流程
//
// 与旧版最大的区别：房间列表由 LinkRuntime 后台发布成不可变 RoomSnapshot，
// 主线程只在 Version 变化时对账；每帧路径只读几个字段，不构造 List/字符串。

using System;
using System.Collections.Generic;
using System.Reflection;
using DB;
using HarmonyLib;
using MAI2.Util;
using Mai2.Mai2Cue;
using Manager;
using Manager.MaiStudio;
using Manager.Party.Party;
using Monitor;
using PartyLink;
using Process;
using UnityEngine;

namespace InStoreLink
{
    using CombineMusicSelectData = Process.MusicSelectProcess.CombineMusicSelectData;
    using MusicSelectData = Process.MusicSelectProcess.MusicSelectData;
    using SubSequence = Process.MusicSelectProcess.SubSequence;

    public static class PatchesParty
    {
        private static Client _gameClient;
        private static MethodInfo _recvStartRecruit;
        private static MethodInfo _recvFinishRecruit;
        private static bool _reflectionWarned;

        private static int _reconciledVersion = -1;
        private static int _connectListVersion = -1;
        private static float _nextReconcile;
        private static bool _pendingDeployRefresh;
        private static readonly Dictionary<string, float> _deliveredAt =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _rejected =
            new Dictionary<string, float>(StringComparer.Ordinal);

        private static float _nextRefresh;
        private static float _refreshSince = -1f;
        private static string _refreshKey;
        private static bool _refreshGaveUp;
        private static string _lastSideRoomId;
        private static bool _sideMessageActive;

        private static MethodInfo _socketError;
        private static bool _socketErrorWarned;
        private static FieldInfo _socketField;
        private static bool _socketFieldWarned;

        // ------------------------------------------------------------ 招募列表

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Client), MethodType.Constructor,
            typeof(string), typeof(PartyLink.Party.InitParam))]
        public static void PostClientCtor(Client __instance, string name,
            PartyLink.Party.InitParam initParam)
        {
            _gameClient = __instance;
            LinkLog.Info("本体 party 客户端已建立，开始轮询招募列表");
            LinkRuntime.StartRecruitPolling(__instance);
        }

        public static void OnMainThreadPump()
        {
            if (_gameClient == null) return;
            RoomSnapshot snapshot = LinkRuntime.Snapshot;
            // 数据变化时立即对账；即使版本没变，也每 2 秒兜底一次，防止游戏自己
            // 把某条招募从列表里丢掉后永远不再补回来。
            if (snapshot.Version != _reconciledVersion || Time.time >= _nextReconcile)
            {
                _nextReconcile = Time.time + 2f;
                _reconciledVersion = snapshot.Version;
                Reconcile(snapshot);
            }
            RefreshMyRoomIfDue();
        }

        // 兼容旧调用点；实际工作由 OnMainThreadPump 驱动。
        public static void ReconcileRecruits()
        {
            OnMainThreadPump();
        }

        private static void Reconcile(RoomSnapshot snapshot)
        {
            if (_gameClient == null) return;
            IManager manager = LinkRuntime.PartyMan;
            if (manager == null) return;

            if (_recvStartRecruit == null)
                _recvStartRecruit = typeof(Client).GetMethod("RecvStartRecruit",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            if (_recvFinishRecruit == null)
                _recvFinishRecruit = typeof(Client).GetMethod("RecvFinishRecruit",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            if (_recvStartRecruit == null || _recvFinishRecruit == null)
            {
                if (!_reflectionWarned)
                {
                    _reflectionWarned = true;
                    LinkLog.Error("拿不到 Client.RecvStartRecruit/RecvFinishRecruit");
                }
                return;
            }

            PruneBookkeeping(snapshot);

            List<RecruitInfo> have = manager.GetRecruitListWithoutMe();
            if (have == null) have = new List<RecruitInfo>();
            bool gameListChanged = false;

            for (int i = 0; i < snapshot.Rooms.Length; i++)
            {
                RecruitInfo room = snapshot.Rooms[i];
                if (room == null) continue;
                string id = LinkRuntime.Identity(room);
                if (string.IsNullOrEmpty(id)) continue;
                if (_rejected.ContainsKey(id)) continue;
                if (ContainsRoom(have, room)) continue;

                float delivered;
                if (_deliveredAt.TryGetValue(id, out delivered) &&
                    Time.time - delivered < 2f) continue;

                if (Singleton<DataManager>.Instance.GetMusic(room.MusicID) == null)
                {
                    _rejected[id] = Time.time;
                    LinkLog.Warn("房间曲目 " + room.MusicID + " 不在本机，忽略这条招募");
                    continue;
                }

                if (FeedStart(room))
                {
                    _deliveredAt[id] = Time.time;
                    gameListChanged = true;
                    LinkLog.Debug("补一个房间进游戏：" + id);
                }
            }

            for (int i = have.Count - 1; i >= 0; i--)
            {
                RecruitInfo room = have[i];
                if (room == null) continue;
                string id = LinkRuntime.Identity(room);
                if (string.IsNullOrEmpty(id) || snapshot.ById.ContainsKey(id)) continue;
                if (FeedFinish(room))
                {
                    _deliveredAt.Remove(id);
                    gameListChanged = true;
                    LinkLog.Debug("房间已关，从游戏里去掉：" + id);
                }
            }

            if (gameListChanged) _connectListVersion = -1;
        }

        private static bool FeedStart(RecruitInfo room)
        {
            try
            {
                Packet packet = new Packet(room.IpAddress);
                packet.encode(new StartRecruit(room));
                _recvStartRecruit.Invoke(_gameClient, new object[] { packet });
                return true;
            }
            catch (Exception ex)
            {
                LinkLog.Error("补房间失败：" + ex.Message);
                return false;
            }
        }

        private static bool FeedFinish(RecruitInfo room)
        {
            try
            {
                Packet packet = new Packet(room.IpAddress);
                packet.encode(new FinishRecruit(room));
                _recvFinishRecruit.Invoke(_gameClient, new object[] { packet });
                return true;
            }
            catch (Exception ex)
            {
                LinkLog.Error("去掉房间失败：" + ex.Message);
                return false;
            }
        }

        private static void PruneBookkeeping(RoomSnapshot snapshot)
        {
            if (_deliveredAt.Count > 0)
            {
                List<string> gone = null;
                foreach (string key in _deliveredAt.Keys)
                {
                    if (snapshot.ById.ContainsKey(key)) continue;
                    if (gone == null) gone = new List<string>();
                    gone.Add(key);
                }
                if (gone != null)
                    for (int i = 0; i < gone.Count; i++) _deliveredAt.Remove(gone[i]);
            }

            if (_rejected.Count > 0)
            {
                List<string> gone = null;
                foreach (string key in _rejected.Keys)
                {
                    if (snapshot.ById.ContainsKey(key)) continue;
                    if (gone == null) gone = new List<string>();
                    gone.Add(key);
                }
                if (gone != null)
                    for (int i = 0; i < gone.Count; i++) _rejected.Remove(gone[i]);
            }
        }

        private static bool ContainsRoom(List<RecruitInfo> list, RecruitInfo room)
        {
            string id = LinkRuntime.Identity(room);
            if (string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < list.Count; i++)
            {
                RecruitInfo item = list[i];
                if (item != null && LinkRuntime.Identity(item) == id) return true;
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Client), "RecvStartRecruit", typeof(Packet))]
        public static bool PreRecvStartRecruit(Packet packet)
        {
            StartRecruit start = packet.getParam<StartRecruit>();
            RecruitInfo info = start == null ? null : start.RecruitInfo;
            if (info == null) return false;

            if (Singleton<DataManager>.Instance.GetMusic(info.MusicID) == null)
            {
                string id = LinkRuntime.Identity(info);
                if (!string.IsNullOrEmpty(id)) _rejected[id] = Time.time;
                LinkLog.Warn("对方选的歌（ID " + info.MusicID + "）你没装，这条招募被忽略");
                return false;
            }
            if (LinkLog.Verbose)
                LinkLog.Info("收到招募：" + JsonUtility.ToJson(info));
            return true;
        }

        // ------------------------------------------------------------ 房间续报

        private static void RefreshMyRoomIfDue()
        {
            if (Time.time < _nextRefresh) return;
            _nextRefresh = Time.time + 10f;

            LinkClient client = LinkRuntime.Client;
            if (client == null || LinkRuntime.Config == null) return;

            RecruitInfo mine = LinkRuntime.MyRoom;
            if (mine == null)
            {
                // 兜底：某些异常路径下 MyRoom 可能没经过 PreSendClass。
                IManager manager = LinkRuntime.PartyMan;
                if (manager != null)
                {
                    List<RecruitInfo> all = manager.GetRecruitList();
                    if (all != null)
                        for (int i = 0; i < all.Count; i++)
                            if (all[i] != null && LinkRuntime.IsMyRoom(all[i]))
                            {
                                mine = all[i];
                                break;
                            }
                }
            }

            if (mine == null)
            {
                _refreshKey = null;
                _refreshSince = -1f;
                _refreshGaveUp = false;
                return;
            }

            string key = LinkRuntime.Identity(mine);
            if (_refreshKey != key)
            {
                _refreshKey = key;
                _refreshSince = Time.time;
                _refreshGaveUp = false;
            }
            if (Time.time - _refreshSince > 600f)
            {
                if (!_refreshGaveUp)
                {
                    _refreshGaveUp = true;
                    LinkLog.Warn("房间续报超过 600 秒，停止续报，交给大厅 TTL");
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
        }

        // ------------------------------------------------------------ 选曲流程

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MusicSelectProcess), "OnStart")]
        public static bool PreMusicSelectOnStart(MusicSelectProcess __instance)
        {
            _reconciledVersion = -1;
            _connectListVersion = -1;
            _pendingDeployRefresh = false;
            LinkRuntime.LastRecruitId = null;
            LinkRuntime.WasInConnectionFolder = false;
            ReleaseStickyRoom(__instance);
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MusicSelectProcess), "PartyExec")]
        public static void PostPartyExec(MusicSelectProcess __instance,
            SubSequence[] ____currentPlayerSubSequence)
        {
            LinkRuntime.MainThreadPump();

            IManager manager = LinkRuntime.PartyMan;
            if (manager == null) return;

            bool inFolder = __instance.IsConnectionFolder();
            bool wasInFolder = LinkRuntime.WasInConnectionFolder;

            if (inFolder && !wasInFolder)
                _connectListVersion = -1;

            // BACK 退出上一间后还能选别的房间。
            try
            {
                bool connected = manager.IsConnect();
                if (connected && !LinkRuntime.WasConnected)
                {
                    LinkRuntime.JoinedRoomId = __instance.RecruitData == null
                        ? null : LinkRuntime.Identity(__instance.RecruitData);
                }
                LinkRuntime.WasConnected = connected;

                if (connected && !string.IsNullOrEmpty(LinkRuntime.JoinedRoomId) &&
                    !manager.IsHost() && inFolder &&
                    LinkRuntime.ConnectList != null)
                {
                    int cur = __instance.CurrentMusicSelect;
                    if (cur >= 0 && cur < LinkRuntime.ConnectList.Count &&
                        LinkRuntime.ConnectList[cur] != null)
                    {
                        string target = LinkRuntime.Identity(LinkRuntime.ConnectList[cur]);
                        if (target != LinkRuntime.JoinedRoomId)
                        {
                            LinkLog.Info("人已退出上一间，取消联机后重新允许选取");
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
                LinkLog.Debug("检查退出房间时出错：" + ex.Message);
            }

            RoomSnapshot snapshot = LinkRuntime.Snapshot;
            if (inFolder && _connectListVersion != snapshot.Version)
            {
                _connectListVersion = snapshot.Version;
                LinkRuntime.LastRecruitId = null;
                if (LinkRuntime.SetConnectDataGame != null)
                {
                    try { LinkRuntime.SetConnectDataGame.Invoke(__instance, null); }
                    catch (Exception ex) { LinkLog.Error("重画联机歌曲列表失败：" + ex.Message); }
                }
                // SetConnectData 只在当前处于 Music 子界面时才会 SetDeployList。
                // 如果玩家正在 Genre 大面板，列表数据已经换了，但卡片还没重铺；
                // 记一笔，等回到 Music 子界面再补一次。
                _pendingDeployRefresh = !AnyMusic(____currentPlayerSubSequence);
            }

            if (_pendingDeployRefresh && inFolder &&
                AnyMusic(____currentPlayerSubSequence) &&
                LinkRuntime.SetConnectDataGame != null)
            {
                _pendingDeployRefresh = false;
                try { LinkRuntime.SetConnectDataGame.Invoke(__instance, null); }
                catch (Exception ex) { LinkLog.Error("补铺联机歌曲列表失败：" + ex.Message); }
            }

            try { UpdateSideMessage(__instance, inFolder); }
            catch (Exception ex) { LinkLog.Debug("更新联机侧栏失败：" + ex.Message); }

            if (wasInFolder && !inFolder)
            {
                _pendingDeployRefresh = false;
                ReleaseStickyRoom(__instance);
            }
            LinkRuntime.WasInConnectionFolder = inFolder;

            if (LinkLog.Verbose)
            {
                int cursor = __instance.CurrentMusicSelect;
                if (cursor != LinkRuntime.LastCursor)
                {
                    LinkRuntime.LastCursor = cursor;
                    LinkLog.Info("选曲光标 -> 第 " + (cursor + 1) + " 项");
                }
                string sig = (inFolder ? "F" : "-")
                    + (__instance.IsConnectingMusic ? "C" : "-")
                    + (__instance.RecruitData != null ? "R" : "-")
                    + " cur=" + cursor
                    + " rooms=" + snapshot.Rooms.Length;
                if (sig != LinkRuntime.LastStateSig)
                {
                    LinkRuntime.LastStateSig = sig;
                    LinkLog.Info("选曲状态 " + sig);
                }
            }
        }

        private static bool AnyMusic(SubSequence[] subSequences)
        {
            if (subSequences == null) return false;
            for (int i = 0; i < subSequences.Length; i++)
                if (subSequences[i] == SubSequence.Music) return true;
            return false;
        }

        private static void UpdateSideMessage(MusicSelectProcess instance, bool inFolder)
        {
            if (!inFolder || !instance.IsConnectingMusic || instance.RecruitData == null)
            {
                if (_sideMessageActive)
                {
                    SetAllSideMessage(instance, CommonMessageID.Scroll_Music_Select.GetName());
                    _sideMessageActive = false;
                    _lastSideRoomId = null;
                }
                return;
            }

            string id = LinkRuntime.LastRecruitId;
            if (string.IsNullOrEmpty(id))
                id = LinkRuntime.Identity(instance.RecruitData);
            if (id == _lastSideRoomId) return;
            _lastSideRoomId = id;

            MechaInfo mecha = instance.RecruitData.MechaInfo;
            List<string> names = new List<string>();
            if (mecha != null && mecha.UserNames != null && mecha.FumenDifs != null)
            {
                for (int i = 0; i < mecha.UserNames.Length && i < mecha.FumenDifs.Length; i++)
                    if (mecha.FumenDifs[i] != -1) names.Add(mecha.UserNames[i]);
            }
            SetAllSideMessage(instance, "联机房间：" + string.Join(" / ", names.ToArray()));
            _sideMessageActive = true;
        }

        private static void SetAllSideMessage(MusicSelectProcess instance, string text)
        {
            if (instance.MonitorArray == null) return;
            for (int i = 0; i < instance.MonitorArray.Length; i++)
                if (instance.IsEntry(i)) instance.MonitorArray[i].SetSideMessage(text);
        }

        private static void ReleaseStickyRoom(MusicSelectProcess instance)
        {
            if (instance == null) return;
            try
            {
                if (LinkRuntime.SetRecruitData != null)
                    LinkRuntime.SetRecruitData.Invoke(instance, new object[] { null });
                instance.IsConnectingMusic = false;
                LinkRuntime.LastRecruitId = null;
                _lastSideRoomId = null;
            }
            catch (Exception ex)
            {
                LinkLog.Debug("松开选中房间失败：" + ex.Message);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MusicSelectProcess), "RecruitData", MethodType.Getter)]
        public static void PostRecruitData(MusicSelectProcess __instance, ref RecruitInfo __result)
        {
            if (!__instance.IsConnectionFolder() || __result == null) return;
            int index = __instance.CurrentMusicSelect;
            List<RecruitInfo> shown = LinkRuntime.ConnectList;
            if (shown != null && index >= 0 && index < shown.Count)
            {
                __result = shown[index];
                return;
            }

            RoomSnapshot snapshot = LinkRuntime.Snapshot;
            if (index >= 0 && index < snapshot.Rooms.Length)
                __result = snapshot.Rooms[index];
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MusicSelectProcess), "IsConnectStart")]
        public static bool PreIsConnectStart(MusicSelectProcess __instance,
            List<CombineMusicSelectData> ____connectCombineMusicDataList,
            SubSequence[] ____currentPlayerSubSequence, ref bool __result)
        {
            __result = false;
            if (LinkRuntime.PartyMan == null) return false;

            RoomSnapshot snapshot = LinkRuntime.Snapshot;
            List<RecruitInfo> shown = LinkRuntime.ConnectList;
            if (shown == null || _connectListVersion != snapshot.Version)
            {
                IManager manager = LinkRuntime.PartyMan;
                List<RecruitInfo> fresh = manager == null
                    ? null : manager.GetRecruitListWithoutMe();
                shown = (fresh != null && fresh.Count > 0)
                    ? fresh : new List<RecruitInfo>(snapshot.Rooms);
            }

            if (shown.Count == 0) return false;
            int index = __instance.CurrentMusicSelect;
            if (index < 0 || index >= shown.Count) index = 0;
            RecruitInfo recruit = shown[index];
            if (recruit == null) return false;

            string id = LinkRuntime.Identity(recruit);
            if (id == LinkRuntime.LastRecruitId) return false;

            if (LinkRuntime.SetRecruitData != null)
                LinkRuntime.SetRecruitData.Invoke(__instance, new object[] { recruit });
            LinkRuntime.LastRecruitId = id;
            __result = true;
            LinkLog.Info("选中房间：" + id);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MusicSelectProcess), "SetConnectData")]
        public static bool PreSetConnectData(MusicSelectProcess __instance,
            List<CombineMusicSelectData> ____connectCombineMusicDataList,
            SubSequence[] ____currentPlayerSubSequence)
        {
            ApplyConnectData(__instance, ____connectCombineMusicDataList,
                ____currentPlayerSubSequence);
            return false;
        }

        private static void ApplyConnectData(MusicSelectProcess instance,
            List<CombineMusicSelectData> connectList, SubSequence[] playerSubSequence)
        {
            if (connectList == null) return;
            connectList.Clear();

            RoomSnapshot snapshot = LinkRuntime.Snapshot;
            List<RecruitInfo> shown = new List<RecruitInfo>();
            SortedDictionary<int, NotesWrapper> notes =
                Singleton<NotesListManager>.Instance.GetNotesList();

            // 显示顺序必须以游戏自己的 RecruitList 为准：光标下标是游戏按它的列表
            // 排出来的，不能拿大厅 HTTP 的返回顺序去赌。快照只在大厅/游戏列表
            // 暂时拿不到时兜底。
            IManager manager = LinkRuntime.PartyMan;
            List<RecruitInfo> rooms = manager == null
                ? null : manager.GetRecruitListWithoutMe();
            if (rooms == null || rooms.Count == 0)
                rooms = new List<RecruitInfo>(snapshot.Rooms);

            for (int i = 0; i < rooms.Count; i++)
            {
                RecruitInfo room = rooms[i];
                if (room == null) continue;
                try
                {
                    int musicId = room.MusicID;
                    MusicData music = Singleton<DataManager>.Instance.GetMusic(musicId);
                    NotesWrapper wrapper;
                    if (music == null || !notes.TryGetValue(musicId, out wrapper) ||
                        wrapper == null || wrapper.NotesList == null)
                    {
                        LinkLog.Warn("曲目 " + musicId + " 不在本机，房间跳过");
                        continue;
                    }

                    CombineMusicSelectData combine = new CombineMusicSelectData();
                    if (musicId < 10000) combine.existStandardScore = true;
                    else if (musicId > 10000 && musicId < 20000) combine.existDeluxeScore = true;
                    for (int p = 0; p < 2; p++)
                        combine.musicSelectData.Add(new MusicSelectData(music, wrapper.NotesList, 0));
                    connectList.Add(combine);
                    shown.Add(room);
                    instance.IsConnectingMusic = true;

                    if (instance.MonitorArray != null)
                    {
                        for (int m = 0; m < instance.MonitorArray.Length; m++)
                        {
                            if (!instance.IsEntry(m)) continue;
                            instance.MonitorArray[m].SetRecruitInfo(music.thumbnailName);
                            SoundManager.PlaySE(Cue.SE_INFO_NORMAL, m);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LinkLog.Warn("房间翻译失败，跳过：" + ex.Message);
                }
            }

            LinkRuntime.ConnectList = shown;
            _connectListVersion = snapshot.Version;

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

        // ------------------------------------------------------------ 加入失败

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PartyLink.ConnectSocket), "Execute_Connect")]
        public static bool PreConnectSocketExecute(PartyLink.ConnectSocket __instance)
        {
            string why = TakeFailureFor(__instance);
            if (why == null) return true;

            if (_socketError == null)
                _socketError = typeof(PartyLink.SocketBase).GetMethod("error",
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(int) }, null);
            if (_socketError == null)
            {
                if (!_socketErrorWarned)
                {
                    _socketErrorWarned = true;
                    LinkLog.Error("拿不到 SocketBase.error，加入失败无法交回本体");
                }
                return true;
            }

            try
            {
                _socketError.Invoke(__instance, new object[] { "InStoreLink：" + why, 0 });
                LinkLog.Info("已把加入失败交回本体：" + why);
            }
            catch (Exception ex)
            {
                LinkLog.Error("通知本体连接失败时出错：" + ex.Message);
                return true;
            }
            return false;
        }

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
                    LinkLog.Error("拿不到 ConnectSocket._socket");
                }
            }
            if (_socketField == null) return null;
            NFSocket nf = _socketField.GetValue(socket) as NFSocket;
            LinkSocket shadow;
            if (nf == null ||
                !LinkRuntime.Redirect.TryGetValue(nf, out shadow) || shadow == null)
                return null;
            return LinkClient.TakeJoinFailure(shadow.StreamKey);
        }
    }
}
