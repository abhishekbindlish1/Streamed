const fs = require('node:fs/promises');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { createHash } = require('node:crypto');

// A deliberate allowlist: never copy a checkout, local config, or cache wholesale.
const frontendFiles = [
  'index.html', 'icon.png', 'src/app.js', 'src/api.js', 'src/styles.css',
  'src/compat.js', 'src/compat.css', 'data/team-db.js', 'data/live-demo.json',
  'data/sources.sample.json',
  ...Array.from({ length: 8 }, (_, index) => `assets/profiles/avatar-${index}.svg`)
];

async function buildWeb(options = {}) {
  const sourceRoot = path.resolve(options.sourceRoot || path.join(__dirname, '..'));
  const realSourceRoot = await fs.realpath(sourceRoot);
  const outputRoot = path.resolve(options.outputRoot || path.join(sourceRoot, 'dist'));
  if (sourceRoot === outputRoot || sourceRoot.startsWith(outputRoot + path.sep)) {
    throw new Error('Build output must not contain the source directory');
  }
  const { version } = JSON.parse(await fs.readFile(path.join(sourceRoot, 'package.json'), 'utf8'));
  if (!/^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$/.test(version)) throw new Error('Invalid package version');
  const releaseTag = options.releaseTag === undefined ? process.env.RELEASE_TAG : options.releaseTag;
  if (releaseTag && releaseTag !== `v${version}`) throw new Error(`Tag ${releaseTag} must match package version v${version}`);
  const commit = options.commit || process.env.GITHUB_SHA || execFileSync('git', ['rev-parse', 'HEAD'], { cwd: sourceRoot, encoding: 'utf8' }).trim();
  await fs.rm(outputRoot, { recursive: true, force: true });
  const site = path.join(outputRoot, 'site');
  const releases = path.join(outputRoot, 'releases');
  await fs.mkdir(site, { recursive: true });
  await fs.mkdir(releases, { recursive: true });

  for (const name of frontendFiles) {
    const source = path.join(sourceRoot, name);
    if (!(await fs.lstat(source)).isFile()) throw new Error(`Only regular frontend files may be packaged: ${name}`);
    if (!(await fs.realpath(source)).startsWith(realSourceRoot + path.sep)) throw new Error(`Frontend file escapes the source directory: ${name}`);
    const target = path.join(site, name);
    await fs.mkdir(path.dirname(target), { recursive: true });
    let data = await fs.readFile(source);
    if (name === 'index.html') {
      // The browser distribution has no Tizen SDK runtime and uses relative URLs
      // so it works under a GitHub Pages project path such as /Streamed/.
      data = data.toString().replace(/^\s*<script src="\$WEBAPIS\/webapis\/webapis\.js"[^>]*><\/script>\r?\n/m, '\n');
    }
    await fs.writeFile(target, data);
  }

  const generated = {
    'data/config.local.js': 'window.STREAMED_CONFIG = { DEMO_MODE: true, TEAM_HUB_API_BASE_URL: "", TEAM_STANDINGS_API_BASE_URL: "" };\n',
    '.nojekyll': '',
    'BUILD.json': JSON.stringify({ version, commit, mode: 'demo' }, null, 2) + '\n',
    'README.txt': `Streamed browser demo v${version}\n\nUnzip this package, then serve this folder over HTTP:\n  python3 -m http.server 8080\nOpen http://localhost:8080, choose a profile, then select a sport.\n\nThis demo uses fixed sample events and public test videos, not current match\nbroadcasts. HLS.js and media playback need Internet access. No backend or API\nkey is required. Profile preferences stay in this browser's local storage.\n\nTo connect your own authorized match API, edit data/config.local.js: set\nDEMO_MODE to false and INTERNAL_API_BASE_URL to your backend's HTTPS origin.\nKeep credentials on your backend. Optional Team Hub services require a separate\nbackend; GitHub Pages cannot run server.js. This ZIP is not a TV installer.\n\nSource and TV setup: https://github.com/abhishekbindlish1/Streamed\n`
  };
  for (const [name, data] of Object.entries(generated)) await fs.writeFile(path.join(site, name), data);
  const inventory = [...frontendFiles, ...Object.keys(generated)].sort();
  // Fixed modes, timestamps, and file order make repeated builds reproducible.
  for (const name of inventory) {
    await fs.chmod(path.join(site, name), 0o644);
    await fs.utimes(path.join(site, name), new Date('2000-01-01T00:00:00Z'), new Date('2000-01-01T00:00:00Z'));
  }
  const archiveName = `streamed-web-v${version}.zip`;
  const archivePath = path.join(releases, archiveName);
  execFileSync('zip', ['-X', '-q', archivePath, ...inventory], { cwd: site, env: { ...process.env, TZ: 'UTC' } });
  const checksum = createHash('sha256').update(await fs.readFile(archivePath)).digest('hex');
  await fs.writeFile(path.join(releases, 'SHA256SUMS'), `${checksum}  ${archiveName}\n`);
  return { site, releases, archivePath, inventory };
}

if (require.main === module) {
  buildWeb().then(result => console.log(`Built ${result.site}\nArchive: ${result.archivePath}`)).catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}

module.exports = { buildWeb, frontendFiles };
