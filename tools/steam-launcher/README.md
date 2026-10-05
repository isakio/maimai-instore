# steam-launcher —— 把 maimai DX 放进 Steam 启动

目标：Steam 库里多一个「maimai DX」，点它就能进游戏，而且

- 全程**没有多余的黑框**（不像直接把 `start.bat` 加进 Steam 那样挂一个命令行窗口）
- Steam 全程显示"正在玩"，时长统计正常
- 不用改 Steam 的任何设置
- 不动你原来的 `start.bat`

## 为什么不能直接把 Sinmai.exe 加进 Steam

`Sinmai.exe` 不是"单文件就能跑"的：它启动时必须

1. 有 `amdaemon.exe` 在跑（SEGA 的服务端守护进程，管网络/认证/IO/keychip）；
2. `amdaemon` 还得被 `inject.exe -d -k mai2hook.dll` 注入钩子，才会在非官方环境下工作；
3. 有 `OPENSSL_ia32cap=:~0x20000000` 这个环境变量（新 CPU 上绕过 OpenSSL 崩溃）。

这三件事全是 `start.bat` 干的 → 直接点 `Sinmai.exe` 只会**黑屏**（连不上"店内服务器"）。
而直接把 `start.bat` 加进 Steam，又会全程挂一个 cmd 窗口。所以在中间放一个
**无窗口的小 exe**（GUI 子系统，不经过 cmd、不创建任何窗口）：

```
Steam ──► MaimaiSteam.exe（Steam 只跟踪它 → "正在玩" / 时长统计）
            │
            ├ 若发现自己被 Steam 拉起来了（或 Steam 的 overlay 已注进自己）：
            │    把"注入 amdaemon"交给计划任务跑（见下一节），那条链完全不在 Steam 的进程树里
            │      MaimaiSteam.exe --inject-only
            │        └ inject.exe -d -k mai2hook.dll amdaemon.exe -f -c …
            │
            ├ 等 amdaemon 真的起来（helper 用 _maimai-steam-inject.txt 回报，最多 35 秒）
            ├ Sinmai.exe -monitor 2
            ├ 15 秒后复查：游戏还在 + amdaemon 还在 → 判定成功，陪到游戏退出
            │  （否则杀干净、重来一轮，最多 2 轮）
            └ 游戏退出后 taskkill amdaemon.exe，删掉计划任务，然后自己退出
```

## 关键结论：Steam 的 overlay 会把注入搞坏（根因）

现象是：**从 Steam 启动时，放 mai2hook.dll 进 amdaemon 一次都没成功过**

```
inject-out.txt:  mai2hook.dll: DLL failed to load inside target process
```

而**同一份 exe**、同一台机器，从资源管理器/WSL 手动跑，1 秒内就成功。查下来的结论：

1. **Steam 会把它的 overlay 注进"它启动的那棵进程树"里的每一个进程。**
   `D:\game\steam\logs\gameoverlay_renderer.txt` 里留着实锤 —— 连我们随手
   `taskkill.exe` 一下都被注进去了：

   ```
   Mon Oct 05 07:58:32 2026 UTC - Current process: taskkill.exe
   Mon Oct 05 07:58:32 2026 UTC - Module file name: D:\game\steam\gameoverlayrenderer64.dll
   ```

2. `amdaemon.exe` 是 `inject.exe` 在**那棵树里面**创建的，创建的一瞬间
   `gameoverlayrenderer64.dll` 就被塞进去了 —— 和 inject 的远程 `LoadLibrary`
   撞在一起，于是 `LoadLibrary(mai2hook.dll)` 返回失败、amdaemon 立刻退出 →
   游戏黑屏（就是你看到的那句报错）。

3. 已经排除的原因（都实测过，别再重复验证）：

   | 怀疑 | 实测结果 |
   | --- | --- |
   | Steam 的 job 对象限制 | `CREATE_BREAKAWAY_FROM_JOB` 明确生效（子进程已在 job 外），照样失败 |
   | 提权差异 | 两种情况下 `TokenIsElevated` 都是 no |
   | 兼容性 shim | `__COMPAT_LAYER` 都是 unset |
   | `SteamAppId` 等环境变量 | 清掉照样失败；手动把同样的变量喂给 launcher 照样成功 |
   | 工作目录 / PATH / Y: 盘 | 两份日志完全一致 |

## 怎么解决

**把"注入"这一步挪出 Steam 的进程树。** launcher 检测到自己是被 Steam 拉起来的
（`SteamAppId` / `SteamClientLaunch` 有值）或 Steam 的 overlay 已经在本进程里，
就用 `schtasks` 起一个 `MaimaiSteam.exe --inject-only`：

```
schtasks /create /tn MaimaiSteamInject /tr "\"<游戏目录>\MaimaiSteam.exe\" --inject-only" /sc once /st 00:00 /f
schtasks /run    /tn MaimaiSteamInject
```

