// background.js — Chrome extension service worker
// Connects to native messaging host and relays messages between content script and host.
//
// Two host channels:
//   1. Request/response (getStatus, control, getLyrics, getCover) — long-lived port
//   2. Beat stream (subscribeBeat) — host pushes unsolicited beat messages

const HOST_NAME = 'xiaoxu_music_host';
let host = null;          // request/response port (long-lived)
let beatHost = null;      // beat stream port (separate, allows async push)
let requestId = 0;
const pending = new Map();

// ---- Beat fan-out state ----
let beatPort = null;            // current content-script port subscribed to beats
let beatLastSubscribe = 0;      // timestamp of last subscribeBeat (for refresh)
const BEAT_RESUBSCRIBE_INTERVAL = 20000; // 20s — refresh before host auto-unsubscribe (30s)

function connectHost() {
  if (host) return host;

  try {
    host = chrome.runtime.connectNative(HOST_NAME);
    console.log('[xiaoxu-music] connectNative SUCCESS, extension ID:', chrome.runtime.id);
  } catch (e) {
    const err = chrome.runtime.lastError;
    console.error('[xiaoxu-music] connectNative THROW, lastError:', err?.message, 'exception:', e.message,
      'extension ID:', chrome.runtime.id);
    return null;
  }

  host.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError;
    console.error('[xiaoxu-music] native host DISCONNECTED immediately, lastError:', err?.message,
      'extension ID:', chrome.runtime.id);
    host = null;
    for (const [id, { reject }] of pending) {
      pending.delete(id);
      reject(new Error(err?.message || 'Native host disconnected'));
    }
  });

  host.onMessage.addListener((message) => {
    const id = message._id;
    if (id !== undefined && pending.has(id)) {
      const { resolve, reject } = pending.get(id);
      pending.delete(id);
      resolve(message);
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

// ---- Beat stream management ----

function connectBeatHost() {
  if (beatHost) return beatHost;

  try {
    beatHost = chrome.runtime.connectNative(HOST_NAME);
    console.log('[xiaoxu-music] beat host connectNative SUCCESS');
  } catch (e) {
    const err = chrome.runtime.lastError;
    console.error('[xiaoxu-music] beat host connectNative FAILED:', err?.message);
    beatHost = null;
    return null;
  }

  beatHost.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError;
    console.warn('[xiaoxu-music] beat host disconnected:', err?.message);
    beatHost = null;
    // Will reconnect on next subscribeBeat
  });

  beatHost.onMessage.addListener((message) => {
    // Route unsolicited beat messages to content script.
    // Forward the full v3 payload (bands/features/onsets/rhythm/state) when present;
    // fall back to legacy bass/volume/pulse/glow fields otherwise.
    if (message.type === 'beat' && beatPort) {
      try {
        const forward = {
          type: 'BEAT_UPDATE',
          bass: message.bass ?? 0,
          volume: message.volume ?? 0,
          pulse: message.pulse ?? 0,
          glow: message.glow ?? 0,
        };
        if (message.bands) forward.bands = message.bands;
        if (message.features) forward.features = message.features;
        if (message.onsets) forward.onsets = message.onsets;
        if (message.rhythm) forward.rhythm = message.rhythm;
        if (message.state) forward.state = message.state;
        if (typeof message.ts === 'number') forward.ts = message.ts;
        beatPort.postMessage(forward);
      } catch (e) {
        console.warn('[xiaoxu-music] beat port post failed:', e.message);
        beatPort = null;
      }
    }
  });

  return beatHost;
}

function subscribeBeat() {
  const h = connectBeatHost();
  if (!h) return false;

  h.postMessage({ type: 'subscribeBeat' });
  beatLastSubscribe = Date.now();
  return true;
}

// Periodic resubscribe to keep host alive (host auto-unsubscribes after 30s of no refresh)
setInterval(() => {
  if (beatPort && beatHost && Date.now() - beatLastSubscribe > BEAT_RESUBSCRIBE_INTERVAL) {
    try {
      beatHost.postMessage({ type: 'subscribeBeat' });
      beatLastSubscribe = Date.now();
    } catch (e) {
      console.warn('[xiaoxu-music] beat resubscribe failed:', e.message);
      beatHost = null;
    }
  }
}, 5000);

// ---- Handle messages from content script ----

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  // Beat subscription management — content script connects a long-lived port
  if (message.type === 'BEAT_SUBSCRIBE') {
    // Reuse existing port if same sender, else replace
    beatPort = sender;
    console.log('[xiaoxu-music] BEAT_SUBSCRIBE from tab', sender.tab?.id);
    const ok = subscribeBeat();
    sendResponse({ ok });
    return false;
  }

  if (message.type === 'BEAT_UNSUBSCRIBE') {
    console.log('[xiaoxu-music] BEAT_UNSUBSCRIBE');
    if (beatHost) {
      try { beatHost.postMessage({ type: 'unsubscribeBeat' }); } catch (e) {}
    }
    beatPort = null;
    return false;
  }

  if (message.type !== 'bridgeRequest') return false;

  const url = message.url || '';
  let pathname = '';
  try {
    pathname = new URL(url).pathname;
  } catch {
    sendResponse({ ok: false, status: 400, data: { error: 'bad url' } });
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
    // Cover cached in session storage — return dataUrl directly as object field
    chrome.storage.session.get('coverDataUrl', (result) => {
      sendResponse({ ok: true, status: 200, data: { type: 'cover', dataUrl: result.coverDataUrl || null } });
    });
    return true;
  } else if (pathname === '/beat/current') {
    // Synchronous beat snapshot (no subscription needed)
    sendToHost({ type: 'getBeat' })
      .then((response) => sendResponse({ ok: true, status: 200, data: response }))
      .catch((err) => sendResponse({ ok: false, status: 503, data: { error: err.message } }));
    return true;
  } else if (pathname === '/state/current') {
    // Combined status + lyrics + cover. Page polls this every 2s.
    Promise.all([
      sendToHost({ type: 'getStatus' }),
      sendToHost({ type: 'getLyrics' }).catch((e) => ({ type: 'lyrics', found: false, error: e.message })),
    ]).then(async ([statusResp, lyricsResp]) => {
      const statusObj = {
        connected: !!statusResp.connected,
        source: statusResp.source ?? null,
        title: statusResp.title ?? null,
        artist: statusResp.artist ?? null,
        album: statusResp.album ?? null,
        isPlaying: !!statusResp.isPlaying,
        positionMs: statusResp.positionMs ?? 0,
        durationMs: statusResp.durationMs ?? 0,
        updatedAt: statusResp.updatedAt ?? null,
      };

      // Fetch cover if status indicates one exists.
      let coverDataUrl = null;
      if (statusResp.hasCover) {
        try {
          const cover = await sendToHost({ type: 'getCover' });
          if (cover && cover.data) {
            coverDataUrl = `data:${cover.contentType};base64,${cover.data}`;
            chrome.storage.session.set({ coverDataUrl });
          } else {
            chrome.storage.session.set({ coverDataUrl: null });
          }
        } catch (e) {
          // ignore — keep cached
        }
      } else {
        chrome.storage.session.set({ coverDataUrl: null });
      }

      const signature = [statusObj.source, statusObj.title, statusObj.artist, statusObj.album]
        .map((x) => (x == null ? '' : String(x))).join('|');

      const data = {
        signature,
        status: statusObj,
        lyrics: lyricsResp && lyricsResp.found
          ? {
              found: true,
              title: lyricsResp.title ?? null,
              artist: lyricsResp.artist ?? null,
              fileName: lyricsResp.fileName ?? null,
              lrc: lyricsResp.lrc ?? null,
              source: lyricsResp.source ?? null,
              synced: !!lyricsResp.synced,
            }
          : { found: false },
        coverDataUrl,
        cached: false,
      };
      sendResponse({ ok: true, status: 200, data });
    }).catch((err) => {
      sendResponse({ ok: false, status: 503, data: { error: err.message } });
    });
    return true;
  } else if (pathname === '/health') {
    sendResponse({ ok: true, status: 200, data: { ok: true, name: 'xiaoxu-music-bridge-extension', version: '3.0.0' } });
    return false;
  } else {
    sendResponse({ ok: false, status: 404, data: {} });
    return false;
  }

  sendToHost(hostMessage)
    .then((response) => {
      if (response.type === 'status' && response.hasCover) {
        return sendToHost({ type: 'getCover' }).then((cover) => {
          if (cover.data) {
            const dataUrl = `data:${cover.contentType};base64,${cover.data}`;
            chrome.storage.session.set({ coverDataUrl: dataUrl });
            response.coverUrl = 'cover:ready';
          } else {
            chrome.storage.session.set({ coverDataUrl: null });
            response.coverUrl = null;
          }
          sendResponse({ ok: true, status: 200, data: response });
        });
      }
      if (response.type === 'status' && !response.hasCover) {
        chrome.storage.session.set({ coverDataUrl: null });
      }
      const ok = response.type !== 'error';
      sendResponse({ ok, status: ok ? 200 : 503, data: response });
    })
    .catch((err) => {
      sendResponse({ ok: false, status: 503, data: { error: err.message } });
    });

  return true;
});

// Detect content script disconnect → unsubscribe beat
chrome.runtime.onConnect.addListener((port) => {
  if (port.name === 'beat-port') {
    beatPort = port.sender;
    port.onDisconnect.addListener(() => {
      console.log('[xiaoxu-music] beat port disconnected');
      if (beatHost) {
        try { beatHost.postMessage({ type: 'unsubscribeBeat' }); } catch (e) {}
      }
      beatPort = null;
    });
  }
});
