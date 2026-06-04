@echo off
chcp 65001 >nul 2>&1
title xiaoxu-music-bridge

echo.
echo  ========================================
echo     xiaoxu-music-bridge  安装
echo  ========================================
echo.
echo  注意：安装过程中会关闭所有 Chrome 窗口
echo  请先保存 Chrome 中的工作
echo.
pause

set "INSTALL_DIR=%~dp0"
set "INSTALL_DIR=%INSTALL_DIR:~0,-1%"

if not exist "%INSTALL_DIR%\xiaoxu-music-host.exe" (
    echo  error: xiaoxu-music-host.exe not found
    pause
    exit /b 1
)
if not exist "%INSTALL_DIR%\extension\manifest.json" (
    echo  error: extension\manifest.json not found
    pause
    exit /b 1
)

set "EXT_ID_FILE=%INSTALL_DIR%\EXTENSION_ID.txt"
if not exist "%EXT_ID_FILE%" (
    echo  error: EXTENSION_ID.txt not found
    pause
    exit /b 1
)

set /p EXT_ID= < "%EXT_ID_FILE%"
set "EXT_ID=%EXT_ID: =%"

if "%EXT_ID%"=="" (
    echo  error: EXTENSION_ID.txt is empty
    pause
    exit /b 1
)

echo  Extension ID: %EXT_ID%
echo  Install path: %INSTALL_DIR%
echo.

:: Kill any existing host process
taskkill /F /IM xiaoxu-music-host.exe >nul 2>&1

:: Generate host manifest via a temp PS1 script (avoids inline escaping nightmares)
set "PS1_FILE=%INSTALL_DIR%\_gen_json.ps1"
(
echo $dir = '%INSTALL_DIR%'
echo $exe = Join-Path $dir 'xiaoxu-music-host.exe'
echo $id = '%EXT_ID%'
echo $obj = @{
echo     name = 'xiaoxu_music_host'
echo     path = $exe
echo     type = 'stdio'
echo     allowed_origins = @("chrome-extension://$id/")
echo }
echo $json = $obj | ConvertTo-Json -Compress
echo [System.IO.File]::WriteAllText((Join-Path $dir 'xiaoxu_music_host.json'^), $json)
) > "%PS1_FILE%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1_FILE%"
del /f /q "%PS1_FILE%" >nul 2>&1

if not exist "%INSTALL_DIR%\xiaoxu_music_host.json" (
    echo  error: failed to generate xiaoxu_music_host.json
    pause
    exit /b 1
)

:: Write registry (both HKCU and HKLM for reliability)
reg add "HKCU\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host" /ve /t REG_SZ /d "%INSTALL_DIR%\xiaoxu_music_host.json" /f >nul 2>&1
reg add "HKLM\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host" /ve /t REG_SZ /d "%INSTALL_DIR%\xiaoxu_music_host.json" /f >nul 2>&1

echo  [OK] 注册表已写入 (HKCU + HKLM)
echo  [OK] host manifest 已生成
echo.

:: Close Chrome to flush native messaging host cache
echo  正在关闭 Chrome ...
taskkill /F /IM chrome.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo.
echo  ========================================
echo     安装完成
echo  ========================================
echo.
echo  Chrome 正在重新启动，请稍等...
echo  启动后：
echo  1. 打开 chrome://extensions
echo  2. 开启开发者模式
echo  3. 点击"加载已解压的扩展程序"选择: %INSTALL_DIR%\extension
echo.

:: Restart Chrome — try common paths
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    start "" "C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    start "" "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else (
    echo  [warn] 未找到 Chrome，请手动启动 Chrome
)

pause
