namespace xiaoxu_music_bridge.Common;

/// <summary>
/// GSMTC circuit breaker — closes the GSMTC path for 30s after a timeout so we
/// don't pay the 1.5s penalty on every getStatus call when GSMTC's broker is
/// deadlocked. Re-opens after 30s; the next request will probe GSMTC again to
/// detect when the broker recovers.
/// </summary>
public static class GsmtcCircuitBreaker
{
    private static DateTime _skipUntil = DateTime.MinValue;
    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(30);

    public static void Open() => _skipUntil = DateTime.Now + OpenDuration;
    public static bool ShouldSkip() => DateTime.Now < _skipUntil;
}