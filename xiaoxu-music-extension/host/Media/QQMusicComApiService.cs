// QQMusicComApiService — primary position source for the Win32 fallback path.
//
// v3.2.7: GSMTC permanently broken (WinRT projection DLL missing from single-file
// publish — see GsmtcHealthTracker.IsPermanentlyBroken). UIA doesn't expose a
// Slider/ProgressBar in QQ Music's CEF UI tree. New: QQ Music ships a hidden
// COM API window called `csQQMusicComApiWnd2017` (title `QQMusic_COM_WND_<UUID>`)
// that accepts WM_COPYDATA with `player/playerstatus` action and replies via a
// registered window message back to the caller's HWND.
//
// v3.2.7 HOTFIX — SendMessage deadlock fix:
//
//   First implementation called SendMessage(W) on the dedicated STA thread that
//   also pumped the reply WndProc. That deadlocks:
//     1. STA thread calls SendMessage(QQMusic, WM_COPYDATA, ourHwnd, &cds)
//     2. QQ Music's WndProc runs, then replies via SendMessage(ourHwnd, replyMsgId, ...)
//     3. That SendMessage from QQ Music blocks until OUR WndProc returns
//     4. Our WndProc can only run when our STA pump calls DispatchMessageW
//     5. But our STA thread is blocked in step 1's SendMessage → pump not running
//     6. → DEADLOCK. Reply never arrives. 1s timeout always triggers. positionMs stays 0.
//
//   New design:
//     - STA thread does ONE thing only: pump GetMessageW/DispatchMessageW forever.
//     - Sender is a worker thread pool task that calls SendMessageTimeout
//       (500ms timeout). QQ Music's WndProc runs on QQ Music's thread, replies
//       via SendMessage(ourHwnd, ...) which queues a sent-message on our
//       STA thread's queue, our DispatchMessageW runs our WndProc, WndProc
//       sets TCS, returns. No deadlock.
//     - Caller awaits the TCS via Task.WhenAny with a 1s timeout.
//
// v3.2.7 HOTFIX — caching:
//   Dashboard polls /state/current at ~1Hz; sending a fresh WM_COPYDATA on
//   every poll hammers QQ Music (which is fine, but unnecessary). Cache the
//   parsed status by (title|artist) for 250ms; reuse within that window. On
//   song change the cache key flips → fresh request fires immediately.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Media;

public sealed class QQMusicComApiService : IDisposable
{
    // Candidate window class names, newest first. New versions can be added.
    private static readonly string[] WindowClasses = new[]
    {
        "csQQMusicComApiWnd2017",
        "csQQMusicComApiWnd2020",
        "csQQMusicComApiWnd2023",
        "QQMusic_COM_WND",
    };

    private const int WM_COPYDATA = 0x004A;
    private const uint SMTO_NORMAL = 0x0000;
    private const int RequestTimeoutMs = 500;
    private const int OverallTimeoutMs = 1000;
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromMilliseconds(250);

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private IntPtr _comApiHwnd;
    private IntPtr _ourReplyHwnd;
    private string _ourReplyClassName = "";
    private uint _replyMsgId;
    private WndProcDelegate? _wndProcDelegate;
    private readonly ConcurrentDictionary<uint, PendingRequest> _pending = new();
    private int _nextReqId;

    // v3.2.7 HOTFIX: simple cache by (title|artist). On a song switch the
    // cache key changes and we fire a fresh request immediately.
    private readonly object _statusCacheLock = new();
    private string _statusCacheKey = "";
    private MediaStatus? _statusCacheValue;
    private DateTime _statusCacheAt;
    private DateTime _lastWindowNotFoundLogAt = DateTime.MinValue;

    // v3.2.7 HOTFIX: STA thread is now a PURE message pump. No action queue,
    // no SendMessage calls on this thread. This breaks the deadlock.
    private Thread? _staThread;
    private volatile bool _staRunning;

    private const string PlayerStatusXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<QQMusic><Action>player/playerstatus</Action></QQMusic>";

