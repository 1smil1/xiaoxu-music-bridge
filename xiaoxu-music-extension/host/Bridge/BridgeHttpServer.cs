// BridgeHttpServer — HTTP server on http://127.0.0.1:17888/ that exposes
// the bridge's state directly to a browser, so:
//   - Lively Wallpaper (no Chrome extension → no Native Messaging) can show
//     the playing track + drive the lyric scroller
//   - Chrome without the extension installed (https://xiaoxu.xin) can still
//     connect to the host without going through the extension's SW pipe
//
// v3.2.4: Added per spec (docs/windows-music-bridge-spec.md § 本地 API 设计).
// Mirrors the GSMTC primary + Win32 fallback pipeline used by Program.cs's
// Native Messaging handlers — same circuit breaker, same 1.5s timeout, same
// cover tier chain. Lives on 127.0.0.1 only (spec § 安全边界).
//
// Endpoints (per spec):
//   GET  /health              → { ok, name, version }
//   GET  /status              → MediaStatus (spec § GET /status)
//   GET  /state/current       → { status, coverDataUrl, lyrics, signature, cached }
//   GET  /beat/current        → { bass, volume, pulse, glow, ... }
//   GET  /cover/current       → image bytes
//   POST /control/play-pause  → { ok }
//   POST /control/next        → { ok }
//   POST /control/previous    → { ok }
//
// CORS: allow-list per spec (http(s)://xiaoxu.xin + localhost dev origins).
// OPTIONS preflight returns 204 with the relevant headers and a 24h Max-Age
// so the browser caches the result and avoids a second round-trip on every
// state poll.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using xiaoxu_music_bridge.Audio;
using xiaoxu_music_bridge.Common;
using xiaoxu_music_bridge.Lyrics;
using xiaoxu_music_bridge.Media;

namespace xiaoxu_music_bridge.Bridge;

public sealed class BridgeHttpServer : IDisposable
{
    private const int Port = 17888;
    private const string HostVersion = "3.2.4";

