# maimai 工具

## il.py —— 游戏程序集 IL 分析

用来看 maimai 本体（`Assembly-CSharp.dll`）里某个方法是怎么实现的。

```bash
python3 -m pip install --break-system-packages --target=/tmp/dntools dnfile dncil

PYTHONPATH=/tmp/dntools python3 il.py list <关键词>       # 找方法（输出 rid）
PYTHONPATH=/tmp/dntools python3 il.py callers <方法名>    # 谁调用了它
PYTHONPATH=/tmp/dntools python3 il.py dump rid:<rid> [起] [止]   # 反汇编
PYTHONPATH=/tmp/dntools python3 il.py type <rid>          # 方法属于哪个类
PYTHONPATH=/tmp/dntools python3 il.py methods <类名关键词>  # 列类的方法
PYTHONPATH=/tmp/dntools python3 il.py fields <类名关键词>   # 列类的字段

# 分析客户端 mod（仓库里的 client/WorldLink.dll，NyanLink 官方构建）：
IL_ASSEMBLY=../client/WorldLink.dll PYTHONPATH=/tmp/dntools python3 il.py dump rid:<rid>
```

`rid:` 形式用来精确指定同名方法（比如一堆类都有 `OnStart`）。
`IL_ASSEMBLY` 环境变量可以换目标程序集（默认是游戏本体的 `Assembly-CSharp.dll`）。

## 已查清的关键结论

### 分类栏的「第三份数据」找到了：`.SelectorTab._tabDatas`

底部那一排分类标签的层级是：

```
Monitor.MusicSelectMonitor._genreTabController   (.GenreSelectController : .TabController)
  → .TabController._tab                          (.SelectorTab)
      → .SelectorTab._tabDatas                   List<TabDataBase>   ← 真正画格子的数据
      → .SelectorTab._leftPanels / _rightPanels / _main   固定数量的格子
```

- `_tabDatas` **只在 `MusicSelectMonitor.OnStartMusicSelect()` 里生成一次**（快照）：
  ```csharp
  var list = new List<TabDataBase>();
  foreach (var d in MusicSelectProcess.GenreSelectDataList[monitor])
      list.Add(new TabDataBase(getTabColor(d), GetTabSprite(d), getTabString(d), ""));
  _genreTabController.SortType2Genre(list, 0);      // -> .TabController.Set -> SetData
  _genreTabController.Change(CurrentCategorySelect);
  ```
  它**不读** `CategoryNameList`，也**不实时读** `_genreSelectDataList`。
- `.SelectorTab.UpdateTab(index)` 只是个环形轮播：左右格子填 `index±1±2…`，中间格子填
  `_tabDatas[index]`。所以 `_tabDatas` 里没有的项，滚到天边也不会出现。
- `GenreSelectDataList`（= `_genreSelectDataList`）每项是 `.GenreSelectData`
  （字段 `categoryID` / `isExtra` / `isTournament` / `genreCategoryColor` …）。
  额外分类排在**最前面**（下标 0..`_currentExtraCategoryCount-1`），基础分类随后。
- **滚动边界用的是 `CategoryNameList.Count`**，不是 `_genreSelectDataList.Count`：
  `Process.SubSequence.MusicSelectSequence.CategoryScrollRight/Left` 里
  `CurrentCategorySelect+1 >= CategoryNameList.Count` 就绕回 0。
- `ExtraMusic()` 在「额外分类」那一段的末尾有一个条件块：
  `if (_connectCombineMusicDataList.Count > 0)` → 把 198 加进 `_genreSelectDataList`、
  把 `IsConnectCategoryEnable = true`；紧接着 `ExtraCategoryName()` 再把
  `GetMusicGenre(198).genreName` **Add 到 `CategoryNameList` 末尾**。
  也就是说：**名字、数据、计数三样是一起更新的**。
- `reinputConnectCombineData()`（`PartyExec` 在 `IsConnectStart`/`IsConnectStop` 时调用）
  只做了「数据」那一步（往 `_genreSelectDataList` / `_combineMusicDataList` 塞 198），
  **不补 `CategoryNameList`、不重拍 `_tabDatas`**。
