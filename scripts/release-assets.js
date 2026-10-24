const fs = require('node:fs/promises');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { createHash, X509Certificate } = require('node:crypto');

const root = path.resolve(__dirname, '..');
const hash = data => createHash('sha256').update(data).digest('hex');
const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();

function validateSigningCertificates(xml, now = new Date()) {
  const encoded = [...xml.matchAll(/<(?:[\w.-]+:)?X509Certificate\b[^>]*>([\s\S]*?)<\/(?:[\w.-]+:)?X509Certificate>/g)];
  if (!encoded.length) throw new Error('TPK signature has no signing certificates');
  return encoded.map(([, text]) => {
    const certificate = new X509Certificate(Buffer.from(text.replace(/\s/g, ''), 'base64'));
    const validFrom = new Date(certificate.validFrom);
    const validTo = new Date(certificate.validTo);
    if (now < validFrom || now >= validTo) throw new Error(`TPK signing certificate is outside its validity period: ${certificate.subject}; expires ${validTo.toISOString()}`);
    return validTo.toISOString();
  });
}

async function context() {
  const { version } = JSON.parse(await fs.readFile(path.join(root, 'apps/web/package.json'), 'utf8'));
  if (process.env.RELEASE_TAG && process.env.RELEASE_TAG !== `v${version}`) throw new Error('Release tag must match the web package version');
  return { version, commit: git('rev-parse', 'HEAD'), dirty: git('status', '--porcelain', '--untracked-files=no') !== '' };
}

async function collect(kind) {
  if (!['tizen', 'android', 'webos'].includes(kind)) throw new Error(`Unknown component: ${kind}`);
  const build = await context();
  const directory = path.join(root, 'release-native', kind);
  await fs.rm(directory, { recursive: true, force: true });
  await fs.mkdir(directory, { recursive: true });
  const records = [];
  async function copy(source, name, role, details) {
    const data = await fs.readFile(source);
    if (!data.length) throw new Error(`Empty package: ${source}`);
    await fs.writeFile(path.join(directory, name), data);
    records.push({ name, role, sha256: hash(data), ...details });
  }
  if (kind === 'tizen') {
    const project = path.join(root, 'apps/tizen-dotnet');
    const manifest = await fs.readFile(path.join(project, 'tizen-manifest.xml'), 'utf8');
    const componentVersion = manifest.match(/<manifest\b[^>]*\sversion="([^"]+)"/)[1];
    const outputs = path.join(project, 'bin/Release/tizen90');
    const packages = (await fs.readdir(outputs)).filter(name => name.endsWith('.tpk'));
    if (packages.length !== 1) throw new Error('Expected exactly one fresh Tizen TPK');
    const inventory = execFileSync('unzip', ['-Z1', path.join(outputs, packages[0])], { encoding: 'utf8' });
    for (const entry of ['tizen-manifest.xml', 'author-signature.xml', 'signature1.xml']) {
      if (!inventory.split('\n').includes(entry)) throw new Error(`TPK missing ${entry}`);
    }
    const expirations = ['author-signature.xml', 'signature1.xml'].flatMap(entry =>
      validateSigningCertificates(execFileSync('unzip', ['-p', path.join(outputs, packages[0]), entry], { encoding: 'utf8' })));
    await copy(path.join(outputs, packages[0]), `streamed-tizen9-${componentVersion}-development.tpk`, 'tizen-development', {
      componentVersion, minimumTizenAPI: '9.0', signing: 'Development author/distributor certificates; installation requires a profile authorized for the target',
      signingExpires: expirations.sort()[0]
    });
  } else if (kind === 'android') {
    for (const [rid, appId, role, label] of [
      ['android-arm64', 'com.streamed.tv.android', 'android-arm64-development', 'android-arm64-development'],
      ['android-x64', 'com.streamed.tv.android.sandbox', 'android-x64-sandbox', 'android-x64-emulator-sandbox']
    ]) {
      await copy(path.join(root, `apps/android/bin/Debug/net10.0-android/${rid}/${appId}-Signed.apk`),
        `streamed-${label}-v${build.version}.apk`, role,
        { architecture: rid, applicationId: appId, minimumAndroidAPI: 26, signing: 'Android debug certificate; uninstall may be required when changing development builds' });
    }
  } else if (kind === 'webos') {
    const source = path.join(root, `apps/webos/dist/packages/streamed-webos-development-v${build.version}.ipk`);
    const data = execFileSync('ar', ['p', source, 'data.tar.gz'], { maxBuffer: 32 * 1024 * 1024 });
    const prefix = 'usr/palm/applications/com.streamed.tv.webos.preview/';
    const read = name => execFileSync('tar', ['-xOzf', '-', prefix + name], { input: data, encoding: 'utf8', maxBuffer: 1024 * 1024 });
    const metadata = JSON.parse(read('BUILD.json'));
    const manifest = JSON.parse(read('appinfo.json'));
    if (metadata.version !== build.version || metadata.commit !== build.commit || metadata.platform !== 'webos' || metadata.mode !== 'demo') throw new Error('webOS package does not match release source');
    if (manifest.id !== 'com.streamed.tv.webos.preview' || manifest.type !== 'web') throw new Error('Unexpected webOS package identity');
    await copy(source, path.basename(source), 'webos-development', { componentVersion: manifest.version, applicationId: manifest.id, compatibilityTarget: 'webOS TV 6.0+', mode: 'offline demo', signing: 'LG Developer Mode package; not an LG Content Store release' });
  }
  await fs.writeFile(path.join(directory, `${kind}.json`), JSON.stringify({ ...build, packages: records }, null, 2) + '\n');
  return directory;
}

