const { test, expect } = require('@playwright/test');

test('published browser package works at a project subpath without backend APIs', async ({ page }) => {
  const errors = [];
  const failedLocal = [];
  const apiRequests = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (request.url().includes('/api/')) apiRequests.push(request.url()); });
  page.on('response', response => { if (response.url().startsWith('http://127.0.0.1:3102/') && response.status() >= 400) failedLocal.push(response.url()); });
  await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  await page.goto('http://127.0.0.1:3102/Streamed/');
  await expect(page.locator('#demoNotice')).toBeVisible();
  await page.locator('#profileList .profile-card[data-profile-id]').first().click();
  await page.locator('[data-sport-id="football"]').click();
  await expect(page.locator('#matchesGrid .match-card[data-match-id]')).toHaveCount(3);
  await page.locator('#matchesGrid .match-card[data-match-id]').first().click();
  await expect(page.locator('.stream-option-button').first()).toBeVisible();
  expect(await page.evaluate(() => window.StreamedAPI.HAS_TEAM_HUB_PROVIDER)).toBe(false);
  expect(apiRequests).toEqual([]);
  expect(failedLocal).toEqual([]);
  expect(errors).toEqual([]);
});
