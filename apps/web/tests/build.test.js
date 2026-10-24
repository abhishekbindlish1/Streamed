const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const { execFileSync } = require('node:child_process');
const { createHash } = require('node:crypto');
const { test } = require('node:test');
const { buildWeb, frontendFiles } = require('../scripts/build-web');

test('browser builds exclude private files, include assets, and reproduce their checksum', async t => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'streamed-build-test-'));
  t.after(() => fs.rm(temp, { recursive: true, force: true }));
  const sourceRoot = path.join(temp, 'source');
  const outputRoot = path.join(temp, 'output');
  for (const name of [...frontendFiles, 'package.json']) {
    const target = path.join(sourceRoot, name);
    await fs.mkdir(path.dirname(target), { recursive: true });
    await fs.copyFile(path.join(__dirname, '..', name), target);
  }
  const secret = 'PRIVATE_TEST_SENTINEL_MUST_NOT_SHIP';
  for (const name of ['.env.local', 'data/config.local.js', 'data/team-backfill.json', 'server.js', 'certificate.p12']) {
    await fs.writeFile(path.join(sourceRoot, name), secret);
  }
  const options = { sourceRoot, outputRoot, commit: 'test-commit', releaseTag: '' };
  const first = await buildWeb(options);
  const archive = await fs.readFile(first.archivePath);
  const entries = execFileSync('unzip', ['-Z1', first.archivePath], { encoding: 'utf8' }).trim().split('\n');
  assert.deepEqual(entries, first.inventory);
  const unpacked = path.join(temp, 'unpacked');
  execFileSync('unzip', ['-q', first.archivePath, '-d', unpacked]);
  for (const name of entries) assert.deepEqual(await fs.readFile(path.join(unpacked, name)), await fs.readFile(path.join(first.site, name)));
  assert.ok(entries.includes('src/compat.js'));
  assert.ok(entries.includes('data/config.local.js'));
  assert.ok(!entries.some(name => /server\.js|\.env|\.p12|backfill|node_modules|tests\//.test(name)));
  for (const name of entries) assert.ok(!(await fs.readFile(path.join(first.site, name))).includes(Buffer.from(secret)), name);
  const context = { window: {} };
  vm.runInNewContext(await fs.readFile(path.join(first.site, 'data/config.local.js'), 'utf8'), context);
  assert.equal(context.window.STREAMED_CONFIG.DEMO_MODE, true);
  assert.equal(context.window.STREAMED_CONFIG.TEAM_HUB_API_BASE_URL, '');
  const html = await fs.readFile(path.join(first.site, 'index.html'), 'utf8');
  assert.ok(!html.includes('$WEBAPIS'));
  for (const match of html.matchAll(/(?:src|href)="([^"]+)"/g)) {
    if (!/^https?:/.test(match[1])) assert.ok(entries.includes(match[1]), `Missing asset: ${match[1]}`);
  }
  const sums = await fs.readFile(path.join(first.releases, 'SHA256SUMS'), 'utf8');
  assert.ok(sums.startsWith(createHash('sha256').update(archive).digest('hex') + '  '));
  const second = await buildWeb(options);
  assert.deepEqual(await fs.readFile(second.archivePath), archive);
  await assert.rejects(buildWeb({ ...options, releaseTag: 'v999.0.0' }), /must match package version/);
  await fs.rm(path.join(sourceRoot, 'src/api.js'));
  await fs.symlink(path.join(sourceRoot, '.env.local'), path.join(sourceRoot, 'src/api.js'));
  await assert.rejects(buildWeb(options), /Only regular frontend files/);
});