- `GenreSelectChainList`（`_genreChainList`）是按下「ジャンル」后出现的大卡片面板，
  不是底部标签栏；它内部会通过 `GetGenreSelectData(monitor, 相对下标)` 读同一份
  `_genreSelectDataList`。
- `genre 198` 数据（`Sinmai_Data/StreamingAssets/A000/musicGenre/musicgenre000198/MusicGenre.xml`）：
  `genreName` = `genreNameTwoLine` = `店内マッチング`，
  `FileName` = `UI_CMN_TabTitle_NetworkBattle`，Color = RGB(67,89,255)，`disable=false`。

### 为什么这一格一开始画不出来（最终结论）

1. 玩家进入选曲界面时 `OnStartMusicSelect()` 已经按当时的 `_genreSelectDataList`
   拍好了 `_tabDatas`（那时招募数据还没来，198 不在里面，`_currentExtraCategoryCount=4`）。
2. 之后 198 才异步进到 `_genreSelectDataList`，而没有任何代码重拍 `_tabDatas`。
3. 而且 `CategoryNameList` 也没补，导致 `CategoryNameList.Count` 比
   `_genreSelectDataList.Count` 少 1，**滚动边界**还够不到最后一格。

两个都要补：名字（滚动边界）+ 标签栏快照（`_tabDatas`）。

### 其他结论

- `MusicSelectProcess.IsConnectCategoryEnable` 在 SDEZ 1.70 里**没有任何读取方**
  （`get_` 全asm 无人调用），所以它是个死标志；之前强行置 true 不会有任何效果。
- 分类标签的图标资源是 `UI_CMN_TabTitle_NetworkBattle`，`_genreSprite` 由
  `MusicSelectMonitor.Initialize` 遍历 `Manager.MaiStudio.MusicGenreName.Table`
  枚举逐个 `GetTabTitleSprite` 建出来 —— 198 在这个枚举里，所以图标本来就齐。

## fake_player.py —— 假玩家（不用等朋友也能测）

在服务端模拟一个正在招募的玩家，用来单独测试客户端。

```bash
# 启动：任何能连到大厅的机器都行（它就是另一个客户端，跑的机器不影响联机）
python3 tools/fake_player.py --server isakio.cn --name 假朋友 --music-id 12054 --auto-accept

# 想在后台挂着、同时看日志：
nohup python3 tools/fake_player.py --server isakio.cn --name 假朋友 \
    --music-id 12054 --auto-accept > /tmp/fake.log 2>&1 &
tail -f /tmp/fake.log

# 关闭：
pkill -f fake_player.py
```

`--server` 也可以是 `127.0.0.1`（和服务端同一台机器）或者你自己大厅的地址。
注意 `install.sh` 只把服务端那几个文件装到 `/opt/instorematchd`，**不含** `fake_player.py`——
想在服务器上跑就先 `scp tools/fake_player.py <服务器>:/tmp/` 再执行。
启动后用 `curl -s http://<大厅>:20100/online` 能看到 `totalUsers` 多了一个。

它会：注册到中继 → 每 15 秒刷新一次招募 → 自动接受对方的建流请求并把数据原样回传。

注意 `--keychip` 必须和真客户端的 keychip 不同（默认 `W8888888888` 够用），
`--music-id` 要选对方游戏里有的曲子。

## instorelink/ —— 联机本体（客户端 mod）

把游戏本体的「局域网 party」接到公网中继上的那个 mod。**它替代了上游 NyanLink 的
`WorldLink.dll`**，协议逐字节兼容（所以两边一家用我们这份、一家用上游那份也能连），
但代码是我们自己写的：零第三方依赖、能直接用 Windows 自带的 `csc.exe` 编（C# 5）。

完整的协议规格、上游源码导读、与游戏本体的 33 个耦合点、踩过的坑，都在
[`docs/客户端mod实现.md`](../docs/客户端mod实现.md)。这里只说怎么改、怎么测。

