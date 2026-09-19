using System.Diagnostics;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Common;

/// <summary>
/// v3.6.7 self-recovery plumbing for the host running in --server mode.
///
/// Three guards, all opt-in via <see cref="HostRecoveryOptions"/>:
///
///   1. <b>Silent respawn</b>: when the WinForms loop returns, the host should
///      NOT exit silently — that is how the user lost music on Chrome restarts
///      (stdin closes → Application.Run() returns → process exits → bridge HTTP
///      dies → the dashboard freezes on a uniform-white particle wall). We
///      fork our own PID with the same args, hand over the log file, and
///      back off with an exponential delay if we keep crashing.
///
///   2. <b>Silence watchdog</b>: if the bridge reports the host is the
///      "playing" instance yet <see cref="AudioBeatService.IsCaptureReady"/>
///      stays false (or the wall-clock keeps advancing with no
///      capture-restart events) for too long, the WASAPI capture is hung.
///      We dispose the capture and ask the AudioBeatService to re-Start().
///
///   3. <b>Crash-loop guard</b>: after N respawns in M seconds we stop trying
///      and let the process exit so the user sees the tray icon turn red
///      instead of being trapped in a hot loop.
///
/// Everything logs to <see cref="LogPaths.DebugLog"/>; nothing throws out of
/// the timer callback (we eat + log).
/// </summary>
public sealed class HostRecovery : IDisposable
{
    private readonly string[] _originalArgs;
    private readonly string _exePath;
    private readonly HostRecoveryOptions _opts;
    private readonly Func<bool> _isHealthy;          // capture-ready AND clock advancing
    private readonly Action _restartCapture;         // dispose + Start() the AudioBeatService
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private System.Threading.Timer? _watchdog;
    private int _respawnCount;
    private DateTime _firstRespawnAt = DateTime.MinValue;

    public HostRecovery(
        string[] originalArgs,
        string exePath,
        HostRecoveryOptions options,
        Func<bool> isHealthy,
        Action restartCapture,
        Action<string>? log = null)
    {
        _originalArgs = originalArgs;
        _exePath = exePath;
        _opts = options;
        _isHealthy = isHealthy;
        _restartCapture = restartCapture;
        _log = log ?? DefaultLog;
    }

    public void Start()
    {
        if (!_opts.EnableSilenceWatchdog && !_opts.EnableRespawn) return;
        _watchdog = new System.Threading.Timer(
            OnTick, state: null,
            dueTime: TimeSpan.FromSeconds(_opts.WatchdogIntervalSec),
            period: TimeSpan.FromSeconds(_opts.WatchdogIntervalSec));
        _log($"[HostRecovery] enabled (respawn={_opts.EnableRespawn}, " +
             $"watchdog={_opts.EnableSilenceWatchdog}, " +
             $"intervalSec={_opts.WatchdogIntervalSec}, " +
             $"silenceThresholdSec={_opts.SilenceThresholdSec})");
    }

    private void OnTick(object? state)
    {
        try
        {
            if (_opts.EnableSilenceWatchdog && !_isHealthy())
            {
                _log($"[HostRecovery] silence/unhealthy — restarting audio capture");
                _restartCapture();
            }
        }
        catch (Exception ex)
        {
            _log($"[HostRecovery] watchdog tick threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Call from the WinForms Application.Run() post-return site. Spawns a
    /// fresh copy of ourselves with the same args (less our marker flag) and
    /// lets the current process exit. Backs off if we keep crashing.
    /// Returns <c>true</c> when a child was spawned (current process should
    /// keep going for a moment so the socket is handed over); <c>false</c>
    /// when the crash-loop guard tripped (caller should return normally).
    /// </summary>
    public bool TryRespawnOnUnexpectedExit(string reason)
    {
        if (!_opts.EnableRespawn)
        {
            _log($"[HostRecovery] respawn disabled — exiting for reason: {reason}");
            return false;
        }

        lock (_gate)
        {
            if (_firstRespawnAt == DateTime.MinValue) _firstRespawnAt = DateTime.UtcNow;
            _respawnCount++;
            var sinceFirst = DateTime.UtcNow - _firstRespawnAt;

            if (sinceFirst > TimeSpan.FromSeconds(_opts.RespawnWindowSec))
            {
                // window slid past — reset counter
                _respawnCount = 1;
                _firstRespawnAt = DateTime.UtcNow;
                sinceFirst = TimeSpan.Zero;
            }

            if (_respawnCount > _opts.MaxRespawnsInWindow)
            {
                _log($"[HostRecovery] crash-loop guard tripped after " +
                     $"{_respawnCount} respawns in {sinceFirst.TotalSeconds:F1}s — exiting");
                return false;
            }

            // Pass --recovery-respawn=<count> so the child knows it is a child
            // of a crashed parent (lets it log differently + skip the watchdog
            // for the first few seconds to avoid double-respawn loops).
            var args = new List<string>(_originalArgs)
            {
                $"--recovery-respawn={_respawnCount}"
            };

            // Exponential backoff up to 4s.
            var backoffMs = Math.Min(4000, 250 * (1 << Math.Min(5, _respawnCount - 1)));
            _log($"[HostRecovery] unexpected exit ({reason}); respawn " +
                 $"#{_respawnCount} in {backoffMs}ms with args: " +
                 $"[{string.Join(" ", args)}]");

            try
            {
                Thread.Sleep(backoffMs);
                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_exePath) ?? Environment.CurrentDirectory,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
                _log($"[HostRecovery] respawn child launched, current pid={Environment.ProcessId} exiting");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[HostRecovery] respawn FAILED: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    public void Dispose()
    {
        try { _watchdog?.Dispose(); } catch { }
        _watchdog = null;
    }

    private static void DefaultLog(string msg)
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
    }
}

public sealed record HostRecoveryOptions(
    bool EnableRespawn = true,
    bool EnableSilenceWatchdog = true,
    double WatchdogIntervalSec = 5.0,
    double SilenceThresholdSec = 30.0,
    int MaxRespawnsInWindow = 5,
    double RespawnWindowSec = 60.0);