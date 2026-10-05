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
                if (!_stubCache.HasValue || !string.Equals(_stubCacheKeychip, Keychip,
                                                           StringComparison.Ordinal))
                {
                    _stubCache = LinkStub.FromKeychip(Keychip);
                    _stubCacheKeychip = Keychip;
                }
                return _stubCache.Value;
            }
        }

        public IPAddress StubAddress
        {
            get { return LinkStub.ToIp(StubIp); }
        }

        /// <summary>-1 连不上 / 0 未连 / 1 连接中 / 2 已连。</summary>
        public int StatusCode { get; private set; }

        public string ErrorMsg { get; private set; }

        /// <summary>最近 20 次心跳的平均往返（ms），0 表示还没测出来。</summary>
        public long DelayAvg
        {
            get { return _delayAvg; }
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
                if (send != null) { try { send.Abort(); } catch (Exception) { } }
                if (recv != null) { try { recv.Abort(); } catch (Exception) { } }
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
                    _delayAvg = n == 0 ? 0 : sum / n;
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
        public void AddAcceptPending(int key, SocketAsyncEventArgs args)
        {
            PendingAccept pending = new PendingAccept();
            pending.Args = args;
            AcceptPending[key] = pending;
            pending.Timeout = new Timer(delegate(object state)
            {
                FailAccept(key, SocketError.TimedOut,
                           "建流超时：" + AcceptTimeoutMs + "ms 内对方没有接流"
                           + "（房间可能已经关了，或者对方直接开打了）");
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
        /// 这次建流失败了（超时 / 对方取消 / 目标不在线）：让游戏的连接流程走失败分支，
        /// 而不是永远卡在"连接中"。
        /// </summary>
        public void FailAccept(int key, SocketError error, string why)
        {
            PendingAccept pending;
            if (!AcceptPending.TryRemove(key, out pending) || pending == null) return;
            StopTimer(pending);
            LinkLog.Warn(why);
            LinkSocket.InvokeCompleted(pending.Args, error);
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
                FailAccept(key, SocketError.ConnectionRefused,
                           "对方取消了这次建流（sid " + msg.Sid.Value + "），放弃连接");
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
