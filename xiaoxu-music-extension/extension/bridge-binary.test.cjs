const assert = require('node:assert/strict');
const test = require('node:test');

const { encodeBridgeBinary } = require('./bridge-binary.js');

test('encodes image bytes without converting the payload to JSON text', () => {
  const encoded = encodeBridgeBinary(Uint8Array.from([0xff, 0xd8, 0xff]), 'image/jpeg');
  assert.deepEqual(encoded, {
    __bridgeBinary: true,
    contentType: 'image/jpeg',
    base64: '/9j/',
  });
});
