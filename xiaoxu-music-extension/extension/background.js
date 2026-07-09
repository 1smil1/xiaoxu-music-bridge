// background.js — Chrome extension service worker
// Connects to native messaging host and relays messages between content script and host.
//
// Single host connection multiplexes:
//   1. Request/response (getStatus, control, getLyrics, getCover)
//   2. Beat stream (subscribeBeat) — host pushes unsolicited beat messages
//
// One chrome.runtime.connectNative = one host.exe process. The previous design
// opened TWO connectNative calls (one for cmd/resp, one for beats), causing
// two host processes to race on port 17889 and split state. Merged into one.

const HOST_NAME = 'xiaoxu_music_host';
let host = null;          // single long-lived native messaging port
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
    console.warn('[xiaoxu-music] native host DISCONNECTED, lastError:', err?.message,
      'extension ID:', chrome.runtime.id);
    const wasBeatActive = beatPort !== null;
    host = null;
    for (const [id, { reject }] of pending) {
      pending.delete(id);
      reject(new Error(err?.message || 'Native host disconnected'));
    }
    // If a content script was subscribed to beats, the new host spawned on the
    // next sendToHost() call needs the subscribeBeat command re-sent, otherwise
    // AudioBeatService is never started on the new host and /health stays at
    // service_started: false. Defer to next tick so the disconnect handler
    // returns first; the subscription is idempotent on the host side.
    if (wasBeatActive) {
      setTimeout(() => {
        try { subscribeBeat(); } catch (e) {
          console.warn('[xiaoxu-music] post-disconnect resubscribe failed:', e.message);
        }
      }, 0);
    }
  });

  host.onMessage.addListener((message) => {
    // Route response messages to pending requests first.
    const id = message._id;
    if (id !== undefined && pending.has(id)) {
      const { resolve, reject } = pending.get(id);
      pending.delete(id);
      resolve(message);
      return;
    }

    // Unsolicited beat push (no _id) — forward to content-script beat port.
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

// ---- Beat stream management (shares the single host connection) ----

function subscribeBeat() {
  const h = connectHost();
  if (!h) return false;

  try {
    h.postMessage({ type: 'subscribeBeat' });
  } catch (e) {
    console.warn('[xiaoxu-music] subscribeBeat postMessage failed:', e.message);
    host = null;
    return false;
  }
  beatLastSubscribe = Date.now();
  return true;
}

// Periodic resubscribe to keep host alive (host auto-unsubscribes after 30s of no refresh)
setInterval(() => {
  if (beatPort && host && Date.now() - beatLastSubscribe > BEAT_RESUBSCRIBE_INTERVAL) {
    try {
      host.postMessage({ type: 'subscribeBeat' });
      beatLastSubscribe = Date.now();
    } catch (e) {
      console.warn('[xiaoxu-music] beat resubscribe failed:', e.message);
      host = null;
    }
  }
}, 5000);

// ---- Handle messages from content script ----

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  // Beat subscription management — content script connects a long-lived port
  if (message.type === 'BEAT_SUBSCRIBE') {
    // The actual Port is registered by chrome.runtime.onConnect (see below).
    // Do NOT overwrite beatPort with sender here — sender is a Sender
    // object (tab/frame metadata) which has no postMessage. We only kick
    // off the host subscription; the forward path uses the Port stored in
    // beatPort by the onConnect listener.
    console.log('[xiaoxu-music] BEAT_SUBSCRIBE from tab', sender.tab?.id);
    const ok = subscribeBeat();
    sendResponse({ ok });
    return false;
  }

  if (message.type === 'BEAT_UNSUBSCRIBE') {
    console.log('[xiaoxu-music] BEAT_UNSUBSCRIBE');
    if (host) {
      try { host.postMessage({ type: 'unsubscribeBeat' }); } catch (e) {}
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

// Detect content script disconnect — unsubscribe beat
chrome.runtime.onConnect.addListener((port) => {
  if (port.name === 'beat-port') {
    // Store the Port (not port.sender - Sender lacks postMessage)
    beatPort = port;
    port.onDisconnect.addListener(() => {
      console.log('[xiaoxu-music] beat port disconnected');
      if (host) {
        try { host.postMessage({ type: 'unsubscribeBeat' }); } catch (e) {}
      }
      beatPort = null;
    });
  }
});
