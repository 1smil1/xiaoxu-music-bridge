// ITunesCoverLookupService — final-tier cover lookup fallback (Tier 4).
//
// Why: QQ Music's public search + CDN is the primary cover source, but it misses
//      for some tracks (CDN 404, search miss, non-QQ sources). The Apple iTunes
//      Search API is a broad, no-auth, public catalog that covers most Chinese
//      pop artists (周杰伦, 五月天, 林俊杰, 田馥甄, ...). We use it as a last resort
//      when QQ returns nothing.
//
// Flow:
//   1. search:  https://itunes.apple.com/search?term=<title artist>&entity=song&country=CN&limit=10
//               → score results (Normalize / IsTitleMatch / Score, threshold 60)
//   2. lookup:  https://itunes.apple.com/lookup?id=<collectionId>&entity=song&country=CN
//               (kept for parity with the QQ two-step design; the search result already
//                carries artworkUrl100, so we upscale that directly)
//   3. artwork: artworkUrl100 "100x100bb.jpg" → replace with "600x600bb.jpg" → download
//
// Notes:
//   - iTunes requires the whole query be URL-encoded (Uri.EscapeDataString). Mixed
//     Chinese + punctuation (e.g. "倔強") 400s if not encoded properly.
//   - country=CN is required to surface China-region results.
//   - Scoring uses trackName (single-song title) + artistName.
//   - Cache: in-memory LRU keyed by Normalize(title)|Normalize(artist). Found/NotFound
//     cached, Error retried. Cap = 32, TTL = 1h. Same pattern as QqMusicCoverLookupService.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Media;