| 文件 | 干什么 |
| --- | --- |
| `LinkProtocol.cs` | 线协议编解码（17 字段）、命令号、伪 IP 算法。**不依赖 Unity/游戏，能单独抽出来单测** |
| `LinkConfig.cs` | 读 `InStoreLink.toml`（也认老的 `WorldLink.toml`），30 行的极简 TOML |
| `LinkClient.cs` | 中继 TCP 连接：注册、心跳、收发线程、四个队列、重连、延迟统计 |
| `LinkSocket.cs` | 影子 socket：把游戏 `NFSocket` 的每个调用转成中继消息 |
| `LinkLobby.cs` | 大厅 HTTP（`/info` `/online` `/recruit/list` `/recruit/start|finish`） |
| `LinkRuntime.cs` | 全局状态：配置、连接、招募列表轮询、"新房间排队等主线程" |
| `PatchesNet.cs` | 通信层 26 个补丁（NFSocket 全量、开房走 HTTP、关加解密、跳过联网自检、状态显示） |
| `PatchesParty.cs` | 招募/选曲 7 个补丁（拉列表、判断能否进房、重画联机歌曲列表） |
| `LinkPatching.cs` | **逐条挂补丁**：哪条挂不上会点名，不会因为一条失败就整组不挂 |
| `LinkMod.cs` | MelonLoader 入口：读配置 → 挂补丁 → 退出时收尾 |
| `LinkLog.cs` | 日志（Debug 开关） |

编译（Windows，和 InStoreMatch 同一套路，不需要 .NET SDK）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build_instorelink.ps1 `
    -Game "D:\game\maimai\SDEZ1.70\Package"
# 产物：<游戏目录>\Mods\InStoreLink.dll
```

在 WSL 里编（借用 Windows 的 csc.exe）：

```bash
bash tools/build_wsl.sh [游戏目录]           # 产物 build/InStoreLink.dll
```

改完之后**先跑测试再进游戏**（游戏里的错误要重启一次游戏才能看到，很费时间）：

```bash
bash tests/run_all.sh          # 6 步：编译 / 协议单测 / 向量 / 端到端 / 兼容探针 / 参数名
bash tests/run_param_check.sh  # 只查补丁参数名（秒级，改完补丁先跑这个）
```

### 改这个 mod 最容易踩的两个坑

1. **Harmony 是按参数名给补丁传参的**（只有 `__instance` / `__result` / `___字段` 是特殊名字）。
   把游戏里的 `NFSocket(Socket nfSocket)` 写成 `socket`，编译零提示，运行时抛
   `Parameter "socket" not found ...`，而 Harmony 只报一句
   `IL Compile Error (unknown location)` —— 所以有 `tools/check_patch_params.cs` 自动查。
2. **注入的私有字段类型必须一致**（`___字段名` 写法），比如
   `MusicSelectProcess._currentPlayerSubSequence` 的元素类型是它自己嵌套的 `SubSequence`，
   不是 `Process.SubSequence`。`tests/GameCompatProbe.cs` 会把这些字段的真实类型打出来。

配套的小工具：

| 工具 | 用途 |
| --- | --- |
| `check_patch_params.cs` | 补丁参数名 / 目标方法逐个对照（`tests/run_param_check.sh` 用它） |
| `find_type.py` | 在 `Assembly-CSharp.dll` 里按名字查类型落哪个命名空间 |
| `dump_sigs.cs` | 用 Mono.Cecil 打印某个 dll 里方法的真实签名（对比上游发布版用） |
| `il.py` | 反汇编 `Assembly-CSharp.dll`（老工具，看方法实现用） |

## InStoreMatch.cs —— 「店内マッチング」客户端插件

MelonLoader + Harmony 插件（C# 5 语法，Windows 自带 csc 就能编）。
装法看仓库根目录的 [README](../README.md)，这里说它内部干什么。

