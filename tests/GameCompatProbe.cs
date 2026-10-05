// InStoreLink 游戏兼容性探针
//
// 不启动游戏，直接用反射检查：
//   1. 我们每个 Harmony 补丁要打的目标方法在不在（不在 = 补丁会静默失败）
//   2. Harmony 要注入的私有字段在不在、类型对不对（类型不对 = 注入失败，补丁等于没打）
//
// 用法（必须在游戏目录里跑，这样 .NET 才找得到 Unity / 游戏的那些 DLL）：
//   tests/run_probe.sh [游戏目录]
// 或者手动：把编好的 exe 拷到 <游戏>\Sinmai_Data\Managed\ 下执行（跑完删掉即可）。
//
// 游戏版本更新后先跑这个，红的地方就是需要重新看的地方。

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

public static class GameCompatProbe
{
    private static int _ok;
    private static int _bad;
    private static bool _dumpSignatures;

    public static int Main(string[] argv)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); }
        catch (Exception) { }
        _dumpSignatures = argv != null && argv.Any(a => a == "--signatures");

        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("探针自己崩了：" + ex.GetType().FullName);
            Console.WriteLine(ex.Message);
            if (ex.InnerException != null)
                Console.WriteLine("内层：" + ex.InnerException.GetType().FullName + " / " + ex.InnerException.Message);
            return 3;
        }
    }

    private static int Run()
    {
        Console.WriteLine("== InStoreLink 游戏兼容性探针 ==");
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Console.WriteLine("程序集目录：" + dir);
        Console.WriteLine();

        // 游戏程序集是 Unity/Mono 编的，直接 LoadFrom 会被 CLR 拒绝（DLL 初始化例程失败），
        // 所以用"只反射"模式加载：不执行任何代码，只读元数据。
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += delegate(object sender, ResolveEventArgs args)
        {
            string simple = new AssemblyName(args.Name).Name + ".dll";
            string candidate = Path.Combine(dir, simple);
            return File.Exists(candidate) ? Assembly.ReflectionOnlyLoadFrom(candidate) : null;
        };

        Assembly game = LoadGame("Assembly-CSharp.dll");
        Assembly amdaemon = LoadGame("AMDaemon.NET.dll");
        if (game == null)
        {
            Console.WriteLine("找不到 Assembly-CSharp.dll —— 请在 <游戏>\\Sinmai_Data\\Managed\\ 下运行本程序。");
            return 2;
        }

        // ---------------------------------------------------------------- 补丁目标
        Console.WriteLine("一、补丁目标（方法）");
        Type opMgr = T(game, "Manager.OperationManager");
        Type network = amdaemon != null ? T(amdaemon, "AMDaemon.Network") : null;
        Type commonMonitor = T(game, "Monitor.CommonMonitor");
        Type socketBase = T(game, "PartyLink.SocketBase");
        Type packet = T(game, "PartyLink.Packet");
        Type util = T(game, "PartyLink.Util");
        Type startup = T(game, "Process.StartupProcess");
        Type nfSocket = T(game, "PartyLink.NFSocket");
        Type partyClient = T(game, "Manager.Party.Party.Client");
        Type musicSelect = T(game, "Process.MusicSelectProcess");
        Type party = T(game, "Manager.Party.Party.Party");
        Type iManager = T(game, "Manager.Party.Party.IManager");
        Type delivery = T(game, "PartyLink.DeliveryChecker");
        Type setting = T(game, "PartyLink.Setting");
        Type advertise = T(game, "PartyLink.Advertise");

        Method(opMgr, "CheckAuth_Proc", "鉴权入口（联机从这里启动）");
        if (network != null) Method(network, "get_IsLanAvailable", "强制 IsLanAvailable=true");
        Method(commonMonitor, "ViewUpdate", "右下角状态 + 主线程分发招募");
        Method(socketBase, "sendClass", "开房/关房改走 HTTP");
        Method(socketBase, "error", "本体网络噪音日志");
        Method(packet, "isSameVersion", "版本检查强制通过");
        Method(util, "MyIpAddress", "本机地址 → 伪 IP");
        Method(startup, "OnUpdate", "跳过联网自检");
        Method(packet, "encrypt", "关闭加密");
        Method(packet, "decrypt", "关闭解密");
        Ctor(nfSocket, "NFSocket(AddressFamily,SocketType,ProtocolType,int)");
        Ctor(nfSocket, "NFSocket(Socket)");
        foreach (string m in new[] { "Poll", "Send", "SendTo", "Receive", "ReceiveFrom", "Bind",
                                     "Listen", "Accept", "ConnectAsync", "SetSocketOption",
                                     "Close", "Shutdown", "get_RemoteEndPoint", "get_LocalEndPoint" })
            Method(nfSocket, m, "影子 socket：" + m);
        Ctor(partyClient, "Client(string, InitParam)");
        Method(partyClient, "RecvStartRecruit", "收到招募（歌没装要拦掉）");
        Method(partyClient, "RecvFinishRecruit", "房间关掉");
        Method(musicSelect, "OnStart", "进选曲界面重置状态");
        Method(musicSelect, "PartyExec", "房间列表变化重画");
        Method(musicSelect, "get_RecruitData", "当前选中房间");
        Method(musicSelect, "set_RecruitData", "写入选中房间");
        Method(musicSelect, "IsConnectStart", "判断是否可以进房");
        Method(musicSelect, "SetConnectData", "重画联机歌曲列表");
        Method(musicSelect, "IsConnectionFolder", "判断是不是店内マッチング 分类");
        Method(musicSelect, "IsEntry", "哪个玩家在玩");
        Method(musicSelect, "get_IsConnectCategoryEnable", "联机分类开关（getter）");
        Method(musicSelect, "set_IsConnectCategoryEnable", "联机分类开关（setter）");
        Method(party, "Get", "拿 party 管理器");
        // 这几个方法挂在接口 / get() 的返回类型上（Party.Get() → IManager）
        Method(iManager, "GetRecruitList", "房间列表（含自己）");
        Method(iManager, "GetRecruitListWithoutMe", "房间列表（不含自己）");
        Method(iManager, "Start", "启动 party");
        MemberOfReturn(delivery, "送信检查器", "get", "start");
        MemberOfReturn(setting, "设置", "get", "setData");
        MemberOfReturn(setting, "设置", "get", "setRetryEnable");
        MemberOfReturn(advertise, "店外广播", "get", "initialize");

        // ---------------------------------------------------------------- 注入字段
        Console.WriteLine();
        Console.WriteLine("二、Harmony 注入的私有字段（类型必须完全一致）");
        Field(musicSelect, "_connectCombineMusicDataList", "List<CombineMusicSelectData>");
        Field(musicSelect, "_currentPlayerSubSequence", "SubSequence[]");
        Field(startup, "_state", "byte（枚举也行，底层类型必须是 byte）");
        Field(startup, "_statusMsg", "string[]");
        Field(startup, "_statusSubMsg", "string[]");
        Field(commonMonitor, "_buildVersionText", "TextMeshProUGUI");
        Field(commonMonitor, "_developmentBuildText", "GameObject");
        Field(packet, "_encrypt", "PacketType");
        Field(packet, "_plane", "PacketType");

        // ---------------------------------------------------------------- 反射句柄
        Console.WriteLine();
        Console.WriteLine("三、运行时要反射调用的东西");
        Type packetTypeEnum = game.GetType("PartyLink.PacketType");
        MethodInfo writeUint = null;
        if (packet != null && packetTypeEnum != null)
        {
            writeUint = packet.GetMethod("write_uint",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { packetTypeEnum, typeof(int), typeof(uint) }, null);
        }
        if (writeUint != null)
        {
            ParameterInfo[] ps = writeUint.GetParameters();
            string sig = string.Join(", ", ps.Select(p => p.ParameterType.Name).ToArray());
            Report(true, "Packet.write_uint(" + sig + ")");
        }
        else
        {
            Report(false, "Packet.write_uint（禁用加解密要用）");
        }

        Type combine = musicSelect == null ? null : musicSelect.GetNestedType("CombineMusicSelectData");
        Report(combine != null, "嵌套类型 MusicSelectProcess.CombineMusicSelectData");
        Type nestedSub = musicSelect == null ? null : musicSelect.GetNestedType("SubSequence");
        FieldInfo subField = musicSelect == null ? null
            : musicSelect.GetField("_currentPlayerSubSequence", BindingFlags.NonPublic | BindingFlags.Instance);
        if (subField != null && nestedSub != null)
        {
            Type elem = subField.FieldType.GetElementType();
            Report(elem == nestedSub,
                "SubSequence 用对了类型（字段元素类型 = " + (elem == null ? "null" : elem.FullName) + "）");
        }
        Type initParam = game.GetType("PartyLink.Party+InitParam") ?? game.GetType("PartyLink.Party.InitParam");
        Report(initParam != null, "PartyLink.Party.InitParam（构造补丁的参数类型）");

        if (_dumpSignatures)
        {
            Console.WriteLine();
            Console.WriteLine("四、我们打补丁的目标方法真实签名（用来对参数表）");
            DumpSignatures(new[]
            {
                opMgr, network, commonMonitor, socketBase, packet, util, startup, nfSocket,
                partyClient, musicSelect, party, iManager, delivery, setting, advertise,
            });
        }

        Console.WriteLine();
        Console.WriteLine(_bad == 0
            ? "全部通过（" + _ok + " 项）—— 补丁目标和注入字段都对得上"
            : "有 " + _bad + " 项对不上（通过 " + _ok + " 项）");
        return _bad == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 工具

    private static Assembly LoadGame(string file)
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string path = Path.Combine(dir, file);
        if (!File.Exists(path)) return null;
        try
        {
            return Assembly.ReflectionOnlyLoadFrom(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ! 加载 " + file + " 失败：" + ex.GetType().Name + " / " + ex.Message);
            return null;
        }
    }

    private static Type T(Assembly asm, string name)
    {
        Type t = null;
        try
        {
            if (asm != null) t = asm.GetType(name);
        }
        catch (Exception)
        {
            t = null;
        }
        if (t == null) Report(false, "类型 " + name);
        return t;
    }

    private static void Ctor(Type t, string label)
    {
        if (t == null) { Report(false, label); return; }
        ConstructorInfo[] list = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        bool found = list.Length > 0;      // 构造补丁按签名匹配，这里只确认"至少有一个构造"
        string detail = found
            ? label + "（" + list.Length + " 个构造：" +
              string.Join(" / ", list.Select(c => "(" + string.Join(",", c.GetParameters()
                  .Select(p => p.ParameterType.Name).ToArray()) + ")").ToArray()) + "）"
            : label;
        Report(found, detail);
    }

    private static void Method(Type t, string name, string why)
    {
        if (t == null) { Report(false, why + " → " + name); return; }
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static;
        MemberInfo[] found = t.GetMember(name, flags);
        Report(found.Length > 0, why + " → " + t.Name + "." + name +
               (found.Length > 0 ? "" : "  ← 不存在，这条补丁会挂不上"));
    }

    private static void Field(Type t, string name, string expected)
    {
        if (t == null) { Report(false, name); return; }
        FieldInfo f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null)
        {
            Report(false, t.Name + "." + name + " ← 不存在（期望 " + expected + "）");
            return;
        }
        string note = "";
        if (f.FieldType.IsEnum)
        {
            // 上游（和我们）用 ref byte 注入枚举字段 —— Mono 不做 IL 校验才跑得通，
            // 所以底层类型必须是 byte，别的就真会炸。
            FieldInfo value = f.FieldType.GetField("value__");
            Type underlying = value == null ? null : value.FieldType;
            bool ok = underlying == typeof(byte);
            note = "（枚举，底层类型 " + (underlying == null ? "?" : underlying.Name) + "）";
            Report(ok, t.Name + "." + name + " = " + Pretty(f.FieldType) + note);
            return;
        }
        Report(true, t.Name + "." + name + " = " + Pretty(f.FieldType) + note);
    }

    /// <summary>检查 get() 的返回类型上有没有这个方法（DeliveryChecker.get().start(...) 这种）。</summary>
    private static void MemberOfReturn(Type t, string label, string getter, string member)
    {
        if (t == null) { Report(false, label); return; }
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static;
        MethodInfo g = t.GetMethod(getter, flags);
        if (g == null)
        {
            Report(false, label + "." + getter + " ← 不存在");
            return;
        }
        Type rt = g.ReturnType;
        Report(rt.GetMember(member, flags).Length > 0,
               label + "." + getter + "()." + member + "（返回类型 " + Pretty(rt) + "）");
    }

    private static string Pretty(Type t)
    {
        if (t.IsArray) return Pretty(t.GetElementType()) + "[]";
        if (t.IsByRef) return "ref " + Pretty(t.GetElementType());
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>))
            return "List<" + Pretty(t.GetGenericArguments()[0]) + ">";
        return t.FullName;
    }

    /// <summary>把目标类型的所有方法签名打出来，方便和我们的补丁参数表逐一对照。</summary>
    private static void DumpSignatures(Type[] types)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;
        foreach (Type t in types)
        {
            if (t == null) continue;
            Console.WriteLine();
            Console.WriteLine("-- " + t.FullName);
            foreach (MethodBase m in t.GetMethods(flags).Cast<MethodBase>()
                         .Concat(t.GetConstructors(flags)).OrderBy(x => x.Name))
            {
                string args = string.Join(", ", m.GetParameters()
                    .Select(p => Pretty(p.ParameterType) + " " + p.Name).ToArray());
                string ret = m is MethodInfo ? " : " + Pretty(((MethodInfo)m).ReturnType) : " : ctor";
                Console.WriteLine("   " + m.Name + "(" + args + ")" + ret);
            }
        }
    }

    private static void Report(bool ok, string what)
    {
        if (ok) _ok++; else _bad++;
        Console.WriteLine("  " + (ok ? "✓" : "✗") + " " + what);
    }
}
