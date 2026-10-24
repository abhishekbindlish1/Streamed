const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { createHash } = require('node:crypto');
const { test } = require('node:test');
const { buildWeb } = require('../apps/web/scripts/build-web');
const { bundle, validateSigningCertificates } = require('../scripts/release-assets');

test('TPK release signing rejects expired, future and missing public certificates', async () => {
  // Public SDK signer only; no key or signing credentials are stored here.
  const pem = await fs.readFile(path.join(__dirname, 'fixtures/certificates/tizen-public-signer.pem'), 'utf8');
  const base64 = pem.replace(/-----[^-]+-----/g, '').replace(/\s/g, '');
  const xml = `<ds:X509Data><ds:X509Certificate>${base64}</ds:X509Certificate></ds:X509Data>`;
  assert.deepEqual(validateSigningCertificates(xml, new Date('2026-10-24')), ['2032-10-24T04:41:53.000Z']);
  assert.throws(() => validateSigningCertificates(xml, new Date('2033-01-01')), /outside its validity/);
  assert.throws(() => validateSigningCertificates(xml, new Date('2021-01-01')), /outside its validity/);
  assert.throws(() => validateSigningCertificates('<Signature/>'), /no signing certificates/);
  const plain = xml.replaceAll('ds:', '');
  assert.equal(validateSigningCertificates(plain, new Date('2026-10-24')).length, 1);
  assert.throws(() => validateSigningCertificates('<X509Certificate>invalid</X509Certificate>'));
});

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
  const roles = { tizen: ['tizen-development'], android: ['android-arm64-development', 'android-x64-sandbox'], webos: ['webos-development'] };
  for (const [kind, items] of Object.entries(roles)) {
    const directory = path.join(input, kind);
    await fs.mkdir(directory, { recursive: true });
    const packages = [];
    for (const role of items) {
      // Synthetic bytes exercise collection/provenance, not installer validation.
      const data = Buffer.from(`test-package-${role}`);
      const name = role + (kind === 'tizen' ? '.tpk' : kind === 'webos' ? '.ipk' : '.apk');
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
  assert.equal(result.packages.length, 5);
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
