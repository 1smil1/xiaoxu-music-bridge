using xiaoxu_music_bridge.Common;

var failures = new List<string>();

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

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("PASS: 8 media mode tests");
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
