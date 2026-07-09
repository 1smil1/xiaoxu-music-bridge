using System;
using System.IO;
using System.Threading;

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

    // File-lock-safe append.
    //
    // When Chrome spawns a new host (old one died or was killed), both the
    // dying host and the new one may try to write to the same log file within
    // a few milliseconds. On Windows the second writer gets an IOException
    // ("being used by another process") which — if it bubbles up to Program.cs
    // — kills the host before it can serve any requests. The 5s polling loop
    // then sees host-unavailable 503s for the entire lifetime of the dying host.
    //
    // SafeAppend retries with FileShare.ReadWrite semantics (open explicitly
    // with shared access) and a small retry budget. After the budget it gives
    // up silently — logging must never crash the host.
    public static void SafeAppend(string path, string line)
    {
        const int maxAttempts = 8;
        const int baseDelayMs = 50;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Open with FileShare.ReadWrite so other processes can write
                // concurrently. Use Append mode to seek to end.
                using var fs = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
                return;
            }
            catch (IOException)
            {
                // Another process still holds the file. Brief backoff then retry.
                if (attempt == maxAttempts) return; // give up silently
                Thread.Sleep(baseDelayMs * attempt);
            }
            catch (UnauthorizedAccessException)
            {
                // Antivirus or perms issue — give up silently for this line.
                return;
            }
            catch
            {
                // Any other IO error — give up silently for this line.
                return;
            }
        }
    }
}