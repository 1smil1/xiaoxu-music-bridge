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

    public static HostLaunchMode Resolve(IEnumerable<string> args)
    {
        var argList = args as IReadOnlyList<string> ?? args.ToList();
        if (argList.Any(arg => string.Equals(arg, "--server", StringComparison.OrdinalIgnoreCase)))
            return HostLaunchMode.Server;
        // Chrome always passes the extension origin (e.g. "chrome-extension://.../") as
        // args[0] when launching the native messaging host. Anything else — manual
        // double-click, shortcut, scheduled task, etc. — should land on the persistent
        // server path so the system tray / status window shows up. Without this, a
        // bare double-click of xiaoxu-music-host.exe would block forever on stdin.
        if (argList.Count > 0 && argList[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            return HostLaunchMode.NativeMessaging;
        return HostLaunchMode.Server;
    }

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
