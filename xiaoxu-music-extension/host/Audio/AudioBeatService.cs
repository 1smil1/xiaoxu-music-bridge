using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Audio;

/// <summary>
/// Real-time audio analysis service. Captures system audio via WASAPI loopback,
/// performs 2048-point FFT + multi-band feature extraction + onset detection +
/// BPM tracking, and pushes a v3 JSON payload at 30Hz.
///
/// v3 payload schema (backward-compatible — keeps top-level bass/pulse/glow/volume):
///   {
///     type: "beat",
///     bass, pulse, glow, volume,           // legacy fields
///     bands:   { sub, bass, lowMid, mid, highMid, treble, air },
///     features: { rms, loudness, centroid, zcr },
///     onsets:   { spectralFlux, bass, mid, treble, flux },
///     rhythm:   { bpm, bpmConfidence, beatPhase, isDownbeat, beatCount, tempoChanged },
///     state:    { isPlaying, isSilence, silenceDuration },
///     ts
///   }
/// </summary>
public sealed class AudioBeatService : IDisposable
{
    private const int FftSize = 2048;
    private const int HopSize = 1024;        // 50% overlap
    private const int PushIntervalMs = 33;   // ~30Hz
    private static readonly string DebugLog = LogPaths.DebugLog;

    private WasapiLoopbackCapture? _capture;
    private Thread? _pushThread;
    private Thread? _processThread;
    private volatile bool _running;

    // Audio ring buffer (input samples from WASAPI)
    private readonly object _ringLock = new();
    private float[] _ringBuffer = new float[8192];
    private int _ringWritePos;
    private int _ringReadPos;
    private int _ringAvailable;
    private int _sampleRate;
    private int _channels;
    private string _deviceName = "unknown";
    private AudioDebugServer? _debugServer;
    private readonly PcmAudioBroadcaster _pcmBroadcaster = new();
    private volatile bool _captureReady;

    // Analysis pipeline
    private Fft? _fft;
    private FeatureExtractor? _features;
    private OnsetDetector? _onsets;
    private BeatTracker? _beats;
    private StateDetector? _stateDetector;

    // Per-frame analysis state (note: actual frame + magnitude buffers are now
    // allocated locally inside ProcessLoop to avoid closure issues with the lock)
    private readonly float[] _magnitude = new float[FftSize / 2];

    // Snapshot of latest analysis (lock-protected)
    private readonly object _snapshotLock = new();
    private AudioFeatures _latestFeatures = new();
    private OnsetSet _latestOnsets = new();
    private BeatState _latestBeats = new();
    private AudioState _latestState = new();

    // Subscription state
    private volatile bool _subscribed;
    private DateTime _lastSubscribeTime = DateTime.MinValue;
    private static readonly TimeSpan SubscribeTimeout = TimeSpan.FromSeconds(30);

    // Push callback (writes JSON to native stdout / HTTP)
    private readonly Action<string> _pushCallback;

    // Diagnostics timer
    private readonly Stopwatch _wallClock = Stopwatch.StartNew();
    private long _lastDiagMs;
    private int _diagFrames;

    public AudioBeatService(Action<string> pushCallback)
    {
        _pushCallback = pushCallback;
    }

