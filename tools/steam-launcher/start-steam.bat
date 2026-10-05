@echo off
rem ---------------------------------------------------------------------------
rem Steam launch wrapper (ASCII only, so cmd can never mis-read it).
rem Differences from the original start.bat:
rem   1. inject is started exactly like the original start.bat does it
rem      ("start /min inject ..."), i.e. in its own minimized console -- running
rem      it inline in this (hidden) console can hang or fail
rem   2. afterwards it VERIFIES amdaemon.exe is alive and retries up to 5 times
rem      (a failed injection is exactly what shows up as a black screen)
rem   3. the game only starts once amdaemon is really up
rem   4. everything is logged to steam-launch.log
rem The original start.bat is untouched - delete this file to go back to it.
rem ---------------------------------------------------------------------------
set OPENSSL_ia32cap=:~0x20000000

pushd %~dp0

set LOG=steam-launch.log
echo ===== %DATE% %TIME% ===== >> %LOG%
echo cwd=%CD% >> %LOG%

set ATTEMPT=1
:retry
echo --- inject attempt %ATTEMPT% --- >> %LOG%
taskkill /f /im amdaemon.exe >> %LOG% 2>&1
start /min inject -d -k mai2hook.dll amdaemon.exe -f -c config_common.json config_server.json config_client.json

rem wait ~3s for amdaemon to settle (ping works as a sleep in a hidden window)
ping -n 4 127.0.0.1 >nul
tasklist /fi "imagename eq amdaemon.exe" /nh | find /i "amdaemon" >nul
if not errorlevel 1 goto amdok

echo amdaemon.exe NOT running after attempt %ATTEMPT% >> %LOG%
set /a ATTEMPT+=1
if %ATTEMPT% LEQ 5 goto retry

echo GAVE UP after 5 attempts >> %LOG%
goto launch

:amdok
echo amdaemon.exe is up (attempt %ATTEMPT%) >> %LOG%

:launch
echo --- starting sinmai.exe -monitor 2 --- >> %LOG%
sinmai.exe -monitor 2

taskkill /f /im amdaemon.exe >> %LOG% 2>&1
echo --- done --- >> %LOG%

popd
