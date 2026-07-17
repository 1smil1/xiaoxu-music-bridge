namespace xiaoxu_music_bridge.Common;

public static class BeatHttpResponsePolicy
{
    private const string SilentFrame = "{\"type\":\"beat\",\"bass\":0,\"pulse\":0,\"glow\":0,\"volume\":0,\"bands\":{\"sub\":0,\"bass\":0,\"lowMid\":0,\"mid\":0,\"highMid\":0,\"treble\":0,\"air\":0},\"features\":{\"rms\":0,\"loudness\":-120,\"centroid\":0,\"zcr\":0},\"onsets\":{\"spectralFlux\":0,\"bass\":0,\"mid\":0,\"treble\":0,\"flux\":0},\"rhythm\":{\"bpm\":0,\"bpmConfidence\":0,\"beatPhase\":0,\"isDownbeat\":false,\"beatCount\":0,\"tempoChanged\":false},\"state\":{\"isPlaying\":false,\"isSilence\":true,\"silenceDuration\":0},\"audioClock\":{\"sampleIndex\":0,\"sampleRate\":0,\"monotonicMs\":0,\"discontinuityId\":0},\"ts\":0}";

    public static string SelectJson(string? fullSnapshotJson) =>
        string.IsNullOrWhiteSpace(fullSnapshotJson) ? SilentFrame : fullSnapshotJson;
}
