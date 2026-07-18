@echo off
chcp 65001 >nul 2>&1
title xiaoxu-music-bridge 安装

echo.
echo  ========================================
echo     xiaoxu-music-bridge  安装
echo  ========================================
echo.
echo  注意：安装过程中会关闭所有 Chrome 窗口
echo  请先保存 Chrome 中的工作
echo.
pause
echo.

for %%I in ("%~dp0.") do set "INSTALL_DIR=%%~fI"

if not exist "%INSTALL_DIR%\xiaoxu-music-host.exe" (
    echo  [ERROR] xiaoxu-music-host.exe not found
    echo.
    pause
    exit /b 1
)
if not exist "%INSTALL_DIR%\extension\manifest.json" (
    echo  [ERROR] extension\manifest.json not found
    echo.
    pause
    exit /b 1
)
if not exist "%INSTALL_DIR%\EXTENSION_ID.txt" (
    echo  [ERROR] EXTENSION_ID.txt not found
    echo.
    pause
    exit /b 1
)

for %%F in (
    background.js
    host-bootstrap.js
    content.js
    reconnect-policy.js
    manifest.json
    icon16.png
    icon48.png
    icon128.png
) do (
    if not exist "%INSTALL_DIR%\extension\%%F" (
        echo  [ERROR] extension\%%F not found
        echo.
        pause
        exit /b 1
    )
)

for %%F in (
    D3DCompiler_47_cor3.dll
    PenImc_cor3.dll
    PresentationNative_cor3.dll
    vcruntime140_cor3.dll
    wpfgfx_cor3.dll
) do (
    if not exist "%INSTALL_DIR%\%%F" (
        echo  [ERROR] Required Host runtime sidecar not found: %%F
        echo.
        pause
        exit /b 1
    )
)

set /p EXT_ID= < "%INSTALL_DIR%\EXTENSION_ID.txt"
set "EXT_ID=%EXT_ID: =%"

if "%EXT_ID%"=="" (
    echo  [ERROR] EXTENSION_ID.txt is empty
    echo.
    pause
    exit /b 1
)

set "XIAOXU_INSTALL_DIR=%INSTALL_DIR%"
set "XIAOXU_EXTENSION_ID=%EXT_ID%"

echo  [1/5] Extension ID: %EXT_ID%
echo  [1/5] Install path: %INSTALL_DIR%
echo.

echo  [2/5] Killing existing host process...
taskkill /F /IM xiaoxu-music-host.exe >nul 2>&1
taskkill /F /IM xiaoxu-music-bridge.exe >nul 2>&1
echo  [2/5] OK
echo.

echo  [3/5] Generating xiaoxu_music_host.json ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$hostPath = [System.IO.Path]::Combine($env:XIAOXU_INSTALL_DIR, 'xiaoxu-music-host.exe'); $manifestPath = [System.IO.Path]::Combine($env:XIAOXU_INSTALL_DIR, 'xiaoxu_music_host.json'); $origin = 'chrome-extension://' + $env:XIAOXU_EXTENSION_ID + '/'; $j = @{name='xiaoxu_music_host';description='xiaoxu-music-bridge native messaging host';path=$hostPath;type='stdio';allowed_origins=@($origin)} | ConvertTo-Json -Compress; [System.IO.File]::WriteAllText($manifestPath, $j)" >nul 2>&1
if %errorlevel% neq 0 (
    echo  [3/5] FAILED - PowerShell error
    echo.
    pause
    exit /b 1
)

if not exist "%INSTALL_DIR%\xiaoxu_music_host.json" (
    echo  [3/5] FAILED - json file not created
    echo.
    pause
    exit /b 1
)
echo  [3/5] OK
echo.

echo  [4/5] Writing registry ...
reg add "HKCU\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host" /ve /t REG_SZ /d "%INSTALL_DIR%\xiaoxu_music_host.json" /f >nul 2>&1
reg add "HKLM\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host" /ve /t REG_SZ /d "%INSTALL_DIR%\xiaoxu_music_host.json" /f >nul 2>&1
echo  [4/5] OK (HKCU + HKLM)
echo.

echo  [4a/5] Registering persistent Host at user login ...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "xiaoxu-music-host" /t REG_SZ /d "\"%INSTALL_DIR%\xiaoxu-music-host.exe\" --server" /f >nul 2>&1
if %errorlevel% neq 0 (
    echo  [4a/5] FAILED - could not write HKCU Run entry
    echo.
    pause
    exit /b 1
)
start "" /B "%INSTALL_DIR%\xiaoxu-music-host.exe" --server
timeout /t 2 /nobreak >nul
echo  [4a/5] OK
echo.

echo  [4b/5] Syncing Chrome User Data JSON ...
rem Some older installs left a stale JSON in Chrome User Data NativeMessagingHosts
rem directory with the WRONG extension ID. Chrome does not always re-read HKCU on
rem hot-update, so we ALSO overwrite the User Data JSON to make sure both lookup
rem paths return the correct ID. Idempotent - safe to run repeatedly.
set "USERDATA_JSON=%LOCALAPPDATA%\Google\Chrome\User Data\NativeMessagingHosts\xiaoxu_music_host.json"
if not exist "%LOCALAPPDATA%\Google\Chrome\User Data\NativeMessagingHosts" (
    mkdir "%LOCALAPPDATA%\Google\Chrome\User Data\NativeMessagingHosts" >nul 2>&1
)
copy /Y "%INSTALL_DIR%\xiaoxu_music_host.json" "%USERDATA_JSON%" >nul 2>&1
if %errorlevel% neq 0 (
    echo  [4b/5] FAILED - could not write User Data JSON
) else (
    echo  [4b/5] OK - %USERDATA_JSON%
)
echo.

echo  [5/5] Closing Chrome ...
taskkill /F /IM chrome.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo.
echo  ========================================
echo     安装完成
echo  ========================================
echo.
echo  Chrome 正在重新启动...
echo  启动后请：
echo  1. 打开 chrome://extensions
echo  2. 开启开发者模式
echo  3. 点击"加载已解压的扩展程序"选择:
echo     %INSTALL_DIR%\extension
echo.
echo  扩展 ID 应为: %EXT_ID%
echo  (manifest.json 已内置 key，ID 在所有电脑上都固定为此值)
echo.

if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    start "" "C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    start "" "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else (
    echo  [WARN] 未找到 Chrome，请手动启动
)

echo.
echo  [6/5] Creating desktop shortcut for audio recovery...
set "RECOVERY_BAT=%INSTALL_DIR%\restart-audio.bat"
set "DESKTOP=%USERPROFILE%\Desktop"
set "SHORTCUT=%DESKTOP%\重启音频服务 (xiaoxu-music-bridge).lnk"
set "XIAOXU_RECOVERY_BAT=%RECOVERY_BAT%"
set "XIAOXU_SHORTCUT=%SHORTCUT%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut($env:XIAOXU_SHORTCUT); $s.TargetPath = $env:XIAOXU_RECOVERY_BAT; $s.WorkingDirectory = $env:XIAOXU_INSTALL_DIR; $s.WindowStyle = 1; $s.Description = 'Restart Windows Audio services to clear GSMTC deadlock'; $s.IconLocation = 'mmcbase.dll,1'; $s.Save()" >nul 2>&1
if exist "%SHORTCUT%" (
    echo  [6/5] OK - shortcut: %SHORTCUT%
) else (
    echo  [6/5] FAILED - shortcut not created
)
echo.

echo  按任意键关闭此窗口...
pause >nul
