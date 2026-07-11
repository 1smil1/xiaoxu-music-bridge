// background.js — Chrome extension service worker
// Connects to native messaging host and relays messages between content script and host.
//
// Architecture:
//   - One chrome.runtime.connectNative = one host.exe process (multiplexed)
//   - One long-lived "xiaoxu-bridge" port per content script keeps the
//     MV3 service worker alive indefinitely — fixes the 30s-death-then-503 cycle
//     where Chrome kills idle workers, then content.js's sendMessage port closes
//     before a response comes back.
//
// The port carries:
//   - bridgeRequest (page fetch → host)  and bridgeResponse (host → page)
//   - BEAT_UPDATE push from host → all open ports (broadcast)

const HOST_NAME = 'xiaoxu_music_host';
let host = null;             // single long-lived native messaging port
let requestId = 0;
const pending = new Map();   // host request id → { resolve, reject } (raw host response)

const activePorts = new Set();  // all open "xiaoxu-bridge" ports

// Beat subscription is bound to port lifetime:
//   - first port connects → start AudioBeatService
//   - last port disconnects → stop AudioBeatService
//   - host reconnects (after disconnect) → re-subscribe if any port is open
let beatLastSubscribe = 0;
const BEAT_RESUBSCRIBE_INTERVAL = 20000; // 20s, before host's 30s auto-unsubscribe
let beatSubscriptionActive = false;

function ensureBeatSubscribed() {
  if (beatSubscriptionActive && host) return;
  const h = connectHost();
  if (!h) return;
  try {
    h.postMessage({ type: 'subscribeBeat' });
    beatLastSubscribe = Date.now();
    beatSubscriptionActive = true;
  } catch (e) {
    console.warn('[xiaoxu-music] subscribeBeat postMessage failed:', e.message);
    host = null;
    beatSubscriptionActive = false;
  }
}

// ---- Native host connection (single instance, multiplexed) ----

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
    host = null;
    beatSubscriptionActive = false;
    for (const [id, { reject }] of pending) {
      pending.delete(id);
      reject(new Error(err?.message || 'Native host disconnected'));
    }
    if (activePorts.size > 0) {
      setTimeout(() => { try { ensureBeatSubscribed(); } catch (e) {
        console.warn('[xiaoxu-music] post-disconnect resubscribe failed:', e.message);
      } }, 0);
    }
  });

  host.onMessage.addListener((message) => {
    // Route response messages to pending requests.
    const id = message._id;
    if (id !== undefined && pending.has(id)) {
      const { resolve } = pending.get(id);
      pending.delete(id);
      resolve(message);
      return;
    }

    // Unsolicited beat push (no _id) → broadcast to all open bridge ports.
    if (message.type === 'beat') {
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
      for (const p of activePorts) {
        try { p.postMessage(forward); } catch (e) { /* port probably closing */ }
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

    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id);
        reject(new Error('Native host response timeout'));
      }
    }, 10000);
  });
}

// Periodic resubscribe (host auto-unsubscribes after 30s of no refresh)
setInterval(() => {
  if (activePorts.size > 0 && host
      && Date.now() - beatLastSubscribe > BEAT_RESUBSCRIBE_INTERVAL) {
    try {
      host.postMessage({ type: 'subscribeBeat' });
      beatLastSubscribe = Date.now();
    } catch (e) {
      console.warn('[xiaoxu-music] beat resubscribe failed:', e.message);
      host = null;
      beatSubscriptionActive = false;
    }
  }
}, 5000);

// ---- Bridge request handling ----

