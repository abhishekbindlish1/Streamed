const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const vm = require('node:vm');
const { buildWebos } = require('../apps/webos/scripts/build-webos');

test('webOS staging supports file origins and contains only public demo assets', async t => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'streamed-webos-test-'));
  t.after(() => fs.rm(temp, { recursive: true, force: true }));
  const result = await buildWebos({ outputRoot: temp, commit: 'webos-test', releaseTag: '', package: false });
  assert.equal(result.manifest.disableBackHistoryAPI, true);
  assert.equal(result.manifest.id, 'com.streamed.tv.webos.preview');
  const html = await fs.readFile(path.join(result.app, 'index.html'), 'utf8');
  assert.ok(!html.includes('$WEBAPIS'));
  assert.ok(!html.includes('src="https://'));
  for (const [, name] of html.matchAll(/(?:src|href)="([^"]+)"/g)) {
    assert.ok((await fs.stat(path.join(result.app, name))).isFile(), name);
  }
  const context = { window: {} };
  vm.runInNewContext(await fs.readFile(path.join(result.app, 'data/config.local.js'), 'utf8'), context);
  vm.runInNewContext(await fs.readFile(path.join(result.app, 'data/catalog.js'), 'utf8'), context);
  assert.equal(context.window.STREAMED_CONFIG.DEMO_MODE, true);
  for (const match of context.window.STREAMED_DEMO_CATALOG.matches) {
    assert.equal(match.streams[0].url, 'media/native-test.mp4');
    assert.ok((await fs.stat(path.join(result.app, match.streams[0].url))).size > 100000);
  }
  assert.equal(JSON.parse(await fs.readFile(path.join(result.app, 'BUILD.json'))).commit, 'webos-test');
});
