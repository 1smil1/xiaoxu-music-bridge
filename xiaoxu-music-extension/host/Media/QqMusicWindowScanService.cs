// QqMusicWindowScanService — Track A1+A2 implementation for v3.2.7.
//
// Problem (carried forward from v3.2.6 v3.2.7 COM API path):
//   The single known COM window `csQQMusicComApiWnd2017` accepts WM_COPYDATA but
//   never replies to the registered message ID we send. 7 candidate msgIds all
//   silently dropped. So we need to enumerate OTHER QQMusic.exe windows and
//   try multiple action/payload/param permutations to find any path that
//   returns real position/duration.
//
// Scan plan:
//   For each QQMusic.exe PID:
//     For each candidate window class (5 total):
//       Find first matching HWND in that process
//       If found, for each (action, payloadTemplate, wParam variant):
//         - Create/own a tiny message-only window with multiple registered
//           window messages (reuse `_candidateMsgIds` semantic; same ansi atom)
//         - SendMessageTimeout(hwnd, WM_COPYDATA, wParam, COPYDATASTRUCT)
//         - Pump messages for up to 200ms
//         - If we received any reply on a registered msg → parse it for
//           position/duration/isPlaying and return; if XML/JSON recognized
//           but doesn't have position → still return as "saw reply"
//         - Decode payload as best-effort (XML / JSON / plaintext)
//
// Cost: at most 5 classes × 6 actions × 5 templates × 2 wParam variants = 300
// attempts; with a 200ms SendMessageTimeout cap per attempt, the total worst
// case budget is 60s. In practice SendMessageTimeout returns immediately when
// QQ Music doesn't process the message (result=0x1 wndProcResult=0x0). So the
// actual cost is seconds not minutes.
//
// On success: a MediaStatus? is returned with a best-effort positionMs; the
// caller (Win32MediaService) will SyncAnchorFromComApi and continue using the
// real value going forward.
//
// Failure path: returns null. Win32MediaService falls back to VirtualClock.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Media;

/// <summary>
/// Brute-force scan of QQ Music's windows for any WM_COPYDATA-compatible COM
/// surface that returns position/duration info. v3.2.7 best-effort — succeeds
/// only when (any combination of class, action, payload, wParam) receives a
/// recognized reply. Failure is logged but never fatal.
/// </summary>
public sealed class QqMusicWindowScanService : IDisposable
{
    // 5 candidate class names. The first 4 were discovered in QQ Music's actual
    // window list but never probed. csQQMusicComApiWnd2017 is kept for
    // completeness so the scan includes the path v3.2.6 already tried.
    private static readonly string[] CandidateClasses = new[]
    {
        "QQMusic_MolePluginWnd",
        "QQMusicDummyWindow",
        "csQQMusicComApiWnd2017",
        "QQMusicDummyWnd",
        "QQMusic_LRC_Wnd",
    };

    // 6 candidate action names. player/playerstatus was confirmed accepted but
    // not-replied; the rest are educated guesses based on Tencent's other
    // products' COM APIs and reverse-engineering forum references.
    private static readonly string[] CandidateActions = new[]
    {
        "player/playerstatus",
        "player/getstatus",
        "player/getcurplaylist",
        "player/nowplayingsong",
        "player/getplayinfo",
        "GetPlayerStatus",
    };

    // 5 payload templates. Different QQ Music versions speak either XML,
    // minimal XML, JSON, or plain text. We try each template per (class,
    // action) combo.
    private static readonly string[] PayloadTemplates = new[]
    {
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><QQMusic><Action>{action}</Action></QQMusic>",
        "<?xml version=\"1.0\"?><QQMusic><Action>{action}</Action><CallbackHwnd>{hwnd:x}</CallbackHwnd></QQMusic>",
        "<?xml version=\"1.0\"?><message action=\"{action}\"/>",
        "{\"action\":\"{action}\",\"callback_hwnd\":{hwnd}}",
        "action={action}",
    };

    // SendMessageTimeout variants: sometimes wParam carries the reply message
    // ID, sometimes it's 0. We try both per attempt.
    private static readonly IntPtr[] WParamVariants = new[] { IntPtr.Zero, (IntPtr)0xC000 };

