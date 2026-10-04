# maimai-instore

[![最新 Release](https://img.shields.io/github/v/release/isakio/maimai-instore?label=release)](https://github.com/isakio/maimai-instore/releases/latest)
[![License: MIT](https://img.shields.io/github/license/isakio/maimai-instore)](LICENSE)
[![公共大厅](https://img.shields.io/badge/%E5%85%AC%E5%85%B1%E5%A4%A7%E5%8E%85-isakio.cn%3A20100-blue)](http://isakio.cn:20100)

[NyanLink](https://github.com/MuNET-OSS/NyanLink)（上游是 [MewoLab/worldlinkd](https://github.com/MewoLab/worldlinkd)）给
maimai DX 提供了 C2C 联机。这个仓库是它的**配套补完**：把「店内マッチング」这个分类
真正修好，另外附一份自研的联机服务端（协议兼容，零依赖）。

> **和 NyanLink 的关系**：这不是它的分支，也不包含它的源码或二进制 —— 但它**配套**它，
> 服务端也是照着它的协议**重写**的：
>
> - `InStoreMatch` 是独立的 MelonLoader 插件，Harmony 补丁全部打在**游戏本体**
>   （`Assembly-CSharp`）上，完全不碰 NyanLink 的 mod；
> - `instorematchd` 是**重新实现**的大厅 + 中继（Python，零依赖），目标是不改 NyanLink 客户端
>   一行就能连 —— 消息格式、命令号、伪 IP 算法都是从它的客户端 mod **逆向**出来的；
> - NyanLink 的客户端 mod（`WorldLink.dll`）请从
>   [官方 release](https://github.com/MuNET-OSS/NyanLink/releases) 下载，本仓库不分发。
>
> License 都是 MIT（沿革：`MewoLab/worldlinkd` → `Japerz12138/worldlinkd` → `MuNET-OSS/NyanLink`）。

**只想玩？** 我们有一台已经跑着的大厅 `http://isakio.cn:20100` —— 去
[**最新 Release**](https://github.com/isakio/maimai-instore/releases/latest) 下载
`InStoreMatch.dll`，照着下面的「方式 A」做就行。想自己开一台，看「方式 B」。

## 它是怎么工作的

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
├── client/
│   ├── InStoreMatch.dll           ← 预编译好的客户端插件（v2.5，SDEZ 1.70），下载即用
│   └── install-instorematch.ps1   ← 一条命令：拷 DLL + 写 WorldLink.toml
├── tools/
│   ├── InStoreMatch.cs            ← 客户端客户端插件（MelonLoader + Harmony，C# 5）
│   ├── build_instorematch.ps1     ← 用 Windows 自带 csc.exe 编译，不需要装 SDK
│   ├── il.py                      ← 反汇编 Assembly-CSharp.dll 的小工具
│   ├── fake_player.py             ← 假玩家：不用真人就能测招募/进房
│   └── README.md                  ← 插件内部逻辑、四个开关、踩过的坑
├── instorematchd/                     ← 自研联机服务端（大厅 + 中继，Python 标准库，零依赖）
│   ├── install.sh                 ← 一键装（systemd）
│   ├── Dockerfile / docker-compose.yml
│   ├── test_protocol.py           ← 协议自测
│   └── README.md                  ← 部署步骤、参数、升级、卸载
└── docs/
    ├── 技术笔记.md              ← ★ 实现细节与调试过程（反汇编证据都在这里）
    ├── 双人联机配置清单.md       ← 给玩家看的完整配置步骤
    └── 给朋友看-安装步骤.md      ← 可以直接转发给搭子的简版说明
```

## 怎么用

### 方式 A：连我的服务器（最省事，客户端装完就能玩）

大厅地址：**`http://isakio.cn:20100`** —— 已经跑着，不用你自己搭。

前提：游戏 **SDEZ 1.70** + **MelonLoader 0.6.4**。

**1. 放两个 mod 到 `<游戏目录>\Mods\`**

| 文件 | 从哪来 |
| --- | --- |
| `WorldLink.dll` | [NyanLink Releases](https://github.com/MuNET-OSS/NyanLink/releases)（本仓库不分发）。两边要用同一个文件：50176 字节 / md5 `9dfa62d5cba41deac0c2c74334ef8371` |
| `InStoreMatch.dll` | 本仓库 [**最新 Release**](https://github.com/isakio/maimai-instore/releases/latest) 下载（v2.5，18432 字节 / md5 `1a94a9b6be9b03bfb274e41b0a8256b3`）。仓库里的 [`client/InStoreMatch.dll`](client/InStoreMatch.dll) 是同一份 |

**2. 在游戏根目录（`Sinmai.exe` 那一层）放 `WorldLink.toml`**

```toml
LobbyUrl="http://isakio.cn:20100"
Debug=false
```

**3. 装了 AquaMai 的话**，把 `AquaMai.toml` 里这一段改成：

```toml
[GameSettings.ForceAsServer]
Disabled = true
```

（注释掉无效，必须显式写 `Disabled = true`。不关的话游戏认为"不在店内"，联机分类根本不会出现。）

**4. 进 Test 模式设两项**：按住 `F1` → `ゲーム設定` →
`店内マッチングの設定` = **ON**，`グループ内基準機の設定` = **基準機**，然后重启游戏。

上面 1~2 步可以一条命令做完（脚本会用仓库里的 `client/InStoreMatch.dll`）：

```powershell
git clone https://github.com/isakio/maimai-instore.git
cd maimai-instore
powershell -ExecutionPolicy Bypass -File .\client\install-instorematch.ps1 `
    -GameDir "D:\game\maimai\SDEZ1.70\Package"
```

（不加 `-LobbyUrl` 就是默认的 `http://isakio.cn:20100`。）

不想 clone 的话：从 [Release](https://github.com/isakio/maimai-instore/releases/latest)
把 `InStoreMatch.dll` 下下来，手动拷进 `Mods\`，再自己写那个 `WorldLink.toml` 也一样。

**验收**：`MelonLoader\Logs\Latest.log` 里应该有

```
[NyanLink] WorldLink server address: isakio.cn:20101
[InStoreMatch] v2.5 已加载（...）
[InStoreMatch] 挂钩成功: reinputConnectCombineData ...（共 7 行“挂钩成功”）
```

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
```

客户端那边把 `-LobbyUrl` / `WorldLink.toml` 的地址换成你自己的：

```powershell
powershell -ExecutionPolicy Bypass -File .\client\install-instorematch.ps1 `
    -GameDir "D:\game\maimai\SDEZ1.70\Package" -LobbyUrl "http://203.0.113.10:20100"
```

> `--host-override`（脚本第一个参数）必须填**客户端能访问到的地址**：`/info` 会把它
> 作为中继地址下发给客户端，填错的话客户端能进大厅但连不上中继。
> 详细参数、升级、卸载见 [`instorematchd/README.md`](instorematchd/README.md)。

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

- `http://isakio.cn:20100` 是**公开大厅**：知道地址的人都能连上来（看板也是公开的，
  会显示在线玩家名和房间列表）。介意的话用「方式 B」自己搭一台。
- 刷卡登录时偶发 `PlInformationProcess.RestoreGhost` 崩溃：本体在 ghost 记录查不到曲目时
  有个分支没判空。跟本插件无关，多启动几次能躲开，详见 `docs/技术笔记.md`。
- 大厅没有配对机制：所有房间对所有人生效，加入是"先到先得"，一个房间满 2 人就不再收。
  多人同时挂着招募时，列表会混在一起（客户端是按"第几条"对应房间的）。

## 鸣谢

- [MuNET-OSS/NyanLink](https://github.com/MuNET-OSS/NyanLink) —— 协议与客户端 mod
- [MewoLab/worldlinkd](https://github.com/MewoLab/worldlinkd) —— NyanLink 的上游

## License

MIT（见 [LICENSE](LICENSE)）。本仓库只包含我们自己写的代码；NyanLink 及其上游的
代码与二进制遵循其各自的许可。
