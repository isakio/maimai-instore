// InStoreLink —— maimai DX 店内联机客户端 mod（WorldLink / NyanLink 的重写版）
//
// 本文件：协议层。和上游 WorldLink 逐字节兼容，不依赖 Unity / 游戏本体，
//         可以单独抽出来编译做单元测试（见 tests/ProtocolTests.cs）。
//
// 线协议回顾（一条消息 = 一行 UTF-8 文本，字段用逗号分隔）：
//   0=固定 1   1=命令   2=协议(6=TCP/17=UDP)   3=流ID   4=源伪IP   5=源端口
//   6=目标伪IP 7=目标端口 8..15=预留给未来（8 个空字段）  16+=数据(base64)
//   序列化时尾部空字段裁掉（TrimEnd(',')），所以心跳就是一行 "1,3"
//
// 上游：https://github.com/MuNET-OSS/NyanLink （MIT, Copyright (c) 2025 Azalea）
// 本重写版同样是 MIT，逻辑与上游保持兼容，实现与注释是我们自己的。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace InStoreLink
{
    /// <summary>中继命令号，数值必须和上游一致。</summary>
    public enum LinkCmd
    {
        // 控制面
        CtlStart = 1,          // 注册，data 里带 keychip
        CtlBind = 2,           // 上游保留、当前没用到
        CtlHeartbeat = 3,      // 心跳，服务端原样回包
        CtlTcpConnect = 4,     // 请求建流（多路复用的 TCP）
        CtlTcpAccept = 5,      // 接受建流
        CtlTcpAcceptAck = 6,   // 上游服务端有常量，客户端没用
        CtlTcpClose = 7,       // 关流

        // 数据面
        DataSend = 21,         // 发数据（payload 是 base64）
        DataBroadcast = 22,    // 广播（只用于 UDP，客户端侧目前被屏蔽）
    }

    public static class LinkProto
    {
        public const int Tcp = 6;          // ProtocolType.Tcp
        public const int Udp = 17;         // ProtocolType.Udp
        public const int Version = 1;      // 注册成功后服务端回的 version
        public const int DataIndex = 16;   // 数据字段的固定下标
        public const int FieldCount = 17;  // 一条消息的字段总数（含下标 0 的固定 1）

        /// <summary>
        /// 命令号的日志名。刻意保持上游那套大写写法，这样我们的日志和上游日志能直接对比，
        /// 文档里引用的抓包也能对得上。
        /// </summary>
        public static string CmdName(int cmd)
        {
            switch (cmd)
            {
                case 1: return "CTL_START";
                case 2: return "CTL_BIND";
                case 3: return "CTL_HEARTBEAT";
                case 4: return "CTL_TCP_CONNECT";
                case 5: return "CTL_TCP_ACCEPT";
                case 6: return "CTL_TCP_ACCEPT_ACK";
                case 7: return "CTL_TCP_CLOSE";
                case 21: return "DATA_SEND";
                case 22: return "DATA_BROADCAST";
                default: return "CMD_" + cmd.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// 一条中继消息。字段名沿用上游（src/dst 是"伪 IP"的 uint32 形式）。
    /// </summary>
    public struct LinkMsg
    {
        public int Cmd;
        public int? Proto;
        public int? Sid;
        public uint? Src;
        public int? SPort;
        public uint? Dst;
        public int? DPort;
        public string Data;

        public LinkCmd Command
        {
            get { return (LinkCmd)Cmd; }
        }

        /// <summary>序列化成一行文本（和上游 Msg.ToString 完全一致）。</summary>
        public override string ToString()
        {
            string[] f = new string[LinkProto.FieldCount];
            f[0] = "1";
            f[1] = Cmd.ToString(CultureInfo.InvariantCulture);
            f[2] = Num(Proto);
            f[3] = Num(Sid);
            f[4] = Num(Src);
            f[5] = Num(SPort);
            f[6] = Num(Dst);
            f[7] = Num(DPort);
            for (int i = 8; i < LinkProto.DataIndex; i++) f[i] = "";
            f[LinkProto.DataIndex] = Data ?? "";
            return string.Join(",", f).TrimEnd(',');
        }

        /// <summary>解析一行文本（字段缺失/为空一律当 null）。</summary>
        public static LinkMsg Parse(string line)
        {
            string[] f = (line ?? "").Split(',');
            LinkMsg m = new LinkMsg();
            m.Cmd = IntAt(f, 1) ?? 0;
            m.Proto = IntAt(f, 2);
            m.Sid = IntAt(f, 3);
            m.Src = UIntAt(f, 4);
            m.SPort = IntAt(f, 5);
            m.Dst = UIntAt(f, 6);
            m.DPort = IntAt(f, 7);
            // 数据段：把下标 16 之后的字段重新拼回来（base64 里不会出现逗号，
            // 但上游就是这么拼的，保持一致）
            if (f.Length > LinkProto.DataIndex)
            {
                m.Data = string.Join(",", f, LinkProto.DataIndex, f.Length - LinkProto.DataIndex);
            }
            else
            {
                m.Data = "";
            }
            return m;
        }

        /// <summary>给日志看的可读形式，例如 "CTL_TCP_CONNECT | Tcp | Stream: 123 | Src: 1.2.3.4:5000 | Dst: ..."</summary>
        public string Readable()
        {
            List<string> parts = new List<string>();
            parts.Add(LinkProto.CmdName(Cmd));
            if (Proto.HasValue) parts.Add(ProtocolName(Proto.Value));
            if (Sid.HasValue) parts.Add("Stream: " + Sid.Value.ToString(CultureInfo.InvariantCulture));
            if (Src.HasValue)
                parts.Add("Src: " + LinkStub.ToIp(Src.Value) + ":" + (SPort ?? 0).ToString(CultureInfo.InvariantCulture));
            if (Dst.HasValue)
                parts.Add("Dst: " + LinkStub.ToIp(Dst.Value) + ":" + (DPort ?? 0).ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(Data))
            {
                string text = null;
                try { text = Encoding.UTF8.GetString(Convert.FromBase64String(Data)); }
                catch (FormatException) { }
                parts.Add(text ?? Data);
            }
            return string.Join(" | ", parts.ToArray());
        }

        public static string ProtocolName(int proto)
        {
            if (proto == LinkProto.Tcp) return "Tcp";
            if (proto == LinkProto.Udp) return "Udp";
            return proto.ToString(CultureInfo.InvariantCulture);
        }

        private static string Num(int? v)
        {
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "";
        }

        private static string Num(uint? v)
        {
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "";
        }

        private static int? IntAt(string[] f, int i)
        {
            if (f.Length <= i || string.IsNullOrEmpty(f[i])) return null;
            int v;
            return int.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? (int?)v : null;
        }

        private static uint? UIntAt(string[] f, int i)
        {
            if (f.Length <= i || string.IsNullOrEmpty(f[i])) return null;
            uint v;
            return uint.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? (uint?)v : null;
        }
    }

    /// <summary>
    /// keychip（机台身份串）和"伪 IP"之间的换算。
    /// 伪 IP 就是 md5(keychip) 的前 4 个字节按大端当 uint32 —— 服务端用同一套算法，
    /// 双方靠它互相寻址，所以它必须是纯函数（无状态、可单测）。
    /// </summary>
    public static class LinkStub
    {
        public static uint FromKeychip(string keychip)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(keychip ?? ""));
                return ((uint)h[0] << 24) | ((uint)h[1] << 16) | ((uint)h[2] << 8) | h[3];
            }
        }

        public static IPAddress ToIp(uint value)
        {
            return new IPAddress(new byte[]
            {
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            });
        }

        public static uint ToU32(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            if (b.Length != 4) throw new ArgumentException("只支持 IPv4：" + ip);
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        /// <summary>
        /// 生成这次启动用的 keychip。上游是 "W9" + 9 位随机数 —— 每次启动都换，
        /// 所以伪 IP 每次都不一样（同一台机子重启后就是"新身份"）。
        /// </summary>
        public static string NewKeychip(Random random)
        {
            return "W9" + random.Next(100000000, 999999999).ToString(CultureInfo.InvariantCulture);
        }

    }
}
