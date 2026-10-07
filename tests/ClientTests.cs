// InStoreLink 客户端逻辑单测（脱离游戏跑）
//
// 为什么单列这一份：协议层有 ProtocolTests.cs，服务端有 tests/py/test_e2e.py 和
// test_edge.py，但"建流挂起 → 等接流 → 超时 / 收到 CLOSE"这段逻辑以前只能靠
// "能编过"来保证 —— 而它恰好是 v0.2 改动最大的地方。
//
// 这几个文件（LinkProtocol / LinkConfig / LinkLog / LinkClient / LinkSocket）只依赖
// System.*，所以能单独编成 exe 跑。LinkLog 要的 MelonLogger 在下面给了一个壳；
// 因此**不要**引用 MelonLoader.dll，否则会和壳撞名。
//
// 编译运行：tests/run_all.sh（用 Windows 自带的 csc.exe，产物在 build/）

using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using InStoreLink;

namespace MelonLoader
{
    /// <summary>LinkLog 的假实现：单测里把日志吞掉（不然要拖进整个 MelonLoader）。</summary>
    public static class MelonLogger
    {
        public static void Msg(string msg) { }
        public static void Warning(string msg) { }
        public static void Error(string msg) { }
    }
}

public static class ClientTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ✓ " : "  ✗ ") + what);
        if (ok) _pass++;
        else _fail++;
    }

    /// <summary>记录"完成事件触发了几次、带什么 SocketError"。</summary>
    private class Probe
    {
        public int Calls;
        public SocketError Error;
        public readonly SocketAsyncEventArgs Args = new SocketAsyncEventArgs();

        public Probe()
        {
            Args.Completed += delegate(object sender, SocketAsyncEventArgs e)
            {
                Calls++;
                Error = e.SocketError;
            };
        }
    }

    private static LinkClient NewClient(string keychip)
    {
        // 只构造、不联网：ConnectAsync 才会真的去连
        return new LinkClient(keychip, "127.0.0.1", 1);
    }

    /// <summary>造一条"等对方接流"的挂起记录，返回它的 key（流ID + 本地端口）。</summary>
    private static int AddPending(LinkClient c, int sid, int port, Probe p)
    {
        PendingAccept pa = new PendingAccept();
        pa.Args = p.Args;
        pa.Proto = LinkProto.Tcp;
        pa.Sid = sid;
        pa.SPort = port;
        pa.Dst = 0x7F000001u;      // 单测不发真包，路由字段只要求不为空
        pa.DPort = 50100;
        int key = sid + port;
        c.AddAcceptPending(key, pa);
        return key;
    }

    public static int Main()
    {
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); }
        catch (Exception) { /* 控制台不支持就随它去 */ }
        Console.WriteLine("== InStoreLink 客户端逻辑单测（建流超时 / CLOSE 处理）==");

        TestDefaultTimeout();
        TestAcceptSuccess();
        TestAcceptThenLateTimeout();
        TestTimeoutFires();
        TestCloseCancelsPending();
        TestJoinFailureWindow();
        TestCloseDropsQueuedAccept();
        TestStreamKeysUnique();
        TestBindQueuesReleasedOnClose();
        TestBadBase64IsDropped();
        TestStrayOpsAfterClose();
        TestSendQueueCap();

        Console.WriteLine();
        Console.WriteLine("通过 " + _pass + " 项，失败 " + _fail + " 项");
        return _fail == 0 ? 0 : 1;
    }

    private static void TestDefaultTimeout()
    {
        Console.WriteLine("1) 默认值");
        Check(LinkClient.AcceptTimeoutMs == 8000, "建流超时默认 8000ms（文档里写的就是这个）");
        // 后面几条把它调短，省得真等 8 秒
        LinkClient.AcceptTimeoutMs = 300;
    }

    private static void TestAcceptSuccess()
    {
        Console.WriteLine("2) 对方接流");
        LinkClient c = NewClient("W9CLIENT0001");
        int sid = 111, port = 60001;
        Probe p = new Probe();
        int key = AddPending(c, sid, port, p);
        c.CompleteAccept(key);

        Check(p.Calls == 1, "完成事件触发一次");
        Check(p.Error == SocketError.Success, "错误码是 Success");
        Check(!c.AcceptPending.ContainsKey(key), "挂起记录已摘掉");
    }

    private static void TestAcceptThenLateTimeout()
    {
        Console.WriteLine("3) 接流成功：完成事件只来一次，且不留失败条子");
        LinkClient c = NewClient("W9CLIENT0002");
        int sid = 222, port = 60002;
        Probe p = new Probe();
        int key = AddPending(c, sid, port, p);
        c.CompleteAccept(key);
        Thread.Sleep(900);                       // 远超上面设的 300ms
        Check(p.Calls == 1, "等了 3 倍超时时间，完成事件仍然只触发一次");
        Check(LinkClient.TakeJoinFailure(key) == null, "成功路径不会留下失败条子");
    }

    private static void TestTimeoutFires()
    {
        Console.WriteLine("4) 一直没人接流：交给游戏自己的错误路径，而不是假装连上");
        LinkClient c = NewClient("W9CLIENT0003");
        int sid = 333, port = 60003;
        Probe p = new Probe();
        int key = AddPending(c, sid, port, p);
        Check(p.Calls == 0, "刚挂起时还没完成");
        Thread.Sleep(1200);
        // 关键：**不能**触发 Completed —— 本体只看"完成事件来过"就当连上了
        Check(p.Calls == 0, "超时不会触发 Completed（否则游戏会误判成连上）");
        Check(!c.AcceptPending.ContainsKey(key), "挂起记录已摘掉");
        string why = LinkClient.TakeJoinFailure(key);
        Check(why != null && why.Contains("超时"), "留下了失败条子交给主线程：" + why);
        Check(LinkClient.TakeJoinFailure(key) == null, "条子取一次就没了（不会重复报）");
        // 这条是回归测试：以前是一张全局条子，旧那次的超时会把新一次连接掐掉
        Check(LinkClient.TakeJoinFailure(key + 1) == null,
              "别的流取不到这条失败（失败是按流绑定的）");
    }

    private static void TestCloseCancelsPending()
    {
        Console.WriteLine("5) 收到服务端 CTL_TCP_CLOSE（房主已经开打/房间关了）");
        LinkClient.AcceptTimeoutMs = 5000;       // 调长，确保是 CLOSE 触发的而不是超时
        LinkClient c = NewClient("W9CLIENT0004");
        int sid = 444, port = 60004;
        Probe p = new Probe();
        int key = AddPending(c, sid, port, p);

        c.HandleIncoming(new LinkMsg
        {
            Cmd = (int)LinkCmd.CtlTcpClose, Proto = LinkProto.Tcp, Sid = sid, DPort = port
        });

        Check(p.Calls == 0, "同样不触发 Completed（不假装连上）");
        Check(!c.AcceptPending.ContainsKey(key), "挂起记录已摘掉");
        string why = LinkClient.TakeJoinFailure(key);
        Check(why != null && why.Contains("取消"), "立刻留下失败条子（不用干等到超时）：" + why);
        LinkClient.AcceptTimeoutMs = 300;
    }

    private static void TestJoinFailureWindow()
    {
        Console.WriteLine("7) 失败条子有保质期（避免记到下一次连接头上）");
        Check(LinkClient.JoinFailureWindowMs == 3000, "保质期 3000ms");
        int key = 9001;
        LinkClient.ReportJoinFailure(key, "很久以前的失败");
        Thread.Sleep(3200);
        Check(LinkClient.TakeJoinFailure(key) == null, "超过保质期就丢掉");
    }

    /// <summary>
    /// 同一轮里连开很多条流，key（流ID+本地端口）必须**互不相同**。
    ///
    /// 以前 ConnectAsync 每次 `new Random()`，用的是同一个时间种子 —— 同一 tick 内连着
    /// 建两条流会拿到完全一样的 _streamId + _bindPort，后一条把前一条在 TcpRecvQ /
    /// AcceptPending 里的记录覆盖掉（两条流串号，数据发到错的房间）。这条用例卡住它。
    /// </summary>
    private static void TestStreamKeysUnique()
    {
        Console.WriteLine("8) 连开 200 条流：流 key 不能重复（共享 Random + 锁）");
        LinkClient c = NewClient("W9CLIENT0006");
        LinkClient.Instance = c;      // LinkSocket 的构造函数走 LinkClient.Instance
        System.Collections.Generic.HashSet<int> keys = new System.Collections.Generic.HashSet<int>();
        int dup = 0;
        for (int i = 0; i < 200; i++)
        {
            LinkSocket s = new LinkSocket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp, 0);
            SocketAsyncEventArgs args = new SocketAsyncEventArgs();
            args.RemoteEndPoint = new System.Net.IPEndPoint(
                new System.Net.IPAddress(new byte[] { 203, 0, 113, 9 }), 50100);
            if (!s.ConnectAsync(args, 0)) continue;
            if (!keys.Add(s.StreamKey)) dup++;
        }
        Check(keys.Count == 200 && dup == 0,
              "200 条流的 StreamKey 全不同（拿到 " + keys.Count + " 个，重复 " + dup + " 个）");
    }

    /// <summary>
    /// 监听用的影子 socket 关掉时，它在 client 里注册的端口队列要一起摘掉。
    /// 这几张表（AcceptQ / UdpRecvQ）以前**只增不减**：本体每开一次联机会话就 Bind 一个
    /// 新端口，一局下来留一堆没人用的队列。
    /// </summary>
    private static void TestBindQueuesReleasedOnClose()
    {
        Console.WriteLine("9) 关掉监听 socket 时，端口队列要一起清掉");
        LinkClient c = NewClient("W9CLIENT0007");
        LinkClient.Instance = c;
        LinkSocket udp = new LinkSocket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp, 0);
        udp.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 60011));
        Check(c.UdpRecvQ.ContainsKey(60011), "Bind 之后 UdpRecvQ 里有这个端口");
        udp.Close();
        Check(!c.UdpRecvQ.ContainsKey(60011), "Close 之后 UdpRecvQ 里的端口被摘掉");

        LinkSocket tcp = new LinkSocket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp, 0);
        tcp.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 60012));
        Check(c.AcceptQ.ContainsKey(60012), "Bind 之后 AcceptQ 里有这个端口");

        // ★ 本体在**同一个端口号**上会同时 Bind Udp 和 Tcp（真机日志：50100 上有 Udp×2 + Tcp×1）。
        //   关掉那条 UDP 不能把 TCP 监听器的 AcceptQ 一起删掉 —— 删了别人就进不来
        //   （玩家侧表现："一进去就被拒绝/超时"，而且只有两边都是真客户端时才现）。
        LinkSocket udpSame = new LinkSocket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp, 0);
        udpSame.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 60012));
        Check(c.UdpRecvQ.ContainsKey(60012), "同一端口再 Bind 一条 UDP：UdpRecvQ 里有它");
        udpSame.Close();
        Check(!c.UdpRecvQ.ContainsKey(60012), "关掉那条 UDP：UdpRecvQ 里的端口被摘掉");
        Check(c.AcceptQ.ContainsKey(60012), "关掉那条 UDP **不影响** TCP 的 AcceptQ");

        tcp.Close();
        Check(!c.AcceptQ.ContainsKey(60012), "Close 之后 AcceptQ 里的端口被摘掉");
    }

    /// <summary>
    /// 对端发来的 data 段 base64 不合法时，Receive 必须**丢掉那条继续**，不能把
    /// FormatException 抛到游戏主线程（同房的人就能让本机崩），也不能一直卡在同一条上。
    /// </summary>
    private static void TestBadBase64IsDropped()
    {
        Console.WriteLine("10) 坏 base64 包：丢掉它，后面的好包继续收");
        LinkClient c = NewClient("W9CLIENT0008");
        LinkClient.Instance = c;
        LinkSocket s = new LinkSocket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp, 0);
        SocketAsyncEventArgs args = new SocketAsyncEventArgs();
        args.RemoteEndPoint = new System.Net.IPEndPoint(
            new System.Net.IPAddress(new byte[] { 203, 0, 113, 9 }), 50100);
        s.ConnectAsync(args, 0);
        int key = s.StreamKey;
        System.Collections.Concurrent.ConcurrentQueue<LinkMsg> q =
            c.TcpRecvQ.Get(key);
        q.Enqueue(new LinkMsg { Cmd = (int)LinkCmd.DataSend, Data = "!!!not-base64!!!" });
        q.Enqueue(new LinkMsg { Cmd = (int)LinkCmd.DataSend, Data = "aGVsbG8=" });   // "hello"

        byte[] buf = new byte[32];
        SocketError err;
        int n = 0;
        bool threw = false;
        try { n = s.Receive(buf, 0, buf.Length, SocketFlags.None, out err); }
        catch (Exception) { threw = true; }
        Check(!threw, "坏包不会抛异常出来");
        Check(n == 5 && System.Text.Encoding.ASCII.GetString(buf, 0, 5) == "hello",
              "坏包被丢掉、后面的好包正常收到（收到 " + n + " 字节）");
    }

    /// <summary>
    /// Close 之后如果还有残留的 Send/Receive（正常生命周期不会，但游戏状态机出错时可能），
    /// 影子 socket 自己不能抛异常 —— 这正是我们"Close 时摘掉映射但不关本体真 socket"
    /// 那个取舍要保住的不变量。
    /// </summary>
    private static void TestStrayOpsAfterClose()
    {
        Console.WriteLine("11) Close 之后再 Receive/Send 不能抛异常");
        LinkClient c = NewClient("W9CLIENT0009");
        LinkClient.Instance = c;
        LinkSocket s = new LinkSocket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp, 0);
        SocketAsyncEventArgs args = new SocketAsyncEventArgs();
        args.RemoteEndPoint = new System.Net.IPEndPoint(
            new System.Net.IPAddress(new byte[] { 203, 0, 113, 9 }), 50100);
        s.ConnectAsync(args, 0);
        s.Close();
        bool threw = false;
        try
        {
            byte[] buf = new byte[8];
            SocketError err;
            s.Receive(buf, 0, buf.Length, SocketFlags.None, out err);
            s.Send(buf, 0, 0, SocketFlags.None);
        }
        catch (Exception ex) { threw = true; Console.WriteLine("      " + ex.GetType().Name); }
        Check(!threw, "Close 之后的收发不会抛异常出来");
    }

    /// <summary>
    /// 发送队列必须有上限：中继堵死时 Send() 会被游戏主线程高频调用，无界队列会把
    /// 内存涨爆（旧实现就是无界 ConcurrentQueue）。到上限就判定"对端不可用"、断开这条
    /// 连接，把多出来的报文丢掉并计数 —— 而不是无限入队，也不是继续丢包：本体的包带
    /// 序列/确认，丢包会让上层协议错位，比断开更糟。
    /// </summary>
    private static void TestSendQueueCap()
    {
        Console.WriteLine("12) SendQ 有上限：超上限判定对端不可用、断开重连，不再无界增长");
        int saved = LinkClient.SendQueueMaxDepth;
        LinkClient.SendQueueMaxDepth = 16;
        LinkClient c = NewClient("W9CLIENT0010");

        // 正常路径：几条包远不到上限，一个都不能丢、也不该触发断开
        for (int i = 0; i < 5; i++)
            c.Send(new LinkMsg
            {
                Cmd = (int)LinkCmd.DataBroadcast, Proto = LinkProto.Udp,
                DPort = 50100, Data = "ok" + i
            });
        Check(c.SendQ.Count == 5, "正常量（5 条）全部入队，没丢（队列 " + c.SendQ.Count + "）");
        Check(c.SendDropped == 0 && !c.SendOverflowed, "正常路径不触发丢弃 / 断开");

        // 塞爆：一路发到远超上限
        for (int i = 0; i < 100; i++)
            c.Send(new LinkMsg
            {
                Cmd = (int)LinkCmd.DataBroadcast, Proto = LinkProto.Udp,
                DPort = 50100, Data = "flood" + i
            });

        Check(c.SendQ.Count <= 16, "队列深度被卡在上限内（实际 " + c.SendQ.Count + "）");
        Check(c.SendDropped > 0, "超出上限的报文被丢弃并计数（丢了 " + c.SendDropped + " 条）");
        Check(c.SendOverflowed, "已判定对端不可用、触发断开重连");

        LinkClient.SendQueueMaxDepth = saved;
    }

    private static void TestCloseDropsQueuedAccept()
    {
        Console.WriteLine("6) 收到 CLOSE 时，把我这边排队等 Accept 的那条请求丢掉");
        LinkClient c = NewClient("W9CLIENT0005");
        int listenPort = 60010;
        c.Bind(listenPort, LinkProto.Tcp);

        // 两条别人发来的建流请求
        c.HandleIncoming(new LinkMsg
        {
            Cmd = (int)LinkCmd.CtlTcpConnect, Proto = LinkProto.Tcp,
            Sid = 555, Src = 111u, SPort = 60011, DPort = listenPort
        });
        c.HandleIncoming(new LinkMsg
        {
            Cmd = (int)LinkCmd.CtlTcpConnect, Proto = LinkProto.Tcp,
            Sid = 666, Src = 222u, SPort = 60012, DPort = listenPort
        });
        Check(c.AcceptQ[listenPort].Count == 2, "队列里有 2 条待接受");

        // 其中一条的发起方放弃了（服务端转发的 CLOSE）
        c.HandleIncoming(new LinkMsg
        {
            Cmd = (int)LinkCmd.CtlTcpClose, Proto = LinkProto.Tcp, Sid = 555, DPort = listenPort
        });

        ConcurrentQueue<LinkMsg> q = c.AcceptQ[listenPort];
        Check(q.Count == 1, "那条作废的请求被丢掉（剩 " + q.Count + " 条）");
        LinkMsg left;
        bool ok = q.TryPeek(out left);
        Check(ok && left.Sid == 666, "留下的是另一条（sid 666），没误删");
    }
}
