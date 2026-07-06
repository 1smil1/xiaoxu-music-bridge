using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using xiaoxu_music_bridge.Lyrics;
using xiaoxu_music_bridge.Media;

// File-based debug log — stdout is reserved for Chrome Native Messaging protocol.
// Writing anything else to stdout (e.g. ASP.NET Core's default "Now listening on..." log)
// corrupts the 4-byte length-prefixed JSON protocol → Chrome disconnects the host immediately.
var logPath = Path.Combine(AppContext.BaseDirectory, "host-debug.log");
Action<string> log = msg =>
{
    try { File.AppendAllText(logPath, $"{DateTime.Now:O} [pid={Environment.ProcessId}] {msg}\n"); }
    catch { }
};

try
{
    log($"=== host start ===");
    log($"args: {string.Join(" | ", args)}");

    // Chrome Native Messaging passes the extension origin as args[0] (e.g. "chrome-extension://...")
    // Skip it — only use args that look like valid local paths for lyrics directory.
    var lyricsDir = args.FirstOrDefault(a => !a.StartsWith("chrome-extension://") && Path.IsPathRooted(a));
    log($"lyricsDir: {lyricsDir ?? "(null)"}");

    var mediaService = new WindowsMediaSessionService();
    var lyricService = new LocalLyricService(lyricsDir, new HttpClient());
    var cache = new StateCache();

    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls("http://localhost:17888");

    // CRITICAL: stdout belongs to Chrome Native Messaging — kill ALL default log providers
    // (Console, Debug, EventSource, EventLog). Without this, Kestrel writes
    // "Now listening on: http://localhost:17888" to stdout, corrupting the protocol.
    builder.Logging.ClearProviders();

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy
                .WithOrigins(
                    "http://xiaoxu.xin",
                    "https://xiaoxu.xin",
                    "http://localhost:5173",
                    "http://127.0.0.1:5173",
                    "http://localhost:3000",
                    "http://127.0.0.1:3000")
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
    });

    var app = builder.Build();

    app.Use(async (context, next) =>
    {
        if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        {
            context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        }
        await next();
    });

    app.UseCors();

    app.MapGet("/health", () => new HealthResponse(true, "xiaoxu-music-host", "2.0.0"));

    // Combined state endpoint — one round-trip returns status + cover (data URL) + lyrics + signature.
    app.MapGet("/state/current", async (CancellationToken ct) =>
    {
        return await cache.GetOrRefreshAsync(mediaService, lyricService, ct);
    });

    app.MapGet("/status", async (CancellationToken ct) => await mediaService.GetStatusAsync(ct));

    app.MapGet("/cover/current", async (CancellationToken ct) =>
    {
        var cover = await mediaService.GetCurrentCoverAsync(ct);
        return cover is null
            ? Results.NotFound()
            : Results.File(cover.Bytes, cover.ContentType);
    });

    app.MapGet("/lyrics/current", async (CancellationToken ct) =>
    {
        var status = await mediaService.GetStatusAsync(ct);
        return await lyricService.GetCurrentLyricsAsync(status, ct);
    });

    app.MapPost("/control/{cmd}", async (string cmd, CancellationToken ct) =>
    {
        var command = cmd switch
        {
            "play-pause" => ControlCommand.PlayPause,
            "next" => ControlCommand.Next,
            "previous" => ControlCommand.Previous,
            _ => throw new ArgumentException($"Unknown command: {cmd}")
        };
        var result = await mediaService.SendCommandAsync(command, ct);
        return result.Ok
            ? Results.Ok(new ControlResponse(true))
            : Results.Problem(result.Error ?? "Media command failed", statusCode: StatusCodes.Status503ServiceUnavailable);
    });

    // Start Kestrel HTTP server (non-blocking)
    await app.StartAsync();
    log("HTTP server started on http://localhost:17888");

    // Native messaging loop on background thread — reads stdin, handles ping/pong, exits on EOF.
    var nmThread = new Thread(() => RunNativeMessagingLoop(log, () =>
    {
        try { app.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
    }))
    {
        IsBackground = true
    };
    nmThread.Start();

    // Main thread blocks until stdin EOF (triggers app.StopAsync) or Ctrl+C
    await app.WaitForShutdownAsync();
    log("host shutdown complete");
}
catch (Exception ex)
{
    log($"FATAL: {ex}");
    throw;
}

