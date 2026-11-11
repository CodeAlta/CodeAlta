import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";
import { testLimit } from "./testTime";

const options = { entry: "../composerProviderPicker.mount.tsx", fixture: "pickerFixture", rpcFixture: "../demo-api.ts", width: 1200, height: 900 } as const;
const rows = `[...document.querySelectorAll('.composer-provider-picker [role="listbox"] > li')].map(item => item.getAttribute('role') === 'separator' ? '# ' + item.textContent : item.querySelector('[data-provider-id]')?.dataset.providerId)`;
const active = `document.querySelector('.composer-provider-picker .bp6-active')?.closest('[data-provider-id]')?.dataset.providerId`;

test("the provider list of the composer searches, lists the favorites first and every group by name, with logos and types", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.click('[aria-label="Session configuration"]');
    await page.until(`document.querySelector('#composer-provider-test')`, "the form of the chip");
    assert.equal(await page.evaluate(`document.querySelector('#composer-provider-test').textContent`), "antigravity-acp");
    await page.click('#composer-provider-test');
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Search providers'`, "the focus in the search box");

    // No favorite: one list by name, without headings, opened on the provider in use.
    assert.deepEqual(await page.evaluate(rows), ["antigravity-acp", "claude-max5", "claude-code", "codex", "codex-cli", "copilot", "xiaomi"]);
    assert.equal(await page.evaluate(active), "antigravity-acp");
    assert.equal(await page.evaluate(`document.querySelector('[data-provider-id="claude-code"]').textContent`), "claude-code (default)");
    assert.equal(await page.evaluate(`document.querySelector('[data-provider-id="claude-max5"]').textContent`), "Claude Maxclaude-max5 · claude-code", "its key and its type beside its name");
    assert.equal(await page.evaluate(`document.querySelector('[data-provider-id="xiaomi"]').textContent`), "xiaomiopenai");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.composer-provider-picker [role="option"]')].every(option => !!option.querySelector('svg, .provider-image-icon'))`), true, "each provider with its logo");

    // The search finds by name, key or type; the arrows and Enter choose without leaving the box.
    await page.type("claude");
    await page.until(`${rows}.join() === 'claude-max5,claude-code'`, "the providers of that type");
    await page.key("ArrowDown", 40);
    assert.equal(await page.evaluate(active), "claude-code");
    await page.key("Enter", 13);
    await page.until(`!document.querySelector('.composer-provider-picker')`, "the list closed by the choice");
    assert.deepEqual(await page.evaluate(`pickerFixture.chosen`), ["claude-code"]);
    assert.equal(await page.evaluate(`document.querySelector('#composer-provider-test').textContent`), "claude-code (default)");
    assert.equal(await page.evaluate(`!!document.querySelector('.composer-selection-form')`), true, "the form of the chip stays open");

    // The favorites of Settings, read again when the list opens, come first under their heading.
    await page.evaluate(`pickerFixture.favorites = ["xiaomi", "codex"]`);
    await page.click('#composer-provider-test');
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Search providers'`, "the list again");
    assert.deepEqual(await page.evaluate(rows), ["# Favorites", "codex", "xiaomi", "# Other providers", "antigravity-acp", "claude-max5", "claude-code", "codex-cli", "copilot"]);
    assert.equal(await page.evaluate(active), "claude-code");

    // A search that finds nothing says so, and Escape closes the list only.
    await page.type("gemini");
    await page.until(`document.querySelector('.composer-provider-picker [role="status"]')?.textContent === 'No providers match this search.'`, "no match");
    await page.key("Enter", 13);
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.composer-provider-picker')`, "the list closed by Escape");
    assert.equal(await page.evaluate(`!!document.querySelector('.composer-selection-form')`), true);
    assert.deepEqual(await page.evaluate(`pickerFixture.chosen`), ["claude-code"]);

    // A click chooses as well.
    await page.click('#composer-provider-test');
    await page.click('.composer-provider-picker [data-provider-id="xiaomi"]');
    await page.until(`!document.querySelector('.composer-provider-picker')`, "the list closed by the click");
    assert.deepEqual(await page.evaluate(`pickerFixture.chosen`), ["claude-code", "xiaomi"]);

    // While the providers are read, the button names the provider in use by its name and waits; one no longer offered is marked.
    await page.evaluate(`pickerFixture.listed = 2; pickerFixture.loading = true; pickerFixture.apply()`);
    await page.until(`document.querySelector('#composer-provider-test').disabled`, "a waiting button");
    await page.evaluate(`pickerFixture.loading = false; pickerFixture.apply()`);
    await page.until(`document.querySelector('#composer-provider-test').textContent === 'xiaomi'`, "a provider still offered");
    await page.click('#composer-provider-test');
    await page.click('.composer-provider-picker [data-provider-id="codex"]');
    await page.evaluate(`pickerFixture.listed = 1; pickerFixture.apply()`);
    await page.until(`document.querySelector('#composer-provider-test').textContent === 'codex · Unavailable'`, "a provider no longer offered");
  }, options);
});
