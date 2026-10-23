// InStoreLink —— 运行时状态与后台轮询
//
// 设计目标：
//   1. 游戏主线程不轮询 HTTP，也不每帧构造房间列表；
//   2. 后台线程只发布不可变 RoomSnapshot，主线程按版本号消费；
//   3. 联机 UI/状态更新只在数据变化时发生。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using Manager;
using Manager.Party.Party;
using PartyLink;
using Process;

namespace InStoreLink
{
    public static class LinkRuntime
    {
        public static LinkConfig Config;
        public static LinkClient Client;
        public static volatile bool Stopping;

        public static readonly ConcurrentDictionary<NFSocket, LinkSocket> Redirect =
            new ConcurrentDictionary<NFSocket, LinkSocket>();

        public static MethodInfo PacketWriteUInt;
        public static MethodInfo SetRecruitData;
        public static MethodInfo SetConnectDataGame;

        // 以下字段只在 Unity 主线程读写。
        public static RecruitInfo MyRoom;
        public static List<RecruitInfo> ConnectList;
        public static string LastRecruitId;
        public static string LastStateSig;
        public static bool WasConnected;
        public static string JoinedRoomId;
        public static bool WasInConnectionFolder;
        public static int LastCursor = -1;

        private static RoomSnapshot _snapshot = RoomSnapshot.Empty;
        private static int _onlineUsers;
        private static int _pollerStarted;
        private static int _checkAuthCalled;
        private static int _lastPumpFrame = -1;
        private static Thread _poller;

        public static bool Verbose
        {
            get { return Config != null && Config.Debug; }
        }

        public static IManager PartyMan
        {
            get { return Manager.Party.Party.Party.Get(); }
        }

        public static RoomSnapshot Snapshot
        {
            get { return Interlocked.CompareExchange(ref _snapshot, null, null); }
        }

        public static int OnlineUsers
        {
            get { return Interlocked.CompareExchange(ref _onlineUsers, 0, 0); }
        }

        public static void BeforePatch()
        {
            LinkLog.Verbose = Verbose;

            PacketWriteUInt = typeof(Packet).GetMethod("write_uint",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(PacketType), typeof(int), typeof(uint) }, null);
            if (PacketWriteUInt == null)
                LinkLog.Error("找不到 Packet.write_uint（游戏版本可能不匹配）");

            PropertyInfo recruitData = typeof(MusicSelectProcess).GetProperty("RecruitData");
            if (recruitData != null) SetRecruitData = recruitData.GetSetMethod(true);
            if (SetRecruitData == null)
                LinkLog.Error("找不到 MusicSelectProcess.RecruitData 的 setter");

            SetConnectDataGame = typeof(MusicSelectProcess).GetMethod("SetConnectData",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (SetConnectDataGame == null)
                LinkLog.Error("找不到 MusicSelectProcess.SetConnectData");

            Client = new LinkClient("A1234567890", "", LinkConfig.DefaultRelayPort);
            LinkClient.Instance = Client;

            string host;
            int port;
            if (Config != null && LinkConfig.TryParseRelay(Config.RelayUrl, out host, out port))
            {
                Client.Host = host;
                Client.Port = port;
                LinkLog.Msg("按配置直连中继 " + host + ":" + port);
            }
        }

        public static void StartClient()
        {
            if (Interlocked.Exchange(ref _checkAuthCalled, 1) == 1) return;
            if (Client == null || Config == null) return;

            string keychip = LinkStub.NewKeychip(new Random());
            Client.Keychip = keychip;
            StartPoller();

            Thread init = new Thread(delegate()
            {
                while (!Stopping && string.IsNullOrEmpty(Client.Host))
                {
                    if (!TryFetchRelayInfo() && !Stopping) Thread.Sleep(5000);
                }
                if (!Stopping && !string.IsNullOrEmpty(Client.Host)) Client.ConnectAsync();
            });
            init.IsBackground = true;
            init.Name = "InStoreLink-Init";
            init.Start();
        }

        public static void StartRecruitPolling(Client client)
        {
            // 本体的 party client 可能被重建；轮询线程只允许一条。
            StartPoller();
        }

        private static void StartPoller()
        {
            if (Client == null || string.IsNullOrEmpty(Client.Keychip)) return;
            if (Interlocked.Exchange(ref _pollerStarted, 1) == 1) return;
            _poller = new Thread(PollLoop);
            _poller.IsBackground = true;
            _poller.Name = "InStoreLink-Poller";
            _poller.Start();
        }

        private static void PollLoop()
        {
            int nextRooms = 0;
            int nextOnline = 0;
            while (!Stopping)
            {
                int now = Environment.TickCount;
                if (now >= nextRooms)
                {
                    nextRooms = now + 10000;
                    PollRooms();
                }
                if (now >= nextOnline)
                {
                    nextOnline = now + 30000;
                    PollOnline();
                }
                if (Stopping) return;
                Thread.Sleep(250);
            }
        }

