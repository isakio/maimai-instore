# NyanLink Companion

[NyanLink](https://github.com/MuNET-OSS/NyanLink)（[MewoLab/worldlinkd](https://github.com/MewoLab/worldlinkd) 的 fork）给
maimai DX 提供了 C2C 联机。这个仓库是它的**配套补完**：把「店内マッチング」这个分类
真正修好，另外附一份自研的联机服务端（协议兼容，零依赖）。

> 本仓库**不分发** NyanLink 的任何二进制，也不包含它的源码 —— 客户端 mod 请自行从
> [官方 release](https://github.com/MuNET-OSS/NyanLink/releases) 下载。这里只有我们自己写的代码。

## 解决的问题

用 NyanLink 联机时，选曲界面底部的分类栏里**不会出现「店内マッチング」那一格**，
所以找不到加入对方房间的入口。反汇编 `Assembly-CSharp.dll` 后定位到原因：

1. 底部标签栏画的是 `Monitor.MusicSelectMonitor._genreTabController` →
   `.SelectorTab._tabDatas`，它**只在进入选曲界面时拍一次快照**。
   NyanLink 的 198 号分类（店内マッチング）是进界面之后才异步塞进数据的 —— 快照不会重拍，
   所以那一格永远不存在。
2. 滚动边界用的是 `CategoryNameList.Count`，这条路径也没同步补名字，导致最后一格够不到。

`tools/WLDiag.cs`（编译成 `WLDiag.dll` 放进 `Mods/`）在检测到 198 号分类后，
用游戏自己的方式重拍标签栏并补齐边界。**两台机器都要装**。

顺带还修了三处本体自己写死的边界问题：

| 问题 | 本体代码 |
| --- | --- |
| 「ジャンル」面板里不给「店内マッチング」画大卡片 | `GenreSelectChainList.SetObjectData` 里按分类名把数据置空 |
| 面板站在倒数第二格时右箭头消失、按下也没反应 | `GenreSelectSequence.CheckButton` / `.Update` 里 `count--` 后的 `>=` 判断漏掉最后一格 |
| 联机分类里没有 BACK 按钮 | `MusicSelectSequence.IsBackEnable() => !IsConnectionFolder(0)` |

细节和完整的排查过程（含每一处反汇编依据）见 [`docs/联机排错记录.md`](docs/联机排错记录.md)。

## 仓库内容

```
├── tools/
│   ├── WLDiag.cs            ← 客户端修复插件（MelonLoader + Harmony，C# 5）
│   ├── build_wldiag.ps1     ← 用 Windows 自带 csc.exe 编译，不需要装 SDK
│   ├── il.py                ← 反汇编 Assembly-CSharp.dll 的小工具
│   ├── fake_player.py       ← 假玩家：不用真人就能测招募/进房
│   └── README.md
├── nyanlinkd/               ← 自研联机服务端（大厅 + 中继，Python 标准库，零依赖）
│   ├── nyanlinkd.py
│   ├── test_protocol.py     ← 协议自测
│   └── README.md            ← 部署步骤（systemd / docker 两种）
└── docs/
    ├── 联机排错记录.md       ← ★ 完整排查过程与结论（反汇编证据都在这里）
    ├── 双人联机配置清单.md    ← 给玩家看的配置步骤
    └── 给朋友看-安装步骤.md   ← 可以直接转发给搭子的简版说明
```

## 部署

### 一、服务端（任意一台有公网 IP 的 Linux，5 分钟）

`nyanlinkd` 只用 Python 标准库，不需要 pip、不需要数据库。

**方式 A：一键脚本（systemd）**

```bash
git clone https://github.com/isakio/NyanLink-Companion.git
cd NyanLink-Companion

# 把 203.0.113.10 换成你的公网 IP 或域名
sudo bash nyanlinkd/install.sh 203.0.113.10
```

脚本会创建服务账号、装到 `/opt/nyanlinkd`、生成 systemd 单元并启动，
最后跑一遍协议自测。装完记得在**防火墙和云厂商安全组**放行
`20100/tcp`（大厅）和 `20101/tcp`（中继）。

**方式 B：Docker**

```bash
cd NyanLink-Companion/nyanlinkd
HOST_OVERRIDE=203.0.113.10 docker compose up -d --build
```

**只想先试试（前台跑）**

```bash
python3 nyanlinkd/nyanlinkd.py --host-override 203.0.113.10
```

> `--host-override` 必须填**客户端能访问到的地址**。`/info` 会把它作为中继地址下发，
> 填错的话客户端能进大厅但连不上中继。

装好后浏览器打开 `http://<你的服务器>:20100/`，能看到实时看板（在线玩家、房间、事件流）。

### 二、客户端（Windows，每台机器都要做）

前置：游戏 **SDEZ 1.70** + **MelonLoader 0.6.4**。

1. **NyanLink 的客户端 mod**（本仓库不分发）
   从 [NyanLink Releases](https://github.com/MuNET-OSS/NyanLink/releases) 下载，
   把 `WorldLink.dll` 放进 `<游戏目录>\Mods\`。
   两台机器要用**同一个文件**（这一份是 50176 字节，md5 `9dfa62d5cba41deac0c2c74334ef8371`）。

2. **本仓库的 `WLDiag.dll`**

   仓库里已经带了编译好的（`client/WLDiag.dll`，v2.5，对应 SDEZ 1.70），
   直接拷进 `<游戏目录>\Mods\` 即可。想自己编就 `powershell -ExecutionPolicy Bypass -File tools\build_wldiag.ps1`。

   也可以让脚本一次做完（拷 DLL + 写 `WorldLink.toml`）：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\client\install-wldiag.ps1 `
       -GameDir "D:\game\maimai\SDEZ1.70\Package" `
       -LobbyUrl "http://203.0.113.10:20100"
   ```

3. **`WorldLink.toml`** 放在**游戏根目录**（和 `Sinmai.exe` 一层）：

   ```toml
   LobbyUrl="http://203.0.113.10:20100"
   Debug=false
   ```

4. **AquaMai 用户注意**：`AquaMai.toml` 里

   ```toml
   [GameSettings.ForceAsServer]
   Disabled = true
   ```

   （注释掉无效，必须显式写 `Disabled = true`。不关的话游戏认为"不在店内"，
   联机分类根本不会出现。）

5. **Test 模式**：按住 `F1` 进入 → `ゲーム設定` →
   `店内マッチングの設定` = **ON**，`グループ内基準機の設定` = **基準機**，然后重启游戏。

验收：`MelonLoader\Logs\Latest.log` 里应该出现

```
[NyanLink] WorldLink server address: <你的服务器>:20101
[WLDiag] v2.5 已加载（...）
[WLDiag] 挂钩成功: reinputConnectCombineData ...（共 7 行“挂钩成功”）
```

### 三、开始玩

1. 两边都**进到选曲界面**（别在标题界面就开招募）
2. 一方选好歌 → 发起招募
3. 另一方进分类栏最右边的「店内マッチング」（蓝色 `UI_CMN_TabTitle_NetworkBattle` 图标），
   或按「ジャンル」→ 右箭头切到最后一格
4. 等最多 10 秒，房间出现 → 选中 → 进房
5. 到准备页后，**两边各按一次 NEXT**

一份可以直接转发给搭子的简版说明在
[`docs/给朋友看-安装步骤.md`](docs/给朋友看-安装步骤.md)。

## 已知问题

- 刷卡登录时偶发 `PlInformationProcess.RestoreGhost` 崩溃：本体在 ghost 记录查不到曲目时
  有个分支没判空。跟本仓库的补丁无关，多启动几次能躲开，详见 `docs/联机排错记录.md`。
- 大厅没有配对机制：所有房间对所有人生效，加入是"先到先得"，一个房间满 2 人就不再收。
  多人同时挂着招募时，列表会混在一起（客户端是按"第几条"对应房间的）。

## 鸣谢

- [MuNET-OSS/NyanLink](https://github.com/MuNET-OSS/NyanLink) —— 协议与客户端 mod
- [MewoLab/worldlinkd](https://github.com/MewoLab/worldlinkd) —— NyanLink 的上游

## License

MIT（见 [LICENSE](LICENSE)）。本仓库只包含我们自己写的代码；NyanLink 及其上游的
代码与二进制遵循其各自的许可。
