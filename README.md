# xiaoxu-music-bridge

Windows local bridge for exposing the current system media session to `xiaoxu.xin`.

The bridge listens only on:

```txt
http://127.0.0.1:17888
```

It does not read QQ Music cookies, download audio, upload audio, or broadcast playback state to a server. Playback remains inside the local QQ Music desktop app.

## Requirements

- Windows 10 19041 or newer
- .NET 9 SDK for building
- QQ Music desktop app for real QQ Music testing

## Run

```powershell
dotnet run --project .\xiaoxu-music-bridge\xiaoxu-music-bridge.csproj
```

Then test:

```powershell
Invoke-RestMethod http://127.0.0.1:17888/health
Invoke-RestMethod http://127.0.0.1:17888/status
Invoke-RestMethod -Method Post http://127.0.0.1:17888/control/play-pause
Invoke-RestMethod -Method Post http://127.0.0.1:17888/control/next
Invoke-RestMethod -Method Post http://127.0.0.1:17888/control/previous
```

## API

- `GET /health`
- `GET /status`
- `GET /cover/current`
- `POST /control/play-pause`
- `POST /control/next`
- `POST /control/previous`

Allowed CORS origins:

- `http://xiaoxu.xin`
- `https://xiaoxu.xin`
- `http://localhost:5173`
- `http://127.0.0.1:5173`

## Test

```powershell
dotnet test .\xiaoxu-music-bridge.sln
```

## Publish

```powershell
dotnet publish .\xiaoxu-music-bridge\xiaoxu-music-bridge.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o .\publish
```

The executable is written to:

```txt
publish\xiaoxu-music-bridge.exe
```

