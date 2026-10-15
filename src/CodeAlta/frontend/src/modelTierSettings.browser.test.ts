import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";

const rows = (scope: string, tier: string) =>
  `[...document.querySelectorAll('#${scope} section.model-tier[aria-label="${tier}"] .model-tier-target')].map(row => row.querySelector('.model-tier-model').title.split('\\n')[1] + ' ' + [...row.querySelectorAll('.model-tier-effort')].map(chip => chip.textContent).join(','))`;
const item = (name: string) => `[...document.querySelectorAll('.model-tier-add .bp6-menu-item')].find(entry => entry.querySelector('.bp6-text-overflow-ellipsis')?.textContent === ${JSON.stringify(name)})`;

test("a row of the tiers changes its model in its place, keeping the efforts the new model offers", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#space .model-tier-target')`, "the tiers of the space");
    assert.deepEqual(await page.evaluate(rows("global", "Complex")), ["claude-code:opus High,XHigh", "codex:gpt-6.1 XHigh", "gone:m "]);

    // The button of a row opens the picker of "Add a model": the models of the tier are not offered again, its own is marked.
    await page.click('#global [aria-label="Change the model of Opus"]');
    await page.until(`${item("Sonnet")}`, "the picker of the row");    assert.equal(await page.evaluate(`${item("GPT-6.1")}.classList.contains('bp6-disabled')`), true, "a model the tier has is not offered");
    assert.equal(await page.evaluate(`${item("Opus")}.classList.contains('bp6-disabled')`), false);
    assert.equal(await page.evaluate(`${item("Opus")}.classList.contains('bp6-active')`), true, "the row's own model is marked");
    await page.clickText('.model-tier-add .bp6-menu-item .bp6-text-overflow-ellipsis', 'Sonnet');
    // Sonnet takes the place of Opus with the effort it offers too; XHigh, which it does not, is left.
    await page.until(`tiersFixture.saved.length === 1`, "the save of the change");
    assert.deepEqual(await page.evaluate(rows("global", "Complex")), ["claude-code:sonnet High", "codex:gpt-6.1 XHigh", "gone:m "]);
    assert.deepEqual(await page.evaluate(`tiersFixture.saved[0].tiers[0].targets.map(target => target.modelId + ':' + target.reasoning.join(','))`), ["sonnet:High", "gpt-6.1:XHigh", "m:"]);
    assert.equal(await page.evaluate(`tiersFixture.saved[0].spaceId`), null);
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Change the model of Sonnet'`, "the focus back on the button of the row");

    // The keyboard: Enter opens the picker, Escape closes it and gives the focus back to the button; Space opens it too.
    await page.evaluate(`document.querySelector('#global [aria-label="Change the model of GPT-6.1"]').focus()`);
    await page.key('Enter', 13, '\r');
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Search models'`, "the search of the picker");
    await page.key('Escape', 27);
    await page.until(`!document.querySelector('.model-tier-add') && document.activeElement?.getAttribute('aria-label') === 'Change the model of GPT-6.1'`, "the picker closed, the focus on its button");
    await page.command("Input.dispatchKeyEvent", { type: "keyDown", key: " ", code: "Space", windowsVirtualKeyCode: 32, text: " " });
    await page.command("Input.dispatchKeyEvent", { type: "keyUp", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Search models'`, "the picker opened with Space");
    await page.key('Escape', 27);
    await page.until(`!document.querySelector('.model-tier-add')`, "the picker closed");
    assert.equal(await page.evaluate(`tiersFixture.saved.length`), 1, "looking changes nothing");

    // A row whose provider is not configured is fixed in its place; with no effort to keep, it starts with the model's.
    assert.equal(await page.evaluate(`document.querySelector('#global [aria-label="Change the model of m"] [data-missing="true"]') !== null`), true);
    await page.click('#global [aria-label="Change the model of m"]');
    await page.clickText('.model-tier-add .bp6-menu-item .bp6-text-overflow-ellipsis', 'GPT-6.2');
    await page.until(`tiersFixture.saved.length === 2`, "the save of the fixed row");
    assert.deepEqual(await page.evaluate(rows("global", "Complex")), ["claude-code:sonnet High", "codex:gpt-6.1 XHigh", "codex:gpt-6.2 Low"]);
    assert.equal(await page.evaluate(`document.querySelector('#global [data-missing="true"]')`), null);

    // The own tiers of a space: the second row changes in its place and keeps both of its efforts, in their order.
    assert.deepEqual(await page.evaluate(rows("space", "Quick")), ["claude-code:haiku Low", "codex:gpt-6.1 Medium,High"]);
    await page.click('#space [aria-label="Change the model of GPT-6.1"]');
    await page.clickText('.model-tier-add .bp6-menu-item .bp6-text-overflow-ellipsis', 'Sonnet');
    await page.until(`tiersFixture.saved.length === 3`, "the save of the space");
    assert.deepEqual(await page.evaluate(rows("space", "Quick")), ["claude-code:haiku Low", "claude-code:sonnet Medium,High"]);
    assert.equal(await page.evaluate(`tiersFixture.saved[2].spaceId`), "business");
    assert.equal(await page.evaluate(`tiersFixture.saved[2].useGlobal`), false);
    // The global tiers are left as they were.
    assert.deepEqual(await page.evaluate(rows("global", "Complex")), ["claude-code:sonnet High", "codex:gpt-6.1 XHigh", "codex:gpt-6.2 Low"]);
    assert.deepEqual(page.problems, []);
  }, { entry: "../modelTierSettings.mount.tsx", fixture: "tiersFixture", rpcFixture: "../demo-api.ts", width: 1400, height: 900 });
});

