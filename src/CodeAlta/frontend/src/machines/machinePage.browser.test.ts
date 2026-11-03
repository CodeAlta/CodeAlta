import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../machines/machinePage.mount.tsx", fixture: "machineFixture", rpcFixture: "../demo-api.ts", styles: ["../machines/machines.css"], width: 1100,
  height: 900, reducedMotion: true } as const;
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
const calls = async (page: { evaluate: (script: string) => Promise<unknown> }) => (await page.evaluate(`[...window.machineFixture.calls]`) as string[]).map(call => JSON.parse(call));

test("the computer that manages a machine changes what its devices may do, its projects and its version from its page", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.machine-devices [role=row]:nth-child(2)')`, "the devices");
    // This computer first, with every permission and its own switch to stop managing.
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.machine-device:not(.machine-device-head) .machine-device-name strong')].map(item => item.textContent)`),
      ["desktop", "phone"]);
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.machine-device:nth-child(3) input[type=checkbox]')].map(input => input.checked)`),
      [true, false, false, false, false, false], "the phone may only view");
    await page.shot("machine-page");

    // A switch gives a permission at once.
    await page.evaluate(`document.querySelector('input[aria-label="Move — phone"]').click()`);
    await page.until(`document.querySelector('input[aria-label="Move — phone"]')?.checked === true`, "move given");

    // Revoke asks in a popover beside the row.
    await page.evaluate(`document.querySelector('button[aria-label="Revoke phone"]').click()`);
    await page.until(`document.querySelector('.machine-confirm')`, "the confirmation");
    assert.equal(await page.evaluate(`document.querySelector('.machine-confirm p').textContent`), "Revoke phone?");
    await page.clickText(".machine-confirm .bp6-button", "Revoke");
    await page.until(`document.querySelector('.machine-device[data-revoked=true]')`, "the phone revoked");

    // A URL clones, and the machine's note is shown.
    await page.evaluate(type(".machine-project-add input", "https://github.com/owner/repo"));
    await page.until(`document.querySelector('.machine-project-add button[type=submit]')?.textContent === 'Clone'`, "the Clone button");
    await page.clickText(".machine-project-add .bp6-button", "Clone");
    await page.until(`[...document.querySelectorAll('.machine-projects strong')].some(item => item.textContent === 'repo')`, "the clone listed");
    await page.until(`document.querySelector('.machine-section[aria-label=Projects]')?.textContent.includes('Cloned into')`, "the note");

    // Check for updates, then the update it found.
    await page.clickText(".machine-section[aria-label=Updates] .bp6-button", "Check for updates");
    await page.until(`[...document.querySelectorAll('.machine-section[aria-label=Updates] .bp6-button')].some(button => button.textContent === 'Update to 4.0.1')`, "the update");
    await page.clickText(".machine-section[aria-label=Updates] .bp6-button", "Update to 4.0.1");
    await page.until(`document.querySelector('.machine-section[aria-label=Updates] [role=status]')?.textContent.includes('switches to 4.0.1')`, "staged");

    // The log.
    await page.clickText(".machine-section[aria-label=Log] .bp6-button", "Show the log");
    await page.until(`document.querySelector('.machine-log')?.textContent.includes('CodeAlta Node started.')`, "the log");

    assert.deepEqual(await calls(page), [{ permission: ["phone", "move", true] }, { revoke: "phone" }, { clone: "https://github.com/owner/repo" },
      { update: "check" }, { update: "install" }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("giving up managing a machine asks first, and a machine this computer does not manage says how to be given it", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('input[aria-label="Manage — desktop"]')`, "this computer's switch");
    await page.evaluate(`document.querySelector('input[aria-label="Manage — desktop"]').click()`);
    await page.until(`document.querySelector('.machine-confirm')?.textContent.includes('Stop managing this machine from here?')`, "the confirmation");
    assert.deepEqual(await calls(page), [], "nothing changes before the person confirms");

    await page.evaluate(`window.machineFixture.show('other')`);
    await page.until(`document.querySelector('.machine-command')`, "the command for the owner");
    assert.equal(await page.evaluate(`document.querySelector('.machine-command').textContent`), "altanode permissions me --allow manage");
    assert.equal(await page.evaluate(`document.querySelector('.machine-devices')`), null, "no switch for a machine this computer does not manage");
    await page.shot("machine-page-not-managed");
    assert.deepEqual(page.problems, []);
  }, options);
});
