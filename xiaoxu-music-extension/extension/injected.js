// injected.js — Page-level fetch interceptor
// Loaded by content.js via script.src into the PAGE context.
// Routes requests to http://127.0.0.1:17888 through the extension.

(function () {
  // Accept either hostname — pages historically used either one.
  const BRIDGE_PATTERNS = [
    /^https?:\/\/127\.0\.0\.1:17888\b/,
    /^https?:\/\/localhost:17888\b/,
  ];
  const originalFetch = window.fetch;

  console.log('[xiaoxu-music] injected.js loaded, fetch patched');

  window.fetch = function (url, options) {
    const urlStr = typeof url === 'string' ? url : (url instanceof Request ? url.url : String(url));

    if (BRIDGE_PATTERNS.some((re) => re.test(urlStr))) {
      return new Promise((resolve, reject) => {
        const id = Math.random().toString(36).slice(2) + Date.now().toString(36);
        const handler = (e) => {
          if (e.data && e.data._bridgeFetchResponse && e.data._bridgeFetchId === id) {
            window.removeEventListener('message', handler);
            if (e.data.error) {
              reject(new TypeError(e.data.error));
            } else {
              const binaryPayload = e.data.data && e.data.data.__bridgeBinary === true
                ? e.data.data
                : null;
              let respBody;
              let contentType = 'application/json';
              if (binaryPayload) {
                const binary = atob(binaryPayload.base64 || '');
                const bytes = new Uint8Array(binary.length);
                for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i);
                respBody = bytes;
                contentType = binaryPayload.contentType || 'application/octet-stream';
              } else {
                respBody = typeof e.data.data === 'string' ? e.data.data : JSON.stringify(e.data.data);
              }
              const respStatus = e.data.status || 200;
              resolve(new Response(respBody, {
                status: respStatus,
                headers: { 'Content-Type': contentType }
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
          body: typeof options?.body === 'string' ? options.body : null,
        }, '*');
      });
    }

    return originalFetch.apply(this, arguments);
  };
})();
