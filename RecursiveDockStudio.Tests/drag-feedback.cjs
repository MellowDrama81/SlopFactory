// Runs the production docking script and stylesheet in a real Chromium browser.
// PLAYWRIGHT_MODULE may point to a bundled installation of playwright.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const root = path.join(__dirname, '../RecursiveDockStudio/wwwroot');
    await page.setContent(`<div id="app"><div style="display:flex;height:100%">
      <section class="tab-group" data-group="source" style="width:50%">
        <nav class="tabs"><button data-tab="overview" class="selected">Overview</button><button data-tab="velocity">Velocity</button></nav>
        <div class="dock-content" data-panel="overview"><div data-panel-view="overview" class="dock-panel-slot is-active">Overview content</div><div data-panel-view="velocity" class="dock-panel-slot">Velocity content</div></div>
      </section>
      <section class="tab-group" data-group="destination" style="width:50%">
        <nav class="tabs"><button data-tab="notes" class="selected">Notes</button></nav>
        <div class="dock-content" data-panel="notes"><div data-panel-view="notes" class="dock-panel-slot is-active">Notes content</div></div>
      </section></div></div>`);
    await page.addStyleTag({ content: fs.readFileSync(path.join(root, 'css/app.css'), 'utf8').replace(/^@import.*$/m, '') });
    await page.addScriptTag({ path: path.join(root, 'js/panel-docking.js') });
    await page.evaluate(() => {
      window.drops = [];
      window.recursiveDock.setReference({ invokeMethodAsync: async (...args) => window.drops.push(args) });
    });
    await page.locator('[data-tab="velocity"]').click();
    assert.ok(await page.locator('[data-tab="velocity"]').evaluate(element => element.classList.contains('selected')),
      'Clicking a tab must select it.');
    assert.ok(await page.locator('[data-panel-view="velocity"]').evaluate(element => element.classList.contains('is-active')),
      'Clicking a tab must reveal its panel.');
    await page.mouse.move(40, 100);
    await page.mouse.down();
    await page.mouse.move(150, 100, { steps: 10 });
    const badge = await page.locator('.dock-drag-ghost').boundingBox();
    console.log('Badge at pointer (150,100):', badge);
    assert.ok(badge && badge.x >= 0 && badge.y >= 0 && badge.x + badge.width <= 1000 && badge.y + badge.height <= 700,
      'Drag badge must be inside the viewport, not below the full-height app.');
    await page.mouse.move(750, 350, { steps: 10 });
    assert.equal(await page.locator('.dock-drag-ghost').innerText(), 'Drop to stack');
    await page.mouse.up();
    assert.deepEqual(await page.evaluate(() => window.drops), [['ApplyDockDrop', 'velocity', 'destination', 'stack']]);
    for (const [zone, x, y] of [['left', 510, 350], ['right', 990, 350], ['top', 750, 10], ['bottom', 750, 690]]) {
      await page.mouse.move(40, 100);
      await page.mouse.down();
      await page.mouse.move(x, y, { steps: 10 });
      const bounds = await page.locator('.dock-drag-ghost').boundingBox();
      assert.ok(bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= 1000 && bounds.y + bounds.height <= 700,
        `Badge must remain on screen at the ${zone} edge.`);
      assert.equal(await page.locator('.dock-drag-ghost').innerText(), `Drop to split ${zone}`);
      await page.mouse.up();
      assert.deepEqual(await page.evaluate(() => window.drops.at(-1)), ['ApplyDockDrop', 'velocity', 'destination', zone]);
    }
    assert.equal(await page.locator('.dock-drag-ghost').count(), 0);
    assert.deepEqual(errors, []);
    console.log('PASS: visible badge, all four split previews, stack callback, release cleanup, no browser errors.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