async function handleBridgeRequest(port, fetchId, message) {
  const url = message.url || '';
  let pathname = '';
  try {
    pathname = new URL(url).pathname;
  } catch {
    try { port.postMessage({ _bridgeFetchId: fetchId, ok: false, status: 400, data: { error: 'bad url' } }); } catch (_) {}
    return;
  }

  function reply(payload) {
    try { port.postMessage({ _bridgeFetchId: fetchId, ...payload }); } catch (_) {}
  }

  if (pathname === '/cover/current') {
    chrome.storage.session.get('coverDataUrl', (result) => {
      reply({ ok: true, status: 200, data: { type: 'cover', dataUrl: result.coverDataUrl || null } });
    });
    return;
  }

  if (pathname === '/health') {
    reply({ ok: true, status: 200, data: { ok: true, name: 'xiaoxu-music-bridge-extension', version: '3.2.8' } });
    return;
  }

  if (pathname === '/state/current') {
    let statusResp, lyricsResp;
    try {
      [statusResp, lyricsResp] = await Promise.all([
        sendToHost({ type: 'getStatus' }),
        sendToHost({ type: 'getLyrics' }).catch((e) => ({ type: 'lyrics', found: false, error: e.message })),
      ]);
    } catch (err) {
      reply({ ok: false, status: 503, data: { error: err.message } });
      return;
    }

    const statusObj = {
      connected: !!statusResp.connected,
      source: statusResp.source ?? null,
      viaFallback: statusResp.viaFallback ?? false,
      title: statusResp.title ?? null,
      artist: statusResp.artist ?? null,
      album: statusResp.album ?? null,
      isPlaying: !!statusResp.isPlaying,
      positionMs: statusResp.positionMs ?? 0,
      durationMs: statusResp.durationMs ?? 0,
      updatedAt: statusResp.updatedAt ?? null,
    };

    let coverDataUrl = null;
    // QQ Music's native GSMTC thumbnail is usually null (CEF doesn't expose it to
    // Windows.Media), but our HandleGetCover Tier 2 will try the QQ Music search API
    // when source=QQMusic. So call getCover for QQ Music regardless of hasCover.
    if (statusResp.hasCover || statusResp.source === 'QQMusic') {
      try {
        const cover = await sendToHost({ type: 'getCover' });
        if (cover && cover.data) {
          coverDataUrl = `data:${cover.contentType};base64,${cover.data}`;
          chrome.storage.session.set({ coverDataUrl });
        } else {
          chrome.storage.session.set({ coverDataUrl: null });
        }
      } catch (e) { /* keep cached */ }
    } else {
      chrome.storage.session.set({ coverDataUrl: null });
    }

    const signature = [statusObj.source, statusObj.title, statusObj.artist, statusObj.album]
      .map((x) => (x == null ? '' : String(x))).join('|');

    reply({
      ok: true,
      status: 200,
      data: {
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
      },
    });
    return;
  }

  let hostMessage;
  if (pathname === '/status' || pathname === '/api/status') {
    hostMessage = { type: 'getStatus' };
  } else if (pathname.startsWith('/control/')) {
    const cmd = pathname.split('/').pop();
    hostMessage = { type: 'control', command: cmd };
  } else if (pathname === '/lyrics/current') {
    hostMessage = { type: 'getLyrics' };
  } else if (pathname === '/beat/current') {
    hostMessage = { type: 'getBeat' };
  } else {
    reply({ ok: false, status: 404, data: {} });
    return;
  }

  try {
    const response = await sendToHost(hostMessage);
    if (response.type === 'status' && (response.hasCover || response.source === 'QQMusic')) {
      const cover = await sendToHost({ type: 'getCover' });
      if (cover.data) {
        const dataUrl = `data:${cover.contentType};base64,${cover.data}`;
        chrome.storage.session.set({ coverDataUrl: dataUrl });
        response.coverUrl = 'cover:ready';
      } else {
        chrome.storage.session.set({ coverDataUrl: null });
        response.coverUrl = null;
      }
    }
    if (response.type === 'status' && !response.hasCover) {
      chrome.storage.session.set({ coverDataUrl: null });
    }
    const ok = response.type !== 'error';
    reply({ ok, status: ok ? 200 : 503, data: response });
  } catch (err) {
    reply({ ok: false, status: 503, data: { error: err.message } });
  }
}

// ---- Single port per content script: "xiaoxu-bridge" ----

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== 'xiaoxu-bridge') return;

  activePorts.add(port);
  console.log('[xiaoxu-music] bridge port connected, total:', activePorts.size);

  // Start beat as soon as any port is up; stop when the last one disconnects.
  ensureBeatSubscribed();

  port.onDisconnect.addListener(() => {
    activePorts.delete(port);
    console.log('[xiaoxu-music] bridge port disconnected, remaining:', activePorts.size);
    if (activePorts.size === 0 && host) {
      try { host.postMessage({ type: 'unsubscribeBeat' }); } catch (e) {}
      beatSubscriptionActive = false;
    }
  });

  port.onMessage.addListener((message) => {
    if (!message || message.type !== 'bridgeRequest') return;
    const fetchId = message._bridgeFetchId;
    handleBridgeRequest(port, fetchId, message).catch((e) => {
      try {
        port.postMessage({ _bridgeFetchId: fetchId, ok: false, status: 503, data: { error: e.message } });
      } catch (_) {}
    });
  });
});
