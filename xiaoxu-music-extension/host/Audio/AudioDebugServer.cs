// AudioDebugServer — HTTP server on localhost:17889 that exposes the same
// AudioBeatService state that's pushed to Chrome via BEAT_UPDATE.
// Allows the user (via debug panel) and Claude (via curl) to see the
// EXACT same data, ensuring what Claude monitors matches what the user sees.
//
// Endpoints:
//   GET /              — help / route list
//   GET /snapshot      — current AudioFrame JSON (single frame, full v3 schema)
//   GET /live          — last 100 frames (time series for trend monitoring)
//   GET /health        — process health + audio device + recent RMS stats
//   GET /raw           — minimal { rms, peak, samplesPerSec, isPlaying, ts }
//   GET /devices       — list active render endpoints (debug Bluetooth capture issue)
//
// Also appends a one-line summary to %USERPROFILE%\xiaoxu-audio-live.log
// every ~1s for tail -f monitoring.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Audio;

public sealed class AudioDebugServer : IDisposable
{
    private const int Port = 17889;
    private static readonly string LiveLogPath = LogPaths.AudioLiveLog;
    private const int MaxHistory = 100;
    private const int HealthWindowSec = 10;

    private readonly object _beatServiceLock = new();
    private AudioBeatService? _beatService;
    private readonly object _historyLock = new();
    private readonly LinkedList<Snapshot> _history = new();

    private HttpListener? _listener;
    private Thread? _thread;
    private Thread? _logThread;
    private volatile bool _running;
    private int _totalRequests;
    private readonly Stopwatch _processClock = Stopwatch.StartNew();

    /// <summary>Construct with the beat service. May be null at startup and attached later via SetBeatService.</summary>
    public AudioDebugServer(AudioBeatService? beatService)
    {
        _beatService = beatService;
    }

    /// <summary>Attach (or replace) the AudioBeatService reference. Called from AudioBeatService.AttachDebugServer via Program.cs wiring.</summary>
    public void SetBeatService(AudioBeatService? service)
    {
        lock (_beatServiceLock) _beatService = service;
    }

    public void Start()
    {
        if (_running) return;
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();

            _running = true;

            _thread = new Thread(Loop) { IsBackground = true, Name = "AudioDebugServer" };
            _thread.Start();

            _logThread = new Thread(LogLoop) { IsBackground = true, Name = "AudioDebugLogger" };
            _logThread.Start();

            Log($"AudioDebugServer started on http://localhost:{Port}/");
        }
        catch (Exception ex)
        {
            Log($"AudioDebugServer START FAILED: {ex.Message}");
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    /// <summary>Append a snapshot to the rolling history (called by AudioBeatService after each push).</summary>
    public void RecordSnapshot(Snapshot s)
    {
        lock (_historyLock)
        {
            _history.AddLast(s);
            while (_history.Count > MaxHistory) _history.RemoveFirst();
        }
    }

    private void Loop()
    {
        while (_running && _listener != null)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { return; }

            Interlocked.Increment(ref _totalRequests);
            try
            {
                string path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
                if (path == "") path = "/";
                string body = path switch
                {
                    "/snapshot" => GetSnapshotJson(),
                    "/live" => GetLiveJson(),
                    "/health" => GetHealthJson(),
                    "/raw" => GetRawJson(),
                    "/devices" => GetDevicesJson(),
                    "/" or "/help" => HelpText(),
                    _ => "{\"error\":\"not found\"}",
                };
                int status = path == "/" || path == "/help" ? 200
                    : path is "/snapshot" or "/live" or "/health" or "/raw" or "/devices" ? 200
                    : 404;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Log($"AudioDebugServer handler error: {ex.Message}");
                try { ctx.Response.Close(); } catch { }
            }
        }
    }

    private void LogLoop()
    {
        while (_running)
        {
            try
            {
                Thread.Sleep(1000);
                string json = GetRawJson();
                LogPaths.SafeAppend(LiveLogPath, json + "\n");
            }
            catch (Exception ex)
            {
                Log($"AudioDebugServer log thread error: {ex.Message}");
            }
        }
    }

    private AudioBeatService? TryGetBeatService()
    {
        lock (_beatServiceLock) return _beatService;
    }

    private string GetSnapshotJson()
    {
        var svc = TryGetBeatService();
        if (svc is null) return "{\"status\":\"waiting\",\"reason\":\"AudioBeatService not started yet — open Chrome wallpaper page to subscribe\"}";
        return svc.GetFullSnapshotJson();
    }

