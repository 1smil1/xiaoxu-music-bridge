# Windows 本地 QQ 音乐桥接器方案

## 目标

做一个 Windows 本地程序 `xiaoxu-music-bridge.exe`，让网页 `xiaoxu.xin` 可以显示并控制当前电脑上的 QQ 音乐。

网页本身不播放 QQ 音乐音频，不保存 QQ 音乐 Cookie，不上传会员音频。真正播放仍然发生在用户本机 QQ 音乐 App。网页只作为本地播放器的展示面板和遥控器。

## 推荐技术栈

- 语言：C# / .NET 8 或 .NET 9
- 程序类型：Windows 桌面后台程序，第一版可以是控制台程序，后续再做托盘
- 本地服务：ASP.NET Core Minimal API
- 媒体状态读取：Windows Global System Media Transport Controls Session Manager
- 播放控制：优先调用系统媒体控制接口；不行再发送媒体键
- 打包：单文件 `.exe`

不建议第一版用 Electron，太重；不建议用 Python，打包和长期后台稳定性不如 C#。

## 工作方式

```txt
QQ 音乐 App 正在播放
        ↓
Windows 系统媒体会话暴露当前媒体信息
        ↓
xiaoxu-music-bridge.exe 读取歌名、歌手、封面、播放状态、进度
        ↓
本地 HTTP API 暴露给浏览器
        ↓
xiaoxu.xin 网页请求 http://127.0.0.1:17888/status
        ↓
网页显示当前歌曲、歌词、封面、播放按钮
```

如果其他人安装这个 exe，网页会连接他们自己的本地 exe，显示和控制他们自己的 QQ 音乐。不会控制小徐的 QQ 音乐。

## 第一阶段范围

先做最小可用版本，不做复杂歌词同步。

必须支持：

- 读取当前播放状态
- 读取歌名
- 读取歌手
- 读取封面，如果系统能提供
- 读取播放/暂停状态
- 读取当前进度和总时长，如果系统能提供
- 播放/暂停
- 上一首
- 下一首
- 提供健康检查接口
- 提供 CORS，允许 `xiaoxu.xin` 访问

暂不要求：

- 精准歌词同步
- QQ 音乐 Cookie
- 账号登录
- 云端数据库
- 多人共享当前状态
- 公开广播小徐正在听什么

## 本地 API 设计

服务监听：

```txt
http://127.0.0.1:17888
```

### GET /health

用于网页判断桥接器是否在线。

响应：

```json
{
  "ok": true,
  "name": "xiaoxu-music-bridge",
  "version": "0.1.0"
}
```

### GET /status

返回当前媒体状态。

响应示例：

```json
{
  "connected": true,
  "source": "QQMusic",
  "title": "云月谣",
  "artist": "兰音Reine",
  "album": "",
  "coverUrl": "http://127.0.0.1:17888/cover/current",
  "isPlaying": true,
  "positionMs": 63240,
  "durationMs": 245000,
  "updatedAt": "2026-05-29T20:30:00+08:00"
}
```

如果没有检测到音乐：

```json
{
  "connected": true,
  "source": null,
  "title": null,
  "artist": null,
  "album": null,
  "coverUrl": null,
  "isPlaying": false,
  "positionMs": 0,
  "durationMs": 0,
  "updatedAt": "2026-05-29T20:30:00+08:00"
}
```

### GET /cover/current

返回当前封面图片。若无法获取封面，返回 404 或一张默认图。

### POST /control/play-pause

切换播放/暂停。

响应：

```json
{ "ok": true }
```

### POST /control/next

下一首。

响应：

```json
{ "ok": true }
```

### POST /control/previous

上一首。

响应：

```json
{ "ok": true }
```

## CORS 要求

允许来源：

```txt
http://xiaoxu.xin
https://xiaoxu.xin
http://localhost:5173
http://127.0.0.1:5173
```

第一版可以为了调试临时允许所有来源，但最终建议只允许这些来源。

注意：如果网页是 `https://xiaoxu.xin`，浏览器请求 `http://127.0.0.1:17888` 可能遇到 mixed content 限制。第一版可以先在 `http://xiaoxu.xin` 下验证。如果后续要 HTTPS 页面稳定访问本地服务，需要考虑：

