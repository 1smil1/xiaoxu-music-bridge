namespace xiaoxu_music_bridge.Common;

public static class MediaModePolicy
{
    public static bool ShouldRecordGsmtcFailure(MediaMode mode) => mode == MediaMode.Auto;
    public static bool ShouldRestartBroker(string? errorCode) => errorCode == "gsmtc_timeout";

    public static string FinalRepairFailureMessage(string? errorCode, string? fallbackMessage) =>
        errorCode == "gsmtc_timeout"
            ? "GSMTC still timed out after RuntimeBroker and Windows Audio were restarted. Sign out or restart Windows; QQ Music may still expose no GSMTC session."
            : fallbackMessage ?? "GSMTC verification failed";

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
