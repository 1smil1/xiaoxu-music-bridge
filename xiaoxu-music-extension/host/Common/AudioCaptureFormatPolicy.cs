namespace xiaoxu_music_bridge.Common;

public static class AudioCaptureFormatPolicy
{
    public static bool IsSupported(bool isIeeeFloat, int bitsPerSample, int blockAlign, int channels) =>
        isIeeeFloat
        && bitsPerSample == 32
        && channels > 0
        && blockAlign == channels * sizeof(float);
}
