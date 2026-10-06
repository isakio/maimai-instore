# InStoreLink 客户端安装脚本（Windows / PowerShell）
#
# 作用：
#   1. 把 InStoreLink.dll（我们的联机 mod）和 InStoreMatch.dll（让分类栏出现「店内マッチング」）
#      复制到 <游戏目录>\Mods\
#   2. 如果 Mods\ 里还留着上游的 WorldLink.dll，把它改名停用（两个 mod 补丁目标重叠，不能共存）
#   3. 生成 <游戏目录>\InStoreLink.toml（默认指向 isakio.cn 的公共大厅）
#   4. 打印剩下需要手动做的 2 件事
#
# 用法（最省事：什么都不用给，脚本会自己找游戏目录）：
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
#
# 找不到的话可以直接把路径给它（游戏根目录 = Sinmai.exe 所在那一层，通常叫 Package）：
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 `
#       -GameDir "D:\game\maimai\SDEZ1.70\Package"
#
# 连自己搭的大厅就再加 -LobbyUrl：
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 `
#       -GameDir "<游戏根目录>" -LobbyUrl "http://你的服务器:20100"

param(
    [string]$GameDir = "",
    [string]$LobbyUrl = "http://isakio.cn:20100"
)

$ErrorActionPreference = "Stop"

function Test-GameDir([string]$path) {
    return [bool]($path -and (Test-Path (Join-Path $path "Sinmai.exe")))
}

# 没给 -GameDir 就自己找：
#   1) 当前目录往上 3 层里有没有；
#   2) 各盘符下名字像游戏目录的（game / maimai / SDEZ / Sinmai …）里限深 4 层找 Sinmai.exe。
if (-not (Test-GameDir $GameDir)) {
    $candidates = New-Object System.Collections.ArrayList
    $here = (Get-Location).Path
    for ($i = 0; $i -lt 3 -and $here; $i++) {
        [void]$candidates.Add($here)
        $here = Split-Path $here -Parent
    }
    foreach ($root in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue).Root) {
        foreach ($d in (Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
                        Where-Object { $_.Name -in @("game", "Game", "games", "maimai", "SDEZ", "Sinmai") })) {
            [void]$candidates.Add($d.FullName)
        }
    }

    foreach ($c in $candidates) { if (Test-GameDir $c) { $GameDir = $c; break } }

    if (-not $GameDir) {
        foreach ($c in $candidates) {
            Write-Host "  ...在 $c 里找 Sinmai.exe" -ForegroundColor DarkGray
            $hit = Get-ChildItem -Path $c -Filter "Sinmai.exe" -Recurse -Depth 4 -File -ErrorAction SilentlyContinue |
                   Select-Object -First 1
            if ($hit) { $GameDir = $hit.DirectoryName; break }
        }
    }
}

# 还找不到就手动给（可以直接把文件夹拖进窗口）
while (-not (Test-GameDir $GameDir)) {
    Write-Host "没自动找到游戏目录。" -ForegroundColor Yellow
    Write-Host "要找的是 Sinmai.exe 所在的那一层（通常叫 Package）。" -ForegroundColor Yellow
    $GameDir = (Read-Host "把路径粘贴进来，或直接把文件夹拖进这个窗口").Trim('"').Trim()
    if (-not $GameDir) { throw "没有提供游戏目录，已退出" }
}
Write-Host "[OK] 游戏目录: $GameDir" -ForegroundColor Green

if (-not (Test-Path (Join-Path $GameDir "MelonLoader"))) {
    throw "这个目录里没有 MelonLoader（$GameDir），请先装 MelonLoader 0.6.4"
}

$mods = Join-Path $GameDir "Mods"
New-Item -ItemType Directory -Force -Path $mods | Out-Null

# 上游那份 WorldLink.dll 必须停用：两边补丁目标重叠，同时存在会把补丁打两遍
$legacy = Join-Path $mods "WorldLink.dll"
if (Test-Path $legacy) {
    $disabled = "$legacy.disabled-" + (Get-Date -Format "yyyyMMdd")
    Move-Item $legacy $disabled -Force
    Write-Host "[..] 把上游的 WorldLink.dll 改名停用了 -> $disabled" -ForegroundColor Yellow
}

