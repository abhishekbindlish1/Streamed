// Real Chromium 69 coverage uses its original DevTools protocol. Current
// Playwright does not support launching this old engine. No TV APIs are implied.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { existsSync } = require('node:fs');
const { spawn, execFileSync } = require('node:child_process');
const { createHash } = require('node:crypto');
const { brotliDecompressSync } = require('node:zlib');
const os = require('node:os');
const path = require('node:path');

const archiveUrl = 'https://registry.npmjs.org/chrome-aws-lambda/-/chrome-aws-lambda-1.5.0.tgz';
const archiveIntegrity = 'QsD2w5HjnFCbqMpEfTTbwtSJQjK5O1EL/kwKgots9PqcR8Tn7Tk+xkEvCB9YcSRHDdGyxctbAq2I+CBgMbdMuw==';
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

async function waitFor(fn) {
  const deadline = Date.now() + 10000;
  while (Date.now() < deadline) {
    if (await fn()) return;
    await delay(50);
  }
  throw new Error('Timed out waiting for legacy browser readiness');
}

async function startLegacy() {
  assert.equal(process.platform, 'linux', 'The pinned legacy binary requires Linux x64');
  assert.equal(process.arch, 'x64', 'The pinned legacy binary requires Linux x64');
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'streamed-legacy-'));
  const browserPath = path.join(temp, 'chromium69');
  const server = spawn(process.execPath, ['server.js'], {
    cwd: path.join(__dirname, '../..'), env: { ...process.env, PORT: '3101' }, stdio: 'pipe'
  });
  let browser;
  let socket;
  let serverLog = '';
  let browserLog = '';
  server.stdout.on('data', data => { serverLog += data; });
  server.stderr.on('data', data => { serverLog += data; });
  const pending = new Map();
  const errors = [];
  let id = 0;

  async function close() {
    if (socket) socket.close();
    for (const proc of [browser, server]) {
      if (!proc || proc.exitCode !== null) continue;
      const exited = new Promise(resolve => proc.once('exit', resolve));
      proc.kill('SIGTERM');
      const timer = setTimeout(() => proc.kill('SIGKILL'), 2000);
      await exited;
      clearTimeout(timer);
    }
    await fs.rm(temp, { recursive: true, force: true });
  }

  try {
    const response = await fetch(archiveUrl, { signal: AbortSignal.timeout(60000) });
    assert.equal(response.ok, true, 'Legacy browser archive download failed');
    const archive = Buffer.from(await response.arrayBuffer());
    assert.equal(createHash('sha512').update(archive).digest('base64'), archiveIntegrity, 'Legacy archive checksum mismatch');
    const archivePath = path.join(temp, 'browser.tgz');
    await fs.writeFile(archivePath, archive);
    const compressed = execFileSync('tar', ['-xOf', archivePath, 'package/bin/chromium-69.0.3452.0.br'], { maxBuffer: 40 * 1024 * 1024 });
    await fs.writeFile(browserPath, brotliDecompressSync(compressed), { mode: 0o700 });
    const fonts = existsSync('/usr/share/fonts/truetype/dejavu') ? '/usr/share/fonts/truetype/dejavu' : '/usr/share/fonts/truetype';
    const fontConfig = path.join(temp, 'fonts.conf');
    await fs.writeFile(fontConfig, `<?xml version="1.0"?><fontconfig><dir>${fonts}</dir><cachedir>${temp}/fonts-cache</cachedir></fontconfig>`);
    await waitFor(async () => {
      assert.equal(server.exitCode, null, serverLog);
      try { return (await fetch('http://127.0.0.1:3101/')).ok; } catch { return false; }
    });
    browser = spawn(browserPath, [
      '--headless', '--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--single-process', '--no-zygote',
      '--remote-debugging-port=0', `--user-data-dir=${temp}/profile`, 'about:blank'
    ], { env: { ...process.env, FONTCONFIG_FILE: fontConfig, XDG_CACHE_HOME: temp }, stdio: 'pipe' });
    browser.stderr.on('data', data => { browserLog += data; });
    browser.stdout.on('data', data => { browserLog += data; });
    await waitFor(() => {
      assert.equal(browser.exitCode, null, browserLog);
      return /DevTools listening on ws:\/\/127\.0\.0\.1:\d+/.test(browserLog);
    });
    const port = browserLog.match(/DevTools listening on ws:\/\/127\.0\.0\.1:(\d+)/)[1];
    const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
    socket = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
      socket.addEventListener('open', resolve, { once: true });
      socket.addEventListener('error', reject, { once: true });
    });
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails);
      const task = pending.get(message.id);
      if (task) {
        pending.delete(message.id);
        if (message.error) task.reject(new Error(JSON.stringify(message.error)));
        else task.resolve(message.result);
      }
    });
    async function call(method, params = {}) {
      const requestId = ++id;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(requestId);
          reject(new Error(`DevTools timeout: ${method}`));
        }, 10000);
        pending.set(requestId, {
          resolve(value) { clearTimeout(timer); resolve(value); },
          reject(error) { clearTimeout(timer); reject(error); }
        });
        socket.send(JSON.stringify({ id: requestId, method, params }));
      });
    }
    async function evaluate(expression) {
      const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result.value;
    }
    await call('Runtime.enable');
    await call('Page.enable');
    await call('Network.enable');
    // The old engine is isolated to local fixtures; block external page requests.
    await call('Network.setBlockedURLs', { urls: ['https://*'] });
    await call('Network.setUserAgentOverride', {
      userAgent: 'Mozilla/5.0 (SMART-TV; Linux; Tizen 5.5) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/69.0.3452.0 TV Safari/537.36'
    });
    // Verify the exact browser-loaded HLS build, including its SRI, rather than
    // silently trusting a moving CDN major-version alias on the older engine.
    const html = await fs.readFile(path.join(__dirname, '../../index.html'), 'utf8');
    const hls = html.match(/src="(https:\/\/cdn\.jsdelivr\.net\/npm\/hls\.js@[\d.]+\/dist\/hls\.min\.js)" integrity="sha384-([^"]+)"/);
    assert.ok(hls, 'Expected a pinned HLS.js script with SHA-384 integrity');
    const hlsResponse = await fetch(hls[1], { signal: AbortSignal.timeout(30000) });
    assert.equal(hlsResponse.ok, true, 'HLS.js download failed');
    const hlsSource = Buffer.from(await hlsResponse.arrayBuffer());
    assert.equal(createHash('sha384').update(hlsSource).digest('base64'), hls[2], 'HLS.js checksum mismatch');
    await evaluate(hlsSource.toString() + ';Hls.version');
    return { call, evaluate, waitFor, close, errors };
  } catch (error) {
    await close();
    throw error;
  }
}

module.exports = { startLegacy };
