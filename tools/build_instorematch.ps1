# Build InStoreMatch.dll with the csc.exe that ships with Windows.
#
#   powershell -ExecutionPolicy Bypass -File build_instorematch.ps1 `
#       -Game "<game root, the folder that contains Sinmai.exe>"
#
# (English-only messages: Windows PowerShell 5.1 mis-reads non-BOM UTF-8 files.)

param(
    [string]$Game = "",
    [string]$Source = "",
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($Game)) {
    throw "-Game is required: the game root folder (the one that contains Sinmai.exe)"
}
$Csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if ([string]::IsNullOrEmpty($Source)) { $Source = Join-Path $PSScriptRoot "InStoreMatch.cs" }
if ([string]::IsNullOrEmpty($Out))    { $Out    = Join-Path $Game "Mods\InStoreMatch.dll" }

if (-not (Test-Path $Csc))    { throw "csc.exe not found: $Csc" }
if (-not (Test-Path $Source)) { throw "source not found: $Source" }
if (-not (Test-Path "$Game\Mods")) { throw "game Mods folder not found: $Game\Mods" }
if (-not (Test-Path "$Game\Sinmai_Data\Managed\Assembly-CSharp.dll")) {
    throw "Assembly-CSharp.dll not found - check the game path"
}

$refs = @(
    "$Game\MelonLoader\net35\MelonLoader.dll",
    "$Game\MelonLoader\net35\0Harmony.dll",
    "$Game\Sinmai_Data\Managed\Assembly-CSharp.dll",
    "$Game\Sinmai_Data\Managed\AMDaemon.NET.dll",
    "$Game\Sinmai_Data\Managed\UnityEngine.CoreModule.dll",
    "$Game\Sinmai_Data\Managed\UnityEngine.dll"
)

# 注意：变量别叫 $args —— 那是 PowerShell 的自动变量
$cscArgs = @("/target:library", "/out:$Out", "/nologo", "/optimize+")
foreach ($r in $refs) {
    if (-not (Test-Path $r)) { throw "missing reference: $r" }
    $cscArgs += "/reference:`"$r`""
}
$cscArgs += "`"$Source`""

# single line, avoids path/encoding surprises
$cmd = "& `"$Csc`" " + ($cscArgs -join " ")
Write-Host "Command:`n$cmd`n" -ForegroundColor Cyan
Invoke-Expression $cmd

if (Test-Path $Out) {
    Write-Host "`nBUILD OK -> $Out" -ForegroundColor Green
    Write-Host "Restart the game and look for [InStoreMatch] lines in MelonLoader\Logs\Latest.log" -ForegroundColor Green
} else {
    Write-Host "`nBUILD FAILED - see errors above." -ForegroundColor Red
}