    private string GetLiveJson()
    {
        Snapshot[] arr;
        lock (_historyLock) arr = _history.ToArray();
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < arr.Length; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append(arr[i].ToJson());
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string GetHealthJson()
    {
        Snapshot[] arr;
        lock (_historyLock) arr = _history.ToArray();

        var svc = TryGetBeatService();

        // Last N seconds stats
        var windowMs = HealthWindowSec * 1000;
        var now = Environment.TickCount64;
        var recent = arr.Where(s => now - s.TickMs < windowMs).ToArray();

        float rmsMin = recent.Length > 0 ? recent.Min(s => s.Rms) : 0;
        float rmsMax = recent.Length > 0 ? recent.Max(s => s.Rms) : 0;
        float rmsAvg = recent.Length > 0 ? recent.Average(s => s.Rms) : 0;
        int peakCount = recent.Count(s => s.Rms > 0.005f);
        double recentSec = recent.Length > 0
            ? (recent[^1].TickMs - recent[0].TickMs) / 1000.0
            : 0;
        double observedHz = recentSec > 0 ? recent.Length / recentSec : 0;

        return JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            uptime_sec = (int)(_processClock.ElapsedMilliseconds / 1000),
            requests = _totalRequests,
            history_count = arr.Length,
            history_capacity = MaxHistory,
            service_started = svc is not null,
            audio = svc is null ? null : new
            {
                sample_rate = svc.SampleRate,
                channels = svc.Channels,
                device = svc.DeviceName,
                rms_min = MathF.Round(rmsMin, 5),
                rms_max = MathF.Round(rmsMax, 5),
                rms_avg = MathF.Round(rmsAvg, 5),
                peak_frames = peakCount,
                observed_hz = MathF.Round((float)observedHz, 2),
                rms_threshold = 0.005f,
                is_above_threshold = rmsMax > 0.005f,
            },
            bpm = arr.Length > 0 ? arr[^1].Bpm : 0f,
            bpm_confidence = arr.Length > 0 ? arr[^1].BpmConfidence : 0f,
            is_playing = arr.Length > 0 && arr[^1].IsPlaying,
        });
    }

    private string GetRawJson()
    {
        Snapshot last;
        lock (_historyLock)
        {
            if (_history.Count == 0) return "{}";
            last = _history.Last!.Value;
        }
        return last.ToJson();
    }

    private string GetDevicesJson()
    {
        var svc = TryGetBeatService();
        if (svc is null)
        {
            return JsonSerializer.Serialize(new
            {
                service_started = false,
                hint = "AudioBeatService not started yet — open Chrome wallpaper page to subscribe. The /devices endpoint will list render endpoints once WASAPI has been initialized."
            });
        }

        var devices = svc.GetRenderDevices();
        return JsonSerializer.Serialize(new
        {
            service_started = true,
            capturing = svc.DeviceName,
            sample_rate = svc.SampleRate,
            channels = svc.Channels,
            count = devices.Count,
            devices = devices.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                state = d.State,
                is_default = d.IsDefault,
                sample_rate = d.SampleRate,
                channels = d.Channels,
            }),
            hint = "If 'capturing' is your speakers but audio plays on Bluetooth, that's the bug — WASAPI loopback captures the default Multimedia endpoint. Switch default playback to the device playing audio, then restart the host.",
        });
    }

    private static string HelpText()
    {
        return JsonSerializer.Serialize(new
        {
            service = "xiaoxu-music-bridge debug",
            endpoints = new[]
            {
                "GET /snapshot — single current AudioFrame (full v3 schema)",
                "GET /live     — last 100 frames (time series, oldest → newest)",
                "GET /health   — process + audio device + RMS stats over last 10s",
                "GET /raw      — latest snapshot only (minimal)",
                "GET /devices  — list active render endpoints (capture-target diagnostics)",
            },
            live_log = LiveLogPath,
            hint = "The /snapshot response is the SAME JSON that BEAT_UPDATE pushes to Chrome.",
        });
    }

    public void Dispose()
    {
        Stop();
    }

    private static void Log(string msg)
    {
        // SafeAppend handles file-lock contention when Chrome respawns the host
        // mid-overlap with the dying old instance — never throws, never blocks.
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] [AudioDebugServer] {msg}\n");
    }
}

/// <summary>
/// Minimal per-frame snapshot stored by AudioDebugServer. Mirrors the most
/// important fields of the v3 payload so the debug endpoint can answer
/// without holding a lock on the full AudioFeatures/OnsetSet/BeatState.
/// </summary>
public readonly struct Snapshot
{
    public readonly long TickMs;
    public readonly double TimestampSec;
    public readonly float Rms;
    public readonly float Bass;
    public readonly float BassOnset;
    public readonly float MidOnset;
    public readonly float TrebleOnset;
    public readonly float FluxOnset;
    public readonly float Bpm;
    public readonly float BpmConfidence;
    public readonly bool IsPlaying;
    public readonly bool IsSilence;

    public Snapshot(
        long tickMs, double ts,
        float rms, float bass,
        float bassOnset, float midOnset, float trebleOnset, float fluxOnset,
        float bpm, float bpmConf, bool isPlaying, bool isSilence)
    {
        TickMs = tickMs;
        TimestampSec = ts;
        Rms = rms;
        Bass = bass;
        BassOnset = bassOnset;
        MidOnset = midOnset;
        TrebleOnset = trebleOnset;
        FluxOnset = fluxOnset;
        Bpm = bpm;
        BpmConfidence = bpmConf;
        IsPlaying = isPlaying;
        IsSilence = isSilence;
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        t = TimestampSec,
        rms = MathF.Round(Rms, 5),
        bass = MathF.Round(Bass, 4),
        onset_bass = MathF.Round(BassOnset, 4),
        onset_mid = MathF.Round(MidOnset, 4),
        onset_treble = MathF.Round(TrebleOnset, 4),
        onset_flux = MathF.Round(FluxOnset, 4),
        bpm = MathF.Round(Bpm, 1),
        bpm_c = MathF.Round(BpmConfidence, 3),
        play = IsPlaying,
        sil = IsSilence,
    });
}