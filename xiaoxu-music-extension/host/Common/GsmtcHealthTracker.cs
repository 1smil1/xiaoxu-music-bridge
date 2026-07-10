using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace xiaoxu_music_bridge.Common;

/// <summary>
/// Tracks GSMTC (GlobalSystemMediaTransportControls) call health and triggers
/// escalating host self-restart when the API is hung.
///
/// Why: GSMTC is a WinRT singleton bound to the OS-level Windows Audio broker.
/// When QQ Music (or any media player) has a stuck media-session handle, the
/// GSMTC API call to <c>GlobalSystemMediaTransportControlsSessionManager.RequestAsync()</c>
/// hangs forever. A simple process restart clears the WinRT activation state —
/// but if the broker itself is wedged, escalating to restarting <c>audiosrv</c>
/// or even <c>audiodg.exe</c> is required. After N unsuccessful escalation
/// attempts, the host stops self-restarting and signals "stuck" so the UI can
/// prompt the user to run <c>restart-audio.bat</c> manually.
///
/// Recovery escalation (persisted across processes via restart_count.txt):
///   1. host self-restart  — clears per-process WinRT activation
///   2. + audiosrv stop/start — rebuilds the GSMTC broker singleton
///   3. + audiodg.exe kill — forces Windows to rebuild audio device graph
///   4. stop self-restart loop, report isStuck=true (manual recovery needed)
///
/// v3.2.5: PERMANENT-BROKEN detection. Some GSMTC failures are unfixable by
/// restart: the WinRT projection DLL is missing from the single-file publish
/// (FileNotFoundException) or a static type initializer crashed
/// (TypeInitializationException). No amount of host/service restart will
/// bring these back. Detect them on first occurrence, persist a flag to
/// disk, and skip all future GSMTC calls (always fall back to Win32). This
/// stops the death-loop where the host restarts every 2s, killing
/// BuildStateResponseAsync mid-flight and breaking the dashboard.
///
/// Levels 2 and 3 require administrator privileges. If the host runs as a
/// normal user (the current deployment), the underlying <c>net stop audiosrv</c>
/// will fail with "Access Denied" — that's fine, we'll still increment the
/// counter and eventually degrade to manual recovery. The infrastructure is in
/// place for when the host is upgraded to run as a Windows Service.
/// </summary>
public static class GsmtcHealthTracker
{
    /// <summary>Consecutive GSMTC timeouts before we consider the API hung.</summary>
    public const int FailureThreshold = 3;

    /// <summary>After this many escalations across processes, stop self-restarting.</summary>
    public const int StuckThreshold = 4;

    /// <summary>Restart count is considered stale (reset) after this many hours of no failures.</summary>
    private const double StaleHours = 1.0;

    private static int _consecutiveFailures;
    private static DateTime _firstFailureAt;
    private static DateTime _lastSuccessAt;
    private static bool _restartTriggered;
    private static readonly object _lock = new();

    // v3.2.5: permanently-broken flag, persisted to disk so new host processes
    // inherit the skip-GSMTC decision across restarts.
    private static bool _isPermanentlyBroken;
    private static readonly object _permanentLock = new();

