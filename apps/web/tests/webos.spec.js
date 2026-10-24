const { test, expect } = require('@playwright/test');

test('webOS Back closes overlays before returning to the launcher', async ({ page }, testInfo) => {
  test.skip(!testInfo.project.name.startsWith('webos-'), 'LG webOS API contract');
  await page.addInitScript(() => {
    window.platformBackCalls = 0;
    window.PalmSystem = { platformBack() { window.platformBackCalls++; } };
    window.webOS = { platformBack() { window.PalmSystem.platformBack(); } };
  });
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  await page.goto('/');
  await page.locator('#profileList .profile-card[data-profile-id]').first().click();
  await page.locator('[data-sport-id="football"]').click();
  await page.locator('#matchesGrid .match-card[data-match-id]').first().click();
  await expect(page.locator('#streamPickerOverlay')).toBeVisible();
  await page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Unidentified', keyCode: 461, bubbles: true })));
  await expect(page.locator('#streamPickerOverlay')).toBeHidden();
  expect(await page.evaluate(() => window.platformBackCalls)).toBe(0);
  await page.evaluate(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'VendorBack', keyCode: 461, bubbles: true })));
  expect(await page.evaluate(() => window.platformBackCalls)).toBe(1);
});
