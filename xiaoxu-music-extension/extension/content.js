// Keeps a lightweight extension connection while xiaoxu.xin is open. The
// service worker uses it only to ensure the persistent local Host is running.

let port = null;
let reconnectTimer = null;
let reconnectAttempt = 0;

function connect() {
  if (port) return;
  try {
    port = chrome.runtime.connect({ name: 'xiaoxu-host-launcher' });
    reconnectAttempt = 0;
  } catch {
    scheduleReconnect();
    return;
  }

  port.onMessage.addListener((message) => {
    if (message?.type === 'HOST_STATUS') {
      window.postMessage({ type: 'XIAOXU_HOST_STATUS', ...message }, '*');
    }
  });

  port.onDisconnect.addListener(() => {
    port = null;
    scheduleReconnect();
  });
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  const delay = self.XiaoxuReconnectPolicy.reconnectDelayMs(reconnectAttempt++);
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, delay);
}

connect();
