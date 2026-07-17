const assert = require('node:assert/strict');
const test = require('node:test');

const { reconnectDelayMs } = require('./reconnect-policy.js');

test('native host reconnect uses capped exponential backoff', () => {
  assert.deepEqual(
    [0, 1, 2, 3, 4, 5, 20].map(reconnectDelayMs),
    [250, 500, 1000, 2000, 5000, 5000, 5000],
  );
});

test('invalid attempts start at the minimum delay', () => {
  assert.equal(reconnectDelayMs(-1), 250);
  assert.equal(reconnectDelayMs(Number.NaN), 250);
});
