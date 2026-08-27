using System.Text;

namespace xiaoxu_music_bridge.Common;

internal static class LogPaths
{
    private const long DefaultMaxBytes = 10 * 1024 * 1024;
    private static readonly object WriteLock = new();
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "xiaoxu-music-host");

    public static string DebugLog { get; } = Path.Combine(AppDataDir, "debug.log");
    public static string AudioLiveLog { get; } = Path.Combine(AppDataDir, "audio-live.log");

    static LogPaths()
    {
        try { Directory.CreateDirectory(AppDataDir); }
        catch { }
    }

    public static void SafeAppend(string path, string line, long maxBytes = DefaultMaxBytes)
    {
        lock (WriteLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                var bytes = Encoding.UTF8.GetBytes(line);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maxBytes)
                {
                    var backup = path + ".1";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(path, backup);
                }

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes);
            }
            catch
            {
                // Diagnostics must never terminate the native host.
            }
        }
    }
}
