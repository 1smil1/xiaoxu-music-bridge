using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using xiaoxu_music_bridge.Lyrics;
using xiaoxu_music_bridge.Media;
using xiaoxu_music_bridge.Audio;
using xiaoxu_music_bridge.Common;

// Chrome Native Messaging format: 4-byte little-endian uint32 length + UTF-8 JSON body
// Read from stdin, write to stdout. No console window (OutputType=WinExe).

try
{
// Log IMMEDIATELY — before any service initialization
LogPaths.SafeAppend(LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss}] HOST STARTING (logPath={LogPaths.DebugLog}), args=[{string.Join(", ", args)}], cwd={Environment.CurrentDirectory}\n");

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

// Audio beat service - captures system audio and computes bass/volume/pulse
// Started on first subscribeBeat command, stopped on unsubscribeBeat
AudioBeatService? beatService = null;
AudioDebugServer? debugServer = null;
var stdoutLock = new object();

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

while (true)
{
    // Read 4-byte length prefix (little-endian uint32)
    var lengthBuffer = new byte[4];
    if (!FillBuffer(stdin, lengthBuffer, 4))
    {
        break; // EOF — Chrome disconnected
    }

    var messageLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);

    // Read message body
    var bodyBuffer = new byte[messageLength];
    if (!FillBuffer(stdin, bodyBuffer, (int)messageLength))
    {
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
            "getStatus" => await HandleGetStatus(gsmtcService, win32Fallback, GsmtcCircuitBreaker.ShouldSkip),
            "control" => await HandleControl(gsmtcService, win32Fallback, GsmtcCircuitBreaker.ShouldSkip, doc.RootElement),
            "getLyrics" => await HandleGetLyrics(gsmtcService, win32Fallback, lyricService, GsmtcCircuitBreaker.ShouldSkip),
            "getCover" => await HandleGetCover(gsmtcService, win32Fallback, coverLookup, itunesCoverLookup, GsmtcCircuitBreaker.ShouldSkip),
            "subscribeBeat" => HandleSubscribeBeat(ref beatService, ref debugServer, stdout, stdoutLock),
            "unsubscribeBeat" => HandleUnsubscribeBeat(ref beatService),
            "getBeat" => HandleGetBeat(beatService),
            "getGsmtcHealth" => HandleGetGsmtcHealth(),
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

    // Write response
    WriteMessage(stdout, responseJson, stdoutLock);
}

// Cleanup
beatService?.Dispose();
debugServer?.Dispose();

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
static async Task<(T Result, bool TimedOut)> WithTimeout<T>(Task<T> task, int timeoutMs, string opName)
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
        return (default!, true);
    }
    return (await task, false);
}

