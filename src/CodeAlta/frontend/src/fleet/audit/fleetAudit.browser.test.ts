import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../../statistics/browserHarness";
import { testLimit } from "../../testTime";

const options = { entry: "../fleet/audit/fleetAudit.mount.tsx", fixture: "fleetAuditFixture", rpcFixture: "../demo-api.ts",
  styles: ["../secrets/secrets.css", "../fleet/fleet.css", "../fleet/fleetSecrets.css", "../fleet/console/fleetConsole.css", "../fleet/audit/fleetAudit.css"], width: 1300, height: 1000, reducedMotion: true } as const;
// Chooses a value of a facet of the filter bar as the user does: its button, then the value in its menu.
async function choose(page: { click(selector: string): Promise<unknown>; clickText(selector: string, text: string): Promise<unknown>; until(script: string, what: string): Promise<unknown> },
  facet: string, label: string) {
  await page.click(`.fleet-filters-facet[data-facet="${facet}"]`);
  await page.until(`[...document.querySelectorAll('.fleet-filters-menu .bp6-menu-item')].some(item => item.textContent.includes(${JSON.stringify(label)}))`, `the values of ${facet}`);
  await page.clickText(".fleet-filters-menu .bp6-menu-item", label);
}
const calls = `[...window.fleetAuditFixture.calls].map(call => JSON.parse(call))`;
const rows = `[...document.querySelectorAll('.fleet-audit-row')].map(row => row.dataset.action)`;
const overflow = `[...document.querySelectorAll('.fleet-filters, .fleet-audit-head, .fleet-audit-card')].filter(element => element.scrollWidth > element.clientWidth + 1).length`;

test("the log is a table by day that filters by kind, person and result, sorts, pages older records, verifies its chain and exports", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 6`, "the newest records");
    await page.shot("fleet-audit");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.fleet-audit-day-title')].map(title => title.firstChild.textContent)`).then(value => (value as string[]).slice(0, 2)),
      ["Today", "Yesterday"]);
    assert.equal(await page.evaluate(`document.querySelector('.fleet-table-group .fleet-audit-day-title .muted-text').textContent`), "3", "a day says how many records it has");
    assert.equal(await page.evaluate(`document.querySelector('[data-action="policy.stop"] .fleet-audit-label').textContent`), "Stopped the work of the fleet");
    assert.equal(await page.evaluate(`document.querySelector('[data-action="person.invite"] .fleet-audit-result').textContent`), "Refused: forbidden");
    assert.equal(await page.evaluate(overflow), 0, "nothing overflows");

    // A record opens on its details: the target, the address, the hashes and the details as JSON.
    await page.click(`.fleet-audit-row[data-action="policy.stop"]`);
    await page.until(`document.querySelector('.fleet-audit-json')`, "the details");
    assert.match(await page.evaluate(`document.querySelector('.fleet-audit-json').textContent`) as string, /"reason": "A runaway automation"/);

    // Older records come below.
    await page.clickText(".fleet-audit-older", "Show older records");
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 10`, "the older records");
    assert.equal((await page.evaluate(calls) as { audit?: { before: number | null } }[]).at(-1)?.audit?.before, 235);

    // Sorted by who did it, the days give way to one list.
    await page.clickText(".fleet-table-sort", "Who");
    await page.until(`!document.querySelector('.fleet-table-group')`, "no days");
    await page.clickText(".fleet-table-sort", "Time");
    await page.until(`document.querySelector('.fleet-table-group')`, "the days again");

    await choose(page, "what", "People and access");
    await choose(page, "result", "Refused or failed");
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 1`, "the refused change of access");
    assert.deepEqual(await page.evaluate(rows), ["person.invite"]);
    assert.equal(await page.evaluate(`document.querySelector('.fleet-filters-facet[data-facet="result"]').textContent`), "Result: Refused or failed", "a facet of one value says it");
    await page.clickText(".fleet-filters .bp6-button", "Clear");
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

    // Narrow: the table keeps what matters, nothing overflows.
    await page.evaluate(`window.fleetAuditFixture.setWidth(430)`);
    await page.until(`document.querySelector('.ide-shell').getBoundingClientRect().width < 500`, "the narrow page");
    assert.equal(await page.evaluate(overflow), 0, "nothing overflows when narrow");
    await page.shot("fleet-audit-narrow");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("older records asked before the filters changed never join the records of the new filters", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 6`, "the newest records");
    await page.evaluate(`window.fleetAuditFixture.holdOlder()`);
    await page.clickText(".fleet-audit-older", "Show older records");
    await page.until(`window.fleetAuditFixture.calls.some(call => JSON.parse(call).audit?.before === 235)`, "the older records asked");

    // The filters change while the server still reads the older page of the whole log.
    await choose(page, "what", "People and access");
    await choose(page, "result", "Refused or failed");
    await page.until(`document.querySelectorAll('.fleet-audit-row').length === 1`, "the refused change of access");
    await page.evaluate(`window.fleetAuditFixture.releaseOlder()`);
    await new Promise(resolve => setTimeout(resolve, 500));
    assert.deepEqual(await page.evaluate(rows), ["person.invite"], "the late page of the old filters is dropped");
    assert.equal(await page.evaluate(`document.querySelector('.fleet-audit-older')`), null, "and its cursor with it");
    assert.deepEqual(page.problems, []);
  }, options);
});

test("a broken chain is said plainly, and a person who may not read the log is told who may", { skip: !edge, timeout: testLimit(90_000) }, async () => {
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
