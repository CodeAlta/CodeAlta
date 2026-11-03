import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

const options = { entry: "../fleet/fleetServerSettings.mount.tsx", fixture: "fleetFixture", rpcFixture: "../demo-api.ts", styles: ["../fleet/fleet.css"], width: 1100,
  height: 800, reducedMotion: true } as const;
// Chooses an option of a native select as the user does: React reads the change event.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;

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
