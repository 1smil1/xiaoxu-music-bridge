// Win32MediaService — fallback media service that reads QQ Music metadata.
//
// v3.2.7: dual-track position sourcing.
//
//  Track A (real position):
//    Tier 1: QQMusicComApiService (csQQMusicComApiWnd2017 WM_COPYDATA) — best-effort
//    Tier 1b: QqMusicWindowScanService — brute-force scan of other QQ Music
//             windows + alternative (action, payload, wParam) combos
//    Tier 1c: UIA Slider — kept as a last-ditch fallback for QQ Music versions
//             that DO expose a Slider (rare; was the v3.2.6 path)
//
//  Track B (virtual clock):
//    When no real position arrives, fall through to a virtual clock that:
//      - Anchors at 0 ms when the song changes (title|artist key transitions)
//      - Accumulates elapsed wall-clock time while StateDetector says playing
//      - Freezes when StateDetector says paused (no negative drift)
//      - Detects song change automatically from the daemon window title text
//      - Gets duration from the on-disk .lrc when available (QqMusicLyricFileService)
//
//  Track C (covered separately in BridgeHttpServer.BuildStateResponseAsync):
//    /state/current returns cached cover/lyrics immediately and refreshes in
//    background — but GetStatusAsync is still fast (<10ms in virtual-clock mode).
//
// What we can extract:
//   Title         ✅  from COM API / window scan / daemon window
//   Artist        ✅  same as Title source chain
//   Source        ✅  "QQMusic"
//   isPlaying     ✅  from real source if available; else StateDetector RMS
//   Position      ✅  from real source if available; else VirtualClockTick
//   Duration      ✅  from real source if available; else .lrc last time-tag
//   Album         ❌  not in title — null
//   Cover         ❌  via QqMusicCoverLookupService (third tier)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using xiaoxu_music_bridge.Audio;
using xiaoxu_music_bridge.Common;
using xiaoxu_music_bridge.Lyrics;

namespace xiaoxu_music_bridge.Media;

public sealed class Win32MediaService : IMediaSessionService, IDisposable
{
    private const string QQMusicProcessName = "QQMusic";
    private const string DaemonWindowClass = "QQMusic_Daemon_Wnd";
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPreviousTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;
    private const byte KeyEventKeyUp = 0x0002;

    // COM API services — both best-effort, never fatal on failure
    private readonly QQMusicComApiService _comApi = new();
    private readonly QqMusicWindowScanService _windowScan = new();
    private readonly QqMusicLyricFileService _lyricFileService = new();

    // Optional injected AudioBeatService for Track B's StateDetector proxy.
    // Set by Program.cs / BridgeHttpServer after the beat service is started.
    private volatile AudioBeatService? _beat;

    // UIA state (between calls — used for Track A tier 1c only)
    private double _lastUiaValue = double.NaN;
    private bool _lastUiaIsPlaying;
    private readonly object _uiaLock = new();
    private static readonly TaskScheduler _uiaScheduler = CreateStaScheduler();
    private static TaskScheduler CreateStaScheduler() => new StaTaskScheduler(1, "UIA-Worker");

    private sealed class StaTaskScheduler : TaskScheduler
    {
        private readonly BlockingCollection<Task> _queue = new();
        private readonly Thread _thread;
        public StaTaskScheduler(int concurrency, string name)
        {
            _thread = new Thread(() =>
            {
                foreach (var t in _queue.GetConsumingEnumerable())
                    TryExecuteTask(t);
            })
            { Name = name, IsBackground = true };
            _thread.TrySetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        protected override void QueueTask(Task task) => _queue.Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();
    }

    // Track B: Virtual clock state — protected by _clockLock
    private readonly object _clockLock = new();
    private string _clockKey = "";
    private long _clockAccumulatedMs;
    private DateTime _clockLastTickAt;
    private int _clockConsecutivePausedTicks;
    private int _clockConsecutivePlayingTicks;
    private bool _clockLastTrustedPlaying;
    private readonly object _fastPlaybackLock = new();
    private string _fastPlaybackKey = "";
    private bool _fastPlaybackAssumedPlaying = true;
    private DateTime? _fastPlaybackPauseCandidateSince;
    private volatile bool _lastPlaybackKnown;
    private DateTime _lastSessionProbeLogAt = DateTime.MinValue;

    public bool LastPlaybackKnown => _lastPlaybackKnown;

