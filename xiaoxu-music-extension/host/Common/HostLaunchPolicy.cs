namespace xiaoxu_music_bridge.Common;

public enum HostLaunchMode
{
    NativeMessaging,
    Server,
}

public static class HostLaunchPolicy
{
    public const string ServerMutexName = @"Local\xiaoxu-music-host-server";

    public static string ResolveServerMutexName(BridgeEndpointSettings endpoint) => endpoint.ServerMutexName;

    public static HostLaunchMode Resolve(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, "--server", StringComparison.OrdinalIgnoreCase))
            ? HostLaunchMode.Server
            : HostLaunchMode.NativeMessaging;

    public static string BuildServerArguments(int? waitForPid) =>
        waitForPid is > 0 ? $"--server --wait-for-pid {waitForPid.Value}" : "--server";

    public static int? GetWaitForPid(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], "--wait-for-pid", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[index + 1], out var pid)
                && pid > 0)
            {
                return pid;
            }
        }
        return null;
    }
}
