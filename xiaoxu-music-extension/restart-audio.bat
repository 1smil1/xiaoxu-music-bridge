@echo off
chcp 65001 >nul 2>&1
title xiaoxu-music-bridge GSMTC deadlock recovery
set "HOST_EXE=%~dp0xiaoxu-music-host.exe"
set "XIAOXU_RESTART_SCRIPT=%~f0"

echo.
echo  ========================================================
echo     xiaoxu-music-bridge  audio service recovery
echo  ========================================================
echo.
echo  This script restarts Windows Audio services to clear a
echo  GSMTC (media session API) deadlock. All playing audio
echo  (QQ Music, browsers, etc.) will mute for 1-3 seconds.
echo.
echo  You will be prompted for administrator privileges.
echo.
pause

:: Self-elevate to admin if not already
net session >nul 2>&1
if %errorLevel% neq 0 (
  echo  [INFO] Requesting administrator privileges...
  powershell -NoProfile -Command "Start-Process -FilePath $env:XIAOXU_RESTART_SCRIPT -Verb RunAs"
  exit /b
)

echo.
echo  [1/5] Stopping host processes...
taskkill /F /IM xiaoxu-music-host.exe /T 2>nul
if %errorLevel% equ 0 (
  echo         host stopped OK
) else (
  echo         no host process found
)

echo.
echo  [2/5] Stopping Windows Audio service (audiosrv)...
net stop audiosrv /y
if %errorLevel% neq 0 (
  echo         audiosrv stop FAILED - check permissions
  pause
  exit /b 1
)
echo         audiosrv stopped OK

echo.
echo  [3/5] Starting Windows Audio service...
net start audiosrv
if %errorLevel% neq 0 (
  echo         audiosrv start FAILED
  pause
  exit /b 1
)
echo         audiosrv started OK
timeout /t 2 /nobreak >nul

echo.
echo  [4/5] Restarting AudioEndpointBuilder...
net stop AudioEndpointBuilder /y >nul 2>&1
net start AudioEndpointBuilder
timeout /t 2 /nobreak >nul
echo         AudioEndpointBuilder restarted

echo.
echo  [5/5] Relaunching host...
if exist "%HOST_EXE%" (
  start "" "%HOST_EXE%" --server
  echo         host relaunched
) else (
  echo         [WARN] xiaoxu-music-host.exe not found next to this script
  echo         Start it manually after this script exits
)

echo.
echo  ========================================================
echo     Recovery complete
echo  ========================================================
echo.
echo  Chrome should reconnect to the host within 5 seconds.
echo  Open chrome://extensions and verify xiaoxu-music-bridge
echo  is connected, then refresh your wallpaper page.
echo.
echo  If music info still shows "未连接播放器" after 30 seconds:
echo    1. Restart QQ Music (or your media player)
echo    2. Refresh the wallpaper page
echo.
pause
