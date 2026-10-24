const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const net = require('node:net');
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const { chromium } = require('@playwright/test');

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const root = path.resolve(__dirname, '..');
const output = path.join(root, 'test-results/webos-simulator');
const appdir = process.env.WEBOS_SIMULATOR_APPDIR;
const app = path.join(root, 'apps/webos/dist/app');
let simulator, browser, log;

async function until(read, predicate, message, timeout = 30000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    try { const result = await read(); if (predicate(result)) return result; } catch {}
    await delay(250);
  }
  throw new Error(message);
}

function targets() {
  return new Promise((resolve, reject) => {
    const request = http.get('http://127.0.0.1:9223/json/list', response => {
      let data = '';
      response.on('data', chunk => data += chunk).on('end', () => {
        try { resolve(JSON.parse(data)); } catch (error) { reject(error); }
      });
    });
    request.setTimeout(2000, () => request.destroy(new Error('Simulator debug endpoint timeout')));
    request.on('error', reject);
  });
}

(async () => {
  if (!appdir) throw new Error('Set WEBOS_SIMULATOR_APPDIR using setup-tv-sdks.py');
  await new Promise((resolve, reject) => {
    const reservation = net.createServer();
    reservation.once('error', () => reject(new Error('Refusing to replace a service already using simulator debug port 9223')));
    reservation.listen(9223, '127.0.0.1', () => reservation.close(resolve));
  });
  await fs.mkdir(output, { recursive: true });
  log = await fs.open(path.join(output, 'simulator.log'), 'w');
  simulator = spawn(path.join(appdir, 'AppRun'), [app, '{}', '--ozone-platform=x11', '--no-sandbox', '--remote-debugging-port=9223'], {
    cwd: root, env: { ...process.env, APPDIR: appdir }, detached: true, stdio: ['ignore', log.fd, log.fd]
  });
  simulator.on('error', error => console.error('::error::LG Simulator launch: ' + error.message));
  await until(targets, pages => pages.length > 0, 'LG Simulator did not expose its debug targets', 90000);
  browser = await chromium.connectOverCDP('http://127.0.0.1:9223', { timeout: 15000 });
  const frame = await until(async () => {
    for (const context of browser.contexts()) for (const page of context.pages()) for (const candidate of page.frames()) {
      if (candidate.url().includes('/apps/webos/dist/app/index.html')) return candidate;
    }
  }, Boolean, 'LG Simulator did not open the packaged app', 30000);
  const keyboard = frame.page().keyboard;
  assert.equal(await frame.evaluate(() => !!(window.PalmSystem || window.webOSSystem)), true, 'LG platform API was not injected by the Simulator');
  await frame.locator('#profileList .profile-card[data-profile-id]').first().focus();
  await keyboard.press('Enter');
  await frame.locator('[data-sport-id="football"]').focus();
  await keyboard.press('Enter');
  await until(() => frame.locator('#matchesGrid .match-card[data-match-id]').count(), count => count === 3, 'LG catalog did not render');
  await frame.locator('body').screenshot({ path: path.join(output, 'webos-simulator-catalog.png') });
  await frame.locator('#matchesGrid .match-card[data-match-id]').first().focus();
  await keyboard.press('Enter');
  await frame.locator('.stream-option-button').first().focus();
  await keyboard.press('Enter');
  await until(() => frame.locator('#videoPlayer').evaluate(video => ({ width: video.videoWidth, height: video.videoHeight, time: video.currentTime, frames: video.getVideoPlaybackQuality().totalVideoFrames })),
    video => video.width === 320 && video.height === 180 && video.time > 0 && video.frames > 0, 'LG Simulator did not decode the sample video');
  await frame.locator('body').screenshot({ path: path.join(output, 'webos-simulator-player.png') });
  const remote = code => frame.evaluate(value => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Unidentified', keyCode: value, bubbles: true })), code);
  for (let i = 0; i < 2; i++) { await remote(19); assert.equal(await frame.locator('#videoPlayer').evaluate(video => video.paused), true); }
  await delay(1700);
  assert.equal(await frame.locator('#videoPlayer').evaluate(video => video.paused), true, 'Startup timer resumed paused LG video');
  for (let i = 0; i < 2; i++) { await remote(415); assert.equal(await frame.locator('#videoPlayer').evaluate(video => video.paused), false); }
  await remote(461);
  assert.equal(await frame.locator('#playerView').evaluate(view => view.hidden), true);
  assert.equal(await frame.locator('#videoPlayer').evaluate(video => video.paused), true);
  assert.equal(await frame.locator('#streamPickerOverlay').evaluate(view => view.hidden), false);
  await remote(461);
  assert.equal(await frame.locator('#streamPickerOverlay').evaluate(view => view.hidden), true);
  console.log('LG Simulator: real platform API, remote selection, decoded MP4, pause/resume and Back passed.');
})().catch(async error => {
  console.error('::error::LG Simulator test: ' + error.message);
  if (browser) for (const context of browser.contexts()) for (const page of context.pages()) {
    await page.screenshot({ path: path.join(output, 'webos-simulator-failure.png') }).catch(() => {});
  }
  process.exitCode = 1;
}).finally(async () => {
  if (browser) await browser.close().catch(() => {});
  if (simulator?.pid) { try { process.kill(-simulator.pid, 'SIGTERM'); } catch {} }
  await log?.close();
});
