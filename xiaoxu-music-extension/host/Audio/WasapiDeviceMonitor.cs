using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using xiaoxu_music_bridge.Common;

namespace xiaoxu_music_bridge.Audio;

/// <summary>
/// v3.6.7: WASAPI default-device-change listener.
///
/// Symptom that motivated this: the user unplugged the Bose USB headset mid-
/// playback. The WasapiLoopbackCapture stopped streaming, the host's
/// <c>AudioBeatService.IsCaptureReady</c> flipped to false, and the dashboard
/// froze on a uniform-white particle wall (no bass, no cover refetch). Host
/// stayed alive but useless until manual restart.
///
/// What this fixes: we register a COM <c>IMMNotificationClient</c> for the
/// audio-render endpoint category. Any <c>OnDefaultDeviceChanged</c> event
/// (added/removed/format-changed) triggers <see cref="OnDefaultDeviceChanged"/>,
/// which calls back into the AudioBeatService to tear down + recreate the
/// loopback capture against the new default device. We log every event so
/// post-mortem investigation is possible from debug.log.
///
/// Thread safety: COM callbacks marshal onto the NAudio internal sync
/// context. We dispatch the capture restart onto a regular ThreadPool task
/// so the COM notification doesn't block on capture disposal.
/// </summary>
public sealed class WasapiDeviceMonitor : IDisposable
{
    private readonly Action _restartCapture;
    private readonly Action<string> _log;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly DeviceNotificationClient _sink;
    private bool _disposed;

    public WasapiDeviceMonitor(Action restartCapture, Action<string>? log = null)
    {
        _restartCapture = restartCapture;
        _log = log ?? DefaultLog;
        _enumerator = new MMDeviceEnumerator();
        _sink = new DeviceNotificationClient(OnDeviceChanged);
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_sink);
            _log("[WasapiDeviceMonitor] registered IMMNotificationClient");
        }
        catch (Exception ex)
        {
            _log($"[WasapiDeviceMonitor] RegisterEndpointNotificationCallback failed: {ex.Message}");
        }
    }

    private void OnDeviceChanged(string deviceId, DeviceState newState, string role, string verb)
    {
        // role: "eConsole" / "eMultimedia" / "eCommunications"
        // verb: "added" / "removed" / "default changed" / "state changed" / "format changed"
        _log($"[WasapiDeviceMonitor] device {verb}: role={role} state={newState} id={deviceId}");

        // For default-render changes, just bounce the capture. For "removed"
        // without a successor, the next Start() will likely throw — we catch
        // there and log; recovery watchdog will retry on the next tick.
        if (verb is "default changed" or "removed" or "state changed")
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _restartCapture();
                    _log("[WasapiDeviceMonitor] capture restart succeeded");
                }
                catch (Exception ex)
                {
                    _log($"[WasapiDeviceMonitor] capture restart failed: {ex.Message}");
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_sink);
            _log("[WasapiDeviceMonitor] unregistered");
        }
        catch { }
        try { _enumerator.Dispose(); } catch { }
    }

    private static void DefaultLog(string msg)
    {
        LogPaths.SafeAppend(LogPaths.DebugLog,
            $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
    }

    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly Action<string, DeviceState, string, string> _onChange;
        public DeviceNotificationClient(Action<string, DeviceState, string, string> onChange)
        {
            _onChange = onChange;
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render) return;
            _onChange(defaultDeviceId, DeviceState.Active, role.ToString(), "default changed");
        }

        public void OnDeviceAdded(string pwstrDeviceId) =>
            _onChange(pwstrDeviceId, DeviceState.Active, "n/a", "added");

        public void OnDeviceRemoved(string pwstrDeviceId) =>
            _onChange(pwstrDeviceId, DeviceState.NotPresent, "n/a", "removed");

        public void OnDeviceStateChanged(string pwstrDeviceId, DeviceState newState) =>
            _onChange(pwstrDeviceId, newState, "n/a", "state changed");

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { /* not interesting */ }
    }
}