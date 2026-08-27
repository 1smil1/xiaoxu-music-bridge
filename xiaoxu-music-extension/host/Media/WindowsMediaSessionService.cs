using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace xiaoxu_music_bridge.Media;

public sealed class WindowsMediaSessionService : IMediaSessionService
{
    private const string CoverUrl = "http://127.0.0.1:17888/cover/current";
    private const byte KeyEventKeyUp = 0x0002;
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPreviousTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;

    public async Task<MediaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSession? session = null;
        try
        {
            session = await GetTargetSessionAsync();
            if (session is null) return MediaStatus.NoMedia();

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var hasCover = mediaProperties.Thumbnail is not null;

            return new MediaStatus(
                Connected: true,
                Source: NormalizeSource(session.SourceAppUserModelId),
                Title: EmptyToNull(mediaProperties.Title),
                Artist: EmptyToNull(mediaProperties.Artist),
                Album: EmptyToNull(mediaProperties.AlbumTitle),
                CoverUrl: hasCover ? CoverUrl : null,
                IsPlaying: playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                PositionMs: ToMilliseconds(timeline.Position),
                DurationMs: ToMilliseconds(timeline.EndTime),
                UpdatedAt: DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
                $"[{DateTime.Now:HH:mm:ss}] GetStatusAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}\n");
            return MediaStatus.NoMedia();
        }
    }

    public async Task<ControlResult> SendCommandAsync(ControlCommand command, CancellationToken cancellationToken)
    {
        var session = await GetTargetSessionAsync();
        var controlled = session is not null && command switch
        {
            ControlCommand.PlayPause => await session.TryTogglePlayPauseAsync(),
            ControlCommand.Next => await session.TrySkipNextAsync(),
            ControlCommand.Previous => await session.TrySkipPreviousAsync(),
            _ => false
        };

        if (controlled)
        {
            return new ControlResult(true);
        }

        SendMediaKey(command);
        return new ControlResult(true);
    }

    public async Task<CoverImage?> GetCurrentCoverAsync(CancellationToken cancellationToken)
    {
        GlobalSystemMediaTransportControlsSession? session = null;
        try
        {
            session = await GetTargetSessionAsync();
            if (session is null) return null;

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            if (mediaProperties.Thumbnail is null) return null;

            await using var stream = (await mediaProperties.Thumbnail.OpenReadAsync()).AsStreamForRead();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);

            var bytes = memory.ToArray();
            return bytes.Length == 0 ? null : new CoverImage(bytes, "image/jpeg");
        }
        catch (Exception ex)
        {
            // COMException from OpenReadAsync() can corrupt WinRT state for the entire process.
            // Catch it here so the exception doesn't propagate and break subsequent calls.
            File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
                $"[{DateTime.Now:HH:mm:ss}] GetCurrentCoverAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}\n");
            return null;
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSession?> GetTargetSessionAsync()
    {
        // GlobalSystemMediaTransportControlsSessionManager.RequestAsync() can hang forever
        // after a COMException corrupts the WinRT state. Add retry logic with timeouts
        // so we can recover without requiring a Windows restart.
        const int maxRetries = 3;
        const int retryDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var managerTask = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
                var winner = await Task.WhenAny(managerTask, Task.Delay(TimeSpan.FromSeconds(3)));

                if (winner != managerTask)
                {
                    File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
                        $"[{DateTime.Now:HH:mm:ss}] GetTargetSessionAsync TIMEOUT (attempt {attempt}/{maxRetries})\n");

                    if (attempt < maxRetries)
                    {
                        await Task.Delay(retryDelayMs);
                        continue;
                    }

                    return null;
                }

                var manager = await managerTask;
                var sessions = manager.GetSessions();

                return sessions.FirstOrDefault(IsLikelyQqMusicSession)
                    ?? manager.GetCurrentSession()
                    ?? sessions.FirstOrDefault();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\Users\nuaa_xuzike\xiaoxu-debug.log",
                    $"[{DateTime.Now:HH:mm:ss}] GetTargetSessionAsync EXCEPTION (attempt {attempt}/{maxRetries}): {ex.GetType().Name}: {ex.Message}\n");

                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelayMs);
                    continue;
                }

                return null;
            }
        }

        return null;
    }

    private static bool IsLikelyQqMusicSession(GlobalSystemMediaTransportControlsSession session)
    {
        var source = session.SourceAppUserModelId;
        return source.Contains("qqmusic", StringComparison.OrdinalIgnoreCase)
            || source.Contains("tencent", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeSource(string sourceAppUserModelId)
    {
        return IsLikelyQqMusicSource(sourceAppUserModelId) ? "QQMusic" : EmptyToNull(sourceAppUserModelId);
    }

    private static bool IsLikelyQqMusicSource(string sourceAppUserModelId)
    {
        return sourceAppUserModelId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase)
            || sourceAppUserModelId.Contains("tencent", StringComparison.OrdinalIgnoreCase);
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long ToMilliseconds(TimeSpan value)
    {
        return value <= TimeSpan.Zero ? 0 : Convert.ToInt64(value.TotalMilliseconds);
    }

    private static void SendMediaKey(ControlCommand command)
    {
        var key = command switch
        {
            ControlCommand.PlayPause => VkMediaPlayPause,
            ControlCommand.Next => VkMediaNextTrack,
            ControlCommand.Previous => VkMediaPreviousTrack,
            _ => VkMediaPlayPause
        };

        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
