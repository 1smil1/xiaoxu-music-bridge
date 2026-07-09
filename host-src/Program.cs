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
File.AppendAllText(LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss}] HOST STARTING (logPath={LogPaths.DebugLog}), args=[{string.Join(", ", args)}], cwd={Environment.CurrentDirectory}\n");

// Chrome Native Messaging passes the extension origin as args[0] (e.g. "chrome-extension://...")
// Skip it — only use args that look like valid local paths
var lyricsDir = args.FirstOrDefault(a => !a.StartsWith("chrome-extension://") && Path.IsPathRooted(a));
var mediaService = new WindowsMediaSessionService();
var lyricService = new LocalLyricService(lyricsDir, new HttpClient());

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
File.AppendAllText(LogPaths.DebugLog,
    $"[{DateTime.Now:HH:mm:ss}] AudioDebugServer STARTED on http://localhost:17889/ (waiting for AudioBeatService)\n");

File.AppendAllText(LogPaths.DebugLog,
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
        File.AppendAllText(LogPaths.DebugLog,
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
            "getStatus" => await HandleGetStatus(mediaService),
            "control" => await HandleControl(mediaService, doc.RootElement),
            "getLyrics" => await HandleGetLyrics(mediaService, lyricService),
            "getCover" => await HandleGetCover(mediaService),
            "subscribeBeat" => HandleSubscribeBeat(ref beatService, ref debugServer, stdout, stdoutLock),
            "unsubscribeBeat" => HandleUnsubscribeBeat(ref beatService),
            "getBeat" => HandleGetBeat(beatService),
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
        File.AppendAllText(LogPaths.DebugLog,
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
            File.AppendAllText(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] LATE {opName} {status}\n");
        });
        return (default!, true);
    }
    return (await task, false);
}

static async Task<string> HandleGetStatus(IMediaSessionService mediaService)
{
    var (status, timedOut) = await WithTimeout(
        mediaService.GetStatusAsync(CancellationToken.None), 5000, "GetStatusAsync");
    if (timedOut)
    {
        File.AppendAllText(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetStatus TIMEOUT — returning empty status (GSMTC likely hung)\n");
        return JsonSerializer.Serialize(new
        {
            type = "status",
            connected = false,
            source = (string?)null,
            title = (string?)null,
            artist = (string?)null,
            album = (string?)null,
            coverUrl = (string?)null,
            hasCover = false,
            isPlaying = false,
            positionMs = 0,
            durationMs = 0,
            updatedAt = (string?)null
        });
    }

    File.AppendAllText(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] Status: title={status.Title}, artist={status.Artist}, hasCover={status.CoverUrl is not null}\n");

    return JsonSerializer.Serialize(new
    {
        type = "status",
        connected = status.Connected,
        source = status.Source,
        title = status.Title,
        artist = status.Artist,
        album = status.Album,
        coverUrl = (string?)null,        // cover fetched separately via getCover
        hasCover = status.CoverUrl is not null,
        isPlaying = status.IsPlaying,
        positionMs = status.PositionMs,
        durationMs = status.DurationMs,
        updatedAt = status.UpdatedAt.ToString("o")
    });
}

static async Task<string> HandleControl(IMediaSessionService mediaService, JsonElement request)
{
    var commandStr = request.GetProperty("command").GetString() ?? "";
    var command = commandStr switch
    {
        "play-pause" => ControlCommand.PlayPause,
        "next" => ControlCommand.Next,
        "previous" => ControlCommand.Previous,
        _ => throw new ArgumentException($"Unknown command: {commandStr}")
    };

    var (result, timedOut) = await WithTimeout(
        mediaService.SendCommandAsync(command, CancellationToken.None), 5000, "SendCommandAsync");
    if (timedOut)
    {
        File.AppendAllText(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleControl TIMEOUT — control={commandStr}\n");
        return JsonSerializer.Serialize(new { type = "controlResult", ok = false, error = "control timeout (GSMTC hung)" });
    }
    return JsonSerializer.Serialize(new
    {
        type = "controlResult",
        ok = result.Ok,
        error = result.Error
    });
}

static async Task<string> HandleGetLyrics(IMediaSessionService mediaService, LocalLyricService lyricService)
{
    var (status, statusTimedOut) = await WithTimeout(
        mediaService.GetStatusAsync(CancellationToken.None), 5000, "GetStatusAsync(forLyrics)");
    if (statusTimedOut)
    {
        File.AppendAllText(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetLyrics TIMEOUT on status — returning no-lyrics\n");
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
    File.AppendAllText(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] Lyrics: title={status.Title}, artist={status.Artist}\n");
    var (lyrics, lyricsTimedOut) = await WithTimeout(
        lyricService.GetCurrentLyricsAsync(status, CancellationToken.None), 5000, "GetCurrentLyricsAsync");
    if (lyricsTimedOut)
    {
        File.AppendAllText(LogPaths.DebugLog,
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
    File.AppendAllText(LogPaths.DebugLog,
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
        synced = lyrics.Synced
    });
}

static async Task<string> HandleGetCover(IMediaSessionService mediaService)
{
    var (cover, timedOut) = await WithTimeout(
        mediaService.GetCurrentCoverAsync(CancellationToken.None), 5000, "GetCurrentCoverAsync");
    if (timedOut)
    {
        File.AppendAllText(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] HandleGetCover TIMEOUT — returning null cover\n");
        return JsonSerializer.Serialize(new { type = "cover", data = (string?)null, contentType = (string?)null });
    }
    if (cover is null)
    {
        return JsonSerializer.Serialize(new { type = "cover", data = (string?)null, contentType = (string?)null });
    }

    return JsonSerializer.Serialize(new
    {
        type = "cover",
        data = Convert.ToBase64String(cover.Bytes),
        contentType = cover.ContentType
    });
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
            File.AppendAllText(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] AudioBeatService ATTACHED to AudioDebugServer\n");
        }

        File.AppendAllText(LogPaths.DebugLog,
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

}
catch (Exception ex)
{
    File.AppendAllText(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss}] FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
}
