import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./browserHarness";

test("the Statistics menu saves the display interval and manual refresh reads immediately", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`statsFixture.render({ scenario: "ready" })`);
    await page.until(`document.querySelector('.stats-block') && !document.querySelector('.stats-tile-skeleton') && !document.querySelector('.stats-block[data-state="loading"]')`, "the overview");
    await page.click('[aria-label="Statistics menu"]');
    await page.clickText('.bp6-menu-item > .bp6-fill', 'Refresh every 10 minutes');
    await page.clickText('.bp6-menu-item', 'Every 5 minutes');
    await page.until(`[...statsFixture.storage.values()].some(value => value.includes('refresh=5'))`, "saved interval");
    await page.evaluate(`statsFixture.remount()`);
    await page.click('[aria-label="Statistics menu"]');
    await page.until(`[...document.querySelectorAll('.bp6-menu-item > .bp6-fill')].some(item => item.textContent.trim() === 'Refresh every 5 minutes')`, "restored interval");
    for (const dark of [true, false]) {
      await page.key('Escape', 27);
      await page.evaluate(`statsFixture.setTheme(${dark})`);
      await page.click('[aria-label="Statistics menu"]');
      await new Promise(resolve => setTimeout(resolve, 300)); // Let the opening animation finish before the picture.
      await page.shot(`refresh-menu-${dark ? "dark" : "light"}`);
    }
    await page.key('Escape', 27);
    await page.until(`!document.querySelector('.stats-tile-skeleton')`, "settled after remount");
    await page.evaluate(`statsFixture.clearCalls(); statsFixture.control.emitData(20261009, 20261009); statsFixture.advance(299_999)`);
    assert.equal(await page.evaluate(`statsFixture.calls().filter(call => call.method === 'summary').length`), 0);
    await page.evaluate(`statsFixture.advance(1)`);
    await page.until(`statsFixture.calls().some(call => call.method === 'summary')`, "five-minute refresh");
    await page.until(`!document.querySelector('.stats-block[data-state="loading"]')`, "refreshed");
    await page.evaluate(`statsFixture.clearCalls(); statsFixture.control.emitData(20261009, 20261009)`);
    await page.click('[aria-label="Refresh statistics"]');
    await page.until(`statsFixture.calls().some(call => call.method === 'summary')`, "manual refresh");
    await page.resize(900, 650, 1.5);
    await page.until(`document.documentElement.scrollWidth <= document.documentElement.clientWidth`, "narrow layout without overflow");
    assert.equal(await page.evaluate(`document.documentElement.scrollWidth > document.documentElement.clientWidth`), false);
  }, { width: 1600, height: 1000, ratio: 1.5 });
});
