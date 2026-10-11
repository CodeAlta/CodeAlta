import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./browserHarness";

test("overview freshness follows stored data, not navigation, pending or failed refreshes", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    const first = "2026-10-08T11:22:33Z", next = "2026-10-09T12:34:56Z";
    const time = `document.querySelector('.stats-data-updated time')?.getAttribute('datetime')`;
    await page.evaluate(`statsFixture.render({ scenario: "ready" }); statsFixture.control.setDataUpdatedAt('${first}')`);
    await page.until(`document.querySelector('.stat-tile-value') && !document.querySelector('.stats-tile-skeleton')`, "the overview");
    assert.equal(await page.evaluate(time), first);
    assert.equal(await page.evaluate(`document.querySelector('.stats-data-updated time').textContent`),
      await page.evaluate(`new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'medium' }).format(new Date('${first}'))`));

    await page.clickText('[role="tab"]', 'Models');
    await page.clickText('[role="tab"]', 'Overview');
    assert.equal(await page.evaluate(time), first, "navigation retains the snapshot's timestamp");
    await page.evaluate(`statsFixture.remount()`);
    await page.until(`document.querySelector('.stat-tile-value') && !document.querySelector('.stats-tile-skeleton')`, "reopened overview");
    assert.equal(await page.evaluate(time), first, "asking again does not invent a new update");

    await page.evaluate(`statsFixture.control.setDataUpdatedAt('${next}'); statsFixture.control.emitData(); statsFixture.control.setLatency(400); statsFixture.control.failNext('summary', 'Freshness test failure')`);
    await page.click('[aria-label="Refresh statistics"]');
    assert.equal(await page.evaluate(time), first, "a pending update still shows the old snapshot");
    await page.until(`document.querySelector('.stats-error')?.textContent.includes('Freshness test failure')`, "failed refresh");
    assert.equal(await page.evaluate(time), first, "failure does not advance freshness");
    await page.clickText('.stats-error button', 'Try again');
    await page.until(`${time} === '${next}'`, "committed update");

    await page.evaluate(`statsFixture.update({ locale: 'fr' })`);
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-data-updated').textContent`), /Dernière mise à jour des données/);
    assert.equal(await page.evaluate(`document.querySelector('.stats-data-updated time').textContent`),
      await page.evaluate(`new Intl.DateTimeFormat('fr', { dateStyle: 'medium', timeStyle: 'medium' }).format(new Date('${next}'))`));
  });
});

test("initial and unavailable data have no invented update time", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`statsFixture.render({ scenario: "ready", latencyMs: 400 }); statsFixture.control.setDataUpdatedAt(undefined)`);
    await page.until(`document.querySelector('.stats-tile-skeleton')`, "initial query");
    assert.equal(await page.evaluate(`document.querySelector('.stats-data-updated time')`), null);
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-data-updated')?.textContent ?? ''`), /Unavailable/);
    await page.until(`document.querySelector('.stat-tile-value') && !document.querySelector('.stats-tile-skeleton')`, "summary without freshness");
    assert.equal(await page.evaluate(`document.querySelector('.stats-data-updated time')`), null);
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-data-updated').textContent`), /Unavailable/);
  });
});
