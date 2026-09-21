using System.Text.Json;

namespace xiaoxu_music_bridge.Lyrics;

internal static class QqQrcService
{
    public static async Task<string?> FetchAsync(HttpClient client, int songId, string songMid, int interval, CancellationToken ct)
    {
        if (songId <= 0 || string.IsNullOrWhiteSpace(songMid)) return null;
        var site = (Environment.GetEnvironmentVariable("XIAOXU_SITE_URL") ?? "https://xmoyue.com").TrimEnd('/');
        var url = $"{site}/api/lyrics/qq?songmid={Uri.EscapeDataString(songMid)}&songid={songId}";
        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return json.RootElement.TryGetProperty("qrc", out var qrc) ? qrc.GetString() : null;
    }
}
