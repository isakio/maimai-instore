// InStoreLink —— 影子 Socket
//
// 游戏本体的 PartyLink 用的是它自己的 NFSocket（内部包着 System.Net.Sockets）。
// 我们不真连网，而是给每个 NFSocket 配一个"影子 socket"：
// 游戏调 Send/Receive/Accept/ConnectAsync/Poll…，我们就转成中继协议上的消息。
//
// 三个队列的 key 约定（跟上游一致，别改，否则两边对不上）：
//   TcpRecvQ[流ID + 本地端口]   收到的流数据
//   AcceptQ[本地端口]           收到的建流请求
//   AcceptCallbacks[流ID + 本地端口]  主动建流后等对端接受的回调
//
// 上游 https://github.com/MuNET-OSS/NyanLink （MIT），本文件是它的等价重写。

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace InStoreLink
{
    public class LinkSocket
    {
        // 游戏本体建 ListenSocket 时用的假端口（上游约定：55535~65534 里随机）
        private const int RandomPortMin = 55535;
        private const int RandomPortMax = 65535;

        private int _bindPort = -1;
        private int _streamId = -1;
        private readonly LinkClient _client;
        private readonly int _proto;

        public EndPoint RemoteEndPoint { get; private set; }

        public EndPoint LocalEndPoint
        {
            // 上游注释说这个只在 ConnectSocket.Enter_Active 里用到，但实际没人读；
            // 还是照原样给一个假的本地地址，免得哪天真读了。
            get { return new IPEndPoint(_client.StubAddress, 0); }
        }

        private LinkSocket(LinkClient client, int proto)
        {
            _client = client;
            _proto = proto;
            RemoteEndPoint = new IPEndPoint(_client.StubAddress, 0);
        }

        /// <summary>游戏本体 new NFSocket(...) 时走这个构造。</summary>
        public LinkSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, int mockId)
            : this(LinkClient.Instance, (int)protocolType)
        {
        }

        public void Listen(int backlog)
        {
            // 服务端（房主）侧的监听：中继帮我们收包，这里什么都不用做
        }

        public void Bind(EndPoint localEndPoint)
        {
            IPEndPoint ep = localEndPoint as IPEndPoint;
            if (ep == null) return;
            _bindPort = ep.Port;
            _client.Bind(_bindPort, _proto);
            LinkLog.Debug("影子 socket 绑定端口 " + _bindPort + "（" + LinkMsg.ProtocolName(_proto) + "）");
        }

        public void SetSocketOption(SocketOptionLevel level, SocketOptionName name, bool value)
        {
            // 只有广播 socket 会调，忽略
        }

        /// <summary>游戏在阻塞调用前用它做 select。</summary>
        public static bool Poll(LinkSocket socket, SelectMode mode)
        {
            if (socket == null) return false;
            if (mode != SelectMode.SelectRead) return mode == SelectMode.SelectWrite;

            if (socket._proto == LinkProto.Udp)
            {
                ConcurrentQueue<LinkMsg> q = socket._client.UdpRecvQ.Get(socket._bindPort);
                return q != null && !q.IsEmpty;
            }
            if (socket._streamId == -1)
            {
                ConcurrentQueue<LinkMsg> q = socket._client.AcceptQ.Get(socket._bindPort);
                return q != null && !q.IsEmpty;
            }
            ConcurrentQueue<LinkMsg> qq = socket._client.TcpRecvQ.Get(socket._streamId + socket._bindPort);
            return qq != null && !qq.IsEmpty;
        }

        private static readonly FieldInfo CompletedField = typeof(SocketAsyncEventArgs)
            .GetField("Completed", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// 游戏要主动连对方（房客侧）。目标地址来自房主发的招募数据，
        /// 所以房主那边必须把 MyIpAddress 换成伪 IP（见 Patches 里的 preMyIpAddress）。
        /// </summary>
        public bool ConnectAsync(SocketAsyncEventArgs e, int mockId)
        {
            IPEndPoint remote = e.RemoteEndPoint as IPEndPoint;
            if (remote == null) return false;

            uint addr = LinkStub.ToU32(remote.Address);

            // 本机会把 127.0.0.1/localhost 当成自己，换成伪 IP
            if (addr == 2130706433u || addr == 16777343u) addr = _client.StubIp;

            Random random = new Random();
            _streamId = random.Next();
            _bindPort = random.Next(RandomPortMin, RandomPortMax);

            int key = _streamId + _bindPort;
            _client.TcpRecvQ[key] = new ConcurrentQueue<LinkMsg>();
            _client.AcceptCallbacks[key] = delegate(LinkMsg msg)
            {
                LinkLog.Info("建流已被对方接受");
                InvokeCompleted(e);
            };

            _client.Send(new LinkMsg
            {
                Cmd = (int)LinkCmd.CtlTcpConnect,
                Proto = _proto,
                Sid = _streamId,
                Src = _client.StubIp,
                SPort = _bindPort,
                Dst = addr,
                DPort = remote.Port
            });
            RemoteEndPoint = new IPEndPoint(LinkStub.ToIp(addr), remote.Port);
            return true;
        }

        /// <summary>
        /// 通知 SocketAsyncEventArgs 的 Completed 事件（上游也是这么反射调的）。
        /// 游戏本体的 ConnectAsync 是异步 API，不触发 Completed 的话它会一直等。
        /// </summary>
        private static void InvokeCompleted(SocketAsyncEventArgs e)
        {
            if (CompletedField == null || e == null) return;
            MulticastDelegate handlers = CompletedField.GetValue(e) as MulticastDelegate;
            if (handlers == null) return;
            Delegate[] list = handlers.GetInvocationList();
            foreach (Delegate handler in list)
            {
                try
                {
                    SocketAsyncEventArgs args = new SocketAsyncEventArgs();
                    args.SocketError = SocketError.Success;
                    handler.DynamicInvoke(e, args);
                }
                catch (Exception ex)
                {
                    LinkLog.Error("触发 Completed 失败：" + ex.Message);
                }
            }
        }

        /// <summary>房主侧接受一个建流请求（游戏在阻塞 Accept 里调）。</summary>
        public LinkSocket Accept()
        {
            ConcurrentQueue<LinkMsg> q = _client.AcceptQ.Get(_bindPort);
            LinkMsg msg;
            if (q == null || !q.TryDequeue(out msg) || !msg.Sid.HasValue || !msg.Src.HasValue)
            {
                LinkLog.Warn("Accept：当前没有待处理的建流请求");
                return null;
            }

            _client.TcpRecvQ[msg.Sid.Value + _bindPort] = new ConcurrentQueue<LinkMsg>();
            _client.Send(new LinkMsg
            {
                Cmd = (int)LinkCmd.CtlTcpAccept,
                Proto = _proto,
                Sid = msg.Sid,
                Src = _client.StubIp,
                SPort = _bindPort,
                Dst = msg.Src,
                DPort = msg.SPort
            });

            LinkSocket accepted = new LinkSocket(_client, _proto);
            accepted._streamId = msg.Sid.Value;
            accepted._bindPort = _bindPort;
            accepted.RemoteEndPoint = new IPEndPoint(LinkStub.ToIp(msg.Src.Value), msg.SPort ?? 0);
            return accepted;
        }

        public int Send(byte[] buffer, int offset, int size, SocketFlags flags)
        {
            IPEndPoint remote = RemoteEndPoint as IPEndPoint;
            if (remote == null) throw new InvalidOperationException("还没有 RemoteEndPoint");
            _client.Send(new LinkMsg
            {
                Cmd = (int)LinkCmd.DataSend,
                Proto = _proto,
                Sid = _streamId == -1 ? (int?)null : _streamId,
                Src = _client.StubIp,
                SPort = _bindPort,
                Dst = LinkStub.ToU32(remote.Address),
                DPort = remote.Port,
                Data = ToBase64(buffer, offset, size)
            });
            return size;
        }

        public int SendTo(byte[] buffer, int offset, int size, SocketFlags flags, EndPoint remoteEp)
        {
            // 上游也是直接屏蔽：广播（UDP 的 0.0.0.0）在公网隧道里没法原样还原，
            // 屏蔽掉之后游戏会走"没有广播"的分支，不影响联机。
            LinkLog.Error("SendTo 被屏蔽（广播不支持）");
            return 0;
        }

        public int Receive(byte[] buffer, int offset, int size, SocketFlags flags, out SocketError errorCode)
        {
            ConcurrentQueue<LinkMsg> q = _client.TcpRecvQ.Get(_streamId + _bindPort);
            LinkMsg msg;
            if (q == null || !q.TryDequeue(out msg) || string.IsNullOrEmpty(msg.Data))
            {
                errorCode = SocketError.WouldBlock;
                return 0;
            }
            byte[] data = Convert.FromBase64String(msg.Data);
            Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
            errorCode = SocketError.Success;
            return data.Length;
        }

        public int ReceiveFrom(byte[] buffer, SocketFlags flags, ref EndPoint remoteEp)
        {
            LinkLog.Error("ReceiveFrom 被屏蔽（广播不支持）");
            return 0;
        }

        public void Close()
        {
            if (_proto == LinkProto.Tcp)
            {
                // 上游这条消息不带流 ID，服务端只能干瞪眼（流表泄漏）；
                // 我们补上 sid/src/dst，我们的 instorematchd 会顺手把两边流表清掉。
                // 对官方服务端来说这条消息它本来也不处理，所以不影响兼容。
                LinkMsg msg = new LinkMsg { Cmd = (int)LinkCmd.CtlTcpClose, Proto = _proto };
                if (_streamId != -1)
                {
                    msg.Sid = _streamId;
                    msg.Src = _client.StubIp;
                    msg.SPort = _bindPort;
                    IPEndPoint remote = RemoteEndPoint as IPEndPoint;
                    if (remote != null)
                    {
                        msg.Dst = LinkStub.ToU32(remote.Address);
                        msg.DPort = remote.Port;
                    }
                    ConcurrentQueue<LinkMsg> ignored;
                    _client.TcpRecvQ.TryRemove(_streamId + _bindPort, out ignored);
                    Action<LinkMsg> ignoredCb;
                    _client.AcceptCallbacks.TryRemove(_streamId + _bindPort, out ignoredCb);
                }
                _client.Send(msg);
            }
        }

        public void Shutdown(SocketShutdown how)
        {
            Close();
        }

        private static string ToBase64(byte[] buffer, int offset, int size)
        {
            byte[] slice = new byte[size];
            Array.Copy(buffer, offset, slice, 0, size);
            return Convert.ToBase64String(slice);
        }
    }
}
