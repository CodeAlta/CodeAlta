import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../../statistics/browserHarness";

const options = { entry: "../fleet/audit/fleetAudit.mount.tsx", fixture: "fleetAuditFixture", rpcFixture: "../demo-api.ts",
  styles: ["../secrets/secrets.css", "../fleet/fleet.css", "../fleet/fleetSecrets.css", "../fleet/audit/fleetAudit.css"], width: 1300, height: 1000, reducedMotion: true } as const;
// Chooses an option of a native select as the user does.
const choose = (selector: string, value: string) => `(() => { const select = document.querySelector(${JSON.stringify(selector)});
  Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)});
  select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const calls = `[...window.fleetAuditFixture.calls].map(call => JSON.parse(call))`;
const rows = `[...document.querySelectorAll('.fleet-audit-row')].map(row => row.dataset.action)`;
const overflow = `[...document.querySelectorAll('.fleet-audit-summary, .fleet-audit-filters, .fleet-audit-head')].filter(element => element.scrollWidth > element.clientWidth + 1).length`;

test("the log reads by day, filters by kind, person and result, pages older records, verifies its chain and exports", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 6`, "the newest records");
    await page.shot("fleet-audit");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-audit-day-title')].map(title => title.textContent)`).then(value => (value as string[]).slice(0, 2)),
      ["Today", "Yesterday"]);
    assert.equal(await page.evaluate(`document.querySelector('[data-action="policy.stop"] .fleet-audit-label').textContent`), "Stopped the work of the fleet");
    assert.equal(await page.evaluate(`document.querySelector('[data-action="person.invite"] .fleet-audit-result').textContent`), "Refused: forbidden");
    assert.equal(await page.evaluate(overflow), 0, "nothing overflows");

    // A record opens on its details: the target, the address, the hashes and the details as JSON.
    await page.click(`[data-action="policy.stop"] .fleet-audit-summary`);
    await page.until(`document.querySelector('.fleet-audit-json')`, "the details");
    assert.match(await page.evaluate(`document.querySelector('.fleet-audit-json').textContent`) as string, /"reason": "A runaway automation"/);

    // Older records come below.
    await page.clickText(".fleet-audit-older", "Show older records");
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 10`, "the older records");
    assert.equal((await page.evaluate(calls) as { audit?: { before: number | null } }[]).at(-1)?.audit?.before, 235);

    await page.evaluate(choose(`select[aria-label="What"]`, "access"));
    await page.evaluate(choose(`select[aria-label="Result"]`, "failure"));
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 1`, "the refused change of access");
    assert.deepEqual(await page.evaluate(rows), ["person.invite"]);
    await page.clickText(".fleet-audit-filters .bp6-button", "Clear the filters");
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 6`, "the whole log again");

    await page.clickText(".fleet-audit-actions .bp6-button", "Verify the chain");
    await page.until(`document.querySelector('.fleet-audit-check')`, "the check");
    assert.match(await page.evaluate(`document.querySelector('.fleet-audit-check').textContent`) as string, /The chain holds: 240 records/);

    await page.clickText(".fleet-audit-actions .bp6-button", "Export CSV…");
    await page.until(`document.querySelector('.fleet-audit-exported')`, "the export");
    await page.clickText(".fleet-audit-exported .bp6-button", "Show in folder");
    await page.until(`window.fleetAuditFixture.calls.some(call => call.includes('reveal'))`, "shown in its folder");
    assert.deepEqual((await page.evaluate(calls) as { export?: unknown }[]).find(call => call.export)?.export, { format: "csv",
      filters: { actions: null, personId: null, deviceId: null, result: null, target: null, address: null, from: null, to: null, text: null } });
    await page.shot("fleet-audit-verified");

    // Narrow: rows stack, nothing overflows.
    await page.evaluate(`window.fleetAuditFixture.setWidth(430)`);
    await page.until(`document.querySelector('.ide-shell').getBoundingClientRect().width < 500`, "the narrow page");
    assert.equal(await page.evaluate(overflow), 0, "nothing overflows when narrow");
    await page.shot("fleet-audit-narrow");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a broken chain is said plainly, and a person who may not read the log is told who may", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 6`, "the log");
    await page.evaluate(`window.fleetAuditFixture.broken()`);
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 6`, "the log again");
    await page.clickText(".fleet-audit-actions .bp6-button", "Verify the chain");
    await page.until(`document.querySelector('.fleet-audit-check.bp6-intent-danger')`, "the broken chain");
    assert.match(await page.evaluate(`document.querySelector('.fleet-audit-check').textContent`) as string, /breaks at record 236/);

    await page.evaluate(`window.fleetAuditFixture.member()`);
    await page.until(`document.querySelector('.fleet-audit-closed')`, "the page of a member");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-audit-card')`), null);
    assert.deepEqual(page.problems, []);
  }, options);
});
