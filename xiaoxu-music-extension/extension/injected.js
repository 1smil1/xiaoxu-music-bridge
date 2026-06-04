// injected.js — Page-level fetch interceptor
// Loaded by content.js via script.src into the PAGE context.
// Routes requests to http://127.0.0.1:17888 through the extension.

(function () {
  const BRIDGE_BASE = 'http://127.0.0.1:17888';
  const originalFetch = window.fetch;

  console.log('[xiaoxu-music] injected.js loaded, fetch patched');

  window.fetch = function (url, options) {
    const urlStr = typeof url === 'string' ? url : (url instanceof Request ? url.url : String(url));

    if (urlStr.startsWith(BRIDGE_BASE)) {
      return new Promise((resolve, reject) => {
        const id = Math.random().toString(36).slice(2) + Date.now().toString(36);
        const handler = (e) => {
          if (e.data && e.data._bridgeFetchResponse && e.data._bridgeFetchId === id) {
            window.removeEventListener('message', handler);
            if (e.data.error) {
              reject(new TypeError(e.data.error));
            } else {
              const respBody = typeof e.data.data === 'string' ? e.data.data : JSON.stringify(e.data.data);
              const respStatus = e.data.status || 200;
              resolve(new Response(respBody, {
                status: respStatus,
                headers: { 'Content-Type': 'application/json' }
              }));
            }
          }
        };
        window.addEventListener('message', handler);

        window.postMessage({
          type: 'BRIDGE_FETCH',
          _bridgeFetchId: id,
          url: urlStr,
          method: options?.method || 'GET',
        }, '*');
      });
    }

    return originalFetch.apply(this, arguments);
  };
})();
