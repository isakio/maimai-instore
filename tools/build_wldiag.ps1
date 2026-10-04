# Build WLDiag.dll with the csc.exe that ships with Windows.
# Usage:  powershell -ExecutionPolicy Bypass -File build_wldiag.ps1
# (English-only messages: Windows PowerShell 5.1 mis-reads non-BOM UTF-8 files.)

$ErrorActionPreference = "Stop"

$Game   = "D:\game\maimai\SDEZ1.70\Package"
$Csc    = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$Source = "C:\Users\isakio\nyanlinkd\WLDiag.cs"
$Out    = "$Game\Mods\WLDiag.dll"

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

$args = @("/target:library", "/out:$Out", "/nologo", "/optimize+")
foreach ($r in $refs) {
    if (-not (Test-Path $r)) { throw "missing reference: $r" }
    $args += "/reference:`"$r`""
}
$args += "`"$Source`""

# single line, avoids path/encoding surprises
$cmd = "& `"$Csc`" " + ($args -join " ")
Write-Host "Command:`n$cmd`n" -ForegroundColor Cyan
Invoke-Expression $cmd

if (Test-Path $Out) {
    Write-Host "`nBUILD OK -> $Out" -ForegroundColor Green
    Write-Host "Restart the game and look for [WLDiag] lines in MelonLoader\Logs\Latest.log" -ForegroundColor Green
} else {
    Write-Host "`nBUILD FAILED - see errors above." -ForegroundColor Red
}
