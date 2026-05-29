namespace xiaoxu_music_bridge.Lyrics;

public sealed record LyricResponse(
    bool Found,
    string? Title,
    string? Artist,
    string? FileName,
    string? Lrc);