    // Spec § CORS 要求. localhost dev origins let `npm run dev` work on the
    // user's machine during frontend iteration without modifying this list.
    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://xiaoxu.xin",
        "https://xiaoxu.xin",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
        "http://localhost:3000",
        "http://127.0.0.1:3000",
    };

    private const string AllowMethods = "GET, POST, OPTIONS";
    private const string AllowHeaders = "Content-Type";
    private const string MaxAge = "86400";

    private readonly WindowsMediaSessionService _gsmtc;
    private readonly Win32MediaService _win32Fallback;
    private readonly object _lyricLock = new();
    private LocalLyricService? _lyricService;
    private readonly object _coverLock = new();
    private QqMusicCoverLookupService? _qqCover;
    private ITunesCoverLookupService? _itunesCover;
    private readonly object _beatLock = new();
    private AudioBeatService? _beatService;

    // Track cover cache to avoid hammering QQ/iTunes on every /state/current poll.
    // Keyed by (title, artist, content-type); value is base64 data URL or null
    // (negative cache). Cache is per-process; survives across requests.
    private readonly Dictionary<string, string?> _coverCache = new();
    private readonly object _coverCacheLock = new();
    private string _coverCacheKey = "";
    private string? _coverCacheValue;

    private HttpListener? _listener;
    private Thread? _thread;
    private volatile bool _running;

    public BridgeHttpServer(
        WindowsMediaSessionService gsmtc,
        Win32MediaService win32Fallback)
    {
        _gsmtc = gsmtc;
        _win32Fallback = win32Fallback;
    }

    /// <summary>
    /// Attach services that may not be ready at startup (lyrics, cover lookup,
    /// beat service). All setters are thread-safe and may be called repeatedly
    /// to refresh the reference (matching how AudioDebugServer.SetBeatService
    /// works — see Audio/AudioDebugServer.cs).
    /// </summary>
    public void SetLyricService(LocalLyricService? svc)
    {
        lock (_lyricLock) _lyricService = svc;
    }

    public void SetCoverLookupServices(QqMusicCoverLookupService? qq, ITunesCoverLookupService? itunes)
    {
        lock (_coverLock)
        {
            _qqCover = qq;
            _itunesCover = itunes;
        }
    }

    public void SetBeatService(AudioBeatService? svc)
    {
        lock (_beatLock) _beatService = svc;
    }

    public void Start()
    {
        if (_running) return;
        try
        {
            _listener = new HttpListener();
            // 127.0.0.1 only — spec § 安全边界: local API must NOT be reachable
            // from the LAN. Localhost + Windows Firewall covers the gap if a
            // future maintainer accidentally widens the prefix.
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _running = true;

            _thread = new Thread(Loop) { IsBackground = true, Name = "BridgeHttpServer" };
            _thread.Start();

            Log($"BridgeHttpServer started on http://127.0.0.1:{Port}/");
        }
        catch (Exception ex)
        {
            Log($"BridgeHttpServer START FAILED: {ex.Message}");
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    // --- Main loop ---------------------------------------------------------

    private void Loop()
    {
        while (_running && _listener != null)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { return; }

            // Detach handling from the listener thread so a slow /state/current
            // can't block other browsers (Lively wallpaper + the user's Chrome
            // both share this server).
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (string.IsNullOrEmpty(path)) path = "/";

            // CORS preflight — must short-circuit BEFORE any auth/validation so
            // the browser's OPTIONS round-trip doesn't hit a 405 from the
            // route table below.
            if (req.HttpMethod == "OPTIONS")
            {
                WriteCorsHeaders(ctx, includeAllowHeaders: true);
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            // All other responses get CORS headers attached unconditionally
            // (Access-Control-Allow-Origin: * is fine here because the server
            // is loopback-only; the allow-list gates which origins get the
            // echoed value).
            WriteCorsHeaders(ctx, includeAllowHeaders: false);

            switch (req.HttpMethod)
            {
                case "GET":
                    await HandleGet(ctx, path);
                    break;
                case "POST":
                    await HandlePost(ctx, path);
                    break;
                default:
                    WriteJson(ctx, 405, new { error = "method not allowed", method = req.HttpMethod });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"Handler error: {ex.GetType().Name}: {ex.Message}");
            try { ctx.Response.Close(); } catch { /* connection may already be torn down */ }
        }
    }

    private async Task HandleGet(HttpListenerContext ctx, string path)
    {
        switch (path)
        {
            case "/health":
            case "/":
                WriteJson(ctx, 200, new
                {
                    ok = true,
                    name = "xiaoxu-music-bridge",
                    version = HostVersion
                });
                break;

            case "/status":
                {
                    var (json, viaFallback) = await BuildStatusJsonAsync();
                    WriteJson(ctx, 200, json);
                }
                break;

            case "/state/current":
                {
                    var (json, viaFallback) = await BuildStatusJsonAsync();
                    var stateResp = await BuildStateResponseAsync(json, viaFallback);
                    WriteJson(ctx, 200, stateResp);
                }
                break;

            case "/beat/current":
                {
                    WriteJson(ctx, 200, BuildBeatJson());
                }
                break;

            case "/cover/current":
                {
                    await HandleCoverCurrent(ctx);
                }
                break;

            default:
                WriteJson(ctx, 404, new { error = "not found", path });
                break;
        }
    }

    private async Task HandlePost(HttpListenerContext ctx, string path)
    {
        switch (path)
        {
            case "/control/play-pause":
            case "/control/next":
            case "/control/previous":
                {
                    var cmd = path.Substring("/control/".Length);
                    var result = await SendControlAsync(cmd);
                    WriteJson(ctx, result.ok ? 200 : 500, new { ok = result.ok, error = result.error, viaFallback = result.viaFallback });
                }
                break;

            default:
                WriteJson(ctx, 404, new { error = "not found", path });
                break;
        }
    }

    // --- Status / state ----------------------------------------------------

    /// <summary>
    /// Mirrors Program.cs HandleGetStatus: try GSMTC with 1.5s timeout, fall
    /// back to Win32MediaService on circuit-breaker open or timeout. Returns
    /// the full MediaStatus JSON shape plus a viaFallback flag.
    /// </summary>
    private async Task<(object json, bool viaFallback)> BuildStatusJsonAsync()
    {
        if (!GsmtcCircuitBreaker.ShouldSkip())
        {
            var (status, timedOut) = await WithTimeout(
                _gsmtc.GetStatusAsync(CancellationToken.None), 1500, "HttpGetStatusAsync");
            if (!timedOut)
            {
                GsmtcHealthTracker.RecordSuccess();
                return (SerializeStatus(status, viaFallback: false), false);
            }
            GsmtcHealthTracker.RecordFailure("HttpGetStatusAsync");
            GsmtcCircuitBreaker.Open();
            Log("GSMTC timeout → Win32 fallback (HTTP)");
        }
        else
        {
            Log("GSMTC skipped (breaker open) → Win32 fallback (HTTP)");
        }

        var fb = await _win32Fallback.GetStatusAsync(CancellationToken.None);
        return (SerializeStatus(fb, viaFallback: true), true);
    }

    private async Task<object> BuildStateResponseAsync(object statusJson, bool viaFallback)
    {
        // The status JSON we just serialized carries title/artist/source/etc.
        // Parse it back via JsonElement so we can attach cover + lyrics +
        // signature without re-fetching GSMTC.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(statusJson));
        var status = doc.RootElement.Clone();

        string? title = status.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString() : null;
        string? artist = status.TryGetProperty("artist", out var aEl) && aEl.ValueKind == JsonValueKind.String
            ? aEl.GetString() : null;

        var coverDataUrl = await ResolveCoverDataUrlAsync(title, artist, viaFallback);
        var lyrics = await ResolveLyricsAsync(title, artist, viaFallback);

        // Signature = content hash. Frontend skips re-render when signature
        // matches the previous response. Excludes positionMs/isPlaying so a
        // seek/pause re-renders even if everything else is identical.
        string sig = ComputeSignature(title, artist, coverDataUrl, lyrics?.Lrc);

        return new
        {
            status = statusJson,
            coverDataUrl,
            lyrics,
            signature = sig,
            cached = false,
        };
    }

    // --- Cover -------------------------------------------------------------

    private async Task<string?> ResolveCoverDataUrlAsync(string? title, string? artist, bool viaFallback)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Cache lookup (avoid hammering QQ/iTunes on every poll — 1Hz from
        // the dashboard means 60 hits/min without caching).
        var key = $"{title}|{artist}|{viaFallback}";
        lock (_coverCacheLock)
        {
            if (_coverCacheKey == key) return _coverCacheValue;
        }

        CoverImage? cover = null;
        QqMusicCoverLookupService? qq;
        ITunesCoverLookupService? itunes;
        lock (_coverLock) { qq = _qqCover; itunes = _itunesCover; }

        // Mirror HandleGetCover tier 3/2b shape: try QQ then iTunes.
        if (qq != null)
        {
            var (c, timedOut) = await WithTimeout(
                qq.GetCoverAsync(title, artist, CancellationToken.None), 5000, "HttpQqCoverLookup");
            if (timedOut) Log($"QQ cover lookup timed out for {title}");
            else if (c != null) cover = c;
        }
        if (cover == null && itunes != null)
        {
            var (c, timedOut) = await WithTimeout(
                itunes.GetCoverAsync(title, artist, CancellationToken.None), 5000, "HttpItunesCoverLookup");
            if (timedOut) Log($"iTunes cover lookup timed out for {title}");
            else if (c != null) cover = c;
        }

        string? dataUrl = cover != null
            ? $"data:{cover.ContentType};base64,{Convert.ToBase64String(cover.Bytes)}"
            : null;

        lock (_coverCacheLock)
        {
            _coverCacheKey = key;
            _coverCacheValue = dataUrl;
        }
        return dataUrl;
    }

    private async Task HandleCoverCurrent(HttpListenerContext ctx)
    {
        var (json, _) = await BuildStatusJsonAsync();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var title = doc.RootElement.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString() : null;
        var artist = doc.RootElement.TryGetProperty("artist", out var aEl) && aEl.ValueKind == JsonValueKind.String
            ? aEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(title))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        QqMusicCoverLookupService? qq;
        ITunesCoverLookupService? itunes;
        lock (_coverLock) { qq = _qqCover; itunes = _itunesCover; }

        CoverImage? cover = null;
        if (qq != null)
        {
            var (c, _) = await WithTimeout(
                qq.GetCoverAsync(title, artist, CancellationToken.None), 5000, "HttpQqCover");
            if (c != null) cover = c;
        }
        if (cover == null && itunes != null)
        {
            var (c, _) = await WithTimeout(
                itunes.GetCoverAsync(title, artist, CancellationToken.None), 5000, "HttpItunesCover");
            if (c != null) cover = c;
        }

        if (cover == null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = cover.ContentType;
        ctx.Response.ContentLength64 = cover.Bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(cover.Bytes);
        ctx.Response.Close();
    }

    // --- Lyrics ------------------------------------------------------------

    private async Task<LyricResponse?> ResolveLyricsAsync(string? title, string? artist, bool viaFallback)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        LocalLyricService? svc;
        lock (_lyricLock) svc = _lyricService;
        if (svc == null)
        {
            return new LyricResponse(
                Found: false, Title: null, Artist: null,
                FileName: null, Lrc: null, Source: null, Synced: false);
        }

        // We need a MediaStatus-shaped input to feed LocalLyricService; build
        // a minimal one with just title/artist (other fields aren't used by
        // the lyric matcher).
        var status = new MediaStatus(
            Connected: true, Source: "HTTP",
            Title: title, Artist: artist, Album: null, CoverUrl: null,
            IsPlaying: false, PositionMs: 0, DurationMs: 0,
            UpdatedAt: DateTimeOffset.Now);

        var (lyrics, timedOut) = await WithTimeout(
            svc.GetCurrentLyricsAsync(status, CancellationToken.None), 5000, "HttpGetLyrics");
        if (timedOut)
        {
            Log("Lyrics lookup timed out");
            return new LyricResponse(
                Found: false, Title: title, Artist: artist,
                FileName: null, Lrc: null, Source: null, Synced: false);
        }
        return lyrics;
    }

    // --- Beat --------------------------------------------------------------

    private object BuildBeatJson()
    {
        AudioBeatService? svc;
        lock (_beatLock) svc = _beatService;
        if (svc == null)
        {
            return new { bass = 0.0, volume = 0.0, pulse = 0.0, glow = 0.0 };
        }
        var (bass, volume, pulse, glow) = svc.GetSnapshot();
        return new
        {
            bass = Math.Round(bass, 3),
            volume = Math.Round(volume, 3),
            pulse = Math.Round(pulse, 3),
            glow = Math.Round(glow, 3),
        };
    }

    // --- Control -----------------------------------------------------------

    private async Task<(bool ok, string? error, bool viaFallback)> SendControlAsync(string command)
    {
        var cmd = command switch
        {
            "play-pause" => ControlCommand.PlayPause,
            "next" => ControlCommand.Next,
            "previous" => ControlCommand.Previous,
            _ => throw new ArgumentException($"Unknown command: {command}")
        };

        if (!GsmtcCircuitBreaker.ShouldSkip())
        {
            var (result, timedOut) = await WithTimeout(
                _gsmtc.SendCommandAsync(cmd, CancellationToken.None), 1500, "HttpSendCommand");
            if (!timedOut)
            {
                GsmtcHealthTracker.RecordSuccess();
                return (result.Ok, result.Error, false);
            }
            GsmtcHealthTracker.RecordFailure("HttpSendCommand");
            GsmtcCircuitBreaker.Open();
        }

        var fb = await _win32Fallback.SendCommandAsync(cmd, CancellationToken.None);
        return (fb.Ok, fb.Error, true);
    }

    // --- Helpers -----------------------------------------------------------

    private static object SerializeStatus(MediaStatus status, bool viaFallback)
    {
        bool hasCover = status.CoverUrl is not null
            || string.Equals(status.Source, "QQMusic", StringComparison.Ordinal);
        return new
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
            viaFallback,
        };
    }

    private static string ComputeSignature(string? title, string? artist, string? coverDataUrl, string? lrc)
    {
        // Cheap, stable, content-addressed. SHA-1 is fine — this is just for
        // dedup, not for security. Truncated to 16 hex chars to keep the
        // /state/current response body small.
        var input = $"{title}|{artist}|{coverDataUrl}|{lrc}";
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static async Task<(T result, bool timedOut)> WithTimeout<T>(Task<T> task, int ms, string opName)
    {
        var winner = await Task.WhenAny(task, Task.Delay(ms));
        if (winner != task) return (default!, true);
        // See Program.cs WithTimeout for rationale: GSMTC throws synchronously
        // (FileNotFoundException on missing WinRT projection DLL). Treat as
        // timeout so the caller can fall back to Win32 instead of bubbling a
        // 500 up to Lively/Chrome.
        try
        {
            return (await task, false);
        }
        catch (Exception ex)
        {
            Log($"{opName} sync fault: {ex.GetType().Name}: {ex.Message}");
            return (default!, true);
        }
    }

    private static void WriteJson(HttpListenerContext ctx, int status, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        try
        {
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* swallow */ }
        }
    }

    private static void WriteCorsHeaders(HttpListenerContext ctx, bool includeAllowHeaders)
    {
        var origin = ctx.Request.Headers["Origin"];
        string allowOrigin;
        if (string.IsNullOrEmpty(origin))
        {
            // Same-origin / curl / native fetch — no Origin header. Echo "*"
            // is safe because we only listen on 127.0.0.1.
            allowOrigin = "*";
        }
        else if (AllowedOrigins.Contains(origin))
        {
            // Echo the requested origin so the browser's credentialed mode
            // and response body work. Per spec § CORS 要求.
            allowOrigin = origin;
        }
        else
        {
            // Reject unknown origins with a non-matching value. Browser will
            // refuse to surface the response body to JS.
            allowOrigin = "null";
        }
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", allowOrigin);
        ctx.Response.Headers.Add("Vary", "Origin");
        ctx.Response.Headers.Add("Access-Control-Allow-Methods", AllowMethods);
        if (includeAllowHeaders)
        {
            ctx.Response.Headers.Add("Access-Control-Allow-Headers", AllowHeaders);
            ctx.Response.Headers.Add("Access-Control-Max-Age", MaxAge);
        }
    }

    private static void Log(string msg)
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] [BridgeHttpServer] {msg}\n");
    }
}