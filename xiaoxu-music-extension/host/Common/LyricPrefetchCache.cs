// LyricPrefetchCache — v3.2.10 hotfix: shared static lyric cache.
//
// The original v3.2.10 prefetch lived inside BridgeHttpServer (which serves
// the HTTP /state/current path used by Lively Wallpaper and non-extension
// browsers). It worked for those callers — but the actual dashboard
// (xiaoxu.xin) fetches /state/current via `injected.js` in the Chrome
// extension, which intercepts `fetch(127.0.0.1:17888)` and reroutes through
// Chrome Native Messaging to Program.cs's HandleGetLyrics. That path never
// touched BridgeHttpServer, so the prefetch cache was never queried and the
// 1-3s lyric-switch delay came right back.
//
// Fix: hoist the cache into a static here so BOTH the Native Messaging path
// (Program.cs HandleGetLyrics) and the HTTP path (BridgeHttpServer
// BuildStateResponseAsync) can read and write the same dictionary. One
// prefetch benefits both callers. Process-local only — each host.exe has its
// own cache (which is fine because each user's install has one host).

using System.Collections.Concurrent;
using System.Linq;
using xiaoxu_music_bridge.Lyrics;

namespace xiaoxu_music_bridge.Common;

public static class LyricPrefetchCache
{
    private static readonly ConcurrentDictionary<string, LyricResponse> _cache = new();

    // Cap at 256 songs (~10 hours of unique-track listening) so the cache
    // doesn't grow unbounded for users with huge libraries. When we overflow,
    // drop the oldest entries by insertion order (ConcurrentDictionary
    // preserves insertion order in Keys enumeration).
    private const int MaxEntries = 256;

    public static string Key(string? title, string? artist)
        => string.IsNullOrWhiteSpace(title) ? "" : $"{title.Trim()}|{(artist ?? "").Trim()}";

    public static bool TryGet(string? title, string? artist, out LyricResponse? lyrics)
    {
        var key = Key(title, artist);
        if (key.Length == 0) { lyrics = null; return false; }
        if (_cache.TryGetValue(key, out var v))
        {
            lyrics = v;
            return true;
        }
        lyrics = null;
        return false;
    }

    public static void Store(string? title, string? artist, LyricResponse lyrics)
    {
        var key = Key(title, artist);
        if (key.Length == 0) return;
        _cache[key] = lyrics;

        // Trim if over capacity. Drop the first ~32 (oldest by insertion order)
        // when we cross MaxEntries so we don't trim on every Store call.
        if (_cache.Count > MaxEntries)
        {
            var toDrop = _cache.Count - MaxEntries + 32;
            foreach (var k in _cache.Keys.Take(toDrop).ToList())
                _cache.TryRemove(k, out _);
        }
    }

    public static void Invalidate(string? title, string? artist)
    {
        var key = Key(title, artist);
        if (key.Length > 0) _cache.TryRemove(key, out _);
    }
}