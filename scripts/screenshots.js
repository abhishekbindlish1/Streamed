const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('@playwright/test');
const { createSandbox } = require('./sandbox');

(async () => {
  const server = await createSandbox();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH || undefined });
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, reducedMotion: 'reduce' });
    await page.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
    const output = path.join(__dirname, '../docs/screenshots');
    await fs.mkdir(output, { recursive: true });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    await page.locator('#profileList .profile-card[data-profile-id]').first().waitFor();
    await page.screenshot({ path: path.join(output, 'web-profiles.png') });
    await page.locator('#profileList .profile-card[data-profile-id]').first().click();
    await page.locator('[data-sport-id="football"]').click();
    await page.locator('#matchesGrid .match-card[data-match-id]').first().waitFor();
    await page.screenshot({ path: path.join(output, 'web-catalog.png') });
    await page.locator('#matchesGrid .match-card[data-match-id]').first().click();
    await page.locator('.stream-option-button').first().waitFor();
    await page.screenshot({ path: path.join(output, 'web-streams.png') });
    console.log('Saved real browser sandbox screenshots in docs/screenshots (web UI, not native app captures).');
  } finally { if (browser) await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