test("the tiers of a space offer its providers and have their own fallback; the global ones name the spaces of a provider", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#space .model-tier-target')`, "the tiers of the space");
    const heading = (name: string) => `[...document.querySelectorAll('.model-tier-add .bp6-menu-header')].find(entry => entry.textContent.includes(${JSON.stringify(name)}))?.querySelector('.model-tier-provider-note')?.textContent ?? null`;

    // A target of the space whose provider it has not got is said; the picker offers only the providers of the space.
    assert.equal(await page.evaluate(`document.querySelector('#space [aria-label="Change the model of GPT-6.1"] .model-tier-provider').textContent`), "Codex · Not in this space");
    assert.equal(await page.evaluate(`document.querySelector('#space [aria-label="Change the model of GPT-6.1"] [data-outside="true"]') !== null`), true);
    await page.click('#space [aria-label="Add a model to Complex"]');
    await page.until(`${item("Work Opus")}`, "the picker of the space");
    assert.equal(await page.evaluate(`${item("GPT-6.2")} === undefined`), true, "Codex is not a provider of Business");
    assert.equal(await page.evaluate(heading("Work Claude")), null, "a provider of the space is not marked in it");
    await page.clickText('.model-tier-add .bp6-menu-item .bp6-text-overflow-ellipsis', 'Work Opus');
    await page.until(`tiersFixture.saved.length === 1`, "the save of the space");
    assert.deepEqual(await page.evaluate(rows("space", "Complex")), ["work-claude:work-opus High"]);
    assert.equal(await page.evaluate(`tiersFixture.saved[0].spaceId`), "business");

    // The fallback of the space is its own: turning it on saves it with the tiers of the space, not the global one.
    await page.click('#space .model-tier-fallback label.bp6-switch');
    await page.until(`tiersFixture.saved.length === 2`, "the save of the fallback of the space");
    assert.deepEqual(await page.evaluate(`[tiersFixture.saved[1].spaceId, tiersFixture.saved[1].fallback.enabled, tiersFixture.saved[1].tiers[0].targets.length]`), ["business", true, 1]);
    await page.until(`document.querySelector('#space .model-tier-triggers')`, "the failures of the space's fallback");
    assert.equal(await page.evaluate(`document.querySelector('#global .model-tier-fallback input').checked`), false, "the global fallback stays off");

    // The global tiers, the usual settings of every space, offer every provider and name the spaces of those not in all.
    await page.until(`!document.querySelector('.model-tier-add')`, "the picker of the space closed");
    await page.click('#global [aria-label="Add a model to Standard"]');
    await page.until(`${item("Work Opus")} && ${item("GPT-6.2")}`, "the picker of the global tiers, with every provider");
    assert.equal(await page.evaluate(heading("Work Claude")), "Only in Business");
    assert.equal(await page.evaluate(heading("Codex")), "Only in Personal");
    await page.key('Escape', 27);
    await page.until(`!document.querySelector('.model-tier-add')`, "the picker closed");
    assert.equal(await page.evaluate(`document.querySelector('#global [aria-label="Change the model of GPT-6.1"] .model-tier-provider').textContent`), "Codex · Only in Personal");
    assert.deepEqual(page.problems, []);
  }, { entry: "../modelTierSettings.mount.tsx", fixture: "tiersFixture", rpcFixture: "../demo-api.ts", width: 1400, height: 900 });
});