    private const int WM_COPYDATA = 0x004A;
    private const int RequestTimeoutMs = 200;       // per attempt — fail fast
    private const int PumpDurationMs = 150;          // how long to drain pump after SendMessageTimeout

    private static readonly string[] MessageIdsForReply = new[]
    {
        "XiaoxuMusicBridge_WindowScan_Reply_v1",
        "QQMusic_Request_Reply",
        "QQMusic_PlayerStatus_Reply",
        "QQMusicDesktopMessage",
        "QQMusicPcClient",
        "QMusicReply",
        "QQMusic_Reply_v1",
    };

    private Thread? _staThread;
    private bool _running;
    private CancellationTokenSource? _cts;
    private IntPtr _ourHwnd = IntPtr.Zero;
    private readonly Dictionary<int, IntPtr> _registeredMsgIds = new();
    private readonly BlockingCollection<CapturedReply> _replyQueue = new();
    private readonly ConcurrentQueue<HwndResult> _scanResults = new();
    private IntPtr _bestEffortCallbackWindow = IntPtr.Zero; // window that posts results back to scanner thread

    public QqMusicWindowScanService()
    {
        _staThread = new Thread(StaThreadMain)
        {
            IsBackground = true,
            Name = "QQMusicWindowScan-STA"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    /// <summary>
    /// Synchronously (from caller's POV) scan all candidate QQMusic windows
    /// and return the first media status with PositionMs &gt; 0. Returns null
    /// if no successful reply was captured within the budget.
    /// </summary>
    public async Task<MediaStatus?> TryAllWindowsAllActionsAsync(
        string? title, string? artist, CancellationToken ct)
    {
        try
        {
            // Run the synchronous scan on our STA thread so we can pump messages.
            var scanTask = Task.Run(() => ScanAll(title, artist, ct), ct);
            var completed = await Task.WhenAny(scanTask, Task.Delay(8000, ct));
            if (completed != scanTask) return null;
            return await scanTask;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log($"scan outer exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private MediaStatus? ScanAll(string? title, string? artist, CancellationToken ct)
    {
        try
        {
            EnsureReplyWindow();
        }
        catch (Exception ex)
        {
            Log($"could not create reply window: {ex.GetType().Name}");
            return null;
        }

        // Process filter: only enumerate QQMusic.exe windows.
        var procs = Process.GetProcessesByName("QQMusic");
        if (procs.Length == 0) return null;
        var pid = procs[0].Id;
        procs.ToList().ForEach(p => p.Dispose());

        // Enumerate all windows for QQMusic.exe, index by class.
        var hwndsByClass = new Dictionary<string, List<IntPtr>>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint foundPid);
            if ((int)foundPid != pid) return true;
            var cn = new StringBuilder(256);
            GetClassName(h, cn, 256);
            var className = cn.ToString();
            if (!hwndsByClass.TryGetValue(className, out var list))
                hwndsByClass[className] = list = new List<IntPtr>();
            list.Add(h);
            return true;
        }, IntPtr.Zero);

        Log($"scan starting — pid={pid} unique classes={hwndsByClass.Count}");

        int attempts = 0;
        int replies = 0;

        foreach (var className in CandidateClasses)
        {
            if (ct.IsCancellationRequested) break;
            if (!hwndsByClass.TryGetValue(className, out var candidates) || candidates.Count == 0)
            {
                continue; // class not present in this QQMusic build
            }
            foreach (var hwnd in candidates)
            {
                foreach (var action in CandidateActions)
                {
                    foreach (var tmpl in PayloadTemplates)
                    {
                        foreach (var wparamVariant in WParamVariants)
                        {
                            if (ct.IsCancellationRequested) break;

                            attempts++;
                            string payload;
                            try
                            {
                                payload = tmpl
                                    .Replace("{action}", action)
                                    .Replace("{hwnd}", _ourHwnd.ToInt64().ToString("x"));
                            }
                            catch { continue; }

                            // Drain queue of stale replies from prior attempts.
                            while (_replyQueue.TryTake(out _)) { }

                            var payloadBytes = Encoding.UTF8.GetBytes(payload);
                            var cdsPtr = AllocateCopyDataStruct(payloadBytes, attempts);
                            var startTime = DateTime.Now;

                            IntPtr sendResult;
                            try
                            {
                                sendResult = SendMessageTimeout(
                                    hwnd,
                                    WM_COPYDATA,
                                    wparamVariant == IntPtr.Zero ? _ourHwnd : wparamVariant,
                                    cdsPtr,
                                    0x0002 /* SMTO_ABORTIFHUNG */,
                                    RequestTimeoutMs,
                                    out _);
                            }
                            catch (Exception ex)
                            {
                                Log($"[scan] send threw on class={className} action={action}: {ex.GetType().Name}");
                                FreeCopyDataStruct(cdsPtr);
                                continue;
                            }

                            // Pump for a brief window to drain any delayed reply.
                            PumpUntil(PumpDurationMs);

                            var sendRsltStr = sendResult.ToInt64().ToString("X");

                            while (_replyQueue.TryTake(out var captured))
                            {
                                replies++;
                                Log($"[scan] GOT REPLY class={className} action={action} tmpl={GetTemplateIndex(tmpl)} wparam=0x{wparamVariant.ToInt64():X} ({captured.RawPreview})");

                                // Attempt parse
                                var status = TryParseReply(captured.Payload, title, artist);
                                FreeCopyDataStruct(cdsPtr);

                                if (status != null && status.PositionMs > 0)
                                {
                                    Log($"[scan] SUCCESS — class={className} action={action} pos={status.PositionMs}ms dur={status.DurationMs}ms");
                                    return status;
                                }
                                // Even without position, the act of getting a reply
                                // is interesting — fall through to try more combos.
                            }

                            FreeCopyDataStruct(cdsPtr);
                        }
                    }
                }
            }
        }

        if (attempts > 0)
        {
            Log($"[scan] finished — attempts={attempts} replies={replies}");
        }
        return null;
    }

    private static int GetTemplateIndex(string tmpl)
    {
        for (int i = 0; i < PayloadTemplates.Length; i++)
            if (PayloadTemplates[i] == tmpl) return i;
        return -1;
    }

    // --- Reply parsing ---

    private MediaStatus? TryParseReply(byte[]? payload, string? fallbackTitle, string? fallbackArtist)
    {
        if (payload == null || payload.Length == 0) return null;
        string text;
        try { text = Encoding.UTF8.GetString(payload); }
        catch { return null; }

        // Try JSON first (most structured)
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            long posMs = ExtractLong(root, "playtime", "position", "positionMs", "current", "curpos");
            long durMs = ExtractLong(root, "totaltime", "duration", "durationMs", "total", "length");
            bool playing = ExtractBool(root, "state", "isPlaying", "playing", "playstate");
            string t = ExtractString(root, "title", "songname", "name") ?? fallbackTitle;
            string a = ExtractString(root, "singer", "artist") ?? fallbackArtist;
            if (posMs > 0 || durMs > 0)
            {
                return new MediaStatus(true, "QQMusic", t, a, null, null, playing, posMs, durMs, DateTimeOffset.Now);
            }
        }
        catch { /* not JSON */ }

        // Try XML
        try
        {
            var xd = new XmlDocument();
            xd.LoadXml(text);
            var root = xd.DocumentElement;
            if (root != null)
            {
                long posMs = ExtractLongXml(root, "playtime", "position", "positionMs");
                long durMs = ExtractLongXml(root, "totaltime", "duration", "durationMs");
                bool playing = ExtractBoolXml(root, "state", "isPlaying", "playing");
                if (posMs > 0 || durMs > 0)
                {
                    return new MediaStatus(true, "QQMusic",
                        ExtractStringXml(root, "title", "songname") ?? fallbackTitle,
                        ExtractStringXml(root, "singer", "artist") ?? fallbackArtist,
                        null, null, playing, posMs, durMs, DateTimeOffset.Now);
                }
            }
        }
        catch { /* not XML */ }

        return null;
    }

    private static long ExtractLong(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)) return v;
                if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var sv)) return sv;
            }
        }
        return 0;
    }

    private static bool ExtractBool(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var el))
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
                if (el.ValueKind == JsonValueKind.Number) return el.GetInt64() != 0;
            }
        }
        return false;
    }

    private static string? ExtractString(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        return null;
    }

    private static long ExtractLongXml(XmlElement root, params string[] names)
    {
        foreach (var n in names)
        {
            var node = root.SelectSingleNode(n);
            if (node != null && long.TryParse(node.InnerText, out var v)) return v;
        }
        return 0;
    }

    private static bool ExtractBoolXml(XmlElement root, params string[] names)
    {
        foreach (var n in names)
        {
            var node = root.SelectSingleNode(n);
            if (node != null)
            {
                var t = node.InnerText.ToLowerInvariant();
                if (t == "true" || t == "1") return true;
            }
        }
        return false;
    }

    private static string? ExtractStringXml(XmlElement root, params string[] names)
    {
        foreach (var n in names)
        {
            var node = root.SelectSingleNode(n);
            if (node != null && !string.IsNullOrWhiteSpace(node.InnerText))
                return node.InnerText;
        }
        return null;
    }

    private readonly struct CapturedReply
    {
        public CapturedReply(byte[]? payload, string rawPreview)
        {
            Payload = payload;
            RawPreview = rawPreview;
        }
        public byte[]? Payload { get; }
        public string RawPreview { get; }
    }

    private readonly struct HwndResult
    {
        public HwndResult(IntPtr hwnd, string className)
        {
            Hwnd = hwnd;
            ClassName = className;
        }
        public IntPtr Hwnd { get; }
        public string ClassName { get; }
    }

    // --- Window pump / WndProc ---

    private void StaThreadMain()
    {
        try { EnsureReplyWindow(); } catch { /* ignore */ }
        _running = true;

        // Pump until shutdown
        while (_running)
        {
            try
            {
                if (!GetMessage(out var msg, IntPtr.Zero, 0, 0))
                {
                    break;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            catch (Exception ex)
            {
                Log($"STA pump exception: {ex.GetType().Name}: {ex.Message}");
            }
        }
        // Drain queue on shutdown
        _replyQueue.CompleteAdding();
    }

    private void EnsureReplyWindow()
    {
        if (_ourHwnd != IntPtr.Zero) return;

        // Pre-create window class + window on this thread
        var className = "XiaoxuMusicBridge_WindowScan_v1_" + Guid.NewGuid().ToString("N");

        WNDCLASSEX wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>();
        // Keep the WndProc delegate rooted for the lifetime of the class to
        // prevent the GC from collecting it (which would make the function
        // pointer go stale). GetFunctionPointerForDelegate is the only safe
        // way to convert a managed method to an unmanaged IntPtr.
        _wndProcDelegate = WndProc;
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        wc.hInstance = GetModuleHandle(null);
        wc.lpszClassName = className;
        if (RegisterClassEx(ref wc) == 0)
        {
            Log($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        _ourHwnd = CreateWindowEx(
            0, className, "XiaoxuMusicBridge_WindowScan",
            0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_ourHwnd == IntPtr.Zero)
        {
            Log("CreateWindowEx returned IntPtr.Zero");
            return;
        }

        // Register the candidate reply message IDs
        foreach (var name in MessageIdsForReply)
        {
            uint id = RegisterWindowMessage(name);
            if (id != 0)
            {
                _registeredMsgIds[(int)id] = (IntPtr)id;
            }
        }

        Log($"[scan] reply window created hwnd=0x{_ourHwnd.ToInt64():X} registeredMsgIds={_registeredMsgIds.Count}");
    }

    /// <summary>
    /// Drain pending window messages for up to <paramref name="ms"/> ms without
    /// blocking on any single message. After the deadline, return.
    /// </summary>
    private void PumpUntil(int ms)
    {
        var deadline = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < deadline)
        {
            var remaining = (int)(deadline - DateTime.Now).TotalMilliseconds;
            if (remaining <= 0) break;
            // PeekMessage with PM_REMOVE so old messages don't stack up.
            // Filter 0,0 means all messages for all windows.
            if (!PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_NOREMOVE */))
            {
                // No messages waiting — sleep a little.
                MsgWaitForMultipleObjects(0, IntPtr.Zero, false, (uint)Math.Min(20, remaining), 0x01u /* QS_POSTMESSAGE */);
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // WM_COPYDATA (0x004A) raw — QQMusic may reply via COPYDATA directly
            if (msg == WM_COPYDATA && lParam != IntPtr.Zero)
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                byte[]? data = null;
                if (cds.cbData > 0 && cds.lpData != IntPtr.Zero)
                {
                    data = new byte[cds.cbData];
                    Marshal.Copy(cds.lpData, data, 0, (int)cds.cbData);
                }
                string preview = data != null ? TruncateForLog(Encoding.UTF8.GetString(data), 160) : "(empty)";
                _replyQueue.TryAdd(new CapturedReply(data, preview));
                return new IntPtr(1);
            }

            // Registered window message — wParam/lParam carry our reqId (in dwData)
            if (_registeredMsgIds.ContainsKey((int)msg))
            {
                byte[]? data = null;
                if (lParam != IntPtr.Zero)
                {
                    // Some QQMusic variants use lParam as a pointer to a UTF-8 string
                    var probeLen = (int)Math.Min(wParam.ToInt64() & 0x7FFFFFFFL, 4096);
                    if (probeLen > 0)
                    {
                        try
                        {
                            data = new byte[probeLen];
                            Marshal.Copy(lParam, data, 0, probeLen);
                            // Trim trailing nulls
                            int trim = data.Length;
                            while (trim > 0 && data[trim - 1] == 0) trim--;
                            if (trim != data.Length) Array.Resize(ref data, trim);
                        }
                        catch { data = null; }
                    }
                }
                if (data == null && wParam != IntPtr.Zero)
                {
                    try
                    {
                        var len = (int)Math.Min(wParam.ToInt64() & 0x7FFFFFFFL, 4096);
                        if (len > 0)
                        {
                            data = new byte[len];
                            Marshal.Copy(wParam, data, 0, len);
                            int trim = data.Length;
                            while (trim > 0 && data[trim - 1] == 0) trim--;
                            if (trim != data.Length) Array.Resize(ref data, trim);
                        }
                    }
                    catch { }
                }
                string preview = data != null ? TruncateForLog(Encoding.UTF8.GetString(data), 160) : "(empty)";
                _replyQueue.TryAdd(new CapturedReply(data, preview));
                return new IntPtr(1);
            }
        }
        catch (Exception ex)
        {
            Log($"WndProc exception: {ex.GetType().Name}: {ex.Message}");
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // --- COPYDATASTRUCT allocator ---

    private static IntPtr AllocateCopyDataStruct(byte[] payload, int reqId)
    {
        int totalSize = Marshal.SizeOf<COPYDATASTRUCT>();
        IntPtr ptr = Marshal.AllocHGlobal(totalSize);
        IntPtr dataPtr = Marshal.AllocHGlobal(payload.Length);
        Marshal.Copy(payload, 0, dataPtr, payload.Length);
        var cds = new COPYDATASTRUCT
        {
            dwData = (IntPtr)reqId,
            cbData = (uint)payload.Length,
            lpData = dataPtr,
        };
        Marshal.StructureToPtr(cds, ptr, false);
        // Stash dataPtr for later free
        _allocMap[ptr] = dataPtr;
        return ptr;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, IntPtr> _allocMap = new();

    private static void FreeCopyDataStruct(IntPtr cdsPtr)
    {
        if (_allocMap.TryRemove(cdsPtr, out var dataPtr))
        {
            try { Marshal.FreeHGlobal(dataPtr); } catch { }
            try { Marshal.DestroyStructure(cdsPtr, typeof(COPYDATASTRUCT)); } catch { }
            try { Marshal.FreeHGlobal(cdsPtr); } catch { }
        }
    }

    private static string TruncateForLog(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "...(" + s.Length + " bytes)";
    }

    private static void Log(string msg)
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] [QqMusicWindowScan] {msg}\n");
    }

    public void Dispose()
    {
        try
        {
            _running = false;
            if (_ourHwnd != IntPtr.Zero)
            {
                PostMessage(_ourHwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            }
            _staThread?.Join(500);
            _replyQueue.Dispose();
        }
        catch { }
    }

    // --- P/Invoke ---

    // Rooted WndProc delegate — must live for the lifetime of the class so
    // the function pointer passed to RegisterClassEx doesn't dangle.
    private WndProcDelegate? _wndProcDelegate;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
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

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public uint cbData;
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

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string lpString);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(int nCount, IntPtr pHandles, bool fWaitAll, uint dwMilliseconds, uint dwWakeMask);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static readonly IntPtr HWND_MESSAGE = (IntPtr)(-3);
}