foreach ($name in @("InStoreLink.dll", "InStoreMatch.dll")) {
    $src = Join-Path $PSScriptRoot $name
    $dst = Join-Path $mods $name
    if (-not (Test-Path $src)) { throw "仓库里缺少 $name（应该在 $PSScriptRoot）" }
    if (Test-Path $dst) {
        $same = (Get-FileHash $src -Algorithm MD5).Hash -eq (Get-FileHash $dst -Algorithm MD5).Hash
        if (-not $same) {
            $bak = "$dst.bak-" + (Get-Date -Format "yyyyMMdd-HHmmss")
            Move-Item $dst $bak -Force
            Write-Host "[..] 原有的 $name 与仓库里这份不同，已备份成 $bak" -ForegroundColor Yellow
        }
    }
    Copy-Item $src $dst -Force
    Write-Host "[OK] $name -> $dst" -ForegroundColor Green
}

# 配置文件：**默认不覆盖已有的**（免得把用户自己填的大厅地址冲掉）。
# 只有显式给了 -LobbyUrl，或者文件还不存在时才会写。
$tomlPath = Join-Path $GameDir "InStoreLink.toml"
$explicitLobby = $PSBoundParameters.ContainsKey("LobbyUrl")
$legacyToml = Join-Path $GameDir "WorldLink.toml"

# 从老的 WorldLink.toml 里把大厅地址搬过来（用户以前改过的话别丢）
if ((-not (Test-Path $tomlPath)) -and (-not $explicitLobby) -and (Test-Path $legacyToml)) {
    $m = Select-String -Path $legacyToml -Pattern '^\s*LobbyUrl\s*=\s*"([^"]+)"' -ErrorAction SilentlyContinue |
         Select-Object -First 1
    if ($m) {
        $LobbyUrl = $m.Matches[0].Groups[1].Value
        Write-Host "[..] 从 WorldLink.toml 沿用了大厅地址：$LobbyUrl" -ForegroundColor Yellow
    }
}

if ((Test-Path $tomlPath) -and -not $explicitLobby) {
    Write-Host "[--] 已有 InStoreLink.toml，保持原样（要改大厅地址就加 -LobbyUrl 参数，或直接编辑它）" -ForegroundColor DarkGray
} else {
    $toml = @"
# 由 install.ps1 生成
LobbyUrl="$LobbyUrl"
Debug=false
"@
    # 注意用 UTF8：ASCII 会把中文注释写成 ?
    Set-Content -Path $tomlPath -Value $toml -Encoding UTF8
    Write-Host "[OK] InStoreLink.toml -> $tomlPath  (LobbyUrl=$LobbyUrl)" -ForegroundColor Green
    Write-Host "     （如果你以前用的是 WorldLink.toml，那个文件可以留着，新文件优先）" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "mod 和配置都装好了。还需要手动做 2 件事：" -ForegroundColor Cyan
Write-Host "  1) 如果你装了 AquaMai：把 AquaMai.toml 里 [GameSettings.ForceAsServer] 改成"
Write-Host "     Disabled = true   （注释掉无效）"
Write-Host "  2) 进 Test 模式（按住 F1）→ ゲーム設定："
Write-Host "     店内マッチングの設定 = ON，グループ内基準機の設定 = 基準機"
Write-Host ""
Write-Host "（如果 $mods\ 里还留着别的新旧同名插件，手动删掉，免得同一套补丁打两遍）" -ForegroundColor Yellow
Write-Host ""
Write-Host "启动游戏后看 MelonLoader\Logs\Latest.log，应出现：" -ForegroundColor Cyan
Write-Host '  [InStoreLink] ... 已加载   和   34 行 [InStoreLink]   ✓ 补丁名 → 目标方法'
Write-Host '  [InStoreLink] 挂钩完成，共 34 条生效'
Write-Host "  [InStoreMatch] v... 已加载  和 7 行 [InStoreMatch] 挂钩成功"
Write-Host '（出现 ✗ 或"挂钩失败"的话，多半是游戏版本不是 SDEZ 1.70）' -ForegroundColor Yellow
Write-Host ""
Write-Host "（想自己搭大厅：仓库里 instorematchd/install.sh 一条命令就够，"
Write-Host "  然后把 -LobbyUrl 换成你自己的地址。）" -ForegroundColor DarkGray