**它要处理的机制**：选曲界面底部那排分类标签画的是 `.SelectorTab._tabDatas`，
**只在进入界面时拍一次快照**；联机的 198 号分类（店内マッチング）是之后才随着
大厅数据出现的 —— 快照不会重拍，那一格就画不出来。

**它做的四件事**：

| # | 做什么 | 挂在哪 |
| --- | --- | --- |
| 1 | 检测到 198 出现后，用游戏自己的方式重拍标签栏（`List<TabDataBase>` → `SortType2Genre` → `Change`） | `reinputConnectCombineData` / `SetConnectData` 的 Postfix，外加 `OnUpdate` 每 30 帧对一次账 |
| 2 | 把「店内マッチング」补进 `CategoryNameList`（滚动边界用的是它的 `Count`，不补够不到最后一格） | 同上 |
| 3 | 让「ジャンル」面板也画出那一格的大卡片（本体按分类名把它删掉） | `MusicSelectProcess.GetCategoryName` 的 Postfix（返回值缀一个零宽空格） |
| 4 | 「ジャンル」面板的右箭头、联机分类里的 BACK（本体都写死不给） | `GenreSelectSequence.CheckButton` / `.Update`、`MusicSelectSequence.IsBackEnable` |

**四个开关**（改 `InStoreMatch.cs` 顶部，重编生效）：

| 开关 | 默认 | 作用 |
| --- | --- | --- |
| `AutoJump` | `false` | 招募出现时自动把分类切到「店内マッチング」 |
| `ShowGenreCard` | `true` | 「ジャンル」面板里显示那一格的大卡片 |
| `ShowRightArrow` | `true` | 面板站在倒数第二格也能往右切到最后 |
| `ShowBackButton` | `true` | 联机分类里放回 BACK 按钮 |

**诊断输出**：默认每 30 秒打一次状态（`CategoryNameList=` / `CurrentCategorySelect=` /
`_tabDatas=`，以及标签栏每格实际显示的文字），日志前缀 `[InStoreMatch]`。

**踩过的坑**（想改代码先看一眼）：

- `Monitor.SetDeployList` 的签名是 **`(bool, bool)`** —— 反射调用写死 `int` 会抛
  `Int32 cannot be converted to Boolean`，结果只切分类、不刷新标签栏
  （现象：高亮偏半格、中间格子空白）
- 本体在"非 freedom 模式"下会把分类数减 1（`count--`），**显示**（`CheckButton`）和
  **按下后的响应**（`Update`）是两套独立判断，要一起改才不会"有箭头但点不动"
- 分类名里带换行（`ゲーム＆\nバラエティ`），日志不清洗会被劈成好几行

编译（用 Windows 自带的 csc，不需要装 SDK）：

```powershell
powershell -ExecutionPolicy Bypass -File .\build_instorematch.ps1 -Game "<游戏根目录>"
```

`-Game` 指向游戏根目录（`Sinmai.exe` 那一层）；源码默认读脚本旁边的 `InStoreMatch.cs`，
产物默认写到 `<Game>\Mods\InStoreMatch.dll`。文件带 UTF-8 BOM，中文日志才不会乱码。

编译产物会直接写进 `游戏目录\Mods\InStoreMatch.dll`，重启游戏立刻生效；
不想要了把那个 dll 删掉即可（`Mods\` 里同时存在新旧两份插件会导致补丁打两遍，
换版本时记得先删旧的）。

### 发新版本

1. 改 `InStoreMatch.cs` 顶部的版本号（`[assembly: MelonInfo(...)]` 第三个参数）和开关
2. 编译：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build_instorematch.ps1 -Game "<游戏目录>"
   ```

3. 把产物拷回仓库并提交：

   ```bash
   cp "<游戏目录>/Mods/InStoreMatch.dll" client/InStoreMatch.dll
   git commit -am "InStoreMatch vX.Y" && git push
   ```

4. 打 Release（把 dll 挂上去，README / 文档里的版本号记得一起改）：

   ```bash
   gh release create vX.Y client/InStoreMatch.dll --title "InStoreMatch vX.Y" --notes "..."
   ```