- 本地服务支持 HTTPS
- 使用本地受信任证书
- 或改用浏览器允许的本地连接方案

## 媒体状态读取建议

优先使用 Windows 的 Global System Media Transport Controls API。

目标能力：

- 枚举当前媒体会话
- 优先选择 QQ 音乐会话
- 如果无法识别 QQ 音乐，选择当前活跃播放会话
- 读取媒体属性：Title、Artist、AlbumTitle、Thumbnail
- 读取播放状态：Playing、Paused、Stopped
- 读取时间线属性：Position、EndTime

如果 QQ 音乐没有暴露完整信息，第一版至少要保证：

- 能控制播放/暂停/上一首/下一首
- `/status` 能返回明确的不可用状态

## 控制播放建议

优先尝试当前媒体会话的控制方法：

- TryPlayAsync
- TryPauseAsync
- TryTogglePlayPauseAsync
- TrySkipNextAsync
- TrySkipPreviousAsync

如果目标会话不支持这些方法，再 fallback 到系统媒体键。

## 歌词方案

第一版不强制做歌词。

第二阶段可选方案：

1. 根据 `title + artist` 调用歌词 API 搜索 LRC。
2. 本地维护歌词目录，例如：

```txt
lyrics/云月谣 - 兰音Reine.lrc
```

3. 桥接器返回完整歌词：

```txt
GET /lyrics/current
```

响应：

```json
{
  "title": "云月谣",
  "artist": "兰音Reine",
  "format": "lrc",
  "lyrics": "[00:12.30]第一句歌词\n[00:18.20]第二句歌词"
}
```

网页负责解析 LRC，并根据 `/status.positionMs` 高亮当前句。

不要第一版就强行读取 QQ 音乐歌词窗口，稳定性未知，容易卡住开发。

## 安全边界

必须遵守：

- 不读取、不保存 QQ 音乐 Cookie
- 不下载 QQ 音乐音频
- 不上传本地播放音频
- 不把用户本地播放状态自动广播到服务器
- 本地 API 默认只监听 `127.0.0.1`，不要监听 `0.0.0.0`
- 控制接口只允许本机网页调用

## Windows Agent 实现步骤

1. 创建 C# 项目：

```bash
dotnet new web -n xiaoxu-music-bridge
cd xiaoxu-music-bridge
```

2. 添加 Windows 相关支持包。根据实际 API 选择合适的 WinRT 包，例如 `Microsoft.Windows.SDK.Contracts`。

3. 实现 `MediaSessionService`：

- 获取当前媒体会话
- 读取媒体属性
- 读取时间线
- 控制播放

4. 实现 Minimal API：

- `/health`
- `/status`
- `/cover/current`
- `/control/play-pause`
- `/control/next`
- `/control/previous`

5. 加 CORS。

6. 确保服务只监听：

```txt
http://127.0.0.1:17888
```

7. 用浏览器测试：

```txt
http://127.0.0.1:17888/health
http://127.0.0.1:17888/status
```

8. 打开 QQ 音乐播放一首歌，再测试 `/status` 是否返回歌名和歌手。

9. 测试控制接口：

```bash
curl -X POST http://127.0.0.1:17888/control/play-pause
curl -X POST http://127.0.0.1:17888/control/next
curl -X POST http://127.0.0.1:17888/control/previous
```

10. 打包 exe：

```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## 验收标准

第一版完成时，应满足：

- 双击 exe 后，本地服务启动
- `GET /health` 返回 `ok: true`
- QQ 音乐播放时，`GET /status` 能看到歌名、歌手、播放状态
- 暂停/播放后，`isPlaying` 能变化
- `/control/play-pause` 能控制 QQ 音乐
- `/control/next` 能切到下一首
- `/control/previous` 能切到上一首
- 程序不需要管理员权限
- 程序不需要 QQ 音乐账号 Cookie
- 程序不上传任何音频文件

## 后续网页对接

网页端可以新增一个“本地播放器”组件：

- 启动时请求 `/health`
- 成功则每 1 秒轮询 `/status`
- 显示当前歌名、歌手、封面、进度条
- 按钮调用控制接口
- 如果连接失败，显示“未连接本地播放器，请启动 xiaoxu-music-bridge.exe”

后续如果要做实时性更好的版本，可以把轮询改为 WebSocket。
