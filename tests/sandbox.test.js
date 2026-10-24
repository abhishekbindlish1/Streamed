const assert = require('node:assert/strict');
const { test } = require('node:test');
const { createSandbox } = require('../scripts/sandbox');

test('sandbox serves the browser and native-compatible fixture APIs without credentials', async t => {
  const server = await createSandbox();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const base = `http://127.0.0.1:${server.address().port}`;
  assert.equal((await (await fetch(base + '/health')).json()).mode, 'sandbox');
  assert.equal((await (await fetch(base + '/api/sports')).json()).length, 4);
  const matches = await (await fetch(base + '/api/matches/football')).json();
  assert.equal(matches.length, 3);
  assert.equal(matches[0].category, 'football');
  const streams = await (await fetch(base + `/api/stream/demo/${matches[0].id}`)).json();
  assert.ok(streams.length > 0 && streams.every(stream => /^https:\/\//.test(stream.embedUrl)));
  assert.equal((await (await fetch(base + '/v1/config')).json()).useBackendStreamProxy, false);
  assert.match(await (await fetch(base + '/')).text(), /demoNotice/);
  assert.equal((await fetch(base + '/.env.local')).status, 404);
  assert.equal((await fetch(base + '/server.js')).status, 404);
  assert.equal((await fetch(base + '/v1/auth/pair/init', { method: 'POST' })).status, 404);
});
