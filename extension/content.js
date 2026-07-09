// content.js — Bridge between page (injected.js) and background (background.js)
// Injected into xiaoxu.xin pages. Runs in content script world (has chrome.runtime access).
//
// Single long-lived port ("xiaoxu-bridge") handles EVERYTHING:
//   - bridgeRequest (page fetch → host) and bridgeResponse
//   - BEAT_SUBSCRIBE / BEAT_UNSUBSCRIBE
//   - BEAT_UPDATE push from background → page
//
// Why one port: an open chrome.runtime.Port keeps the MV3 service worker
// alive indefinitely. The previous design (sendMessage + separate beat-port)
// was killed by Chrome after ~30s of inactivity, causing
// "host unavailable (503)" + "message port closed" on every fresh poll.
// A long-lived port is the standard fix for this.

console.log('[xiaoxu-music] content script loaded, url:', location.href);

// Inject the fetch interceptor into the PAGE context via script.src (bypasses CSP)
const script = document.createElement('script');
script.src = chrome.runtime.getURL('injected.js');
(document.head || document.documentElement).appendChild(script);
script.remove();

function postBridgeError(fetchId, message) {
  window.postMessage({
    _bridgeFetchResponse: true,
    _bridgeFetchId: fetchId,
    ok: false,
    status: 503,
    error: message,
    data: { error: message },
  }, '*');
}

// ---- Single long-lived port ----

let port = null;
let reconnectTimer = null;
let reqId = 0;
let extensionContextInvalidated = false;
const pending = new Map(); // _bridgeFetchId → { resolve, reject }

function isContextInvalidated(err) {
  if (!err) return false;
  const msg = (err.message || String(err) || '').toLowerCase();
  // Only treat EXPLICIT "extension context invalidated" as fatal.
  // "Message port closed" happens whenever the SW is terminated by Chrome
  // (idle timeout, low-memory eviction, browser shutdown) — it is NOT a
  // context invalidation, the extension is still usable, just reconnect.
  // Treating it as fatal here would lock `extensionContextInvalidated=true`
  // forever, and every subsequent BRIDGE_FETCH would 503.
  return msg.includes('extension context invalidated');
}

function openPort() {
  if (port || extensionContextInvalidated) return;
  try {
    port = chrome.runtime.connect({ name: 'xiaoxu-bridge' });
  } catch (e) {
    if (isContextInvalidated(e)) {
      console.warn('[xiaoxu-music] extension context invalidated — please reload the page (Ctrl+Shift+R) to recover');
      extensionContextInvalidated = true;
      window.postMessage({ type: 'BRIDGE_RELOAD_REQUIRED' }, '*');
      return;
    }
    console.warn('[xiaoxu-music] connect failed:', e.message);
    scheduleReconnect();
    return;
  }

  port.onMessage.addListener((msg) => {
    // bridgeResponse for a pending fetch
    if (msg && msg._bridgeFetchId != null && pending.has(msg._bridgeFetchId)) {
      const cb = pending.get(msg._bridgeFetchId);
      pending.delete(msg._bridgeFetchId);
      cb(msg);
      return;
    }

    // Beat push
    if (msg && msg.type === 'BEAT_UPDATE') {
      const out = {
        type: 'BEAT_UPDATE',
        bass: msg.bass ?? 0,
        volume: msg.volume ?? 0,
        pulse: msg.pulse ?? 0,
        glow: msg.glow ?? 0,
      };
      if (msg.bands) out.bands = msg.bands;
      if (msg.features) out.features = msg.features;
      if (msg.onsets) out.onsets = msg.onsets;
      if (msg.rhythm) out.rhythm = msg.rhythm;
      if (msg.state) out.state = msg.state;
      if (typeof msg.ts === 'number') out.ts = msg.ts;
      window.postMessage(out, '*');
      return;
    }

    if (msg && msg.type === 'BEAT_DISCONNECTED') {
      window.postMessage({ type: 'BEAT_DISCONNECTED' }, '*');
      return;
    }
  });

  port.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError;
    console.warn('[xiaoxu-music] bridge port disconnected, lastError:', err?.message);
    port = null;
    if (isContextInvalidated(err)) {
      console.warn('[xiaoxu-music] extension context invalidated — please reload the page (Ctrl+Shift+R) to recover');
      extensionContextInvalidated = true;
      window.postMessage({ type: 'BRIDGE_RELOAD_REQUIRED' }, '*');
      return;
    }
    // Reject all pending fetches so the page's await Promise resolves with 503
    for (const [, cb] of pending) {
      cb({ error: err?.message || 'Bridge port disconnected', status: 503 });
    }
    pending.clear();
    scheduleReconnect();
  });
}

function scheduleReconnect() {
  if (reconnectTimer || extensionContextInvalidated) return;
  // Reconnect fast — every poll waits at most 100ms before the port comes back.
  // Old 1000ms left a window where 4 in-flight /state/current requests all 503'd.
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    openPort();
  }, 100);
}

// Open immediately so the service worker is kept alive as soon as the page loads
openPort();

// ---- Page → extension message router ----

window.addEventListener('message', (e) => {
  if (e.source !== window) return;
  const data = e.data;
  if (!data) return;

  if (data.type === 'BRIDGE_FETCH') {
    if (!port) {
      const msg = extensionContextInvalidated
        ? 'Extension was reloaded. Please reload this page (Ctrl+Shift+R).'
        : 'Bridge port not connected';
      console.warn('[xiaoxu-music] BRIDGE_FETCH failed (no port):', data.url, '—', msg);
      postBridgeError(data._bridgeFetchId, msg);
      return;
    }
    const id = ++reqId;
    try {
      port.postMessage({
        type: 'bridgeRequest',
        _bridgeFetchId: id,
        url: data.url,
        method: data.method,
      });
      pending.set(id, (response) => {
        if (response && response.error) {
          console.warn('[xiaoxu-music] BRIDGE_FETCH host error:', data.url, '—', response.error);
          postBridgeError(data._bridgeFetchId, response.error);
        } else {
          window.postMessage({
            _bridgeFetchResponse: true,
            _bridgeFetchId: data._bridgeFetchId,
            ok: response?.ok !== false,
            status: response?.status ?? 200,
            data: response?.data,
          }, '*');
        }
      });
    } catch (e) {
      console.warn('[xiaoxu-music] BRIDGE_FETCH postMessage threw:', data.url, '—', e.message);
      pending.delete(id);
      postBridgeError(data._bridgeFetchId, e.message);
    }
    return;
  }

  if (data.type === 'BEAT_SUBSCRIBE') {
    // Beat subscription is implicit — the port is already open. The background
    // starts AudioBeatService as soon as the port connects, so just ack.
    if (port) {
      window.postMessage({ type: 'BEAT_SUBSCRIBE_ACK', ok: true }, '*');
    } else {
      // Port not yet up — ack false so the page knows to retry
      window.postMessage({ type: 'BEAT_SUBSCRIBE_ACK', ok: false, error: 'Bridge port not connected' }, '*');
    }
    return;
  }

  if (data.type === 'BEAT_UNSUBSCRIBE') {
    // No-op: beat is bound to the port lifetime, not a separate subscription
    return;
  }
});
