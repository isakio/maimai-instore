// InStoreLink 协议层单测（不依赖 Unity / 游戏 / MelonLoader，能单独编成 exe 跑）
//
// 覆盖：
//   1. 线协议编解码：和上游 WorldLink 逐字节一致（含尾部逗号裁剪、数据段在 16 号位）
//   2. 用真实日志里出现过的报文往返（这些值来自我们抓到的联机日志）
//   3. 伪 IP 算法：md5(keychip) 前 4 字节大端（用日志里的真实对应关系当向量）
//   4. 配置解析：TOML 三个键 + RelayUrl 四种写法
//
// 编译运行：tests/run_all.sh（用 Windows 自带的 csc.exe，产物在 build/）

using System;
using System.Collections.Generic;
using InStoreLink;

public static class ProtocolTests
{
    private static int _pass;
    private static int _fail;

    public static int Main()
    {
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); }
        catch (Exception) { /* 控制台不支持就随它去 */ }
        Console.WriteLine("== InStoreLink 协议层单测 ==");

        TestSerialize();
        TestRoundTrip();
        TestRealVectors();
        TestStubIp();
        TestConfig();

        Console.WriteLine();
        Console.WriteLine(_fail == 0
            ? "全部通过（" + _pass + " 项）"
            : "失败 " + _fail + " 项（通过 " + _pass + " 项）");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ 1. 序列化

    private static void TestSerialize()
    {
        Console.WriteLine("1) 序列化");

        LinkMsg heartbeat = new LinkMsg();
        heartbeat.Cmd = (int)LinkCmd.CtlHeartbeat;
        Eq("1,3", heartbeat.ToString(), "心跳只有两个字段");

        LinkMsg connect = new LinkMsg();
        connect.Cmd = (int)LinkCmd.CtlTcpConnect;
        connect.Proto = LinkProto.Tcp;
        connect.Sid = 424242;
        connect.Src = 4078886013u;
        connect.SPort = 5000;
        connect.Dst = 3870867194u;
        connect.DPort = 50100;
        Eq("1,4,6,424242,4078886013,5000,3870867194,50100",
            connect.ToString(), "建流消息（无数据时尾部逗号被裁掉）");

        LinkMsg data = connect;
        data.Cmd = (int)LinkCmd.DataSend;
        data.Data = "aGVsbG8=";
        Eq("1,21,6,424242,4078886013,5000,3870867194,50100,,,,,,,,,aGVsbG8=",
            data.ToString(), "数据消息：8 个预留空字段 + 下标 16 的数据");

        LinkMsg register = new LinkMsg();
        register.Cmd = (int)LinkCmd.CtlStart;
        register.Data = "W9367886794";
        Eq("1,1,,,,,,,,,,,,,,,W9367886794", register.ToString(), "注册消息：keychip 放在数据位");
    }

    // ------------------------------------------------------------ 2. 往返

    private static void TestRoundTrip()
    {
        Console.WriteLine("2) 解析→序列化 往返");
        string[] lines = new string[]
        {
            "1,3",
            "1,1,,,,,,,,,,,,,,,W9367886794",
            "1,4,6,424242,4078886013,5000,3870867194,50100",
            "1,5,6,424242,3870867194,50100,4078886013,5000",
            "1,21,6,424242,4078886013,5000,3870867194,50100,,,,,,,,,aGVsbG8=",
            "1,7,6,424242,4078886013,5000,3870867194,50100",
        };
        foreach (string line in lines)
        {
            LinkMsg msg = LinkMsg.Parse(line);
            Eq(line, msg.ToString(), "往返：" + Shorten(line));
        }

        // 解析出来的字段值也要对
        LinkMsg parsed = LinkMsg.Parse("1,21,6,424242,4078886013,5000,3870867194,50100,,,,,,,,,aGVsbG8=");
        Eq((int)LinkCmd.DataSend, parsed.Cmd, "cmd 解析");
        Eq(LinkProto.Tcp, parsed.Proto.Value, "proto 解析");
        Eq(424242, parsed.Sid.Value, "sid 解析");
        Eq(4078886013u, parsed.Src.Value, "src（uint，超过 int 范围也不能被截断）");
        Eq(50100, parsed.DPort.Value, "dport 解析");
        Eq("aGVsbG8=", parsed.Data, "data 解析");

        // 可读形式（日志里就是这种）
        Eq("DATA_SEND | Tcp | Stream: 424242 | Src: 243.30.220.125:5000 | Dst: 230.184.190.250:50100 | hello",
            parsed.Readable(), "Readable() 和上游日志格式一致");
    }

    // ------------------------------------------------------------ 3. 真实报文

    private static void TestRealVectors()
    {
        Console.WriteLine("3) 真实日志里的报文（2026-10-03 联机抓的）");
        // 这两条对应日志里的：
        //   243.30.220.125 <<< DATA_SEND | Tcp | Stream: 421409813 | Src: 243.30.220.125:57497 | ...
        //   230.184.190.250 >>> CTL_START | W9751871074
        LinkMsg send = LinkMsg.Parse("1,21,6,421409813,4078886013,57497,4078886013,50100,,,,,,,,,e30=");
        Eq("DATA_SEND | Tcp | Stream: 421409813 | Src: 243.30.220.125:57497 | Dst: 243.30.220.125:50100 | {}",
            send.Readable(), "日志里的 DATA_SEND 能被我们解出来");
        Eq("243.30.220.125", LinkStub.ToIp(send.Src.Value).ToString(), "同一条报文的伪 IP");

        LinkMsg hello = LinkMsg.Parse("1,1,,,,,,,,,,,,,,,W9751871074");
        Eq("CTL_START | W9751871074", hello.Readable(), "日志里的注册报文");
    }

    // ------------------------------------------------------------ 4. 伪 IP

    private static void TestStubIp()
    {
        Console.WriteLine("4) 伪 IP = md5(keychip) 前 4 字节（大端）");
        CheckStub("W9367886794", 4078886013u, "243.30.220.125");
        CheckStub("W9751871074", 3870867194u, "230.184.190.250");
        Eq(4078886013u, LinkStub.ToU32(LinkStub.ToIp(4078886013u)), "uint → IP → uint 还原");

        // 服务端（instorematchd / worldlinkd）用的是同一套算法，这里再跟 Python 的 md5 结果对一次
        Eq(LinkStub.FromKeychip("W1111111111"), LinkStub.FromKeychip("W1111111111"), "同输入同输出");
        CheckTrue(LinkStub.FromKeychip("a") != LinkStub.FromKeychip("b"), "不同 keychip 得到不同伪 IP");
    }

    private static void CheckStub(string keychip, uint expected, string expectedIp)
    {
        uint got = LinkStub.FromKeychip(keychip);
        Eq(expected, got, keychip + " → uint32");
        Eq(expectedIp, LinkStub.ToIp(got).ToString(), keychip + " → 点分十进制");
    }

    // ------------------------------------------------------------ 5. 配置

    private static void TestConfig()
    {
        Console.WriteLine("5) 配置");
        string host;
        int port;

        CheckTrue(LinkConfig.TryParseRelay("isakio.cn:20101", out host, out port) &&
                  host == "isakio.cn" && port == 20101, "RelayUrl = host:port");
        CheckTrue(LinkConfig.TryParseRelay("http://isakio.cn:20101", out host, out port) &&
                  host == "isakio.cn" && port == 20101, "RelayUrl = http://host:port");
        CheckTrue(LinkConfig.TryParseRelay("isakio.cn", out host, out port) &&
                  host == "isakio.cn" && port == 20101, "RelayUrl = host（默认端口 20101）");
        CheckTrue(LinkConfig.TryParseRelay("", out host, out port) == false, "空 RelayUrl → false（走大厅 /info）");

        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "instorelink-cfg-test.toml");
        System.IO.File.WriteAllText(tmp,
            "# 注释\n" +
            "LobbyUrl=\"http://isakio.cn:20100\"   # 行内注释\n" +
            "RelayUrl = \"isakio.cn:20102\"\n" +
            "Debug=true\n");
        string note;
        LinkConfig cfg = LinkConfig.Load(tmp, out note);
        Eq("http://isakio.cn:20100", cfg.LobbyUrl, "LobbyUrl 解析（带行内注释）");
        Eq("isakio.cn:20102", cfg.RelayUrl, "RelayUrl 解析");
        Eq(true, cfg.Debug, "Debug 解析");
        System.IO.File.Delete(tmp);

        LinkConfig missing = LinkConfig.Load("/definitely/not/here.toml", out note);
        Eq(LinkConfig.DefaultLobbyUrl, missing.LobbyUrl, "文件不存在时用默认大厅");
        CheckTrue(!string.IsNullOrEmpty(note), "文件不存在时有提示");
    }

    // ------------------------------------------------------------ 断言

    private static void Eq(object expected, object actual, string what)
    {
        bool ok = Equals(expected, actual);
        if (ok) _pass++; else _fail++;
        Console.WriteLine("  " + (ok ? "✓" : "✗") + " " + what +
                          (ok ? "" : "：期望 [" + Fmt(expected) + "] 实际 [" + Fmt(actual) + "]"));
    }

    private static void CheckTrue(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine("  " + (ok ? "✓" : "✗") + " " + what);
    }

    private static string Fmt(object o)
    {
        return o == null ? "null" : o.ToString();
    }

    private static string Shorten(string s)
    {
        return s.Length <= 40 ? s : s.Substring(0, 37) + "...";
    }
}