static void RunNativeMessagingLoop(Action<string> log, Action onExit)
{
    try
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        log("native messaging loop started, waiting for stdin");

        while (true)
        {
            // Read 4-byte little-endian uint32 length prefix
            var lengthBuffer = new byte[4];
            if (!FillBuffer(stdin, lengthBuffer, 4))
            {
                log("stdin EOF on length read — Chrome disconnected");
                break;
            }

            var messageLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
            log($"received message length={messageLength}");
            if (messageLength == 0 || messageLength > 10 * 1024 * 1024)
            {
                log($"skipping malformed message (length={messageLength})");
                continue;
            }

            var bodyBuffer = new byte[messageLength];
            if (!FillBuffer(stdin, bodyBuffer, (int)messageLength))
            {
                log("stdin EOF on body read — Chrome disconnected mid-message");
                break;
            }

            var json = Encoding.UTF8.GetString(bodyBuffer);
            log($"received: {json}");

            string responseJson;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var type = doc.RootElement.GetProperty("type").GetString();
                responseJson = type switch
                {
                    "ping" => JsonSerializer.Serialize(new { type = "pong" }),
                    _ => JsonSerializer.Serialize(new { type = "pong", echo = type })
                };
            }
            catch (Exception ex)
            {
                log($"message parse error: {ex.Message}");
                responseJson = JsonSerializer.Serialize(new { type = "error", message = ex.Message });
            }

            var responseBytes = Encoding.UTF8.GetBytes(responseJson);
            var respLenBytes = BitConverter.GetBytes((uint)responseBytes.Length);
            stdout.Write(respLenBytes, 0, 4);
            stdout.Write(responseBytes, 0, responseBytes.Length);
            stdout.Flush();
            log($"sent response: {responseJson}");
        }
    }
    catch (Exception ex)
    {
        log($"native messaging loop exception: {ex}");
    }
    onExit();
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

internal sealed record HealthResponse(bool Ok, string Name, string Version);
internal sealed record ControlResponse(bool Ok);

public sealed class StateCache
{
    private string? _lastSignature;
    private MediaStatus? _cachedStatus;
    private string? _cachedCoverDataUrl;
    private LyricResponse? _cachedLyrics;
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly object _lock = new();

    public async Task<StateResponse> GetOrRefreshAsync(IMediaSessionService media, LocalLyricService lyrics, CancellationToken ct)
    {
        var status = await media.GetStatusAsync(ct);
        var sig = $"{status.Artist ?? ""}|{status.Title ?? ""}|{status.Album ?? ""}|{status.Source ?? ""}";

        lock (_lock)
        {
            // Signature unchanged AND cached within last 1s — return cached payload, skip cover/lyric queries.
            if (sig == _lastSignature && (DateTime.UtcNow - _lastRefresh).TotalSeconds < 1.0)
            {
                return new StateResponse(_cachedStatus!, _cachedCoverDataUrl, _cachedLyrics, sig, Cached: true);
            }
        }

        // Signature changed OR stale — re-fetch cover + lyrics.
        string? coverDataUrl = null;
        if (status.CoverUrl is not null)
        {
            try
            {
                var cover = await media.GetCurrentCoverAsync(ct);
                if (cover is not null)
                {
                    coverDataUrl = $"data:{cover.ContentType};base64,{Convert.ToBase64String(cover.Bytes)}";
                }
            }
            catch
            {
                // Cover fetch failure is not fatal — leave null, frontend shows fallback.
            }
        }

        var lrc = await lyrics.GetCurrentLyricsAsync(status, ct);

        lock (_lock)
        {
            _lastSignature = sig;
            _cachedStatus = status;
            _cachedCoverDataUrl = coverDataUrl;
            _cachedLyrics = lrc;
            _lastRefresh = DateTime.UtcNow;
        }

        return new StateResponse(status, coverDataUrl, lrc, sig, Cached: false);
    }
}

public sealed record StateResponse(
    MediaStatus Status,
    string? CoverDataUrl,
    LyricResponse? Lyrics,
    string Signature,
    bool Cached);

public partial class Program;
