using System.Diagnostics;
using System.Text.Json;
using xiaoxu_music_bridge.Media;

namespace xiaoxu_music_bridge.Common;

public sealed class GsmtcRecoveryService
{
    private static readonly SemaphoreSlim RepairLock = new(1, 1);

    public async Task<object> RepairAsync(CancellationToken cancellationToken)
    {
        if (!await RepairLock.WaitAsync(0, cancellationToken))
            return Failure("repair_busy", "Another GSMTC repair is already running", "probe");

        try
        {
            var mode = CurrentMode();
            GsmtcHealthTracker.ResetRecoveryState();
            GsmtcCircuitBreaker.Reset();

            Log("repair started: recovery state reset");
            var first = await RunProbeProcessAsync(cancellationToken);
            LogProbe("initial-probe", first);
            if (first.IsSuccess) return Success(mode, first, restarted: "none");
            if (!MediaModePolicy.ShouldRestartBroker(first.ErrorCode))
                return Failure(first.ErrorCode!, first.ErrorMessage ?? "GSMTC probe failed", "probe", mode);

            RestartRuntimeBroker();
            await Task.Delay(1500, cancellationToken);
            var afterBroker = await RunProbeProcessAsync(cancellationToken);
            LogProbe("runtime-broker-verify", afterBroker);
            if (afterBroker.IsSuccess) return Success(mode, afterBroker, restarted: "runtimeBroker");
            if (!MediaModePolicy.ShouldRestartBroker(afterBroker.ErrorCode))
                return Failure(afterBroker.ErrorCode!, afterBroker.ErrorMessage ?? "GSMTC probe failed after RuntimeBroker restart", "broker-verify", mode);

            Log("requesting elevated audio service restart");
            var restart = await RestartAudioServicesElevatedAsync(cancellationToken);
            if (!restart.ok)
            {
                Log($"audio service restart failed: {restart.code}: {restart.message}");
                return Failure(restart.code, restart.message, "service-restart", mode);
            }

            var verified = await RunProbeProcessAsync(cancellationToken);
            LogProbe("audio-service-verify", verified);
            return verified.IsSuccess
                ? Success(mode, verified, restarted: "audioServices")
                : Failure(verified.ErrorCode ?? "gsmtc_timeout", verified.ErrorMessage ?? "GSMTC still timed out after RuntimeBroker and audio service restart", "audio-verify", mode);
        }
        finally
        {
            RepairLock.Release();
        }
    }

    public static async Task<GsmtcProbeResult> ProbeCurrentProcessAsync()
    {
        try
        {
            var status = await new WindowsMediaSessionService().GetStatusAsync(CancellationToken.None);
            if (string.IsNullOrWhiteSpace(status.Title))
                return new(true, status.Source, null, status.Artist, status.PositionMs, status.DurationMs,
                    "no_media_session", "GSMTC is available but no current media session was found");
            return new(true, status.Source, status.Title, status.Artist, status.PositionMs, status.DurationMs, null, null);
        }
        catch (FileNotFoundException ex) { return DependencyFailure(ex); }
        catch (DllNotFoundException ex) { return DependencyFailure(ex); }
        catch (TypeInitializationException ex) { return DependencyFailure(ex); }
        catch (Exception ex)
        {
            return new(false, null, null, null, 0, 0, "gsmtc_activation_failed", ex.Message);
        }
    }

    public static int RestartAudioServicesElevatedHelper()
    {
        return Run("net.exe", "stop audiosrv /y", 20_000)
            && Run("net.exe", "start audiosrv", 20_000)
            && Run("net.exe", "stop AudioEndpointBuilder /y", 20_000, allowFailure: true)
            && Run("net.exe", "start AudioEndpointBuilder", 20_000)
            ? 0 : 1;
    }

    private static async Task<GsmtcProbeResult> RunProbeProcessAsync(CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate host executable");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--gsmtc-probe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Cannot start GSMTC probe");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var exited = await Task.WhenAny(process.WaitForExitAsync(cancellationToken), Task.Delay(5000, cancellationToken));
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            return new(false, null, null, null, 0, 0, "gsmtc_timeout", "GSMTC probe timed out after 5 seconds");
        }

        try
        {
            return JsonSerializer.Deserialize<GsmtcProbeResult>(await outputTask)
                ?? new(false, null, null, null, 0, 0, "gsmtc_activation_failed", "Probe returned no result");
        }
        catch (Exception ex)
        {
            return new(false, null, null, null, 0, 0, "gsmtc_activation_failed", ex.Message);
        }
    }

    private static async Task<(bool ok, string code, string message)> RestartAudioServicesElevatedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate host executable");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--restart-gsmtc-services",
                Verb = "runas",
                UseShellExecute = true,
            });
            if (process is null) return (false, "service_restart_failed", "Could not start elevated repair helper");
            var exitTask = process.WaitForExitAsync(cancellationToken);
            if (await Task.WhenAny(exitTask, Task.Delay(60_000, cancellationToken)) != exitTask)
                return (false, "service_restart_failed", "Timed out waiting for the elevated audio service repair");
            return process.ExitCode == 0
                ? (true, "", "")
                : (false, "service_restart_failed", $"Audio service repair exited with code {process.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "uac_cancelled", "Administrator approval was cancelled");
        }
        catch (Exception ex)
        {
            return (false, "service_restart_failed", ex.Message);
        }
    }

    private static bool Run(string fileName, string arguments, int timeoutMs, bool allowFailure = false)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null || !process.WaitForExit(timeoutMs)) return false;
        return allowFailure || process.ExitCode == 0;
    }

    private static void RestartRuntimeBroker()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var killed = 0;
        foreach (var process in Process.GetProcessesByName("RuntimeBroker"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId) continue;
                    process.Kill(entireProcessTree: true);
                    killed++;
                }
                catch (Exception ex)
                {
                    Log($"RuntimeBroker PID={process.Id} restart failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        Log($"RuntimeBroker restart requested: killed={killed}, session={sessionId}");
    }

    private static GsmtcProbeResult DependencyFailure(Exception ex) =>
        new(false, null, null, null, 0, 0, "missing_dependency", ex.Message);

    private static string CurrentMode()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xiaoxu-music-host");
        return MediaModeStore.ToWireValue(new MediaModeStore(directory).Get());
    }

    private static object Success(string mode, GsmtcProbeResult probe, string restarted) => new
    {
        ok = true, mode, repaired = true, restarted,
        session = new { source = probe.Source, title = probe.Title, artist = probe.Artist, positionMs = probe.PositionMs, durationMs = probe.DurationMs }
    };

    private static void LogProbe(string stage, GsmtcProbeResult result) =>
        Log($"{stage}: success={result.IsSuccess}, api={result.ApiAvailable}, code={result.ErrorCode ?? "none"}, source={result.Source ?? "none"}, title={result.Title ?? "none"}");

    private static void Log(string message) => LogPaths.SafeAppend(LogPaths.DebugLog,
        $"[{DateTime.Now:HH:mm:ss.fff}] [GsmtcRepair] {message}\n");

    private static object Failure(string code, string message, string stage, string? mode = null) => new
    {
        ok = false, mode = mode ?? CurrentMode(), repaired = false,
        error = new { code, message, stage }
    };
}
