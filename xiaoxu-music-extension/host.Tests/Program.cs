using xiaoxu_music_bridge.Common;

var failures = new List<string>();

try { await PcmAudioBroadcasterTests.ReadAsyncEndsWhenStreamEnds(); }
catch (Exception ex) { failures.Add($"FAIL: PCM stream lifecycle: {ex.Message}"); }

Run("missing settings default to auto", () =>
{
    using var temp = new TempDirectory();
    var store = new MediaModeStore(temp.Path);
    Equal(MediaMode.Auto, store.Get());
});

Run("all modes persist across store instances", () =>
{
    using var temp = new TempDirectory();
    foreach (var mode in new[] { MediaMode.Auto, MediaMode.Win32, MediaMode.Gsmtc })
    {
        new MediaModeStore(temp.Path).Set(mode);
        Equal(mode, new MediaModeStore(temp.Path).Get());
    }
});

Run("invalid persisted mode falls back to auto", () =>
{
    using var temp = new TempDirectory();
    Directory.CreateDirectory(temp.Path);
    File.WriteAllText(System.IO.Path.Combine(temp.Path, "media-mode.json"), "{\"mode\":\"invalid\"}");
    Equal(MediaMode.Auto, new MediaModeStore(temp.Path).Get());
});

Run("mode parser rejects unknown values", () =>
{
    Equal(false, MediaModeStore.TryParse("fallback", out _));
    Equal(true, MediaModeStore.TryParse("GSMTC", out var mode));
    Equal(MediaMode.Gsmtc, mode);
});

Run("win32 never tries GSMTC", () =>
{
    Equal(false, MediaModePolicy.ShouldTryGsmtc(MediaMode.Win32, breakerOpen: false));
    Equal(true, MediaModePolicy.ShouldUseWin32(MediaMode.Win32, gsmtcSucceeded: false));
});

Run("forced GSMTC ignores breaker and never falls back", () =>
{
    Equal(true, MediaModePolicy.ShouldTryGsmtc(MediaMode.Gsmtc, breakerOpen: true));
    Equal(false, MediaModePolicy.ShouldUseWin32(MediaMode.Gsmtc, gsmtcSucceeded: false));
});

Run("auto respects breaker and falls back", () =>
{
    Equal(false, MediaModePolicy.ShouldTryGsmtc(MediaMode.Auto, breakerOpen: true));
    Equal(true, MediaModePolicy.ShouldUseWin32(MediaMode.Auto, gsmtcSucceeded: false));
    Equal(false, MediaModePolicy.ShouldUseWin32(MediaMode.Auto, gsmtcSucceeded: true));
});

Run("GSMTC repair succeeds only with usable current media", () =>
{
    Equal(false, new GsmtcProbeResult(true, null, null, null, 0, 0, null, null).IsSuccess);
    Equal(false, new GsmtcProbeResult(false, null, null, null, 0, 0, "gsmtc_timeout", "timeout").IsSuccess);
    Equal(true, new GsmtcProbeResult(true, "QQMusic", "Song", "Artist", 1000, 2000, null, null).IsSuccess);
});

Run("native messaging EOF exits the host", () =>
{
    Equal(false, NativeHostLifetime.ShouldContinueAfterInputClosed());
});

Run("host launch mode distinguishes persistent server from native messaging", () =>
{
    Equal(HostLaunchMode.Server, HostLaunchPolicy.Resolve(new[] { "--server" }));
    Equal(HostLaunchMode.NativeMessaging, HostLaunchPolicy.Resolve(new[] { "chrome-extension://fixed-id/" }));
});

Run("server replacement waits for the previous process", () =>
{
    Equal("--server --wait-for-pid 4321", HostLaunchPolicy.BuildServerArguments(4321));
    Equal("--server", HostLaunchPolicy.BuildServerArguments(null));
});

Run("bridge endpoint defaults for missing and invalid ports", () =>
{
    foreach (var value in new string?[] { null, "", "invalid", "1023", "65536", "-1" })
        Equal(BridgeEndpointSettings.DefaultPort, BridgeEndpointSettings.Resolve(value).Port);
});

Run("bridge endpoint accepts a valid custom port", () =>
{
    var settings = BridgeEndpointSettings.Resolve("24567");
    Equal(24567, settings.Port);
    Equal("http://localhost:24567/health", settings.HealthUri.AbsoluteUri);
    Equal("http://localhost:24567/", settings.ListenerPrefix);
});

Run("default bridge mutex remains compatible", () =>
{
    var settings = BridgeEndpointSettings.Resolve(null);
    Equal(@"Local\xiaoxu-music-host-server", HostLaunchPolicy.ResolveServerMutexName(settings));
});

Run("custom bridge mutex is port specific", () =>
{
    var first = HostLaunchPolicy.ResolveServerMutexName(BridgeEndpointSettings.Resolve("24567"));
    var second = HostLaunchPolicy.ResolveServerMutexName(BridgeEndpointSettings.Resolve("24568"));
    Equal(false, first == HostLaunchPolicy.ServerMutexName);
    Equal(false, first == second);
});

Run("beat HTTP response preserves the complete v3 audio frame", () =>
{
    const string frame = "{\"type\":\"beat\",\"bass\":0.2,\"bands\":{\"sub\":0.1},\"features\":{\"rms\":0.02},\"onsets\":{},\"rhythm\":{},\"state\":{},\"ts\":123}";
    Equal(frame, BeatHttpResponsePolicy.SelectJson(frame));
});

Run("beat HTTP response has a compatible silent frame before capture starts", () =>
{
    var json = BeatHttpResponsePolicy.SelectJson(null);
    Equal(true, json.Contains("\"bands\""));
    Equal(true, json.Contains("\"state\""));
    Equal(true, json.Contains("\"ts\""));
});

Run("debug log rotates when the next write exceeds the size limit", () =>
{
    using var temp = new TempDirectory();
    Directory.CreateDirectory(temp.Path);
    var path = System.IO.Path.Combine(temp.Path, "debug.log");
    File.WriteAllText(path, "12345678");

    LogPaths.SafeAppend(path, "abc", maxBytes: 10);

    Equal("abc", File.ReadAllText(path));
    Equal("12345678", File.ReadAllText(path + ".1"));
});

Run("debug log stays under current local application data", () =>
{
    var expected = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "xiaoxu-music-host",
        "debug.log");
    Equal(Path.GetFullPath(expected), Path.GetFullPath(LogPaths.DebugLog));
});

Run("cover identity is song based and independent of media mode", () =>
{
    Equal("underwater|权恩妃 (권은비)", CoverIdentity.Create("  Underwater ", "权恩妃  (권은비)"));
});

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("PASS: 20 host policy tests + PCM stream tests");
return 0;

void Run(string name, Action test)
{
    try { test(); }
    catch (Exception ex) { failures.Add($"FAIL: {name}: {ex.Message}"); }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected {expected}, got {actual}");
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xiaoxu-mode-test-{Guid.NewGuid():N}");
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
