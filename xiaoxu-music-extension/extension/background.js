// background.js — Chrome extension service worker
// Connects to native messaging host and relays messages between content script and host.

const HOST_NAME = 'xiaoxu_music_host';
let host = null;
let requestId = 0;
const pending = new Map();

function connectHost() {
  if (host) return host;

  try {
    host = chrome.runtime.connectNative(HOST_NAME);
  } catch (e) {
    console.error('[xiaoxu-music] Failed to connect native host:', e);
    return null;
  }

  host.onMessage.addListener((message) => {
    const id = message._id;
    if (id !== undefined && pending.has(id)) {
      const { resolve, reject } = pending.get(id);
      pending.delete(id);
      resolve(message);
    }
  });

  host.onDisconnect.addListener(() => {
    console.warn('[xiaoxu-music] Native host disconnected');
    host = null;
    for (const [id, { reject }] of pending) {
      pending.delete(id);
      reject(new Error('Native host disconnected'));
    }
  });

  return host;
}

function sendToHost(message) {
  return new Promise((resolve, reject) => {
    const h = connectHost();
    if (!h) {
      reject(new Error('Cannot connect to native host'));
      return;
    }

    const id = ++requestId;
    pending.set(id, { resolve, reject });
    h.postMessage({ ...message, _id: id });

    // Timeout after 10s
    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id);
        reject(new Error('Native host response timeout'));
      }
    }, 10000);
  });
}

// Handle messages from content script
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type !== 'bridgeRequest') return false;

  const url = message.url || '';
  let pathname = '';
  try {
    pathname = new URL(url).pathname;
  } catch {
    sendResponse({ ok: false, status: 400, body: '{}' });
    return false;
  }

  let hostMessage;

  if (pathname === '/status' || pathname === '/api/status') {
    hostMessage = { type: 'getStatus' };
  } else if (pathname.startsWith('/control/')) {
    const cmd = pathname.split('/').pop();
    hostMessage = { type: 'control', command: cmd };
  } else if (pathname === '/lyrics/current') {
    hostMessage = { type: 'getLyrics' };
  } else if (pathname === '/cover/current') {
    hostMessage = { type: 'getCover' };
  } else if (pathname === '/health') {
    sendResponse({ ok: true, status: 200, body: JSON.stringify({ ok: true, name: 'xiaoxu-music-bridge-extension', version: '1.0.0' }) });
    return false;
  } else {
    sendResponse({ ok: false, status: 404, body: '{}' });
    return false;
  }

  sendToHost(hostMessage)
    .then((response) => {
      // If status response has a coverUrl, we need to also fetch the cover
      // and convert it to a data URL (since there's no HTTP server anymore).
      if (response.type === 'status' && response.coverUrl) {
        return sendToHost({ type: 'getCover' }).then((cover) => {
          if (cover.data) {
            response.coverUrl = `data:${cover.contentType};base64,${cover.data}`;
          } else {
            response.coverUrl = null;
          }
          sendResponse({ ok: true, status: 200, body: JSON.stringify(response) });
        });
      }

      const body = JSON.stringify(response);
      const ok = response.type !== 'error';
      sendResponse({ ok, status: ok ? 200 : 503, body });
    })
    .catch((err) => {
      sendResponse({ ok: false, status: 503, body: JSON.stringify({ error: err.message }) });
    });

  return true; // async sendResponse
});
