// InStoreLink —— 通信层补丁
//
// 这一层干的事：把游戏本体的"局域网 socket"整个换成我们的影子 socket，
// 把招募（开房/关房）改成走 HTTP 大厅，顺手关掉包加密（因为加密后的包没法中继转发）。
//
// 补丁清单（每一项都在 docs/客户端mod实现.md 的"与游戏本体的耦合点"表里）：
//   NFSocket.*                → 转发给 LinkSocket（影子 socket）
//   SocketBase.sendClass      → 拦掉无关广播；StartRecruit/FinishRecruit 改走 HTTP
//   SocketBase.error          → 原样打日志（本来会被吞）
//   PartyLink.Util.MyIpAddress→ 返回伪 IP（房主把伪 IP 写进招募数据，房客才知道连哪）
//   Packet.encrypt/decrypt    → 直接拷贝明文（隧道两端都是我们，不需要加密）
//   Packet.isSameVersion      → 强制 true（两边客户端版本号不同也能连）
//   AMDaemon.Network.IsLanAvailable → 强制 true（否则游戏认为"不在店内"）
//   StartupProcess.OnUpdate   → 跳过本体联网自检，直接进"可联机"状态；顺带写状态行
//   CommonMonitor.ViewUpdate  → 右下角状态文字（上游 mod 画的那几行状态面板）
//
// 上游 https://github.com/MuNET-OSS/NyanLink （MIT），本文件是它的等价重写。

using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using HarmonyLib;
using Manager;
using Manager.Party.Party;
using Monitor;
using PartyLink;
using Process;
using TMPro;
using UnityEngine;

namespace InStoreLink
{
    public static class PatchesNet
    {
        // 影子 socket 的假 mockID：Accept() 里造出来的那个哑 socket 用它，构造时跳过注册
        private const int MockSocketId = 3939;

        // ------------------------------------------------------------ 入场

        [HarmonyPrefix]
        [HarmonyPatch(typeof(OperationManager), "CheckAuth_Proc")]
        public static bool PreCheckAuth()
        {
            // 游戏"刷卡登录"时会走到这里，是我们启动联机的时机
            LinkRuntime.StartClient();
            return true;
        }

