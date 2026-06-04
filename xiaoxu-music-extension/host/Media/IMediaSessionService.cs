namespace xiaoxu_music_bridge.Media;

public interface IMediaSessionService
{
    Task<MediaStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<ControlResult> SendCommandAsync(ControlCommand command, CancellationToken cancellationToken);

    Task<CoverImage?> GetCurrentCoverAsync(CancellationToken cancellationToken);
}
