using xiaoxu_music_bridge.Common;
using xiaoxu_music_bridge.Media;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace xiaoxu_music_bridge.Lyrics;

public sealed class LocalLyricService
{
    private readonly string _lyricsDirectory;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, LyricResponse> _cache = [];

    public LocalLyricService(string? lyricsDirectory, HttpClient httpClient)
    {
        _lyricsDirectory = lyricsDirectory
            ?? Environment.GetEnvironmentVariable("XIAOXU_LYRICS_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "lyrics");
        _httpClient = httpClient;
    }

    public async Task<LyricResponse> GetCurrentLyricsAsync(MediaStatus status, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(status.Title))
        {
            return new LyricResponse(false, status.Title, status.Artist, null, null);
        }

        Directory.CreateDirectory(_lyricsDirectory);

        foreach (var path in GetCandidatePaths(status))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var lrc = await File.ReadAllTextAsync(path, cancellationToken);
            return new LyricResponse(true, status.Title, status.Artist, Path.GetFileName(path), lrc, "local", true);
        }

        var cacheKey = $"{Normalize(status.Artist ?? "")}|{Normalize(status.Title ?? "")}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        // v3.6.7: Netease is now the first online source — it's the only
        // provider that returns word-level YRC karaoke timing, and the
        // dashboard prefers YRC over LRC when both are present. QQ
        // (direct + ygking fallback) and LRClib are line-level only and
        // remain as fallback tiers below.
        var onlineLyrics = await SearchNeteaseAsync(status, cancellationToken);
        LogPaths.SafeAppend(LogPaths.DebugLog, $"[{DateTime.Now:HH:mm:ss}] Lyrics: Netease result={(onlineLyrics is not null ? "FOUND" : "null")}\n");
        if (onlineLyrics is not null)
        {
            _cache[cacheKey] = onlineLyrics;
            return onlineLyrics;
        }

        onlineLyrics = await SearchQqMusicAsync(status, cancellationToken);
        LogPaths.SafeAppend(LogPaths.DebugLog, $"[{DateTime.Now:HH:mm:ss}] Lyrics: QQ result={(onlineLyrics is not null ? "FOUND" : "null")}\n");
        if (onlineLyrics is not null)
        {
            _cache[cacheKey] = onlineLyrics;
            return onlineLyrics;
        }

        onlineLyrics = await SearchLrclibAsync(status, cancellationToken);
        LogPaths.SafeAppend(LogPaths.DebugLog, $"[{DateTime.Now:HH:mm:ss}] Lyrics: LRCLIB result={(onlineLyrics is not null ? "FOUND" : "null")}\n");
        if (onlineLyrics is not null)
        {
            _cache[cacheKey] = onlineLyrics;
            return onlineLyrics;
        }

        // v3.6.8: AMLL TTML DB as last-resort fallback. Word-level karaoke
        // timing sourced from a community-maintained mirror of Apple Music
        // lyric files (amll-ttml-db.stevexmh.net). Only useful for songs
        // that someone has uploaded — most tracks return 404. We try
        // Netease first (most coverage), then QQ.
        onlineLyrics = await SearchAmllDbAsync(status, cancellationToken);
        LogPaths.SafeAppend(LogPaths.DebugLog, $"[{DateTime.Now:HH:mm:ss}] Lyrics: AMLLDB result={(onlineLyrics is not null ? "FOUND" : "null")}\n");
        if (onlineLyrics is not null)
        {
            _cache[cacheKey] = onlineLyrics;
            return onlineLyrics;
        }

