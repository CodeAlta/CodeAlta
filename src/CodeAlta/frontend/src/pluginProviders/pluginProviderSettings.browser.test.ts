import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const options = { entry: "../pluginProviders/pluginProviderSettings.mount.tsx", fixture: "pluginSettingsFixture", rpcFixture: "../demo-api.ts", width: 1600, height: 1000 } as const;

test("the panel of a plugin provider shows once after the form changes around it", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    const panels = `document.querySelectorAll('.plugin-provider-panel').length`;
    await page.until(`document.querySelector('.provider-state-problem') && document.querySelector('.plugin-provider-command code')`, "the signed-out provider and its command");
    assert.equal(await page.evaluate(panels), 1);
    // Ready once tested: the problem above the fields goes away, and the panel must not be left behind a second time.
    await page.clickText('.provider-settings-footer button', 'Test selected provider');
    await page.until(`pluginSettingsFixture.probes === 1 && !document.querySelector('.provider-state-problem')`, "the tested provider");
    assert.equal(await page.evaluate(panels), 1);
  }, options);
});

test("the panel of a plugin provider shows what the provider runs after its update", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    const command = `document.querySelector('.plugin-provider-command code')?.textContent`;
    await page.until(`${command} === 'copilot --acp'`, "the command before the update");
    await page.clickText('.plugin-provider-command button', 'Update to 1.0.2');
    await page.until(`${command} === 'copilot-1.0.2 --acp'`, "the command of the updated provider");
    assert.equal(await page.evaluate(`document.querySelectorAll('.plugin-provider-command button').length`), 0, "no update is offered any more");
    assert.match(String(await page.evaluate(`document.querySelector('.plugin-provider-panel').textContent`)), /Updated to 1\.0\.2\./);
  }, options);
});
