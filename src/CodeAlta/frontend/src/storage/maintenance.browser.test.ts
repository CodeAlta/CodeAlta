import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

test("App-owned maintenance survives Settings navigation, replaces keyed toasts and detaches by epoch", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    const f = "window.maintenanceFixture";
    const snapshot = "document.querySelector('#maintenance-snapshot').textContent";
    const messages = "[...document.querySelectorAll('.bp6-toast-message')].map(e => e.textContent)";
    await page.until(`${f}.streams.length === 2`, "both App subscriptions");
    await page.evaluate(`${f}.streams.forEach(stream => stream.emit('running'))`);
    await page.until(`${messages}.includes('Clearing scratchpad…') && ${messages}.includes('Session cleanup is running…')`, "two keyed running notices");
    await page.evaluate(`${f}.streams.forEach(stream => stream.emit('running')); ${f}.page(false)`);
    await page.until(`!document.querySelector('#storage-page')`, "Settings navigation");
    assert.deepEqual(await page.evaluate(`${f}.streams.map(stream => stream.signal.aborted)`), [false, false]);
    assert.equal(await page.evaluate(`${f}.streams.length`), 2, "Navigating Settings does not recreate observers.");
    await page.evaluate(`${f}.streams[0].emit('completed'); ${f}.streams[1].emit('skipped')`);
    await page.until(`${messages}.includes('Scratchpad cleared.') && ${messages}.includes('Session cleanup is not available yet.')`, "final outcomes while Settings is closed");
    assert.equal(await page.evaluate(`${messages}.length`), 2, "Outcomes replace running toasts rather than adding duplicates.");
    await page.evaluate(`${f}.page(true)`);
    await page.until(`document.querySelector('#storage-page') && JSON.parse(${snapshot}).maintenance.state === 'completed'`, "latest status on reopening Settings");

    const completed = await page.evaluate(snapshot);
    await page.evaluate(`${f}.available(false)`);
    await page.until(`${f}.streams.every(stream => stream.signal.aborted)`, "outage detaches observation");
    assert.equal(await page.evaluate(snapshot), completed, "A liveness outage must not erase known terminal outcomes.");
    await page.evaluate(`${f}.available(true)`);
    await page.until(`${f}.streams.length === 4`, "same epoch reconnects observation");
    assert.equal(await page.evaluate(snapshot), completed, "Recovery keeps final outcomes while waiting for replay.");
    await page.evaluate(`${f}.streams[2].emit('completed'); ${f}.streams[3].emit('skipped')`);
    assert.equal(await page.evaluate(snapshot), completed);

    await page.evaluate(`${f}.epoch('second')`);
    await page.until(`${f}.streams.length === 6 && ${snapshot} === '{}'`, "new epoch clears the old snapshot");
    assert.deepEqual(await page.evaluate(`${f}.streams.map(stream => stream.signal.aborted)`), [true, true, true, true, false, false]);
    await page.evaluate(`${f}.streams[0].emit('partial'); ${f}.streams[1].emit('completed'); ${f}.streams[4].emit('running'); ${f}.streams[5].emit('running')`);
    await page.until(`JSON.parse(${snapshot}).maintenance.state === 'running' && JSON.parse(${snapshot}).retention.state === 'running'`, "only the new epoch publishes");
    await page.until(`${messages}.includes('Clearing scratchpad…') && ${messages}.includes('Session cleanup is running…')`, "keys reused for the new epoch");
    assert.equal(await page.evaluate(`${messages}.length`), 2);
    await page.evaluate(`${f}.available(false)`);
    await page.until(`${f}.streams.every(stream => stream.signal.aborted) && JSON.parse(${snapshot}).maintenance.state === 'unavailable' && JSON.parse(${snapshot}).retention.state === 'unavailable'`, "outage cannot leave unfinished maintenance showing as running");
    await page.evaluate(`${f}.available(true)`);
    await page.until(`${f}.streams.length === 8`, "unfinished maintenance reconnects");
    await page.evaluate(`${f}.streams[6].emit('running'); ${f}.streams[7].emit('running')`);
    await page.until(`${messages}.includes('Clearing scratchpad…') && ${messages}.includes('Session cleanup is running…')`, "running replay after recovery");
    await page.evaluate(`${f}.unmount()`);
    await page.until(`${f}.streams.every(stream => stream.signal.aborted)`, "unmount detaches both streams");
    await page.until(`${messages}.length === 2 && ${messages}.includes('Scratchpad cleanup status is unavailable.') && ${messages}.includes('Session cleanup status is unavailable.')`, "detachment replaces indefinite running toasts");
  }, { entry: "../storage/maintenance.mount.tsx", fixture: "maintenanceFixture" });
});
