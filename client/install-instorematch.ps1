# InStoreMatch 安装脚本（Windows / PowerShell）
#
# 作用：
#   1. 把仓库里的 InStoreMatch.dll 复制到 <游戏目录>\Mods\
#   2. 生成 <游戏目录>\WorldLink.toml（默认指向 isakio.cn 的公共大厅）
#   3. 打印剩下需要手动做的 3 件事
#
# 用法（连公共大厅，最省事）：
#   powershell -ExecutionPolicy Bypass -File .\install-instorematch.ps1 `
#       -GameDir "D:\game\maimai\SDEZ1.70\Package"
#
# 想连自己搭的大厅，就加 -LobbyUrl：
#   powershell -ExecutionPolicy Bypass -File .\install-instorematch.ps1 `
#       -GameDir "D:\game\maimai\SDEZ1.70\Package" `
#       -LobbyUrl "http://你的服务器:20100"
#
# 注意：-GameDir 要指向 Sinmai.exe 所在的那一层（通常叫 Package）。

param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$LobbyUrl = "http://isakio.cn:20100"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path (Join-Path $GameDir "Sinmai.exe"))) {
    throw "-GameDir 里没有 Sinmai.exe：$GameDir`n要指向游戏根目录（Sinmai.exe 所在那层，一般叫 Package）"
}
if (-not (Test-Path (Join-Path $GameDir "MelonLoader"))) {
    throw "这个目录里没有 MelonLoader，请先装 MelonLoader 0.6.4"
}

$mods = Join-Path $GameDir "Mods"
New-Item -ItemType Directory -Force -Path $mods | Out-Null

# 两个 mod 一起装。WorldLink.dll 如果本来就有、且和仓库里这份不一样，先备份再覆盖 ——
# 联机双方必须用同一个文件（md5 一致），所以这里以仓库里的为准。
foreach ($name in @("WorldLink.dll", "InStoreMatch.dll")) {
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

$toml = @"
# 由 install-instorematch.ps1 生成
LobbyUrl="$LobbyUrl"
Debug=false
"@
Set-Content -Path (Join-Path $GameDir "WorldLink.toml") -Value $toml -Encoding ASCII
Write-Host "[OK] WorldLink.toml -> $GameDir\WorldLink.toml  (LobbyUrl=$LobbyUrl)" -ForegroundColor Green

Write-Host ""
Write-Host "mod 和配置都装好了。还需要手动做 2 件事：" -ForegroundColor Cyan
Write-Host "  1) 如果你装了 AquaMai：把 AquaMai.toml 里 [GameSettings.ForceAsServer] 改成"
Write-Host "     Disabled = true   （注释掉无效）"
Write-Host "  2) 进 Test 模式（按住 F1）→ ゲーム設定："
Write-Host "     店内マッチングの設定 = ON，グループ内基準機の設定 = 基準機"
Write-Host ""
Write-Host "（如果 $mods\ 里还留着本插件改名前的旧版本，手动删掉，免得同一套补丁打两遍）" -ForegroundColor Yellow
Write-Host ""
Write-Host "启动游戏后看 MelonLoader\Logs\Latest.log，应出现：" -ForegroundColor Cyan
Write-Host "  [InStoreMatch] v2.5 已加载 ...  和 7 行 [InStoreMatch] 挂钩成功"
Write-Host ""
Write-Host "（想自己搭大厅：仓库里 instorematchd/install.sh 一条命令就够，"
Write-Host "  然后把 -LobbyUrl 换成你自己的地址。）" -ForegroundColor DarkGray