public sealed class ITunesCoverLookupService
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly object _cacheLock = new();
    private const int CacheCapacity = 32;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private const int LookupTimeoutMs = 5000;

    private enum CacheState { Found, NotFound, Error }

    private sealed record CacheEntry(CacheState State, byte[]? Bytes, string ContentType, DateTime ExpiresAt);

    public ITunesCoverLookupService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CoverImage?> GetCoverAsync(string? title, string? artist, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var cleanedTitle = CleanTitle(title);
        var cleanedArtist = CleanArtist(artist ?? "");
        var key = $"{Normalize(cleanedArtist)}|{Normalize(cleanedTitle)}";

        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] [iTunes] GetCoverAsync called title='{cleanedTitle}' artist='{cleanedArtist}'\n");

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow && entry.State != CacheState.Error)
            {
                var hit = entry.State == CacheState.Found && entry.Bytes is not null
                    ? new CoverImage(entry.Bytes, entry.ContentType)
                    : null;
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] [iTunes] cache hit state={entry.State} bytes={entry.Bytes?.Length ?? 0}\n");
                return hit;
            }
        }

        CoverImage? result = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(LookupTimeoutMs);
            result = await SearchAndDownloadAsync(cleanedTitle, cleanedArtist, cts.Token);
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [iTunes] SearchAndDownload EXCEPTION {ex.GetType().Name}: {ex.Message}\n");
        }

        LogPaths.SafeAppend(LogPaths.DebugLog,
            result is not null
                ? $"[{DateTime.Now:HH:mm:ss}] [iTunes] returning cover bytes={result.Bytes.Length}\n"
                : $"[{DateTime.Now:HH:mm:ss}] [iTunes] returning null (miss / network error)\n");

        lock (_cacheLock)
        {
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
        ITunesTrack? best = null;
        foreach (var query in GetSearchQueries(title, artist))
        {
            best = await SearchForTrackAsync(query, title, artist, cancellationToken);
            if (best is not null) break;
        }

        if (best is null || string.IsNullOrWhiteSpace(best.ArtworkUrl100))
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [iTunes] no track matched (score below threshold or no artwork)\n");
            return null;
        }

        // artworkUrl100 looks like ".../source/100x100bb.jpg". Upscale to 600x600.
        var hiResUrl = Regex.Replace(best.ArtworkUrl100, @"\d+x\d+bb", "600x600bb");
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] [iTunes] collectionId={best.CollectionId} downloading {hiResUrl}\n");

        byte[]? bytes = await DownloadCoverAsync(hiResUrl, cancellationToken);
        if (bytes is null)
        {
            // Fall back to the original 100x100 URL if the upscaled variant 404s.
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [iTunes] 600x600 miss, trying original {best.ArtworkUrl100}\n");
            bytes = await DownloadCoverAsync(best.ArtworkUrl100, cancellationToken);
        }

        return bytes is null ? null : new CoverImage(bytes, "image/jpeg");
    }

    private async Task<ITunesTrack?> SearchForTrackAsync(string query, string title, string artist, CancellationToken cancellationToken)
    {
        // Encode the whole term — iTunes 400s on raw mixed CJK + punctuation.
        var searchUrl = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&entity=song&country=CN&limit=10";

        ITunesSearchResponse? search;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [iTunes] search query='{query}' status={(int)response.StatusCode}\n");
            if (!response.IsSuccessStatusCode) return null;

            search = JsonSerializer.Deserialize<ITunesSearchResponse>(
                await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [iTunes] search EXCEPTION query='{query}' {ex.GetType().Name}: {ex.Message}\n");
            return null;
        }

        var tracks = search?.Results;
        if (tracks is null || tracks.Length == 0)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [iTunes] search query='{query}' returned 0 results\n");
            return null;
        }

        var best = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.ArtworkUrl100))
            .OrderByDescending(t => Score(t, title, artist))
            .FirstOrDefault();

        var bestScore = best is null ? -1000 : Score(best, title, artist);
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss}] [iTunes] query='{query}' results={tracks.Length} bestScore={bestScore} track='{best?.TrackName ?? "(none)"}' pass={(bestScore >= 60)}\n");

        if (best is null || bestScore < 60) return null;
        return best;
    }

    private async Task<byte[]?> DownloadCoverAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] [iTunes] download status={(int)response.StatusCode} url={url}\n");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length < 16)
            {
                LogPaths.SafeAppend(LogPaths.DebugLog,
                    $"[{DateTime.Now:HH:mm:ss}] [iTunes] download too small bytes={bytes.Length} url={url}\n");
                return null;
            }
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [iTunes] download OK status=200 bytes={bytes.Length}\n");
            return bytes;
        }
        catch (Exception ex)
        {
            LogPaths.SafeAppend(LogPaths.DebugLog,
                $"[{DateTime.Now:HH:mm:ss}] [iTunes] download EXCEPTION {ex.GetType().Name}: {ex.Message} url={url}\n");
            return null;
        }
    }

    private static IEnumerable<string> GetSearchQueries(string title, string artist)
    {
        if (!string.IsNullOrWhiteSpace(artist))
        {
            yield return $"{title} {artist}";
        }
        yield return title;
    }

    private static int Score(ITunesTrack track, string title, string artist)
    {
        var score = 0;
        var trackName = Normalize(track.TrackName ?? "");
        var artistName = Normalize(track.ArtistName ?? "");
        var wantedTitle = Normalize(title);
        var wantedArtist = Normalize(artist);

        if (!IsTitleMatch(trackName, wantedTitle)) return -1000;
        if (trackName == wantedTitle) score += 100;
        else if (trackName.Contains(wantedTitle) || wantedTitle.Contains(trackName)) score += 45;

        if (!string.IsNullOrWhiteSpace(wantedArtist))
        {
            if (artistName == wantedArtist) score += 70;
            else if (artistName.Contains(wantedArtist) || wantedArtist.Contains(artistName)) score += 35;
        }

        var rawTrack = track.TrackName ?? "";
        if (rawTrack.Contains("伴奏", StringComparison.OrdinalIgnoreCase)) score -= 100;
        if (rawTrack.Contains("Instrumental", StringComparison.OrdinalIgnoreCase)) score -= 100;
        if (rawTrack.Contains("Live", StringComparison.OrdinalIgnoreCase)) score -= 20;
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

    // DTO subset of the iTunes Search / Lookup response.
    private sealed record ITunesSearchResponse(
        [property: JsonPropertyName("resultCount")] int ResultCount,
        [property: JsonPropertyName("results")] ITunesTrack[] Results);

    private sealed record ITunesTrack(
        [property: JsonPropertyName("collectionId")] long CollectionId,
        [property: JsonPropertyName("trackName")] string? TrackName,
        [property: JsonPropertyName("artistName")] string? ArtistName,
        [property: JsonPropertyName("artworkUrl100")] string? ArtworkUrl100);
}
