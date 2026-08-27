namespace xiaoxu_music_bridge.Common;

internal static class CoverIdentity
{
    public static string Create(string? title, string? artist)
    {
        return $"{Normalize(title)}|{Normalize(artist)}";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }
}
