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
