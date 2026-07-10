// Win32MediaService — fallback media service that reads QQ Music metadata.
//
// v3.2.7: position source swapped from UIA → QQMusicComApiService.
//   - GSMTC (v3.2.5) permanently broken on this machine (WinRT projection DLL missing).
//   - UIA (v3.2.6) doesn't expose a Slider/ProgressBar in QQ Music's CEF tree.
//   - New: csQQMusicComApiWnd2017 hidden COM API window accepts WM_COPYDATA with
//     `player/playerstatus` action and replies via registered window message
//     carrying real position / duration / isPlaying / title / artist.
//
//   Title/artist ALSO still come from the daemon window ("歌名 - 歌手" pattern)
//   as a fallback — COM API may return nulls on some QQ Music versions and the
//   daemon window has been the reliable source since v3.2.0.
//
//   UIA code is retained as a tertiary fallback for QQ Music versions that
//   expose a Slider — it does no harm to keep the path live.
//
// What we can extract (v3.2.7):
//   Title         ✅  from COM API, fallback to daemon window title (before " - ")
//   Artist        ✅  from COM API, fallback to daemon window title (after " - ")
//   Source        ✅  "QQMusic" derived from process name / COM API source
//   isPlaying     ✅  from COM API state code; UIA delta as fallback
//   Position      ✅  from COM API playtime; UIA RangeValue as fallback
//   Duration      ✅  from COM API totaltime; UIA RangeValue as fallback
//   Album         ❌  not in title — left null
//   Cover         ❌  CEF renders offscreen; PrintWindow path is brittle — covered
//                   via QqMusicCoverLookupService (third tier)
//
// GSMTC remains the primary path; this is only used after a GSMTC timeout.
// COM API failures fall back gracefully to UIA; UIA failures degrade to
// (isPlaying=false, position=0) and the dashboard still works.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Media;

public sealed class Win32MediaService : IMediaSessionService, IDisposable
{
    private const string QQMusicProcessName = "QQMusic";
    private const string DaemonWindowClass = "QQMusic_Daemon_Wnd";
    private const int CoverUrlStub = 17888; // matches WindowsMediaSessionService.CoverUrl
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPreviousTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;
    private const byte KeyEventKeyUp = 0x0002;

    // v3.2.7: Primary position source for the fallback path. STA thread +
    // WM_COPYDATA client that talks to QQ Music's hidden `csQQMusicComApiWnd2017`.
    private readonly QQMusicComApiService _comApi = new();

    // UIA state — held between calls so isPlaying can be inferred by delta.
    // volatile because GetStatusAsync is called from many Native Messaging threads.
    private double _lastUiaValue = double.NaN;
    private DateTime _lastUiaReadAt = DateTime.MinValue;
    private bool _lastUiaIsPlaying;
    private readonly object _uiaLock = new();

    // UIA requires an STA (single-threaded apartment) COM thread. The host
    // starts on an MTA thread pool worker (Native Messaging reads from stdin
    // on the main thread; HTTP requests fire on the .NET ThreadPool). Trying
    // to AutomationElement.RootElement on a non-STA thread throws
    // TypeInitializationException because the Automation static class probes
    // COM threading on first use. So we marshal every UIA read onto one
    // dedicated long-lived STA worker thread.
    private static readonly TaskScheduler _uiaScheduler = CreateStaScheduler();
    private static TaskScheduler CreateStaScheduler()
    {
        // Single-threaded, STA, with a name visible in the debugger.
        return new StaTaskScheduler(1, "UIA-Worker");
    }

    private sealed class StaTaskScheduler : TaskScheduler
    {
        private readonly BlockingCollection<Task> _queue = new();
        private readonly Thread _thread;
        public StaTaskScheduler(int concurrency, string name)
        {
            // v3.2.5: TrySetApartmentState must be called BEFORE Start, not
            // inside the thread body. The original v3.2.3 implementation set
            // it inside the lambda, which silently failed (apartment was
            // already locked at thread start) and left the thread as MTA —
            // then AutomationElement.RootElement on first use threw
            // FileNotFoundException for WindowsBase v9 missing AND COM
            // apartment errors. Setting it before Start guarantees STA.
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

    public async Task<MediaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var (daemonTitle, daemonArtist) = ReadQqMusicTitleAndArtist();
        if (daemonTitle == null && daemonArtist == null)
        {
            // No QQ Music track loaded — reset UIA state so the next track's
            // first poll doesn't think the song is paused.
            lock (_uiaLock) { _lastUiaValue = double.NaN; _lastUiaIsPlaying = false; }
            return MediaStatus.NoMedia();
        }
        bool hasTrack = !string.IsNullOrWhiteSpace(daemonTitle);

        // v3.2.7 HOTFIX: race the COM API against a 500ms deadline. If it
        // returns in time, use it; otherwise return immediately with
        // daemon-window title/artist only (no live position). The poll loop
        // calls us at ~1Hz so the next attempt will fire within ~1s.
        //
        // This replaces the v3.2.7-initial `comTask.Wait(1500)` synchronous
        // wait that made every /state/current block the worker thread for
        // 1.5s — that's why song-switch UI felt laggy.
        long posMs = 0, durMs = 0;
        bool isPlaying = false;
        string? title = daemonTitle;
        string? artist = daemonArtist;

        try
        {
            var comTask = _comApi.GetPlayerStatusAsync(daemonTitle, daemonArtist, cancellationToken);
            var comTimeout = Task.Delay(500, cancellationToken);
            var winner = await Task.WhenAny(comTask, comTimeout);
            if (winner == comTask)
            {
                var comStatus = await comTask;
                if (comStatus != null)
                {
                    if (comStatus.PositionMs > 0) posMs = comStatus.PositionMs;
                    if (comStatus.DurationMs > 0) durMs = comStatus.DurationMs;
                    if (comStatus.PositionMs > 0 || comStatus.DurationMs > 0)
                    {
                        isPlaying = comStatus.IsPlaying;
                    }
                    if (!string.IsNullOrWhiteSpace(comStatus.Title)) title = comStatus.Title;
                    if (!string.IsNullOrWhiteSpace(comStatus.Artist)) artist = comStatus.Artist;
                    LogPaths.SafeAppend(LogPaths.DebugLog,
                        $"[{DateTime.Now:HH:mm:ss}] [ComApi] pos={posMs}ms dur={durMs}ms playing={isPlaying} title='{title}' artist='{artist}'\n");
                }
            }
            else
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] [ComApi] slow (>500ms) — returning daemon-window-only this poll\n");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [ComApi] GetPlayerStatusAsync wrapper caught (degraded): {ex.GetType().Name}: {ex.Message}\n");
        }

