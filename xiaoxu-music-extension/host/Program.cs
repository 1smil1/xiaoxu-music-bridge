using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using xiaoxu_music_bridge.Bridge;
using xiaoxu_music_bridge.Lyrics;
using xiaoxu_music_bridge.Media;
using xiaoxu_music_bridge.Audio;
using xiaoxu_music_bridge.Common;

// Chrome Native Messaging format: 4-byte little-endian uint32 length + UTF-8 JSON body
// Read from stdin, write to stdout. No console window (OutputType=WinExe).

if (args.Contains("--gsmtc-probe", StringComparer.OrdinalIgnoreCase))
{
    var result = await GsmtcRecoveryService.ProbeCurrentProcessAsync();
    var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
    await Console.OpenStandardOutput().WriteAsync(bytes);
    return;
}

if (args.Contains("--restart-gsmtc-services", StringComparer.OrdinalIgnoreCase))
{
    Environment.Exit(GsmtcRecoveryService.RestartAudioServicesElevatedHelper());
    return;
}

try
{
var launchMode = HostLaunchPolicy.Resolve(args);
if (launchMode == HostLaunchMode.NativeMessaging)
{
    await RunNativeLauncherAsync();
    return;
}

var waitForPid = HostLaunchPolicy.GetWaitForPid(args);
if (waitForPid is null && await IsServerHealthyAsync()) return;
if (waitForPid is > 0)
{
    try
    {
        using var previous = System.Diagnostics.Process.GetProcessById(waitForPid.Value);
        previous.WaitForExit(15000);
    }
    catch (ArgumentException) { }
}

using var serverMutex = new Mutex(initiallyOwned: true, HostLaunchPolicy.ServerMutexName, out var ownsServerMutex);
if (!ownsServerMutex) return;

var hostStartedAt = DateTimeOffset.Now;
var hostPid = Environment.ProcessId;
// Log IMMEDIATELY — before any service initialization
LogPaths.SafeAppend(LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss}] HOST STARTING pid={hostPid} startedAt={hostStartedAt:o} (logPath={LogPaths.DebugLog}), args=[{string.Join(", ", args)}], cwd={Environment.CurrentDirectory}\n");

// Log initial GSMTC health snapshot (note: this also resets a stale persisted count)
var initialHealth = GsmtcHealthTracker.GetSnapshot();
LogPaths.SafeAppend(LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss}] GSMTC health at startup: totalRestarts={initialHealth.TotalRestarts}, stuck={initialHealth.IsStuck}\n");

// Chrome Native Messaging passes the extension origin as args[0] (e.g. "chrome-extension://...")
// Skip it — only use args that look like valid local paths
var lyricsDir = args.FirstOrDefault(a => !a.StartsWith("chrome-extension://") && Path.IsPathRooted(a));
// Primary: GSMTC (full metadata: album, cover, position, duration).
// Fallback: Win32 GetWindowText on QQMusic_Daemon_Wnd (title + artist only).
// When GSMTC's broker deadlocks, the fallback keeps the dashboard usable with
// partial info instead of returning connected=false.
var gsmtcService = new WindowsMediaSessionService();
var win32Fallback = new Win32MediaService();
DateTime gsmtcSkipUntil = DateTime.MinValue; // circuit breaker
// Shared HttpClient for lyric + cover lookup services (avoids socket-pool fragmentation)
var sharedHttp = new HttpClient();
var coverLookup = new QqMusicCoverLookupService(sharedHttp);
var itunesCoverLookup = new ITunesCoverLookupService(sharedHttp);
var lyricService = new LocalLyricService(lyricsDir, sharedHttp);
var mediaModeStore = new MediaModeStore(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "xiaoxu-music-host"));
var gsmtcRecovery = new GsmtcRecoveryService();

// v3.2.10 hotfix: subscribe to Win32MediaService.TrackChanged at the top
// level so the Native Messaging path (Program.cs HandleGetLyrics) also
// benefits from lyric prefetch. The original prefetch in BridgeHttpServer
// only helped the HTTP path; the extension's injected.js intercepts fetch
// to 127.0.0.1:17888 and reroutes through this Native Messaging handler,
// which never touched BridgeHttpServer's cache. Now both paths share
// LyricPrefetchCache (Common/LyricPrefetchCache.cs).
win32Fallback.TrackChanged += (sender, e) =>
{
    _ = System.Threading.Tasks.Task.Run(async () =>
    {
        try
        {
            var status = await win32Fallback.GetFastStatusAsync(System.Threading.CancellationToken.None);
            if (status == null || string.IsNullOrWhiteSpace(status.Title)) return;

            var lookupTitle = status.Title.Trim();
            var lookupArtist = status.Artist?.Trim();

            // Skip if a previous prefetch already populated this key (e.g.
            // BridgeHttpServer's subscriber wrote it first — order doesn't
            // matter because we just want at-least-once delivery).
            if (LyricPrefetchCache.TryGet(lookupTitle, lookupArtist, out var existing) && existing?.Found == true)
            {
                return;
            }

            var mediaStatus = new xiaoxu_music_bridge.Media.MediaStatus(
                Connected: true, Source: "Prefetch",
                Title: lookupTitle, Artist: lookupArtist, Album: null, CoverUrl: null,
                IsPlaying: false, PositionMs: 0, DurationMs: 0,
                UpdatedAt: DateTimeOffset.Now);

            var (lyrics, timedOut, _) = await WithTimeout(
                lyricService.GetCurrentLyricsAsync(mediaStatus, System.Threading.CancellationToken.None),
                5000, "TrackChangedPrefetch");

            // Re-check after the await — discard stale prefetch if the
            // user has already switched to another song.
            var now = await win32Fallback.GetFastStatusAsync(System.Threading.CancellationToken.None);
            if (now?.Title?.Trim() != lookupTitle || now?.Artist?.Trim() != lookupArtist)
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] [TrackChangedPrefetch] discarded (status changed): expected='{lookupTitle}' got='{now?.Title}'\n");
                return;
            }

            if (!timedOut && lyrics != null && lyrics.Found)
            {
                LyricPrefetchCache.Store(lookupTitle, lookupArtist, lyrics);
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] [TrackChangedPrefetch] → stored (title='{lookupTitle}', artist='{lookupArtist}', lrcLen={lyrics.Lrc?.Length ?? 0})\n");
            }
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [TrackChangedPrefetch] failed: {ex.GetType().Name}: {ex.Message}\n");
        }
    });
};