    public void Start()
    {
        _captureReady = false;
        try
        {
            _capture = new WasapiLoopbackCapture();
            var waveFormat = _capture.WaveFormat;
            if (!AudioCaptureFormatPolicy.IsSupported(
                waveFormat.Encoding == WaveFormatEncoding.IeeeFloat,
                waveFormat.BitsPerSample,
                waveFormat.BlockAlign,
                waveFormat.Channels))
            {
                throw new NotSupportedException(
                    $"WASAPI mix format must be Float32; got {waveFormat.Encoding}/{waveFormat.BitsPerSample}-bit, blockAlign={waveFormat.BlockAlign}, channels={waveFormat.Channels}");
            }
            _sampleRate = _capture.WaveFormat.SampleRate;
            _channels = _capture.WaveFormat.Channels;
            _deviceName = GetDefaultRenderDeviceName();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, _) =>
            {
                _captureReady = false;
                _pcmBroadcaster.EndStream();
            };
            _capture.StartRecording();
            _captureReady = true;

            // Initialize pipeline with actual sample rate
            _fft = new Fft(FftSize);
            _features = new FeatureExtractor(FftSize, _sampleRate);
            _onsets = new OnsetDetector(FftSize);
            _beats = new BeatTracker();
            _stateDetector = new StateDetector();

            _running = true;

            _pushThread = new Thread(PushLoop)
            {
                IsBackground = true,
                Name = "AudioBeatPush",
                Priority = ThreadPriority.BelowNormal
            };
            _pushThread.Start();

            _processThread = new Thread(ProcessLoop)
            {
                IsBackground = true,
                Name = "AudioBeatProcess",
                Priority = ThreadPriority.BelowNormal
            };
            _processThread.Start();

            Log($"AudioBeatService started, sample rate={_sampleRate}, channels={_capture.WaveFormat.Channels}, device={_deviceName}");
        }
        catch (Exception ex)
        {
            _captureReady = false;
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            Log($"AudioBeatService.Start FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public void Subscribe()
    {
        _subscribed = true;
        _lastSubscribeTime = DateTime.Now;
    }

    public void Unsubscribe()
    {
        _subscribed = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            var byteSpan = e.Buffer.AsSpan(0, e.BytesRecorded);
            var samples = MemoryMarshal.Cast<byte, float>(byteSpan);
            if (samples.Length == 0) return;

            int ch = _capture?.WaveFormat.Channels ?? 2;
            int capturedFrames = samples.Length / ch;
            long callbackUs = _wallClock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            long firstSampleUs = callbackUs - capturedFrames * 1_000_000L / _sampleRate;
            _pcmBroadcaster.Publish(samples, _sampleRate, ch, Math.Max(0, firstSampleUs));

            // Downmix to mono + push to ring buffer
            lock (_ringLock)
            {
                for (int i = 0; i < samples.Length; i += ch)
                {
                    float mix = 0f;
                    int n = Math.Min(ch, samples.Length - i);
                    for (int c = 0; c < n; c++) mix += samples[i + c];
                    if (n > 1) mix /= n;

                    _ringBuffer[_ringWritePos] = mix;
                    _ringWritePos = (_ringWritePos + 1) % _ringBuffer.Length;
                    if (_ringAvailable < _ringBuffer.Length) _ringAvailable++;
                    else _ringReadPos = (_ringReadPos + 1) % _ringBuffer.Length;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"OnDataAvailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Pull samples from ring buffer in HopSize chunks, run FFT + features + onsets.
    /// Runs on a separate thread to keep audio callback cheap.
    /// </summary>
    private void ProcessLoop()
    {
        var frame = new float[FftSize];
        var mag = new float[FftSize / 2];
        int frameIdx = 0;

        while (_running)
        {
            Thread.Sleep(8);

            // Read as many samples as available; build an FftSize frame using overlap-add.
            // Strategy: each iteration, we aim to fill the rest of `frame` from oldest to newest.
            int readThisIteration = 0;
            lock (_ringLock)
            {
                while (_ringAvailable > 0 && frameIdx < FftSize)
                {
                    frame[frameIdx++] = _ringBuffer[_ringReadPos];
                    _ringReadPos = (_ringReadPos + 1) % _ringBuffer.Length;
                    _ringAvailable--;
                    readThisIteration++;
                }
            }

            if (frameIdx < FftSize) continue;

            // Frame complete — shift left by HopSize (overlap-add) and reuse for next iteration
            Array.Copy(frame, HopSize, frame, 0, FftSize - HopSize);
            frameIdx = FftSize - HopSize;

            // Apply Hann window + FFT
            var winFrame = new float[FftSize];
            Array.Copy(frame, winFrame, FftSize);
            Fft.ApplyHannWindow(winFrame);
            _fft?.ForwardMagnitude(winFrame, mag);

            // Compute features + onsets + beats
            _features?.Extract(mag, winFrame, _latestFeatures);
            _onsets?.Update(mag, _latestFeatures, 1f / 30f, _latestOnsets);
            _beats?.Update(_latestOnsets.BassOnset + _latestOnsets.FluxOnset, _wallClock.Elapsed.TotalSeconds, 1f / 30f);
            _stateDetector?.Update(_latestFeatures.Rms, _wallClock.Elapsed.TotalSeconds);

            _diagFrames++;
        }
    }

    private void PushLoop()
    {
        while (_running)
        {
            try
            {
                Thread.Sleep(PushIntervalMs);

                if (_subscribed && DateTime.Now - _lastSubscribeTime > SubscribeTimeout)
                {
                    _subscribed = false;
                }
                if (!_subscribed) continue;

                string json = SerializeSnapshot();
                _pushCallback(json);
                _debugServer?.RecordSnapshot(BuildSnapshot());

                // Diagnostic log every 10s
                long now = _wallClock.ElapsedMilliseconds;
                if (now - _lastDiagMs > 10000)
                {
                    _lastDiagMs = now;
                    Log($"Pushed {_diagFrames} frames in 10s (rate={_diagFrames / 10f}Hz, bpm={_latestBeats.Bpm:F1}, conf={_latestBeats.BpmConfidence:F2}, isPlaying={_latestState.IsPlaying})");
                    _diagFrames = 0;
                }
            }
            catch (Exception ex)
            {
                Log($"PushLoop: {ex.Message}");
            }
        }
    }

    private string SerializeSnapshot()
    {
        lock (_snapshotLock)
        {
            var f = _latestFeatures;
            var o = _latestOnsets;
            var b = _beats?.State ?? new BeatState();
            var s = _stateDetector?.State ?? new AudioState();
            var clock = _pcmBroadcaster.GetClock();

            // Map new v3 fields back to legacy bass/pulse/glow for backward compat
            // - bass: smoothed bass band (0-1)
            // - pulse: bass onset pulse (decaying)
            // - glow: bass max-of-instant (peaks/transients)
            // - volume: RMS-derived
            float bass = MathF.Min(1f, f.Bass);
            float pulse = o.BassOnset;
            float glow = MathF.Min(1f, f.Bass * 1.2f);
            float volume = MathF.Min(1f, f.Rms * 4f);

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "beat",
                bass = MathF.Round(bass, 3),
                pulse = MathF.Round(pulse, 3),
                glow = MathF.Round(glow, 3),
                volume = MathF.Round(volume, 3),

                bands = new
                {
                    sub = MathF.Round(f.Sub, 3),
                    bass = MathF.Round(f.Bass, 3),
                    lowMid = MathF.Round(f.LowMid, 3),
                    mid = MathF.Round(f.Mid, 3),
                    highMid = MathF.Round(f.HighMid, 3),
                    treble = MathF.Round(f.Treble, 3),
                    air = MathF.Round(f.Air, 3),
                },

                features = new
                {
                    rms = MathF.Round(f.Rms, 4),
                    loudness = MathF.Round(f.Loudness, 2),
                    centroid = MathF.Round(f.Centroid, 1),
                    zcr = MathF.Round(f.Zcr, 3),
                },

                onsets = new
                {
                    spectralFlux = MathF.Round(o.SpectralFlux, 3),
                    bass = MathF.Round(o.BassOnset, 3),
                    mid = MathF.Round(o.MidOnset, 3),
                    treble = MathF.Round(o.TrebleOnset, 3),
                    flux = MathF.Round(o.FluxOnset, 3),
                },

                rhythm = new
                {
                    bpm = MathF.Round(b.Bpm, 1),
                    bpmConfidence = MathF.Round(b.BpmConfidence, 2),
                    beatPhase = MathF.Round(b.BeatPhase, 3),
                    isDownbeat = b.IsDownbeat,
                    beatCount = b.BeatCount,
                    tempoChanged = b.TempoChanged,
                },

                state = new
                {
                    isPlaying = s.IsPlaying,
                    isSilence = s.IsSilence,
                    silenceDuration = MathF.Round(s.SilenceDuration, 2),
                },

                audioClock = new
                {
                    sampleIndex = clock.SampleIndex,
                    sampleRate = clock.SampleRate,
                    monotonicMs = clock.MonotonicMs,
                    discontinuityId = clock.DiscontinuityId,
                },

                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
    }

    public (float bass, float volume, float pulse, float glow) GetSnapshot()
    {
        lock (_snapshotLock)
        {
            var f = _latestFeatures;
            var o = _latestOnsets;
            float bass = MathF.Min(1f, f.Bass);
            float pulse = o.BassOnset;
            float glow = MathF.Min(1f, f.Bass * 1.2f);
            float volume = MathF.Min(1f, f.Rms * 4f);
            return (bass, volume, pulse, glow);
        }
    }

    /// <summary>
    /// v3.2.7: Expose the StateDetector's isPlaying snapshot for callers
    /// (Win32MediaService virtual clock) that need a playing/paused signal
    /// derived from system audio levels. Returns (isPlaying, volume) —
    /// isPlaying is false until the StateDetector has had at least one
    /// frame of input. Volume is the RMS-derived level (0..1).
    /// </summary>
    public (bool isPlaying, float volume) GetIsPlayingAndVolume()
    {
        lock (_snapshotLock)
        {
            var f = _latestFeatures;
            var s = _stateDetector?.State;
            float volume = MathF.Min(1f, f.Rms * 4f);
            bool isPlaying = s?.IsPlaying ?? false;
            return (isPlaying, volume);
        }
    }

    /// <summary>
    /// Returns a JSON snapshot with all v3 fields for HTTP /state/current endpoint.
    /// </summary>
    public string GetFullSnapshotJson()
    {
        return SerializeSnapshot();
    }

    public bool TrySubscribeAudio(out PcmAudioSubscription? subscription) =>
        _pcmBroadcaster.TrySubscribe(out subscription);

    public AudioClockSnapshot GetAudioClock() => _pcmBroadcaster.GetClock();

    public bool IsCaptureReady => _captureReady;

    // ---- Public diagnostic surface for AudioDebugServer ----

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public string DeviceName => _deviceName;

    /// <summary>Attach (or detach) the debug HTTP server. Pass null to detach.</summary>
    public void AttachDebugServer(AudioDebugServer? server)
    {
        _debugServer = server;
    }

    /// <summary>
    /// Enumerate active render (output) endpoints so the debug server can list them.
    /// Used by /devices endpoint to diagnose "which device is WASAPI capturing".
    /// </summary>
    public IReadOnlyList<RenderDeviceInfo> GetRenderDevices()
    {
        var result = new List<RenderDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string defaultId = defaultDevice.ID;

            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in devices)
            {
                try
                {
                    result.Add(new RenderDeviceInfo(
                        Id: d.ID,
                        Name: d.FriendlyName,
                        State: d.State.ToString(),
                        IsDefault: d.ID == defaultId,
                        SampleRate: d.AudioClient?.MixFormat?.SampleRate ?? 0,
                        Channels: d.AudioClient?.MixFormat?.Channels ?? 0
                    ));
                }
                finally
                {
                    d.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log($"GetRenderDevices: {ex.Message}");
        }
        return result;
    }

    private static string GetDefaultRenderDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch (Exception ex)
        {
            return $"<enumerate failed: {ex.Message}>";
        }
    }

    private Snapshot BuildSnapshot()
    {
        AudioFeatures f;
        OnsetSet o;
        BeatState b;
        AudioState s;
        lock (_snapshotLock)
        {
            f = _latestFeatures;
            o = _latestOnsets;
            b = _beats?.State ?? new BeatState();
            s = _stateDetector?.State ?? new AudioState();
        }

        return new Snapshot(
            tickMs: Environment.TickCount64,
            ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            rms: f.Rms,
            bass: f.Bass,
            bassOnset: o.BassOnset,
            midOnset: o.MidOnset,
            trebleOnset: o.TrebleOnset,
            fluxOnset: o.FluxOnset,
            bpm: b.Bpm,
            bpmConf: b.BpmConfidence,
            isPlaying: s.IsPlaying,
            isSilence: s.IsSilence
        );
    }

    public void Dispose()
    {
        _running = false;
        _captureReady = false;
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _pcmBroadcaster.Dispose();
    }

    private static void Log(string msg)
    {
        // SafeAppend handles file-lock contention when Chrome respawns the host
        // mid-overlap with the dying old instance — never throws, never blocks.
        LogPaths.SafeAppend(DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] [AudioBeatService] {msg}\n");
    }
}

/// <summary>
/// Lightweight view of an audio render endpoint, surfaced via /devices endpoint.
/// </summary>
public sealed record RenderDeviceInfo(
    string Id,
    string Name,
    string State,
    bool IsDefault,
    int SampleRate,
    int Channels
);
