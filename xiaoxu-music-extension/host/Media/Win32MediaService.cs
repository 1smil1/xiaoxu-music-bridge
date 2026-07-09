// Win32MediaService — fallback media service that reads QQ Music metadata
// directly from its hidden Daemon window title (instead of via GSMTC broker).
//
// Why: GSMTC broker deadlocks freeze GlobalSystemMediaTransportControlsSessionManager
//      .RequestAsync() indefinitely. This service bypasses the broker entirely:
//      it uses Win32 GetWindowText() on QQMusic_Daemon_Wnd, whose title is formatted
//      as "歌名 - 歌手" whenever a track is loaded.
//
// What we can extract:
//   Title         ✅  from window title (before " - ")
//   Artist        ✅  from window title (after " - ")
//   Source        ✅  "QQMusic" derived from process name
//   isPlaying     ❌  no reliable source — left false (honest "I don't know");
//                   frontend uses AudioBeatService RMS as proxy (see WallpaperDashboard.tsx)
//   Album/Album   ❌  not in title — left null
//   Position      ❌  no reliable source — left 0
//   Duration      ❌  no reliable source — left 0
//   Cover         ❌  CEF renders offscreen; PrintWindow path is brittle — covered
//                   via QqMusicCoverLookupService (third tier)
//
// GSMTC remains the primary path; this is only used after a GSMTC timeout.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

    public Task<MediaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var (title, artist) = ReadQqMusicTitleAndArtist();
        bool hasTrack = !string.IsNullOrWhiteSpace(title);

        return Task.FromResult(new MediaStatus(
            Connected: true,
            Source: hasTrack ? "QQMusic" : null,
            Title: title,
            Artist: artist,
            Album: null,
            CoverUrl: null,
            IsPlaying: false, // Win32 fallback cannot distinguish play/pause
            PositionMs: 0,
            DurationMs: 0,
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