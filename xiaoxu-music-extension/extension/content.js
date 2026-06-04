// content.js — Bridge between page (injected.js) and background (background.js)
// Injected into xiaoxu.xin pages. Runs in content script world (has chrome.runtime access).

console.log('[xiaoxu-music] content script loaded, url:', location.href);

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
        _bridgeFetchId: e.data._bridgeFetchId,
        ...response,
      }, '*');
    }
  );
});
