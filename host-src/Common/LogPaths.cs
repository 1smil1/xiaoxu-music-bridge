using System;
using System.IO;

namespace xiaoxu_music_bridge.Common;

// Centralized log paths so the host works for any Windows user.
// Old versions hardcoded C:\Users\nuaa_xuzike\... which broke for everyone else.
internal static class LogPaths
{
    // %LOCALAPPDATA%\xiaoxu-music-host\
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "xiaoxu-music-host");

    public static string DebugLog { get; } = Path.Combine(AppDataDir, "debug.log");
    public static string AudioLiveLog { get; } = Path.Combine(AppDataDir, "audio-live.log");

    static LogPaths()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
        }
        catch
        {
            // If we can't create the log dir (rare), fall back silently.
            // Logging should never crash the host.
        }
    }
}