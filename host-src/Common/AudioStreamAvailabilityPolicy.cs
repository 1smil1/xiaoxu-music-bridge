namespace xiaoxu_music_bridge.Common;

public static class AudioStreamAvailabilityPolicy
{
    public static int StatusCode(bool serviceExists, bool captureReady) =>
        serviceExists && captureReady ? 200 : 503;
}
