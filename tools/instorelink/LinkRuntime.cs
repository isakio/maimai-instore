// InStoreLink —— 运行时状态（相当于上游的 Futari 静态类）
//
// 这里放"整个 mod 共用"的东西：配置、中继连接、NFSocket→影子 socket 的映射表、
// 招募列表轮询用的缓存、以及几处补丁要用的反射句柄。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using DB;
using Manager;
using Manager.Party.Party;
using MAI2.Util;
using Monitor;
using PartyLink;
using Process;

namespace InStoreLink
{
    public static class LinkRuntime
    {
        public static LinkConfig Config;
        public static LinkClient Client;
        public static bool Stopping;

        /// <summary>
        /// 游戏本体的 NFSocket → 我们的影子 socket。
        /// 用 ConcurrentDictionary：写它的是游戏线程（NFSocket 构造），而读它的除了游戏线程，
        /// 还有"中继收到建流确认 → 回调"这条路径（可能在接收线程上）——
        /// 普通 Dictionary 在这种交叉访问下没有保证。
        /// </summary>
        public static readonly ConcurrentDictionary<NFSocket, LinkSocket> Redirect =
            new ConcurrentDictionary<NFSocket, LinkSocket>();

        /// <summary>Packet.write_uint(PacketType, int, uint)：禁用加解密时用来回写长度。</summary>
        public static MethodInfo PacketWriteUInt;

        /// <summary>MusicSelectProcess.RecruitData 的 setter（IsConnectStart 里要直接写进去）。</summary>
        public static MethodInfo SetRecruitData;

        /// <summary>MusicSelectProcess.IsConnectCategoryEnable 的 setter（永远置 false，留着给以后用）。</summary>
        public static MethodInfo SetConnectCategoryEnable;

        /// <summary>上一次招募列表快照，key = "伪IP : 曲目ID"。</summary>
        public static Dictionary<string, RecruitInfo> LastRecruits = new Dictionary<string, RecruitInfo>();

        /// <summary>
        /// 最近一次真正翻译进"联机歌曲列表"的房间，**顺序就是游戏里光标的下标顺序**。
        /// 和 LastRecruits（大厅原始列表）不是一回事：装不了的歌会被跳过，
        /// 所以按光标取房间必须用这一份，否则会显示 A 的歌、进去却是 B 的房间。
        /// null = 还没翻译过（那时才退回用原始列表）。
        /// </summary>
        public static List<RecruitInfo> ConnectList;

        public static int OnlineUserCount;
        public static int MusicIdSum;
        public static bool SideMessageFlag;
        /// <summary>
        /// 上一次真正写进 MusicSelectProcess.RecruitData 的房间（Identity 字符串）。
        /// 用来避免"光标没动也每帧重设一遍"，也让 IsConnectStart 能在**换房间**时
        /// 重新对准（见 PreIsConnectStart）。
        /// </summary>
        public static string LastRecruitId;
        /// <summary>诊断用：上一次打印的选曲状态组合，只在组合变化时打一行。</summary>
        public static string LastStateSig;
        /// <summary>
        /// 上一次见到的"已经进了某间房"（IManager.IsConnect()）；用来在刚连上的那一帧
        /// 记下进的是哪一间（JoinedRoomId）。
        /// </summary>
        public static bool WasConnected;
        /// <summary>
        /// 当前真正连着的房间（Identity）。人按 BACK 回到房间列表、光标挪到另一间之后，
        /// 靠它跟光标所在的房间比，判断"人已经退出上一间了"。
        /// </summary>
        public static string JoinedRoomId;
        /// <summary>
        /// 上一帧人是不是站在「店内マッチング」那一栏里。松开"上次选中的房间"必须
        /// **边沿触发**（在栏里 → 离开栏），不能只看"当前不在栏里"：外面浏览普通
        /// 分类时 PreIsConnectStart 也会顺手把 RecruitData 对准某个房间（本体也是
        /// 这么干的），只看当前状态就会"设一次、松一次"每帧来回抖，
        /// 旧版还会顺带把 ConnectList 一起清掉 —— 那正是"进第 2 间→退出→按了没反应"。
        /// </summary>
        public static bool WasInConnectionFolder;
        /// <summary>诊断用：上一次看到的选曲光标位置（看这一栏能不能把光标挪到第 2 间）。</summary>
        public static int LastCursor = -1;

        private static bool _checkAuthCalled;
        private static bool _isInit;
        private static int _lastRoomCount = -1;   // 只在房间数变化时打日志，免得刷屏
        private static Thread _onlineThread;
        private static Thread _recruitThread;
        private static int _recruitPollingStarted;

