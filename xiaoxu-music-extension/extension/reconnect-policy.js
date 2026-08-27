(function exposeReconnectPolicy(root) {
  const delays = [250, 500, 1000, 2000, 5000];

  function reconnectDelayMs(attempt) {
    const index = Number.isFinite(attempt) ? Math.max(0, Math.floor(attempt)) : 0;
    return delays[Math.min(index, delays.length - 1)];
  }

  const policy = { reconnectDelayMs };
  if (typeof module !== 'undefined' && module.exports) module.exports = policy;
  else root.XiaoxuReconnectPolicy = policy;
})(typeof self !== 'undefined' ? self : globalThis);
