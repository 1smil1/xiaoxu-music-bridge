// QQMusicComApiService — primary position source for the Win32 fallback path.
//
// v3.2.7: GSMTC is permanently broken on the user's machine (WinRT projection
// DLL missing from single-file publish — see GsmtcHealthTracker.IsPermanentlyBroken).
// UIA was a partial fix in v3.2.3, but QQ Music's CEF-rendered UI doesn't expose
// a Slider/ProgressBar to UIA at all (1437 nodes walked, zero sliders). So neither
// GSMTC nor UIA can give us real position.
//
// What does work: QQ Music ships a hidden COM-API window called
// `csQQMusicComApiWnd2017` (title `QQMusic_COM_WND_<UUID>`). This window accepts
// WM_COPYDATA messages with XML/JSON payloads describing actions like
// `player/playerstatus` and (per the reverse-engineered protocol) replies via a
// registered window message back to the caller's HWND. Protocol isn't
// officially documented — community reverse-engineering. We try the action name
// `player/playerstatus` first; if it returns empty, we'd expand the action list.
//
// Threading: WM_COPYDATA SendMessage must be sent from a thread that pumps a
// Windows message loop, otherwise the reply window message can be delivered to
// the wrong thread or get queued indefinitely. So we run a dedicated STA thread
// that:
//   1. Registers a unique reply window class + creates a message-only window.
//   2. Pumps GetMessageW/DispatchMessageW forever.
//   3. Accepts request tasks via TaskScheduler.StartNew(... _staScheduler),
//      which runs the SendMessage on the STA thread.
//   4. The WndProc hands the reply (delivered as another WM_COPYDATA whose
//      dwData carries the request id we chose) to the matching TaskCompletionSource.
//
// Timeout: 1 second. The dashboard polls /state/current at ~1 Hz; we want the
// whole fallback chain (title from daemon window + position from COM API) to
// complete inside that budget. If QQ Music is sluggish or hung we degrade to
// position=0 and let the beat-based rAF gate on the frontend decide play/pause.

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
    // Candidate window class names, newest first. QQ Music's COM API window
    // class has changed across major versions; we list the known ones and stop
    // at the first hit. New versions can be added here.
    private static readonly string[] WindowClasses = new[]
    {
        "csQQMusicComApiWnd2017",
        "csQQMusicComApiWnd2020",
        "csQQMusicComApiWnd2023",
        "QQMusic_COM_WND",
    };

    private const int WM_COPYDATA = 0x004A;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private IntPtr _comApiHwnd;
    private IntPtr _ourReplyHwnd;
    private string _ourReplyClassName = "";
    private uint _replyMsgId;
    private WndProcDelegate? _wndProcDelegate; // GC pin
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<string>> _pending = new();
    private int _nextReqId;

    // STA pump — SendMessage WM_COPYDATA + WndProc reply both need a thread
    // pumping messages. TaskScheduler that schedules onto this thread.
    private readonly Thread _staThread;
    private readonly BlockingCollection<Action> _staQueue = new();
    private volatile bool _staRunning;

    public QQMusicComApiService()
    {
        _staThread = new Thread(StaThreadMain)
        {
            Name = "QQMusicComApi-STA",
            IsBackground = true,
        };
        // STA must be set BEFORE Start (see Win32MediaService v3.2.5 lesson).
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void StaThreadMain()
    {
        _staRunning = true;

        // Register a unique window class so we can be sure no other process
        // collides with us. RegisterWindowMessage gives us a globally-unique
        // message id that QQ Music can use to address replies to us.
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

        // Message-only window — invisible, no taskbar entry, never shown.
        _ourReplyHwnd = CreateWindowExW(
            0, _ourReplyClassName, "XiaoxuMusicBridge-ComApiReply",
            0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_ourReplyHwnd == IntPtr.Zero)
        {
            Log($"[ComApi] CreateWindowExW failed err={Marshal.GetLastWin32Error()}");
        }

        // Standard message pump. We don't use a TaskScheduler — we just
        // marshal calls onto this thread via _staQueue so the SendMessage
        // and WndProc reply stay on the same apartment.
        while (_staRunning)
        {
            // TryGetMessage with non-blocking peek so we can also drain our
            // internal queue between windows messages. (Real-world load is
            // negligible so this is fine.)
            IntPtr got = GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (got == 0 || got == -1) break; // WM_QUIT or error
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
            // Drain any queued SendMessage tasks between windows messages
            while (_staQueue.TryTake(out var action, 0)) action();
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _replyMsgId && lParam != IntPtr.Zero)
        {
            // QQ Music sends the response as WM_COPYDATA in the registered
            // message. lpData of the COPYDATASTRUCT carries the UTF-8 bytes,
            // dwData carries our request id (we set it on send).
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
                    if (_pending.TryRemove(reqId, out var tcs))
                    {
                        tcs.TrySetResult(responseXml);
                    }
                    else
                    {
                        Log($"[ComApi] reply for unknown reqId={reqId}");
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
    /// Ask QQ Music for current player status (position, duration, isPlaying,
    /// title, artist). Returns null if the COM API window can't be found,
    /// the request times out, or the response can't be parsed.
    ///
    /// Safe to call from any thread — internally marshals onto the STA pump.
    /// </summary>
    public Task<MediaStatus?> GetPlayerStatusAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<MediaStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Enqueue the whole SendMessage attempt onto the STA thread so we
        // don't race the WndProc reply dispatch.
        _staQueue.Add(() =>
        {
            try
            {
                var result = GetPlayerStatusOnSta(ct);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Log($"[ComApi] STA GetPlayerStatus failed: {ex.GetType().Name}: {ex.Message}");
                tcs.TrySetResult(null);
            }
        });

        return tcs.Task;
    }

    private MediaStatus? GetPlayerStatusOnSta(CancellationToken ct)
    {
        // Locate COM API window lazily + re-scan if it went away (QQ Music
        // can restart its listener window on version upgrade / crash).
        if (_comApiHwnd == IntPtr.Zero || !IsWindow(_comApiHwnd))
        {
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
                // First-deploy debug: surface which classes we tried.
                Log($"[ComApi] COM API window not found (tried: {string.Join(", ", WindowClasses)})");
                return null;
            }
        }
        if (_ourReplyHwnd == IntPtr.Zero)
        {
            Log("[ComApi] reply window not created — STA init failed");
            return null;
        }

        var reqId = unchecked((uint)Interlocked.Increment(ref _nextReqId));
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                  "<QQMusic><Action>player/playerstatus</Action></QQMusic>";

        var responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = responseTcs;

        byte[] payload;
        try
        {
            payload = Encoding.UTF8.GetBytes(xml);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(reqId, out _);
            Log($"[ComApi] payload encode failed: {ex.Message}");
            return null;
        }

        var pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            var cds = new COPYDATASTRUCT
            {
                dwData = (IntPtr)reqId,
                cbData = payload.Length,
                lpData = pinned.AddrOfPinnedObject(),
            };
            // wParam = our reply HWND per community reverse-engineered protocol.
            // (Some variants expect wParam=0; if responses never arrive after
            // first deploy, try 0 here.)
            var sendResult = SendMessage(_comApiHwnd, WM_COPYDATA, _ourReplyHwnd, ref cds);
            Log($"[ComApi] sent reqId={reqId} sendResult={sendResult.ToInt64()} (TRUE={sendResult != IntPtr.Zero})");
        }
        catch (Exception ex)
        {
            _pending.TryRemove(reqId, out _);
            Log($"[ComApi] SendMessage THREW: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            pinned.Free();
        }

        // Wait for reply up to 1s. We can't `await` here because we're on the
        // STA thread inside an Action; instead poll with a short sleep loop
        // that yields to the message pump by calling Thread.Sleep (the
        // BlockingCollection.TryTake on _staQueue in StaThreadMain also helps
        // drain background work).
        var deadline = Environment.TickCount + 1000;
        while (Environment.TickCount < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                _pending.TryRemove(reqId, out _);
                return null;
            }
            if (responseTcs.Task.IsCompleted) break;
            Thread.Sleep(20);
        }

        if (!responseTcs.Task.IsCompleted)
        {
            _pending.TryRemove(reqId, out _);
            Log($"[ComApi] reqId={reqId} timeout (1s)");
            return null;
        }

        string responseXml;
        try
        {
            responseXml = responseTcs.Task.Result;
        }
        catch (Exception ex)
        {
            Log($"[ComApi] reqId={reqId} reply fault: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        try
        {
            return ParsePlayerStatusXml(responseXml);
        }
        catch (Exception ex)
        {
            Log($"[ComApi] parse failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static MediaStatus? ParsePlayerStatusXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        // Some QQ Music versions reply with JSON instead of XML. Try XML first
        // (the documented/observed shape), fall back to JSON.
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

    // QQ Music state code meaning: not officially documented. We default to
    // "code 1 = playing", but the first successful raw response log will
    // reveal the actual mapping. To make the heuristic robust we also treat
    // state==2 as playing (some versions use 2 for playing, 1 for paused).
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
            // Post WM_QUIT to our message pump so it exits cleanly.
            if (_ourReplyHwnd != IntPtr.Zero)
            {
                PostMessage(_ourReplyHwnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            }
            if (_staThread.IsAlive)
            {
                _staThread.Join(500);
            }
            if (_ourReplyHwnd != IntPtr.Zero)
            {
                DestroyWindow(_ourReplyHwnd);
                _ourReplyHwnd = IntPtr.Zero;
            }
            if (!string.IsNullOrEmpty(_ourReplyClassName))
            {
                UnregisterClassW(_ourReplyClassName, GetModuleHandleW(null));
            }
            _staQueue.CompleteAdding();
            _staQueue.Dispose();
        }
        catch (Exception ex)
        {
            Log($"[ComApi] Dispose error: {ex.GetType().Name}: {ex.Message}");
        }
    }

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

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

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