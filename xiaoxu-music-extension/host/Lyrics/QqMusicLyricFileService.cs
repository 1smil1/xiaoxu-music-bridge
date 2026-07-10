// QqMusicLyricFileService — Track A4 (v3.2.7):
// Reads QQ Music's on-disk lyric cache. Best-effort; provides the song's
// duration (last LRC time-tag) as a supplementary signal. Does NOT solve the
// lyric-sync problem because we still don't have real position unless the
// virtual clock (Track B) is in use.
//
// What it does:
//   1. Scan known QQ Music lyric directories for .lrc files
//   2. Match the current track by either:
//      - Exact filename match against a clean slug of "title-artist"
//      - First `[ti:]` / `[ar:]` tag inside the LRC body
//   3. Parse the last time tag in the file as the song duration (in ms)
//
// Why we still bother even though LocalLyricService covers lyric content:
//   - The .lrc on disk contains the canonical duration, which is needed
//     when the host runs in virtual-clock fallback mode (so the lyric
//     scroller knows when to stop)
//   - Sometimes QQ Music's lyric-on-disk has the verified-canonical
//     melody timestamps while the imported-from-LocalLyricService copy
//     might be from a different release
//
// Caching:
//   - Hot-path is per (title, artist) key with a 5 minute TTL
//   - Negative cache (no file found) is shorter (60s) to recover quickly
//     when the user changes tracks

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Lyrics;

public sealed record QqMusicLyricFileResult(
    bool Found,
    string? FileName,
    string? FilePath,
    long DurationMs,
    string? MatchedTitle,
    string? MatchedArtist,
    string? Preview);

public sealed class QqMusicLyricFileService
{
    // 3 well-known lyric directories. Order matters: Music app stores synced
    // lyrics here first; older "QQMusic" build uses Documents path.
    private static readonly string[] LyricDirs = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QQMusic", "Lyric"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQMusic", "Lyric"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QQMusic", "Lyric"),
    };

    // Time tag: [mm:ss.xx] or [mm:ss.xxx] - capture group 1 = minutes, 2 = seconds
    private static readonly Regex TimeTagRegex = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:]\d{1,3})?\]", RegexOptions.Compiled);
    private static readonly Regex MetaTitleRegex = new(@"\[ti:(.+?)\]", RegexOptions.Compiled);
    private static readonly Regex MetaArtistRegex = new(@"\[ar:(.+?)\]", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    private enum CacheKind { Hit, Miss }
    private sealed record CacheEntry(CacheKind Kind, QqMusicLyricFileResult? Result, DateTime ExpiresAt);

    public async Task<QqMusicLyricFileResult> FindAsync(
        string? title, string? artist, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new QqMusicLyricFileResult(false, null, null, 0, null, null, null);

        var key = NormalizeKey(title, artist);

        // Cache lookup
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTime.Now)
        {
            return cached.Result!;
        }

        // Run the disk scan off the calling thread
        var result = await Task.Run(() => ScanDirectories(title, artist, ct), ct);

        var kind = result.Found ? CacheKind.Hit : CacheKind.Miss;
        var ttl = result.Found ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(60);
        _cache[key] = new CacheEntry(kind, result, DateTime.Now + ttl);

        return result;
    }

    private QqMusicLyricFileResult ScanDirectories(string title, string? artist, CancellationToken ct)
    {
        var titleSlug = Slugify(title);
        var artistSlug = artist != null ? Slugify(artist) : null;

        foreach (var dir in LyricDirs)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.lrc", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) return NotFound();
                try
                {
                    var fi = new FileInfo(file);
                    // Cheap pre-filter by filename — many QQ Music lyric files are
                    // named "<title>-<artist>.lrc" with various slug conventions.
                    var fname = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrEmpty(titleSlug)
                        && (fname.Contains(titleSlug, StringComparison.OrdinalIgnoreCase)
                            || (artistSlug != null && fname.Contains(artistSlug, StringComparison.OrdinalIgnoreCase))))
                    {
                        return ParseIfMatches(file, title, artist);
                    }
                }
                catch { /* skip */ }
            }
        }
        return NotFound();
    }

    private QqMusicLyricFileResult ParseIfMatches(string path, string title, string? artist)
    {
        try
        {
            string body = File.ReadAllText(path);
            long durMs = ExtractDurationMs(body);
            var (metaTitle, metaArtist) = ExtractMeta(body);

            // Best effort title/artist match
            bool titleMatches = MatchScore(metaTitle ?? "", title) > 0.7
                || string.Equals(metaTitle, title, StringComparison.OrdinalIgnoreCase);
            bool artistMatches = artist == null || string.IsNullOrEmpty(metaArtist)
                || MatchScore(metaArtist, artist) > 0.7;

            if (titleMatches && artistMatches && durMs > 0)
            {
                return new QqMusicLyricFileResult(
                    Found: true,
                    FileName: Path.GetFileName(path),
                    FilePath: path,
                    DurationMs: durMs,
                    MatchedTitle: metaTitle ?? title,
                    MatchedArtist: metaArtist ?? artist,
                    Preview: body.Length > 240 ? body.Substring(0, 240) + "..." : body);
            }
            return NotFound();
        }
        catch
        {
            return NotFound();
        }
    }

    private static (string? title, string? artist) ExtractMeta(string body)
    {
        string? t = null, a = null;
        var tm = MetaTitleRegex.Match(body);
        if (tm.Success) t = tm.Groups[1].Value.Trim();
        var am = MetaArtistRegex.Match(body);
        if (am.Success) a = am.Groups[1].Value.Trim();
        return (t, a);
    }

    private static long ExtractDurationMs(string body)
    {
        long maxMs = 0;
        foreach (Match m in TimeTagRegex.Matches(body))
        {
            if (long.TryParse(m.Groups[1].Value, out var mm)
                && long.TryParse(m.Groups[2].Value, out var ss))
            {
                var ms = (mm * 60 + ss) * 1000;
                if (ms > maxMs) maxMs = ms;
            }
        }
        return maxMs;
    }

    private static double MatchScore(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var aslug = Slugify(a);
        var bslug = Slugify(b);
        if (aslug == bslug) return 1.0;
        if (aslug.Contains(bslug) || bslug.Contains(aslug)) return 0.9;
        return 0.0;
    }

    private static string Slugify(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.ToLowerInvariant();
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Replace(' ', '-');
    }

    private static string NormalizeKey(string title, string? artist)
    {
        return $"{Slugify(title)}|{Slugify(artist ?? "")}";
    }

    private static QqMusicLyricFileResult NotFound() =>
        new(false, null, null, 0, null, null, null);
}