    private static string CountFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "xiaoxu-music-host",
        "gsmtc_restart_count.txt");

    private static string PermanentFlagPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "xiaoxu-music-host",
        "gsmtc_permanently_broken.flag");

    /// <summary>
    /// v3.2.5: True once a non-recoverable GSMTC failure was observed
    /// (FileNotFoundException, TypeInitializationException, etc.). When true,
    /// GsmtcCircuitBreaker.ShouldSkip() returns true forever, so the host stays
    /// on the Win32 fallback path and never tries GSMTC again. The flag is
    /// persisted to disk so new host processes inherit the decision.
    /// </summary>
    public static bool IsPermanentlyBroken
    {
        get { lock (_permanentLock) return _isPermanentlyBroken; }
    }

    /// <summary>
    /// Static constructor: load persisted permanently-broken flag if present.
    /// Must come AFTER all field initializers so the flag is read into the
    /// field, not lost in a race.
    /// </summary>
    static GsmtcHealthTracker()
    {
        try
        {
            if (File.Exists(PermanentFlagPath))
            {
                lock (_permanentLock) _isPermanentlyBroken = true;
                string content = File.ReadAllText(PermanentFlagPath).Trim();
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] GSMTC permanently-broken flag LOADED at startup: {content}\n");
            }
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] WARN: failed to load permanently-broken flag: {ex.GetType().Name}\n");
        }
    }

    /// <summary>
    /// v3.2.5: Test-only / recovery helper. Clears the in-memory and persisted
    /// permanently-broken flag so GSMTC will be retried on the next call.
    /// The user can trigger this by running <c>reset-bridge.bat</c> after
    /// reinstalling the missing WinRT projection DLL.
    /// </summary>
    public static void ClearPermanentlyBrokenFlag()
    {
        lock (_permanentLock) _isPermanentlyBroken = false;
        try { if (File.Exists(PermanentFlagPath)) File.Delete(PermanentFlagPath); } catch { /* ignore */ }
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] GSMTC permanently-broken flag CLEARED — GSMTC will be retried\n");
    }

    /// <summary>Called from getStatus / getLyrics after a successful (non-timeout) response.</summary>
    public static void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures > 0)
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] GSMTC RECOVERED after {_consecutiveFailures} failures\n");
            }
            _consecutiveFailures = 0;
            _lastSuccessAt = DateTime.Now;
            // Successful call → also clear stale escalation count (GSMTC is healthy)
            TryResetCount();
        }
    }

    /// <summary>Called from getStatus / getLyrics after a 5s timeout.</summary>
    /// <param name="operationName">For logging — which operation timed out.</param>
    /// <param name="syncFault">v3.2.5: the synchronous exception thrown by the
    /// GSMTC task, if any. When non-null and the exception type indicates a
    /// non-recoverable failure (missing WinRT DLL, type init crash), the
    /// permanently-broken flag is set and no self-restart escalation happens.</param>
    public static void RecordFailure(string operationName, Exception? syncFault = null)
    {
        int failures;
        TimeSpan elapsed;
        bool shouldRestart = false;
        bool permanentFaultObserved = false;

        // v3.2.5: detect permanent breakage BEFORE counting toward escalation.
        // These exception types mean "no restart will help" — escalating
        // audiosrv/audiodg just thrashes the system and kills the host mid-
        // /state/current, leaving the dashboard permanently broken.
        if (syncFault != null && IsPermanentFault(syncFault))
        {
            lock (_permanentLock)
            {
                if (!_isPermanentlyBroken)
                {
                    _isPermanentlyBroken = true;
                    permanentFaultObserved = true;
                }
            }
            if (permanentFaultObserved)
            {
                PersistPermanentFlag(syncFault);
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] *** GSMTC PERMANENTLY BROKEN ({syncFault.GetType().Name}: {syncFault.Message}) — " +
                    $"switching to permanent Win32 fallback. No more self-restart. ***\n");
            }
        }

        lock (_lock)
        {
            // If permanently broken, log the failure but DO NOT count toward
            // escalation. Counting + SelfRestart.Run here is exactly what
            // breaks /state/current mid-flight and strands the dashboard.
            if (_isPermanentlyBroken)
            {
                _consecutiveFailures++;
                int n = _consecutiveFailures;
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] GSMTC failure #{n} ({operationName}) — permanently broken, skipping escalation\n");
                return;
            }

            if (_consecutiveFailures == 0)
            {
                _firstFailureAt = DateTime.Now;
            }
            _consecutiveFailures++;
            failures = _consecutiveFailures;
            elapsed = DateTime.Now - _firstFailureAt;
            shouldRestart = failures >= FailureThreshold && !_restartTriggered;
            if (shouldRestart) _restartTriggered = true;
        }

        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] GSMTC failure #{failures} ({operationName}) " +
            $"[elapsed since first failure: {elapsed.TotalSeconds:F1}s]\n");

        if (shouldRestart)
        {
            int totalRestarts = LoadAndIncrementCount();
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] *** GSMTC STUCK ({failures} consecutive failures, " +
                $"{elapsed.TotalSeconds:F0}s elapsed) — escalation level {totalRestarts}/{StuckThreshold} ***\n");

            if (totalRestarts >= StuckThreshold)
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] *** ESCALATION LIMIT REACHED — host will NOT self-restart. " +
                    $"User must run restart-audio.bat manually. ***\n");
                SelfRestart.StuckOnExit = true;
                // Spawn a no-op replacement so the pipe doesn't go EOF, but skip the audiosrv escalation
                SelfRestart.SpawnReplacementAndExit(skipEscalation: true);
                return;
            }

            // Spawn + exit must happen off the lock to avoid self-deadlock
            SelfRestart.Run(level: totalRestarts);
        }
    }

    /// <summary>
    /// v3.2.5: Returns true when <paramref name="ex"/> (or any inner exception)
    /// indicates a failure that no host/service restart can recover from. These
    /// are load-time / type-system failures, not runtime broker deadlocks.
    /// </summary>
    private static bool IsPermanentFault(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            // Missing WinRT projection DLL — single-file publish dropped a
            // transitive dependency. Re-running the host won't materialize
            // the file; only an installer / rebuild can fix it.
            if (e is FileNotFoundException) return true;
            if (e is DllNotFoundException) return true;
            if (e is EntryPointNotFoundException) return true;
            // A static constructor / type initializer crashed. The CLR caches
            // the failure for the AppDomain lifetime — a host restart gives
            // a fresh AppDomain, so technically THIS could recover. But in
            // practice the user's machine is missing a system component
            // (Visual C++ runtime, WinRT activation context, etc.) and every
            // restart hits the same fault. Treat as permanent.
            if (e is TypeInitializationException) return true;
        }
        return false;
    }

    private static void PersistPermanentFlag(Exception syncFault)
    {
        try
        {
            string dir = Path.GetDirectoryName(PermanentFlagPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(PermanentFlagPath,
                $"permanent|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}|" +
                $"{syncFault.GetType().Name}|{syncFault.Message}");
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] WARN: failed to persist permanent-broken flag: " +
                $"{ex.GetType().Name}: {ex.Message}\n");
        }
    }

    /// <summary>For diagnostics: current failure count and time since last success.</summary>
    public static (int failures, double secondsSinceLastSuccess) GetStatus()
    {
        lock (_lock)
        {
            double sinceSuccess = _lastSuccessAt == DateTime.MinValue
                ? -1
                : (DateTime.Now - _lastSuccessAt).TotalSeconds;
            return (_consecutiveFailures, sinceSuccess);
        }
    }

    /// <summary>For diagnostics: full escalation state including persistent counters.</summary>
    public static GsmtcHealthSnapshot GetSnapshot()
    {
        int totalRestarts;
        DateTime lastFailureAt;
        lock (_lock)
        {
            totalRestarts = LoadCount_NoLock();
            lastFailureAt = _firstFailureAt;
        }
        bool isStuck = totalRestarts >= StuckThreshold
            || (lastFailureAt != DateTime.MinValue
                && (DateTime.Now - lastFailureAt).TotalHours > StaleHours
                && totalRestarts > 0);
        return new GsmtcHealthSnapshot(
            ConsecutiveFailures: GetStatus().failures,
            TotalRestarts: totalRestarts,
            LastSuccessAt: _lastSuccessAt == DateTime.MinValue ? null : _lastSuccessAt,
            IsStuck: isStuck);
    }

    private static int LoadAndIncrementCount()
    {
        lock (_lock)
        {
            int current = LoadCount_NoLock();
            int next = current + 1;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CountFilePath)!);
                // Format: "<count>|<lastFailureUnixMs>" — used for staleness reset on startup.
                File.WriteAllText(CountFilePath,
                    $"{next}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            }
            catch (Exception ex)
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] WARN: failed to persist restart count: " +
                    $"{ex.GetType().Name}: {ex.Message}\n");
            }
            return next;
        }
    }

    private static int LoadCount_NoLock()
    {
        try
        {
            if (!File.Exists(CountFilePath)) return 0;
            string text = File.ReadAllText(CountFilePath).Trim();
            // New format: "count|timestampMs". Legacy: plain integer (count only).
            string[] parts = text.Split('|');
            if (!int.TryParse(parts[0], out int n)) return 0;
            if (parts.Length >= 2 && long.TryParse(parts[1], out long tsMs))
            {
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(tsMs);
                if (age.TotalHours > StaleHours)
                {
                    // Stale: GSMTC was healthy for >1h between this and the previous failure.
                    // Treat the persisted count as if it never happened.
                    LogPaths.SafeAppend(LogPaths.DebugLog,
                        $"[{DateTime.Now:HH:mm:ss.fff}] GSMTC restart count stale " +
                        $"({age.TotalHours:F1}h old) — resetting\n");
                    try { File.Delete(CountFilePath); } catch { /* ignore */ }
                    return 0;
                }
            }
            return n;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryResetCount()
    {
        try
        {
            if (File.Exists(CountFilePath))
            {
                File.Delete(CountFilePath);
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] GSMTC healthy — restart count cleared\n");
            }
        }
        catch
        {
            // Non-fatal
        }
    }
}

