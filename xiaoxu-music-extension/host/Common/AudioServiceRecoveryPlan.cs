namespace xiaoxu_music_bridge.Common;

public readonly record struct AudioServiceCommand(string Arguments, bool AllowFailure = false);

public static class AudioServiceRecoveryPlan
{
    public static IReadOnlyList<AudioServiceCommand> Commands { get; } = new[]
    {
        new AudioServiceCommand("stop AudioEndpointBuilder /y", AllowFailure: true),
        new AudioServiceCommand("start AudioEndpointBuilder"),
        new AudioServiceCommand("start Audiosrv"),
    };

    public static bool IsRecovered(string endpointBuilderState, string audioServiceState) =>
        string.Equals(endpointBuilderState, "RUNNING", StringComparison.OrdinalIgnoreCase)
        && string.Equals(audioServiceState, "RUNNING", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<AudioServiceCommand> CommandsForStates(string endpointBuilderState, string audioServiceState) =>
        string.Equals(endpointBuilderState, "RUNNING", StringComparison.OrdinalIgnoreCase)
        && string.Equals(audioServiceState, "STOPPED", StringComparison.OrdinalIgnoreCase)
            ? new[] { new AudioServiceCommand("start Audiosrv") }
            : Commands;
}
