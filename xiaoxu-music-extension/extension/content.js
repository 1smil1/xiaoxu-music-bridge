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
