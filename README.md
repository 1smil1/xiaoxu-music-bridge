# xiaoxu-music-bridge

Windows local bridge for exposing the current system media session to `xiaoxu.xin`.

The bridge listens only on:

```txt
http://127.0.0.1:17888
```

It does not read QQ Music cookies, download audio, upload audio, or broadcast playback state to a server. Playback remains inside the local QQ Music desktop app.

## What's in this repo

- `xiaoxu-music-extension/host/` — single-file host executable source (native messaging host + Kestrel HTTP + WinForms tray UI, v3.6.1)
- `xiaoxu-music-extension/extension/` — Chrome MV3 extension source
- `xiaoxu-music-extension/install.bat` / `uninstall.bat` / `install.ps1` — install scripts
- `xiaoxu-music-extension/EXTENSION_ID.txt` — Chrome extension ID (created on first load)
- `xiaoxu-music-extension/xiaoxu-music-bridge.zip` — release archive

## Requirements

- Windows 10 19041 or newer
- .NET 9 SDK for building
- QQ Music desktop app for real QQ Music testing

## Run (development)

```powershell
dotnet run --project .\xiaoxu-music-extension\host\xiaoxu-music-host.csproj -- --server
```

Then test:

```powershell
Invoke-RestMethod http://127.0.0.1:17888/health
Invoke-RestMethod http://127.0.0.1:17888/status
Invoke-RestMethod -Method Post http://127.0.0.1:17888/control/play-pause
Invoke-RestMethod -Method Post http://127.0.0.1:17888/control/next
Invoke-RestMethod -Method Post http://127.0.0.1:17888/control/previous
```

A tray icon appears in the Windows notification area. Double-click to open the status window, right-click for 退出.

## API

- `GET /health`
- `GET /status`
- `GET /state/current`
- `GET /beat/current`
- `GET /cover/current`
- `GET /lyrics/query?title=...&artist=...`
- `POST /control/play-pause`
- `POST /control/next`
- `POST /control/previous`

Allowed CORS origins:

- `http://xiaoxu.xin`
- `https://xiaoxu.xin`
- `http://localhost:5173`
- `http://127.0.0.1:5173`

## Publish

```powershell
dotnet publish .\xiaoxu-music-extension\host\xiaoxu-music-host.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o .\publish
```

The executable is written to:

```txt
publish\xiaoxu-music-host.exe
```

## Install (end user)

1. Unzip the release archive (or copy `xiaoxu-music-extension/extension/` and `xiaoxu-music-host.exe` into a directory).
2. Run `install.bat`. The script kills any running host, writes the native messaging manifest + registry keys, and restarts Chrome.
3. In Chrome, open `chrome://extensions`, enable Developer mode, click "Load unpacked", and select the `extension/` directory.
4. To uninstall, run `uninstall.bat` and remove the extension from Chrome.