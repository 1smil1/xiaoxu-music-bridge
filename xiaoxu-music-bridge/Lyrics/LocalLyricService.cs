using xiaoxu_music_bridge.Media;

namespace xiaoxu_music_bridge.Lyrics;

public sealed class LocalLyricService
{
    private readonly string _lyricsDirectory;

    public LocalLyricService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _lyricsDirectory = configuration["Lyrics:Directory"]
            ?? Environment.GetEnvironmentVariable("XIAOXU_LYRICS_DIR")
            ?? GetDefaultLyricsDirectory(environment.ContentRootPath);
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
            return new LyricResponse(true, status.Title, status.Artist, Path.GetFileName(path), lrc);
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
}
