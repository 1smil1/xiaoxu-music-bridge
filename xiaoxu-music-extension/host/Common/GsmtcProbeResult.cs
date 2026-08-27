namespace xiaoxu_music_bridge.Common;

public sealed record GsmtcProbeResult(
    bool ApiAvailable,
    string? Source,
    string? Title,
    string? Artist,
    long PositionMs,
    long DurationMs,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsSuccess => ApiAvailable && !string.IsNullOrWhiteSpace(Title) && ErrorCode is null;
}
