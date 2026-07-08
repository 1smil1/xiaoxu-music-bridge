// content.js — Bridge between page (injected.js) and background (background.js)
// Injected into xiaoxu.xin pages. Runs in content script world (has chrome.runtime access).

console.log('[xiaoxu-music] content script loaded, url:', location.href);

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

// Inject the fetch interceptor into the PAGE context via script.src (bypasses CSP)
const script = document.createElement('script');
script.src = chrome.runtime.getURL('injected.js');
(document.head || document.documentElement).appendChild(script);
script.remove();

// Listen for bridge fetch requests from the injected script
window.addEventListener('message', (e) => {
  if (e.source !== window) return;
  if (e.data?.type !== 'BRIDGE_FETCH') return;

  console.log('[xiaoxu-music] BRIDGE_FETCH received:', e.data.url, e.data.method);

  try {
    chrome.runtime.sendMessage(
      { type: 'bridgeRequest', url: e.data.url, method: e.data.method },
      (response) => {
        const lastError = chrome.runtime.lastError;
        console.log('[xiaoxu-music] sendMessage callback, lastError:', lastError?.message, 'response:', response);
        if (lastError) {
          postBridgeError(e.data._bridgeFetchId, lastError.message || 'Extension runtime unavailable');
          return;
        }

        window.postMessage({
          _bridgeFetchResponse: true,
          _bridgeFetchId: e.data._bridgeFetchId,
          ...response,
        }, '*');
      }
    );
  } catch (error) {
    const message = error && typeof error.message === 'string' ? error.message : 'Extension runtime unavailable';
    console.warn('[xiaoxu-music] sendMessage failed:', message);
    postBridgeError(e.data._bridgeFetchId, message);
  }
});

// ---- Beat stream: page → content → background → page ----
// Page calls window.postMessage({type:'BEAT_SUBSCRIBE'}) to start receiving beats.
// Background pushes BEAT_UPDATE messages here; we forward to page via postMessage.

let beatPort = null;

window.addEventListener('message', (e) => {
  if (e.source !== window) return;
  const data = e.data;
  if (!data) return;

  if (data.type === 'BEAT_SUBSCRIBE') {
    // Use a long-lived port to receive async beat pushes from background
    if (beatPort) {
      // Already subscribed — just ack
      window.postMessage({ type: 'BEAT_SUBSCRIBE_ACK', ok: true }, '*');
      return;
    }
    try {
      beatPort = chrome.runtime.connect({ name: 'beat-port' });
      beatPort.onMessage.addListener((msg) => {
        if (msg.type === 'BEAT_UPDATE') {
          // Forward to page — include v3 fields when present so the 12-event
          // rhythm pool can evaluate onsets/centroid/silence/etc.
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
        }
      });
      beatPort.onDisconnect.addListener(() => {
        beatPort = null;
        window.postMessage({ type: 'BEAT_DISCONNECTED' }, '*');
      });

      // Tell background to start beat stream
      chrome.runtime.sendMessage({ type: 'BEAT_SUBSCRIBE' }, (resp) => {
        window.postMessage({ type: 'BEAT_SUBSCRIBE_ACK', ok: !!resp?.ok }, '*');
      });
    } catch (err) {
      window.postMessage({ type: 'BEAT_SUBSCRIBE_ACK', ok: false, error: err?.message }, '*');
    }
    return;
  }

  if (data.type === 'BEAT_UNSUBSCRIBE') {
    if (beatPort) {
      try {
        chrome.runtime.sendMessage({ type: 'BEAT_UNSUBSCRIBE' }, () => {});
      } catch (e) {}
      try { beatPort.disconnect(); } catch (e) {}
      beatPort = null;
    }
    return;
  }
});