    // Limits: anchor resets when the same anchor is trusted for too many polls.
    // Without this, a paused-but-heard-a-spike could "drift" past 0 again.
    private const int ConsecutivePlayingRequired = 1; // 1 poll of "playing" advances
    private const int ConsecutivePausedRequired = 1;
    private static readonly TimeSpan FastPauseConfirmDelay = TimeSpan.FromMilliseconds(100);

    // Track A1b: Window-scan throttling — v3.2.7 attempts ~60 (action × payload × class)
    // combos that take ~3s end-to-end. We don't want to eat that on every 1Hz poll.
    // Strategy: scan once per song-change transition, then never again until the song
    // changes. After a successful scan attempt the flag is flipped so subsequent
    // polls skip the scan entirely.
    private string _lastScannedSongKey = "";
    private DateTime _lastScanCompletedAt = DateTime.MinValue;

    // v3.2.10: TrackChanged event. Fires when the daemon window's title|artist
    // key transitions between polls. Subscribers (BridgeHttpServer) use it to
    // fire-and-forget a lyric prefetch so the next /state/current poll returns
    // the new song's lyrics inline (0ms cache hit) instead of paying the 1-3s
    // QQ Music search+LRC HTTP round-trip on the response critical path.
    //
    // Last-emitted tracking uses whitespace-trimmed values so transient UI
    // refreshes ("Song  -  Artist" → "Song - Artist") don't fire spurious
    // events. We only emit when the normalized key actually changes.
    public event EventHandler? TrackChanged;
    private string? _lastEmittedTitle;
    private string? _lastEmittedArtist;

    public void AttachBeatService(AudioBeatService? svc) => _beat = svc;

    public async Task<MediaStatus> GetFastStatusAsync(CancellationToken cancellationToken)
    {
        var (daemonTitle, daemonArtist) = ReadQqMusicTitleAndArtist();
        if (daemonTitle == null && daemonArtist == null)
        {
            lock (_uiaLock) { _lastUiaValue = double.NaN; _lastUiaIsPlaying = false; }
            ResetClock("no-track");
            return MediaStatus.NoMedia();
        }

        var key = $"{daemonTitle}|{daemonArtist}";
        // Fast status is the dashboard's lyric clock source. The audio-energy
        // proxy can momentarily drop during quiet passages/capture jitter, so
        // require a sustained quiet window before treating QQ Music as paused.
        var rawBeatPlaying = DetectIsPlayingFromBeat();
        var isPlaying = SmoothFastPlaybackState(key, rawBeatPlaying);
        var posMs = VirtualClockTick(key, isPlaying);
        var hasTrack = !string.IsNullOrWhiteSpace(daemonTitle);
        var durMs = hasTrack
            ? await TryLyricFileDuration(daemonTitle!, daemonArtist, 0, cancellationToken)
            : 0;

        EmitTrackChangedIfNeeded(daemonTitle, daemonArtist, hasTrack);

        return new MediaStatus(
            Connected: true,
            Source: "QQMusic",
            Title: daemonTitle,
            Artist: daemonArtist,
            Album: null,
            CoverUrl: null,
            IsPlaying: isPlaying,
            PositionMs: posMs,
            DurationMs: durMs,
            UpdatedAt: DateTimeOffset.Now);
    }

    public async Task<MediaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var (daemonTitle, daemonArtist) = ReadQqMusicTitleAndArtist();
        if (daemonTitle == null && daemonArtist == null)
        {
            lock (_uiaLock) { _lastUiaValue = double.NaN; _lastUiaIsPlaying = false; }
            ResetClock("no-track");
            return MediaStatus.NoMedia();
        }
        var key = $"{daemonTitle}|{daemonArtist}";
        bool hasTrack = !string.IsNullOrWhiteSpace(daemonTitle);

        // ============================================================
        // TRACK A — real position (best-effort, fall through quickly)
        // ============================================================

        long posMs = 0, durMs = 0;
        bool isPlaying = false;
        string? title = daemonTitle;
        string? artist = daemonArtist;

        // Tier 1: Original COM API
        var comStatus = await TryComApiGetStatus(daemonTitle!, daemonArtist, cancellationToken);
        if (comStatus != null) ApplyRealStatus(comStatus, daemonTitle, daemonArtist, ref posMs, ref durMs, ref isPlaying, ref title, ref artist, key, "[ComApi]");

