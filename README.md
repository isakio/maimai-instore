# maimai-instore

[![最新 Release](https://img.shields.io/github/v/release/isakio/maimai-instore?label=release)](https://github.com/isakio/maimai-instore/releases/latest)
[![License: MIT](https://img.shields.io/github/license/isakio/maimai-instore)](LICENSE)
[![公共大厅](https://img.shields.io/badge/%E5%85%AC%E5%85%B1%E5%A4%A7%E5%8E%85-isakio.cn%3A20100-blue)](http://isakio.cn:20100)

让 maimai DX 的「店内マッチング」能跨公网联机的一套东西：**一个客户端 mod + 一份自研的
大厅/中继服务端**。两台不在同一个局域网里的机子，装完就能在选曲界面互相看到、进同一个房间、
一起打。

> **为什么会有自己的客户端 mod**：这条路最早是
> [NyanLink](https://github.com/MuNET-OSS/NyanLink)（上游
> [MewoLab/worldlinkd](https://github.com/MewoLab/worldlinkd)）打开的 —— 它的客户端 mod
> 把游戏本体的局域网 party 换成了走公网中继的隧道。但它后来不太动了，所以本仓库现在
> **自己重写了一个协议兼容的客户端 mod（`InStoreLink`）**，不再分发上游的二进制：
>
> - **`InStoreLink`** —— 客户端 mod（C# / MelonLoader + Harmony）：注册中继、心跳、
>   影子 socket、开房走 HTTP 大厅、把对方的房间喂回游戏本体。线协议与上游**逐字节兼容**，
>   所以两边一家用我们这份、一家用上游那份也能连；
> - **`InStoreMatch`** —— 客户端插件：让选曲界面真的**画出**「店内マッチング」那一格
>   （游戏只在进界面时给分类栏拍一次快照，联机分类是之后才出现的）；
> - **`instorematchd`** —— 大厅 + 中继服务端（Python，零依赖），协议同样兼容上游。
>
> 代码是我们写的，但**协议与大量游戏侧机制来自上游的逆向成果**：署名（MIT，
> Copyright (c) 2025 Azalea）与出处保留在 [`third_party/NyanLink/`](third_party/NyanLink/README.md)，
> 完整的协议规格、上游源码导读、与游戏本体的 33 个耦合点都在
> [`docs/客户端mod实现.md`](docs/客户端mod实现.md)。
> 想用"直接跑上游二进制"的老方案，看分支
> [`legacy-worldlink`](https://github.com/isakio/maimai-instore/tree/legacy-worldlink)。

**只想玩？** 我们有一台已经跑着的大厅 `http://isakio.cn:20100` —— 去
[**最新 Release**](https://github.com/isakio/maimai-instore/releases/latest) 拿两个 dll，
照着下面的「方式 A」做就行。想自己开一台看「方式 B」。

## 它是怎么工作的

游戏本体的「店内マッチング」是**局域网 party**：同店的几台机子通过 `Manager.Party.Party`
互相发现、互连 socket。跨公网联机的做法是把这条链路整个换掉：

```
 游戏本体 ── Send/Receive ──► InStoreLink 的影子 socket ──► 中继 :20101 ──► 对面
      ▲                                                      ▲
      └── 开房/关房改成 POST /recruit/start|finish ──► 大厅 :20100 ─┘
          房间列表每 10 秒 GET /recruit/list 拉一次
```

`InStoreLink.dll` 干的就是这一层（它替代了上游的 `WorldLink.dll`，协议完全一致）：
把"本机 IP"换成 `md5(keychip)` 算出的**伪 IP**、把游戏的 party 包转成一行行文本消息走中继、
顺便关掉包加密（隧道里跑明文，服务端不用知道密钥）。详细规格见
[`docs/客户端mod实现.md`](docs/客户端mod实现.md)。

而 `InStoreMatch.dll` 解决的是另一半问题：

游戏本体的选曲界面里，底部那排分类标签（`Monitor.MusicSelectMonitor._genreTabController`
→ `.SelectorTab._tabDatas`）**只在进入界面时拍一次快照**。联机用的 198 号分类
（店内マッチング）是进界面之后才随着大厅数据异步出现的 —— 快照不会重拍，
那一格就画不出来，也就找不到加入房间的入口。

`InStoreMatch.dll` 在检测到 198 号分类出现后，用游戏自己的方式重建这份标签栏数据、
把「店内マッチング」补进去，并同步好滚动边界。**两台机器都要装。**

为了这一格用起来完整，插件还处理了三处游戏本体写死的边界：

| 处理 | 本体行为 |
| --- | --- |
| 「ジャンル」面板里画出这一格的大卡片 | 本体按分类名把数据置空、不画 |
| 面板右箭头（在倒数第二格也要能切到最后） | 本体的 `count--` 判断把最后一格排除在外 |
| 联机分类里的 BACK 按钮 | 本体 `IsBackEnable()` 直接不给 |

每一处的反汇编依据、完整的调试过程见 [`docs/技术笔记.md`](docs/技术笔记.md)。

## 仓库内容

```
├── client/                        ← 装客户端要的东西全在这儿
│   ├── InStoreLink.dll            ← 联机 mod（v0.1，替代上游 WorldLink.dll）
│   ├── InStoreMatch.dll           ← 让「店内マッチング」那一格画出来（v2.5）
│   └── install.ps1                ← 一条命令：拷两个 dll + 停用旧的 + 写 InStoreLink.toml
├── tools/
│   ├── instorelink/*.cs           ← InStoreLink 源码（C# 5，11 个文件，零第三方依赖）
│   ├── build_instorelink.ps1      ← 用 Windows 自带 csc.exe 编译，不需要装 SDK
│   ├── build_wsl.sh               ← 在 WSL 里编译（借用 Windows 的 csc.exe）
│   ├── InStoreMatch.cs            ← InStoreMatch 源码（单文件）
│   ├── build_instorematch.ps1     ← 编译 InStoreMatch
│   ├── check_patch_params.cs      ← 检查补丁参数名（Harmony 是按名字传参的）
│   ├── il.py / find_type.py / dump_sigs.cs / fingerprint.cs  ← 读游戏程序集的小工具
│   ├── fake_player.py             ← 假玩家：不用真人就能测招募/进房
│   └── README.md                  ← 插件内部逻辑、四个开关、踩过的坑
├── tests/                         ← 测试：协议单测 / 向量 / 端到端 / 兼容探针 / 参数名 / 发行版指纹 / 文档一致性
│   ├── run_all.sh                 ← 一键跑全部（8 步）
│   ├── ProtocolTests.cs           ← 协议层单测（不依赖游戏，能单独编出来跑）
│   ├── GameCompatProbe.cs         ← 游戏兼容性探针（补丁目标 / 注入字段）
│   └── py/                        ← 协议模型、真实日志反验、端到端、文档一致性
├── instorematchd/                 ← 自研联机服务端（大厅 + 中继，Python 标准库，零依赖）
│   ├── install.sh                 ← 一键装（systemd）
│   ├── Dockerfile / docker-compose.yml
│   ├── test_protocol.py           ← 协议自测
│   └── README.md                  ← 部署步骤、参数、升级、卸载
├── third_party/NyanLink/          ← 上游的 MIT 许可与出处（协议来源说明）
└── docs/
    ├── 客户端mod实现.md         ← ★ 协议规格 + 上游源码导读 + 与游戏本体的耦合点
    ├── 技术笔记.md              ← 实现细节与调试过程（反汇编证据都在这里）
    ├── 双人联机配置清单.md       ← 给玩家看的完整配置步骤
    └── 给朋友看-安装步骤.md      ← 可以直接转发给搭子的简版说明
```

## 怎么用

### 方式 A：连我的服务器（最省事，客户端装完就能玩）

大厅地址：**`http://isakio.cn:20100`** —— 已经跑着，不用你自己搭。

前提：游戏 **SDEZ 1.70** + **MelonLoader 0.6.4**。

**1. 放两个 mod 到 `<游戏目录>\Mods\`**

| 文件 | 干什么 | 校验 |
| --- | --- | --- |
| [`client/InStoreLink.dll`](client/InStoreLink.dll) | 联机本体：注册中继、开房、把对方的房间喂回游戏 | v0.1，48640 字节 / md5 `e1454e501bcf1f758545ab81e45e9193` |
| [`client/InStoreMatch.dll`](client/InStoreMatch.dll) | 让选曲界面画出「店内マッチング」那一格 | v2.5，18432 字节 / md5 `1a94a9b6be9b03bfb274e41b0a8256b3` |

两个文件都在本仓库的 [`client/`](client/) 里（[最新 Release](https://github.com/isakio/maimai-instore/releases/latest) 也附了），不用再去别的地方下。

> 如果你 `Mods\` 里还有以前那份 `WorldLink.dll`，**必须先删掉**：它和 `InStoreLink.dll`
> 补丁目标重叠，两个一起跑会把补丁打两遍。我们的安装脚本会自动把它改名停用。

**2. 在游戏根目录（`Sinmai.exe` 那一层）放 `InStoreLink.toml`**

```toml
LobbyUrl="http://isakio.cn:20100"
Debug=false
```

（老的 `WorldLink.toml` 也认 —— 找不到 `InStoreLink.toml` 时会回退用它，所以升级不用改名。）

**3. 装了 AquaMai 的话**，把 `AquaMai.toml` 里这一段改成：

```toml
[GameSettings.ForceAsServer]
Disabled = true
```

（注释掉无效，必须显式写 `Disabled = true`。不关的话游戏认为"不在店内"，联机分类根本不会出现。）

**4. 进 Test 模式设两项**：按住 `F1` → `ゲーム設定` →
`店内マッチングの設定` = **ON**，`グループ内基準機の設定` = **基準機**，然后重启游戏。

上面 1~2 步可以一条命令做完（脚本会用仓库里的两个 dll，并把旧的 `WorldLink.dll` 改名停用）：

```powershell
git clone https://github.com/isakio/maimai-instore.git
cd maimai-instore
powershell -ExecutionPolicy Bypass -File .\client\install.ps1
```

脚本会自己找游戏目录；找不到会提示你把文件夹拖进窗口，也可以直接指定：
`-GameDir "<游戏根目录，就是 Sinmai.exe 那一层>"`。

它会顺手写一份 `<游戏根目录>\InStoreLink.toml`（**已经存在的话不动它**，免得覆盖你自己填的
地址），默认就是我们的公共大厅 `http://isakio.cn:20100` ——
**不自己搭服务器的话，这个参数根本不用管**。
只有你自己搭了大厅（见「方式 B」）时，才在命令最后多给一个 `-LobbyUrl` 参数改掉它。

不想 clone 的话：从 [Release](https://github.com/isakio/maimai-instore/releases/latest)
把两个 dll 下下来手动拷进 `Mods\`，再自己写那个 `InStoreLink.toml` 也一样。

**验收**：`MelonLoader\Logs\Latest.log` 里应该有

```
[InStoreLink] InStoreLink 0.1.0 已加载（配置 InStoreLink.toml，大厅 http://isakio.cn:20100，详细日志 关）
[InStoreLink]   ✓ PreCheckAuth → OperationManager.CheckAuth_Proc
...（一共 33 行“✓ 补丁名 → 目标方法”）
[InStoreLink] 挂钩完成，共 33 条生效
[InStoreLink] 已连接中继 isakio.cn:20101（本机伪 IP ...）
[InStoreMatch] v2.5 已加载（...）
[InStoreMatch] 挂钩成功: reinputConnectCombineData ...（共 7 行“挂钩成功”）
```

（`Debug=true` 时还会打心跳和每个包；排查问题的时候再开。）

### 方式 B：自己搭服务器（不想连我那台，或者想开给一群人）

`instorematchd` 只用 Python 标准库，一台有公网 IP 的 Linux 5 分钟搞定。

```bash
git clone https://github.com/isakio/maimai-instore.git && cd maimai-instore

# 把 203.0.113.10 换成你的公网 IP 或域名
sudo bash instorematchd/install.sh 203.0.113.10
```

脚本会建服务账号、装到 `/opt/instorematchd`、生成 systemd 单元并启动、跑一遍协议自测。
装完在**防火墙和云厂商安全组**放行 `20100/tcp`（大厅）+ `20101/tcp`（中继），
然后浏览器打开 `http://203.0.113.10:20100/` 就是看板。

也可以用 Docker：

```bash
cd maimai-instore/instorematchd
HOST_OVERRIDE=203.0.113.10 docker compose up -d --build

# 要开管理员视图（看未打码的完整信息）就再加一个变量：
# HOST_OVERRIDE=203.0.113.10 IMD_ADMIN_TOKEN=你的token docker compose up -d --build
```

客户端那边要把地址换成你自己的。两种做法都行：

```powershell
# 装的时候直接给参数（推荐）
powershell -ExecutionPolicy Bypass -File .\client\install.ps1 `
    -LobbyUrl "http://203.0.113.10:20100"
```

或者装完之后手动改 `<游戏根目录>\InStoreLink.toml`：

```toml
LobbyUrl="http://203.0.113.10:20100"
```

> `-LobbyUrl` 就是"大厅地址"这个参数本身；而 `InStoreLink.toml` 才是游戏真正读的配置文件
> （安装脚本只是帮你把它写出来而已）。

> `--host-override`（脚本第一个参数）必须填**客户端能访问到的地址**：`/info` 会把它
> 作为中继地址下发给客户端，填错的话客户端能进大厅但连不上中继。
> 详细参数、升级、卸载见 [`instorematchd/README.md`](instorematchd/README.md)。

看板默认**脱敏**（keychip 显示成 `W9999***877`、IP 显示成 `203.0.x.x`，玩家名保留）；
想看未打码的完整信息，装的时候把 token 一起给它：

```bash
sudo IMD_ADMIN_TOKEN='你的token' bash instorematchd/install.sh <你的域名或公网IP>
# 之后用 /admin?token=你的token 看全量；不给这个变量则不开放 /admin
```

两种方式可以混用：你自建了大厅，也可以把地址发给朋友一起连。

### 开始玩

1. 两边都**进到选曲界面**（别在标题界面就开招募）
2. 一方选好歌 → 发起招募
3. 另一方进分类栏最右边的「店内マッチング」（蓝色 `UI_CMN_TabTitle_NetworkBattle` 图标），
   或按「ジャンル」→ 右箭头切到最后一格
4. 等最多 10 秒，房间出现 → 选中 → 进房
5. 到准备页后，**两边各按一次 NEXT**

一份可以直接转发给搭子的简版说明在
[`docs/给朋友看-安装步骤.md`](docs/给朋友看-安装步骤.md)。

## 已知问题

- `http://isakio.cn:20100` 是**公开大厅**：知道地址的人都能连上来，看板也是公开的 ——
  不过 keychip / IP 已经打码，只留玩家名、房间和曲目信息。介意的话用「方式 B」自己搭一台。
- 刷卡登录时偶发 `PlInformationProcess.RestoreGhost` 崩溃：本体在 ghost 记录查不到曲目时
  有个分支没判空。跟本插件无关，多启动几次能躲开，详见 `docs/技术笔记.md`。
- **最多只能 2 人一起玩，这不是服务端的限制**：游戏本体的店内マッチング是 4 人房
  （本体的 `Manager.Party.Party`，一个房间最多 4 台机），但 NyanLink 的客户端 mod 把这条
  路径换成了只认一个对端的「ふたり」隧道 —— 它自己的类就叫 `FutariClient` /
  `FutariSocket` / `FutariMsg`，招募数据里房间也只有两格
  （`UserNames:["自己","ＧＵＥＳＴ"]`、`Entrys:[true,false]`、`JoinNumber:0/1`）。
  大厅和中继对人数没有上限（任意两个客户端之间都能建流），所以你在大厅里**能看到**
  第三个房间/第三个人，但他点进同一个房间时，对面的 mod 没有"再接一台机"的代码路径，
  连不上。想真的 4 人：回到同一局域网，用游戏本体的店内匹配。
- 大厅没有配对机制：所有房间对所有人生效，加入是"先到先得"，一个房间满 2 人就不再收。
  多人同时挂着招募时，列表会混在一起（客户端是按"第几条"对应房间的）。

## 鸣谢

- [MuNET-OSS/NyanLink](https://github.com/MuNET-OSS/NyanLink) —— 协议与客户端 mod 的最初实现，
  我们的 `InStoreLink` / `instorematchd` 都是照着它逆向重写的
- [MewoLab/worldlinkd](https://github.com/MewoLab/worldlinkd) —— NyanLink 的上游

## License

- **本仓库的全部内容**（`tools/`、`tests/`、`instorematchd/`、`client/` 下的两个 dll、文档）：
  MIT，见 [LICENSE](LICENSE)。本仓库**不再分发任何第三方二进制**。
- **`client/InStoreLink.dll` 与 `instorematchd`**：是我们自己的实现，但**协议与大量游戏侧机制
  来自 [NyanLink](https://github.com/MuNET-OSS/NyanLink) 的逆向成果**，属于衍生作品 ——
  按它的 MIT 许可保留署名（Copyright (c) 2025 Azalea），出处说明见
  [`third_party/NyanLink/`](third_party/NyanLink/README.md)。
