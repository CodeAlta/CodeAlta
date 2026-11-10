import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const options = { entry: "../fleet/fleetSecretsSettings.mount.tsx", fixture: "fleetSecretsFixture", rpcFixture: "../demo-api.ts",
  styles: ["../secrets/secrets.css", "../fleet/fleet.css", "../fleet/fleetSecrets.css"], width: 1300, height: 900, reducedMotion: true } as const;
// Types in an input as the user does: React reads the input event.
const type = (selector: string, value: string) => `(() => { const input = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)});
  input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
// Chooses an option of a native select as the user does.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const calls = `[...window.fleetSecretsFixture.calls].map(call => JSON.parse(call))`;

test("the values of a server show their scope, machines and rotation, and a value is set, rotated and given from popovers beside its row",
  { skip: !edge, timeout: testLimit(90_000) }, async () => {
    await withCanvas(async page => {
      await page.until(`document.querySelectorAll('.fleet-secret-row').length === 3`, "the values");
      await page.shot("fleet-secrets-large");
      // Each row: its scope, its version, host-only, and the machines that hold it; a revoked machine held the first one.
      const tags = await page.evaluate(`[...document.querySelectorAll('.fleet-secret-row')].map(row => [row.dataset.fleetSecret,
        [...row.querySelectorAll('.fleet-secret-tags .bp6-tag')].map(tag => tag.textContent)])`) as [string, string[]][];
      assert.deepEqual(tags, [
        ["GRAFANA_SERVICE_ACCOUNT_TOKEN_PRODUCTION", ["All my work", "Version 2", "Rotate: a revoked machine held it"]],
        ["DEPLOY_TOKEN", ["Environment: production", "Version 1", "MCP servers and plugin tools only"]],
        ["GRAFANA_URL", ["Space: Work", "Version 1", "Variable"]],
      ]);
      assert.match(await page.evaluate(`document.querySelector('[data-fleet-secret=DEPLOY_TOKEN] .secret-meta').textContent`) as string, /^On Mac mini, Linux box · Set /);
      assert.equal(await page.evaluate(`document.querySelector('[data-fleet-secret=GRAFANA_URL] .fleet-secret-value').textContent`), "https://grafana.example.com",
        "a variable is read back");
      assert.equal(await page.evaluate(`document.querySelectorAll('[data-fleet-secret=DEPLOY_TOKEN] .fleet-secret-value').length`), 0, "a secret never is");
      const overflow = await page.evaluate(`[...document.querySelectorAll('.secret-row')].filter(row => row.scrollWidth > row.clientWidth + 1).length`);
      assert.equal(overflow, 0, "nothing overflows");

      // Add a value…: a host-only secret of a project, for the Mac only.
      await page.clickText(".fleet-secrets-head .bp6-button", "Add a value…");
      await page.until(`document.querySelector('.fleet-secret-new')`, "the form");
      await page.evaluate(type(".fleet-secret-new input[aria-label=Name]", "SENTRY_TOKEN"));
      await page.evaluate(type(".fleet-secret-new input[aria-label=Value]", "sntrys_0123456789"));
      await page.evaluate(choose(".fleet-secret-new select[aria-label='Where it applies']", "project:api"));
      // What host-only means is said where it is chosen: given to MCP servers and plugin tools, never to the agent's shell.
      assert.equal(await page.evaluate(`document.querySelector('.fleet-secret-host-only').textContent`), "Only for MCP servers and plugin tools, never the agent's shell");
      assert.equal(await page.evaluate(`document.querySelector('[data-fleet-secret=DEPLOY_TOKEN] .fleet-secret-tags .bp6-tag:last-child').title`),
        "Given to MCP servers and plugin tools, never to the agent's shell.");
      await page.evaluate(`document.querySelector('.fleet-secret-host-only input').click()`);
      await page.evaluate(`[...document.querySelectorAll('.fleet-secret-new [role=group] label')].find(label => label.textContent === 'Linux box').querySelector('input').click()`);
      await page.shot("fleet-secrets-add");
      await page.clickText(".fleet-secret-new .bp6-button", "Add");
      await page.until(`!document.querySelector('.fleet-secret-new:not(.fleet-environment-new)')`, "the form closes");
      assert.deepEqual((await page.evaluate(calls) as unknown[])[0], { set: { kind: "secret", name: "SENTRY_TOKEN", scopeKind: "project", scopeId: "api", value: "sntrys_0123456789",
        hostOnly: true, machines: ["mac"] } });

      // Rotate, beside its row; Machines gives the Linux box its copy.
      await page.clickText("[data-fleet-secret=GRAFANA_SERVICE_ACCOUNT_TOKEN_PRODUCTION] .bp6-button", "Rotate");
      await page.until(`document.querySelector('.secret-rotate input')`, "the new value");
      await page.evaluate(type(".secret-rotate input", "glsa_rotated"));
      await page.clickText(".secret-rotate .bp6-button", "Rotate");
      await page.until(`!document.querySelector('.secret-rotate')`, "rotated");
      await page.clickText("[data-fleet-secret=GRAFANA_SERVICE_ACCOUNT_TOKEN_PRODUCTION] .bp6-button", "Machines");
      await page.until(`document.querySelector('.fleet-secret-machines')`, "the machines");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-secret-machines label')].map(label => [label.textContent, label.querySelector('input').checked])`),
        [["Mac mini", true], ["Linux box", false]]);
      await page.evaluate(`[...document.querySelectorAll('.fleet-secret-machines label')].find(label => label.textContent === 'Linux box').querySelector('input').click()`);
      await page.until(`window.fleetSecretsFixture.calls.length === 3`, "given");
      assert.deepEqual((await page.evaluate(calls) as unknown[]).slice(1), [{ rotate: "s1", value: "glsa_rotated" }, { give: "s1", machine: "linux" }]);
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("a run that waits for an environment is approved in a popover, and the page stays whole in a narrow window", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelector('.fleet-approval-row')`, "the run that waits");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-approval-row .secret-reference').textContent`), "Deploy the API");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-environment-row .bp6-tag')].map(tag => tag.textContent)`),
      ["Approved by Alex", "On machines tagged prod"]);
    await page.clickText(".fleet-approval-row .bp6-button", "Approve…");
    await page.until(`document.querySelector('.fleet-server-confirm')`, "the confirmation");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-server-confirm p').textContent`), "Approve this run of production on Mac mini?");
    assert.deepEqual(await page.evaluate(calls), [], "nothing is signed before it is confirmed");
    await page.clickText(".fleet-server-confirm .bp6-button", "Approve");
    await page.until(`!document.querySelector('.fleet-approval-row')`, "approved, it no longer waits");
    assert.deepEqual(await page.evaluate(calls), [{ approve: "a1" }]);

    // A window of 900 by 650: rows wrap, none overflows.
    await page.evaluate(`window.fleetSecretsFixture.setWidth(430)`);
    await page.until(`document.querySelector('.fleet-secrets-settings').getBoundingClientRect().width < 450`, "the narrow page");
    await page.shot("fleet-secrets-narrow");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.secret-row')].filter(row => row.scrollWidth > row.clientWidth + 1).length`), 0, "no row overflows");
    assert.deepEqual(page.problems, []);
  }, options);
});
