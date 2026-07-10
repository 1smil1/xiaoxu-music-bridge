// Win32MediaService — fallback media service that reads QQ Music metadata
// directly from its hidden Daemon window title (instead of via GSMTC broker).
//
// Why: GSMTC broker deadlocks freeze GlobalSystemMediaTransportControlsSessionManager
//      .RequestAsync() indefinitely. This service bypasses the broker entirely:
//      it uses Win32 GetWindowText() on QQMusic_Daemon_Wnd, whose title is formatted
//      as "歌名 - 歌手" whenever a track is loaded.
//
// What we can extract (v3.2.3):
//   Title         ✅  from window title (before " - ")
//   Artist        ✅  from window title (after " - ")
//   Source        ✅  "QQMusic" derived from process name
//   isPlaying     ✅  inferred by UIA: poll the QQ Music progress-slider twice and
//                   check whether Value changed (≥50 ms ⇒ playing)
//   Position      ✅  UIA RangeValuePattern.Current.Value − Minimum  (ms)
//   Duration      ✅  UIA RangeValuePattern.Current.Maximum − Minimum (ms)
//   Album         ❌  not in title — left null
//   Cover         ❌  CEF renders offscreen; PrintWindow path is brittle — covered
//                   via QqMusicCoverLookupService (third tier)
//
// GSMTC remains the primary path; this is only used after a GSMTC timeout.
// UIA failures (e.g. QQ Music upgrades the slider's control type) fall back
// gracefully — we return isPlaying=false, position=0, duration=0 and log
// `[UIA] read failed: ...` so the dashboard still works in degraded mode.

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

public sealed class Win32MediaService : IMediaSessionService
{
    private const string QQMusicProcessName = "QQMusic";
    private const string DaemonWindowClass = "QQMusic_Daemon_Wnd";
    private const int CoverUrlStub = 17888; // matches WindowsMediaSessionService.CoverUrl
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPreviousTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;
    private const byte KeyEventKeyUp = 0x0002;

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
            _thread = new Thread(() =>
            {
                // SetApartmentState must be called before Start.
                _thread.TrySetApartmentState(ApartmentState.STA);
                foreach (var t in _queue.GetConsumingEnumerable())
                    TryExecuteTask(t);
            })
            { Name = name, IsBackground = true };
            _thread.Start();
        }
        protected override void QueueTask(Task task) => _queue.Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();
    }

    public Task<MediaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var (title, artist) = ReadQqMusicTitleAndArtist();
        if (title == null && artist == null)
        {
            // No QQ Music track loaded — reset UIA state so the next track's
            // first poll doesn't think the song is paused.
            lock (_uiaLock) { _lastUiaValue = double.NaN; _lastUiaIsPlaying = false; }
            return Task.FromResult(MediaStatus.NoMedia());
        }
        bool hasTrack = !string.IsNullOrWhiteSpace(title);

        // v3.2.3: UIA read for position / isPlaying / duration.
        // Title/artist still come from the daemon window because QQ Music's
        // UIA tree exposes "Title" only as the window title (same source).
        var procs = Process.GetProcessesByName(QQMusicProcessName);
        int pid = procs.Length > 0 ? procs[0].Id : 0;
        long posMs = 0, durMs = 0;
        bool isPlaying = false;
        if (pid > 0)
        {
            // Run UIA on the dedicated STA scheduler — see _uiaScheduler comment.
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
            catch (AggregateException ae) when (ae.InnerException != null)
            {
                throw ae.InnerException;
            }
        }

        return Task.FromResult(new MediaStatus(
            Connected: true,
            Source: hasTrack ? "QQMusic" : null,
            Title: title,
            Artist: artist,
            Album: null,
            CoverUrl: null,
            IsPlaying: isPlaying,
            PositionMs: posMs,
            DurationMs: durMs,
            UpdatedAt: DateTimeOffset.Now));
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
}