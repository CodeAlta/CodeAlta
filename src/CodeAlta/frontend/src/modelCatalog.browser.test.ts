import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";
import { testLimit } from "./testTime";

const options = { entry: "../modelCatalog.mount.tsx", fixture: "catalogFixture", rpcFixture: "../demo-api.ts", width: 1600, height: 1000, ratio: 1.5,
  styles: ["../../node_modules/@blueprintjs/table/lib/css/table.css"] };

test("Models refreshes only the chosen provider, replacing retired models and retaining the other inventories", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.model-catalog-grid')?.textContent.includes('first-retired')`, "rendered initial model inventory");
    assert.ok(await page.evaluate(`!!document.querySelector('[aria-label="Refresh provider"]')`), "the inventory page offers a provider refresh");
    assert.deepEqual(await page.evaluate(`catalogFixture.probes`), [], "navigation only reads cached inventory");
    assert.equal(await page.evaluate(`document.querySelector('[aria-label="Refresh provider"] option[value="disabled"]')`), null);
    await page.evaluate(`catalogFixture.listed = 'new'; catalogFixture.hold = true`);
    await page.clickText('button', 'Refresh models');
    await page.until(`catalogFixture.pending.length === 1`, "provider refresh");
    assert.equal(await page.evaluate(`document.querySelector('[aria-label="Refresh provider"]').disabled`), true);
    assert.deepEqual(await page.evaluate(`catalogFixture.probes`), ['first']);
    await page.evaluate(`catalogFixture.pending.shift()()`);
    await page.until(`document.querySelector('.model-catalog-grid')?.textContent.includes('first-new')`, "new model");
    assert.equal(await page.evaluate(`document.querySelector('.model-catalog-grid').textContent.includes('first-retired')`), false);
    assert.equal(await page.evaluate(`document.querySelector('.model-catalog-grid').textContent.includes('second-retired')`), true);
    assert.equal(await page.evaluate(`catalogFixture.reads.filter(id => id === 'second').length`), 1, "another provider is not read again");
    // The list of a native select is drawn and driven by the system, each in its own way: the option is chosen as the list does it.
    await page.evaluate(`(() => { const select = document.querySelector('[aria-label="Refresh provider"]');
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, 'second');
      select.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    await page.until(`document.querySelector('[aria-label="Refresh provider"]').value === 'second'`, "chosen second provider");
    await page.evaluate(`catalogFixture.hold = false`);
    await page.clickText('button', 'Refresh models');
    await page.until(`document.querySelector('.model-catalog-grid')?.textContent.includes('second-new')`, "second provider refreshed");
    assert.deepEqual(await page.evaluate(`catalogFixture.probes`), ['first', 'second']);
    assert.equal(await page.evaluate(`document.querySelector('.model-catalog-grid').textContent.includes('first-new')`), true);
    await page.resize(900, 650);
    assert.equal(await page.evaluate(`(() => { const bar = document.querySelector('.model-catalog-refresh'); return bar.scrollWidth > bar.clientWidth; })()`), false);
  }, options);
});

test("Models reports failed and stale refreshes, allows retry and ignores completion after unmount", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.model-catalog-grid')`, "initial inventory");
    for (const failure of [{ status: 'busy' }, { replyEpoch: 'other' }, { replyProvider: 'other' }, { reject: true }]) {
      await page.evaluate(`Object.assign(catalogFixture, { status: 'ok', replyEpoch: 'epoch', replyProvider: null, reject: false }, ${JSON.stringify(failure)})`);
      await page.clickText('button', 'Refresh models');
      await page.until(`document.querySelector('.model-catalog-refresh-notice[role="alert"]')`, "refresh failure");
      assert.equal(await page.evaluate(`document.body.textContent.includes('private diagnostic')`), false);
      assert.equal(await page.evaluate(`catalogFixture.reads.length`), 2, "invalid refresh does not replace inventory");
    }
    await page.evaluate(`Object.assign(catalogFixture, { status: 'ok', replyEpoch: 'epoch', replyProvider: null, reject: false, hold: true })`);
    await page.clickText('button', 'Refresh models');
    await page.until(`catalogFixture.pending.length === 1`, "held refresh");
    await page.evaluate(`catalogFixture.unmount()`);
    await page.until(`!document.querySelector('.model-catalog-page')`, "unmounted page");
    await page.evaluate(`catalogFixture.pending.shift()()`);
    assert.equal(await page.evaluate(`catalogFixture.reads.length`), 2, "obsolete refresh does not read again");
  }, options);
});
