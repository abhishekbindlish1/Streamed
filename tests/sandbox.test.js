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
  assert.ok(streams.length > 0 && streams.every(stream => stream.embedUrl.startsWith(base + '/media/')));
  const video = await fetch(streams[0].embedUrl, { headers: { Range: 'bytes=0-31' } });
  assert.equal(video.status, 206);
  assert.equal((await video.arrayBuffer()).byteLength, 32);
  assert.match(video.headers.get('content-range'), /^bytes 0-31\//);
  assert.equal((await fetch(base + '/media/native-test.mp4', { headers: { Range: 'bytes=999999999-' } })).status, 416);
  assert.match(await (await fetch(base + '/media/native-test.m3u8')).text(), /#EXTM3U/);
  assert.equal((await (await fetch(base + '/v1/config')).json()).useBackendStreamProxy, false);
  assert.match(await (await fetch(base + '/')).text(), /demoNotice/);
  assert.equal((await fetch(base + '/.env.local')).status, 404);
  assert.equal((await fetch(base + '/server.js')).status, 404);
  assert.equal((await fetch(base + '/v1/auth/pair/init', { method: 'POST' })).status, 404);
});

test('pairing fixture is explicit, remains pending until completion, and never creates a production session', async t => {
  const server = await createSandbox({ pairing: true });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const base = `http://127.0.0.1:${server.address().port}`;
  await fetch(base + '/__test/pair/fail-next-init', { method: 'POST' });
  assert.equal((await fetch(base + '/v1/auth/pair/init', { method: 'POST' })).status, 503);
  const init = await (await fetch(base + '/v1/auth/pair/init', { method: 'POST' })).json();
  const poll = () => fetch(base + '/v1/auth/pair/poll?token=' + encodeURIComponent(init.pollToken)).then(r => r.json());
  assert.equal(init.code, '123456');
  assert.equal((await poll()).status, 'pending');
  await fetch(base + '/__test/pair/complete', { method: 'POST' });
  assert.deepEqual(await poll(), { status: 'complete', jwt: 'sandbox-fixture-session-not-a-production-jwt' });
});
