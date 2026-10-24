const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { createHash } = require('node:crypto');
const { test } = require('node:test');
const { buildWeb } = require('../apps/web/scripts/build-web');
const { bundle } = require('../scripts/release-assets');

async function fixture(t) {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'streamed-release-test-'));
  t.after(() => fs.rm(temp, { recursive: true, force: true }));
  const input = path.join(temp, 'incoming');
  const output = path.join(temp, 'release');
  const web = await buildWeb({ outputRoot: path.join(temp, 'web'), commit: 'release-test', releaseTag: '' });
  const version = JSON.parse(await fs.readFile(path.join(web.site, 'BUILD.json'))).version;
  const expected = { version, commit: 'release-test' };
  await fs.mkdir(path.join(input, 'browser'), { recursive: true });
  await fs.copyFile(web.archivePath, path.join(input, 'browser', path.basename(web.archivePath)));
  const roles = { tizen: ['tizen-development'], android: ['android-arm64-development', 'android-x64-sandbox'] };
  for (const [kind, items] of Object.entries(roles)) {
    const directory = path.join(input, kind);
    await fs.mkdir(directory, { recursive: true });
    const packages = [];
    for (const role of items) {
      // Synthetic bytes exercise collection/provenance, not installer validation.
      const data = Buffer.from(`test-package-${role}`);
      const name = role + (kind === 'tizen' ? '.tpk' : '.apk');
      await fs.writeFile(path.join(directory, name), data);
      packages.push({ name, role, sha256: createHash('sha256').update(data).digest('hex') });
    }
    await fs.writeFile(path.join(directory, `${kind}.json`), JSON.stringify({ ...expected, dirty: false, packages }));
  }
  return { input, output, expected };
}

test('release combines every platform with verifiable checksums and install notes', async t => {
  const { input, output, expected } = await fixture(t);
  const result = await bundle(input, output, expected);
  assert.equal(result.packages.length, 4);
  for (const line of (await fs.readFile(path.join(output, 'SHA256SUMS'), 'utf8')).trim().split('\n')) {
    const [digest, name] = line.split('  ');
    assert.equal(createHash('sha256').update(await fs.readFile(path.join(output, name))).digest('hex'), digest);
  }
  const instructions = await fs.readFile(path.join(output, 'INSTALL.md'), 'utf8');
  assert.match(instructions, /Tizen 9 or newer/);
  assert.match(instructions, /does not support the 2020 Tizen 5.5 TV/);
});

test('release refuses mismatched source commits', async t => {
  const { input, output, expected } = await fixture(t);
  await assert.rejects(bundle(input, output, { ...expected, commit: 'another-source' }), /does not match/);
});

test('release refuses tampered native downloads', async t => {
  const { input, output, expected } = await fixture(t);
  await fs.appendFile(path.join(input, 'android/android-arm64-development.apk'), 'tampered');
  await assert.rejects(bundle(input, output, expected), /Checksum mismatch/);
});

test('release refuses missing platforms and dirty native builds', async t => {
  const { input, output, expected } = await fixture(t);
  const metadataFile = path.join(input, 'tizen/tizen.json');
  const metadata = JSON.parse(await fs.readFile(metadataFile));
  await fs.writeFile(metadataFile, JSON.stringify({ ...metadata, dirty: true }));
  await assert.rejects(bundle(input, output, expected), /clean release source/);
  await fs.writeFile(metadataFile, JSON.stringify({ ...metadata, packages: [] }));
  await assert.rejects(bundle(input, output, expected), /every platform exactly once/);
});
