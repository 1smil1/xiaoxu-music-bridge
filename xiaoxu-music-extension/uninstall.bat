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
echo    2. Chrome 扩展缓存数据 (chrome.storage.session)
echo    3. 此文件夹中的所有文件
echo.
echo  注意：卸载过程中会关闭所有 Chrome 窗口
echo  请先保存 Chrome 中的工作
echo.
echo  Chrome 扩展需要手动在 chrome://extensions 中移除。
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
echo  [OK] 注册表已清除 (HKCU + HKLM)

:: Clear Chrome storage session cache for this extension
:: chrome.storage.session 数据存储在 Chrome User Data 内部，无法通过 bat 直接清除
:: 重启 Chrome 后 service worker 会被销毁，session storage 自动清除
echo  [OK] 重启 Chrome 以清除扩展缓存...

:: Close Chrome to flush native messaging host registry and clear session storage
taskkill /F /IM chrome.exe >nul 2>&1
timeout /t 2 /nobreak >nul

:: Delete all files in current directory (except uninstall.bat itself)
set "INSTALL_DIR=%~dp0"
del /f /q "%INSTALL_DIR%\xiaoxu-music-host.exe" >nul 2>&1
del /f /q "%INSTALL_DIR%\xiaoxu-music-host.pdb" >nul 2>&1
del /f /q "%INSTALL_DIR%\xiaoxu_music_host.json" >nul 2>&1
del /f /q "%INSTALL_DIR%\install.bat" >nul 2>&1
del /f /q "%INSTALL_DIR%\EXTENSION_ID.txt" >nul 2>&1
rd /s /q "%INSTALL_DIR%\extension" >nul 2>&1
echo  [OK] 文件已删除

:: Restart Chrome
if exist "C:\Program Files\Google\Chrome\Application\chrome.exe" (
    start "" "C:\Program Files\Google\Chrome\Application\chrome.exe"
) else if exist "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" (
    start "" "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
)

echo.
echo  卸载完成
echo  请前往 chrome://extensions 移除 xiaoxu-music-bridge 扩展。
echo  然后可以手动删除此文件夹: %INSTALL_DIR%
echo.
pause

:: Self-delete: remove uninstall.bat and the empty folder
del /f /q "%~f0" >nul 2>&1
cd ..
rd /q "%INSTALL_DIR%" >nul 2>&1
