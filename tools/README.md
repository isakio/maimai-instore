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

# 分析客户端 mod（从 NyanLink release 下载的 WorldLink.dll）：
IL_ASSEMBLY=/path/to/WorldLink.dll PYTHONPATH=/tmp/dntools python3 il.py dump rid:<rid>
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

### NyanLink 为什么看不到这一格（最终结论）

1. 玩家进入选曲界面时 `OnStartMusicSelect()` 已经按当时的 `_genreSelectDataList`
   拍好了 `_tabDatas`（那时招募数据还没来，198 不在里面，`_currentExtraCategoryCount=4`）。
2. 之后 mod 把 198 异步塞进 `_genreSelectDataList`，但没有任何代码重拍 `_tabDatas`。
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
# 在服务器上跑（先确认 nyanlinkd 在运行）
cd /opt/nyanlinkd
nohup python3 fake_player.py --server 127.0.0.1 --name 假朋友 --music-id 12054 --auto-accept > /tmp/fake.log 2>&1 &
tail -f /tmp/fake.log          # 看它在干什么
pkill -f fake_player.py        # 用完停掉
```

它会：注册到中继 → 每 15 秒刷新一次招募 → 自动接受对方的建流请求并把数据原样回传。

注意 `--keychip` 必须和真客户端的 keychip 不同（默认 `W8888888888` 够用），
`--music-id` 要选对方游戏里有的曲子。

## WLDiag.cs —— 选曲界面诊断/修复插件

针对「店内マッチング分类不显示」写的 MelonLoader 插件（v2）。它做三件事：

1. **诊断**：打印 `GenreSelectDataList` 每项 `categoryID`，以及底部标签栏真正的数据
   `SelectorTab._tabDatas`（项数 + 每项标题/是否有图标 + 左右格子数）
2. **修复 A**：一旦 `GenreSelectDataList` 里出现 198，就照游戏自己的方式重拍 `_tabDatas`
   （`build List<TabDataBase>` → `_genreTabController.SortType2Genre(list,0)` →
   `Change(CurrentCategorySelect)`）
3. **修复 B**：`CategoryNameList` 比 `GenreSelectDataList` 少一项时补上「店内マッチング」，
   否则 `CategoryScrollRight/Left` 的滚动边界够不到最后一格
4. **验证 C**：第一次对账通过后，把 `CurrentCategorySelect` 直接切到 198、再调
   `Monitor.SetDeployList(0,0)` 刷新 —— 屏幕应该立刻跳到「店内マッチング」那一格。
   这一步同时验证「屏幕上的标签栏到底是不是这个对象」。只做一次。

触发点：`reinputConnectCombineData` / `SetConnectData` 的 Postfix，外加 `OnUpdate` 里
每帧对一次账（项数对不上才动手，最多每 30 帧试一次）。

日志里会打 `CategoryNameList=` / `CurrentCategorySelect=` / `_tabDatas=` 三个数，
以及标签栏每格的标题（例：`… | TUVWXYZ[有图] | 数字・その他[有图] | 店内マッチング[有图]`）。

> **2026-10-04 实测通过**：分类栏出现了「店内マッチング」，能进去、能看到房间。
> 关键修正是 `SetDeployList` 的签名是 `(bool, bool)` —— 反射调用写死 `int` 会抛
> `Int32 cannot be converted to Boolean`，那样只会切分类、不刷新标签栏，
> 看起来就是「高亮偏半格、中间格子空白」。
>
> 版本备注：v2.1 加 `AutoJump` 开关 + 清洗分类名换行；v2.2 加 `ShowRightArrow` /
> `ShowBackButton`；v2.3 修正右箭头的边界（本体用了被减 1 的 count）；
> v2.4 连"按下没反应"也一起修（`GenreSelectSequence.Update` 里同样的 count-1，
> 做法是在这次 Update 期间临时借用 freedom 分支，Finalizer 负责还原）；
> **v2.5 把 `AutoJump` 默认改成 false**（招募出现不再自动跳分类，手动切即可）。
>
> 四个开关，改 `WLDiag.cs` 顶部重编即可：`AutoJump`（招募出现时自动切到店内联机）、
> `ShowGenreCard`（面板里的「店内マッチング」大卡片）、`ShowRightArrow`（面板右箭头）、
> `ShowBackButton`（联机分类里的 BACK）。

编译（用 Windows 自带的 csc，不需要装 SDK）：

```powershell
powershell -ExecutionPolicy Bypass -File .\build_wldiag.ps1 -Game "D:\game\maimai\SDEZ1.70\Package"
```

`-Game` 指向游戏根目录（`Sinmai.exe` 那一层）；源码默认读脚本旁边的 `WLDiag.cs`，
产物默认写到 `<Game>\Mods\WLDiag.dll`。文件带 UTF-8 BOM，中文日志才不会乱码。

编译产物会自动放到 `游戏目录\Mods\WLDiag.dll`。重启游戏后立刻生效，
不想要了直接删掉那个 dll（还有一个 `WLDiag.dll` 同名文件不需要保留其它东西）。
