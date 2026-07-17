(function (root, factory) {
  const api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.XiaoxuHostBootstrap = api;
})(typeof self !== 'undefined' ? self : globalThis, function () {
  function createHostEnsurer({ probe, launch }) {
    let pending = null;
    return function ensureHost() {
      if (pending) return pending;
      pending = (async () => {
        if (await probe()) return { ok: true, source: 'existing' };
        const result = await launch();
        if (!result || result.ok !== true) {
          return { ok: false, source: 'launch', error: result?.error || 'Host launch failed' };
        }
        return { ok: true, source: 'launched' };
      })().finally(() => { pending = null; });
      return pending;
    };
  }

  return { createHostEnsurer };
});