async function bundle(input, output, expected) {
  if (path.resolve(input) === path.resolve(output)) throw new Error('Input and output directories must differ');
  const browserDir = path.join(input, 'browser');
  const browserFiles = (await fs.readdir(browserDir)).filter(name => name.endsWith('.zip'));
  if (browserFiles.length !== 1) throw new Error('Expected one browser ZIP');
  const browserPath = path.join(browserDir, browserFiles[0]);
  const browser = JSON.parse(execFileSync('unzip', ['-p', browserPath, 'BUILD.json'], { encoding: 'utf8' }));
  if (browser.version !== expected.version || browser.commit !== expected.commit) throw new Error('Browser build does not match the release source');
  const records = [{ name: browserFiles[0], role: 'browser-demo', sha256: hash(await fs.readFile(browserPath)), source: browserPath }];
  for (const kind of ['tizen', 'android', 'webos']) {
    const directory = path.join(input, kind);
    const metadata = JSON.parse(await fs.readFile(path.join(directory, `${kind}.json`), 'utf8'));
    if (metadata.version !== expected.version || metadata.commit !== expected.commit || metadata.dirty) throw new Error(`${kind} build does not match clean release source`);
    for (const item of metadata.packages) {
    if (path.basename(item.name) !== item.name || item.name === '..') throw new Error('Invalid package filename');
      const source = path.join(directory, item.name);
      if (hash(await fs.readFile(source)) !== item.sha256) throw new Error(`Checksum mismatch: ${item.name}`);
      records.push({ ...item, source });
    }
  }
  const roles = records.map(record => record.role).sort();
  const required = ['android-arm64-development', 'android-x64-sandbox', 'browser-demo', 'tizen-development', 'webos-development'].sort();
  if (JSON.stringify(roles) !== JSON.stringify(required) || new Set(records.map(item => item.name)).size !== records.length) throw new Error('Release must include every platform exactly once');
  await fs.rm(output, { recursive: true, force: true });
  await fs.mkdir(output, { recursive: true });
  for (const record of records) await fs.copyFile(record.source, path.join(output, record.name));
  const manifest = { version: expected.version, commit: expected.commit, packages: records.map(({ source, ...item }) => item) };
  await fs.writeFile(path.join(output, 'BUILD-INFO.json'), JSON.stringify(manifest, null, 2) + '\n');
  const notes = `# Streamed v${expected.version}\n\nAll packages below are built from commit ${expected.commit}.\n\n## Downloads\n\n${records.map(item => `- ${item.name}`).join('\n')}\n\n## Install and compatibility\n\n- Browser ZIP: unzip and serve over HTTP. It uses sample events and public test videos; no backend credentials are needed.\n- Tizen TPK: native .NET app for Tizen 9 or newer, signed with current SDK development certificates. See signingExpires in BUILD-INFO.json; the SDK developer CA currently expires January 1, 2027. The default SDK build uses a disposable author; uninstall an older build when its signing identity differs. Configured Samsung profiles can provide a stable identity. Downloading does not authorize installation on arbitrary Samsung TVs. A compatible TV and an authorized Samsung author/distributor profile are still required. This TPK does not support the 2020 Tizen 5.5 TV; use the web client there.\n- ARM64 APK: experimental development build for 64-bit Android devices, API 26+. Install with adb or Android package installer. It retains the real pairing/login flow; production login and media playback are not validated by the fixture tests. Debug signing is for testing; updates from a different debug signer may require uninstalling the prior app.\n- LG IPK: experimental webOS TV preview targeting 6.0+, with an offline sample catalog and generated H.264/AAC video. Install using LG Developer Mode and ares-install, then launch com.streamed.tv.webos.preview. It does not enable Developer Mode or provide Content Store authorization. Real TV codecs, DRM and provider streams need separate acceptance testing.\n- x64 sandbox APK: Android emulator only. Start npm run sandbox on the emulator host; it uses 10.0.2.2:8080 and skips pairing for fixture data. Do not use it as a production app.\n\nNative builds remain experimental. Signing keys and credentials are never included. SHA256SUMS covers every download, including BUILD-INFO.json and this file.\n\nThis source repository is private; release downloads require repository access.\n`;
  await fs.writeFile(path.join(output, 'INSTALL.md'), notes);
  const files = (await fs.readdir(output)).sort();
  const sums = [];
  for (const name of files) sums.push(`${hash(await fs.readFile(path.join(output, name)))}  ${name}`);
  await fs.writeFile(path.join(output, 'SHA256SUMS'), sums.join('\n') + '\n');
  return manifest;
}

if (require.main === module) {
  const action = process.argv[2];
  (action === 'bundle'
    ? context().then(build => { if (build.dirty) throw new Error('Release checkout must be clean'); return bundle(path.join(root, 'incoming'), path.join(root, 'release'), build); })
    : collect(action)).then(result => console.log(typeof result === 'string' ? result : `Prepared all-platform release v${result.version}`)).catch(error => { console.error(error.message); process.exitCode = 1; });
}
module.exports = { bundle, validateSigningCertificates };
