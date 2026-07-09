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
/// </summary>
public static class GsmtcCircuitBreaker
{
    private static DateTime _skipUntil = DateTime.MinValue;
    private static DateTime _coverSkipUntil = DateTime.MinValue;
    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(30);

    // Status / Lyrics breaker
    public static void Open() => _skipUntil = DateTime.Now + OpenDuration;
    public static bool ShouldSkip() => DateTime.Now < _skipUntil;

    // Cover-only breaker (independent)
    public static void OpenCover() => _coverSkipUntil = DateTime.Now + OpenDuration;
    public static bool ShouldSkipCover() => DateTime.Now < _coverSkipUntil;
}