        return new LyricResponse(false, status.Title, status.Artist, null, null);
    }

    private IEnumerable<string> GetCandidatePaths(MediaStatus status)
    {
        var title = SafeFileName(status.Title ?? "");
        var artist = SafeFileName(status.Artist ?? "");

        if (!string.IsNullOrWhiteSpace(artist))
        {
            yield return Path.Combine(_lyricsDirectory, $"{artist} - {title}.lrc");
        }

        yield return Path.Combine(_lyricsDirectory, $"{title}.lrc");
    }

    private static string SafeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<LyricResponse?> SearchLrclibAsync(MediaStatus status, CancellationToken cancellationToken)
    {
        var title = CleanTitle(status.Title ?? "");
        var artist = CleanArtist(status.Artist ?? "");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var query = string.IsNullOrWhiteSpace(artist)
            ? $"track_name={Uri.EscapeDataString(title)}"
            : $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        var url = $"https://lrclib.net/api/search?{query}";

        LrclibTrack[]? results;
        try
        {
            results = JsonSerializer.Deserialize<LrclibTrack[]>(await _httpClient.GetStringAsync(url, cancellationToken));
        }
        catch
        {
            return null;
        }

        if (results is null || results.Length == 0)
        {
            return null;
        }

        var best = results
            .OrderByDescending(result => Score(result, title, artist, status.DurationMs))
            .FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.SyncedLyrics) || !string.IsNullOrWhiteSpace(result.PlainLyrics));

        if (best is null || Score(best, title, artist, status.DurationMs) < 70)
        {
            return null;
        }

        var synced = !string.IsNullOrWhiteSpace(best.SyncedLyrics);
        var lrc = synced ? best.SyncedLyrics : PlainLyricsToPseudoLrc(best.PlainLyrics);
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return null;
        }

        return new LyricResponse(
            true,
            status.Title,
            status.Artist,
            $"{best.ArtistName} - {best.TrackName}",
            lrc,
            synced ? "lrclib-synced" : "lrclib-plain",
            synced);
    }

    // v3.6.8: AMLL TTML DB word-level fallback.
    // The DB lives at https://amll-ttml-db.stevexmh.net and exposes TTML
    // files under /{platform}/{musicId}?format=ttml. Platforms: 'ncm'
    // (Netease), 'qq'. Songs must be uploaded by the community to exist
    // in the mirror — most tracks 404. We always try Netease first since
    // the DB has heavier Netease coverage, then QQ as a fallback. Search
    // by title+artist returns the music ID we need to construct the URL.
    private async Task<LyricResponse?> SearchAmllDbAsync(MediaStatus status, CancellationToken cancellationToken)
    {
        var title = CleanTitle(status.Title ?? "");
        var artist = CleanArtist(status.Artist ?? "");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var ncmId = await TryFindNeteaseIdAsync(title, artist, status.DurationMs, cancellationToken);
        if (ncmId.HasValue && await TryFetchAmllTtmlAsync("ncm", ncmId.Value.ToString(), status, cancellationToken) is { } ncmResp)
        {
            return ncmResp;
        }

        // QQ lookup is more expensive (needs direct API dance). Skip for now
        // — most AMLL DB entries are sourced from Netease anyway. Re-enable
        // when QQ coverage on the DB grows.
        return null;
    }

    private async Task<LyricResponse?> TryFetchAmllTtmlAsync(
        string platform,
        string musicId,
        MediaStatus status,
        CancellationToken cancellationToken)
    {
        var url = $"https://amll-ttml-db.stevexmh.net/{platform}/{Uri.EscapeDataString(musicId)}?format=ttml";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var ttml = await response.Content.ReadAsStringAsync(cancellationToken);
            // AMLL DB returns 200 even for misses sometimes with empty body,
            // so check the payload for the <tt> root.
            if (string.IsNullOrWhiteSpace(ttml) || !ttml.Contains("<tt", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return new LyricResponse(
                true,
                status.Title,
                status.Artist,
                $"amll-db/{platform}/{musicId}",
                null,
                $"amll-db-{platform}-ttml",
                true,
                null,
                ttml);
        }
        catch
        {
            return null;
        }
    }

    // Lightweight Netease ID lookup — calls the same web search the
    // SearchNeteaseAsync flow uses but only returns the first hit's ID.
    // Re-uses the existing NeteaseSearchResponse shape without parsing the
    // full song payload.
    private async Task<long?> TryFindNeteaseIdAsync(
        string title,
        string artist,
        long durationMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            var searchUrl = $"https://music.163.com/api/search/get/web?csrf_token=&s={Uri.EscapeDataString(query)}&type=1&offset=0&limit=5";
            using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.Referrer = new Uri("https://music.163.com/");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            NeteaseSearchResponse? search;
            try
            {
                search = JsonSerializer.Deserialize<NeteaseSearchResponse>(
                    await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch
            {
                return null;
            }
            var songs = search?.Result?.Songs;
            if (songs is null || songs.Length == 0)
            {
                return null;
            }
            // Title + duration match wins, otherwise first hit. Cheap check.
            var best = songs
                .OrderByDescending(song => Score(song, title, artist, durationMs))
                .FirstOrDefault();
            return best?.Id > 0 ? best.Id : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<LyricResponse?> SearchQqMusicAsync(MediaStatus status, CancellationToken cancellationToken)
    {
        var title = CleanTitle(status.Title ?? "");
        var artist = CleanArtist(status.Artist ?? "");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var directLyrics = await SearchQqMusicDirectAsync(status, title, artist, cancellationToken);
        if (directLyrics is not null)
        {
            return directLyrics;
        }

        return await SearchQqMusicYgkingAsync(status, title, artist, cancellationToken);
    }

    private async Task<LyricResponse?> SearchQqMusicDirectAsync(MediaStatus status, string title, string artist, CancellationToken cancellationToken)
    {
        foreach (var query in GetSearchQueries(title, artist))
        {
            var searchUrl = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(query)}&format=json&p=1&n=10";

            QqDirectSearchResponse? search;
            try
            {
                using var request = CreateQqRequest(searchUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                search = JsonSerializer.Deserialize<QqDirectSearchResponse>(await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch
            {
                continue;
            }

            var songs = search?.Data?.Song?.List;
            if (songs is null || songs.Length == 0)
            {
                continue;
            }

            var best = songs
                .OrderByDescending(song => Score(song, title, artist, status.DurationMs))
                .FirstOrDefault(song => !string.IsNullOrWhiteSpace(song.SongMid));
            if (best is null || Score(best, title, artist, status.DurationMs) < 80)
            {
                continue;
            }

            QqDirectLyricResponse? lyric;
            try
            {
                var lyricUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"
                    + $"?songmid={Uri.EscapeDataString(best.SongMid)}"
                    + "&format=json&nobase64=1&g_tk=5381&loginUin=0&hostUin=0"
                    + "&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq&needNewCode=0";
                using var request = CreateQqRequest(lyricUrl);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                lyric = JsonSerializer.Deserialize<QqDirectLyricResponse>(await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch
            {
                continue;
            }

            var lrc = lyric?.Lyric;
            if (string.IsNullOrWhiteSpace(lrc))
            {
                continue;
            }

            return new LyricResponse(
                true,
                status.Title,
                status.Artist,
                $"{string.Join("/", best.Singer.Select(singer => singer.Name))} - {best.SongName}",
                lrc,
                "qqmusic-direct",
                true);
        }

        return null;
    }

    private async Task<LyricResponse?> SearchQqMusicYgkingAsync(MediaStatus status, string title, string artist, CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var searchUrl = $"https://api.ygking.top/api/search?keyword={Uri.EscapeDataString(query)}&type=song&num=8";

        QqSearchResponse? search;
        try
        {
            search = JsonSerializer.Deserialize<QqSearchResponse>(await _httpClient.GetStringAsync(searchUrl, cancellationToken));
        }
        catch
        {
            return null;
        }

        var songs = search?.Data?.List;
        if (songs is null || songs.Length == 0)
        {
            return null;
        }

        var best = songs
            .OrderByDescending(song => Score(song, title, artist, status.DurationMs))
            .FirstOrDefault(song => !string.IsNullOrWhiteSpace(song.Mid));
        if (best is null || Score(best, title, artist, status.DurationMs) < 80)
        {
            return null;
        }

        QqLyricResponse? lyric;
        try
        {
            var lyricUrl = $"https://api.ygking.top/api/lyric?mid={Uri.EscapeDataString(best.Mid)}";
            lyric = JsonSerializer.Deserialize<QqLyricResponse>(await _httpClient.GetStringAsync(lyricUrl, cancellationToken));
        }
        catch
        {
            return null;
        }

        var lrc = lyric?.Data?.Lyric;
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return null;
        }

        return new LyricResponse(
            true,
            status.Title,
            status.Artist,
            $"{string.Join("/", best.Singer.Select(singer => singer.Name))} - {best.Title}",
            lrc,
            "qqmusic",
            true);
    }

    private static HttpRequestMessage CreateQqRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        return request;
    }

    private static IEnumerable<string> GetSearchQueries(string title, string artist)
    {
        if (!string.IsNullOrWhiteSpace(artist))
        {
            yield return $"{title} {artist}";
        }

        yield return title;
    }

    private static int Score(LrclibTrack track, string title, string artist, long durationMs)
    {
        var score = 0;
        var trackName = Normalize(track.TrackName);
        var artistName = Normalize(track.ArtistName);
        var wantedTitle = Normalize(title);
        var wantedArtist = Normalize(artist);

        if (!IsTitleMatch(trackName, wantedTitle)) return -1000;
        if (trackName == wantedTitle) score += 80;
        else if (trackName.Contains(wantedTitle) || wantedTitle.Contains(trackName)) score += 40;

        if (!string.IsNullOrWhiteSpace(wantedArtist))
        {
            if (artistName == wantedArtist) score += 60;
            else if (artistName.Contains(wantedArtist) || wantedArtist.Contains(artistName)) score += 25;
        }

        if (durationMs > 0 && track.Duration > 0)
        {
            var deltaSeconds = Math.Abs(track.Duration - durationMs / 1000.0);
            if (deltaSeconds <= 3) score += 35;
            else if (deltaSeconds <= 8) score += 15;
        }

        if (!string.IsNullOrWhiteSpace(track.SyncedLyrics)) score += 30;
        if (track.TrackName.Contains("伴奏", StringComparison.OrdinalIgnoreCase)) score -= 100;
        if (track.TrackName.Contains("翻自", StringComparison.OrdinalIgnoreCase)) score -= 30;
        return score;
    }

    private async Task<LyricResponse?> SearchNeteaseAsync(MediaStatus status, CancellationToken cancellationToken)
    {
        var title = CleanTitle(status.Title ?? "");
        var artist = CleanArtist(status.Artist ?? "");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var searchUrl = $"https://music.163.com/api/search/get/web?csrf_token=&s={Uri.EscapeDataString(query)}&type=1&offset=0&limit=8";

        NeteaseSearchResponse? search;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.Referrer = new Uri("https://music.163.com/");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            search = JsonSerializer.Deserialize<NeteaseSearchResponse>(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch
        {
            return null;
        }

        var songs = search?.Result?.Songs;
        if (songs is null || songs.Length == 0)
        {
            return null;
        }

        var best = songs
            .OrderByDescending(song => Score(song, title, artist, status.DurationMs))
            .FirstOrDefault();
        if (best is null || best.Id <= 0 || Score(best, title, artist, status.DurationMs) < 80)
        {
            return null;
        }

        // tv=1 (was -1) so Netease returns both `lrc` (line-level) and `yrc`
        // (word-level karaoke). v3.6.7: prefer yrc when available so the
        // dashboard can render per-character highlights.
        var lyricUrl = $"https://music.163.com/api/song/lyric?id={best.Id}&lv=1&kv=1&tv=1";
        NeteaseLyricResponse? lyric;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
            request.Headers.Referrer = new Uri("https://music.163.com/");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            lyric = JsonSerializer.Deserialize<NeteaseLyricResponse>(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch
        {
            return null;
        }

        var lrc = lyric?.Lrc?.Lyric;
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return null;
        }

        // YRC is the Netease word-level payload ([lineStart,lineDur]<char
        // offset,dur>char<...>). Empty / null falls back to line-level LRC
        // — dashboard parser picks the richer one automatically.
        var yrc = lyric?.Yrc?.Lyric;
        var source = !string.IsNullOrWhiteSpace(yrc) ? "netease-yrc" : "netease";

        return new LyricResponse(
            true,
            status.Title,
            status.Artist,
            $"{string.Join("/", best.Artists.Select(artist => artist.Name))} - {best.Name}",
            lrc,
            source,
            true,
            yrc);
    }

    private static int Score(NeteaseSong song, string title, string artist, long durationMs)
    {
        var score = 0;
        var songName = Normalize(song.Name);
        var artistName = Normalize(string.Join(" ", song.Artists.Select(item => item.Name)));
        var wantedTitle = Normalize(title);
        var wantedArtist = Normalize(artist);

        if (!IsTitleMatch(songName, wantedTitle)) return -1000;
        if (songName == wantedTitle) score += 100;
        else if (songName.Contains(wantedTitle) || wantedTitle.Contains(songName)) score += 45;

        if (!string.IsNullOrWhiteSpace(wantedArtist))
        {
            if (artistName == wantedArtist) score += 70;
            else if (artistName.Contains(wantedArtist) || wantedArtist.Contains(artistName)) score += 35;
        }

        if (durationMs > 0 && song.Duration > 0)
        {
            var deltaSeconds = Math.Abs(song.Duration / 1000.0 - durationMs / 1000.0);
            if (deltaSeconds <= 3) score += 35;
            else if (deltaSeconds <= 8) score += 15;
        }

        if (song.Name.Contains("伴奏", StringComparison.OrdinalIgnoreCase)) score -= 100;
        if (song.Name.Contains("翻自", StringComparison.OrdinalIgnoreCase)) score -= 30;
        return score;
    }

    private static int Score(QqSong song, string title, string artist, long durationMs)
    {
        var score = 0;
        var songName = Normalize(song.Title);
        var artistName = Normalize(string.Join(" ", song.Singer.Select(item => item.Name)));
        var wantedTitle = Normalize(title);
        var wantedArtist = Normalize(artist);

        if (!IsTitleMatch(songName, wantedTitle)) return -1000;
        if (songName == wantedTitle) score += 100;
        else if (songName.Contains(wantedTitle) || wantedTitle.Contains(songName)) score += 45;

        if (!string.IsNullOrWhiteSpace(wantedArtist))
        {
            if (artistName == wantedArtist) score += 70;
            else if (artistName.Contains(wantedArtist) || wantedArtist.Contains(artistName)) score += 35;
        }

        if (durationMs > 0 && song.Interval > 0)
        {
            var deltaSeconds = Math.Abs(song.Interval - durationMs / 1000.0);
            if (deltaSeconds <= 3) score += 35;
            else if (deltaSeconds <= 8) score += 15;
        }

        if (song.Title.Contains("伴奏", StringComparison.OrdinalIgnoreCase)) score -= 100;
        if (song.Title.Contains("翻自", StringComparison.OrdinalIgnoreCase)) score -= 30;
        return score;
    }

    private static int Score(QqDirectSong song, string title, string artist, long durationMs)
    {
        var score = 0;
        var songName = Normalize(song.SongName);
        var artistName = Normalize(string.Join(" ", song.Singer.Select(item => item.Name)));
        var wantedTitle = Normalize(title);
        var wantedArtist = Normalize(artist);

        if (!IsTitleMatch(songName, wantedTitle)) return -1000;
        if (songName == wantedTitle) score += 100;
        else if (songName.Contains(wantedTitle) || wantedTitle.Contains(songName)) score += 45;

        if (!string.IsNullOrWhiteSpace(wantedArtist))
        {
            if (artistName == wantedArtist) score += 70;
            else if (artistName.Contains(wantedArtist) || wantedArtist.Contains(artistName)) score += 35;
        }

        if (durationMs > 0 && song.Interval > 0)
        {
            var deltaSeconds = Math.Abs(song.Interval - durationMs / 1000.0);
            if (deltaSeconds <= 3) score += 35;
            else if (deltaSeconds <= 8) score += 15;
        }

        if (song.SongName.Contains("伴奏", StringComparison.OrdinalIgnoreCase)) score -= 100;
        if (song.SongName.Contains("翻自", StringComparison.OrdinalIgnoreCase)) score -= 30;
        return score;
    }

    private static string CleanTitle(string title)
    {
        var cleaned = Regex.Replace(title, @"\s*[-(（\[]?\s*(feat\.?|ft\.?|with)\s+.*$", "", RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private static string CleanArtist(string artist)
    {
        var firstArtist = Regex.Split(artist, @"[/,;、&]| feat\. | ft\. ", RegexOptions.IgnoreCase).FirstOrDefault();
        return (firstArtist ?? artist).Trim();
    }

    private static string Normalize(string value)
    {
        var cleaned = Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "");
        return cleaned.Trim();
    }

    private static bool IsTitleMatch(string candidateTitle, string wantedTitle)
    {
        return !string.IsNullOrWhiteSpace(candidateTitle)
            && !string.IsNullOrWhiteSpace(wantedTitle)
            && (candidateTitle == wantedTitle
                || candidateTitle.Contains(wantedTitle)
                || wantedTitle.Contains(candidateTitle));
    }

    private static string? PlainLyricsToPseudoLrc(string? plainLyrics)
    {
        if (string.IsNullOrWhiteSpace(plainLyrics))
        {
            return null;
        }

        return string.Join('\n', plainLyrics
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));
    }

    private sealed record LrclibTrack(
        [property: JsonPropertyName("trackName")] string TrackName,
        [property: JsonPropertyName("artistName")] string ArtistName,
        [property: JsonPropertyName("albumName")] string? AlbumName,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);

    private sealed record QqSearchResponse(
        [property: JsonPropertyName("data")] QqSearchData? Data);

    private sealed record QqSearchData(
        [property: JsonPropertyName("list")] QqSong[] List);

    private sealed record QqSong(
        [property: JsonPropertyName("mid")] string Mid,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("singer")] QqSinger[] Singer,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record QqSinger(
        [property: JsonPropertyName("name")] string Name);

    private sealed record QqLyricResponse(
        [property: JsonPropertyName("data")] QqLyricData? Data);

    private sealed record QqLyricData(
        [property: JsonPropertyName("lyric")] string? Lyric);

    private sealed record QqDirectSearchResponse(
        [property: JsonPropertyName("data")] QqDirectSearchData? Data);

    private sealed record QqDirectSearchData(
        [property: JsonPropertyName("song")] QqDirectSongResult? Song);

    private sealed record QqDirectSongResult(
        [property: JsonPropertyName("list")] QqDirectSong[] List);

    private sealed record QqDirectSong(
        [property: JsonPropertyName("songmid")] string SongMid,
        [property: JsonPropertyName("songname")] string SongName,
        [property: JsonPropertyName("singer")] QqSinger[] Singer,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record QqDirectLyricResponse(
        [property: JsonPropertyName("lyric")] string? Lyric);

    private sealed record NeteaseSearchResponse(
        [property: JsonPropertyName("result")] NeteaseSearchResult? Result);

    private sealed record NeteaseSearchResult(
        [property: JsonPropertyName("songs")] NeteaseSong[] Songs);

    private sealed record NeteaseSong(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("artists")] NeteaseArtist[] Artists,
        [property: JsonPropertyName("duration")] long Duration);

    private sealed record NeteaseArtist(
        [property: JsonPropertyName("name")] string Name);

    private sealed record NeteaseLyricResponse(
        [property: JsonPropertyName("lrc")] NeteaseLyric? Lrc,
        [property: JsonPropertyName("yrc")] NeteaseLyric? Yrc,
        [property: JsonPropertyName("tlyric")] NeteaseLyric? TLyric);

    private sealed record NeteaseLyric(
        [property: JsonPropertyName("lyric")] string? Lyric);
}
