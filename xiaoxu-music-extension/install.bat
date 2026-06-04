@echo off
chcp 65001 >nul 2>&1
title xiaoxu-music-bridge

echo.
echo  xiaoxu-music-bridge
echo.

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

reg add "HKCU\Software\Google\Chrome\NativeMessagingHosts\xiaoxu_music_host" /ve /t REG_SZ /d "%INSTALL_DIR%\xiaoxu_music_host.json" /f >nul 2>&1
if %errorlevel% neq 0 (
    echo  error: registry write failed
    pause
    exit /b 1
)

set "EXE_PATH=%INSTALL_DIR%\xiaoxu-music-host.exe"
set "EXE_PATH_JSON=%EXE_PATH:\=\\%"

powershell -Command "$j = '{ \"path\": \"%EXE_PATH_JSON%\", \"name\": \"xiaoxu_music_host\", \"allowed_origins\": [\"chrome-extension://%EXT_ID%/\"], \"type\": \"stdio\" }'; [System.IO.File]::WriteAllText('%INSTALL_DIR%\xiaoxu_music_host.json', $j)"

if not exist "%INSTALL_DIR%\xiaoxu_music_host.json" (
    echo  error: failed to generate xiaoxu_music_host.json
    pause
    exit /b 1
)

echo  registry: OK
echo  manifest: OK
echo.
echo  Done.
echo.
echo  Next step:
echo  1. Open chrome://extensions
echo  2. Enable Developer mode
echo  3. Click "Load unpacked" and select: %INSTALL_DIR%\extension
echo.
pause