    public QQMusicComApiService()
    {
        _staThread = new Thread(StaThreadMain)
        {
            Name = "QQMusicComApi-Pump",
            IsBackground = true,
        };
        // STA must be set BEFORE Start (see Win32MediaService v3.2.5 lesson).
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void StaThreadMain()
    {
        _staRunning = true;

        // Register a globally-unique reply message id. QQ Music's WndProc (per
        // community reverse-engineering) sends its reply with this same id.
        _replyMsgId = RegisterWindowMessageW("XiaoxuMusicBridge_ComApi_Reply_v1");

        _wndProcDelegate = WndProc;
        _ourReplyClassName = "XiaoxuMusicBridge_ComApiReply_" + Guid.NewGuid().ToString("N");

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = GetModuleHandleW(null),
            lpszClassName = _ourReplyClassName,
        };
        var reg = RegisterClassExW(ref wc);
        if (reg == 0)
        {
            Log($"[ComApi] RegisterClassExW failed err={Marshal.GetLastWin32Error()}");
        }

        _ourReplyHwnd = CreateWindowExW(
            0, _ourReplyClassName, "XiaoxuMusicBridge-ComApiReply",
            0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_ourReplyHwnd == IntPtr.Zero)
        {
            Log($"[ComApi] CreateWindowExW failed err={Marshal.GetLastWin32Error()}");
        }

        // Pure message pump. This is the ONLY thing this thread does — that's
        // the deadlock fix.
        while (_staRunning)
        {
            IntPtr got = GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (got == 0 || got == -1) break;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _replyMsgId && lParam != IntPtr.Zero)
        {
            // QQ Music replies with WM_COPYDATA in the registered message. The
            // COPYDATASTRUCT lives only during this DispatchMessageW call, so
            // we must decode and stash the bytes immediately.
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            if (cds.cbData > 0 && cds.lpData != IntPtr.Zero)
            {
                try
                {
                    var bytes = new byte[cds.cbData];
                    Marshal.Copy(cds.lpData, bytes, 0, cds.cbData);
                    var responseXml = Encoding.UTF8.GetString(bytes);
                    var reqId = unchecked((uint)cds.dwData.ToInt64());
                    Log($"[ComApi] RAW response len={responseXml.Length} reqId={reqId}: {TruncateForLog(responseXml, 240)}");
                    if (_pending.TryRemove(reqId, out var pending))
                    {
                        pending.ResponseTcs.TrySetResult(responseXml);
                    }
                    else
                    {
                        Log($"[ComApi] reply for unknown/timed-out reqId={reqId}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ComApi] WndProc decode failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Ask QQ Music for current player status. Returns null if the COM API
    /// window can't be found, the request times out, or the response can't
    /// be parsed. Safe to call from any thread.
    /// </summary>
    public Task<MediaStatus?> GetPlayerStatusAsync(string? title, string? artist, CancellationToken ct)
    {
        // v3.2.7 HOTFIX: cache by (title|artist). Dashboard polls ~1Hz so on
        // most polls we return the cached value without touching QQ Music.
        var cacheKey = $"{title ?? ""}|{artist ?? ""}";
        lock (_statusCacheLock)
        {
            if (_statusCacheKey == cacheKey
                && _statusCacheValue != null
                && DateTime.UtcNow - _statusCacheAt < StatusCacheTtl)
            {
                return Task.FromResult<MediaStatus?>(_statusCacheValue);
            }
        }

        var comApiHwnd = EnsureComApiHwnd();
        if (comApiHwnd == IntPtr.Zero) return Task.FromResult<MediaStatus?>(null);
        if (_ourReplyHwnd == IntPtr.Zero)
        {
            Log("[ComApi] reply window not created — STA init failed");
            return Task.FromResult<MediaStatus?>(null);
        }

        var reqId = unchecked((uint)Interlocked.Increment(ref _nextReqId));
        var responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = new PendingRequest(responseTcs, Environment.TickCount);

        // Send WM_COPYDATA from a worker thread pool task, NOT the STA pump.
        // The pump is blocked in GetMessageW waiting for replies; if we called
        // SendMessage from it, we'd deadlock (see file-header comment).
        var payloadBytes = Encoding.UTF8.GetBytes(PlayerStatusXml);
        Task.Run(() => SendCopyDataOnWorker(comApiHwnd, reqId, payloadBytes));

        return AwaitWithTimeoutAsync(responseTcs.Task, TimeSpan.FromMilliseconds(OverallTimeoutMs), ct)
            .ContinueWith(t =>
            {
                // Always remove from pending dict + free resources (no pinned
                // payload to free in this design — we kept it inside the worker).
                _pending.TryRemove(reqId, out _);

                if (t.IsFaulted)
                {
                    var ex = t.Exception?.GetBaseException();
                    Log($"[ComApi] reqId={reqId} fault: {ex?.GetType().Name}: {ex?.Message}");
                    return null;
                }
                if (!t.IsCompletedSuccessfully)
                {
                    // Timeout — already logged in AwaitWithTimeoutAsync
                    return null;
                }

                MediaStatus? parsed;
                try { parsed = ParsePlayerStatusXml(t.Result); }
                catch (Exception ex)
                {
                    Log($"[ComApi] reqId={reqId} parse failed: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
                if (parsed == null) return null;

                // v3.2.7 HOTFIX: stick the result into the per-song cache so
                // the next poll within 250ms returns instantly.
                lock (_statusCacheLock)
                {
                    _statusCacheKey = cacheKey;
                    _statusCacheValue = parsed;
                    _statusCacheAt = DateTime.UtcNow;
                }
                return (MediaStatus?)parsed;
            });
    }

    private IntPtr EnsureComApiHwnd()
    {
        // FindWindowW is thread-safe; call from any thread.
        if (_comApiHwnd != IntPtr.Zero && IsWindow(_comApiHwnd)) return _comApiHwnd;
        _comApiHwnd = IntPtr.Zero;
        foreach (var cls in WindowClasses)
        {
            var h = FindWindowW(cls, null);
            if (h != IntPtr.Zero)
            {
                _comApiHwnd = h;
                Log($"[ComApi] found window class={cls} hwnd={h.ToInt64()}");
                break;
            }
        }
        if (_comApiHwnd == IntPtr.Zero)
        {
            // Throttle: only log once per 30s when window genuinely isn't there.
            if ((DateTime.UtcNow - _lastWindowNotFoundLogAt).TotalSeconds > 30)
            {
                Log($"[ComApi] COM API window not found (tried: {string.Join(", ", WindowClasses)})");
                _lastWindowNotFoundLogAt = DateTime.UtcNow;
            }
            return IntPtr.Zero;
        }
        return _comApiHwnd;
    }

    private void SendCopyDataOnWorker(IntPtr hwnd, uint reqId, byte[] payload)
    {
        // Pin the payload so GC can't move it while the kernel reads it.
        var handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
        var cdsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<COPYDATASTRUCT>());
        try
        {
            var cds = new COPYDATASTRUCT
            {
                dwData = (IntPtr)reqId,
                cbData = payload.Length,
                lpData = handle.AddrOfPinnedObject(),
            };
            Marshal.StructureToPtr(cds, cdsPtr, false);

            IntPtr wndProcResult;
            // SendMessageTimeout returns 0 on timeout (no WndProc call) or
            // non-zero on success. The reply comes via our HWND's registered
            // window message (handled by WndProc on the STA pump thread).
            var sendResult = SendMessageTimeout(
                hwnd, WM_COPYDATA, _ourReplyHwnd, cdsPtr,
                SMTO_NORMAL, RequestTimeoutMs, out wndProcResult);

            if (sendResult == IntPtr.Zero)
            {
                // Timed out — QQ Music didn't process within 500ms. Don't
                // fail the TCS yet; reply can still come later (until the
                // caller's 1s overall timeout). Just log.
                Log($"[ComApi] reqId={reqId} SendMessageTimeout TIMEOUT after {RequestTimeoutMs}ms (QQ Music slow or window hung)");
            }
            else
            {
                Log($"[ComApi] reqId={reqId} SendMessageTimeout OK wndProcResult={wndProcResult.ToInt64()}");
            }
        }
        catch (Exception ex)
        {
            Log($"[ComApi] reqId={reqId} SendMessageTimeout THREW: {ex.GetType().Name}: {ex.Message}");
            if (_pending.TryRemove(reqId, out var pending))
            {
                pending.ResponseTcs.TrySetException(ex);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(cdsPtr);
            try { handle.Free(); } catch { }
        }
    }

    private static async Task<string> AwaitWithTimeoutAsync(Task<string> task, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(timeout, cts.Token);
        var winner = await Task.WhenAny(task, delayTask);
        if (winner == task)
        {
            cts.Cancel();
            return await task; // propagate exceptions / result
        }
        throw new TimeoutException($"QQMusicComApi request timed out after {timeout.TotalMilliseconds:F0}ms");
    }

    private static MediaStatus? ParsePlayerStatusXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return ParsePlayerStatusFromXml(doc);
        }
        catch (XmlException)
        {
            return TryParsePlayerStatusJson(xml);
        }
    }

    private static MediaStatus? ParsePlayerStatusFromXml(XmlDocument doc)
    {
        string? title = SelectText(doc, "songname", "SongName", "title");
        string? artist = SelectText(doc, "singer", "Singer", "artist");
        long posMs = SelectLong(doc, "playtime", "PlayTime", "position", "positionMs");
        long durMs = SelectLong(doc, "totaltime", "TotalTime", "duration", "durationMs");
        int stateCode = SelectInt(doc, "state", "playstatus", "playState", "status");
        bool isPlaying = InterpretIsPlaying(stateCode);

        if (title == null && posMs == 0 && durMs == 0) return null;
        return new MediaStatus(
            Connected: true,
            Source: "QQMusic",
            Title: title,
            Artist: artist,
            Album: null,
            CoverUrl: null,
            IsPlaying: isPlaying,
            PositionMs: posMs,
            DurationMs: durMs,
            UpdatedAt: DateTimeOffset.Now);
    }

    private static MediaStatus? TryParsePlayerStatusJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var dataEl) ? dataEl : root;

            string? title = TryString(data, "songname", "SongName", "title", "name");
            string? artist = TryString(data, "singer", "Singer", "artist");
            long posMs = TryLong(data, "playtime", "PlayTime", "position", "positionMs");
            long durMs = TryLong(data, "totaltime", "TotalTime", "duration", "durationMs");
            int stateCode = TryInt(data, "state", "playstatus", "playState", "status");
            bool isPlaying = InterpretIsPlaying(stateCode);

            if (title == null && posMs == 0 && durMs == 0) return null;
            return new MediaStatus(
                Connected: true,
                Source: "QQMusic",
                Title: title,
                Artist: artist,
                Album: null,
                CoverUrl: null,
                IsPlaying: isPlaying,
                PositionMs: posMs,
                DurationMs: durMs,
                UpdatedAt: DateTimeOffset.Now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // QQ Music state code is undocumented. Default to {1,2}=playing, others=stopped.
    // First raw response in debug.log will confirm — adjust if wrong.
    private static bool InterpretIsPlaying(int stateCode)
    {
        if (stateCode == 1 || stateCode == 2) return true;
        if (stateCode == 0 || stateCode == 3) return false;
        return false;
    }

    private static string? SelectText(XmlDocument doc, params string[] names)
    {
        foreach (var n in names)
        {
            var node = doc.SelectSingleNode("//" + n);
            if (node != null && !string.IsNullOrWhiteSpace(node.InnerText))
                return node.InnerText.Trim();
        }
        return null;
    }

    private static long SelectLong(XmlDocument doc, params string[] names)
    {
        foreach (var n in names)
        {
            var node = doc.SelectSingleNode("//" + n);
            if (node != null && long.TryParse(node.InnerText.Trim(), out var v)) return v;
        }
        return 0;
    }

    private static int SelectInt(XmlDocument doc, params string[] names)
    {
        foreach (var n in names)
        {
            var node = doc.SelectSingleNode("//" + n);
            if (node != null && int.TryParse(node.InnerText.Trim(), out var v)) return v;
        }
        return 0;
    }

    private static string? TryString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return null;
    }

    private static long TryLong(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
                if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var vs)) return vs;
            }
        }
        return 0;
    }

    private static int TryInt(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) return v;
                if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var vs)) return vs;
            }
        }
        return 0;
    }

    private static string TruncateForLog(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private static void Log(string msg)
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
    }

    public void Dispose()
    {
        try
        {
            _staRunning = false;
            if (_ourReplyHwnd != IntPtr.Zero)
            {
                PostMessage(_ourReplyHwnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            }
            _staThread?.Join(500);
            if (_ourReplyHwnd != IntPtr.Zero)
            {
                DestroyWindow(_ourReplyHwnd);
                _ourReplyHwnd = IntPtr.Zero;
            }
            if (!string.IsNullOrEmpty(_ourReplyClassName))
            {
                UnregisterClassW(_ourReplyClassName, GetModuleHandleW(null));
            }
            // Best-effort: cancel any pending TCS so awaiting callers don't hang.
            foreach (var kv in _pending)
            {
                kv.Value.ResponseTcs.TrySetCanceled();
            }
            _pending.Clear();
        }
        catch (Exception ex)
        {
            Log($"[ComApi] Dispose error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed record PendingRequest(TaskCompletionSource<string> ResponseTcs, int CreatedAtTick);

    // ---- P/Invoke --------------------------------------------------------

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMin, uint wMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}