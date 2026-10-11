import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";

test("provider default-model picker lists, searches and selects without saving and rejects obsolete reads", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#provider-model')`, "provider form");
    assert.equal(await page.evaluate(`modelsFixture.calls.length`), 0, "no provider is queried before opening its picker");
    await page.click('[aria-label="Choose a model"]');
    await page.until(`document.querySelector('[data-model-id="first-fast"]')`, "available provider models");
    await page.click('[aria-label="Search models"]');
    await page.type('reasoning');
    await page.until(`!document.querySelector('[data-model-id="first-fast"]')`, "filtered models");
    await page.key('ArrowDown', 40);
    await page.key('Enter', 13);
    await page.until(`document.querySelector('#provider-model').value === 'first-reasoning'`, "keyboard selection");
    assert.equal(await page.evaluate(`modelsFixture.saved.length`), 0, "choosing changes only the draft");
    await page.click('#provider-model');
    await page.key('End', 35);
    await page.type('-custom');
    assert.equal(await page.evaluate(`document.querySelector('#provider-model').value`), 'first-reasoning-custom');
    await page.click('#provider-model + .bp6-input-action button');
    assert.equal(await page.evaluate(`document.querySelector('#provider-model').value`), '', "reset preserves the provider default");
    await page.evaluate(`modelsFixture.hold = true`);
    await page.click('[aria-label="Choose a model"]');
    await page.until(`modelsFixture.pending.length === 1`, "held inventory read");
    await page.key('Escape', 27);
    await page.click('[data-provider-key="second"]');
    await page.evaluate(`modelsFixture.hold = false; modelsFixture.pending.shift()()`);
    await page.click('[aria-label="Choose a model"]');
    await page.until(`document.querySelector('[data-model-id="second-fast"]')`, "second provider models");
    assert.equal(await page.evaluate(`!!document.querySelector('[data-model-id="first-fast"]')`), false);
    await page.click('[aria-label="Search models"]');
    await page.key('Escape', 27);
    await page.until(`!document.querySelector('.provider-model-picker')`, "closed picker");
    await page.evaluate(`modelsFixture.status = 'unavailable'`);
    await page.click('[aria-label="Choose a model"]');
    await page.until(`document.querySelector('.provider-model-picker [role="alert"]')`, "inventory error");
    await page.evaluate(`modelsFixture.status = 'ok'`);
    await page.clickText('.provider-model-picker button', 'Try again');
    await page.until(`document.querySelector('[data-model-id="second-fast"]')`, "retry");
    await page.click('[data-model-id="second-fast"]');
    await page.clickText('.provider-settings-footer button', 'Save and apply');
    await page.until(`modelsFixture.saved.length === 1`, "explicit save");
    assert.equal(await page.evaluate(`modelsFixture.saved[0].provider.model`), 'second-fast');
  }, { entry: "../providerModels.mount.tsx", fixture: "modelsFixture", rpcFixture: "../demo-api.ts", width: 1600, height: 1000, ratio: 1.5 });
});

test("provider model inventories reject foreign replies and explain empty or bounded lists", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#provider-model')`, "provider form");
    for (const [change, message] of [
      [{ replyEpoch: 'other' }, 'Host identity changed. Reload required.'],
      [{ replyEpoch: 'epoch', replyProvider: 'other' }, 'Invalid model inventory. Reload required.'],
      [{ replyProvider: null, empty: true }, 'This provider reported no models. A configured default is not inventory.'],
      [{ empty: false, truncated: true }, 'Some provider inventories are bounded; more models may exist.'],
    ] as const) {
      await page.evaluate(`Object.assign(modelsFixture, ${JSON.stringify(change)})`);
      await page.click('[aria-label="Choose a model"]');
      await page.until(`document.querySelector('.provider-model-picker')?.textContent.includes(${JSON.stringify(message)})`, message);
      await page.click('[aria-label="Search models"]');
      await page.key('Escape', 27);
      await page.until(`!document.querySelector('.provider-model-picker')`, "closed picker");
    }
    await page.click('#provider-url');
    await page.key('End', 35);
    await page.type('/changed');
    assert.equal(await page.evaluate(`document.querySelector('[aria-label="Choose a model"]').disabled`), true, "unsaved connection changes cannot query the old provider");
    assert.equal(await page.evaluate(`document.querySelector('#provider-model').disabled`), false, "manual model IDs remain editable");
  }, { entry: "../providerModels.mount.tsx", fixture: "modelsFixture", rpcFixture: "../demo-api.ts" });
});