        public static bool Verbose
        {
            get { return Config != null && Config.Debug; }
        }

        /// <summary>招募/party 的总管（上游叫 PartyMan）。</summary>
        public static IManager PartyMan
        {
            get { return Manager.Party.Party.Party.Get(); }
        }

        // ---------------------------------------------------------------- 初始化

        /// <summary>
        /// 补丁生效前要做的准备：解析反射句柄、读配置、决定中继地址。
        /// 上游是 MelonMod.OnInitializeMelon 里调 OnBeforePatch()。
        /// </summary>
        public static void BeforePatch()
        {
            LinkLog.Verbose = Verbose;

            PacketWriteUInt = typeof(Packet).GetMethod("write_uint",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(PacketType), typeof(int), typeof(uint) }, null);
            if (PacketWriteUInt == null) LinkLog.Error("找不到 Packet.write_uint（游戏版本可能不匹配）");

            PropertyInfo recruitData = typeof(MusicSelectProcess).GetProperty("RecruitData");
            // 注意：setter 是私有的，GetSetMethod()（不带参数）只会看公开的 setter，
            // 必须传 true 才会返回非公开的那一个 —— 第一次实测就栽在这里。
            if (recruitData != null) SetRecruitData = recruitData.GetSetMethod(true);
            if (SetRecruitData == null) LinkLog.Error("找不到 MusicSelectProcess.RecruitData 的 setter");

            PropertyInfo categoryEnable = typeof(MusicSelectProcess).GetProperty("IsConnectCategoryEnable");
            if (categoryEnable != null) SetConnectCategoryEnable = categoryEnable.GetSetMethod(true);

            Client = new LinkClient("A1234567890", "", LinkConfig.DefaultRelayPort);
            LinkClient.Instance = Client;

            string host;
            int port;
            if (LinkConfig.TryParseRelay(Config.RelayUrl, out host, out port))
            {
                Client.Host = host;
                Client.Port = port;
                LinkLog.Msg("按配置直连中继 " + host + ":" + port);
            }
            // 没配 RelayUrl 的话，中继地址要等刷卡登录后由 StartClient 的线程去大厅 /info 拿
            // （带重试，见 TryFetchRelayInfo）。
        }

        /// <summary>
        /// 从大厅 /info 取中继地址。同步 GET + 超时，拿不到就返回 false 由调用方稍后重试。
        /// （以前这里是异步回调、失败了没人重试：大厅只要在那一瞬间不通，这一整局就永远连不上。）
        /// </summary>
        private static bool TryFetchRelayInfo()
        {
            string url = LinkLobby.Combine(Config.LobbyUrl, "/info");
            string body = LinkLobby.GetWithTimeout(url, LinkLobby.TimeoutMs);
            if (string.IsNullOrEmpty(body))
            {
                LinkLog.Error("拿不到中继地址（" + url + "）");
                return false;
            }
            ServerInfo info = JsonUtility_FromJson<ServerInfo>(body);
            if (info == null || string.IsNullOrEmpty(info.relayHost))
            {
                LinkLog.Error("大厅 /info 返回的内容看不懂：" + body);
                return false;
            }
            Client.Host = info.relayHost;
            Client.Port = info.relayPort > 0 ? info.relayPort : LinkConfig.DefaultRelayPort;
            LinkLog.Msg("中继地址（来自大厅）：" + Client.Host + ":" + Client.Port);
            return true;
        }

        /// <summary>游戏登录时调用：随机 keychip → 连中继 → 起在线人数轮询线程。</summary>
        public static void StartClient()
        {
            if (_checkAuthCalled) return;
            _checkAuthCalled = true;
            if (_isInit) return;

            string keychip = LinkStub.NewKeychip(new Random());
            Thread thread = new Thread(delegate()
            {
                // 等中继地址：配置里给了 RelayUrl 的话 BeforePatch 已经填好，
                // 否则去大厅 /info 拿；拿不到就每 5 秒重试一次，而不是干等着永不重试。
                while (!Stopping && string.IsNullOrEmpty(Client.Host))
                {
                    if (!TryFetchRelayInfo() && !Stopping) Thread.Sleep(5000);
                }
                if (string.IsNullOrEmpty(Client.Host)) return;

                Client.Keychip = keychip;
                Client.ConnectAsync();
                _isInit = true;

                Thread.Sleep(2000);        // 等游戏本体初始化完
                FetchOnlineUserCount();
                _onlineThread = Interval(30000, FetchOnlineUserCount, "在线人数轮询");
            });
            thread.IsBackground = true;
            thread.Name = "InStoreLink-Init";
            thread.Start();
        }

