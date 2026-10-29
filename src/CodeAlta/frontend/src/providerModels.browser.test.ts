import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";

test("provider default-model picker lists, searches and selects without saving and rejects obsolete reads", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#provider-model')`, "provider form");
    await page.until(`modelsFixture.calls.length > 0`, "context metadata for the current provider");
    assert.equal(await page.evaluate(`modelsFixture.saved.length`), 0, "discovery never saves");
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

test("provider context reuses inventory across model edits, preserves offline choices, and fences obsolete reads", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    const choose = (selector: string, value: string) => page.evaluate(`(() => {
      const element = document.querySelector(${JSON.stringify(selector)});
      Object.getOwnPropertyDescriptor(${selector === "#provider-context" ? "HTMLSelectElement" : "HTMLInputElement"}.prototype, 'value').set.call(element, ${JSON.stringify(value)});
      element.dispatchEvent(new Event(${selector === "#provider-context" ? "'change'" : "'input'"}, { bubbles: true }));
    })()`);
    await page.until(`document.querySelector('#provider-context')`, "context selector");
    await choose("#provider-model", "first-reasoning");
    await page.until(`document.querySelector('#provider-context option[value="922000"]')`, "exact expanded endpoint");
    assert.deepEqual(await page.evaluate(`[...document.querySelector('#provider-context').options].map(option => option.value)`), ["", "272000", "922000"]);
    assert.deepEqual(await page.evaluate(`[...document.querySelector('#provider-context').options].map(option => option.textContent)`), ["Default (272K)", "272K", "922K"]);
    await page.click('#provider-context');
    await page.key('End', 35);
    await page.key('Enter', 13);
    await page.until(`document.querySelector('#provider-context').value === '922000'`, "keyboard-selected context");
    assert.equal(await page.evaluate(`modelsFixture.saved.length`), 0);
    const reads = await page.evaluate(`modelsFixture.calls.length`);
    await choose("#provider-model", "first-fast");
    await page.until(`document.querySelector('#provider-context option[value="128000"]')`, "other model's endpoints");
    assert.equal(await page.evaluate(`document.querySelector('#provider-context').value`), "", "new model starts at Default");
    await choose("#provider-model", "first-reasoning-custom");
    await page.until(`document.querySelector('#provider-context').options.length === 1`, "unknown model offers only Default");
    await choose("#provider-model", "first-reasoning");
    await page.until(`document.querySelector('#provider-context').value === '922000'`, "per-model preference restored");
    assert.equal(await page.evaluate(`modelsFixture.calls.length`), reads, "model edits reuse the provider inventory");
    // An unrelated form edit and a failed discovery must preserve the exact saved choice as unverified.
    await page.evaluate(`modelsFixture.status = 'unavailable'`);
    await choose("#provider-url", "http://localhost/changed");
    await page.until(`document.querySelector('[aria-label="Choose a model"]').disabled`, "unsaved connection invalidates inventory");
    await choose("#provider-url", "http://localhost/v1");
    await page.until(`modelsFixture.calls.length > ${reads} && !document.querySelector('#provider-context-notice').textContent.includes('Loading')`, "failed refreshed discovery");
    await page.until(`document.querySelector('#provider-context option:checked')?.textContent.includes('Unverified')`, "offline preference retained");
    await page.clickText('.provider-settings-footer button', 'Save and apply');
    await page.until(`modelsFixture.saved.length === 1`, "explicit save");
    assert.deepEqual(await page.evaluate(`modelsFixture.saved[0].provider.contextPreferences`), { "first-reasoning": 922000 });
    // A pending provider inventory resolves against the latest model, not the one selected when it started.
    await page.evaluate(`modelsFixture.status = 'ok'; modelsFixture.hold = true`);
    await choose("#provider-url", "http://localhost/changed");
    await choose("#provider-model", "first-reasoning");
    await choose("#provider-url", "http://localhost/v1");
    await page.until(`modelsFixture.pending.length > 0`, "held context read");
    await choose("#provider-model", "first-fast");
    await page.evaluate(`modelsFixture.hold = false; modelsFixture.pending.splice(0).forEach(resolve => resolve())`);
    await page.until(`document.querySelector('#provider-context option[value="128000"]')`, "current model response");
    assert.equal(await page.evaluate(`!!document.querySelector('#provider-context option[value="922000"]')`), false);
    // A late reply for the old provider cannot replace the new provider's inventory.
    await page.evaluate(`modelsFixture.hold = true`);
    await choose("#provider-url", "http://localhost/changed");
    await choose("#provider-url", "http://localhost/v1");
    await page.until(`modelsFixture.pending.length > 0`, "held obsolete provider read");
    await page.evaluate(`modelsFixture.hold = false`);
    await page.click('[data-provider-key="second"]');
    await page.until(`document.querySelector('#provider-context option[value="128000"]')`, "new provider's default model");
    await page.evaluate(`modelsFixture.pending.splice(0).forEach(resolve => resolve())`);
    assert.equal(await page.evaluate(`!!document.querySelector('#provider-context option[value="922000"]')`), false);
    assert.equal(await page.evaluate(`document.querySelector('#provider-context').value`), "");
  }, { entry: "../providerModels.mount.tsx", fixture: "modelsFixture", rpcFixture: "../demo-api.ts", width: 1600, height: 1000, ratio: 1.5 });
});

