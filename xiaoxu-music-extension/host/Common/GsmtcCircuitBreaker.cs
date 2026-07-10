namespace xiaoxu_music_bridge.Common;

/// <summary>
/// GSMTC circuit breaker — closes the GSMTC path for 30s after a timeout so we
/// don't pay the 1.5s penalty on every getStatus call when GSMTC's broker is
/// deadlocked. Re-opens after 30s; the next request will probe GSMTC again to
/// detect when the broker recovers.
///
/// v3.2.2: Cover and Status/Lyric breakers are now independent. Cover calls
/// (GetCurrentCoverAsync, WinRT async + CEF thumbnail decode) are the heaviest
/// and most likely to deadlock when GSMTC's broker is unhealthy; opening the
/// status breaker as a side effect used to block the position pipeline for
/// 30s, breaking user-initiated seeks and freezing lyric scroll. Cover and
/// status now fail over independently.
///
/// v3.2.2.1: Background probe — when the status breaker opens, a background
/// task probes GSMTC every 3s and closes the breaker the moment the broker
/// recovers (typically 1-3s after the user's seek/seek-animation completes).
/// Without this, a QQ Music seek would deadlock GSMTC briefly → breaker opens
/// → status shows viaFallback=true → positionMs=0 → lyric frozen for up to
/// 30s waiting for the natural reset.
/// </summary>
public static class GsmtcCircuitBreaker
{
    private static DateTime _skipUntil = DateTime.MinValue;
    private static DateTime _coverSkipUntil = DateTime.MinValue;
    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(30);
    private static bool _probeActive = false;
    private static Func<Task<bool>>? _probeFn = null;
    private static Action<string>? _probeLog = null;

    // Status / Lyrics breaker
    public static void Open()
    {
        _skipUntil = DateTime.Now + OpenDuration;
        TriggerProbe();
    }
    public static void Close() => _skipUntil = DateTime.MinValue;
    public static bool ShouldSkip() => DateTime.Now < _skipUntil;

    // Cover-only breaker (independent)
    public static void OpenCover() => _coverSkipUntil = DateTime.Now + OpenDuration;
    public static bool ShouldSkipCover() => DateTime.Now < _coverSkipUntil;

    /// <summary>
    /// Register a probe function. It must return true when GSMTC is healthy
    /// (call completed within budget with no exception), false otherwise.
    /// Called once at startup; the breaker holds a reference for the process
    /// lifetime.
    /// </summary>
    public static void RegisterProbe(Func<Task<bool>> probeFn, Action<string> log)
    {
        _probeFn = probeFn;
        _probeLog = log;
    }

    private static void TriggerProbe()
    {
        if (_probeActive || _probeFn == null) return;
        _probeActive = true;
        var fn = _probeFn;
        var log = _probeLog;
        _ = Task.Run(async () =>
        {
            try
            {
                int attempt = 0;
                while (ShouldSkip() && attempt < 10)
                {
                    await Task.Delay(3000);
                    attempt++;
                    try
                    {
                        bool healthy = await fn();
                        if (healthy)
                        {
                            Close();
                            log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Background GSMTC probe SUCCESS (attempt {attempt}) — status breaker closed\n");
                            return;
                        }
                        log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Background probe attempt {attempt} still failing\n");
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Background probe attempt {attempt} exception: {ex.GetType().Name}\n");
                    }
                }
            }
            finally
            {
                _probeActive = false;
            }
        });
    }
}