        public static void FetchOnlineUserCount()
        {
            if (Stopping) return;
            string body = LinkLobby.Get(LinkLobby.Combine(Config.LobbyUrl, "/online"));
            if (string.IsNullOrEmpty(body)) return;
            OnlineUserInfo info = JsonUtility_FromJson<OnlineUserInfo>(body);
            if (info != null) OnlineUserCount = info.totalUsers;
        }

        /// <summary>
        /// 起一个"每隔 delay 毫秒跑一次 action"的后台线程（上游的 int.Interval 扩展）。
        /// </summary>
        public static Thread Interval(int delay, Action action, string name)
        {
            Thread thread = new Thread(delegate()
            {
                while (!Stopping)
                {
                    try
                    {
                        Thread.Sleep(delay);
                        if (Stopping) return;
                        action();
                    }
                    catch (ThreadInterruptedException) { return; }
                    catch (Exception ex)
                    {
                        LinkLog.Error(name + " 出错：" + ex.Message);
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = "InStoreLink-" + name;
            thread.Start();
            return thread;
        }

        /// <summary>招募列表轮询（10 秒一次），把 /recruit/list 的差量喂回游戏本体。</summary>
        public static void StartRecruitPolling(Client client)
        {
            // 本体的 party 客户端如果每进一次选曲就重建一次，这里会被反复调用 ——
            // 不拦一下就是每进一次多一条 10 秒轮询线程（上游没有这层保护）。
            if (Interlocked.Exchange(ref _recruitPollingStarted, 1) == 1) return;
            _recruitThread = Interval(10000, delegate()
            {
                if (Stopping || client == null) return;

                string body = LinkLobby.Get(LinkLobby.Combine(Config.LobbyUrl, "/recruit/list"));
                if (string.IsNullOrEmpty(body))
                {
                    if (_lastRoomCount != 0)
                    {
                        _lastRoomCount = 0;
                        LinkLog.Debug("招募列表：当前没有房间");
                    }
                    return;
                }

                List<RecruitInfo> current = new List<RecruitInfo>();
                foreach (string line in body.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    RecruitRecordIn record = JsonUtility_FromJson<RecruitRecordIn>(trimmed);
                    if (record != null && record.RecruitInfo != null) current.Add(record.RecruitInfo);
                }

                // 只更新快照，不在这里往游戏里塞东西：
                // 上游是"每轮把每个房间都重新喂一遍游戏"，游戏每次都当成新招募 → 每 10 秒响一次提示音。
                // 现在改成主线程对账（PatchesParty.ReconcileRecruits）：游戏缺哪个才补哪个。
                Dictionary<string, RecruitInfo> snapshot = new Dictionary<string, RecruitInfo>();
                foreach (RecruitInfo info in current) snapshot[Identity(info)] = info;
                LastRecruits = snapshot;
                if (_lastRoomCount != snapshot.Count)
                {
                    _lastRoomCount = snapshot.Count;
                    LinkLog.Debug("招募列表：现在有 " + snapshot.Count + " 个房间");
                }
            }, "招募列表轮询");
        }

        public static string Identity(RecruitInfo info)
        {
            return info.IpAddress + " : " + info.MusicID;
        }

        public static void Stop()
        {
            Stopping = true;
            if (Client != null) Client.Stop();
        }

        // ---------------------------------------------------------------- 小工具

        /// <summary>
        /// JsonUtility 的薄包装：解析失败（返回 null）不抛异常，也不打崩调用方。
        /// （JsonUtility 在字段缺失时是宽容的，这里主要防"响应根本不是 JSON"这种）
        /// </summary>
        public static T JsonUtility_FromJson<T>(string text) where T : class
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                return UnityEngine.JsonUtility.FromJson<T>(text);
            }
            catch (Exception ex)
            {
                LinkLog.Debug("JSON 解析失败：" + ex.Message + " / " + text);
                return null;
            }
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

    /// <summary>POST /recruit/start|finish 的请求体（字段名必须和上游一致）。</summary>
    [Serializable]
    public class RecruitRecordOut
    {
        public RecruitInfo RecruitInfo;
        public string Keychip;
    }

    /// <summary>GET /recruit/list 每行的响应体（Keychip/Time 服务端已去掉）。</summary>
    [Serializable]
    public class RecruitRecordIn
    {
        public RecruitInfo RecruitInfo;
        public string Keychip;
    }
}