// Audio beat service - captures system audio and computes bass/volume/pulse
// Started on first subscribeBeat command, stopped on unsubscribeBeat
AudioBeatService? beatService = null;
AudioDebugServer? debugServer = null;
var stdoutLock = new object();

// v3.2.4: HTTP server on http://127.0.0.1:17888/ for Lively Wallpaper + Chrome
// without extension. Listens on loopback only (per spec § 安全边界). Shares the
// same GSMTC + Win32 fallback pipeline as the Native Messaging handlers.
var bridgeHttp = new BridgeHttpServer(gsmtcService, win32Fallback);
bridgeHttp.SetLyricService(lyricService);
bridgeHttp.SetCoverLookupServices(coverLookup, itunesCoverLookup);
bridgeHttp.Start();
if (!bridgeHttp.IsRunning)
    throw new InvalidOperationException("Bridge HTTP server could not bind localhost:17888");
LogPaths.SafeAppend(LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss}] BridgeHttpServer STARTED on http://127.0.0.1:17888/\n");

// v3.2.2.1: Register GSMTC recovery probe. When the status breaker opens
// (e.g., user seeked in QQ Music → CEF animation briefly deadlocked GSMTC),
// this probe runs in the background every 3s and closes the breaker the
// moment GSMTC recovers — so the next /state/current poll gets a real
// positionMs and lyric catches up to the seek target within ~3-5s.
GsmtcCircuitBreaker.RegisterProbe(
    async () =>
    {
        try
        {
            var task = gsmtcService.GetStatusAsync(CancellationToken.None);
            var winner = await Task.WhenAny(task, Task.Delay(1500));
            if (winner != task) return false;
            await task;
            return true;
        }
        catch
        {
            return false;
        }
    },
    msg => LogPaths.SafeAppend(LogPaths.DebugLog, msg));

// Start the debug HTTP server immediately so Claude can curl it before the
// user opens the wallpaper page. It will return "waiting" responses until
// AudioBeatService is attached (which happens on first subscribeBeat).
debugServer = new AudioDebugServer(null);
debugServer.Start();
LogPaths.SafeAppend(LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss}] AudioDebugServer STARTED on http://localhost:17889/ (waiting for AudioBeatService)\n");

LogPaths.SafeAppend(LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss}] INIT OK, lyricsDir={lyricsDir}\n");

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

// Start capture immediately so the HTTP bridge can use audio energy for
// playback state before a browser extension sends subscribeBeat.
if (beatService is null)
{
    beatService = new AudioBeatService(json =>
    {
        WriteMessage(stdout, json, stdoutLock);
    });
    beatService.Start();
    if (debugServer is not null)
    {
        beatService.AttachDebugServer(debugServer);
        debugServer.SetBeatService(beatService);
    }
    bridgeHttp.SetBeatService(beatService);
    win32Fallback.AttachBeatService(beatService);
    LogPaths.SafeAppend(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] AudioBeatService STARTED at host startup\n");
}

// The persistent server owns HTTP, media and audio services independently of
// Chrome. Native Messaging invocations return above after ensuring this process.
await Task.Delay(Timeout.Infinite);

