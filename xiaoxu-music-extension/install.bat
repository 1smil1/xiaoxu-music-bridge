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

set "INSTALL_DIR=%~dp0"
set "INSTALL_DIR=%INSTALL_DIR:~0,-1%"

:: Check files
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

set /p EXT_ID= < "%INSTALL_DIR%\EXTENSION_ID.txt"
set "EXT_ID=%EXT_ID: =%"

if "%EXT_ID%"=="" (
    echo  [ERROR] EXTENSION_ID.txt is empty
    echo.
    pause
    exit /b 1
)

echo  [1/4] Extension ID: %EXT_ID%
echo  [1/4] Install path: %INSTALL_DIR%
echo.

:: Step 1: Kill existing host process
echo  [2/5] Killing existing host process...
taskkill /F /IM xiaoxu-music-host.exe >nul 2>&1
echo  [2/5] OK
echo.

:: Step 2: Generate host manifest (ConvertTo-Json handles backslash escaping)
echo  [3/5] Generating xiaoxu_music_host.json ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$j = @{name='xiaoxu_music_host';description='xiaoxu-music-bridge native messaging host';path='%INSTALL_DIR%\xiaoxu-music-host.exe';type='stdio';allowed_origins=@('chrome-extension://%EXT_ID%/')} | ConvertTo-Json -Compress; [System.IO.File]::WriteAllText('%INSTALL_DIR%\xiaoxu_music_host.json', $j)" >nul 2>&1
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

:: Step 3: Write registry
echo  [4/5] Writing registry ...
reg add "HKCU\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host" /ve /t REG_SZ /d "%INSTALL_DIR%\xiaoxu_music_host.json" /f >nul 2>&1
reg add "HKLM\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host" /ve /t REG_SZ /d "%INSTALL_DIR%\xiaoxu_music_host.json" /f >nul 2>&1
echo  [4/5] OK (HKCU + HKLM)
echo.

:: Step 4: Create desktop shortcut to host exe
echo  [5/5] Creating desktop shortcut ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s = (New-Object -COM WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\xiaoxu-music-bridge.lnk'); $s.TargetPath = '%INSTALL_DIR%\xiaoxu-music-host.exe'; $s.WorkingDirectory = '%INSTALL_DIR%'; $s.IconLocation = '%INSTALL_DIR%\xiaoxu-music-host.exe,0'; $s.Description = 'xiaoxu-music-bridge - 双击启动，右键托盘可退出'; $s.Save()" >nul 2>&1
if %errorlevel% neq 0 (
    echo  [5/5] FAILED - PowerShell error
    echo.
    pause
    exit /b 1
)
echo  [5/5] OK (desktop shortcut created)
echo.

:: Step 4: Restart Chrome
echo  Closing Chrome ...
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

if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    start "" "C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    start "" "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) else (
    echo  [WARN] 未找到 Chrome，请手动启动
)

echo  按任意键关闭此窗口...
pause >nul