        private static void PollRooms()
        {
            if (Config == null) return;
            string body = LinkLobby.Get(LinkLobby.Combine(Config.LobbyUrl, "/recruit/list"));
            List<RecruitInfo> rooms = new List<RecruitInfo>();
            if (!string.IsNullOrEmpty(body))
            {
                string[] lines = body.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    RecruitRecordIn record = JsonUtility_FromJson<RecruitRecordIn>(line);
                    if (record != null && record.RecruitInfo != null)
                        rooms.Add(record.RecruitInfo);
                }
            }
            PublishRooms(rooms);
        }

        private static void PollOnline()
        {
            if (Config == null) return;
            string body = LinkLobby.Get(LinkLobby.Combine(Config.LobbyUrl, "/online"));
            if (string.IsNullOrEmpty(body)) return;
            OnlineUserInfo info = JsonUtility_FromJson<OnlineUserInfo>(body);
            if (info != null) Interlocked.Exchange(ref _onlineUsers, info.totalUsers);
        }

        private static void PublishRooms(List<RecruitInfo> all)
        {
            List<RecruitInfo> rooms = new List<RecruitInfo>();
            Dictionary<string, RecruitInfo> byId =
                new Dictionary<string, RecruitInfo>(StringComparer.Ordinal);
            List<string> ids = new List<string>();

            for (int i = 0; i < all.Count; i++)
            {
                RecruitInfo room = all[i];
                if (room == null || IsMyRoom(room)) continue;
                string id = Identity(room);
                if (string.IsNullOrEmpty(id) || byId.ContainsKey(id)) continue;
                byId[id] = room;
                rooms.Add(room);
                ids.Add(id);
            }
            ids.Sort(StringComparer.Ordinal);
            string signature = string.Join("|", ids.ToArray());

            RoomSnapshot old = Snapshot;
            if (old.Signature == signature) return;

            RoomSnapshot next = new RoomSnapshot(old.Version + 1, rooms.ToArray(), byId, signature);
            Interlocked.Exchange(ref _snapshot, next);
            LinkLog.Debug("招募快照 v" + next.Version + "：" + next.Rooms.Length + " 个房间");
        }

        public static string Identity(RecruitInfo info)
        {
            if (info == null) return null;
            try { return info.IpAddress + " : " + info.MusicID; }
            catch (Exception) { return null; }
        }

        public static bool IsMyRoom(RecruitInfo room)
        {
            if (room == null || Client == null || string.IsNullOrEmpty(Client.Keychip)) return false;
            try
            {
                byte[] mine = LinkStub.ToIp(Client.StubIp).GetAddressBytes();
                byte[] theirs = room.IpAddress.GetAddressBytes();
                if (theirs == null || theirs.Length != mine.Length) return false;
                for (int i = 0; i < mine.Length; i++)
                    if (mine[i] != theirs[i]) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TryFetchRelayInfo()
        {
            string url = LinkLobby.Combine(Config.LobbyUrl, "/info");
            string body = LinkLobby.GetWithTimeout(url, LinkLobby.TimeoutMs);
            if (string.IsNullOrEmpty(body)) return false;
            ServerInfo info = JsonUtility_FromJson<ServerInfo>(body);
            if (info == null || string.IsNullOrEmpty(info.relayHost)) return false;
            Client.Host = info.relayHost;
            Client.Port = info.relayPort > 0 ? info.relayPort : LinkConfig.DefaultRelayPort;
            LinkLog.Msg("中继地址（来自大厅）：" + Client.Host + ":" + Client.Port);
            return true;
        }

        public static void MainThreadPump()
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame == _lastPumpFrame) return;
            _lastPumpFrame = frame;
            PatchesParty.OnMainThreadPump();
        }

        public static void Stop()
        {
            Stopping = true;
            if (Client != null) Client.Stop();
        }

        public static T JsonUtility_FromJson<T>(string text) where T : class
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return UnityEngine.JsonUtility.FromJson<T>(text); }
            catch (Exception ex)
            {
                LinkLog.Debug("JSON 解析失败：" + ex.Message);
                return null;
            }
        }
    }

    public sealed class RoomSnapshot
    {
        public static readonly RoomSnapshot Empty = new RoomSnapshot(
            0, new RecruitInfo[0],
            new Dictionary<string, RecruitInfo>(StringComparer.Ordinal), "");

        public readonly int Version;
        public readonly RecruitInfo[] Rooms;
        public readonly Dictionary<string, RecruitInfo> ById;
        public readonly string Signature;

        public RoomSnapshot(int version, RecruitInfo[] rooms,
            Dictionary<string, RecruitInfo> byId, string signature)
        {
            Version = version;
            Rooms = rooms;
            ById = byId;
            Signature = signature;
        }
    }

    [Serializable]
    public class ServerInfo
    {
        public string relayHost;
        public int relayPort;
    }

    [Serializable]
    public class OnlineUserInfo
    {
        public int totalUsers;
        public int activeRecruits;
    }

    [Serializable]
    public class RecruitRecordOut
    {
        public RecruitInfo RecruitInfo;
        public string Keychip;
    }

    [Serializable]
    public class RecruitRecordIn
    {
        public RecruitInfo RecruitInfo;
        public string Keychip;
    }
}
