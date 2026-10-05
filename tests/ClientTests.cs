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
        TestCloseDropsQueuedAccept();

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
        c.AddAcceptPending(sid + port, p.Args);
        c.CompleteAccept(sid + port);

        Check(p.Calls == 1, "完成事件触发一次");
        Check(p.Error == SocketError.Success, "错误码是 Success");
        Check(!c.AcceptPending.ContainsKey(sid + port), "挂起记录已摘掉");
    }

    private static void TestAcceptThenLateTimeout()
    {
        Console.WriteLine("3) 接流成功后，超时器不许再触发第二次");
        LinkClient c = NewClient("W9CLIENT0002");
        int sid = 222, port = 60002;
        Probe p = new Probe();
        c.AddAcceptPending(sid + port, p.Args);
        c.CompleteAccept(sid + port);
        Thread.Sleep(900);                       // 远超上面设的 300ms
        Check(p.Calls == 1, "等了 3 倍超时时间，完成事件仍然只触发一次");
    }

    private static void TestTimeoutFires()
    {
        Console.WriteLine("4) 一直没人接流");
        LinkClient c = NewClient("W9CLIENT0003");
        int sid = 333, port = 60003;
        Probe p = new Probe();
        c.AddAcceptPending(sid + port, p.Args);
        Check(p.Calls == 0, "刚挂起时还没完成");
        Thread.Sleep(1200);
        Check(p.Calls == 1, "超时后完成事件触发（游戏因此不会一直卡在连接中）");
        Check(p.Error == SocketError.TimedOut, "错误码是 TimedOut");
        Check(!c.AcceptPending.ContainsKey(sid + port), "挂起记录已摘掉");
        Thread.Sleep(300);
        Check(p.Calls == 1, "超时只触发一次");
    }

    private static void TestCloseCancelsPending()
    {
        Console.WriteLine("5) 收到服务端 CTL_TCP_CLOSE（房主已经开打/房间关了）");
        LinkClient.AcceptTimeoutMs = 5000;       // 调长，确保是 CLOSE 触发的而不是超时
        LinkClient c = NewClient("W9CLIENT0004");
        int sid = 444, port = 60004;
        Probe p = new Probe();
        c.AddAcceptPending(sid + port, p.Args);

        c.HandleIncoming(new LinkMsg
        {
            Cmd = (int)LinkCmd.CtlTcpClose, Proto = LinkProto.Tcp, Sid = sid, DPort = port
        });

        Check(p.Calls == 1, "完成事件立刻触发（不用干等到超时）");
        Check(p.Error == SocketError.ConnectionRefused, "错误码是 ConnectionRefused");
        Check(!c.AcceptPending.ContainsKey(sid + port), "挂起记录已摘掉");
        LinkClient.AcceptTimeoutMs = 300;
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
