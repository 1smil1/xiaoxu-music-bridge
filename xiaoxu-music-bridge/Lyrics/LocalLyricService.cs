using xiaoxu_music_bridge.Media;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace xiaoxu_music_bridge.Lyrics;

public sealed class LocalLyricService
{
    private readonly string _lyricsDirectory;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, LyricResponse> _cache = [];

    public LocalLyricService(IConfiguration configuration, IWebHostEnvironment environment, HttpClient httpClient)
    {
        _lyricsDirectory = configuration["Lyrics:Directory"]
            ?? Environment.GetEnvironmentVariable("XIAOXU_LYRICS_DIR")
            ?? GetDefaultLyricsDirectory(environment.ContentRootPath);
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

        var onlineLyrics = await SearchQqMusicAsync(status, cancellationToken);
        if (onlineLyrics is not null)
        {
            _cache[cacheKey] = onlineLyrics;
            return onlineLyrics;
        }

        onlineLyrics = await SearchNeteaseAsync(status, cancellationToken);
        if (onlineLyrics is not null)
        {
            _cache[cacheKey] = onlineLyrics;
            return onlineLyrics;
        }

        onlineLyrics = await SearchLrclibAsync(status, cancellationToken);
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

    private static string GetDefaultLyricsDirectory(string contentRootPath)
    {
        var root = new DirectoryInfo(contentRootPath);
        var parent = string.Equals(root.Name, "publish", StringComparison.OrdinalIgnoreCase) && root.Parent is not null
            ? root.Parent.FullName
            : root.FullName;

        return Path.Combine(parent, "lyrics");
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
            results = await _httpClient.GetFromJsonAsync<LrclibTrack[]>(url, cancellationToken);
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

        if (best is null)
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

    private async Task<LyricResponse?> SearchQqMusicAsync(MediaStatus status, CancellationToken cancellationToken)
    {
        var title = CleanTitle(status.Title ?? "");
        var artist = CleanArtist(status.Artist ?? "");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var searchUrl = $"https://api.ygking.top/api/search?keyword={Uri.EscapeDataString(query)}&type=song&num=8";

        QqSearchResponse? search;
        try
        {
            search = await _httpClient.GetFromJsonAsync<QqSearchResponse>(searchUrl, cancellationToken);
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
        if (best is null)
        {
            return null;
        }

        QqLyricResponse? lyric;
        try
        {
            var lyricUrl = $"https://api.ygking.top/api/lyric?mid={Uri.EscapeDataString(best.Mid)}";
            lyric = await _httpClient.GetFromJsonAsync<QqLyricResponse>(lyricUrl, cancellationToken);
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

    private static int Score(LrclibTrack track, string title, string artist, long durationMs)
    {
        var score = 0;
        var trackName = Normalize(track.TrackName);
        var artistName = Normalize(track.ArtistName);
        var wantedTitle = Normalize(title);
        var wantedArtist = Normalize(artist);

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

            search = await response.Content.ReadFromJsonAsync<NeteaseSearchResponse>(cancellationToken);
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
        if (best is null || best.Id <= 0)
        {
            return null;
        }

        var lyricUrl = $"https://music.163.com/api/song/lyric?id={best.Id}&lv=1&kv=1&tv=-1";
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

            lyric = await response.Content.ReadFromJsonAsync<NeteaseLyricResponse>(cancellationToken);
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

        return new LyricResponse(
            true,
            status.Title,
            status.Artist,
            $"{string.Join("/", best.Artists.Select(artist => artist.Name))} - {best.Name}",
            lrc,
            "netease",
            true);
    }

    private static int Score(NeteaseSong song, string title, string artist, long durationMs)
    {
        var score = 0;
        var songName = Normalize(song.Name);
        var artistName = Normalize(string.Join(" ", song.Artists.Select(item => item.Name)));
        var wantedTitle = Normalize(title);
        var wantedArtist = Normalize(artist);

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
        [property: JsonPropertyName("lrc")] NeteaseLyric? Lrc);

    private sealed record NeteaseLyric(
        [property: JsonPropertyName("lyric")] string? Lyric);
}