test("testing a recovered provider refreshes its context choices without saving", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#provider-context')`, "initial context selector");
    await page.evaluate(`modelsFixture.status = 'unavailable'`);
    await page.click('[data-provider-key="second"]');
    await page.until(`document.querySelector('#provider-context-notice').textContent.includes('unavailable')`, "offline inventory");
    assert.equal(await page.evaluate(`document.querySelector('#provider-context').options.length`), 1);
    await page.clickText('.provider-settings-footer button', 'Test selected provider');
    await page.until(`document.querySelector('#provider-context option[value="128000"]')`, "refreshed choices after test");
    assert.equal(await page.evaluate(`document.querySelector('#provider-context option').textContent`), "Default (128K)");
    assert.equal(await page.evaluate(`modelsFixture.saved.length`), 0, "testing never saves a preference");
  }, { entry: "../providerModels.mount.tsx", fixture: "modelsFixture", rpcFixture: "../demo-api.ts" });
});

test("a default model the provider's complete inventory does not list is named above its models", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    const missing = `document.querySelector('.provider-model-picker .provider-model-missing')?.textContent ?? null`;
    const reopen = async () => {
      await page.click('[aria-label="Choose a model"]');
      await page.until(`document.querySelector('.provider-model-picker [data-model-id]')`, "the models of the provider");
    };
    const close = async () => {
      await page.click('[aria-label="Search models"]');
      await page.key('Escape', 27);
      await page.until(`!document.querySelector('.provider-model-picker')`, "closed picker");
    };
    await page.until(`document.querySelector('#provider-model')`, "provider form");
    assert.equal(await page.evaluate(`document.querySelector('#provider-model').placeholder`), "first-retired", "the field is left to the default of the template");
    await reopen();
    assert.equal(await page.evaluate(missing), "This provider does not list first-retired. Choose one of its models.");
    // A model of the list is chosen: nothing is missing any more, and nothing is saved.
    await page.click('[aria-label="Search models"]');
    await page.key('ArrowDown', 40);
    await page.key('Enter', 13);
    await page.until(`document.querySelector('#provider-model').value === 'first-fast'`, "the chosen model");
    await reopen();
    assert.equal(await page.evaluate(missing), null);
    await close();
    // A model typed by hand that the list does not have is named as well.
    await page.click('#provider-model');
    await page.key('End', 35);
    await page.type('-custom');
    await reopen();
    assert.equal(await page.evaluate(missing), "This provider does not list first-fast-custom. Choose one of its models.");
    await close();
    // A bounded inventory may leave a model out: nothing is said of it.
    await page.evaluate(`modelsFixture.truncated = true`);
    await reopen();
    assert.equal(await page.evaluate(missing), null);
    await close();
    // A provider whose default is the first model listed has nothing missing.
    await page.evaluate(`modelsFixture.truncated = false`);
    await page.click('[data-provider-key="second"]');
    await reopen();
    assert.equal(await page.evaluate(missing), null);
    assert.equal(await page.evaluate(`modelsFixture.saved.length`), 0, "looking saves nothing");
  }, { entry: "../providerModels.mount.tsx", fixture: "modelsFixture", rpcFixture: "../demo-api.ts" });
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

test("a provider that lists hundreds of models offers all of them", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    // Azure OpenAI lists 475 models in one page: the ones after the 128th were once refused as an invalid inventory.
    await page.until(`document.querySelector('#provider-model')`, "provider form");
    await page.evaluate(`modelsFixture.listed = 473`);
    await page.click('[aria-label="Choose a model"]');
    await page.until(`document.querySelector('[data-model-id="first-listed-472"]')`, "the last model of a large inventory");
    assert.equal(await page.evaluate(`document.querySelectorAll('.provider-model-picker [data-model-id]').length`), 475);
    await page.click('[aria-label="Search models"]');
    await page.type('listed-422');
    await page.until(`document.querySelectorAll('.provider-model-picker [data-model-id]').length === 1`, "the searched model");
    await page.click('[data-model-id="first-listed-422"]');
    await page.until(`document.querySelector('#provider-model').value === 'first-listed-422'`, "a model listed after the 128th");
  }, { entry: "../providerModels.mount.tsx", fixture: "modelsFixture", rpcFixture: "../demo-api.ts" });
});
