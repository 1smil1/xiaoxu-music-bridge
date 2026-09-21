namespace xiaoxu_music_bridge.Lyrics;

/// <summary>
/// Lyric payload returned by the bridge HTTP /state/current endpoint.
///
/// v3.6.8: added <see cref="Yrc"/> so the dashboard can distinguish
/// Netease's word-level karaoke payload (per-character timing) from the
/// generic line-level <see cref="Lrc"/>.
///
/// v3.6.8 + amll-db: added <see cref="Ttml"/> for AMLL TTML DB lookups
/// (community-maintained Apple Music-style word-level lyric database at
/// amll-ttml-db.stevexmh.net). The TTML XML carries per-word timing
/// tags so the dashboard can render character-by-character highlighting
/// the way folia-major does. The dashboard parser picks Ttml first when
/// non-empty, then Yrc, then LRC line-level.
/// </summary>
public sealed record LyricResponse(
    bool Found,
    string? Title,
    string? Artist,
    string? FileName,
    string? Lrc,
    string? Source = null,
    bool Synced = false,
    string? Yrc = null,
    string? Ttml = null,
    string? Qrc = null);
