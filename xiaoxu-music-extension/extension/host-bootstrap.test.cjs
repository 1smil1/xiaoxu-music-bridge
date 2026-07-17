const test = require('node:test');
const assert = require('node:assert/strict');
const { createHostEnsurer } = require('./host-bootstrap.js');

test('healthy local server does not launch native messaging', async () => {
  let launches = 0;
  const ensureHost = createHostEnsurer({
    probe: async () => true,
    launch: async () => { launches += 1; return { ok: true }; },
  });

  assert.deepEqual(await ensureHost(), { ok: true, source: 'existing' });
  assert.equal(launches, 0);
});

test('missing local server launches it once', async () => {
  let launches = 0;
  const ensureHost = createHostEnsurer({
    probe: async () => false,
    launch: async () => { launches += 1; return { ok: true }; },
  });

  assert.deepEqual(await ensureHost(), { ok: true, source: 'launched' });
  assert.equal(launches, 1);
});

test('concurrent page connections share one launch attempt', async () => {
  let launches = 0;
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  const ensureHost = createHostEnsurer({
    probe: async () => false,
    launch: async () => { launches += 1; await gate; return { ok: true }; },
  });

  const first = ensureHost();
  const second = ensureHost();
  release();
  await Promise.all([first, second]);
  assert.equal(launches, 1);
});
