const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { test } = require('node:test');
const { startLegacy } = require('./harness');

test('actual Chromium 69: layout, catalog, search, profiles, and numeric Return', { timeout: 120000 }, async t => {
  const browser = await startLegacy();
  t.after(() => browser.close());
  const { call, evaluate, waitFor } = browser;
  for (const [width, height] of [[1920, 1080], [1280, 720]]) {
    await t.test(`${width} x ${height}`, async () => {
      try {
        await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
        await call('Page.navigate', { url: 'http://127.0.0.1:3101/' });
        await waitFor(() => evaluate('!!window.StreamedCompat && !!document.querySelector(".profile-card[data-profile-id]")'));
        assert.equal(await evaluate('CSS.supports("width", "min(1px, 2px)")'), false);
        assert.equal(await evaluate('CSS.supports("aspect-ratio", "16/9")'), false);
        assert.equal(await evaluate('StreamedCompat.hasFlexGap'), false);
        const box = 'function(selector) { var r=document.querySelector(selector).getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height}; }';
        assert.deepEqual(await evaluate(`(${box})("#profileView")`), { x: 0, y: 0, width, height });
        await evaluate('document.querySelector(".profile-card[data-profile-id]").click()');
        await waitFor(() => evaluate('!!document.querySelector("[data-sport-id=football]")'));
        await evaluate('document.querySelector("[data-sport-id=football]").click()');
        assert.equal(await evaluate('document.querySelectorAll("#matchesGrid .match-card[data-match-id]").length'), 3);
        const poster = await evaluate(`(${box})(".poster")`);
        assert.ok(poster.height > 100);
        assert.ok(Math.abs(poster.width / poster.height - 16 / 9) < 0.02);
        assert.equal((await evaluate(`(${box})(".hero-copy")`)).width, 640);
        assert.equal(await evaluate('(function(){var n=document.querySelectorAll("#sportsList > .sport-button");return n[1].getBoundingClientRect().left-n[0].getBoundingClientRect().right;})()'), 6);
        await evaluate('document.querySelector("#searchButton").click(); var input=document.querySelector("#searchInput"); input.value="Red City"; input.dispatchEvent(new Event("input",{bubbles:true}));');
        assert.equal(await evaluate('document.querySelector("#searchResults").textContent.indexOf("Red City") >= 0'), true);
        const back = 'document.dispatchEvent(new KeyboardEvent("keydown",{key:"Unidentified",keyCode:10009,bubbles:true}))';
        await evaluate(back);
        assert.equal(await evaluate('document.querySelector("#searchOverlay").hidden'), true);
        await evaluate('document.querySelector("#matchesGrid .match-card[data-match-id]").click()');
        assert.deepEqual(await evaluate(`(${box})("#streamPickerOverlay")`), { x: 0, y: 0, width, height });
        assert.ok(await evaluate('document.querySelectorAll(".stream-option-button").length') > 0);
        await evaluate(back);
        assert.equal(await evaluate('document.querySelector("#streamPickerOverlay").hidden'), true);
        await evaluate('document.querySelector("#profileButton").click()');
        const count = await evaluate('JSON.parse(localStorage.getItem("STREAMED_PROFILES")).length');
        await evaluate('document.querySelector(".add-profile-card").click()');
        assert.equal(await evaluate('JSON.parse(localStorage.getItem("STREAMED_PROFILES")).length'), count + 1);
        assert.deepEqual(browser.errors, []);
      } catch (error) {
        await fs.mkdir('test-results', { recursive: true });
        const shot = await call('Page.captureScreenshot', { format: 'png' });
        await fs.writeFile(`test-results/legacy-${width}.png`, Buffer.from(shot.data, 'base64'));
        throw error;
      }
    });
  }
});
