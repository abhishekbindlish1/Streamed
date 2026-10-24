const { test, expect } = require('@playwright/test');

test.beforeEach(async ({ page }) => {
  // Exercise a deterministic offline catalog rather than third-party providers.
  await page.route('**/*', route => {
    const url = new URL(route.request().url());
    if (url.hostname !== '127.0.0.1') return route.abort();
    return route.continue();
  });
});

async function browseFootball(page) {
  await page.goto('/');
  await page.locator('#profileList .profile-card[data-profile-id]').first().click();
  await page.locator('[data-sport-id="football"]').click();
  await expect(page.locator('#matchesGrid .match-card[data-match-id]')).toHaveCount(3);
}

test('profiles, posters, catalog, search, and Samsung Return work', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await browseFootball(page);
  const bounds = await page.locator('.poster').first().boundingBox();
  expect(Math.abs(bounds.width / bounds.height - 16 / 9)).toBeLessThan(0.02);
  await page.locator('#searchButton').click();
  await page.locator('#searchInput').fill('Red City');
  await expect(page.locator('#searchResults')).toContainText('Red City');
  await page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Unidentified', keyCode: 10009, bubbles: true })));
  await expect(page.locator('#searchOverlay')).toBeHidden();
  await page.locator('#matchesGrid .match-card[data-match-id]').first().click();
  await expect(page.locator('#streamPickerOverlay')).toBeVisible();
  await expect(page.locator('.stream-option-button').first()).toBeVisible();
  const overlay = await page.locator('#streamPickerOverlay').boundingBox();
  const viewport = page.viewportSize();
  expect(overlay).toMatchObject({ x: 0, y: 0, width: viewport.width, height: viewport.height });
  await page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'XF86Back', keyCode: 10009, bubbles: true })));
  await expect(page.locator('#streamPickerOverlay')).toBeHidden();
  await page.locator('#profileButton').click();
  const profiles = page.locator('#profileList .profile-card[data-profile-id]');
  const count = await profiles.count();
  await page.locator('.add-profile-card').click();
  await expect(profiles).toHaveCount(count + 1);
  expect(errors).toEqual([]);
});

test('margin fallback spaces navigation when flex gap is unavailable', async ({ page }) => {
  await page.addInitScript(() => {
    const original = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollHeight');
    Object.defineProperty(Element.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        if (this.style.display === 'flex' && this.style.rowGap === '1px') return 0;
        return original.get.call(this);
      }
    });
  });
  await browseFootball(page);
  await expect(page.locator('html')).toHaveClass(/no-flex-gap/);
  const gap = await page.locator('#sportsList').evaluate(nav => {
    const buttons = nav.querySelectorAll(':scope > .sport-button');
    return buttons[1].getBoundingClientRect().left - buttons[0].getBoundingClientRect().right;
  });
  expect(gap).toBeGreaterThanOrEqual(5);
  expect(gap).toBeLessThanOrEqual(7);
});

test('TV uses AVPlay for HLS, numeric media control and Return tear it down', async ({ page }, testInfo) => {
  test.skip(!testInfo.project.name.startsWith('tizen-'), 'Samsung-specific native API contract');
  await page.addInitScript(() => {
    let state = 'NONE';
    window.nativeCalls = [];
    window.tizen = { tvinputdevice: { registerKey: name => window.nativeCalls.push(['register', name]) } };
    window.webapis = { avplay: {
      getState: () => state,
      open(url) { window.nativeCalls.push(['open', url]); state = 'IDLE'; },
      setListener() {}, setTimeoutForBuffering() {}, setBufferingParam() {}, setStreamingProperty() {},
      setDisplayRect(...rect) { window.nativeCalls.push(['rect', ...rect]); },
      setDisplayMethod() {},
      prepareAsync(success) { state = 'READY'; success(); },
      play() { state = 'PLAYING'; window.nativeCalls.push(['play']); },
      pause() { state = 'PAUSED'; window.nativeCalls.push(['pause']); },
      stop() { state = 'IDLE'; window.nativeCalls.push(['stop']); },
      close() { state = 'NONE'; window.nativeCalls.push(['close']); }
    } };
  });
  await browseFootball(page);
  await page.locator('#matchesGrid .match-card[data-match-id]').first().click();
  await page.locator('.stream-option-button').first().click();
  await expect(page.locator('#avplayPlayer')).toBeVisible();
  const calls = await page.evaluate(() => window.nativeCalls);
  expect(calls.find(call => call[0] === 'open')[1]).toContain('.m3u8');
  expect(calls).toContainEqual(['rect', 0, 0, 1920, 1080]);
  await page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Unidentified', keyCode: 10252, bubbles: true })));
  expect(await page.evaluate(() => window.webapis.avplay.getState())).toBe('PAUSED');
  // Startup retries must not undo an explicit pause. Pause and Play are
  // idempotent commands, including while the initial retry timers are pending.
  await page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'VendorPause', keyCode: 19, bubbles: true })));
  await page.waitForTimeout(1700);
  expect(await page.evaluate(() => window.webapis.avplay.getState())).toBe('PAUSED');
  for (let attempt = 0; attempt < 2; attempt++) {
    await page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'VendorPlay', keyCode: 415, bubbles: true })));
    expect(await page.evaluate(() => window.webapis.avplay.getState())).toBe('PLAYING');
  }
  await page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'XF86Back', keyCode: 10009, bubbles: true })));
  await expect(page.locator('#playerView')).toBeHidden();
  expect(await page.evaluate(() => window.webapis.avplay.getState())).toBe('NONE');
});