        // Tier 1b: Brute-force window scan (only if tier 1 gave nothing useful AND
        // we haven't already scanned this song). v3.2.7 scans are expensive (~3s)
        // so they run once per song-change transition, then the flag sticks.
        if (posMs == 0 && durMs == 0 && _lastScannedSongKey != key)
        {
            try
            {
                _lastScannedSongKey = key; // mark attempted regardless of outcome
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [WindowScan] first attempt for new song key='{key}'\n");
                var scanStatus = await _windowScan.TryAllWindowsAllActionsAsync(daemonTitle, daemonArtist, cancellationToken);
                _lastScanCompletedAt = DateTime.Now;
                if (scanStatus != null) ApplyRealStatus(scanStatus, daemonTitle, daemonArtist, ref posMs, ref durMs, ref isPlaying, ref title, ref artist, key, "[WindowScan]");
            }
            catch (Exception ex)
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] [WindowScan] exception: {ex.GetType().Name}\n");
            }
        }

        // Tier 1c: UIA (rare but kept for old QQ Music builds)
        if (posMs == 0 && durMs == 0)
        {
            var (uiaPlaying, uiaPos, uiaDur) = TryUiaRead();
            if (uiaPos > 0 || uiaDur > 0)
            {
                posMs = uiaPos;
                durMs = uiaDur;
                if (uiaPos > 0) isPlaying = uiaPlaying;
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] [UIA] pos={posMs}ms dur={durMs}ms playing={isPlaying}\n");
            }
        }

        // ============================================================
        // TRACK B — virtual clock fallback when nothing else worked
        // ============================================================

        if (posMs == 0 && durMs == 0)
        {
            var beatPlaying = DetectIsPlayingFromBeat();
            var virtualPos = VirtualClockTick(key, beatPlaying);
            posMs = virtualPos;

            // If the beat service is missing or not producing data yet, fall
            // back to "playing" so the lyric scroller still has something to
            // do (it's better to drift ahead by a few ms than to be frozen).
            isPlaying = _beat == null ? true : beatPlaying;

            // Pull duration from on-disk .lrc (best-effort, non-blocking).
            // Only attempts the first time we see this song key.
            if (hasTrack) durMs = await TryLyricFileDuration(title!, artist, durMs, cancellationToken);
        }

        // v3.2.10: Fire TrackChanged when (title, artist) transitions. Trim
        // whitespace before comparing so transient UI refreshes don't trigger
        // spurious prefetch fetches. Subscribers (BridgeHttpServer) MUST be
        // non-blocking — they run on this thread otherwise the COM API /
        // window scan would stall while a 1-3s lyric lookup is in flight.
        EmitTrackChangedIfNeeded(title, artist, hasTrack);

        return new MediaStatus(
            Connected: true,
            Source: hasTrack ? "QQMusic" : null,
            Title: title,
            Artist: artist,
            Album: null,
            CoverUrl: null,
            IsPlaying: isPlaying,
            PositionMs: posMs,
            DurationMs: durMs,
            UpdatedAt: DateTimeOffset.Now);
    }

    private void EmitTrackChangedIfNeeded(string? title, string? artist, bool hasTrack)
    {
        var normTitle = title?.Trim();
        var normArtist = artist?.Trim();
        if (!hasTrack || (normTitle == _lastEmittedTitle && normArtist == _lastEmittedArtist)) return;

        _lastEmittedTitle = normTitle;
        _lastEmittedArtist = normArtist;
        try { TrackChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [Win32] TrackChanged subscriber threw: {ex.GetType().Name}: {ex.Message}\n");
        }
    }

    private void ApplyRealStatus(
        MediaStatus incoming,
        string fallbackTitle,
        string? fallbackArtist,
        ref long posMs, ref long durMs, ref bool isPlaying,
        ref string? title, ref string? artist,
        string songKey, string sourceTag)
    {
        if (incoming.PositionMs > 0) posMs = incoming.PositionMs;
        if (incoming.DurationMs > 0) durMs = incoming.DurationMs;
        if (incoming.PositionMs > 0 || incoming.DurationMs > 0) isPlaying = incoming.IsPlaying;
        if (!string.IsNullOrWhiteSpace(incoming.Title)) title = incoming.Title;
        if (!string.IsNullOrWhiteSpace(incoming.Artist)) artist = incoming.Artist;
        SyncAnchorFromReal(songKey, posMs, isPlaying);
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] {sourceTag} pos={posMs}ms dur={durMs}ms playing={isPlaying} title='{title}' artist='{artist}'\n");
    }

    // --- Virtual Clock ---

    /// <summary>
    /// v3.2.7: Track B's anchor-based virtual clock. On song-change
    /// (title|artist key transitions), the accumulator resets to 0 and
    /// isPlaying-from-beat starts gating advancement. While the beat
    /// detector says playing, wall-clock delta is added. While it says
    /// paused, the accumulator stays frozen.
    /// </summary>
    private long VirtualClockTick(string currentKey, bool beatIsPlaying)
    {
        lock (_clockLock)
        {
            var now = DateTime.UtcNow;

            // Song-change detection: same anchor key as last tick → continue;
            // different → reset anchor.
            if (_clockKey != currentKey)
            {
                _clockKey = currentKey;
                _clockAccumulatedMs = 0;
                _clockLastTickAt = now;
                _clockConsecutivePlayingTicks = 0;
                _clockConsecutivePausedTicks = 0;
                _clockLastTrustedPlaying = false;
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [VirtualClock] song change → reset anchor (key='{currentKey}')\n");
                return 0;
            }

            // Debounce: only trust beat's playing signal after we've seen
            // the same answer ConsecutivePlayingRequired/PausedRequired
            // polls in a row. The /state/current poll loop runs at ~1 Hz.
            if (beatIsPlaying) _clockConsecutivePlayingTicks++;
            else _clockConsecutivePlayingTicks = 0;

            if (!beatIsPlaying) _clockConsecutivePausedTicks++;
            else _clockConsecutivePausedTicks = 0;

            bool trustedPlaying = beatIsPlaying
                ? _clockConsecutivePlayingTicks >= ConsecutivePlayingRequired
                : _clockConsecutivePausedTicks < ConsecutivePausedRequired;

            var dt = (long)(now - _clockLastTickAt).TotalMilliseconds;
            if (dt < 0) dt = 0;
            if (dt > 5000) dt = 5000; // clamp huge gaps from process suspension

            if (trustedPlaying)
            {
                _clockAccumulatedMs += dt;
            }
            _clockLastTickAt = now;
            if (dt >= 750 || trustedPlaying != _clockLastTrustedPlaying || !beatIsPlaying)
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [VirtualClock] tick key='{currentKey}' rawBeat={beatIsPlaying} trusted={trustedPlaying} dt={dt}ms pos={_clockAccumulatedMs}ms playTicks={_clockConsecutivePlayingTicks} pauseTicks={_clockConsecutivePausedTicks}\n");
            }
            _clockLastTrustedPlaying = trustedPlaying;

            return Math.Max(0, _clockAccumulatedMs);
        }
    }

    /// <summary>
    /// Called when Track A returns a real position. The clock's accumulator
    /// takes on that value as ground truth (so if the user seeks in QQ
    /// Music and we briefly see real data, the next fallback tick matches).
    /// We also reset the key to keep things consistent.
    /// </summary>
    private void SyncAnchorFromReal(string songKey, long realPosMs, bool realIsPlaying)
    {
        lock (_clockLock)
        {
            if (_clockKey != songKey)
            {
                _clockKey = songKey;
                _clockConsecutivePlayingTicks = realIsPlaying ? ConsecutivePlayingRequired : 0;
                _clockConsecutivePausedTicks = realIsPlaying ? 0 : ConsecutivePausedRequired;
            }
            else
            {
                // Same song — snap accumulator to real value
                _clockAccumulatedMs = Math.Max(0, realPosMs);
                _clockConsecutivePlayingTicks = realIsPlaying ? ConsecutivePlayingRequired : 0;
                _clockConsecutivePausedTicks = realIsPlaying ? 0 : ConsecutivePausedRequired;
            }
            _clockLastTickAt = DateTime.UtcNow;
        }
    }

    private void ResetClock(string reason)
    {
        lock (_clockLock)
        {
            _clockKey = reason;
            _clockAccumulatedMs = 0;
            _clockLastTickAt = DateTime.UtcNow;
            _clockConsecutivePlayingTicks = 0;
            _clockConsecutivePausedTicks = 0;
        }
    }

    /// <summary>
    /// Returns true when the audio beat service indicates playing. The
    /// StateDetector debounces on RMS threshold already (1.5s enter /
    /// 0.3s exit) so we don't need additional smoothing here.
    /// When no beat service is attached yet (e.g. during startup before
    /// AudioBeatService.Start completes), returns true so the virtual
    /// clock advances optimistically.
    /// </summary>
    private bool DetectIsPlayingFromBeat()
    {
        var beat = _beat;
        if (beat != null)
        {
            try
            {
                var (isPlaying, volume) = beat.GetIsPlayingAndVolume();
                if (isPlaying || volume > 0.0001f)
                {
                    _lastPlaybackKnown = true;
                    return true;
                }

                _lastPlaybackKnown = true;
                return false;
            }
            catch
            {
                _lastPlaybackKnown = false;
                return true;
            }
        }

        var session = DetectQqMusicPlaybackFromAudioSession();
        if (session.known)
        {
            _lastPlaybackKnown = true;
            return session.isPlaying;
        }

        if (beat == null)
        {
            _lastPlaybackKnown = false;
            return true; // optimistic default during early startup
        }

        _lastPlaybackKnown = false;
        return false;
    }

    private (bool known, bool isPlaying, float peak, string? device, string? state) DetectQqMusicPlaybackFromAudioSession()
    {
        try
        {
            var qqPids = new HashSet<int>(Process.GetProcessesByName(QQMusicProcessName).Select(p => p.Id));
            if (qqPids.Count == 0) return (false, false, 0f, null, null);

            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active);
            bool sawSession = false;
            float maxPeak = 0f;
            string? bestDevice = null;
            string? bestState = null;
            foreach (var device in devices)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (!qqPids.Contains((int)session.GetProcessID)) continue;
                        sawSession = true;
                        var peak = session.AudioMeterInformation.MasterPeakValue;
                        if (peak >= maxPeak)
                        {
                            maxPeak = peak;
                            bestDevice = device.FriendlyName;
                            bestState = session.State.ToString();
                        }
                        if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                        {
                            LogSessionProbe(true, true, peak, device.FriendlyName, session.State.ToString());
                            return (true, true, peak, device.FriendlyName, session.State.ToString());
                        }
                    }
                }
                finally
                {
                    device.Dispose();
                }
            }

            if (sawSession)
            {
                LogSessionProbe(true, false, maxPeak, bestDevice, bestState);
                return (true, false, maxPeak, bestDevice, bestState);
            }
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] [Win32AudioSession] probe failed: {ex.GetType().Name}: {ex.Message}\n");
        }
        return (false, false, 0f, null, null);
    }

    private void LogSessionProbe(bool known, bool isPlaying, float peak, string? device, string? state)
    {
        var now = DateTime.Now;
        if (now - _lastSessionProbeLogAt < TimeSpan.FromSeconds(3)) return;
        _lastSessionProbeLogAt = now;
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{now:HH:mm:ss.fff}] [Win32AudioSession] known={known} playing={isPlaying} peak={peak:F4} state={state} device='{device}'\n");
    }

    private bool SmoothFastPlaybackState(string songKey, bool rawBeatPlaying)
    {
        lock (_fastPlaybackLock)
        {
            var now = DateTime.UtcNow;
            if (_fastPlaybackKey != songKey)
            {
                _fastPlaybackKey = songKey;
                _fastPlaybackAssumedPlaying = true;
                _fastPlaybackPauseCandidateSince = null;
            }

            if (rawBeatPlaying)
            {
                _fastPlaybackAssumedPlaying = true;
                _fastPlaybackPauseCandidateSince = null;
                return true;
            }

            _fastPlaybackPauseCandidateSince ??= now;
            if (now - _fastPlaybackPauseCandidateSince.Value >= FastPauseConfirmDelay)
            {
                _fastPlaybackAssumedPlaying = false;
            }

            return _fastPlaybackAssumedPlaying;
        }
    }

    private long _lyricFileDurationCacheKey = 0;
    private long _lyricFileDurationMs = 0;

    private async Task<long> TryLyricFileDuration(string title, string? artist, long currentDurMs, CancellationToken ct)
    {
        // Don't fetch if we already have a real duration
        if (currentDurMs > 0) return currentDurMs;

        // Per-song negative cache: don't re-scan the disk every poll
        var songKey = (title ?? "").GetHashCode() ^ (artist ?? "").GetHashCode();
        if (songKey == _lyricFileDurationCacheKey) return _lyricFileDurationMs;

        try
        {
            var result = await _lyricFileService.FindAsync(title, artist, ct);
            _lyricFileDurationCacheKey = songKey;
            _lyricFileDurationMs = result.DurationMs;
            if (result.DurationMs > 0)
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] [QqLyricFile] found duration {result.DurationMs}ms from '{result.FileName}'\n");
            }
            return result.DurationMs;
        }
        catch
        {
            return 0;
        }
    }

    // --- Tier 1 helper ---

    private async Task<MediaStatus?> TryComApiGetStatus(string title, string? artist, CancellationToken ct)
    {
        try
        {
            var comTask = _comApi.GetPlayerStatusAsync(title, artist, ct);
            var winner = await Task.WhenAny(comTask, Task.Delay(500, ct));
            if (winner == comTask) return await comTask;
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [ComApi] slow (>500ms) — falling through\n");
            return null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [ComApi] GetPlayerStatusAsync threw: {ex.GetType().Name}\n");
            return null;
        }
    }

    // --- Tier 1c helper ---

    private (bool isPlaying, long positionMs, long durationMs) TryUiaRead()
    {
        try
        {
            var procs = Process.GetProcessesByName(QQMusicProcessName);
            int pid = procs.Length > 0 ? procs[0].Id : 0;
            if (pid <= 0) return (false, 0, 0);

            var task = Task.Factory.StartNew(
                () => ReadPositionViaUIA(pid),
                CancellationToken.None,
                TaskCreationOptions.None,
                _uiaScheduler);
            if (!task.Wait(2000)) return (false, 0, 0);
            return task.Result;
        }
        catch { return (false, 0, 0); }
    }

    private (bool isPlaying, long positionMs, long durationMs) ReadPositionViaUIA(int qqPid)
    {
        try
        {
            var root = AutomationElement.RootElement;
            var qqWindow = root.FindFirst(
                TreeScope.Children,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, qqPid),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)
                )
            );
            if (qqWindow == null) return (false, 0, 0);

            var slider = qqWindow.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider)
            );
            if (slider == null) return (false, 0, 0);

            if (slider.GetCurrentPattern(RangeValuePattern.Pattern) is not RangeValuePattern rp)
                return (false, 0, 0);

            double current = rp.Current.Value;
            double min = rp.Current.Minimum;
            double max = rp.Current.Maximum;

            long posMs = (long)Math.Max(0, current - min);
            long durMs = (long)Math.Max(0, max - min);
            if (durMs <= 0) return (false, 0, 0);

            bool playing;
            lock (_uiaLock)
            {
                if (!double.IsNaN(_lastUiaValue))
                {
                    playing = Math.Abs(current - _lastUiaValue) > 50;
                }
                else
                {
                    playing = true;
                }
                _lastUiaValue = current;
                _lastUiaIsPlaying = playing;
            }

            return (playing, posMs, durMs);
        }
        catch
        {
            return (false, 0, 0);
        }
    }

    public Task<ControlResult> SendCommandAsync(ControlCommand command, CancellationToken cancellationToken)
    {
        var key = command switch
        {
            ControlCommand.PlayPause => VkMediaPlayPause,
            ControlCommand.Next => VkMediaNextTrack,
            ControlCommand.Previous => VkMediaPreviousTrack,
            _ => VkMediaPlayPause,
        };
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
        return Task.FromResult(new ControlResult(true));
    }

    public Task<CoverImage?> GetCurrentCoverAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<CoverImage?>(null);
    }

    private static (string? title, string? artist) ReadQqMusicTitleAndArtist()
    {
        var procs = Process.GetProcessesByName(QQMusicProcessName);
        if (procs.Length == 0)
        {
            return (null, null);
        }

        IntPtr daemon = IntPtr.Zero;
        foreach (var p in procs)
        {
            if (TryFindDaemonWindow(p.Id, out var h)) { daemon = h; break; }
        }
        if (daemon == IntPtr.Zero) return (null, null);

        var sb = new StringBuilder(512);
        int len = GetWindowText(daemon, sb, 512);
        if (len <= 0) return (null, null);

        var raw = sb.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);

        var sep = " - ";
        var idx = raw.IndexOf(sep, StringComparison.Ordinal);
        if (idx <= 0) return (raw, null);

        var title = raw.Substring(0, idx).Trim();
        var artist = raw.Substring(idx + sep.Length).Trim();
        if (title.Length == 0) return (null, null);
        return (title, artist.Length == 0 ? null : artist);
    }

    private static bool TryFindDaemonWindow(int processId, out IntPtr hWnd)
    {
        hWnd = IntPtr.Zero;
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if ((int)pid != processId) return true;
            var cn = new StringBuilder(256);
            GetClassName(h, cn, 256);
            if (cn.ToString() == DaemonWindowClass)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        hWnd = found;
        return hWnd != IntPtr.Zero;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public void Dispose()
    {
        try { _comApi.Dispose(); } catch { }
        try { _windowScan.Dispose(); } catch { }
    }
}
