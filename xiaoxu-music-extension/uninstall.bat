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
echo    1. 注册表中的桥接器配置
echo    2. 此文件夹中的所有文件
echo.

set /p confirm=确认卸载吗 (输入 Y 确认):
if /i not "%confirm%"=="Y" (
    echo  已取消。
    pause
    exit /b
)

:: Delete registry
powershell -Command "Remove-Item -Path 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host' -ErrorAction SilentlyContinue" >nul 2>&1
echo  [OK] 注册表已清除

:: Delete all files in current directory (except uninstall.bat itself)
set "INSTALL_DIR=%~dp0"
del /f /q "%INSTALL_DIR%\xiaoxu-music-host.exe" >nul 2>&1
del /f /q "%INSTALL_DIR%\xiaoxu_music_host.json" >nul 2>&1
del /f /q "%INSTALL_DIR%\install.bat" >nul 2>&1
rd /s /q "%INSTALL_DIR%\extension" >nul 2>&1
echo  [OK] 文件已删除

echo.
echo  卸载完成
echo  请前往 chrome://extensions 移除 xiaoxu-music-bridge 扩展。
echo.
pause
