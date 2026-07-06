// content.js — Lifecycle hook for xiaoxu-music-host.exe
// Runs at document_start on xiaoxu.xin pages.
// Tells the background SW to start the host on load, stop on unload.
// All music data flows over HTTP directly to localhost:17888 — no message relay.

try { chrome.runtime.sendMessage({ type: 'startHost' }); } catch (e) {}

window.addEventListener('beforeunload', () => {
  try { chrome.runtime.sendMessage({ type: 'stopHost' }); } catch (e) {}
});
