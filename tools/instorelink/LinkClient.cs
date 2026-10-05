// InStoreLink —— 中继连接（把游戏本体的"局域网 party"搬到公网上的那一层）
//
// 一条 TCP 连接 + 四个队列：
//   SendQ          ：要发出去的消息（由收发线程每 10ms 抽干）
//   TcpRecvQ       ：收到的流数据，key = 流ID + 本地端口（上游的约定，见 LinkSocket）
//   UdpRecvQ       ：收到的 UDP 数据，key = 本地端口
//   AcceptQ        ：收到的建流请求，key = 本地端口（等游戏自己 Accept）
//   AcceptCallbacks：自己主动建流后，等对端 accept 的回调，key = 流ID + 本地端口
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
    public class LinkClient
    {
        public const int StatusFailed = -1;
        public const int StatusIdle = 0;
        public const int StatusConnecting = 1;
        public const int StatusConnected = 2;

        private const int SendTickMs = 10;         // 发送线程的节拍
        private const int HeartbeatMs = 1000;      // 心跳间隔
        private const int DelayWindowSize = 20;    // 延迟滑动窗口

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
        public readonly ConcurrentDictionary<int, Action<LinkMsg>> AcceptCallbacks =
            new ConcurrentDictionary<int, Action<LinkMsg>>();

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
                LinkLog.Info("已连接中继 " + Host + ":" + Port + "（本机伪 IP " + StubIp + "）");

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
                    {
                        Action<LinkMsg> cb = AcceptCallbacks.Get(msg.Sid.Value + msg.DPort.Value);
                        if (cb != null) cb(msg);
                    }
                    break;
            }
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