static async Task<string> HandleGetStatus(
    WindowsMediaSessionService gsmtc,
    Win32MediaService fallback,
    Func<bool> shouldSkipGsmtc)
{
    // Try GSMTC first unless the circuit breaker says it's been failing
    if (!shouldSkipGsmtc())
    {
        var (status, timedOut) = await WithTimeout(
            gsmtc.GetStatusAsync(CancellationToken.None), 1500, "GetStatusAsync");
        if (!timedOut)
        {
            GsmtcHealthTracker.RecordSuccess();
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] Status (GSMTC): title={status.Title}, artist={status.Artist}, hasCover={status.CoverUrl is not null}\n");
            return SerializeStatus(status, viaFallback: false);
        }
        // Timed out — log failure and open the circuit breaker for 30s
        GsmtcHealthTracker.RecordFailure("GetStatusAsync");
        GsmtcCircuitBreaker.Open();
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
    return SerializeStatus(fbStatus, viaFallback: true);

    static string SerializeStatus(MediaStatus status, bool viaFallback)
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
            positionMs = status.PositionMs,
            durationMs = status.DurationMs,
            updatedAt = status.UpdatedAt.ToString("o"),
            viaFallback
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
    JsonElement request)
{
    var commandStr = request.GetProperty("command").GetString() ?? "";
    var command = commandStr switch
    {
        "play-pause" => ControlCommand.PlayPause,
        "next" => ControlCommand.Next,
        "previous" => ControlCommand.Previous,
        _ => throw new ArgumentException($"Unknown command: {commandStr}")
    };

    if (!shouldSkipGsmtc())
    {
        var (result, timedOut) = await WithTimeout(
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
        GsmtcHealthTracker.RecordFailure("SendCommandAsync");
        GsmtcCircuitBreaker.Open();
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
        viaFallback = true
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
        var (s, statusTimedOut) = await WithTimeout(
            gsmtc.GetStatusAsync(CancellationToken.None), 1500, "GetStatusAsync(forLyrics)");
        if (!statusTimedOut)
        {
            status = s;
            GsmtcHealthTracker.RecordSuccess();
        }
        else
        {
            GsmtcHealthTracker.RecordFailure("GetStatusAsync(forLyrics)");
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

    var (lyrics, lyricsTimedOut) = await WithTimeout(
        lyricService.GetCurrentLyricsAsync(status, CancellationToken.None), 5000, "GetCurrentLyricsAsync");
    if (lyricsTimedOut)
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetLyrics TIMEOUT on lyric fetch — returning no-lyrics\n");
        return JsonSerializer.Serialize(new
        {
            type = "lyrics",
            found = false,
            title = (string?)null,
            artist = (string?)null,
            fileName = (string?)null,
            lrc = (string?)null,
            source = (string?)null,
            synced = false
        });
    }
    LogPaths.SafeAppend(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] Lyrics result: found={lyrics.Found}, source={lyrics.Source}, lrcLen={lyrics.Lrc?.Length ?? 0}\n");
    return JsonSerializer.Serialize(new
    {
        type = "lyrics",
        found = lyrics.Found,
        title = lyrics.Title,
        artist = lyrics.Artist,
        fileName = lyrics.FileName,
        lrc = lyrics.Lrc,
        source = lyrics.Source,
        synced = lyrics.Synced,
        viaFallback
    });
}

static async Task<string> HandleGetCover(
    WindowsMediaSessionService gsmtc,
    Win32MediaService fallback,
    QqMusicCoverLookupService coverLookup,
    ITunesCoverLookupService itunesLookup,
    Func<bool> shouldSkipGsmtc)
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

        var (qqCover, qqTimedOut) = await WithTimeout(
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

        var (itCover, itTimedOut) = await WithTimeout(
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

    if (!shouldSkipGsmtc())
    {
        var (cover, timedOut) = await WithTimeout(
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
            var (gsmtcStatus, statusTimedOut) = await WithTimeout(
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
                        var win32 = await fallback.GetStatusAsync(CancellationToken.None);
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
        GsmtcHealthTracker.RecordFailure("GetCurrentCoverAsync");
        GsmtcCircuitBreaker.Open();
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetCover GSMTC TIMEOUT → Win32 fallback + QQ/iTunes cover lookup\n");
    }
    else
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetCover GSMTC skipped (circuit breaker open) → Win32 fallback + QQ/iTunes cover lookup\n");
    }

    // --- Tier 3: GSMTC unavailable — Win32 title/artist → QQ → iTunes -------

    var fbStatus = await fallback.GetStatusAsync(CancellationToken.None);
    if (string.IsNullOrWhiteSpace(fbStatus.Title))
    {
        return NoCover(viaFallback: true);
    }

    var tier3 = await TryQqThenItunes(fbStatus.Title, fbStatus.Artist, viaFallback: true);
    return tier3 ?? NoCover(viaFallback: true);
}

static string HandleSubscribeBeat(ref AudioBeatService? service, ref AudioDebugServer? debugServer, Stream stdout, object stdoutLock)
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
        stuckThreshold = GsmtcHealthTracker.StuckThreshold,
    });
}

}
catch (Exception ex)
{
    LogPaths.SafeAppend(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
}