计划任务的父进程是 `svchost`（任务计划服务），**不是** Steam 跟踪的任何进程，
所以 overlay 不会进去，amdaemon 就是干净的 —— 和手动跑 `start.bat` 一模一样。
helper 干完活会在游戏目录写一行 `_maimai-steam-inject.txt`（`ok` / `failed`），
launcher 收到 `ok` 才启动游戏，用完把计划任务删掉。

### 另一条同样有效的路（可选）

把这个快捷方式的 Steam 覆盖层关掉也能解决（Steam 就不会往树里注入）：
**库里右键 `maimai DX` → 属性 → 常规 → 取消勾选「启用 Steam 界面」**。
launcher 在"有 overlay"和"没有 overlay"两种情况下都能正常工作，所以这一步可选；
真机验证时它是我们定位根因用的对照组。

## 编译

**A. Windows + Visual Studio 的 cl.exe**（和你编 mod 一样的那套）

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\steam-launcher\build.ps1
# 产物：client\MaimaiSteam.exe
```

**B. WSL 里用 zig 交叉编译**（不需要装 VS；脚本会用 `ZIG` 环境变量或 `/tmp/zig/zig`）

```bash
ZIG=/tmp/zig/zig bash tools/steam-launcher/build_wsl.sh
# 产物：client/MaimaiSteam.exe
```

zig 可以从 <https://ziglang.org/download/> 下个 linux x86_64 包解压即用（约 50MB）。

## 安装

把这个 exe 放到**和 `Sinmai.exe` 同一层**（也就是 `Package\`）：

```
<游戏目录>\
├── Sinmai.exe
├── start.bat            ← 你原来的，不动它
├── MaimaiSteam.exe      ← 本目录编译出来的（Steam 里加它）
└── ...
```

`client/install.ps1` 会顺手把它拷过去。

仓库里附了一份编好的 `client/MaimaiSteam.exe`（**976896 字节 / md5 `ce6742d034300b867841a90c5ab06c3e`**）。
用上面任一方式重编后 md5 会变，这是正常的 —— 编译器会在产物里写时间戳，
判断"是不是同一份"看行为（或看 `MaimaiSteam.exe` 旁的源码）而不是 md5。

## 加进 Steam

库 → 添加非 Steam 游戏 → 浏览选 `MaimaiSteam.exe`，然后在属性里确认：

| 字段 | 值 |
| --- | --- |
| 目标 | `...\Package\MaimaiSteam.exe` |
| 启动选项 | *（留空）* |
| 起始位置 | `...\Package` |

图标随便挑（`Sinmai.exe` 那个就行，只是用来抽图标，不会执行它）。

**注意**：非 Steam 游戏不能用 `steam://rungameid/…` 或 `steam -applaunch` 这种命令行触发，
只能在库里点「开始」（实测：Steam 只记录 URL、不启动）。

## 日志与排查

| 文件（在 `Package\` 下） | 内容 |
| --- | --- |
| `maimaiDX.log` | launcher 视角：上下文（提权/Steam 环境变量/overlay 有没有在本进程里）、走了哪条路、amdaemon 有没有起来、游戏活了多久、是否重试；失败时还会把 `inject-out.txt` 的内容抄进来 |
| `inject-out.txt` | `inject` 自己的输出（失败时这里有原话） |
| `_maimai-steam-inject.txt` | 计划任务 helper 的回报（`ok` / `failed`），正常情况下跑完就删 |

一份**成功**的 Steam 启动长这样：

```
launcher start [started by Steam] [Steam overlay DLL is inside this process]
Steam is in this process tree -> injection goes to the Task Scheduler
detach: injection handed to the Task Scheduler (outside Steam's process tree)
[helper] attempt 1: amdaemon.exe is up
detached injection: amdaemon.exe is up
round 1: started Sinmai.exe -monitor 2
15s check: game=up amdaemon=up
launch looks healthy, waiting for the game to exit
```

常见情况：

- `game exited after 26s+` → 正常，游戏是自己退出的（你关的/打完退出）
- `round N: launch failed (this is the black-screen case), retrying` → 注入没成，自动重试
- 最后弹错误框 + 日志里有 `inject says: mai2hook.dll: DLL failed to load inside target process`
  → 注入又被别的东西干扰了（杀软 / 另一个注入器 / Steam overlay 还在）。
  先把这条快捷方式的覆盖层关掉再试（见上面「另一条同样有效的路」），
  并把 `maimaiDX.log` + `inject-out.txt` 发出来。
- 计划任务建不起来（日志里 `detach: schtasks /create failed`）→ launcher 会自动退回"在树里直接注入"，
  行为跟旧版一样。

### 排查用的小开关（一般用不到）

| 参数 | 作用 |
| --- | --- |
| `--inject-only` | 只做注入（计划任务用的就是这个） |
| `--force-detach` | 强制走计划任务那条路，方便手动验证 |
| `--no-detach` | 强制留在 Steam 进程树里直接注入（复现老问题时用） |

## 回退

删掉 `MaimaiSteam.exe` / `maimaiDX.log` / `inject-out.txt`，Steam 里改回直接指向
`start.bat`（会挂一个黑框，但能用），或者重新用你原来的启动方式 —— `start.bat` 从来没被改过。
