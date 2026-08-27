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
// v3.2.5: BuildStateResponseAsync now runs cover + lyrics in parallel via
// Task.WhenAll (was sequential: 5s cover + 5s lyrics = up to 15s total worst
// case, which exceeded the dashboard's poll budget and timed out the curl
// session). Both single-source timeouts reduced so the parallel worst case
// is ≤ 5s. WithTimeout now returns the sync exception so callers can
// classify permanent GSMTC failures.
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
    public const string HostVersion = "3.6.2";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private const string AllowMethods = "GET, PUT, POST, OPTIONS";
    private const string AllowHeaders = "Content-Type";
    private const string MaxAge = "86400";

    private readonly WindowsMediaSessionService _gsmtc;
    private readonly Win32MediaService _win32Fallback;
    private readonly BridgeEndpointSettings _endpoint;
    private readonly MediaModeStore _mediaModeStore;
    private readonly GsmtcRecoveryService _gsmtcRecovery = new();
    private readonly object _lyricLock = new();
    private LocalLyricService? _lyricService;
    private readonly object _coverLock = new();
    private QqMusicCoverLookupService? _qqCover;
    private ITunesCoverLookupService? _itunesCover;
    private readonly object _beatLock = new();
    private AudioBeatService? _beatService;

    // v3.2.7: per-song cover + lyric caches backing the fire-and-forget
    // /state/current path. The previous single-slot cache only stored the
    // most recent track's data — now we keep one slot per (title, artist)
    // key so song switches don't have to re-fetch the previous song's
    // cover/lyrics synchronously.
    //
    // ReaderWriterLockSlim protects concurrent access from many poll
    // requests. Cache is per-process; survives across requests.
    private readonly Dictionary<string, string?> _coverCache = new();
    private readonly ReaderWriterLockSlim _coverCacheLock = new();
    // v3.2.10 hotfix: lyric cache moved to Common.LyricPrefetchCache (static)
    // so the Native Messaging path (Program.cs HandleGetLyrics) and the HTTP
    // path (this server) share the same dictionary.
    private string _lastBackgroundFetchKey = ""; // dedupes background fetches

    /// <summary>Legacy single-slot cover cache — kept only for direct lookups in /cover/current.</summary>
    private string _coverCacheKey = "";
    private string? _coverCacheValue;
    private readonly object _coverCacheLegacyLock = new();

    private HttpListener? _listener;
    private Thread? _thread;
    private CancellationTokenSource _stopCts = new();
    private volatile bool _running;

    public bool IsRunning => _running;

    public BridgeHttpServer(
        WindowsMediaSessionService gsmtc,
        Win32MediaService win32Fallback,
        BridgeEndpointSettings endpoint)
    {
        _gsmtc = gsmtc;
        _win32Fallback = win32Fallback;
        _endpoint = endpoint;
        _mediaModeStore = new MediaModeStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xiaoxu-music-host"));

        // v3.2.10 hotfix: lyric prefetch subscription moved to Program.cs
        // (top-level statement) so it benefits the Native Messaging path
        // too. The original v3.2.10 prefetch here only helped the HTTP
        // path; the dashboard extension's injected.js intercepts fetch to
        // 127.0.0.1:17888 and reroutes through Program.cs HandleGetLyrics,
        // bypassing this server's cache entirely. Both paths now share
        // Common.LyricPrefetchCache.
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
            if (_stopCts.IsCancellationRequested)
            {
                _stopCts.Dispose();
                _stopCts = new CancellationTokenSource();
            }

            _listener = new HttpListener();
            // v3.2.7 hotfix: bind via the loopback hostname so the listener
            // accepts BOTH ::1 (IPv6) and 127.0.0.1 (IPv4). Windows resolves
            // `localhost` to both families; Chrome's Happy Eyeballs (RFC 6555)
            // tries IPv6 first. Previously we bound only 127.0.0.1, which made
            // the IPv6 attempt fail with connection-refused — and in some
            // Chrome versions that surfaces as 503 to JS even though the IPv4
            // fallback would succeed. Using `localhost` defers to the OS for
            // both families.
            //
            // Loopback-only — spec § 安全边界: local API must NOT be reachable
            // from the LAN. Windows Firewall covers the gap if a future
            // maintainer accidentally widens the prefix.
            _listener.Prefixes.Add(_endpoint.ListenerPrefix);
            _listener.Start();
            _running = true;

            _thread = new Thread(Loop) { IsBackground = true, Name = "BridgeHttpServer" };
            _thread.Start();

            Log($"BridgeHttpServer started on {_endpoint.ListenerPrefix} (IPv4 + IPv6 loopback)");
        }
        catch (Exception ex)
        {
            Log($"BridgeHttpServer START FAILED: {ex.Message}");
        }
    }

    public void Stop()
    {
        _running = false;
        try { _stopCts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose()
    {
        Stop();
    }

    // --- v3.2.10 hotfix: TrackChanged prefetch -----------------------------
    //
    // Subscription moved to Program.cs so the Native Messaging path
    // (HandleGetLyrics, used by extension's injected.js-routed fetches)
    // benefits too. Both paths share Common.LyricPrefetchCache.

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
                if (path == "/audio/stream"
                    && !AudioStreamOriginPolicy.IsAllowed(req.Headers["Origin"]))
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    return;
                }

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
                case "PUT":
                    await HandlePut(ctx, path);
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
                    version = HostVersion,
                    pid = Environment.ProcessId,
                    startedAt = _startedAt,
                    uptimeSeconds = Math.Max(0, (long)(DateTimeOffset.Now - _startedAt).TotalSeconds)
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
                    WriteRawJson(ctx, 200, BuildBeatJson());
                }
                break;

            case "/audio/stream":
                {
                    await HandleAudioStream(ctx);
                }
                break;

            case "/cover/current":
                {
                    await HandleCoverCurrent(ctx);
                }
                break;

            // Arbitrary-song lyrics lookup for listen-together guests. The
            // host's LiveKit frames carry title/artist; the guest forwards
            // them here so its OWN bridge runs the same local LRC → QQ →
            // NetEase → lrclib chain instead of replicating it in JS.
            case "/lyrics/query":
                {
                    var q = ctx.Request.QueryString;
                    var qTitle = q["title"];
                    var qArtist = q["artist"];
                    if (string.IsNullOrWhiteSpace(qTitle))
                    {
                        WriteJson(ctx, 400, new { error = "title is required" });
                        break;
                    }

                    // Reuse the same prefetch cache /state/current uses so a
                    // guest listening to what the host already resolved pays
                    // 0ms instead of another QQ round-trip.
                    if (!(LyricPrefetchCache.TryGet(qTitle, qArtist, out var queried)
                          && queried != null && queried.Found))
                    {
                        queried = await ResolveLyricsAsync(qTitle, qArtist, false);
                        if (queried != null && queried.Found)
                        {
                            LyricPrefetchCache.Store(qTitle, qArtist, queried);
                        }
                    }

                    WriteJson(ctx, 200, queried ?? new LyricResponse(
                        Found: false, Title: qTitle, Artist: qArtist,
                        FileName: null, Lrc: null, Source: null, Synced: false));
                }
                break;

            case "/settings/media-mode":
                WriteJson(ctx, 200, new { mode = MediaModeStore.ToWireValue(_mediaModeStore.Get()) });
                break;

            case "/gsmtc/health":
                {
                    var health = GsmtcHealthTracker.GetSnapshot();
                    WriteJson(ctx, 200, new
                    {
                        mode = MediaModeStore.ToWireValue(_mediaModeStore.Get()),
                        isPermanentlyBroken = GsmtcHealthTracker.IsPermanentlyBroken,
                        consecutiveFailures = health.ConsecutiveFailures,
                        totalRestarts = health.TotalRestarts,
                        isStuck = health.IsStuck,
                        lastSuccessAt = health.LastSuccessAt,
                    });
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

            case "/debug/lyric-clock":
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    if (body.Length > 4000) body = body.Substring(0, 4000);
                    Log($"[LyricClockClient] {body}");
                    WriteJson(ctx, 200, new { ok = true });
                }
                break;

            case "/gsmtc/repair":
                {
                    var result = await _gsmtcRecovery.RepairAsync(CancellationToken.None);
                    WriteJson(ctx, 200, result);
                }
                break;

            default:
                WriteJson(ctx, 404, new { error = "not found", path });
                break;
        }
    }

    // --- Status / state ----------------------------------------------------

    /// <summary>
    /// Mirrors Program.cs HandleGetState: try GSMTC with 1.5s timeout, fall
    /// back to the fast Win32 path on circuit-breaker open or timeout. Returns
    /// the full MediaStatus JSON shape plus a viaFallback flag.
    /// </summary>
    private async Task<(object json, bool viaFallback)> BuildStatusJsonAsync()
    {
        var mode = _mediaModeStore.Get();
        if (MediaModePolicy.ShouldTryGsmtc(mode, GsmtcCircuitBreaker.ShouldSkip()))
        {
            var (status, timedOut, syncFault) = await WithTimeout(
                _gsmtc.GetStatusAsync(CancellationToken.None), 1500, "HttpGetStatusAsync");
            if (!timedOut && status != null && !string.IsNullOrWhiteSpace(status.Title))
            {
                GsmtcHealthTracker.RecordSuccess();
                return (SerializeStatus(status, false, true, mode, MediaMode.Gsmtc), false);
            }

            if (timedOut)
            {
                GsmtcHealthTracker.RecordFailure("HttpGetStatusAsync", syncFault);
                GsmtcCircuitBreaker.Open();
            }

            if (!MediaModePolicy.ShouldUseWin32(mode, gsmtcSucceeded: false))
            {
                var errorCode = syncFault != null ? "gsmtc_activation_failed"
                    : timedOut ? "gsmtc_timeout" : "no_media_session";
                return (SerializeStatus(status ?? MediaStatus.NoMedia(), false, false, mode, MediaMode.Gsmtc,
                    new { code = errorCode, message = GsmtcErrorMessage(errorCode), stage = "probe" }), false);
            }

            Log("GSMTC unavailable → Win32 fallback (HTTP auto mode)");
        }
        else
        {
            Log(mode == MediaMode.Win32
                ? "GSMTC bypassed by forced Win32 mode (HTTP)"
                : "GSMTC skipped (breaker open) → Win32 fast fallback (HTTP auto mode)");
        }

        var fb = await _win32Fallback.GetFastStatusAsync(CancellationToken.None);
        var viaFallback = mode == MediaMode.Auto;
        return (SerializeStatus(fb, viaFallback, _win32Fallback.LastPlaybackKnown, mode, MediaMode.Win32), viaFallback);
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

        // v3.2.7 hotfix (Track C2): Lyrics are awaited INLINE so the
        // dashboard sees them on the FIRST poll after a song change. The
        // previous fire-and-forget version showed "暂无歌词" for 1-3 seconds
        // because the response was sent before the background fetch
        // completed. Lyric queries are lightweight (~1.5s HTTP GET) so
        // adding them back to the critical path is acceptable.
        //
        // v3.2.10 hotfix: lyrics are checked against Common.LyricPrefetchCache
        // first. The prefetch subscription (in Program.cs) fires on song
        // change and writes here, so the first poll after the switch hits
        // 0ms instead of paying the 1-3s QQ Music API round-trip inline.
        //
        // Cover (50-200KB image bytes) stays fire-and-forget — image
        // downloads are what made /state/current feel laggy in v3.2.6.
        var coverCacheKey = CoverIdentity.Create(title, artist);

        // Lyrics: shared cache first (Program.cs writes here on song change).
        LyricResponse? lyrics;
        bool haveLyrics = LyricPrefetchCache.TryGet(title, artist, out lyrics)
                          && lyrics != null && lyrics.Found;
        if (!haveLyrics)
        {
            // Cache miss — fetch inline so the first poll returns real lyrics.
            try
            {
                lyrics = await ResolveLyricsAsync(title, artist, viaFallback);
                if (lyrics != null && lyrics.Found)
                {
                    LyricPrefetchCache.Store(title, artist, lyrics);
                }
                Log($"lyrics fetched inline: found={lyrics?.Found} source={lyrics?.Source} title='{title}'");
            }
            catch (Exception ex)
            {
                Log($"inline lyrics fetch failed: {ex.GetType().Name}: {ex.Message}");
                lyrics = null;
            }
        }

        // Cover: cached lookup first (fast path).
        _coverCacheLock.EnterReadLock();
        bool haveCover = _coverCache.TryGetValue(coverCacheKey, out var coverDataUrl);
        _coverCacheLock.ExitReadLock();
        if (!haveCover) coverDataUrl = null;

        // Cover background fetch — only kick once per cache key while a
        // lookup is in flight. Polls can arrive faster than the image lookup
        // completes, so do not start duplicate QQ/iTunes downloads.
        bool needCoverFetch = !haveCover && coverCacheKey != _lastBackgroundFetchKey;
        if (needCoverFetch) _lastBackgroundFetchKey = coverCacheKey;

        if (needCoverFetch && !string.IsNullOrWhiteSpace(title))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var newCover = await ResolveCoverDataUrlAsync(title, artist, viaFallback);
                    _coverCacheLock.EnterWriteLock();
                    _coverCache[coverCacheKey] = newCover;
                    _coverCacheLock.ExitWriteLock();
                }
                catch (Exception ex)
                {
                    if (_lastBackgroundFetchKey == coverCacheKey) _lastBackgroundFetchKey = "";
                    Log($"background cover fetch failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        string sig = ComputeSignature(title, artist, coverDataUrl, lyrics?.Lrc);
        object? audioClock = null;
        AudioBeatService? beat;
        lock (_beatLock) beat = _beatService;
        if (beat != null)
        {
            var clock = beat.GetAudioClock();
            audioClock = new
            {
                sampleIndex = clock.SampleIndex,
                sampleRate = clock.SampleRate,
                monotonicMs = clock.MonotonicMs,
                discontinuityId = clock.DiscontinuityId,
            };
        }

        return new
        {
            status = statusJson,
            coverDataUrl,
            lyrics,
            signature = sig,
            cached = haveCover || haveLyrics,
            audioClock,
        };
    }

    /// <summary>
    /// v3.2.7 background-fetch variant. Mirrors what the previous in-line
    /// BuildStateResponseAsync did (cover + lyrics in parallel) but runs
    /// off the response critical path.
    /// </summary>
    private async Task<(string? cover, LyricResponse? lyrics)> FetchCoverAndLyricsAsync(
        string? title, string? artist, bool viaFallback)
    {
        var coverTask = ResolveCoverDataUrlAsync(title, artist, viaFallback);
        var lyricsTask = ResolveLyricsAsync(title, artist, viaFallback);
        await Task.WhenAll(coverTask, lyricsTask);
        return (coverTask.Result, lyricsTask.Result);
    }

    // --- Cover -------------------------------------------------------------

    private async Task<string?> ResolveCoverDataUrlAsync(string? title, string? artist, bool viaFallback)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Cache lookup (avoid hammering QQ/iTunes on every poll — 1Hz from
        // the dashboard means 60 hits/min without caching).
        var key = CoverIdentity.Create(title, artist);
        lock (_coverCacheLock)
        {
            if (_coverCacheKey == key) return _coverCacheValue;
        }

        CoverImage? cover = null;
        QqMusicCoverLookupService? qq;
        ITunesCoverLookupService? itunes;
        lock (_coverLock) { qq = _qqCover; itunes = _itunesCover; }

        // v3.2.5: Single-source timeouts reduced from 5s → 3s. Cover + lyrics
        // now run in parallel (Task.WhenAll in BuildStateResponseAsync), so
        // the /state/current total budget is max(cover, lyrics) ≈ 3s, well
        // under the dashboard's poll deadline.
        // Mirror HandleGetCover tier 3/2b shape: try QQ then iTunes.
        if (qq != null)
        {
            var (c, timedOut, _) = await WithTimeout(
                qq.GetCoverAsync(title, artist, CancellationToken.None), 3000, "HttpQqCoverLookup");
            if (timedOut) Log($"QQ cover lookup timed out for {title}");
            else if (c != null) cover = c;
        }
        if (cover == null && itunes != null)
        {
            var (c, timedOut, _) = await WithTimeout(
                itunes.GetCoverAsync(title, artist, CancellationToken.None), 3000, "HttpItunesCoverLookup");
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
            var (c, _, _) = await WithTimeout(
                qq.GetCoverAsync(title, artist, CancellationToken.None), 3000, "HttpQqCover");
            if (c != null) cover = c;
        }
        if (cover == null && itunes != null)
        {
            var (c, _, _) = await WithTimeout(
                itunes.GetCoverAsync(title, artist, CancellationToken.None), 3000, "HttpItunesCover");
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

        var (lyrics, timedOut, _) = await WithTimeout(
            svc.GetCurrentLyricsAsync(status, CancellationToken.None), 4000, "HttpGetLyrics");
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

    private string BuildBeatJson()
    {
        AudioBeatService? svc;
        lock (_beatLock) svc = _beatService;
        return BeatHttpResponsePolicy.SelectJson(svc?.GetFullSnapshotJson());
    }

    private async Task HandleAudioStream(HttpListenerContext ctx)
    {
        if (!AudioStreamOriginPolicy.IsAllowed(ctx.Request.Headers["Origin"]))
        {
            WriteJson(ctx, 403, new { error = "origin not allowed" });
            return;
        }

        AudioBeatService? svc;
        lock (_beatLock) svc = _beatService;
        var availabilityStatus = AudioStreamAvailabilityPolicy.StatusCode(
            svc != null,
            svc?.IsCaptureReady == true);
        if (availabilityStatus != 200 || svc == null)
        {
            WriteJson(ctx, availabilityStatus, new { error = "audio capture unavailable" });
            return;
        }

        if (!svc.TrySubscribeAudio(out var subscription) || subscription == null)
        {
            WriteJson(ctx, 409, new { error = "audio stream already subscribed" });
            return;
        }

        using (subscription)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.SendChunked = true;
            ctx.Response.KeepAlive = true;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

            try
            {
                while (_running && !_stopCts.IsCancellationRequested)
                {
                    var packet = await subscription.ReadAsync(
                        TimeSpan.FromSeconds(1),
                        _stopCts.Token)
                        ?? PcmAudioBroadcaster.BuildKeepAlivePacket(svc.GetAudioClock());
                    await ctx.Response.OutputStream.WriteAsync(packet, _stopCts.Token);
                    await ctx.Response.OutputStream.FlushAsync(_stopCts.Token);
                }
            }
            catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (
                ex is HttpListenerException
                or IOException
                or EndOfStreamException
                or ObjectDisposedException)
            {
                Log($"Audio stream disconnected: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }
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

        var mode = _mediaModeStore.Get();
        if (MediaModePolicy.ShouldTryGsmtc(mode, GsmtcCircuitBreaker.ShouldSkip()))
        {
            var (result, timedOut, syncFault) = await WithTimeout(
                _gsmtc.SendCommandAsync(cmd, CancellationToken.None), 1500, "HttpSendCommand");
            if (!timedOut)
            {
                GsmtcHealthTracker.RecordSuccess();
                return (result.Ok, result.Error, false);
            }
            GsmtcHealthTracker.RecordFailure("HttpSendCommand", syncFault);
            GsmtcCircuitBreaker.Open();
            if (!MediaModePolicy.ShouldUseWin32(mode, gsmtcSucceeded: false))
                return (false, syncFault?.Message ?? "GSMTC control timed out", false);
        }

        if (mode == MediaMode.Gsmtc)
            return (false, "GSMTC control unavailable", false);

        var fb = await _win32Fallback.SendCommandAsync(cmd, CancellationToken.None);
        return (fb.Ok, fb.Error, mode == MediaMode.Auto);
    }

    // --- Helpers -----------------------------------------------------------

    private static object SerializeStatus(
        MediaStatus status,
        bool viaFallback,
        bool playbackKnown,
        MediaMode requestedMode,
        MediaMode activeMode,
        object? mediaError = null)
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
            playbackKnown,
            positionMs = status.PositionMs,
            durationMs = status.DurationMs,
            updatedAt = status.UpdatedAt.ToString("o"),
            viaFallback,
            requestedMode = MediaModeStore.ToWireValue(requestedMode),
            activeMode = MediaModeStore.ToWireValue(activeMode),
            mediaError,
        };
    }

    private static string GsmtcErrorMessage(string code) => code switch
    {
        "gsmtc_timeout" => "GSMTC request timed out",
        "gsmtc_activation_failed" => "GSMTC could not be activated",
        _ => "No current GSMTC media session",
    };

    private async Task HandlePut(HttpListenerContext ctx, string path)
    {
        if (path != "/settings/media-mode")
        {
            WriteJson(ctx, 404, new { error = "not found", path });
            return;
        }

        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        try
        {
            using var document = JsonDocument.Parse(body);
            var value = document.RootElement.TryGetProperty("mode", out var element) ? element.GetString() : null;
            if (!MediaModeStore.TryParse(value, out var mode))
            {
                WriteJson(ctx, 400, new { error = "invalid media mode", allowed = new[] { "auto", "win32", "gsmtc" } });
                return;
            }

            _mediaModeStore.Set(mode);
            WriteJson(ctx, 200, new { mode = MediaModeStore.ToWireValue(mode) });
        }
        catch (JsonException)
        {
            WriteJson(ctx, 400, new { error = "invalid JSON body" });
        }
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

    private static async Task<(T result, bool timedOut, Exception? syncFault)> WithTimeout<T>(Task<T> task, int ms, string opName)
    {
        var winner = await Task.WhenAny(task, Task.Delay(ms));
        if (winner != task) return (default!, true, null);
        // See Program.cs WithTimeout for rationale: GSMTC throws synchronously
        // (FileNotFoundException on missing WinRT projection DLL). Treat as
        // timeout so the caller can fall back to Win32 instead of bubbling a
        // 500 up to Lively/Chrome.
        // v3.2.5: also return the sync exception so callers can classify it
        // as a permanent failure (FileNotFoundException → stop the restart loop).
        try
        {
            return (await task, false, null);
        }
        catch (Exception ex)
        {
            Log($"{opName} sync fault: {ex.GetType().Name}: {ex.Message}");
            return (default!, true, ex);
        }
    }

    // v3.2.7 hotfix: camelCase naming so the frontend (which expects lowercase
    // keys like `found`, `lrc`, `fileName`) can read LyricResponse fields back.
    // Without this, the record's PascalCase properties (Found/Lrc/FileName) come
    // through as-is, every LyricResponse field is undefined on the dashboard, and
    // the lyric column shows "暂无歌词" even though /state/current actually carries
    // the full LRC body. All other responses are already anonymous objects with
    // lowercase keys, so this policy only changes the LyricResponse record shape.
    private static readonly JsonSerializerOptions HttpJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static void WriteJson(HttpListenerContext ctx, int status, object body)
    {
        var json = JsonSerializer.Serialize(body, HttpJsonOptions);
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

    private static void WriteRawJson(HttpListenerContext ctx, int status, string json)
    {
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
            try { ctx.Response.Close(); } catch { }
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
        else if (AudioStreamOriginPolicy.IsAllowed(origin))
        {
            // Echo the requested origin so browser CORS checks can expose
            // loopback responses to the approved site and development origins.
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
        // v3.2.7 hotfix: Chrome 130+ enforces Private Network Access (PNA) for
        // https:// → http://localhost fetches. Without this header the browser
        // returns 503 to JS and the dashboard's /state/current polling never
        // succeeds → lyrics frozen on first song. Echo on both the actual
        // response and the OPTIONS preflight.
        ctx.Response.Headers.Add("Access-Control-Allow-Private-Network", "true");
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
