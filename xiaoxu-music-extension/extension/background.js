// Service worker: starts the persistent local server when xiaoxu.xin opens.
// All media, lyric, cover, control and audio-frame traffic goes directly from
// the page to http://localhost:17888.

importScripts('host-bootstrap.js');

const NATIVE_HOST = 'xiaoxu_music_host';
const HEALTH_URL = 'http://localhost:17888/health';
const activePorts = new Set();
let healthTimer = null;

async function probeHost() {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 800);
  try {
    const response = await fetch(HEALTH_URL, { cache: 'no-store', signal: controller.signal });
    return response.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timeout);
  }
}

function launchHost() {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(NATIVE_HOST, { type: 'ensureServer' }, (response) => {
      const error = chrome.runtime.lastError;
      if (error) resolve({ ok: false, error: error.message });
      else resolve(response || { ok: false, error: 'Native launcher returned no response' });
    });
  });
}

const ensureHost = self.XiaoxuHostBootstrap.createHostEnsurer({ probe: probeHost, launch: launchHost });

async function checkHost() {
  const result = await ensureHost();
  for (const port of activePorts) {
    try { port.postMessage({ type: 'HOST_STATUS', ...result }); } catch { }
  }
}

function updateHealthTimer() {
  if (activePorts.size > 0 && healthTimer == null) {
    healthTimer = setInterval(() => { void checkHost(); }, 60000);
  } else if (activePorts.size === 0 && healthTimer != null) {
    clearInterval(healthTimer);
    healthTimer = null;
  }
}

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== 'xiaoxu-host-launcher') return;
  activePorts.add(port);
  updateHealthTimer();
  void checkHost();

  port.onDisconnect.addListener(() => {
    activePorts.delete(port);
    updateHealthTimer();
  });
});