while (true)
{
    // Read 4-byte length prefix (little-endian uint32)
    var lengthBuffer = new byte[4];
    if (!FillBuffer(stdin, lengthBuffer, 4))
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] stdin EOF pid={hostPid} — exiting so Chrome can start a fresh host\n");
        if (!NativeHostLifetime.ShouldContinueAfterInputClosed()) break;
    }

    var messageLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);

    // Read message body
    var bodyBuffer = new byte[messageLength];
    if (!FillBuffer(stdin, bodyBuffer, (int)messageLength))
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] stdin body EOF pid={hostPid} — exiting\n");
        break;
    }

    var json = Encoding.UTF8.GetString(bodyBuffer);

    // Log raw message only when not a ping (pings spam the log)
    if (!json.Contains("\"ping\""))
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] RAW msg: len={messageLength}, text={json}\n");
    }

    // Chrome native messaging may truncate the last byte (missing closing brace)
    if (!json.EndsWith("}") && !json.EndsWith("]"))
    {
        var repaired = json + "}";
        try { JsonDocument.Parse(repaired); json = repaired; }
        catch { /* keep original, will fail in the try block below */ }
    }

    // Try to extract _id from raw string as fallback (in case JSON parse fails)
    long? fallbackId = null;
    var idMatch = System.Text.RegularExpressions.Regex.Match(json, @"""_id""\s*:\s*(\d+)");
    if (idMatch.Success) fallbackId = long.Parse(idMatch.Groups[1].Value);

    // Parse and handle request
    string responseJson;
    try
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();

        // Extract _id so we can echo it back in the response
        var hasId = doc.RootElement.TryGetProperty("_id", out var idElement);
        var id = hasId ? idElement.GetInt64() : fallbackId;

        responseJson = type switch
        {
            "getStatus" => await HandleGetStatus(gsmtcService, win32Fallback, GsmtcCircuitBreaker.ShouldSkip, mediaModeStore),
            "getState" => await HandleGetState(gsmtcService, win32Fallback, lyricService, GsmtcCircuitBreaker.ShouldSkip, mediaModeStore),
            "control" => await HandleControl(gsmtcService, win32Fallback, GsmtcCircuitBreaker.ShouldSkip, mediaModeStore, doc.RootElement),
            "getLyrics" => await HandleGetLyrics(gsmtcService, win32Fallback, lyricService, GsmtcCircuitBreaker.ShouldSkip),
            "getCover" => await HandleGetCover(gsmtcService, win32Fallback, coverLookup, itunesCoverLookup, GsmtcCircuitBreaker.ShouldSkipCover),
            "subscribeBeat" => HandleSubscribeBeat(ref beatService, ref debugServer, bridgeHttp, win32Fallback, stdout, stdoutLock),
            "unsubscribeBeat" => HandleUnsubscribeBeat(ref beatService),
            "getBeat" => HandleGetBeat(beatService),
            "getGsmtcHealth" => HandleGetGsmtcHealth(),
            "getMediaMode" => JsonSerializer.Serialize(new { type = "mediaMode", mode = MediaModeStore.ToWireValue(mediaModeStore.Get()) }),
            "setMediaMode" => HandleSetMediaMode(mediaModeStore, doc.RootElement),
            "repairGsmtc" => JsonSerializer.Serialize(await gsmtcRecovery.RepairAsync(CancellationToken.None)),
            _ => JsonSerializer.Serialize(new { type = "error", message = $"Unknown command: {type}" })
        };

        // Inject _id into the response JSON so the extension can match it
        if (id.HasValue)
        {
            var responseObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseJson)!;
            responseObj["_id"] = JsonSerializer.SerializeToElement(id.Value);
            responseJson = JsonSerializer.Serialize(responseObj);
        }
    }
    catch (Exception ex)
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] EXCEPTION: type={ex.GetType().Name}, msg={ex.Message}\n{ex.StackTrace}\n");
        responseJson = JsonSerializer.Serialize(new { type = "error", message = ex.Message });

        // Always inject _id so the extension can match the error response
        if (fallbackId.HasValue)
        {
            var responseObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseJson)!;
            responseObj["_id"] = JsonSerializer.SerializeToElement(fallbackId.Value);
            responseJson = JsonSerializer.Serialize(responseObj);
        }
    }

    // Write response — if stdout pipe is broken (Chrome killed the SW / host),
    // WriteMessage throws. v3.2.7 hotfix: log it and break out of the loop so the
    // host exits cleanly with a stack trace instead of dying silently (which made
    // /state/current polling hang on "host unavailable 503" with no diagnostic).
    try
    {
        WriteMessage(stdout, responseJson, stdoutLock);
    }
    catch (Exception writeEx)
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] WriteMessage FAILED (Chrome probably killed the SW): {writeEx.GetType().Name}: {writeEx.Message}\n");
        break;
    }
}

// Cleanup
LogPaths.SafeAppend(LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss.fff}] HOST STOPPING pid={hostPid} uptimeMs={(DateTimeOffset.Now - hostStartedAt).TotalMilliseconds:F0}\n");
beatService?.Dispose();
debugServer?.Dispose();
bridgeHttp.Dispose();

static bool FillBuffer(Stream stream, byte[] buffer, int count)
{
    var offset = 0;
    while (offset < count)
    {
        var read = stream.Read(buffer, offset, count - offset);
        if (read == 0) return false;
        offset += read;
    }
    return true;
}

static void WriteMessage(Stream stdout, string json, object? lockObj = null)
{
    var responseBytes = Encoding.UTF8.GetBytes(json);
    var lengthBytes = BitConverter.GetBytes((uint)responseBytes.Length);

    if (lockObj != null)
    {
        lock (lockObj)
        {
            stdout.Write(lengthBytes, 0, 4);
            stdout.Write(responseBytes, 0, responseBytes.Length);
            stdout.Flush();
        }
    }
    else
    {
        stdout.Write(lengthBytes, 0, 4);
        stdout.Write(responseBytes, 0, responseBytes.Length);
        stdout.Flush();
    }
}

// Race a WinRT async task against a deadline. If the deadline wins, return the
// fallback and arrange for a late-completion log so we know whether GSMTC was
// actually hung vs. just slow. The leaked Task is harmless — it eventually
// completes or faults and is then GC'd.
// v3.2.5: also returns the synchronous exception (if any) so callers can pass
// it to GsmtcHealthTracker.RecordFailure and have it classified as a permanent
// failure (FileNotFoundException → stop the restart loop).
static async Task<(T Result, bool TimedOut, Exception? SyncFault)> WithTimeout<T>(Task<T> task, int timeoutMs, string opName)
{
    var winner = await Task.WhenAny(task, Task.Delay(timeoutMs));
    if (winner != task)
    {
        _ = task.ContinueWith(t =>
        {
            string status = t.IsCompletedSuccessfully
                ? "completed_late"
                : $"faulted:{t.Exception?.GetType().Name}:{t.Exception?.Message}";
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] LATE {opName} {status}\n");
        });
        return (default!, true, null);
    }
    // GSMTC throws synchronously (e.g. FileNotFoundException for a missing WinRT
    // projection DLL — happens when single-file publish drops a transitive
    // dependency). The original code rethrew here, which bypassed the Win32
    // fallback in HandleGetStatus/HandleGetLyrics/HandleControl. Treat the
    // synchronous fault the same as a timeout so the caller can fall back.
    try
    {
        return (await task, false, null);
    }
    catch (Exception ex)
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] {opName} sync fault: {ex.GetType().Name}: {ex.Message}\n");
        return (default!, true, ex);
    }
}

static async Task<string> HandleGetStatus(
    WindowsMediaSessionService gsmtc,
    Win32MediaService fallback,
    Func<bool> shouldSkipGsmtc,
    MediaModeStore mediaModeStore)
{
    var mode = mediaModeStore.Get();
    // Try GSMTC first unless the circuit breaker says it's been failing
    if (MediaModePolicy.ShouldTryGsmtc(mode, shouldSkipGsmtc()))
    {
        var statusStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (status, timedOut, syncFault) = await WithTimeout(
            gsmtc.GetStatusAsync(CancellationToken.None), 1500, "GetStatusAsync");
        statusStopwatch.Stop();
        if (!timedOut)
        {
            GsmtcHealthTracker.RecordSuccess();
            // v3.2.9: GSMTC returned successfully within 1.5s but with no
            // title/artist — strongly indicates GSMTC failed to enumerate QQ
            // Music's session (QQ Music uses CEF, doesn't register a GSMTC
            // session). Without this probe the dashboard sits on "GSMTC" with
            // empty title / 未连接播放器 while QQ Music's daemon window IS
            // visible to Win32. Try Win32 before returning the empty GSMTC
            // status — if Win32 found a track via QQMusic_Daemon_Wnd title,
            // return that instead.
            if (mode == MediaMode.Auto && string.IsNullOrEmpty(status.Title) && string.IsNullOrEmpty(status.Artist))
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] HandleGetStatus GSMTC NoMedia (no session) -> probing Win32\n");
                var probeStatus = await fallback.GetStatusAsync(CancellationToken.None);
                if (!string.IsNullOrEmpty(probeStatus.Title) || !string.IsNullOrEmpty(probeStatus.Artist))
                {
                    LogPaths.SafeAppend(LogPaths.DebugLog,
                        $"[{DateTime.Now:HH:mm:ss}] Status (Win32 probe-recovered): title={probeStatus.Title}, artist={probeStatus.Artist}\n");
                    return SerializeStatus(probeStatus, true, fallback.LastPlaybackKnown, mode, MediaMode.Win32);
                }
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] HandleGetStatus GSMTC NoMedia + Win32 also empty -> returning GSMTC NoMedia\n");
            }
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] Status (GSMTC): title={status.Title}, artist={status.Artist}, pos={status.PositionMs}, dur={status.DurationMs}, dt={statusStopwatch.ElapsedMilliseconds}ms\n");
            if (mode == MediaMode.Gsmtc && string.IsNullOrWhiteSpace(status.Title))
                return SerializeStatus(status, false, false, mode, MediaMode.Gsmtc,
                    new { code = "no_media_session", message = "No current GSMTC media session", stage = "probe" });
            return SerializeStatus(status, false, true, mode, MediaMode.Gsmtc);
        }
        // Timed out — log failure and open the circuit breaker for 30s
        GsmtcHealthTracker.RecordFailure("GetStatusAsync", syncFault);
        GsmtcCircuitBreaker.Open();
        if (mode == MediaMode.Gsmtc)
            return SerializeStatus(MediaStatus.NoMedia(), false, false, mode, MediaMode.Gsmtc,
                new { code = syncFault != null ? "gsmtc_activation_failed" : "gsmtc_timeout", message = syncFault?.Message ?? "GSMTC request timed out", stage = "probe" });
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetStatus GSMTC TIMEOUT → falling back to Win32 daemon title\n");
    }
    else
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetStatus GSMTC skipped (circuit breaker open) → Win32 fallback\n");
    }

    var fbStatus = await fallback.GetStatusAsync(CancellationToken.None);
    LogPaths.SafeAppend(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] Status (Win32 fallback): title={fbStatus.Title}, artist={fbStatus.Artist}\n");
    return SerializeStatus(fbStatus, mode == MediaMode.Auto, fallback.LastPlaybackKnown, mode, MediaMode.Win32);

    static string SerializeStatus(MediaStatus status, bool viaFallback, bool playbackKnown,
        MediaMode requestedMode, MediaMode activeMode, object? mediaError = null)
    {
        // For QQ Music the native GSMTC thumbnail is usually null (CEF doesn't expose
        // it to Windows.Media). Mark hasCover=true when source=QQMusic so the bridge
        // will actually call getCover - Tier 2 of HandleGetCover then runs the QQ Music
        // lookup as a fallback before returning null.
        bool hasCover = status.CoverUrl is not null
            || string.Equals(status.Source, "QQMusic", StringComparison.Ordinal);
        return JsonSerializer.Serialize(new
        {
            type = "status",
            connected = status.Connected,
            source = status.Source,
            title = status.Title,
            artist = status.Artist,
            album = status.Album,
            coverUrl = (string?)null,
            hasCover,
            isPlaying = status.IsPlaying,
            playbackKnown,
            positionMs = status.PositionMs,
            durationMs = status.DurationMs,
            updatedAt = status.UpdatedAt.ToString("o"),
            viaFallback,
            requestedMode = MediaModeStore.ToWireValue(requestedMode),
            activeMode = MediaModeStore.ToWireValue(activeMode),
            mediaError
        });
    }
}

// Global circuit breaker state — closes the GSMTC path for 30s after a timeout
// so we don't pay the 1.5s penalty on every getStatus when GSMTC is hung.
// (Implementation lives in Common/GsmtcCircuitBreaker.cs — top-level types must
// come AFTER all top-level statements, so we keep it out of this try block.)

static async Task<string> HandleControl(
    WindowsMediaSessionService gsmtc,
    Win32MediaService fallback,
    Func<bool> shouldSkipGsmtc,
    MediaModeStore mediaModeStore,
    JsonElement request)
{
    var mode = mediaModeStore.Get();
    var commandStr = request.GetProperty("command").GetString() ?? "";
    var command = commandStr switch
    {
        "play-pause" => ControlCommand.PlayPause,
        "next" => ControlCommand.Next,
        "previous" => ControlCommand.Previous,
        _ => throw new ArgumentException($"Unknown command: {commandStr}")
    };

    if (MediaModePolicy.ShouldTryGsmtc(mode, shouldSkipGsmtc()))
    {
        var (result, timedOut, syncFault) = await WithTimeout(
            gsmtc.SendCommandAsync(command, CancellationToken.None), 1500, "SendCommandAsync");
        if (!timedOut)
        {
            GsmtcHealthTracker.RecordSuccess();
            return JsonSerializer.Serialize(new
            {
                type = "controlResult",
                ok = result.Ok,
                error = result.Error,
                viaFallback = false
            });
        }
        GsmtcHealthTracker.RecordFailure("SendCommandAsync", syncFault);
        GsmtcCircuitBreaker.Open();
        if (mode == MediaMode.Gsmtc)
            return JsonSerializer.Serialize(new { type = "error", message = syncFault?.Message ?? "GSMTC control timed out" });
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleControl GSMTC TIMEOUT control={commandStr} → media keys\n");
    }
    else
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleControl GSMTC skipped (circuit breaker open) → media keys\n");
    }

    // Fallback: send media keys system-wide. Works for QQ Music and any media player.
    var fbResult = await fallback.SendCommandAsync(command, CancellationToken.None);
    return JsonSerializer.Serialize(new
    {
        type = "controlResult",
        ok = fbResult.Ok,
        error = fbResult.Error,
        viaFallback = mode == MediaMode.Auto
    });
}

static async Task<string> HandleGetLyrics(
    WindowsMediaSessionService gsmtc,
    Win32MediaService fallback,
    LocalLyricService lyricService,
    Func<bool> shouldSkipGsmtc)
{
    MediaStatus status;
    bool viaFallback = false;

    if (!shouldSkipGsmtc())
    {
        var (s, statusTimedOut, syncFault) = await WithTimeout(
            gsmtc.GetStatusAsync(CancellationToken.None), 1500, "GetStatusAsync(forLyrics)");
        if (!statusTimedOut)
        {
            status = s;
            GsmtcHealthTracker.RecordSuccess();
        }
        else
        {
            GsmtcHealthTracker.RecordFailure("GetStatusAsync(forLyrics)", syncFault);
            GsmtcCircuitBreaker.Open();
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] HandleGetLyrics GSMTC TIMEOUT → Win32 fallback\n");
            status = await fallback.GetStatusAsync(CancellationToken.None);
            viaFallback = true;
        }
    }
    else
    {
        status = await fallback.GetStatusAsync(CancellationToken.None);
        viaFallback = true;
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetLyrics GSMTC skipped (circuit breaker open) → Win32 fallback\n");
    }

    LogPaths.SafeAppend(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] Lyrics: title={status.Title}, artist={status.Artist}, viaFallback={viaFallback}\n");

    var lyrics = await ResolveLyricsForStatus(status, lyricService, viaFallback);
    return JsonSerializer.Serialize(BuildLyricsResponse(lyrics, viaFallback));
}

static async Task RunNativeLauncherAsync()
{
    var stdin = Console.OpenStandardInput();
    var stdout = Console.OpenStandardOutput();
    var lengthBuffer = new byte[4];
    if (!FillBuffer(stdin, lengthBuffer, lengthBuffer.Length)) return;

    var messageLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
    if (messageLength == 0 || messageLength > 1024 * 1024) return;
    var body = new byte[messageLength];
    if (!FillBuffer(stdin, body, body.Length)) return;

    long? requestId = null;
    try
    {
        using var request = JsonDocument.Parse(body);
        if (request.RootElement.TryGetProperty("_id", out var idElement))
            requestId = idElement.GetInt64();
    }
    catch (JsonException) { }

    var result = await EnsureServerAsync();
    var response = new Dictionary<string, object?>
    {
        ["type"] = "ensureServer",
        ["ok"] = result.ok,
        ["error"] = result.error,
    };
    if (requestId.HasValue) response["_id"] = requestId.Value;
    WriteMessage(stdout, JsonSerializer.Serialize(response));
}

static async Task<(bool ok, string? error)> EnsureServerAsync()
{
    if (await IsServerHealthyAsync()) return (true, null);

    var exePath = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        return (false, "Cannot locate xiaoxu-music-host.exe");

    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            Arguments = HostLaunchPolicy.BuildServerArguments(null),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        });
    }
    catch (Exception ex)
    {
        return (false, ex.Message);
    }

    for (var attempt = 0; attempt < 25; attempt++)
    {
        await Task.Delay(200);
        if (await IsServerHealthyAsync()) return (true, null);
    }
    return (false, "Server did not become healthy within 5 seconds");
}

static async Task<bool> IsServerHealthyAsync()
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        using var response = await client.GetAsync("http://localhost:17888/health");
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

static async Task<string> HandleGetState(
    WindowsMediaSessionService gsmtc,
    Win32MediaService fallback,
    LocalLyricService lyricService,
    Func<bool> shouldSkipGsmtc,
    MediaModeStore mediaModeStore)
{
    var mode = mediaModeStore.Get();
    string statusJson;
    if (mode == MediaMode.Win32 || (mode == MediaMode.Auto && shouldSkipGsmtc()))
    {
        var fast = await fallback.GetFastStatusAsync(CancellationToken.None);
        statusJson = SerializeFastWin32Status(fast, mode == MediaMode.Auto, fallback.LastPlaybackKnown, mode);
    }
    else
    {
        statusJson = await HandleGetStatus(gsmtc, fallback, shouldSkipGsmtc, mediaModeStore);
    }
    using var doc = JsonDocument.Parse(statusJson);
    var statusElement = doc.RootElement.Clone();
    var status = MediaStatusFromJson(statusElement);
    var viaFallback = statusElement.TryGetProperty("viaFallback", out var viaEl)
                      && viaEl.ValueKind == JsonValueKind.True;

    LogPaths.SafeAppend(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] State: title={status.Title}, artist={status.Artist}, viaFallback={viaFallback}\n");

    var lyrics = await ResolveLyricsForStatus(status, lyricService, viaFallback);
    var signature = ComputeStateSignature(status, lyrics?.Lrc);

    return JsonSerializer.Serialize(new
    {
        type = "state",
        status = statusElement,
        lyrics = BuildLyricsResponse(lyrics, viaFallback),
        signature,
        cached = lyrics?.Found ?? false
    });
}

static string SerializeFastWin32Status(MediaStatus status, bool viaFallback, bool playbackKnown, MediaMode requestedMode)
{
    return JsonSerializer.Serialize(new
    {
        type = "status",
        connected = status.Connected,
        source = status.Source,
        title = status.Title,
        artist = status.Artist,
        album = status.Album,
        coverUrl = (string?)null,
        hasCover = status.CoverUrl is not null || string.Equals(status.Source, "QQMusic", StringComparison.Ordinal),
        isPlaying = status.IsPlaying,
        playbackKnown,
        positionMs = status.PositionMs,
        durationMs = status.DurationMs,
        updatedAt = status.UpdatedAt.ToString("o"),
        viaFallback,
        requestedMode = MediaModeStore.ToWireValue(requestedMode),
        activeMode = "win32",
        mediaError = (object?)null,
    });
}

static async Task<xiaoxu_music_bridge.Lyrics.LyricResponse?> ResolveLyricsForStatus(
    MediaStatus status,
    LocalLyricService lyricService,
    bool viaFallback)
{
    // v3.2.10 hotfix: check the shared LyricPrefetchCache first. If a
    // TrackChanged prefetch already populated this (title, artist) — or
    // BridgeHttpServer did during a parallel HTTP poll — return it inline
    // (0ms) instead of paying the 1-3s QQ Music API round-trip.
    xiaoxu_music_bridge.Lyrics.LyricResponse? lyrics;
    bool cacheHit = LyricPrefetchCache.TryGet(status.Title, status.Artist, out var cached)
                    && cached != null
                    && cached.Found;
    if (cacheHit)
    {
        lyrics = cached;
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] Lyrics: cache HIT for title={status.Title} (lrcLen={lyrics!.Lrc?.Length ?? 0})\n");
    }
    else
    {
        var (l, lyricsTimedOut, _) = await WithTimeout(
            lyricService.GetCurrentLyricsAsync(status, CancellationToken.None), 5000, "GetCurrentLyricsAsync");
        lyrics = l;
        if (lyricsTimedOut)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] HandleGetLyrics TIMEOUT on lyric fetch — returning no-lyrics\n");
            return null;
        }
        // Populate the cache so subsequent TrackChanged-poll cycles hit
        // 0ms for this song. If !Found we don't cache (negative result
        // shouldn't lock out a later retry that might succeed).
        if (lyrics != null && lyrics.Found)
        {
            LyricPrefetchCache.Store(status.Title, status.Artist, lyrics);
        }
    }
    LogPaths.SafeAppend(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] Lyrics result: found={lyrics?.Found}, source={lyrics?.Source}, lrcLen={lyrics?.Lrc?.Length ?? 0}\n");
    return lyrics;
}

static object BuildLyricsResponse(xiaoxu_music_bridge.Lyrics.LyricResponse? lyrics, bool viaFallback)
{
    return new
    {
        type = "lyrics",
        found = lyrics?.Found ?? false,
        title = lyrics?.Title,
        artist = lyrics?.Artist,
        fileName = lyrics?.FileName,
        lrc = lyrics?.Lrc,
        source = lyrics?.Source,
        synced = lyrics?.Synced ?? false,
        viaFallback
    };
}

static MediaStatus MediaStatusFromJson(JsonElement status)
{
    string? StringProp(string name) =>
        status.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    bool BoolProp(string name) =>
        status.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    long LongProp(string name) =>
        status.TryGetProperty(name, out var el) && el.TryGetInt64(out var value)
            ? value
            : 0;

    DateTimeOffset updatedAt = DateTimeOffset.Now;
    var updatedAtRaw = StringProp("updatedAt");
    if (!string.IsNullOrWhiteSpace(updatedAtRaw)
        && DateTimeOffset.TryParse(updatedAtRaw, out var parsedUpdatedAt))
    {
        updatedAt = parsedUpdatedAt;
    }

    return new MediaStatus(
        Connected: BoolProp("connected"),
        Source: StringProp("source"),
        Title: StringProp("title"),
        Artist: StringProp("artist"),
        Album: StringProp("album"),
        CoverUrl: StringProp("coverUrl"),
        IsPlaying: BoolProp("isPlaying"),
        PositionMs: LongProp("positionMs"),
        DurationMs: LongProp("durationMs"),
        UpdatedAt: updatedAt);
}

static string ComputeStateSignature(MediaStatus status, string? lrc)
{
    var input = $"{status.Source}|{status.Title}|{status.Artist}|{status.Album}|{lrc}";
    var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
    var sb = new StringBuilder(16);
    for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
    return sb.ToString();
}

static async Task<string> HandleGetCover(
    WindowsMediaSessionService gsmtc,
    Win32MediaService fallback,
    QqMusicCoverLookupService coverLookup,
    ITunesCoverLookupService itunesLookup,
    Func<bool> shouldSkipGsmtcCover)
{
    // TIER 1  : GSMTC alive + native cover        → return base64 directly
    // TIER 2  : GSMTC alive, cover=null, QQ-ish    → QQ lookup → iTunes lookup
    // TIER 2b : GSMTC alive, cover=null, non-QQ    → Win32 title/artist → iTunes lookup
    // TIER 3  : GSMTC skipped / timed out          → Win32 title/artist → QQ lookup → iTunes lookup
    // TIER 4  : (built into 2/2b/3) iTunes is the last-resort source everywhere.
    //
    // Each source is tried in sequence; a miss flows to the next source rather than
    // short-circuiting the whole handler.

    // --- Local helpers ------------------------------------------------------

    // Run QQ then iTunes for a given title/artist. Returns serialized cover JSON
    // on the first hit, or null if both miss.
    async Task<string?> TryQqThenItunes(string? title, string? artist, bool viaFallback)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var (qqCover, qqTimedOut, _) = await WithTimeout(
            coverLookup.GetCoverAsync(title, artist, CancellationToken.None), 5000, "QqMusicCoverLookup");
        if (qqTimedOut)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] HandleGetCover QQ lookup TIMEOUT for title={title}\n");
        }
        else if (qqCover is not null)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] HandleGetCover QQ lookup OK bytes={qqCover.Bytes.Length} for title={title}\n");
            return SerializeCover(qqCover, viaFallback);
        }

        return await TryItunes(title, artist, viaFallback);
    }

    // Run iTunes for a given title/artist. Returns serialized cover JSON on hit, or null.
    async Task<string?> TryItunes(string? title, string? artist, bool viaFallback)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var (itCover, itTimedOut, _) = await WithTimeout(
            itunesLookup.GetCoverAsync(title, artist, CancellationToken.None), 5000, "ITunesCoverLookup");
        if (itTimedOut)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] HandleGetCover iTunes lookup TIMEOUT for title={title}\n");
            return null;
        }
        if (itCover is not null)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] HandleGetCover iTunes lookup OK bytes={itCover.Bytes.Length} for title={title}\n");
            return SerializeCover(itCover, viaFallback);
        }
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetCover iTunes lookup null for title={title}\n");
        return null;
    }

    static string SerializeCover(CoverImage cover, bool viaFallback) => JsonSerializer.Serialize(new
    {
        type = "cover",
        data = Convert.ToBase64String(cover.Bytes),
        contentType = cover.ContentType,
        viaFallback
    });

    static string NoCover(bool viaFallback) => JsonSerializer.Serialize(new
    {
        type = "cover",
        data = (string?)null,
        contentType = (string?)null,
        viaFallback
    });

    // --- GSMTC path (Tier 1 / 2 / 2b) ---------------------------------------

    if (!shouldSkipGsmtcCover())
    {
        var (cover, timedOut, coverSyncFault) = await WithTimeout(
            gsmtc.GetCurrentCoverAsync(CancellationToken.None), 1500, "GetCurrentCoverAsync");
        if (!timedOut)
        {
            GsmtcHealthTracker.RecordSuccess();
            if (cover is not null)
            {
                // Tier 1: native cover from GSMTC
                return SerializeCover(cover, viaFallback: false);
            }

            // GSMTC alive but no native cover. Read its metadata to decide the next source.
            // v3.2.5: ignore the sync fault from this internal call — we already
            // validated GSMTC works for cover, and any fault here would already
            // have been caught by the first WithTimeout above.
            var (gsmtcStatus, statusTimedOut, _) = await WithTimeout(
                gsmtc.GetStatusAsync(CancellationToken.None), 1500, "GetStatusAsync(forCover)");

            if (!statusTimedOut)
            {
                string sourceRaw = gsmtcStatus.Source ?? "(null)";
                bool isQqMusic = sourceRaw.Equals("QQMusic", StringComparison.OrdinalIgnoreCase)
                              || sourceRaw.Contains("QQ", StringComparison.OrdinalIgnoreCase)
                              || sourceRaw.Contains("Tencent", StringComparison.OrdinalIgnoreCase);
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] HandleGetCover Tier2 sourceRaw='{sourceRaw}' isQqMusic={isQqMusic} title={gsmtcStatus.Title}\n");

                if (isQqMusic && !string.IsNullOrWhiteSpace(gsmtcStatus.Title))
                {
                    // Tier 2: QQ Music → iTunes
                    var t2 = await TryQqThenItunes(gsmtcStatus.Title, gsmtcStatus.Artist, viaFallback: false);
                    if (t2 is not null) return t2;
                }
                else
                {
                    // Tier 2b: non-QQ source with no native cover. QQ lookup is pointless
                    // (song isn't in QQ's catalog by source), so go straight to iTunes.
                    // Prefer GSMTC's own title/artist; fall back to Win32 if GSMTC has none.
                    string? title = gsmtcStatus.Title;
                    string? artist = gsmtcStatus.Artist;
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        var win32 = await fallback.GetFastStatusAsync(CancellationToken.None);
                        title = win32.Title;
                        artist = win32.Artist;
                        LogPaths.SafeAppend(LogPaths.DebugLog,
                            $"[{DateTime.Now:HH:mm:ss}] HandleGetCover Tier2b using Win32 title={title}\n");
                    }
                    var t2b = await TryItunes(title, artist, viaFallback: false);
                    if (t2b is not null) return t2b;
                }
            }

            return NoCover(viaFallback: false);
        }
        GsmtcHealthTracker.RecordFailure("GetCurrentCoverAsync", coverSyncFault);
        GsmtcCircuitBreaker.OpenCover();
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetCover GSMTC TIMEOUT → Win32 fast fallback + QQ/iTunes cover lookup\n");
    }
    else
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetCover GSMTC skipped (circuit breaker open) → Win32 fast fallback + QQ/iTunes cover lookup\n");
    }

    // --- Tier 3: GSMTC unavailable — Win32 title/artist → QQ → iTunes -------

    var fbStatus = await fallback.GetFastStatusAsync(CancellationToken.None);
    if (string.IsNullOrWhiteSpace(fbStatus.Title))
    {
        return NoCover(viaFallback: true);
    }

    var tier3 = await TryQqThenItunes(fbStatus.Title, fbStatus.Artist, viaFallback: true);
    return tier3 ?? NoCover(viaFallback: true);
}

static string HandleSubscribeBeat(ref AudioBeatService? service, ref AudioDebugServer? debugServer, BridgeHttpServer bridgeHttp, Win32MediaService win32Fallback, Stream stdout, object stdoutLock)
{
    if (service is null)
    {
        service = new AudioBeatService(json =>
        {
            // Push beat message to stdout (thread-safe via lock)
            WriteMessage(stdout, json, stdoutLock);
        });
        service.Start();

        // Attach to the already-running debug server. The debug server was
        // started at host startup so Claude could monitor before the user
        // opened the wallpaper page.
        if (debugServer is not null)
        {
            service.AttachDebugServer(debugServer);
            debugServer.SetBeatService(service);
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] AudioBeatService ATTACHED to AudioDebugServer\n");
        }

        // v3.2.4: BridgeHttpServer's /beat/current endpoint needs the same
        // beat service. Attach after first subscribe so the http server can
        // answer beat polls from Lively / non-extension Chrome immediately.
        bridgeHttp.SetBeatService(service);

        // v3.2.7 (Track B): Wire the beat service into the Win32 fallback so
        // its virtual clock has a StateDetector-backed isPlaying signal.
        // Without this, the clock would default to optimistic "playing" forever.
        win32Fallback.AttachBeatService(service);

        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] AudioBeatService STARTED\n");
    }
    service.Subscribe();
    return JsonSerializer.Serialize(new { type = "subscribeBeat", ok = true });
}

static string HandleUnsubscribeBeat(ref AudioBeatService? service)
{
    service?.Unsubscribe();
    return JsonSerializer.Serialize(new { type = "unsubscribeBeat", ok = true });
}

static string HandleGetBeat(AudioBeatService? service)
{
    if (service is null)
    {
        return JsonSerializer.Serialize(new { type = "beat", bass = 0.0, volume = 0.0, pulse = 0.0, glow = 0.0 });
    }
    var result = service.GetSnapshot();
    return JsonSerializer.Serialize(new
    {
        type = "beat",
        bass = Math.Round(result.bass, 3),
        volume = Math.Round(result.volume, 3),
        pulse = Math.Round(result.pulse, 3),
        glow = Math.Round(result.glow, 3)
    });
}

static string HandleGetGsmtcHealth()
{
    var snap = GsmtcHealthTracker.GetSnapshot();
    return JsonSerializer.Serialize(new
    {
        type = "gsmtcHealth",
        consecutiveFailures = snap.ConsecutiveFailures,
        totalRestarts = snap.TotalRestarts,
        lastSuccessAt = snap.LastSuccessAt?.ToString("o"),
        isStuck = snap.IsStuck,
        isPermanentlyBroken = GsmtcHealthTracker.IsPermanentlyBroken,
        stuckThreshold = GsmtcHealthTracker.StuckThreshold,
    });
}

static string HandleSetMediaMode(MediaModeStore store, JsonElement request)
{
    var value = request.TryGetProperty("mode", out var element) ? element.GetString() : null;
    if (!MediaModeStore.TryParse(value, out var mode))
        return JsonSerializer.Serialize(new { type = "error", message = "Invalid media mode" });
    store.Set(mode);
    return JsonSerializer.Serialize(new { type = "mediaMode", mode = MediaModeStore.ToWireValue(mode) });
}

}
catch (Exception ex)
{
    LogPaths.SafeAppend(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
}
