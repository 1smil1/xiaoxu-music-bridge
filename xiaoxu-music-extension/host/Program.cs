using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using xiaoxu_music_bridge.Lyrics;
using xiaoxu_music_bridge.Media;

// Chrome Native Messaging format: 4-byte little-endian uint32 length + UTF-8 JSON body
// Read from stdin, write to stdout. No console window (OutputType=WinExe).

try
{
// Log IMMEDIATELY — before any service initialization
File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
    $"[{DateTime.Now:HH:mm:ss}] HOST STARTING, args=[{string.Join(", ", args)}], cwd={Environment.CurrentDirectory}\n");

// Chrome Native Messaging passes the extension origin as args[0] (e.g. "chrome-extension://...")
// Skip it — only use args that look like valid local paths
var lyricsDir = args.FirstOrDefault(a => !a.StartsWith("chrome-extension://") && Path.IsPathRooted(a));
var mediaService = new WindowsMediaSessionService();
var lyricService = new LocalLyricService(lyricsDir, new HttpClient());

File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
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

    // Log raw message for debugging
    File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
        $"[{DateTime.Now:HH:mm:ss}] RAW msg: len={messageLength}, hex={BitConverter.ToString(bodyBuffer[..Math.Min(bodyBuffer.Length, 64)])}, text={json}\n");

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
        File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
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
    var responseBytes = Encoding.UTF8.GetBytes(responseJson);
    var lengthBytes = BitConverter.GetBytes((uint)responseBytes.Length);
    stdout.Write(lengthBytes, 0, 4);
    stdout.Write(responseBytes, 0, responseBytes.Length);
    stdout.Flush();
}

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

static async Task<string> HandleGetStatus(IMediaSessionService mediaService)
{
    var status = await mediaService.GetStatusAsync(CancellationToken.None);

    File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
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

    var result = await mediaService.SendCommandAsync(command, CancellationToken.None);
    return JsonSerializer.Serialize(new
    {
        type = "controlResult",
        ok = result.Ok,
        error = result.Error
    });
}

static async Task<string> HandleGetLyrics(IMediaSessionService mediaService, LocalLyricService lyricService)
{
    var status = await mediaService.GetStatusAsync(CancellationToken.None);
    File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
        $"[{DateTime.Now:HH:mm:ss}] Lyrics: title={status.Title}, artist={status.Artist}\n");
    var lyrics = await lyricService.GetCurrentLyricsAsync(status, CancellationToken.None);
    File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
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
    var cover = await mediaService.GetCurrentCoverAsync(CancellationToken.None);
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

}
catch (Exception ex)
{
    File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
        $"[{DateTime.Now:HH:mm:ss}] FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
}
