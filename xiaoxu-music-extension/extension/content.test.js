const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function plain(value) {
  return JSON.parse(JSON.stringify(value));
}

function loadContentScript(sendMessage) {
  const listeners = {};
  const postedMessages = [];
  const context = {
    console: { log() {}, warn() {}, error() {} },
    location: { href: 'https://xiaoxu.xin/dashboard' },
    chrome: {
      runtime: {
        getURL: (file) => `chrome-extension://id/${file}`,
        sendMessage,
        lastError: null,
      },
    },
    document: {
      createElement: () => ({ remove() {} }),
      head: { appendChild() {} },
      documentElement: { appendChild() {} },
    },
    window: {
      addEventListener(type, listener) { listeners[type] = listener; },
      postMessage(message) { postedMessages.push(message); },
    },
  };
  const code = fs.readFileSync(path.join(__dirname, 'content.js'), 'utf8');
  vm.runInNewContext(code, context, { filename: 'content.js' });
  return { listeners, postedMessages, pageWindow: context.window, runtime: context.chrome.runtime };
}

test('returns an error response when the extension runtime context is invalidated', () => {
  const { listeners, postedMessages, pageWindow } = loadContentScript(() => {
    throw new Error('Extension context invalidated.');
  });

  listeners.message({
    source: pageWindow,
    data: {
      type: 'BRIDGE_FETCH',
      _bridgeFetchId: 'req-1',
      url: 'http://127.0.0.1:17888/status',
      method: 'GET',
    },
  });

  assert.deepEqual(plain(postedMessages), [
    {
      _bridgeFetchResponse: true,
      _bridgeFetchId: 'req-1',
      ok: false,
      status: 503,
      error: 'Extension context invalidated.',
      data: { error: 'Extension context invalidated.' },
    },
  ]);
});

test('returns an error response when chrome.runtime.lastError is set', () => {
  let callback;
  const { listeners, postedMessages, pageWindow, runtime } = loadContentScript((message, cb) => {
    callback = cb;
  });

  listeners.message({
    source: pageWindow,
    data: {
      type: 'BRIDGE_FETCH',
      _bridgeFetchId: 'req-2',
      url: 'http://127.0.0.1:17888/status',
      method: 'GET',
    },
  });

  runtime.lastError = { message: 'Native host disconnected' };
  assert.equal(typeof callback, 'function');
  callback(undefined);

  assert.deepEqual(plain(postedMessages), [
    {
      _bridgeFetchResponse: true,
      _bridgeFetchId: 'req-2',
      ok: false,
      status: 503,
      error: 'Native host disconnected',
      data: { error: 'Native host disconnected' },
    },
  ]);
});