/// <summary>Diagnostic snapshot of GSMTC recovery state.</summary>
public sealed record GsmtcHealthSnapshot(
    int ConsecutiveFailures,
    int TotalRestarts,
    DateTime? LastSuccessAt,
    bool IsStuck);

/// <summary>
/// Escalating self-restart logic. Extracted from <see cref="GsmtcHealthTracker"/>
/// so the recovery state machine is easy to read in isolation.
/// </summary>
internal static class SelfRestart
{
    /// <summary>
    /// If true, the about-to-spawn replacement host will NOT escalate audio
    /// services. Set by <see cref="GsmtcHealthTracker"/> when the escalation
    /// limit is reached — we still need to spawn a replacement so the SW
    /// stays connected, but we don't want to thrash <c>audiosrv</c> further.
    /// </summary>
    public static bool StuckOnExit { get; set; }

    /// <summary>
    /// Escalate-and-restart based on the current level.
    /// </summary>
    /// <param name="level">1 = plain restart; 2 = + audiosrv cycle; 3 = + audiodg.exe kill.</param>
    public static void Run(int level)
    {
        // v3.2.7: if GSMTC has been flagged permanently broken, the host must
        // NOT self-restart — every restart creates a ~10-15s downtime + audio
        // device re-initialization window during which /state/current returns
        // null and the dashboard's lyric sync breaks. The user has to run
        // restart-audio.bat manually to recover (or reset-bridge.bat if they
        // reinstall the missing WinRT DLL).
        if (GsmtcHealthTracker.IsPermanentlyBroken)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] ESCALATION: GSMTC PERMANENTLY BROKEN — " +
                $"suppressing self-restart at level {level}. User must run restart-audio.bat manually.\n");
            return;
        }

        try
        {
            switch (level)
            {
                case 1:
                    // Plain host self-restart — current behavior
                    break;
                case 2:
                    TryRestartAudiosrv();
                    break;
                case 3:
                    TryRestartAudiosrv();
                    TryKillAudiodg();
                    break;
                default:
                    // level >= StuckThreshold is handled by caller
                    break;
            }
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] ESCALATION failed at level {level}: " +
                $"{ex.GetType().Name}: {ex.Message}\n");
        }

        SpawnReplacementAndExit(skipEscalation: false);
    }

    /// <summary>
    /// Spawn a fresh instance of this host, then exit the current one. Chrome's
    /// service worker will detect the disconnect and reconnect to the new
    /// process on the next native message.
    /// </summary>
    public static void SpawnReplacementAndExit(bool skipEscalation)
    {
        // v3.2.7: belt-and-braces guard. Track C1 also blocks Run() above,
        // but this direct call site (from RecordFailure's stuck-on-exit path)
        // needs the same protection.
        if (GsmtcHealthTracker.IsPermanentlyBroken)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] SpawnReplacementAndExit suppressed: GSMTC PERMANENTLY BROKEN. " +
                $"Drain and stay alive.\n");
            return;
        }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                using var p = Process.GetCurrentProcess();
                exePath = p.MainModule?.FileName;
            }

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] Self-restart FAILED: cannot locate own exe path\n");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };

            string[] originalArgs = Environment.GetCommandLineArgs();
            for (int i = 1; i < originalArgs.Length; i++)
            {
                psi.ArgumentList.Add(originalArgs[i]);
            }

            var newProc = Process.Start(psi);
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] Self-restart spawned new host PID={newProc?.Id} " +
                $"path={exePath} skipEscalation={skipEscalation} stuck={StuckOnExit} " +
                $"args=[{string.Join(", ", originalArgs, 1, originalArgs.Length - 1)}]\n");
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] Self-restart spawn FAILED: {ex.GetType().Name}: {ex.Message}\n");
        }

        // Give the log line a moment to flush, then exit. Chrome will see EOF
        // on our stdin/stdout and the SW will reconnect on the next message.
        Environment.Exit(0);
    }

    private static void TryRestartAudiosrv()
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] ESCALATING to audiosrv restart (level 2)\n");
        try
        {
            // net.exe is a separate process, no UAC prompt if we already have admin.
            // If we don't have admin, net will return errorlevel 5 (Access Denied) — log it and continue.
            using (var p = Process.Start(new ProcessStartInfo
            {
                FileName = "net.exe",
                Arguments = "stop audiosrv /y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }))
            {
                string stdout = p?.StandardOutput.ReadToEnd() ?? "";
                string stderr = p?.StandardError.ReadToEnd() ?? "";
                p?.WaitForExit(15000);
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] net stop audiosrv exit={p?.ExitCode} out={stdout.Trim()} err={stderr.Trim()}\n");
            }

            Thread.Sleep(1500);

            using (var p = Process.Start(new ProcessStartInfo
            {
                FileName = "net.exe",
                Arguments = "start audiosrv",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }))
            {
                string stdout = p?.StandardOutput.ReadToEnd() ?? "";
                string stderr = p?.StandardError.ReadToEnd() ?? "";
                p?.WaitForExit(15000);
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] net start audiosrv exit={p?.ExitCode} out={stdout.Trim()} err={stderr.Trim()}\n");
            }

            Thread.Sleep(2000);
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] audiosrv restart threw: {ex.GetType().Name}: {ex.Message}\n");
        }
    }

    private static void TryKillAudiodg()
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] ESCALATING to audiodg.exe kill (level 3) — Windows will auto-rebuild\n");
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/F /IM audiodg.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            string stdout = p?.StandardOutput.ReadToEnd() ?? "";
            string stderr = p?.StandardError.ReadToEnd() ?? "";
            p?.WaitForExit(10000);
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] taskkill audiodg.exe exit={p?.ExitCode} out={stdout.Trim()} err={stderr.Trim()}\n");
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] audiodg.exe kill threw: {ex.GetType().Name}: {ex.Message}\n");
        }
    }
}