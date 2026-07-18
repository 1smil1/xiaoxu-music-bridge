# 小许音乐桥接

小许音乐桥接让 `xiaoxu.xin` 通过本机 Host 读取 Windows 当前媒体状态、封面、歌词和音频分析数据，并控制播放。

## 下载与安装

系统要求：Windows 10 版本 2004（内部版本 19041）或更高版本，或 Windows 11；仅支持 x64。

请下载稳定版运行包：

https://github.com/1smil1/xiaoxu-music-bridge/releases/latest/download/xiaoxu-music-bridge-windows-x64.zip

> 注意：GitHub 自动生成的 **Source code (zip)** 是源代码，不含 EXE，不能用于安装。请下载上面的 `xiaoxu-music-bridge-windows-x64.zip`。

1. 下载运行包 ZIP，并将其中内容完整解压到一个固定文件夹。不要直接在压缩包内运行文件。
2. 双击解压目录中的 `install.bat`，按提示完成安装。
3. 在 Chrome 地址栏打开 `chrome://extensions`，开启“开发者模式”。
4. 点击“加载已解压的扩展程序”，选择解压目录中的 `extension` 文件夹。
5. 访问 https://xiaoxu.xin。

安装时 Host 会立即启动，并注册为用户登录时自动启动。扩展会连接同一个本地 Host；如果 Host 尚未运行，扩展也会尝试启动它。

请保留完整的解压目录。移动目录后，注册的 Host 路径会失效，必须在新位置重新运行 `install.bat`。删除文件前请先运行 `uninstall.bat`，再移除 Chrome 扩展和解压目录。

## 验证安装

浏览器打开：

http://localhost:17888/health

正常时会返回 JSON，其中 `ok` 为 `true`，`version` 应为 `3.5.0`。

## 本地 API

3.5.0 运行包中的 Host 仅监听本机回环地址，主要接口如下：

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/health` | Host 版本、进程和运行时间 |
| `GET` | `/status` | 当前媒体状态 |
| `GET` | `/state/current` | 当前状态、封面 Data URL、歌词、签名和音频时钟 |
| `GET` | `/cover/current` | 当前封面图片 |
| `GET` | `/beat/current` | 当前节拍和音频特征快照 |
| `GET` | `/audio/stream` | 本机系统回环采集的 PCM 音频流 |
| `POST` | `/control/play-pause` | 播放或暂停 |
| `POST` | `/control/next` | 下一首 |
| `POST` | `/control/previous` | 上一首 |
| `GET` | `/settings/media-mode` | 读取媒体模式 |
| `PUT` | `/settings/media-mode` | 设置 `auto`、`win32` 或 `gsmtc` 模式 |
| `GET` | `/gsmtc/health` | GSMTC 诊断状态 |
| `POST` | `/gsmtc/repair` | 触发 GSMTC 修复 |

歌词在当前运行包中由 `GET /state/current` 的 `lyrics` 字段返回。仓库保留的旧版 ASP.NET 桥接服务另有兼容接口 `GET /lyrics/current`，它不是 3.5.0 运行包 Host 的独立路由。

Host 确实提供本机回环 PCM 流。普通媒体状态不会自动上传音频；当用户使用“一起听”等需要音频的功能时，网页可能中继该音频流。

## 开发

Host 项目：`xiaoxu-music-extension/host/xiaoxu-music-host.csproj`

Host 测试项目：`xiaoxu-music-extension/host.Tests/xiaoxu-music-host.Tests.csproj`

```powershell
dotnet run --project .\xiaoxu-music-extension\host.Tests\xiaoxu-music-host.Tests.csproj -c Release
```

扩展 CommonJS 测试：

```powershell
node --test .\xiaoxu-music-extension\extension\bridge-binary.test.cjs
node --test .\xiaoxu-music-extension\extension\host-bootstrap.test.cjs
node --test .\xiaoxu-music-extension\extension\reconnect-policy.test.cjs
```

构建 Windows 发布包：

```powershell
pwsh .\scripts\Build-Release.ps1 -SmokeTest
```

构建入口为 `scripts/Build-Release.ps1`。源代码始终保留在仓库中；可执行 EXE 和运行包 ZIP 作为 GitHub Release 资产发布。
