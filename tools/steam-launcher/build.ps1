# Build MaimaiSteam.exe with MSVC (the cl.exe that comes with Visual Studio /
# Build Tools). No game files are needed - this is a plain Win32 program.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\steam-launcher\build.ps1
#
# Run it from a "x64 Native Tools Command Prompt for VS" (so cl.exe is on PATH),
# or pass -Csc if you only have the framework csc (not applicable here).
#
# (English-only messages: Windows PowerShell 5.1 mis-reads non-BOM UTF-8 files.)

param(
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"

$Root   = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Source = Join-Path $PSScriptRoot "launcher.cpp"
if ([string]::IsNullOrEmpty($Out)) { $Out = Join-Path $Root "client\MaimaiSteam.exe" }

if (-not (Test-Path $Source)) { throw "source not found: $Source" }
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    throw "cl.exe not found. Run this from 'x64 Native Tools Command Prompt for VS'."
}

Push-Location $PSScriptRoot
try {
    & cl.exe /nologo /std:c++17 /EHsc /O2 /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN `
        launcher.cpp "/Fe:$Out" /link /SUBSYSTEM:WINDOWS
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
finally {
    Remove-Item -ErrorAction SilentlyContinue *.obj, *.pdb
    Pop-Location
}

Write-Host "BUILD OK -> $Out" -ForegroundColor Green
Write-Host "Install: copy $Out and tools\steam-launcher\start-steam.bat next to Sinmai.exe," -ForegroundColor Cyan
Write-Host "         then add the exe as a non-Steam game (start-in = that folder)." -ForegroundColor Cyan
