// background.js — Chrome MV3 service worker
// Lifecycle manager for xiaoxu-music-host.exe.
// The page fetches http://localhost:17888/state/current directly over HTTP;
// this SW only spawns the host on page-load and stops it on page-close.

const HOST_NAME = 'xiaoxu_music_host';
let hostPort = null;
let keepAlive = null;

function startHost() {
  if (hostPort) return true;

  try {
    hostPort = chrome.runtime.connectNative(HOST_NAME);
  } catch (e) {
    hostPort = null;
    return false;
  }

  hostPort.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError;
    console.warn('[xiaoxu-music] native host disconnected:', err?.message || 'unknown');
    hostPort = null;
    if (keepAlive) { clearInterval(keepAlive); keepAlive = null; }
  });

  // 25s ping: keeps SW alive (MV3 30s timeout) + detects host crashes.
  keepAlive = setInterval(() => {
    if (!hostPort) return;
    try {
      hostPort.postMessage({ type: 'ping' });
    } catch (e) {
      hostPort = null;
      clearInterval(keepAlive);
      keepAlive = null;
    }
  }, 25000);

  return true;
}

function stopHost() {
  if (keepAlive) { clearInterval(keepAlive); keepAlive = null; }
  if (hostPort) {
    try { hostPort.disconnect(); } catch (e) {}
    hostPort = null;
  }
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg?.type === 'startHost') {
    // startHost is synchronous (connectNative returns immediately); call directly.
    const ok = startHost();
    sendResponse({ ok });
    return false;
  }
  if (msg?.type === 'stopHost') {
    stopHost();
    sendResponse({ ok: true });
    return false;
  }
  return false;
});

// On SW startup (e.g. after Chrome restart), if any xiaoxu.xin tab is already open,
// re-establish the host. chrome.tabs.query is async → use sendResponse asynchronously.
chrome.runtime.onStartup.addListener(() => {
  chrome.tabs.query({ url: '*://xiaoxu.xin/*' }, (tabs) => {
    if (tabs && tabs.length > 0) {
      startHost();
    }
  });
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.tabs.query({ url: '*://xiaoxu.xin/*' }, (tabs) => {
    if (tabs && tabs.length > 0) {
      startHost();
    }
  });
});
