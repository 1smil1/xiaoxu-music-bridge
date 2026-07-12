using System.Text.Json;

namespace xiaoxu_music_bridge.Common;

public enum MediaMode
{
    Auto,
    Win32,
    Gsmtc,
}

public sealed class MediaModeStore
{
    private readonly string _settingsPath;
    private readonly object _lock = new();

    public MediaModeStore(string settingsDirectory)
    {
        _settingsPath = Path.Combine(settingsDirectory, "media-mode.json");
    }

    public MediaMode Get()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_settingsPath)) return MediaMode.Auto;
                using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                return document.RootElement.TryGetProperty("mode", out var value)
                    && TryParse(value.GetString(), out var mode)
                    ? mode
                    : MediaMode.Auto;
            }
            catch
            {
                return MediaMode.Auto;
            }
        }
    }

    public void Set(MediaMode mode)
    {
        lock (_lock)
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { mode = ToWireValue(mode) }));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
    }

    public static bool TryParse(string? value, out MediaMode mode)
    {
        mode = value?.Trim().ToLowerInvariant() switch
        {
            "auto" => MediaMode.Auto,
            "win32" => MediaMode.Win32,
            "gsmtc" => MediaMode.Gsmtc,
            _ => (MediaMode)(-1),
        };
        return mode is MediaMode.Auto or MediaMode.Win32 or MediaMode.Gsmtc;
    }

    public static string ToWireValue(MediaMode mode) => mode switch
    {
        MediaMode.Auto => "auto",
        MediaMode.Win32 => "win32",
        MediaMode.Gsmtc => "gsmtc",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
