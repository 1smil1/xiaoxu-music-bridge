using System;
using System.Diagnostics;
using System.IO;

namespace xiaoxu_music_bridge.Common;

/// <summary>
/// Tracks GSMTC (GlobalSystemMediaTransportControls) call health and triggers
/// a host self-restart when the API is hung.
///
/// Why: GSMTC is a WinRT singleton bound to the host process. When QQ Music (or
/// any media player) has a stuck media-session handle, the GSMTC API call to
/// <c>GlobalSystemMediaTransportControlsSessionManager.RequestAsync()</c> hangs
/// forever — only a process restart clears the WinRT activation state. We can
/// detect the hang (5s timeout) and trigger a restart automatically.
///
/// Recovery flow on self-restart:
///   1. Old host: detect GSMTC stuck after N consecutive timeouts
///   2. Old host: spawn new instance of itself (detached) with same args
///   3. Old host: Environment.Exit(0) — Chrome sees stdin EOF
///   4. SW background.js onDisconnect: host=null, ensureBeatSubscribed()
///   5. SW: chrome.runtime.connectNative → Chrome spawns fresh host
///   6. New host: clean WinRT state, GSMTC works
/// </summary>
public static class GsmtcHealthTracker
{
    /// <summary>Consecutive GSMTC timeouts before we consider the API hung.</summary>
    public const int FailureThreshold = 3;

    private static int _consecutiveFailures;
    private static DateTime _firstFailureAt;
    private static DateTime _lastSuccessAt;
    private static bool _restartTriggered;
    private static readonly object _lock = new();

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
        }
    }

    /// <summary>Called from getStatus / getLyrics after a 5s timeout.</summary>
    public static void RecordFailure(string operationName)
    {
        int failures;
        TimeSpan elapsed;
        bool shouldRestart = false;
        lock (_lock)
        {
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
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] *** GSMTC STUCK ({failures} consecutive failures, " +
                $"{elapsed.TotalSeconds:F0}s elapsed) — initiating host self-restart ***\n");
            // Spawn new + exit must happen off the lock to avoid self-deadlock
            SelfRestart();
        }
    }

    /// <summary>
    /// Spawn a fresh instance of this host, then exit the current one. Chrome's
    /// service worker will detect the disconnect and reconnect to the new process
    /// (or to a Chrome-spawned replacement) on the next native message.
    /// </summary>
    private static void SelfRestart()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                // Fallback: use Process.MainModule
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

            // Pass through the original args (lyrics dir etc.) so the new
            // host has the same configuration.
            string[] originalArgs = Environment.GetCommandLineArgs();
            for (int i = 1; i < originalArgs.Length; i++)
            {
                psi.ArgumentList.Add(originalArgs[i]);
            }

            var newProc = Process.Start(psi);
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss.fff}] Self-restart spawned new host PID={newProc?.Id} " +
                $"path={exePath} args=[{string.Join(", ", originalArgs, 1, originalArgs.Length - 1)}]\n");
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
}