using System;

namespace xiaoxu_music_bridge.Common;

public static class AudioStreamOriginPolicy
{
    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://xiaoxu.xin",
        "https://xiaoxu.xin",
        "https://xiaoxu-blog-dun.vercel.app",
        "http://ppt2html.xin",
        "https://ppt2html.xin",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
        "http://localhost:3000",
        "http://127.0.0.1:3000",
    };

    public static bool IsAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        return AllowedOrigins.Contains(origin);
    }
}
