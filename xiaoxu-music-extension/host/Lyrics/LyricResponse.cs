namespace xiaoxu_music_bridge.Lyrics;

/// <summary>
/// Lyric payload returned by the bridge HTTP /state/current endpoint.
///
/// v3.6.7: added <see cref="Yrc"/> so the dashboard can distinguish
/// Netease's word-level karaoke payload (per-character timing) from the
/// generic line-level <see cref="Lrc"/>. The dashboard parser uses YRC
/// first if non-empty; LRC stays the fallback for the lyric stage.
/// </summary>
public sealed record LyricResponse(
    bool Found,
    string? Title,
    string? Artist,
    string? FileName,
    string? Lrc,
    string? Source = null,
    bool Synced = false,
    string? Yrc = null);