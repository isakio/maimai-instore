# Build InStoreLink.dll with the csc.exe that ships with Windows.
#
#   powershell -ExecutionPolicy Bypass -File build_instorelink.ps1 `
#       -Game "<game root, the folder that contains Sinmai.exe>"
#
# 和 build_instorematch.ps1 同一套路：不需要 .NET SDK，
# 用 Windows 自带的 C# 5 编译器 + 游戏本身的 DLL 当引用。
#
# (English-only messages: Windows PowerShell 5.1 mis-reads non-BOM UTF-8 files.)

param(
    [string]$Game = "",
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($Game)) {
    throw "-Game is required: the game root folder (the one that contains Sinmai.exe)"
}
$Csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$SrcDir = Join-Path $PSScriptRoot "instorelink"
if ([string]::IsNullOrEmpty($Out)) { $Out = Join-Path $Game "Mods\InStoreLink.dll" }

if (-not (Test-Path $Csc)) { throw "csc.exe not found: $Csc" }
if (-not (Test-Path $SrcDir)) { throw "source folder not found: $SrcDir" }
if (-not (Test-Path "$Game\Sinmai_Data\Managed\Assembly-CSharp.dll")) {
    throw "Assembly-CSharp.dll not found - check the game path"
}

$refs = @(
    "$Game\MelonLoader\net35\MelonLoader.dll",
    "$Game\MelonLoader\net35\0Harmony.dll",
    "$Game\Sinmai_Data\Managed\Assembly-CSharp.dll",
    "$Game\Sinmai_Data\Managed\AMDaemon.NET.dll",
    "$Game\Sinmai_Data\Managed\UnityEngine.CoreModule.dll",
    "$Game\Sinmai_Data\Managed\UnityEngine.dll",
    "$Game\Sinmai_Data\Managed\UnityEngine.JSONSerializeModule.dll",
    "$Game\Sinmai_Data\Managed\UnityEngine.UI.dll",
    "$Game\Sinmai_Data\Managed\Unity.TextMeshPro.dll"
)

$cscArgs = @("/target:library", "/out:$Out", "/nologo", "/optimize+", "/langversion:5")
foreach ($r in $refs) {
    if (-not (Test-Path $r)) { throw "missing reference: $r" }
    $cscArgs += "/reference:`"$r`""
}
foreach ($f in (Get-ChildItem -Path $SrcDir -Filter *.cs | Sort-Object Name)) {
    $cscArgs += "`"$($f.FullName)`""
}

# single line, avoids path/encoding surprises
$cmd = "& `"$Csc`" " + ($cscArgs -join " ")
Write-Host "Command:`n$cmd`n" -ForegroundColor Cyan
Invoke-Expression $cmd

if (Test-Path $Out) {
    $len = (Get-Item $Out).Length
    Write-Host "`nBUILD OK -> $Out ($len bytes)" -ForegroundColor Green
    Write-Host "Restart the game and look for [InStoreLink] lines in MelonLoader\Logs\Latest.log" -ForegroundColor Green
    Write-Host "NOTE: WorldLink.dll must NOT be in Mods\ as well - the two mods would fight over the same hooks." -ForegroundColor Yellow
} else {
    Write-Host "`nBUILD FAILED - see errors above." -ForegroundColor Red
}
