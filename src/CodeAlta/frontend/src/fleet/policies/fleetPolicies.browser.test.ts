import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../../statistics/browserHarness";
import { testLimit } from "../../testTime";

const options = { entry: "../fleet/policies/fleetPolicies.mount.tsx", fixture: "fleetPoliciesFixture", rpcFixture: "../demo-api.ts",
  styles: ["../secrets/secrets.css", "../fleet/fleet.css", "../fleet/fleetSecrets.css", "../fleet/access/fleetAccess.css", "../fleet/console/fleetConsole.css", "../fleet/policies/fleetPolicies.css"], width: 1300, height: 1100,
  reducedMotion: true } as const;
// Types in an input as the user does: React reads the input event.
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
const calls = `[...window.fleetPoliciesFixture.calls].map(call => JSON.parse(call))`;
const overflow = `[...document.querySelectorAll('.fleet-policy-field, .fleet-secrets-head, .fleet-policy-version')].filter(element => element.scrollWidth > element.clientWidth + 1).length`;

test("an owner stops chosen machines in one confirmed action, the title bar says so, and lifting lets the work go on", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-policy-switch') && document.querySelectorAll('.fleet-policy-version').length === 2`, "the policy");
    await page.shot("fleet-policies");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-stop-indicator')`), null, "nothing in the title bar while the work goes on");
    assert.equal(await page.evaluate(overflow), 0, "nothing overflows");
    // The versions are a table, the newest first, the one in force marked; a row opens all it changed.
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-policy-version')].map(row => row.dataset.version)`), ["2", "1"]);
    assert.equal(await page.evaluate(`document.querySelector('.fleet-policy-version[data-version="2"] .bp6-tag')?.textContent`), "In force");
    await page.click(`.fleet-policy-version[data-version="1"]`);
    await page.until(`document.querySelector('.fleet-policy-version-details li')`, "the changes of a version");
    await page.click(`.fleet-policy-version[data-version="1"]`);

    await page.clickText(".fleet-policy-switch .bp6-button", "Stop the fleet…");
    await page.until(`document.querySelector('.fleet-policy-stop-form')`, "the confirmation beside the button");
    await page.clickText(".fleet-policy-stop-form label", "Chosen machines");
    await page.until(`document.querySelector('.fleet-policy-choices')`, "the machines");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.fleet-policy-stop-form .bp6-button')].find(button => button.textContent === 'Stop the work').disabled`), true,
      "a stop of machines names one");
    await page.clickText(".fleet-policy-choices label", "Mac mini");
    await page.evaluate(type(".fleet-policy-stop-form input[aria-label^='Why']", "A runaway automation"));
    await page.shot("fleet-policies-stop-confirm");
    await page.clickText(".fleet-policy-stop-form .bp6-button", "Stop the work");
    await page.until(`document.querySelector('.fleet-policy-stopped')`, "the stop");
    const signed = (await page.evaluate(calls) as { sign?: { stop: { scope: string; machines: string[]; reason: string }; changes: string[] } }[]).find(call => call.sign)!.sign!;
    assert.deepEqual([signed.stop.scope, signed.stop.machines, signed.stop.reason], ["machines", ["mini"], "A runaway automation"]);
    assert.deepEqual(signed.changes, ["STOP: the work of the fleet stops on Mac mini"], "this computer signs what it read");
    assert.match(await page.evaluate(`document.querySelector('.fleet-policy-stopped').textContent`) as string, /stopped on Mac mini.*A runaway automation/s);
    await page.until(`document.querySelector('.fleet-stop-indicator')`, "the stop in the title bar");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-stop-indicator').textContent`), "Fleet stopped: 1 machine");
    await page.shot("fleet-policies-stopped");
    await page.click(".fleet-stop-indicator");
    await page.until(`window.fleetPoliciesFixture.calls.some(call => call.includes('fleetPolicies'))`, "the indicator opens the page");

    await page.clickText(".fleet-policy-switch .bp6-button", "Let the work go on…");
    await page.until(`document.querySelector('.fleet-policy-lift-form')`, "the confirmation of the lift");
    await page.clickText(".fleet-policy-lift-form .bp6-button", "Let the work go on");
    await page.until(`!document.querySelector('.fleet-policy-stopped') && !document.querySelector('.fleet-stop-indicator')`, "the work goes on");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a signer changes a rule and signs what this computer reads; a person who does not sign reads only", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-policy-rules fieldset')`, "the rules");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.fleet-policy-actions .bp6-button')].find(button => button.textContent === 'Review and sign…').disabled`), true,
      "nothing to sign before a change");
    await page.evaluate(type(".fleet-policy-field[data-rule='maxSessionMinutes'] input", "90"));
    assert.equal(await page.evaluate(`document.querySelectorAll('.fleet-policy-changed').length`), 1, "the field changed says so");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-policy-field[data-set]')].map(field => field.dataset.rule)`),
      ["providers", "sandbox", "agentStarts", "maxSessionMinutes"], "the fields that set a rule are marked");
    await page.shot("fleet-policies-changed");
    await page.clickText(".fleet-policy-actions .bp6-button", "Review and sign…");
    await page.until(`document.querySelector('.fleet-policy-review li')`, "the review");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-policy-review li')].map(item => item.textContent)`), ["The rules change"]);
    await page.clickText(".fleet-policy-review .bp6-button", "Sign");
    await page.until(`window.fleetPoliciesFixture.calls.some(call => call.includes('"maxSessionMinutes":90'))`, "signed");

    await page.evaluate(`window.fleetPoliciesFixture.setWidth(430)`);
    await page.until(`document.querySelector('.ide-shell').getBoundingClientRect().width < 500`, "the narrow page");
    assert.equal(await page.evaluate(overflow), 0, "nothing overflows when narrow");
    await page.shot("fleet-policies-narrow");

    await page.evaluate(`window.fleetPoliciesFixture.member()`);
    await page.until(`document.querySelector('.fleet-policy-rules fieldset')?.disabled === true`, "the page of a person who does not sign");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.fleet-policy-switch .bp6-button')].length`), 0, "no kill switch to press");
    assert.deepEqual(page.problems, []);
  }, options);
});
