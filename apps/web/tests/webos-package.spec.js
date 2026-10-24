const { test, expect } = require('@playwright/test');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { pathToFileURL } = require('node:url');
const { buildWebos } = require('../../webos/scripts/build-webos');

test('webOS packaged file app plays offline video and handles remote/lifecycle cleanup', async ({ page }, testInfo) => {
  test.skip(!testInfo.project.name.startsWith('webos-'), 'Packaged webOS browser contract; not the LG Simulator');
  const output = await fs.mkdtemp(path.join(os.tmpdir(), 'streamed-webos-package-'));
  const errors = [];
  const network = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route(/^https?:\/\//, route => { network.push(route.request().url()); return route.abort(); });
  await page.addInitScript(() => {
    window.platformBackCalls = 0;
    window.PalmSystem = { platformBack() { window.platformBackCalls++; } };
  });
  const remote = code => page.evaluate(value => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Unidentified', keyCode: value, bubbles: true })), code);
  async function startVideo() {
    const match = page.locator('#matchesGrid .match-card[data-match-id]').first();
    await match.focus();
    await page.keyboard.press('Enter');
    await page.locator('.stream-option-button').first().focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('#videoPlayer')).toBeVisible();
    await expect.poll(() => page.locator('#videoPlayer').evaluate(video => ({ width: video.videoWidth, height: video.videoHeight }))).toEqual({ width: 320, height: 180 });
    await expect.poll(() => page.locator('#videoPlayer').evaluate(video => video.currentTime)).toBeGreaterThan(0);
    await expect.poll(() => page.locator('#videoPlayer').evaluate(video => video.getVideoPlaybackQuality().totalVideoFrames)).toBeGreaterThan(0);
  }
  try {
    const { app } = await buildWebos({ outputRoot: output, package: false, releaseTag: '' });
    await page.goto(pathToFileURL(path.join(app, 'index.html')).href);
    await page.locator('#profileList .profile-card[data-profile-id]').first().focus();
    await page.keyboard.press('Enter');
    await page.locator('[data-sport-id="football"]').focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('#matchesGrid .match-card[data-match-id]')).toHaveCount(3);
    await page.screenshot({ path: testInfo.outputPath('webos-packaged-catalog.png') });
    await startVideo();
    await page.screenshot({ path: testInfo.outputPath('webos-packaged-player.png') });
    for (let i = 0; i < 2; i++) { await remote(19); await expect(page.locator('#videoPlayer')).toHaveJSProperty('paused', true); }
    await page.waitForTimeout(1700);
    await expect(page.locator('#videoPlayer')).toHaveJSProperty('paused', true);
    for (let i = 0; i < 2; i++) { await remote(415); await expect(page.locator('#videoPlayer')).toHaveJSProperty('paused', false); }
    await remote(461);
    await expect(page.locator('#playerView')).toBeHidden();
    await expect(page.locator('#videoPlayer')).toHaveJSProperty('paused', true);
    await expect(page.locator('#streamPickerOverlay')).toBeVisible();
    await remote(461);
    await expect(page.locator('#streamPickerOverlay')).toBeHidden();
    expect(await page.evaluate(() => window.platformBackCalls)).toBe(0);
    await startVideo();
    await page.evaluate(() => {
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await expect(page.locator('#playerView')).toBeHidden();
    await expect(page.locator('#videoPlayer')).toHaveJSProperty('paused', true);
    await page.evaluate(() => {
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await expect(page.locator('#streamPickerOverlay')).toBeVisible();
    await remote(461);
    await expect(page.locator('#streamPickerOverlay')).toBeHidden();
    expect(await page.evaluate(() => window.platformBackCalls)).toBe(0);
    await remote(461);
    expect(await page.evaluate(() => window.platformBackCalls)).toBe(1);
    expect(network).toEqual([]);
    expect(errors).toEqual([]);
  } catch (error) {
    await page.screenshot({ path: testInfo.outputPath('webos-packaged-failure.png') }).catch(() => {});
    throw error;
  } finally {
    await page.close();
    await fs.rm(output, { recursive: true, force: true });
  }
});
