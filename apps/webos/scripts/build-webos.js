const fs = require('node:fs/promises');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { createHash } = require('node:crypto');
const { buildWeb } = require('../../web/scripts/build-web');

const root = path.resolve(__dirname, '../../..');

async function buildWebos(options = {}) {
  const output = path.resolve(options.outputRoot || path.join(__dirname, '../dist'));
  if (output === root || root.startsWith(output + path.sep)) throw new Error('Build output must not contain the repository');
  const version = JSON.parse(await fs.readFile(path.join(root, 'apps/web/package.json'))).version;
  const commit = options.commit || execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
  // Use the existing audited frontend allowlist and generated demo config.
  // Never copy a checkout or a developer's config.local.js into an installer.
  const web = await buildWeb({ outputRoot: path.join(output, 'shared'), commit, releaseTag: options.releaseTag });
  const app = path.join(output, 'app');
  const packages = path.join(output, 'packages');
  await fs.rm(app, { recursive: true, force: true });
  await fs.rm(packages, { recursive: true, force: true });
  await fs.cp(web.site, app, { recursive: true });
  await fs.mkdir(packages, { recursive: true });
  const manifest = JSON.parse(await fs.readFile(path.join(__dirname, '../appinfo.json')));
  await fs.writeFile(path.join(app, 'appinfo.json'), JSON.stringify(manifest, null, 2) + '\n');
  await fs.mkdir(path.join(app, 'vendor'), { recursive: true });
  for (const name of ['webOSTV.js', 'LICENSE-2.0.txt']) {
    await fs.copyFile(path.join(__dirname, '../vendor', name), path.join(app, 'vendor', name));
  }
  const hls = await fs.readFile(require.resolve('hls.js/dist/hls.min.js'));
  const sourceHtml = await fs.readFile(path.join(app, 'index.html'), 'utf8');
  const integrity = sourceHtml.match(/src="https:\/\/cdn\.jsdelivr\.net\/npm\/hls\.js@1\.7\.3\/dist\/hls\.min\.js" integrity="([^"]+)"/);
  if (!integrity || 'sha384-' + createHash('sha384').update(hls).digest('base64') !== integrity[1]) throw new Error('Bundled HLS library does not match its pinned integrity');
  await fs.writeFile(path.join(app, 'vendor/hls.min.js'), hls);
  await fs.copyFile(path.join(path.dirname(require.resolve('hls.js/package.json')), 'LICENSE'), path.join(app, 'vendor/HLS-LICENSE.txt'));
  await fs.mkdir(path.join(app, 'media'), { recursive: true });
  await fs.copyFile(path.join(root, 'tests/fixtures/media/native-test.mp4'), path.join(app, 'media/native-test.mp4'));
  const catalog = JSON.parse(await fs.readFile(path.join(app, 'data/live-demo.json')));
  for (const match of catalog.matches) match.streams = [{ label: 'Generated H.264/AAC sample', type: 'mp4', url: 'media/native-test.mp4' }];
  await fs.writeFile(path.join(app, 'data/live-demo.json'), JSON.stringify(catalog, null, 2) + '\n');
  await fs.writeFile(path.join(app, 'data/catalog.js'), 'window.STREAMED_DEMO_CATALOG = ' + JSON.stringify(catalog).replace(/</g, '\\u003c') + ';\n');
  const html = (await fs.readFile(path.join(app, 'index.html'), 'utf8'))
    .replace(/src="https:\/\/cdn\.jsdelivr\.net\/npm\/hls\.js@1\.7\.3\/dist\/hls\.min\.js"/, 'src="vendor/hls.min.js"')
    .replace('<script src="src/compat.js">', '<script src="vendor/webOSTV.js"></script>\n    <script src="data/catalog.js"></script>\n    <script src="src/compat.js">');
  await fs.writeFile(path.join(app, 'index.html'), html);
  await fs.writeFile(path.join(app, 'BUILD.json'), JSON.stringify({ version, commit, mode: 'demo', platform: 'webos', componentVersion: manifest.version }, null, 2) + '\n');
  let archivePath;
  if (options.package !== false) {
    execFileSync(process.execPath, [require.resolve('@webos-tools/cli/bin/ares-package'), app, '--outdir', packages], { stdio: 'pipe' });
    const files = (await fs.readdir(packages)).filter(name => name.endsWith('.ipk'));
    if (files.length !== 1) throw new Error('Expected exactly one webOS IPK');
    archivePath = path.join(packages, `streamed-webos-development-v${version}.ipk`);
    await fs.rename(path.join(packages, files[0]), archivePath);
  }
  return { app, packages, archivePath, version, commit, manifest };
}

if (require.main === module) {
  buildWebos().then(result => console.log(`webOS app: ${result.app}\nIPK: ${result.archivePath}`)).catch(error => {
    console.error(error.stderr?.toString() || error.message);
    process.exitCode = 1;
  });
}
module.exports = { buildWebos };
