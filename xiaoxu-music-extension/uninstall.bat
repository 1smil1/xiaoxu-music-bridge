@echo off
chcp 65001 >nul 2>&1
title xiaoxu-music-bridge 卸载
color 0C

echo.
echo  ========================================
echo     xiaoxu-music-bridge  卸载
echo  ========================================
echo.
echo  即将删除以下内容：
echo    1. 注册表中的桥接器配置 (HKCU + HKLM)
echo    2. 已知的桥接器程序、运行库、扩展和快捷方式
echo.
echo  请先在 chrome://extensions 中手动移除 xiaoxu-music-bridge 扩展，
echo  然后确认卸载。
echo.

set /p confirm=确认卸载吗 (输入 Y 确认):
if /i not "%confirm%"=="Y" (
    echo  已取消。
    pause
    exit /b
)

:: Kill host process
taskkill /F /IM xiaoxu-music-host.exe >nul 2>&1

:: Delete registry (both HKCU and HKLM)
reg delete "HKCU\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host" /f >nul 2>&1
reg delete "HKLM\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "xiaoxu-music-host" /f >nul 2>&1
echo  [INFO] 注册表清理命令已完成 (HKCU + HKLM)

set "CLEANUP_FAILED=0"

:: Delete stale JSON in Chrome User Data NativeMessagingHosts directory
:: (mirrors what install.bat copies in; old installs leave a file behind
:: pointing at a dead extension ID and Chrome silently refuses to spawn
:: the host until it is removed)
set "USERDATA_JSON=%LOCALAPPDATA%\Google\Chrome\User Data\NativeMessagingHosts\xiaoxu_music_host.json"
call :delete_required_file "%USERDATA_JSON%" "Chrome User Data native host JSON"

:: Delete only known package files. Leave unknown user files untouched.
for %%I in ("%~dp0.") do set "INSTALL_DIR=%%~fI"
set "SHORTCUT=%USERPROFILE%\Desktop\重启音频服务 (xiaoxu-music-bridge).lnk"
call :delete_required_file "%SHORTCUT%" "desktop recovery shortcut"
call :delete_required_file "%INSTALL_DIR%\xiaoxu-music-host.exe" "xiaoxu-music-host.exe"
call :delete_required_file "%INSTALL_DIR%\xiaoxu-music-host.pdb" "xiaoxu-music-host.pdb"
call :delete_required_file "%INSTALL_DIR%\D3DCompiler_47_cor3.dll" "D3DCompiler_47_cor3.dll"
call :delete_required_file "%INSTALL_DIR%\PenImc_cor3.dll" "PenImc_cor3.dll"
call :delete_required_file "%INSTALL_DIR%\PresentationNative_cor3.dll" "PresentationNative_cor3.dll"
call :delete_required_file "%INSTALL_DIR%\vcruntime140_cor3.dll" "vcruntime140_cor3.dll"
call :delete_required_file "%INSTALL_DIR%\wpfgfx_cor3.dll" "wpfgfx_cor3.dll"
call :delete_required_file "%INSTALL_DIR%\xiaoxu_music_host.json" "xiaoxu_music_host.json"
call :delete_required_file "%INSTALL_DIR%\install.bat" "install.bat"
call :delete_required_file "%INSTALL_DIR%\restart-audio.bat" "restart-audio.bat"
call :delete_required_file "%INSTALL_DIR%\README-install.txt" "README-install.txt"
call :delete_required_file "%INSTALL_DIR%\EXTENSION_ID.txt" "EXTENSION_ID.txt"
call :delete_required_file "%INSTALL_DIR%\EXTENSION_ID.txt.example" "EXTENSION_ID.txt.example"
call :delete_required_file "%INSTALL_DIR%\extension\manifest.json" "extension\manifest.json"
call :delete_required_file "%INSTALL_DIR%\extension\background.js" "extension\background.js"
call :delete_required_file "%INSTALL_DIR%\extension\host-bootstrap.js" "extension\host-bootstrap.js"
call :delete_required_file "%INSTALL_DIR%\extension\content.js" "extension\content.js"
call :delete_required_file "%INSTALL_DIR%\extension\reconnect-policy.js" "extension\reconnect-policy.js"
call :delete_required_file "%INSTALL_DIR%\extension\icon16.png" "extension\icon16.png"
call :delete_required_file "%INSTALL_DIR%\extension\icon48.png" "extension\icon48.png"
call :delete_required_file "%INSTALL_DIR%\extension\icon128.png" "extension\icon128.png"
call :remove_directory_if_empty "%INSTALL_DIR%\extension" "extension directory"

if not "%CLEANUP_FAILED%"=="0" (
    echo.
    echo  [ERROR] Required package cleanup failed.
    echo  uninstall.bat was kept so cleanup can be retried.
    echo.
    pause
    exit /b 1
)

echo  [OK] 已知的程序文件已删除

echo.
echo  卸载完成
echo.

:: Self-delete, then remove the install directory only if it is empty.
del /f /q "%~f0" >nul 2>&1
cd /d "%SystemRoot%"
rd /q "%INSTALL_DIR%" >nul 2>&1
exit /b 0

:delete_required_file
if not exist "%~1" exit /b 0
del /f /q "%~1" >nul 2>&1
if exist "%~1" (
    echo  [ERROR] Could not delete required package file: "%~2"
    set "CLEANUP_FAILED=1"
)
exit /b 0

:remove_directory_if_empty
if not exist "%~1\" exit /b 0
dir /b /a "%~1" 2>nul | findstr /r "." >nul
if not errorlevel 1 (
    echo  [WARN] "%~2" contains unknown files and was preserved.
    exit /b 0
)
rd /q "%~1" >nul 2>&1
if exist "%~1\" (
    echo  [ERROR] Could not remove required package directory: "%~2"
    set "CLEANUP_FAILED=1"
)
exit /b 0
