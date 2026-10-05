# InStoreLink —— maimai 店内联机「上游解剖 + 重写」

这份目录里有两样东西：

1. **说明文档**（就是本文件）：把上游 [MuNET-OSS/NyanLink](https://github.com/MuNET-OSS/NyanLink)
   的客户端 mod（发布出来叫 `WorldLink.dll`）**从头到尾读了一遍**之后的笔记 ——
   它怎么把游戏本体的「局域网 party」搬到公网上、线协议长什么样、跟游戏哪些地方咬在一起。
2. **重写版客户端 mod**（`tools/instorelink/`，产物 `InStoreLink.dll`）：协议完全兼容，
   但代码是我们自己的（不带 Tomlet 依赖、线程模型更稳、配置和日志更适合我们排查），
   以后要改就改这份。

> 上游是 MIT（Copyright (c) 2025 Azalea & Clansty & Japerz），我们这份是它的衍生作品，
> 署名与出处保留在 [`third_party/NyanLink/`](../third_party/NyanLink/README.md)（见文末「许可」）。

```
tools/instorelink/            ← 重写版源码：C# 5，11 个文件（本文件讲的就是它）
tools/build_instorelink.ps1   ← Windows 侧构建（用系统自带 csc.exe，不需要 SDK）
tools/build_wsl.sh            ← WSL 侧构建（借用 Windows 的 csc.exe）
tools/fingerprint.cs          ← 程序集指纹：判断发行版 dll 是不是当前源码编的
tests/                        ← 测试：协议单测 / 向量 / 端到端 / 兼容探针 / 参数名 / 发行版指纹 / 文档一致性
├── ProtocolTests.cs          ← 协议层单测（不依赖游戏，能单独编出来跑）
├── GameCompatProbe.cs        ← 游戏兼容性探针（补丁目标 / 注入字段类型）
├── run_all.sh                ← 一键跑全部（8 步）
└── py/{linkproto,test_vectors,test_e2e,test_docs}.py
docs/客户端mod实现.md          ← 本文件
```

---

## 一、一句话概括

游戏本体的「店内マッチング」是**局域网 party**：同一家店里的几台机子通过 `Manager.Party.Party`
互相发现、互相连 socket。上游 mod 干的事就是**把这条链路整个换掉**：

- 把自己的"本机 IP"换成 `md5(keychip)` 算出来的**伪 IP**；
- 把游戏往 party socket 里写/读的每一个字节，转成一行文本消息，走公网中继转给对方；
- 招募（开房/关房）不走本体的广播，而是 POST 到大厅的 HTTP 接口，房客每 10 秒拉一次列表；
- 顺便把包加密关掉（隧道两端都是明文，服务端不用知道密钥）。

所以它不需要改游戏数据、不需要同屏，就能让两台**不在同一个局域网**的机子进同一个 party。

---

## 二、三个角色

| 角色 | 是什么 | 我们仓库里的对应物 |
| --- | --- | --- |
| **游戏本体** | SDEZ 1.70 的 `Assembly-CSharp.dll`：`PartyLink.*`（socket、包）、`Manager.Party.Party.*`（party 管理、招募） | 不动它，只打补丁 |
| **客户端 mod** | 上游 `WorldLink.dll` / 我们的 `InStoreLink.dll`：MelonLoader + Harmony 插件 | `tools/instorelink/` |
| **服务端** | 大厅（HTTP，管招募列表）+ 中继（TCP，转发 party 数据） | `instorematchd`（我们自己那套） |

数据流（两台机子联机时）：

```
 游戏本体(A) ──┐                                    ┌── 游戏本体(B)
   PartyLink   │  Send/Receive                       │   PartyLink
      ▲        ▼                                     ▲
      │   ┌─────────┐   一行文本消息（TCP）    ┌─────────┐    │
      └───│ 影子socket│ ─────────────────────► │ 中继 :20101│ ───┘
          └─────────┘  ◄─────────────────────  └─────────┘
               │                                     │
               │  招募开房/关房（HTTP POST）          │  拉房间列表（HTTP GET，10 秒一次）
               ▼                                     ▼
           ┌──────────────── 大厅 :20100 ────────────────┐
           │ /recruit/start /recruit/finish /recruit/list │
           │ /info（告诉客户端中继地址） /online（在线人数）│
           └──────────────────────────────────────────────┘
```

---

## 三、一次联机的完整时序

1. **启动**：`OnInitializeMelon` 读 `InStoreLink.toml`（找不到就回退老的 `WorldLink.toml`）
   → `OnBeforePatch()` 拿中继地址
   （配置里没写 `RelayUrl` 就问大厅 `GET /info`）。
2. **刷卡登录**：本体走到 `OperationManager.CheckAuth_Proc`，mod 在这里
   - 随机生成 keychip（`"W9" + 9 位随机数`）→ 算出本机伪 IP；
   - 连中继 TCP，发 `CTL_START`（data = keychip），收到 `version=1` 表示注册成功；
   - 起两个线程：每 10ms 抽一次发送队列（每 1 秒插一个心跳），另起一个读循环。
3. **跳过联网自检**：本体启动时会先做一次"店里网络自检"（`StartupProcess`），
   mod 直接把状态机从 `0x04`（等自检）推到 `0x08`（就绪），并手动把
   `DeliveryChecker` / `Setting` / `Advertise` / `PartyMan` 拉起来。
4. **进选曲界面**：本体创建 party 客户端（`Manager.Party.Party.Client`），
   mod 挂在这个构造函数上，开始**每 10 秒** `GET /recruit/list`。
5. **开房**：房主选好歌发起招募 → 本体调 `SocketBase.sendClass(StartRecruit)`，
   mod 把它拦下来，改成 `POST /recruit/start`（body 里带 keychip + RecruitInfo）。
6. **房客看到房间**：轮询线程拉到列表 → 把 `StartRecruit` 包喂回本体
   → `MusicSelectProcess` 里那一栏列出房间（歌曲名、难度、房主头像）。
7. **进房**：房客选中房间 → 本体要连房主的 IP（也就是伪 IP）→ mod 的
   `ConnectAsync` 发 `CTL_TCP_CONNECT`；中继按伪 IP 找到房主、把请求转过去；
   房主 `Accept()` 后回 `CTL_TCP_ACCEPT`，两端各拿到一条"流"。
8. **开始打歌**：之后本体在 socket 上写/读的每个包，都被
   `DATA_SEND`（base64）包着走中继，对端解出来喂回本体 —— 后面就是游戏自己的逻辑了。

---

## 四、协议规格（这是最要紧的部分）

### 4.1 中继：一行一条消息，17 个字段

```
0     1     2        3     4      5      6      7      8..15      16..
固定1 cmd   proto    sid   src    sPort  dst    dPort  预留(空)    data
```

| 字段 | 含义 |
| --- | --- |
| `0` | 固定 `1`（解析时忽略，纯粹占位） |
| `1` | 命令号（见下表） |
| `2` | 协议：`6`=TCP，`17`=UDP（就是 `System.Net.Sockets.ProtocolType` 的数值） |
| `3` | 流 ID（TCP 多路复用用；一条 party socket 一个随机 int） |
| `4`/`6` | 源/目标**伪 IP**（uint32 十进制，不是点分十进制！） |
| `5`/`7` | 源/目标端口 |
| `8..15` | 预留，恒为空（上游注释：for future use） |
| `16..` | 数据：**base64**（游戏包体），可以再含逗号，所以解析时要从 16 位往后重新拼 |

序列化规则：空字段输出为空串，最后 `TrimEnd(',')` —— 所以心跳就是一行 `1,3`。

命令表：

| 命令号 | 名字 | 干什么 |
| --- | --- | --- |
| 1 | `CTL_START` | 注册，data = keychip；服务端回 `1,1,,,,,,,,,,,,,,,version=1` |
| 2 | `CTL_BIND` | 上游服务端有常量，两边都没用 |
| 3 | `CTL_HEARTBEAT` | 心跳，服务端原样回 `1,3`（客户端拿它算延迟） |
| 4 | `CTL_TCP_CONNECT` | 请求建流：`sid`+`src/sport`+`dst/dport` |
| 5 | `CTL_TCP_ACCEPT` | 接受建流 |
| 6 | `CTL_TCP_ACCEPT_ACK` | 上游服务端有常量，客户端没用 |
| 7 | `CTL_TCP_CLOSE` | 关流 |
| 21 | `DATA_SEND` | 数据（base64） |
| 22 | `DATA_BROADCAST` | 广播，只用于 UDP；客户端侧被 `SendTo` 屏蔽了 |

伪 IP 算法（客户端、服务端、我们的 instorematchd 三边一致）：

```python
md5(keychip.encode()).digest()[:4]  →  大端 uint32  →  直接写成点分十进制
# 例（合成值）：W2718281828 → 147.117.41.153（= 2473929113）
```

> 为什么用伪 IP：本体到处都在比较"这是不是本机地址""目标 IP 是谁"，
> 直接把真实 IP 换成一个固定映射的假 IP，改一处 (`PartyLink.Util.MyIpAddress`)
> 就能让整套逻辑照跑。代价是它必须同时出现在**招募数据**里（房客靠它找房主）。

### 4.2 中继的流模型

- 每个客户端注册后，服务端按伪 IP 建索引：`clients[stub] = 连接`。
- `DATA_SEND` / `CTL_TCP_CONNECT` 的目标要么按 `sid` 查流表、要么直接按 `dst` 伪 IP 查。
- 服务端转发时会把 `src`/`dst` **改写成双方的伪 IP**（客户端不需要知道真实 IP）。
- 客户端侧的队列 key（必须两边一致，否则数据进不了正确的队列）：

  | 队列 | key | 装什么 |
  | --- | --- | --- |
  | `tcpRecvQ` | `流ID + 本地端口` | 收到的流数据 |
  | `udpRecvQ` | `本地端口` | 收到的 UDP 数据 |
  | `acceptQ` | `本地端口` | 收到的建流请求（等游戏 `Accept`） |
  | `acceptPending` | `流ID + 本地端口` | 自己建流后等对方接受的挂起记录（带 8 秒超时器） |

### 4.3 大厅 HTTP

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/recruit/start` | 开房。body：`{"Keychip":"W...","RecruitInfo":{...}}` |
| POST | `/recruit/finish` | 关房。服务端校验 keychip 后删掉 |
| GET | `/recruit/list` | 每行一条 JSON（**已去掉 Keychip**），房客每 10 秒拉一次 |
| GET | `/info` | `{"relayHost":"isakio.cn","relayPort":20101}`，客户端不配 `RelayUrl` 时用它 |
| GET | `/online` | `{"totalUsers":2,"activeRecruits":1}`，客户端拿它显示在线人数 |

`RecruitInfo` 的结构（上游 Kotlin 服务端的权威定义；两边字段名必须一致）：

```
RecruitInfo
├── MechaInfo
│   ├── IsJoin(bool)  IpAddress(uint, 伪IP)  MusicID(int)
│   ├── Entrys(bool[])  UserIDs(long[])  UserNames(string[])
│   ├── IconIDs(int[])  FumenDifs(int[])  Rateing / ClassValue / MaxClassValue / UserType(int[])
│   └── （数组长度 2：自己 + 一个占位 "ＧＵＥＳＴ"）
├── MusicID(int)  GroupID(int)  EventModeID(bool)
└── JoinNumber(int)  PartyStance(int)  _startTimeTicks/_recvTimeTicks(long)
```

房间以伪 IP 为 key 存在内存里，**30 秒**没刷新就过期（`MAX_TTL`，我们那套可配）。

### 4.4 隧道里到底跑的是什么（游戏包体，实测样本）

`DATA_SEND` 的 `data` 解出来是游戏自己的 party 包。我们抓了一次真实会话（假玩家侧收到 29 个包），
长这样：

```
24 00 00 00 | b0 53 10 00 | b1 53 10 00 | 0c 00 00 00 | {"State":4} + 0 填充
 总长 = 36      源机台        目标机台      （见下）        正文（明文 JSON）
```

- 全是**小端**；
- 第 1 个字段是整包长度（这里 36）—— 这正好对应上游 `encrypt` 补丁"往偏移 0 写 count"的动作，
  也是我们能直接看到明文的原因；
- 第 2、3 个字段是游戏**内部的机台地址编码**（样本里 `0x1053B0` / `0x1053B1`，两台差 1），
  **不是**我们在协议里用的伪 IP —— 伪 IP 出现在正文 JSON 里
  （`MechaInfo.IpAddress = 288605160`）；
- 第 4 个字段样本里是 `12` / `7`，怀疑是命令号而不是长度（`{}` 那条也是 7），样本太少没敢下结论；
- 正文就是 **明文 JSON**：`{"State":4}`（party 状态）、`{"MechaInfo":{...}}`（自己的机台信息）、
  `{}`（心跳类空包）。

一次真实的进出记录（2026-10-05 实测，一方是我们这版 mod、另一方是假玩家）：

```
建流成功      >>> CTL_TCP_CONNECT / <<< CTL_TCP_ACCEPT（两边各一条）
双向数据      发出 29 个 DATA_SEND、收到 29 个（假玩家原样回弹）
```

### 4.5 异常流程：谁在什么时候发 `CTL_TCP_CLOSE`

上游（以及我们 v0.1）只在**游戏自己关流**时发一条 `CTL_TCP_CLOSE`，服务端收到就清表。
问题是"加入别人房间"这件事有一堆走不到正常结局的岔路，而两边都不知道对方已经不在了 ——
房客的表现就是**一直卡在"连接中"**，房主的表现是有个看不见的人挂着。

v0.2 补上这几条（服务端 `instorematchd`，客户端配合）：

| 触发 | 服务端做什么 | 房客（发起方）会看到 |
| --- | --- | --- |
| 目标伪 IP 不在线 | 立刻回一条 `CTL_TCP_CLOSE` 给发起方 | 连接**马上失败**（`ConnectionRefused`），不再干等 |
| 房主结束了招募（`/recruit/finish`） | 把它名下所有挂起建流一并取消 | 同上 |
| 挂起超过 `--pending-timeout`（默认 10 秒）没人接 | 回收并向两边各发一条 | 8 秒客户端超时 / 服务端先到就提前失败 |
| 接流方 Accept 了一条已经超时回收的流 | 回 `CTL_TCP_CLOSE` 给接流方，别让它攥着假流 | — |
| 一方 `CTL_TCP_CLOSE` | 除了清两边流表，**再转发给对端** | 对面那条流也会被清掉 |
| 挂起流数量超过 `MAX_STREAMS`(10) | 只拒这一条新流（v0.1 是**把整个连接踢掉**） | 这次加入失败，但人还在线 |

另外两条和"房主先开打"直接相关：

- **客户端建流超时**：8 秒没等到 `CTL_TCP_ACCEPT` 就按 `SocketError.TimedOut` 收尾，
  让游戏的连接流程走失败分支（上游没有这一步，只能永远等）。
- **招募续报**：房主还在等的时候每 10 秒把房间往大厅重报一次，避免 30 秒 TTL 把
  一条还有效的招募悄悄清掉。同一个房间最多续报 10 分钟，到点就停手交回给 TTL ——
  万一本体某条退出路径不发 `FinishRecruit`，也不会留下一条永远撤不掉的"幽灵房间"。

这些都在 `tests/py/test_edge.py` 里逐条验过（跑 `bash tests/run_all.sh` 会带上）。

---

## 五、与游戏本体的耦合点（补丁清单）

这是"改东西时最容易踩雷"的地方。[`tools/instorelink/PatchesNet.cs`](../tools/instorelink/PatchesNet.cs)
和 [`PatchesParty.cs`](../tools/instorelink/PatchesParty.cs) 一共 33 个补丁：

| 补丁 | 目标 | 为什么必须 |
| --- | --- | --- |
| `PreCheckAuth` | `Manager.OperationManager.CheckAuth_Proc` | 刷卡登录时启动联机（生成 keychip、连中继） |
| `PreIsLanAvailable` | `AMDaemon.Network.IsLanAvailable` | 强制 true，否则游戏认为"不在店内"，店内マッチング 分类根本不出现（和 AquaMai 的 `ForceAsServer` 冲突，那边必须关） |
| `PostCommonMonitorViewUpdate` | `Monitor.CommonMonitor.ViewUpdate` | 右下角状态文字；同时**每帧在主线程分发招募列表**（我们的改法） |
| `PreSendClass` | `PartyLink.SocketBase.sendClass` | 拦掉店外广播；把 `StartRecruit`/`FinishRecruit` 改成走 HTTP |
| `PreSocketError` | `PartyLink.SocketBase.error` | 本体常拿它打噪音（`send failed null (0)`），不打出来会当成"出错了" |
| `PostIsSameVersion` | `PartyLink.Packet.isSameVersion` | 强制 true，两边版本号不同也让它过 |
| `PreMyIpAddress` | `PartyLink.Util.MyIpAddress` | 把本机地址换成伪 IP —— 整套寻址的地基 |
| `PostStartupOnUpdate` | `Process.StartupProcess.OnUpdate` | 跳过本体联网自检（状态 `0x04`→`0x08`）并手动拉起 party 相关服务；顺带写开机自检那三行状态 |
| `PrePacketEncrypt` / `PrePacketDecrypt` | `PartyLink.Packet.encrypt/decrypt` | 把加解密换成"原样拷贝 + 写长度"，隧道里跑明文（否则服务端没法转发） |
| `PostNFSocketCtor` 等 16 个 | `PartyLink.NFSocket.*` | 影子 socket：把本体的每个 socket 调用转到我们的实现上。**拿不到影子对象时一律 `return true`（交回本体原生实现）** —— 包括 `RemoteEndPoint`/`LocalEndPoint` 两个 getter：它们以前返回 `null`，而调用方会直接取端口，那就变成 `NullReferenceException` |
| `PostClientCtor` | `Manager.Party.Party.Client` 构造 | 开始轮询 `/recruit/list` |
| `PreRecvStartRecruit` | `Client.RecvStartRecruit` | 对方选的歌没装就拦掉并提示（不拦会崩） |
| `PreMusicSelectOnStart` / `PostPartyExec` | `MusicSelectProcess` | 进界面重置状态；房间列表变了重画；右侧显示"谁在等" |
| `PostRecruitData` | `MusicSelectProcess.RecruitData` getter | 光标停在第几条就用第几个房间 |
| `PreIsConnectStart` / `PreSetConnectData` | `MusicSelectProcess.IsConnectStart/SetConnectData` | 把房间列表翻译成本体的"联机歌曲列表"（缩略图、SE、按钮） |

> 两处"看着像 bug、其实是游戏版本内情"的地方（是探针实测出来的，写补丁前不知道会踩）：
>
> - `StartupProcess._state` 的真实类型是**私有嵌套枚举** `Process.StartupProcess+StartUpState`
>   （`private sealed enum`，底层类型 `byte`）。上游（和我们）用 `ref byte ____state` 注入 ——
>   严格说类型不匹配，靠的是 Mono 不做 IL 校验。探针会检查它的底层类型确实是 `byte`，
>   不是的话这条补丁会炸。
> - `MusicSelectProcess._currentPlayerSubSequence` 的元素类型是**它自己嵌套的**
>   `MusicSelectProcess+SubSequence`，而不是 `Process.SubSequence`（游戏里有好几个同名枚举）。
>   写错了注入会失败，而 C# 编译期看不出来。
> - **补丁方法的普通参数必须和游戏里的参数名一字不差**：Harmony 是按**名字**给补丁传参的
>   （只有 `__instance` / `__result` / `___字段` 是特殊名字）。我们把
>   `NFSocket(Socket nfSocket)` 的 `nfSocket` 顺手写成 `socket`，编译毫无提示，进游戏才抛
>   `Parameter "socket" not found in method ...`，而 Harmony 又把它包成一句没头没尾的
>   `IL Compile Error (unknown location)`。现在有 `tests/run_param_check.sh` 专门查这个。

---

## 六、上游源码导读（`mod/` 一共 1654 行，其中联机逻辑 5 个文件 1580 行）

| 文件 | 行数 | 讲什么 |
| --- | --- | --- |
| `FutariTypes.cs` | 205 | 命令号枚举、`FutariMsg`（就是 4.1 的编解码）、日志封装 |
| `FutariExt.cs` | 131 | 工具：md5→伪 IP、base64、HTTP 的 Get/Post 扩展、`Interval()` 起后台线程 |
| `FutariClient.cs` | 239 | TCP 连接、注册、心跳、收发线程、重连、四个队列、延迟统计、状态码 |
| `FutariSocket.cs` | 215 | 影子 socket（Send/Receive/Accept/ConnectAsync/Poll…） |
| `FutariPatch.cs` | 790 | 所有 Harmony 补丁（就是第五节那张表） |

服务端（Kotlin，699 行）：`FutariTypes.kt`（协议与 JSON 结构）、`FutariLobby.kt`（HTTP 接口）、
`FutariRelay.kt`（中继）、`Application.kt`（入口）。我们的 `instorematchd` 就是照这三份重写的。

---

## 七、我们重写版（InStoreLink）改了什么

协议**一个字节都没改**（能和我们的大厅、也能和官方中继互通），改的是实现：

| # | 改动 | 为什么 |
| --- | --- | --- |
| 1 | 去掉 Tomlet 依赖，自己写 30 行 TOML 解析 | 上游靠反射调 Tomlet 的两个版本；我们只要三个键，零依赖更省事（也少了两个 DLL 的引用） |
| 2 | 招募列表改成**排队 + 主线程分发** | 上游是在轮询线程里直接调游戏的 `RecvStartRecruit`（跨线程改游戏状态）；我们在 `CommonMonitor.ViewUpdate` 这个每帧钩子里分发 |
| 3 | `Close()` 带上 `sid`/`src`/`dst` | 上游的 `CTL_TCP_CLOSE` 只有一个命令号，服务端没法知道关哪条流（流表只增不减）；我们的 instorematchd 会顺手清掉两边流表 |
| 4 | 加 `Stop()`（`OnApplicationQuit` 调用） | 上游没有正常的退出路径，退出游戏时线程还在无限重连 |
| 5 | `RelayUrl` 支持 `host` / `host:port` / `http://host:port` 三种写法 | 上游只认 `Uri` 能解析的形式，写错了才 fallback，容易困惑 |
| 6 | 日志文案统一成 `InStoreLink` / 中文状态（离线/未连接/连接中） | 顺便去掉了上游写在自检界面的彩蛋（`CAT :3`、`CRAZY THURSDAY`） |
| 7 | 记录最后一次连接错误、日志级别更清楚 | 排查"连不上中继"时不用翻整份日志 |
| 8 | 服务器 `/recruit/list` 的解析做了空行/坏行容错 | 上游任何一行 JSON 坏掉都会抛，整轮轮询白跑 |
| 9 | 逐条 `✓ 补丁名 → 目标方法` **不受 `Debug` 开关影响**，一律打出来 | 这 33 行是文档里让玩家确认"插件在这台机器上挂上了没有"的验收依据；以前它走 `LinkLog.Info`（Debug 才打），默认配置下一条都看不到 |
| 10 | `ApplyConnectData` 里把房间翻译进联机歌曲列表时**整条兜一层异常** | `GetNotesList()[musicId]` 是按曲目 ID 索引的，歌不在这台机器的谱面表里时可能返回 null、也可能直接抛越界/KeyNotFound；这是在 Unity 主线程上，抛出去就是整局崩（上游正是在这里崩的）。兜住之后只跳过这一条房间 |
| 11 | 新增 `LinkRuntime.ConnectList`：**记住真正显示出来的房间顺序**，`RecruitData` getter 按光标取时用它 | 大厅里装不了的歌会被跳过，此时"光标第 n 格"和"原始房间列表第 n 项"不是同一个房间 —— 会变成显示 A 的歌、进去却是 B 的房间 |
| 12 | 本体 `SocketBase.error` 的噪音降级成 `Warn`，并写明"与本插件无关" | 它本来就是游戏网络层自己抱怨（`send failed null (0)`），以前打成 `Error`，日志里一片红，容易误判成 mod 坏了 |
| 13 | 轮询用的 `LinkLobby.Get` 统一走带 8 秒超时的那条实现 | `WebClient` 本身没有超时：大厅卡住不回时，在线人数/招募列表这两个轮询线程会一直陪着等，列表再也不刷新 |
| 14 | 影子 socket 映射表换成 `ConcurrentDictionary` | 写它的是游戏线程（`NFSocket` 构造），读它的还包括"中继收到建流确认 → 回调"这条可能跑在接收线程上的路径；普通 `Dictionary` 在这种交叉访问下没有保证 |
| 15 | **建流加了 8 秒超时**：等不到 `CTL_TCP_ACCEPT` 就按 `SocketError.TimedOut` 结束这次连接 | 上游发起建流后就撒手不管：房主已经开打 / 房间没了 / 他那边卡住，房客就永远停在"连接中"。配套地，服务端也会在目标不在线、房间关掉、挂起超时这三种情况下主动推 `CTL_TCP_CLOSE` |
| 16 | 客户端处理收到的 `CTL_TCP_CLOSE` | 以前只发不收：服务端就算想通知"这条流废了"，客户端也是直接忽略。现在会区分三种情况——还在等接流就放弃这次连接、待 Accept 队列里那条丢掉、已建好的流清掉接收队列 |
| 17 | **招募期间每 10 秒往大厅续报一次"我的房间"** | 大厅的房间是 30 秒 TTL，而本体只在开始招募那一下发一次 `StartRecruit`（实测 5 分钟里总共 6 次，不是周期广播）。不续报的话，开好房干等 30 秒，大厅里那条房间就悄悄没了 —— 后来的人再也看不到。续报在主线程做，房间数据从 `PartyManager.GetRecruitList()`（含自己）里按伪 IP 找 |
| 18 | 被我们拒掉的房间（歌没装）记一笔，不再无限重喂 | 拒掉之后游戏永远不会"有这个房间"，对账每 2 秒又会喂一次 —— 无限重试 + 无限刷日志（`_deliveredAt` 那本账也因此越积越多）。现在房间从大厅消失时会把两本账一起清掉 |
| 19 | `StartRecruitPolling` 幂等 | 它挂在 `Client` 构造函数上，而 `Interval()` 每次无条件起一条线程：本体如果每进一次选曲就重建 party 客户端，就是每进一次多一条 10 秒轮询线程 |

> 和 `InStoreMatch.dll` 的关系：那个负责**让分类栏出现「店内マッチング」这一格**（本体快照问题），
> 这个负责**把那一格接到公网**。两个都要装，而且装了 `InStoreLink.dll` 就**必须删掉
> `WorldLink.dll`** —— 两者的补丁目标几乎完全重合，同时存在会打两遍。

---

## 八、构建与部署

**Windows（推荐，和你现有那套一样）**：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build_instorelink.ps1 `
    -Game "D:\game\maimai\SDEZ1.70\Package"
# 产物：<游戏目录>\Mods\InStoreLink.dll
```

**WSL 里构建**（借用 Windows 自带的 `csc.exe`，不需要 .NET SDK）：

```bash
bash tools/build_wsl.sh                      # 产物 build/InStoreLink.dll
bash tools/build_wsl.sh /mnt/e/game/xxx/Package   # 游戏装别处时
```

装的时候：删掉 `Mods\WorldLink.dll` → 放进 `InStoreLink.dll` → 写一份 `InStoreLink.toml`
（`LobbyUrl` 指大厅；`RelayUrl` 可选；老的 `WorldLink.toml` 也认）。
日志前缀会从 `[NyanLink]` / `[WorldLink]` 变成 `[InStoreLink]`。

> **能不能和朋友混用？** 协议完全一致，理论上你装我们的、他装上游的也能连上
> （两边都连同一个大厅/中继）。但联机这东西出问题时最难查的就是"两边不一样"，
> 所以建议要么都换、要么都不换。

**当前状态（2026-10-05）**：本机已切到我们这版 —— `Mods\` 里是
`InStoreLink.dll` + `InStoreMatch.dll` + `AquaMai.dll`，
上游的 `WorldLink.dll` 改名为 `WorldLink.dll.disabled-20261005` 停用（要回滚改回来即可）。
配置用的是 `InStoreLink.toml`（大厅地址没变）。

实机跑过三轮：

1. 第一轮 33 条补丁里挂了 32 条（唯一失败的就是上面那条 `nfSocket`/`socket` 参数名笔误），
   但连接、注册、心跳、影子 socket 绑定、跳过联网自检、选曲界面的「店内マッチング」全都正常；
2. 用假玩家（[`tools/fake_player.py`](../tools/fake_player.py)）在大厅挂了一个房间，
   游戏里 10 秒内就收到了：`[InStoreLink] 收到招募：{... "UserNames":["假朋友", ...]}` ——
   **大厅→轮询→主线程分发→游戏本体**这条链路实测打通。

3. 第三轮（33/33 全部挂上）：大厅里让假玩家挂了个房间，
   游戏里选中它一路按到 NEXT —— **建流成功、双向各 29 个数据包**（细节见 4.4 节与「已知限制」）。

还没验证的只剩一件：**两台真机一起打完一首**（假玩家不会推进 party 状态机，见「已知限制」）。

---

## 九、测试

```bash
bash tests/run_all.sh        # 九步全跑，一分钟左右
```

| 测试 | 覆盖什么 | 现在的结果 |
| --- | --- | --- |
| 编译 | 源码 ↔ 游戏本体 API 是否对得上 | ✅ 通过（`build/InStoreLink.dll`） |
| `tests/ProtocolTests.cs` | 序列化/解析往返、伪 IP、配置解析（36 项） | ✅ 全绿 |
| `tests/py/test_vectors.py` | 同一批向量 + **用真实抓包日志反验**（17 项；给出 `MAIMAI_LOGS` 时 18 项） | ✅ 全绿（12 种真实报文全部能还原） |
| `tests/py/test_e2e.py` | 起真的 instorematchd，跑完 开房→列表→建流→传数据→关流→关房（18 项） | ✅ 全绿 |
| `tests/py/test_edge.py` | **异常流程**：房主先开打 / 目标不在线 / 反复重试 / 挂起超时回收 / 身份校验 / 限速与房间上限（14 项） | ✅ 全绿 |
| `tests/GameCompatProbe.cs` | **游戏兼容性探针**：补丁目标方法是否存在、注入字段类型是否匹配、反射句柄拿不拿得到（60 项） | ✅ 全绿 |
| `tools/check_patch_params.cs` | **参数名检查**：两个 dll 的补丁（InStoreLink 33 条 + InStoreMatch 8 个补丁方法）的普通参数名逐个和游戏对齐，外加 Prefix/Postfix 标注、`___字段` 是否存在（Harmony 是按名字传参的） | ✅ 全绿 |
| `tools/fingerprint.cs` | **发行版指纹**：`client/` 里那两个 dll 是不是真的由当前源码编出来的（csc 输出不可复现，md5 比不出来） | ✅ 全绿 |
| `tests/py/test_docs.py` | **文档一致性**：发行 dll 的字节数 / md5、Markdown 相对链接、补丁条数、旧名字残留、`third_party/` 里有没有二进制 | ✅ 全绿 |
| `tests/run_release_check.sh` | **Release 附件一致性**（要 gh + 联网，所以不在上面那九步里）：GitHub 上最新 Release 挂的两个 dll 和 `client/` 里的实物是否一致 —— 拿 API 的 digest 比，不靠下载（下载链接有 CDN 缓存） | ✅ 全绿 |

探针也可以单独跑（游戏更新之后必跑）：

```bash
bash tests/run_probe.sh      # 临时把探针拷进 Managed\ 执行，跑完自动删掉
bash tests/run_param_check.sh  # 只查参数名（秒级）
bash tests/run_fingerprint.sh  # 只查发行版 dll 有没有落后于源码
bash tests/run_release_check.sh  # 只查 Release 附件有没有落后于 client/（发版后跑，需要 gh + 联网）

# 手里有联机时抓的 *.log 时，第 3 步会额外拿真实报文再验一遍（不设就跳过）
MAIMAI_LOGS=/path/to/logs bash tests/run_all.sh
```

真机上要看的日志（进游戏实测已经在 2026-10-05 跑过三轮，见上一节的「当前状态」）：

1. `Mods\` 里删掉 `WorldLink.dll`，放入 `InStoreLink.dll`，重启游戏；
2. 日志里逐条打 `[InStoreLink]   ✓ 补丁名 → 目标方法`（33 行），然后是
   `[InStoreLink] 挂钩完成，共 33 条生效`；哪条挂不上会点名 `✗` 并把异常打出来
   （逐条挂载，一条失败不会连累其余补丁）；
3. `[InStoreLink] 已连接中继 isakio.cn:20101（本机伪 IP …）`；
4. 进选曲界面 → 店内マッチング → 用 `fake_player.py` 或朋友开房验证能否看到并进入。

---

## 十、已知限制

前半截是**和上游一样的设计约束**（不是 bug），后半截是 v0.2 之后还剩的真限制。

- **最多 2 个人**。上游整套类都叫 `Futari*`（ふたり=两人）：party 里只有"自己 + 一个对端"，
  招募数据里房间也只有两格（`UserNames:["我","ＧＵＥＳＴ"]`）。本体的店内マッチング是 4 人房，
  但那走的是本体自己的 LAN party，这条隧道没实现多对端。
- **UDP 广播不可用**：`SendTo` / `ReceiveFrom` 直接返回 0（公网隧道还原不了广播语义）。
- **包加密被关掉**：party 包在隧道里是明文（base64），服务端能看到内容 —— 这也是它能转发的原因。
  自己搭大厅的话，等于你信任那台服务器。
- **keychip 每次启动都换**：伪 IP 随之改变。所以同一台机子重启后在大厅里是"新身份"，
  旧房间记录要等 TTL 过期（正常结束时游戏会发 `FinishRecruit`，房间立刻就撤）。
- **和中继断开就重连**，重连期间游戏那边的连接会卡住（本体不会自动重开 party 连接）。
- **对端掉线时我们不会给游戏伪造"连接已关闭"**：v0.2 会把服务端那条流清掉、也在
  日志里说清楚，但客户端 `Receive` 依旧只会返回 `WouldBlock` —— 真发 EOF（返回 0）
  有可能让本体的读取循环空转，风险比收益大，所以这一步没做，靠本体自己的 party 超时兜底。
- **假玩家（`tools/fake_player.py`）只能验证到"进房间"**：它把收到的包原样回弹，所以能看到
  建流成功、双向各几十个包，但游戏在等对端**推进状态机**（需要收到对端的 `MechaInfo` 和
  `{"State":n}` 迁移），于是会停在准备页 —— 这是假玩家的能力极限，不是 mod 的问题。
  要验证"能一起打歌"，必须两台真机。
- **派对状态机没有完整逆向**：我们把自己发出的包（`State 4`、`MechaInfo`、`{}`）看清楚了，
  但对方该按什么顺序回什么，还没摸。以后要让假玩家"陪打"，需要补这块。

---

## 十一、许可

- 上游 [MuNET-OSS/NyanLink](https://github.com/MuNET-OSS/NyanLink)：MIT，
  Copyright (c) 2025 Azalea（作者署名 `Azalea & Clansty & Japerz`）。
- 本目录（`InStoreLink` 重写版、测试、文档）：同样 MIT。协议与行为是照着上游做的，
  代码结构和注释是我们自己写的；**发布时必须保留上游署名**，并说明"这是基于 NyanLink 修改的"，
  不能写成"原样收录"。
