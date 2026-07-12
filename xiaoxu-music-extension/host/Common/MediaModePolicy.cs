namespace xiaoxu_music_bridge.Common;

public static class MediaModePolicy
{
    public static bool ShouldTryGsmtc(MediaMode mode, bool breakerOpen) => mode switch
    {
        MediaMode.Win32 => false,
        MediaMode.Gsmtc => true,
        _ => !breakerOpen,
    };

    public static bool ShouldUseWin32(MediaMode mode, bool gsmtcSucceeded) => mode switch
    {
        MediaMode.Win32 => true,
        MediaMode.Gsmtc => false,
        _ => !gsmtcSucceeded,
    };
}
