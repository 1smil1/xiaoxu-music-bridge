// content.js — Bridge between page (injected.js) and background (background.js)
// Injected into xiaoxu.xin pages. Runs in content script world (has chrome.runtime access).

console.log('[xiaoxu-music] content script loaded, url:', location.href);

// Kick the host if it's not already running on localhost:17888. Without this,
// visiting xiaoxu.xin alone wouldn't start the bridge — only an actual
// /status or /control fetch from page code would, and the home page doesn't
// issue those. The background's "kick" handler uses chrome.runtime
// .sendNativeMessage, which makes Chrome start the registered native host.
// The host's NativeMessaging mode then calls EnsureServerAsync (Program.cs
// :738), which spawns `host.exe --server` if no server is already up.
(async function ensureBridgeHost() {
  try {
    const r = await fetch('http://localhost:17888/health', { cache: 'no-store' });
    if (r.ok) {
      console.log('[xiaoxu-music] host already running, skipping kick');
      return;
    }
  } catch (_) {
    // host down — kick it
  }
  console.log('[xiaoxu-music] host not running, sending kick');
  chrome.runtime.sendMessage({ type: 'kick' }, (response) => {
    if (chrome.runtime.lastError) {
      console.error('[xiaoxu-music] kick failed:', chrome.runtime.lastError.message);
    } else {
      console.log('[xiaoxu-music] kick result:', response);
    }
  });
})();

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

  chrome.runtime.sendMessage(
    { type: 'bridgeRequest', url: e.data.url, method: e.data.method },
    (response) => {
      console.log('[xiaoxu-music] sendMessage callback, lastError:', chrome.runtime.lastError?.message, 'response:', response);
      if (chrome.runtime.lastError) {
        window.postMessage({
          _bridgeFetchId: e.data._bridgeFetchId,
          error: chrome.runtime.lastError.message,
        }, '*');
        return;
      }

      window.postMessage({
        _bridgeFetchResponse: true,
        _bridgeFetchId: e.data._bridgeFetchId,
        ...response,
      }, '*');
    }
  );
});
