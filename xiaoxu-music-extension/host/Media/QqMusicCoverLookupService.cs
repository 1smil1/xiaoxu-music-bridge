// QqMusicCoverLookupService — third-tier cover lookup for the Win32 fallback path.
//
// Why: GSMTC's `GetCurrentCoverAsync` is the only path that returns a real album-art
//      bitmap. When GSMTC's broker deadlocks (frequent on Windows 11), we skip GSMTC
//      and the Win32MediaService has no native cover source. This service queries
//      QQ Music's public search API for `albummid`, then downloads a JPG from the
//      public `y.gtimg.cn` CDN and returns it as a base64 CoverImage.
//
// Search scoring re-uses the algorithms in LocalLyricService (Normalize / IsTitleMatch
// / Score) so cover and lyric matches stay consistent. Duration signal is dropped
// because the Win32 fallback cannot report duration.
//
// Cache: in-memory LRU keyed by `Normalize(title)|Normalize(artist)`. Three states:
//   Found(bytes), NotFound, Error. Only Found/NotFound are cached; Error retries on
//   next call. Cap = 32 entries, TTL = 1h.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Media;

public sealed class QqMusicCoverLookupService
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly object _cacheLock = new();
    private const int CacheCapacity = 32;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private const int LookupTimeoutMs = 5000;

    private enum CacheState { Found, NotFound, Error }

    private sealed record CacheEntry(CacheState State, byte[]? Bytes, string ContentType, DateTime ExpiresAt);

    public QqMusicCoverLookupService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CoverImage?> GetCoverAsync(string? title, string? artist, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var cleanedTitle = CleanTitle(title);
        var cleanedArtist = CleanArtist(artist ?? "");
        var key = $"{Normalize(cleanedArtist)}|{Normalize(cleanedTitle)}";

        // Cache lookup (Found/NotFound only — Error is retried each call)
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow && entry.State != CacheState.Error)
            {
                return entry.State == CacheState.Found && entry.Bytes is not null
                    ? new CoverImage(entry.Bytes, entry.ContentType)
                    : null;
            }
        }

        CoverImage? result = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(LookupTimeoutMs);
            result = await SearchAndDownloadAsync(cleanedTitle, cleanedArtist, cts.Token);
        }
        catch
        {
            // Swallow — fall through to NotFound caching
        }

        // Cache the result
        lock (_cacheLock)
        {
            // Evict oldest entry if at capacity
            if (_cache.Count >= CacheCapacity)
            {
                var oldestKey = _cache
                    .OrderBy(kv => kv.Value.ExpiresAt)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
                if (oldestKey is not null) _cache.Remove(oldestKey);
            }

            var state = result is not null ? CacheState.Found : CacheState.NotFound;
            _cache[key] = new CacheEntry(
                State: state,
                Bytes: result?.Bytes,
                ContentType: result?.ContentType ?? "image/jpeg",
                ExpiresAt: DateTime.UtcNow + CacheTtl);
        }

        return result;
    }

    private async Task<CoverImage?> SearchAndDownloadAsync(string title, string artist, CancellationToken cancellationToken)
    {
        string? albummid = null;
        foreach (var query in GetSearchQueries(title, artist))
        {
            albummid = await SearchForAlbummidAsync(query, title, artist, cancellationToken);
            if (albummid is not null) break;
        }

        if (string.IsNullOrWhiteSpace(albummid)) return null;

        // Try 500x500 first, fallback to 300x300
        byte[]? bytes = await DownloadCoverAsync($"https://y.gtimg.cn/music/photo_new/T002R500x500M{albummid}_1.jpg", cancellationToken);
        if (bytes is null)
        {
            bytes = await DownloadCoverAsync($"https://y.gtimg.cn/music/photo_new/T002R300x300M{albummid}_1.jpg", cancellationToken);
        }

        return bytes is null ? null : new CoverImage(bytes, "image/jpeg");
    }

    private async Task<string?> SearchForAlbummidAsync(string query, string title, string artist, CancellationToken cancellationToken)
    {
        var searchUrl = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(query)}&format=json&p=1&n=10";

        QqDirectSearchResponse? search;
        try
        {
            using var request = CreateQqRequest(searchUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            search = JsonSerializer.Deserialize<QqDirectSearchResponse>(
                await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch
        {
            return null;
        }

        var songs = search?.Data?.Song?.List;
        if (songs is null || songs.Length == 0) return null;

        // Pick best-scoring song; require score >= 60 (lower than lyric threshold 80
        // because we lack duration signal in the fallback path).
        var best = songs
            .Where(s => !string.IsNullOrWhiteSpace(s.AlbumMid))
            .OrderByDescending(s => Score(s, title, artist))
            .FirstOrDefault();

        if (best is null || Score(best, title, artist) < 60) return null;
        return best.AlbumMid;
    }

    private async Task<byte[]?> DownloadCoverAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri("https://y.qq.com/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            // Guard against empty / non-image payloads (CDN sometimes returns HTML)
            if (bytes.Length < 16) return null;
            return bytes;
        }
        catch
        {
            return null;
        }
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

    private static int Score(QqDirectSong song, string title, string artist)
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

    // DTO subset of client_search_cp response
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
        [property: JsonPropertyName("albummid")] string AlbumMid);

    private sealed record QqSinger(
        [property: JsonPropertyName("name")] string Name);
}