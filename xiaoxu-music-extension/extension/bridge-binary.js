(function exposeBridgeBinary(root) {
  function encodeBridgeBinary(bytes, contentType) {
    const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    let binary = '';
    const chunkSize = 0x8000;
    for (let offset = 0; offset < view.length; offset += chunkSize) {
      binary += String.fromCharCode(...view.subarray(offset, offset + chunkSize));
    }
    return {
      __bridgeBinary: true,
      contentType: contentType || 'application/octet-stream',
      base64: btoa(binary),
    };
  }

  const api = { encodeBridgeBinary };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.XiaoxuBridgeBinary = api;
})(typeof self !== 'undefined' ? self : globalThis);
