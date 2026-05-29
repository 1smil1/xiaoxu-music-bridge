namespace xiaoxu_music_bridge.Media;

public sealed record MediaStatus(
    bool Connected,
    string? Source,
    string? Title,
    string? Artist,
    string? Album,
    string? CoverUrl,
    bool IsPlaying,
    long PositionMs,
    long DurationMs,
    DateTimeOffset UpdatedAt)
{
    public static MediaStatus NoMedia()
    {
        return new MediaStatus(
            Connected: true,
            Source: null,
            Title: null,
            Artist: null,
            Album: null,
            CoverUrl: null,
            IsPlaying: false,
            PositionMs: 0,
            DurationMs: 0,
            UpdatedAt: DateTimeOffset.Now);
    }
}