        // 让游戏认为"店内有局域网"，否则店内マッチング 分类不出现
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AMDaemon.Network), "IsLanAvailable", MethodType.Getter)]
        public static bool PreIsLanAvailable(ref bool __result)
        {
            __result = true;
            return false;
        }

        // ------------------------------------------------------------ 状态显示

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CommonMonitor), "ViewUpdate")]
        public static void PostCommonMonitorViewUpdate(CommonMonitor __instance,
            TextMeshProUGUI ____buildVersionText, GameObject ____developmentBuildText)
        {
            if (____buildVersionText == null || ____developmentBuildText == null) return;

            // 借用本体原本放"开发版"字样的位置来显示我们的状态
            ____buildVersionText.transform.position = ____developmentBuildText.transform.position;
            ____buildVersionText.gameObject.SetActive(true);

            LinkClient client = LinkRuntime.Client;
            if (client == null) return;

            switch (client.StatusCode)
            {
                case LinkClient.StatusFailed:
                    ____buildVersionText.text = "InStoreLink 离线";
                    ____buildVersionText.color = Color.red;
                    break;
                case LinkClient.StatusIdle:
                    ____buildVersionText.text = "InStoreLink 未连接";
                    ____buildVersionText.color = Color.gray;
                    break;
                case LinkClient.StatusConnecting:
                    ____buildVersionText.text = "InStoreLink 连接中";
                    ____buildVersionText.color = Color.yellow;
                    break;
                case LinkClient.StatusConnected:
                    int rooms = LinkRuntime.PartyMan == null ? 0 : LinkRuntime.PartyMan.GetRecruitList().Count;
                    if (LinkRuntime.OnlineUserCount > 0)
                    {
                        ____buildVersionText.text = "房间 " + rooms + " · 在线 " + LinkRuntime.OnlineUserCount;
                        ____buildVersionText.color = new Color(172 / 256f, 181 / 256f, 250 / 256f);
                    }
                    else
                    {
                        ____buildVersionText.text = "在线 0";
                        ____buildVersionText.color = Color.gray;
                    }
                    break;
            }

            // 轮询线程收到的"新房间 / 关房"在这里落到游戏里 —— 这一帧是主线程，
            // 上游是在轮询线程里直接调游戏方法的，我们挪到主线程来（见 LinkRuntime 注释）。
            try
            {
                PatchesParty.ReconcileRecruits();
            }
            catch (Exception ex)
            {
                LinkLog.Error("分发招募列表失败：" + ex.Message);
            }
        }

        // ------------------------------------------------------------ 发包拦截

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SocketBase), "sendClass", typeof(ICommandParam))]
        public static bool PreSendClass(SocketBase __instance, ICommandParam info)
        {
            if (info is AdvocateDelivery || info is Setting.SettingHostAddress)
                return false;                       // 店外广播，直接吞掉

            StartRecruit start = info as StartRecruit;
            FinishRecruit finish = info as FinishRecruit;
            if (start != null || finish != null)
            {
                RecruitInfo recruit = start != null ? start.RecruitInfo : finish.RecruitInfo;
                string action = start != null ? "start" : "finish";
                RecruitRecordOut record = new RecruitRecordOut();
                record.Keychip = LinkRuntime.Client.Keychip;
                record.RecruitInfo = recruit;
                string body = JsonUtility.ToJson(record);
                string url = LinkLobby.Combine(LinkRuntime.Config.LobbyUrl, "/recruit/" + action);
                LinkLobby.PostAsync(url, body, null);
                LinkLog.Info("已上报 " + action + " 招募：" + body);
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SocketBase), "error", typeof(string), typeof(int))]
        public static bool PreSocketError(string message, int no)
        {
            // 本体经常用它打无关痛痒的噪音（比如跳过联网自检之后的 "send failed null"）。
            // 用 Warn 而不是 Error：这是**本体**的网络层抱怨，不代表我们的插件坏了，
            // 打成 Error 会让人在日志里一眼看到一片红、误判成 mod 出问题。
            LinkLog.Warn("本体网络层报错（与本插件无关，供排查）：" + message + " (" + no + ")");
            return true;
        }

        // 两边客户端版本号不一致也让它过（同一个游戏版本才这么干）
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Packet), "isSameVersion")]
        public static void PostIsSameVersion(ref bool __result)
        {
            __result = true;
        }

        // 把自己的"本机地址"换成伪 IP —— 房主的招募数据里带的就是这个
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PartyLink.Util), "MyIpAddress", typeof(int))]
        public static bool PreMyIpAddress(int mockID, ref IPAddress __result)
        {
            __result = LinkRuntime.Client.StubAddress;
            return false;
        }

        // ------------------------------------------------------------ 跳过自检 + 状态行

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartupProcess), "OnUpdate")]
        public static void PostStartupOnUpdate(ref byte ____state,
            string[] ____statusMsg, string[] ____statusSubMsg)
        {
            if (____statusMsg == null || ____statusSubMsg == null) return;

            int n = ____statusMsg.Length;
            if (n >= 3)
            {
                LinkClient client = LinkRuntime.Client;
                ____statusMsg[n - 3] = "InStoreLink";
                string status;
                switch (client == null ? LinkClient.StatusIdle : client.StatusCode)
                {
                    case LinkClient.StatusFailed: status = "BAD"; break;
                    case LinkClient.StatusIdle: status = "未连接"; break;
                    case LinkClient.StatusConnecting: status = "连接中"; break;
                    default: status = "良好"; break;
                }
                ____statusSubMsg[n - 3] = status;

                long avg = client == null ? 0 : client.DelayAvg;
                ____statusMsg[n - 2] = "延迟";
                ____statusSubMsg[n - 2] = avg == 0 ? "N/A" : avg + " ms";

                ____statusMsg[n - 1] = "店内联机";
                ____statusSubMsg[n - 1] = "";
            }

            // 本体停在"等待联网自检"时，直接把它推进到就绪，并手动把该起的服务起起来
            if (____state != 0x04) return;
            ____state = 0x08;

            DeliveryChecker.get().start(true);
            Setting.Data data = new Setting.Data();
            data.set(false, 4);
            Setting.get().setData(data);
            Setting.get().setRetryEnable(true);
            Advertise.get().initialize(DB.MachineGroupID.ON);
            LinkRuntime.PartyMan.Start(DB.MachineGroupID.ON);
            LinkLog.Msg("已跳过本体联网自检");
        }

        // ------------------------------------------------------------ 包加解密（直接放行明文）

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Packet), "encrypt")]
        public static bool PrePacketEncrypt(Packet __instance, PacketType ____encrypt, PacketType ____plane)
        {
            if (____encrypt == null || ____plane == null) return false;
            ____encrypt.ClearAndResize(____plane.Count);
            Array.Copy(____plane.GetBuffer(), 0, ____encrypt.GetBuffer(), 0, ____plane.Count);
            ____encrypt.ChangeCount(____plane.Count);
            if (LinkRuntime.PacketWriteUInt != null)
                LinkRuntime.PacketWriteUInt.Invoke(null, new object[] { ____plane, 0, (uint)____plane.Count });
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Packet), "decrypt")]
        public static bool PrePacketDecrypt(Packet __instance, PacketType ____encrypt, PacketType ____plane)
        {
            if (____encrypt == null || ____plane == null) return false;
            ____plane.ClearAndResize(____encrypt.Count);
            Array.Copy(____encrypt.GetBuffer(), 0, ____plane.GetBuffer(), 0, ____encrypt.Count);
            ____plane.ChangeCount(____encrypt.Count);
            if (LinkRuntime.PacketWriteUInt != null)
                LinkRuntime.PacketWriteUInt.Invoke(null, new object[] { ____plane, 0, (uint)____plane.Count });
            return false;
        }

        // ------------------------------------------------------------ NFSocket → 影子 socket

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NFSocket), MethodType.Constructor,
            typeof(AddressFamily), typeof(SocketType), typeof(ProtocolType), typeof(int))]
        public static void PostNFSocketCtor(NFSocket __instance, AddressFamily addressFamily,
            SocketType socketType, ProtocolType protocolType, int mockID)
        {
            if (mockID == MockSocketId) return;     // 是我们自己在 Accept 里造的哑 socket
            LinkSocket shadow = new LinkSocket(addressFamily, socketType, protocolType, mockID);
            LinkRuntime.Redirect[__instance] = shadow;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NFSocket), MethodType.Constructor, typeof(Socket))]
        // 参数名必须和游戏里的完全一致（这里叫 nfSocket）——Harmony 是按名字找参数的，
        // 名字对不上就是 "Parameter "xxx" not found in method ..."。踩过一次。
        public static void PostNFSocketCtorFromSocket(NFSocket __instance, Socket nfSocket)
        {
            // 上游这里直接抛异常（他们认为不该走到这儿）。我们改成兜底：
            // 照着这个真 socket 的属性建一个影子 socket，免得哪天真出现就把游戏搞崩。
            LinkLog.Warn("遇到 NFSocket(Socket) 构造 —— 按它的属性建影子 socket 兜底");
            if (nfSocket == null) return;
            LinkRuntime.Redirect[__instance] = new LinkSocket(
                nfSocket.AddressFamily, nfSocket.SocketType, nfSocket.ProtocolType, 0);
        }

        private static LinkSocket Shadow(NFSocket socket)
        {
            LinkSocket shadow;
            LinkRuntime.Redirect.TryGetValue(socket, out shadow);
            return shadow;
        }

        /// <summary>
        /// 没有影子 socket 时的兜底：打一行警告，然后**让本体自己跑**（等价于原生行为）。
        /// 上游在这里直接用影子对象，影子不在就会 NRE 把游戏打死；我们宁可退化成原生。
        /// </summary>
        private static bool NoShadow(NFSocket socket, string what)
        {
            LinkLog.Warn("这个 socket 没有影子对象（" + what + "）—— 交给本体原生处理");
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "Poll")]
        public static bool PreNFPoll(NFSocket socket, SelectMode mode, ref bool __result)
        {
            LinkSocket shadow = Shadow(socket);
            if (shadow == null) return NoShadow(socket, "Poll");
            __result = LinkSocket.Poll(shadow, mode);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "Send")]
        public static bool PreNFSend(NFSocket __instance, byte[] buffer, int offset, int size,
            SocketFlags socketFlags, ref int __result)
        {
            LinkSocket shadow = Shadow(__instance);
            if (shadow == null) return NoShadow(__instance, "Send");
            __result = shadow.Send(buffer, offset, size, socketFlags);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "SendTo")]
        public static bool PreNFSendTo(NFSocket __instance, byte[] buffer, int offset, int size,
            SocketFlags socketFlags, EndPoint remoteEP, ref int __result)
        {
            LinkSocket shadow = Shadow(__instance);
            if (shadow == null) return NoShadow(__instance, "SendTo");
            __result = shadow.SendTo(buffer, offset, size, socketFlags, remoteEP);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "Receive")]
        public static bool PreNFReceive(NFSocket __instance, byte[] buffer, int offset, int size,
            SocketFlags socketFlags, out SocketError errorCode, ref int __result)
        {
            errorCode = SocketError.Success;       // out 参数必须先赋值（下面兜底分支会直接 return）
            LinkSocket shadow = Shadow(__instance);
            if (shadow == null) return NoShadow(__instance, "Receive");
            __result = shadow.Receive(buffer, offset, size, socketFlags, out errorCode);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "ReceiveFrom")]
        public static bool PreNFReceiveFrom(NFSocket __instance, byte[] buffer, SocketFlags socketFlags,
            ref EndPoint remoteEP, ref int __result)
        {
            LinkSocket shadow = Shadow(__instance);
            if (shadow == null) return NoShadow(__instance, "ReceiveFrom");
            __result = shadow.ReceiveFrom(buffer, socketFlags, ref remoteEP);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "Bind")]
        public static bool PreNFBind(NFSocket __instance, EndPoint localEndP)
        {
            LinkSocket shadow = Shadow(__instance);
            // 没有影子 socket 就交回本体：拦着不 bind 会让这个真 socket 永远没绑上
            if (shadow == null) return NoShadow(__instance, "Bind");
            shadow.Bind(localEndP);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "Listen")]
        public static bool PreNFListen(NFSocket __instance, int backlog)
        {
            LinkSocket shadow = Shadow(__instance);
            if (shadow == null) return NoShadow(__instance, "Listen");
            shadow.Listen(backlog);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "Accept")]
        public static bool PreNFAccept(NFSocket __instance, ref NFSocket __result)
        {
            LinkSocket listener = Shadow(__instance);
            if (listener == null) return NoShadow(__instance, "Accept");
            LinkSocket accepted = listener.Accept();
            if (accepted == null)
            {
                // 没有待处理的建流请求（正常情况不该走到：游戏是先 Poll 再 Accept 的）
                LinkLog.Warn("Accept 时没有待处理的建流请求");
                return true;                        // 交给本体原生处理，别造一个空壳出来
            }
            NFSocket mock = new NFSocket(AddressFamily.InterNetwork, SocketType.Dgram,
                ProtocolType.Udp, MockSocketId);
            LinkRuntime.Redirect[mock] = accepted;
            __result = mock;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "ConnectAsync")]
        public static bool PreNFConnectAsync(NFSocket __instance, SocketAsyncEventArgs e, int mockID,
            ref bool __result)
        {
            LinkSocket shadow = Shadow(__instance);
            if (shadow == null) return NoShadow(__instance, "ConnectAsync");
            __result = shadow.ConnectAsync(e, mockID);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "SetSocketOption")]
        public static bool PreNFSetSocketOption(NFSocket __instance, SocketOptionLevel optionLevel,
            SocketOptionName optionName, bool optionValue)
        {
            LinkSocket shadow = Shadow(__instance);
            if (shadow == null) return NoShadow(__instance, "SetSocketOption");
            shadow.SetSocketOption(optionLevel, optionName, optionValue);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "Close")]
        public static bool PreNFClose(NFSocket __instance)
        {
            LinkSocket shadow = Shadow(__instance);
            // 没影子 socket 就别拦着，让本体关掉它自己的真 socket（否则就是泄漏）
            if (shadow == null) return true;
            shadow.Close();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "Shutdown")]
        public static bool PreNFShutdown(NFSocket __instance, SocketShutdown how)
        {
            LinkSocket shadow = Shadow(__instance);
            if (shadow == null) return true;
            shadow.Shutdown(how);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "RemoteEndPoint", MethodType.Getter)]
        public static bool PreNFGetRemoteEndPoint(NFSocket __instance, ref EndPoint __result)
        {
            LinkSocket shadow = Shadow(__instance);
            // 拿不到影子对象时不能返回 null：调用方（本体）会直接取端口/.ToString()，
            // 那就变成 NullReferenceException。交给本体原生的 getter 拿真地址更安全。
            if (shadow == null) return NoShadow(__instance, "RemoteEndPoint");
            __result = shadow.RemoteEndPoint;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NFSocket), "LocalEndPoint", MethodType.Getter)]
        public static bool PreNFGetLocalEndPoint(NFSocket __instance, ref EndPoint __result)
        {
            LinkSocket shadow = Shadow(__instance);
            if (shadow == null) return NoShadow(__instance, "LocalEndPoint");
            __result = shadow.LocalEndPoint;
            return false;
        }
    }
}
