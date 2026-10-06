// InStoreLink —— 中继连接（把游戏本体的"局域网 party"搬到公网上的那一层）
//
// 一条 TCP 连接 + 四个队列：
//   SendQ          ：要发出去的消息（由收发线程每 10ms 抽干）
//   TcpRecvQ       ：收到的流数据，key = 流ID + 本地端口（上游的约定，见 LinkSocket）
//   UdpRecvQ       ：收到的 UDP 数据，key = 本地端口
//   AcceptQ        ：收到的建流请求，key = 本地端口（等游戏自己 Accept）
//   AcceptPending ：自己主动建流后，等对端 accept 的挂起记录，key = 流ID + 本地端口
//                   （带超时器：对方一直不接就判这次连接失败，见 AddAcceptPending）
//
// 和上游 FutariClient 的差别（都是为了少踩坑，协议不变）：
//   1. 支持 Stop()：退出游戏时把收发线程停掉，不再无限重连（上游只会一直重连）
//   2. 心跳延迟窗口、状态码的语义保持一致（-1 失败 / 0 未连 / 1 连接中 / 2 已连）
//   3. 连不上时保留最后一次的错误信息，方便日志排查

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace InStoreLink
{
    /// <summary>
    /// 一次"我主动建流、正等对方 Accept"的挂起状态。
    /// 除了那个异步事件参数，还带一个超时器 —— 上游只挂个回调就撒手不管，
    /// 对方永远不 Accept 的话，游戏会一直停在"连接中"。
    /// </summary>
    public sealed class PendingAccept
    {
        public SocketAsyncEventArgs Args;
        public Timer Timeout;
        // 这条挂起对应的中继报文五元组：超时收尾时要拿它们发 CTL_TCP_CLOSE 告诉服务端
        public int Proto;
        public int Sid;
        public int SPort;
        public uint Dst;
        public int DPort;
    }

    public class LinkClient
    {
        public const int StatusFailed = -1;
        public const int StatusIdle = 0;
        public const int StatusConnecting = 1;
        public const int StatusConnected = 2;

        private const int SendTickMs = 10;         // 发送线程的节拍
        private const int HeartbeatMs = 1000;      // 心跳间隔
        private const int DelayWindowSize = 20;    // 延迟滑动窗口
        /// <summary>
        /// 建流之后等对方接流的时限；超了就当作这次连接失败（见 AddAcceptPending）。
        /// 默认 8 秒；写成可写字段是为了让 tests/ClientTests.cs 能把它调短，
        /// 不用为了验一条超时真等 8 秒。
        /// </summary>
        public static int AcceptTimeoutMs = 8000;

        public static LinkClient Instance;

        public string Keychip;
        public string Host;
        public int Port;

        public readonly ConcurrentQueue<LinkMsg> SendQ = new ConcurrentQueue<LinkMsg>();
        public readonly ConcurrentDictionary<int, ConcurrentQueue<LinkMsg>> TcpRecvQ =
            new ConcurrentDictionary<int, ConcurrentQueue<LinkMsg>>();
        public readonly ConcurrentDictionary<int, ConcurrentQueue<LinkMsg>> UdpRecvQ =
            new ConcurrentDictionary<int, ConcurrentQueue<LinkMsg>>();
        public readonly ConcurrentDictionary<int, ConcurrentQueue<LinkMsg>> AcceptQ =
            new ConcurrentDictionary<int, ConcurrentQueue<LinkMsg>>();
        /// <summary>自己主动建流、还在等对方接流的请求，key = 流ID + 本地端口。</summary>
        public readonly ConcurrentDictionary<int, PendingAccept> AcceptPending =
            new ConcurrentDictionary<int, PendingAccept>();

        private TcpClient _tcp;
        private StreamWriter _writer;
        private StreamReader _reader;
        private Thread _sendThread;
        private Thread _recvThread;
        private volatile bool _stopping;
        private int _reconnecting;               // 0/1，避免重入

        private readonly Stopwatch _heartbeat = Stopwatch.StartNew();
        private readonly long[] _delayWindow = new long[DelayWindowSize];
        private int _delayIndex;
        private long _delayAvg;

        private uint? _stubCache;
        private string _stubCacheKeychip;      // 上面那个缓存是按哪个 keychip 算出来的
        private readonly object _stubLock = new object();

        public LinkClient(string keychip, string host, int port)
        {
            Keychip = keychip;
            Host = host;
            Port = port;
            for (int i = 0; i < _delayWindow.Length; i++) _delayWindow[i] = -1;
        }

        /// <summary>本次启动用的伪 IP（keychip 的 md5 前 4 字节）。</summary>
        public uint StubIp
        {
            get
            {
                // 注意：BeforePatch 里 new 这个对象时用的还是占位 keychip，真正的 keychip
                // 要等刷卡登录（StartClient）才设进来。所以缓存必须跟着 keychip 走 ——
                // 不然只要在刷卡之前有人读过一次（比如游戏调 Util.MyIpAddress），
                // 这一局就会一直用错的身份，伪 IP 和中继那边对不上。
                //
                // 加锁：读它的除了游戏主线程，还有接收线程（每包日志里的 StubIp）和
                // 建流线程；两个字段分开写，并发下可能"值是新的、缓存键是旧的"，
                // 于是短暂算出错的伪 IP —— 而伪 IP 错了就是"目标不在线"。
                lock (_stubLock)
                {
                    if (!_stubCache.HasValue || !string.Equals(_stubCacheKeychip, Keychip,
                                                               StringComparison.Ordinal))
                    {
                        _stubCache = LinkStub.FromKeychip(Keychip);
                        _stubCacheKeychip = Keychip;
                    }
                    return _stubCache.Value;
                }
            }
        }

        public IPAddress StubAddress
        {
            get { return LinkStub.ToIp(StubIp); }
        }

        /// <summary>-1 连不上 / 0 未连 / 1 连接中 / 2 已连。</summary>
        public int StatusCode { get; private set; }

        public string ErrorMsg { get; private set; }

        /// <summary>
        /// "这次加入没成功"的交接位：客户端线程写，游戏主线程取（见 PatchesParty 里的
        /// ConnectSocket.Execute_Connect 补丁）。**按流 ID 分开记**：以前是一张全局条子，
        /// 结果旧那一次的超时会把玩家刚按的新一次连接掐掉（日志里实测到过）。
        /// 超过这个时间的条子会被丢掉，免得张冠李戴到下一次连接上。
        /// </summary>
        public const int JoinFailureWindowMs = 3000;
        private static readonly ConcurrentDictionary<int, string> _joinFailures =
            new ConcurrentDictionary<int, string>();
        private static readonly ConcurrentDictionary<int, int> _joinFailureAt =
            new ConcurrentDictionary<int, int>();

        /// <summary>
        /// 报告"这次加入失败"（建流超时 / 对方取消了）。
        ///
        /// 注意**不能**直接给那个 SocketAsyncEventArgs 触发 Completed：本体只把
        /// "完成事件来过"当成"连上了" —— `ConnectSocket.Execute_Connect` 只检查
        /// `_connectDone` 这个布尔，压根不看 `SocketError`，触发之后游戏会进 Active、
        /// 然后卡在联机选曲那边（比不触发还糟）。所以这里只留个条子，由主线程的补丁
        /// 去调本体自己的 `SocketBase.error()`，让游戏按"连接失败"的原有流程收尾。
        /// </summary>
        public static void ReportJoinFailure(int key, string message)
        {
            _joinFailures[key] = message;
            _joinFailureAt[key] = Environment.TickCount;
        }

        /// <summary>主线程取走**某条流**的失败原因；没有（或者已经太旧）返回 null。</summary>
        public static string TakeJoinFailure(int key)
        {
            string msg;
            if (!_joinFailures.TryRemove(key, out msg) || msg == null) return null;
            int at;
            _joinFailureAt.TryRemove(key, out at);
            if (unchecked(Environment.TickCount - at) > JoinFailureWindowMs) return null;
            return msg;
        }

        /// <summary>最近 20 次心跳的平均往返（ms），0 表示还没测出来。</summary>
        public long DelayAvg
        {
            // 接收线程写、游戏主线程读（状态栏那行"延迟"）。long 在 32 位运行时不是
            // 原子写，直接读可能读到一半新一半旧的值 —— 表现是状态栏偶尔闪一个荒谬的
            // 延迟数字。Interlocked.Read / Exchange 两个方向都原子。
            get { return Interlocked.Read(ref _delayAvg); }
        }

        public bool Stopping
        {
            get { return _stopping; }
        }

        public void ConnectAsync()
        {
            Thread t = new Thread(ConnectLoop);
            t.IsBackground = true;
            t.Name = "InStoreLink-Connect";
            t.Start();
        }

        public void Stop()
        {
            _stopping = true;
            CloseSocket();
            foreach (int key in new List<int>(AcceptPending.Keys)) CancelAccept(key);
            Thread send = _sendThread, recv = _recvThread;
            _sendThread = null;
            _recvThread = null;
            if (send != null) { try { send.Abort(); } catch (Exception) { } }
            if (recv != null) { try { recv.Abort(); } catch (Exception) { } }
            StatusCode = StatusIdle;
        }

        private void CloseSocket()
        {
            try { if (_tcp != null) _tcp.Close(); }
            catch (Exception) { }
            _tcp = null;
        }

        private void ConnectLoop()
        {
            while (!_stopping)
            {
                StatusCode = StatusConnecting;
                try
                {
                    _tcp = new TcpClient();
                    _tcp.NoDelay = true;
                    _tcp.Connect(Host, Port);
                }
                catch (Exception ex)
                {
                    StatusCode = StatusFailed;
                    ErrorMsg = ex.Message;
                    LinkLog.Error("连不上中继 " + Host + ":" + Port + " —— " + ex.Message);
                    CloseSocket();
                    for (int i = 0; i < 30 && !_stopping; i++) Thread.Sleep(100);   // 3 秒后重试
                    continue;
                }

                if (_stopping)
                {
                    CloseSocket();
                    return;
                }

                Stream stream = _tcp.GetStream();
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
                StatusCode = StatusConnected;

                Send(new LinkMsg { Cmd = (int)LinkCmd.CtlStart, Data = Keychip });
                // 用 Msg（常显）：README / 配置清单 / 给朋友的说明 / 技术文档都写着
                // "日志里应该出现这一行"，默认 Debug=false 的玩家必须也能看到。
                LinkLog.Msg("已连接中继 " + Host + ":" + Port + "（本机伪 IP " + StubIp + "）");

                _sendThread = StartLoop(SendLoop, "InStoreLink-Send");
                _recvThread = StartLoop(RecvLoop, "InStoreLink-Recv");
                return;
            }
        }

        private Thread StartLoop(ThreadStart body, string name)
        {
            Thread t = new Thread(body);
            t.IsBackground = true;
            t.Name = name;
            t.Start();
            return t;
        }

        private void SendLoop()
        {
            try
            {
                while (!_stopping)
                {
                    Thread.Sleep(SendTickMs);
                    if (_stopping) return;
                    if (_heartbeat.ElapsedMilliseconds > HeartbeatMs)
                    {
                        _heartbeat.Restart();
                        Send(new LinkMsg { Cmd = (int)LinkCmd.CtlHeartbeat });
                    }
                    LinkMsg msg;
                    while (SendQ.TryDequeue(out msg)) WriteLine(msg);
                }
            }
            catch (ThreadAbortException) { }
            catch (Exception ex)
            {
                LinkLog.Error("发送线程出错：" + ex.Message);
                Reconnect("发送线程");
            }
        }

        private void RecvLoop()
        {
            try
            {
                while (!_stopping)
                {
                    string line = _reader.ReadLine();
                    if (line == null) break;                 // 对端断开
                    line = line.Trim('\uFEFF', '\r', '\n');
                    if (line.Length == 0) continue;
                    HandleIncoming(LinkMsg.Parse(line));
                }
            }
            catch (ThreadAbortException) { }
            catch (Exception ex)
            {
                if (!_stopping) LinkLog.Error("接收线程出错：" + ex.Message);
            }
            if (!_stopping) Reconnect("接收线程结束");
        }

        private void Reconnect(string why)
        {
            if (_stopping) return;
            if (Interlocked.Exchange(ref _reconnecting, 1) == 1) return;
            try
            {
                LinkLog.Warn(why + "，重连中…");
                CloseSocket();
                Thread send = _sendThread, recv = _recvThread;
                _sendThread = null;
                _recvThread = null;
                // ★ 别 Abort **当前线程**：Reconnect 就是从 SendLoop/RecvLoop 里调出来的，
                //   而 Thread.Abort() 抛的 ThreadAbortException 即使被 catch 住，也会在
                //   catch 末尾被运行时自动重抛 —— 于是这个线程当场死掉，下面那句
                //   ConnectAsync() 根本执行不到，"断开后自动重连"整条路是死的
                //   （表现：中继断一次之后客户端就再也不收发，日志里也没有重连记录）。
                //   当前线程本来就该返回了（调用点在循环末尾），让它自然结束即可。
                Thread self = Thread.CurrentThread;
                if (send != null && send != self) { try { send.Abort(); } catch (Exception) { } }
                if (recv != null && recv != self) { try { recv.Abort(); } catch (Exception) { } }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
            if (!_stopping) ConnectAsync();
        }

        /// <summary>注册本地监听端口（游戏本体在 Bind 时调用）。</summary>
        public void Bind(int port, int proto)
        {
            if (proto == LinkProto.Tcp)
                AcceptQ.TryAdd(port, new ConcurrentQueue<LinkMsg>());
            else if (proto == LinkProto.Udp)
                UdpRecvQ.TryAdd(port, new ConcurrentQueue<LinkMsg>());
        }

        /// <summary>把一条消息交给上层处理（收到的包、以及自己发给自己的包都走这里）。</summary>
        public void HandleIncoming(LinkMsg msg)
        {
            if (msg.Cmd != (int)LinkCmd.CtlHeartbeat)
                LinkLog.Info(StubIp + " <<< " + msg.Readable());

            switch (msg.Command)
            {
                case LinkCmd.CtlStart:
                    if (!string.IsNullOrEmpty(msg.Data))
                    {
                        LinkLog.Info("服务端注册回应：" + msg.Data);
                        // 服务端回的是 "version=N"；号对不上就提醒一句（协议相同但版本不同）
                        int server;
                        if (msg.Data.StartsWith("version=", StringComparison.Ordinal)
                            && int.TryParse(msg.Data.Substring("version=".Length), out server)
                            && server != LinkProto.Version)
                        {
                            LinkLog.Warn("服务端协议版本是 " + server + "，我们按 "
                                         + LinkProto.Version + " 写的，可能会连不上");
                        }
                    }
                    break;

                case LinkCmd.CtlHeartbeat:
                    long delay = _heartbeat.ElapsedMilliseconds;
                    _delayWindow[_delayIndex] = delay;
                    _delayIndex = (_delayIndex + 1) % _delayWindow.Length;
                    long sum = 0; int n = 0;
                    for (int i = 0; i < _delayWindow.Length; i++)
                    {
                        if (_delayWindow[i] < 0) continue;
                        sum += _delayWindow[i];
                        n++;
                    }
                    Interlocked.Exchange(ref _delayAvg, n == 0 ? 0 : sum / n);
                    LinkLog.Info("心跳 " + delay + "ms（均值 " + _delayAvg + "ms）");
                    break;

                case LinkCmd.DataSend:
                case LinkCmd.DataBroadcast:
                    if (msg.Proto == LinkProto.Udp && msg.DPort.HasValue)
                    {
                        ConcurrentQueue<LinkMsg> q = UdpRecvQ.Get(msg.DPort.Value);
                        if (q != null) q.Enqueue(msg);
                    }
                    else if (msg.Proto == LinkProto.Tcp && msg.Sid.HasValue && msg.DPort.HasValue)
                    {
                        ConcurrentQueue<LinkMsg> q = TcpRecvQ.Get(msg.Sid.Value + msg.DPort.Value);
                        if (q != null) q.Enqueue(msg);
                    }
                    break;

                case LinkCmd.CtlTcpConnect:
                    if (msg.DPort.HasValue)
                    {
                        ConcurrentQueue<LinkMsg> q = AcceptQ.Get(msg.DPort.Value);
                        if (q != null) q.Enqueue(msg);
                    }
                    break;

                case LinkCmd.CtlTcpAccept:
                    if (msg.Sid.HasValue && msg.DPort.HasValue)
                        CompleteAccept(msg.Sid.Value + msg.DPort.Value);
                    break;

                case LinkCmd.CtlTcpClose:
                    // 服务端在"对端不在线 / 那条流已经废了 / 房间关了"时会主动推这条
                    HandleClose(msg);
                    break;
            }
        }

        // ------------------------------------------------------- 建流的挂起 / 超时 / 取消

        /// <summary>
        /// 登记一次"等对方接流"，并起一个一次性超时器。
        /// 超时那一支见 <see cref="FailAccept"/>。
        /// </summary>
        public void AddAcceptPending(int key, PendingAccept pending)
        {
            if (pending == null) return;
            AcceptPending[key] = pending;
            pending.Timeout = new Timer(delegate(object state)
            {
                FailAccept(key, "建流超时：" + AcceptTimeoutMs + "ms 内对方没有接流"
                                + "（对方不在线 / 房间已经关了 / 或者他直接开打了）");
            }, null, AcceptTimeoutMs, Timeout.Infinite);
        }

        /// <summary>
        /// 对方接流了：摘掉挂起记录、停掉超时器，再通知游戏"连接成功"。
        /// 必须 TryRemove 而不是 TryGetValue —— 否则超时器晚一步还会触发第二次
        /// Completed，游戏那边就会收到两次连接完成。
        /// </summary>
        public void CompleteAccept(int key)
        {
            PendingAccept pending;
            if (!AcceptPending.TryRemove(key, out pending) || pending == null) return;
            StopTimer(pending);
            LinkLog.Info("建流已被对方接受");
            LinkSocket.InvokeCompleted(pending.Args, SocketError.Success);
        }

        /// <summary>
        /// 这次建流失败了（超时 / 对方取消）。
        ///
        /// 交给主线程去走本体自己的错误路径（见 LinkRuntime.ReportJoinFailure 的注释：
        /// 直接触发 Completed 会让游戏以为连上了）。
        /// </summary>
        public void FailAccept(int key, string why)
        {
            PendingAccept pending;
            if (!AcceptPending.TryRemove(key, out pending) || pending == null) return;
            StopTimer(pending);
            LinkLog.Warn(why);
            // 顺手告诉服务端"这条挂起我不要了"。以前只清本地，服务端那条要留满它的
            // pending 超时（默认 10 秒）—— 连点几次就攒起来，攒到上限之后**新的一次
            // 加入会被服务端当场拒掉**，玩家看到的是"一按 NEXT 就弹提示退出"。
            Send(new LinkMsg
            {
                Cmd = (int)LinkCmd.CtlTcpClose, Proto = pending.Proto, Sid = pending.Sid,
                Src = StubIp, SPort = pending.SPort, Dst = pending.Dst, DPort = pending.DPort
            });
            ReportJoinFailure(key, why);
        }

        /// <summary>放弃一次挂起（游戏自己把 socket 关了、或我们退出时）。</summary>
        public bool CancelAccept(int key)
        {
            PendingAccept pending;
            if (!AcceptPending.TryRemove(key, out pending) || pending == null) return false;
            StopTimer(pending);
            return true;
        }

        private static void StopTimer(PendingAccept pending)
        {
            if (pending.Timeout == null) return;
            try { pending.Timeout.Dispose(); } catch (Exception) { }
            pending.Timeout = null;
        }

        /// <summary>收到服务端的"这条流没了"。</summary>
        private void HandleClose(LinkMsg msg)
        {
            if (!msg.Sid.HasValue || !msg.DPort.HasValue) return;
            int key = msg.Sid.Value + msg.DPort.Value;

            // 1) 我还在等对方接流 → 这次"加入"直接失败（最常见的一种：
            //    房主没等人就开打了，服务端把挂起的建流取消掉）。
            if (AcceptPending.ContainsKey(key))
            {
                // 服务端现在会把拒绝原因放在 data 里（目标不在线 / 挂起流过多 / 挂起已回收…）
                string reason = string.IsNullOrEmpty(msg.Data) ? "对方取消了这次建流" : msg.Data;
                FailAccept(key, "加入失败：" + reason + "（sid " + msg.Sid.Value + "）");
                return;
            }

            // 2) 我这边还排着一条没 Accept 的建流请求 → 丢掉它，
            //    别让游戏去接受一条已经死掉的流。
            ConcurrentQueue<LinkMsg> accepts = AcceptQ.Get(msg.DPort.Value);
            if (accepts != null && !accepts.IsEmpty) DropQueuedAccept(msg.DPort.Value, msg.Sid.Value);

            // 3) 已经建好的流 → 把空的接收队列清掉；还有没读走的数据就先留着。
            ConcurrentQueue<LinkMsg> recv = TcpRecvQ.Get(key);
            if (recv != null)
            {
                if (recv.IsEmpty)
                {
                    ConcurrentQueue<LinkMsg> dropped;
                    TcpRecvQ.TryRemove(key, out dropped);
                }
                LinkLog.Info("对端关闭了流 " + msg.Sid.Value);
            }
        }

        /// <summary>
        /// 把某条已经作废的建流请求从待 Accept 队列里摘掉。
        /// ConcurrentQueue 删不了中间项，只能倒出来再放回去。
        /// </summary>
        public void DropQueuedAccept(int port, int sid)
        {
            ConcurrentQueue<LinkMsg> q = AcceptQ.Get(port);
            if (q == null) return;
            List<LinkMsg> keep = new List<LinkMsg>();
            LinkMsg m;
            while (q.TryDequeue(out m))
            {
                if (m.Sid.HasValue && m.Sid.Value == sid) continue;
                keep.Add(m);
            }
            foreach (LinkMsg item in keep) q.Enqueue(item);
        }

        /// <summary>入队一条消息；如果目标是本机伪 IP，就地处理（相当于自己连自己）。</summary>
        public void Send(LinkMsg msg)
        {
            if (msg.Dst.HasValue && msg.Dst.Value == StubIp)
            {
                LinkLog.Debug("本机回环 " + msg.Readable());
                HandleIncoming(msg);
                return;
            }
            SendQ.Enqueue(msg);
        }

        private void WriteLine(LinkMsg msg)
        {
            StreamWriter w = _writer;
            if (w == null) return;
            try
            {
                w.WriteLine(msg.ToString());
                if (msg.Cmd != (int)LinkCmd.CtlHeartbeat)
                    LinkLog.Info(StubIp + " >>> " + msg.Readable());
            }
            catch (Exception ex)
            {
                if (!_stopping) LinkLog.Error("写入失败：" + ex.Message);
            }
        }
    }

    /// <summary>ConcurrentDictionary.TryGetValue 的糖，省得每次写 out。</summary>
    internal static class DictEx
    {
        public static V Get<K, V>(this ConcurrentDictionary<K, V> dict, K key) where V : class
        {
            V value;
            return dict.TryGetValue(key, out value) ? value : null;
        }
    }
}
