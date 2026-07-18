using System.Globalization;

namespace xiaoxu_music_bridge.Common;

public sealed class BridgeEndpointSettings
{
    public const int DefaultPort = 17888;
    public const string PortEnvironmentVariable = "XIAOXU_BRIDGE_PORT";

    private BridgeEndpointSettings(int port) => Port = port;

    public int Port { get; }
    public Uri HealthUri => new($"http://localhost:{Port}/health");
    public string ListenerPrefix => $"http://localhost:{Port}/";
    public string ServerMutexName => Port == DefaultPort
        ? HostLaunchPolicy.ServerMutexName
        : $"{HostLaunchPolicy.ServerMutexName}-{Port}";

    public static BridgeEndpointSettings FromEnvironment() =>
        Resolve(Environment.GetEnvironmentVariable(PortEnvironmentVariable));

    public static BridgeEndpointSettings Resolve(string? configuredPort)
    {
        var port = int.TryParse(configuredPort, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 1024 and <= 65535
                ? parsed
                : DefaultPort;
        return new BridgeEndpointSettings(port);
    }
}
