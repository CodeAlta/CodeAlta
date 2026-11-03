import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../fleet/fleetServerSettings.mount.tsx", fixture: "fleetFixture", rpcFixture: "../demo-api.ts", styles: ["../fleet/fleet.css"], width: 1100,
  height: 800, reducedMotion: true } as const;
// Chooses an option of a native select as the user does: React reads the change event.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;

// Types in an input as the user does: React reads the input event.
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;

test("the owner's computer updates the fleet at a time it chooses, follows the campaign, cancels it and sets the policy", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    // The card of the server says its version and its policy.
    await page.until(`document.querySelector('.fleet-updates-summary')`, "the updates of the server");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-updates-summary').textContent`), "Server 4.0.1 · Manual");

    // Update the fleet… : the latest version, now, every machine by default, with each machine's version.
    await page.clickText(".fleet-server-updates .bp6-button", "Update the fleet…");
    await page.until(`document.querySelector('.fleet-update-dialog .fleet-update-machines')`, "the form");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-update-machines label')].map(label => [label.textContent, label.querySelector('input').checked])`),
      [["Mac mini 4.0.1", true], ["Linux box 4.0.1 · Offline", true]]);
    await page.shot("fleet-update-dialog");

    // At a time, on the Mac only.
    await page.evaluate(choose(".fleet-update-dialog .fleet-telemetry-choices select", "at"));
    await page.until(`document.querySelector('.fleet-update-dialog [role=alert]')?.textContent === 'Choose a date and a time.'`, "a time is needed");
    await page.evaluate(type(".fleet-update-dialog input[type=datetime-local]", "2099-01-02T03:04"));
    await page.evaluate(`document.querySelectorAll('.fleet-update-machines input')[1].click()`);
    await page.clickText(".fleet-update-dialog .bp6-button", "Update");
    await page.until(`!document.querySelector('.fleet-update-dialog')`, "the form closes");
    const [started] = (await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call));
    assert.equal(started.update.notBefore, new Date("2099-01-02T03:04").toISOString(), "the time of this computer, in UTC");
    assert.deepEqual([started.update.version, started.update.feed, started.update.machines], [null, null, ["mac"]]);
    assert.ok(started.update.timeZone, "the time zone the time was chosen in");

    // The campaign shows with each machine, and Cancel before the server's step.
    await page.until(`document.querySelector('.fleet-campaign')?.dataset.state === 'scheduled'`, "the campaign");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-campaign-machines li')].map(item => item.querySelector('.bp6-tag').textContent)`),
      ["Pending", "Switching when idle"], "the server last, then the Mac");
    await page.shot("fleet-update-campaign");
    await page.clickText(".fleet-campaign .bp6-button", "Cancel");
    await page.until(`document.querySelector('.fleet-campaign')?.dataset.state === 'cancelled'`, "cancelled");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.fleet-campaign .bp6-button')].length`), 0, "nothing to cancel any more");

    // Policy… : pinned to a version.
    await page.clickText(".fleet-server-updates .bp6-button", "Policy…");
    await page.until(`document.querySelector('.fleet-policy-dialog select')`, "the policy form");
    await page.evaluate(choose(".fleet-policy-dialog select", "pinned"));
    await page.until(`document.querySelector('.fleet-policy-dialog [role=alert]')?.textContent === 'This is not a version.'`, "a version is needed");
    await page.evaluate(type(".fleet-policy-dialog input", "4.0.1"));
    await page.clickText(".fleet-policy-dialog .bp6-button", "Save");
    await page.until(`!document.querySelector('.fleet-policy-dialog')`, "the policy form closes");
    await page.until(`document.querySelector('.fleet-updates-summary')?.textContent === 'Server 4.0.1 · Pinned · 4.0.1'`, "the policy read again");
    const calls = (await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call));
    assert.deepEqual(calls.slice(1), [{ cancel: "c1" }, { policy: { mode: "pinned", pinnedVersion: "4.0.1", window: null } }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a new device of the person is approved in a popover beside its row, once its words are the ones the device shows", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-device-row[data-status=pending]')`, "the new device");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-device-row[data-status=pending] .fleet-device-title').textContent`), "LaptopNew device");
    assert.equal(await page.evaluate(`document.querySelectorAll('.fleet-device-row[data-status=pending] .fleet-device-words').length`), 0,
      "the words show in the confirmation, where they are compared");

    // Approve… asks first, beside the row, with the six words of the device's key.
    await page.clickText(".fleet-device-row[data-status=pending] .bp6-button", "Approve…");
    await page.until(`document.querySelector('.fleet-device-confirm')`, "the confirmation");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-device-confirm p').textContent`), "Does Laptop show these words?");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-device-confirm .fleet-device-words .bp6-tag')].map(tag => tag.textContent)`),
      ["otter", "river", "robin", "saddle", "salmon", "pearl"]);
    assert.deepEqual(await page.evaluate(`[...window.fleetFixture.calls]`), [], "nothing is approved before the words are confirmed");
    await page.shot("fleet-device-confirm");

    // The approval names the words compared: the host signs the key of those words only.
    await page.clickText(".fleet-device-confirm .bp6-button", "It shows the same words");
    await page.until(`!document.querySelector('.fleet-device-row[data-status=pending]')`, "approved");
    assert.deepEqual((await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call)),
      [{ approve: "laptop", words: "otter river robin saddle salmon pearl" }]);
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the owner makes a recovery key, sees its words once with Copy, and closes them only after typing two of them back", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-server-recovery')`, "the recovery key of the server");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-server-recovery-row .bp6-tag').textContent`), "None");
    await page.clickText(".fleet-server-recovery .bp6-button", "Make a recovery key…");

    // The 32 words, numbered, and Copy.
    await page.until(`document.querySelectorAll('.fleet-recovery-words li').length === 32`, "the words");
    const words = await page.evaluate(`[...document.querySelectorAll('.fleet-recovery-words li')].map(item => item.textContent)`) as string[];
    assert.deepEqual(words.slice(0, 3), ["acid", "acorn", "actor"]);
    await page.shot("fleet-recovery-words");
    await page.clickText(".fleet-recovery-dialog .bp6-button", "Copy");
    await page.until(`[...document.querySelectorAll('.fleet-recovery-dialog .bp6-button')].some(button => button.textContent === 'Copied')`, "copied");

    // A click outside does not close it; the check asks two words by their number.
    await page.evaluate(`document.querySelector('.bp6-overlay-backdrop')?.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))`);
    assert.ok(await page.evaluate(`!!document.querySelector('.fleet-recovery-dialog')`), "still open");
    await page.clickText(".fleet-recovery-dialog .bp6-button", "I wrote them down");
    await page.until(`document.querySelectorAll('.fleet-recovery-check input').length === 2`, "the check");
    const asked = (await page.evaluate(`[...document.querySelectorAll('.fleet-recovery-check label span')].map(span => span.textContent)`) as string[])
      .map(label => Number(label.replace("Word ", "")));
    const done = `[...document.querySelectorAll('.fleet-recovery-dialog .bp6-button')].find(button => button.textContent === 'Done')`;
    await page.evaluate(type(".fleet-recovery-check label:nth-of-type(1) input", words[asked[0] - 1]));
    await page.evaluate(type(".fleet-recovery-check label:nth-of-type(2) input", "wrong"));
    assert.equal(await page.evaluate(`${done}.disabled`), true, "a wrong word keeps it open");
    await page.evaluate(type(".fleet-recovery-check label:nth-of-type(2) input", ` ${words[asked[1] - 1].toUpperCase()} `));
    await page.until(`${done}.disabled === false`, "both words right");
    await page.evaluate(`${done}.click()`);
    await page.until(`!document.querySelector('.fleet-recovery-dialog')`, "the dialog closes");
    await page.until(`document.querySelector('.fleet-server-recovery-row .bp6-tag')?.textContent === 'Kept'`, "the server read again");
    assert.deepEqual((await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call)),
      [{ recoveryKey: "made" }, { copied: words.join(" ") }]);

    // A new one is confirmed beside the row first.
    await page.clickText(".fleet-server-recovery .bp6-button", "New recovery key…");
    await page.until(`document.querySelector('.fleet-server-confirm .bp6-intent-danger')`, "the confirmation");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-server-confirm p').textContent`), "Make a new recovery key?");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("the owner's computer sees the telemetry of the fleet and changes it without the page holding a secret", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    // The card of the server says where the machines send, in one line.
    await page.until(`document.querySelector('.fleet-telemetry-summary')`, "the telemetry of the server");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-telemetry-summary').textContent`),
      "Machines send through this server · traces, metrics → https://otlp.example:4318");

    // Change… opens the form with the setting; the header held by the server shows masked, its value empty.
    await page.clickText(".fleet-server-telemetry .bp6-button", "Change…");
    await page.until(`document.querySelector('.fleet-telemetry-dialog .fleet-telemetry-header')`, "the form");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-telemetry-header input')].map(input => [input.value, input.placeholder, input.type])`),
      [["x-api-key", "Name", "text"], ["", "••••6789", "password"]]);
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-telemetry-signals input')].map(input => input.checked)`), [true, true, false]);
    await page.shot("fleet-telemetry-dialog");

    // Logs too, then Save: the header goes without a value, so that the host keeps the one it holds.
    await page.evaluate(`document.querySelectorAll('.fleet-telemetry-signals input')[2].click()`);
    await page.clickText(".fleet-telemetry-dialog .bp6-button", "Save");
    await page.until(`!document.querySelector('.fleet-telemetry-dialog')`, "the form closes");
    assert.deepEqual((await page.evaluate(`[...window.fleetFixture.calls]`) as string[]).map(call => JSON.parse(call)), [{ enabled: true,
      endpoint: "https://otlp.example:4318", route: "server", signals: ["traces", "metrics", "logs"], headers: [{ name: "x-api-key", value: null }] }]);
    await page.until(`document.querySelector('.fleet-telemetry-summary')?.textContent.includes('traces, metrics, logs')`, "the summary read again");
    await page.shot("fleet-telemetry-card");

    // A form that cannot be signed says why, and Save waits.
    await page.clickText(".fleet-server-telemetry .bp6-button", "Change…");
    await page.until(`document.querySelector('.fleet-telemetry-dialog .fleet-telemetry-choices select')`, "the form again");
    await page.evaluate(choose(".fleet-telemetry-choices select", "grpc"));
    await page.until(`document.querySelector('.fleet-telemetry-dialog [role=alert]')?.textContent === 'The server forwards OTLP over HTTP only.'`, "the reason");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.fleet-telemetry-dialog .bp6-button')].find(button => button.textContent === 'Save').disabled`), true);
    assert.deepEqual(page.problems, []);
  }, options);
});
