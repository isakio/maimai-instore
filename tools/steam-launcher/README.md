# steam-launcher —— 把 maimai DX 放进 Steam 启动

目标：Steam 库里多一个「maimai DX」，点它就能进游戏，而且

- 全程**没有多余的黑框**（不像直接把 `start.bat` 加进 Steam 那样挂一个命令行窗口）
- Steam 全程显示"正在玩"，时长统计 / overlay / 手柄映射都正常
- 不动你原来的 `start.bat`

## 为什么不能直接把 Sinmai.exe 加进 Steam

`Sinmai.exe` 不是"单文件就能跑"的：它启动时必须

1. 有 `amdaemon.exe` 在跑（SEGA 的服务端守护进程，管网络/认证/IO/keychip）；
2. `amdaemon` 还得被 `inject.exe -d -k mai2hook.dll` 注入钩子，才会在非官方环境下工作；
3. 有 `OPENSSL_ia32cap=:~0x20000000` 这个环境变量（新 CPU 上绕过 OpenSSL 崩溃）。

这三件事全是 `start.bat` 干的 → 直接点 `Sinmai.exe` 只会**黑屏**（连不上"店内服务器"）。
而直接把 `start.bat` 加进 Steam，又会全程挂一个 cmd 窗口。所以在中间放一个**无窗口的小 exe**：

```
Steam ──► MaimaiSteam.exe（本目录编译出来的，GUI 子系统，无控制台）
            │  自己把整套启动流程做完，全程不经过 cmd / 不创建任何窗口：
            │  重复 3 轮：
            │    重复 5 次：
            │      inject.exe -d -k mai2hook.dll amdaemon.exe -f -c …
            │      （输出抓进 inject-out.txt；卡住 20 秒就杀掉）
            │      等 4 秒看 amdaemon.exe 是否活着 → 没起来就清干净再来
            │    amdaemon 起来 → Sinmai.exe -monitor 2
            │    15 秒后复查：游戏还在 + amdaemon 还在 → 判定成功，陪到游戏退出
            │    否则（= 黑屏那种）杀干净、进入下一轮
            └─ 游戏退出后 taskkill amdaemon.exe 收尾，然后自己退出
```

> 早期版本是"launcher 跑 bat、bat 里 inject"，实测**从 Steam 启动时 inject 会连续失败**
> （Steam 的 job 对象 + 控制台那一层），所以现在 inject 这一步由 exe 直接做，
> 既避开了那层，也能把 inject 的真实输出抓下来（`inject-out.txt`）。

## 踩过的坑（改这个之前先看一眼）

1. **`inject` 不能放在"隐藏窗口"的同一条控制台里直接跑**——实测会卡住或报
   `mai2hook.dll: DLL failed to load inside target process`，然后 amdaemon 起不来、游戏黑屏。
   必须像原版 `start.bat` 那样用 `start /min inject …`（另开一个最小化控制台）。
2. **别用 VBS/`wscript` 当入口**：Windows 11 已经把 VBScript 标记为弃用
   （事件日志里会出现 `VBScriptDeprecationAlert`），而且脚本宿主偶尔会报运行时错误弹框。
3. **日志文件别一直开着**：launcher 早期版本把 `steam-launch.log` 一直 hold 住，
   结果 bat 那边的 `>>` 写入全部静默失败（看起来像"bat 没跑"）。
   现在 launcher 写 `maimaiDX.log`、bat 写 `steam-launch.log`，互不干扰。
4. `inject` 偶发失败是这个环境的常态（原因没完全定位，和安全软件/时序都有关），
   所以这套东西的核心不是"找出原因"，而是**校验 + 重试**：没起来就重来。

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
（`start-steam.bat` 是这个 exe 早期版本用的启动脚本，现在 exe 不再需要它；
想只用 bat 的人可以自己留着用。）

仓库里附了一份编好的 `client/MaimaiSteam.exe`（**966144 字节 / md5 `371671448f64ddc5a99a318c55faaab3`**）。
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

## 日志与排查

| 文件（在 `Package\` 下） | 内容 |
| --- | --- |
| `maimaiDX.log` | launcher 视角：第几轮、游戏是否起来、活了多久、是否重试 |
| `steam-launch.log` | bat 视角：`inject` 第几次尝试、amdaemon 有没有起来、什么时候启动游戏 |

常见情况：

- game exited after **26s+** → 正常，游戏是自己退出的（你关的/打完退出）
- game exited after 几秒 + `that launch died too early, retrying` → 注入失败那只手，自动重试
- `the batch finished without starting the game (inject failed)` → 5 次注入全失败，
  这时候要看 `steam-launch.log` 里 inject 的报错，通常是安全软件在拦 DLL 注入

## 回退

删掉 `MaimaiSteam.exe` / `start-steam.bat`（以及两个 log），Steam 里改回直接指向
`start.bat`（会挂一个黑框，但能用），或者重新用你原来的启动方式 —— `start.bat` 从来没被改过。
