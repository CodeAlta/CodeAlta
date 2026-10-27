import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";

const options = { entry: "../codeModeSettings.mount.tsx", fixture: "codeModeFixture", rpcFixture: "../demo-api.ts", width: 1300, height: 1000 } as const;
// Chooses an option of a native select as the user does: React reads the change event.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const optionTexts = (selector: string) => `[...document.querySelector(${JSON.stringify(selector)}).options].map(option => option.textContent)`;

test("the mode of every session is On when nothing is set, and the providers and models that choose are links", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#settings-code-mode')`, "the page");
    assert.deepEqual(await page.evaluate(optionTexts("#settings-code-mode")), ["Off", "On", "Only"]);
    assert.equal(await page.evaluate(`document.querySelector('#settings-code-mode').value`), "on");

    await page.evaluate(choose("#settings-code-mode", "off"));
    await page.until(`codeModeFixture.savedModes.length === 1 && document.querySelector('#settings-code-mode').value === 'off'`, "the save of off");
    assert.match(await page.evaluate(`document.querySelector('.code-mode-settings .settings-field-help').textContent`) as string, /^Agents call their tools one at a time/);
    await page.evaluate(choose("#settings-code-mode", "on"));
    await page.until(`codeModeFixture.savedModes.length === 2 && document.querySelector('#settings-code-mode').value === 'on'`, "the save of on");
    assert.deepEqual(await page.evaluate(`codeModeFixture.savedModes`), ["off", "on"]);

    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.code-mode-scope-list > li')].map(row => row.querySelector('.sandbox-scope-name').textContent + ' ' + row.querySelector('.code-mode-scope-mode').textContent)`),
      ["Copilot · copilot-haiku Off"]);
    await page.clickText(".code-mode-scope-list .sandbox-scope-name", "Copilot · copilot-haiku");
    assert.deepEqual(await page.evaluate(`codeModeFixture.opened.map(scope => scope.providerKey + ':' + scope.modelId)`), ["copilot:copilot-haiku"]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a provider and its models choose their mode, and say what they follow without one", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('#provider-code-mode') && document.querySelector('.provider-code-mode-model')?.textContent === 'Claude Haiku 4.5'`, "the field, with its model named");
    assert.deepEqual(await page.evaluate(optionTexts("#provider-code-mode")), ["Follow the settings (On)", "Off", "On", "Only"]);
    assert.equal(await page.evaluate(`document.querySelector('#provider-code-mode').value`), "");
    assert.deepEqual(await page.evaluate(optionTexts(".provider-code-mode-models select")), ["Follow the settings (On)", "Off", "On", "Only"]);
    assert.equal(await page.evaluate(`document.querySelector('.provider-code-mode-models select').value`), "off");

    // The models that can get a mode of their own; one added starts with the mode it has now.
    await page.clickText(".provider-code-mode button", "Code Mode by model");
    await page.until(`document.querySelector('.provider-code-mode-add select')?.options.length === 3`, "the models offered");
    assert.deepEqual(await page.evaluate(optionTexts(".provider-code-mode-add select")), ["Choose a model…", "GPT-6.1 Sol", "Gemini 4 Argon"]);
    await page.evaluate(choose(".provider-code-mode-add select", "gpt-6.1-sol"));
    await page.until(`document.querySelectorAll('.provider-code-mode-models > li').length === 2`, "the row of the added model");

    // The provider's mode is what its models follow.
    await page.evaluate(choose("#provider-code-mode", "only"));
    await page.until(`[...document.querySelectorAll('.provider-code-mode-models select')].every(select => select.options[0].textContent === 'Follow the settings (Only)')`, "the rows follow the provider");
    await page.click('[aria-label="Remove the Code Mode of Claude Haiku 4.5"]');
    await page.until(`document.querySelectorAll('.provider-code-mode-models > li').length === 1`, "a row removed");
    assert.deepEqual(await page.evaluate(`codeModeFixture.savedScopes.map(request => (request.modelId ?? '-') + ':' + (request.mode ?? '-'))`),
      ["gpt-6.1-sol:on", "-:only", "copilot-haiku:-"]);
    assert.deepEqual(page.problems, []);
  }, options);
});