        // Fallback: UIA — only if COM API gave us no useful position data.
        // Same defensive try/catch as v3.2.5.
        if (posMs == 0 && durMs == 0)
        {
            var procs = Process.GetProcessesByName(QQMusicProcessName);
            int pid = procs.Length > 0 ? procs[0].Id : 0;
            if (pid > 0)
            {
                try
                {
                    var task = Task.Factory.StartNew(
                        () => ReadPositionViaUIA(pid),
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        _uiaScheduler);
                    if (task.Wait(2000))
                    {
                        (isPlaying, posMs, durMs) = task.Result;
                    }
                    else
                    {
                        LogPaths.SafeAppend(LogPaths.DebugLog,
                            $"[{DateTime.Now:HH:mm:ss}] [UIA] read timed out after 2s\n");
                    }
                }
                catch (Exception ex)
                {
                    var diag = new StringBuilder();
                    for (var e = ex; e != null; e = e.InnerException)
                    {
                        diag.Append($" | {e.GetType().Name}: {e.Message}");
                        if (e is System.IO.FileNotFoundException fnf && fnf.FileName != null)
                            diag.Append($" [FileName={fnf.FileName}]");
                    }
                    LogPaths.SafeAppend(LogPaths.DebugLog,
                        $"[{DateTime.Now:HH:mm:ss}] [UIA] GetStatusAsync wrapper caught (degraded): {diag.ToString().TrimStart(' ', '|')}\n");
                }
            }
        }

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
        // Cover extraction would require PrintWindow on qbcore_osr_wnd + crop + encode.
        // QQ Music uses CEF with hardware-accelerated rendering that often fails PrintWindow.
        // Leave null for now — same as GSMTC's behavior when no thumbnail is available.
        return Task.FromResult<CoverImage?>(null);
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

            // QQ Music's progress bar is a Slider control. FindFirst with Descendants
            // so we don't have to know the exact visual-tree path.
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

            // Infer isPlaying by comparing current value to the previous read.
            // 50 ms threshold absorbs sub-tick rounding from QQ Music's progress
            // animation (the slider value sometimes flickers by a few ms even when
            // actually paused — empirically verified in v3.2.3 dev).
            bool playing;
            lock (_uiaLock)
            {
                if (!double.IsNaN(_lastUiaValue))
                {
                    playing = Math.Abs(current - _lastUiaValue) > 50;
                }
                else
                {
                    // First poll: be optimistic — QQ Music's default behavior on
                    // launch is "continue playing the previous track", and if the
                    // user just paused we'd correct within one poll (≤2s).
                    playing = true;
                }
                _lastUiaValue = current;
                _lastUiaReadAt = DateTime.Now;
                _lastUiaIsPlaying = playing;
            }

            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [UIA] pos={posMs}ms dur={durMs}ms playing={playing}\n");
            return (playing, posMs, durMs);
        }
        catch (Exception ex)
        {
            // UIA failures (COM exception, slider not exposed, automation disabled)
            // are non-fatal — degrade to the old behavior (isPlaying=false,
            // position=0). The frontend's beat-based proxy in
            // WallpaperDashboard.tsx still works in this state.
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                sb.Append($" | {e.GetType().Name}: {e.Message}");
                if (e is System.IO.FileNotFoundException fnf && fnf.FileName != null)
                    sb.Append($" [FileName={fnf.FileName}]");
            }
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [UIA] read failed: {sb.ToString().TrimStart(' ', '|')}\n");
            return (false, 0, 0);
        }
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

        // Format: "Title - Artist"  (QQ Music appends trailing spaces)
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
                return false; // stop enumerating
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

    // v3.2.7: tear down the COM API STA thread + reply window when the host
    // shuts down. Program.cs doesn't currently call Dispose on Win32MediaService,
    // but if it ever does (e.g. for tests or graceful shutdown hooks) this
    // makes it safe.
    public void Dispose()
    {
        try { _comApi.Dispose(); } catch { /* swallow */ }
    }
}