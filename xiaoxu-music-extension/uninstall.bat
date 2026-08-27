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
echo    2. 此文件夹中的所有文件
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
echo  [OK] 注册表已清除 (HKCU + HKLM)

:: Delete desktop shortcut created by install.bat
del /f /q "%USERPROFILE%\Desktop\xiaoxu-music-bridge.lnk" >nul 2>&1

:: Delete all files in current directory (except uninstall.bat itself)
set "INSTALL_DIR=%~dp0"
del /f /q "%INSTALL_DIR%\xiaoxu-music-host.exe" >nul 2>&1
del /f /q "%INSTALL_DIR%\xiaoxu-music-host.pdb" >nul 2>&1
del /f /q "%INSTALL_DIR%\xiaoxu_music_host.json" >nul 2>&1
del /f /q "%INSTALL_DIR%\install.bat" >nul 2>&1
del /f /q "%INSTALL_DIR%\EXTENSION_ID.txt" >nul 2>&1
rd /s /q "%INSTALL_DIR%\extension" >nul 2>&1
echo  [OK] 文件已删除

echo.
echo  卸载完成
echo.

:: Self-delete: remove uninstall.bat and the empty folder
del /f /q "%~f0" >nul 2>&1
cd ..
rd /q "%INSTALL_DIR%" >nul 2>&1
