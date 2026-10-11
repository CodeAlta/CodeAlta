import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "./browserHarness";

const harness = { entry: "./lifetime.mount.tsx", fixture: "lifetimeFixture" };
const state = `JSON.parse(document.querySelector('#answer').textContent)`;
const mount = (page: Page, options: object) => page.evaluate(`lifetimeFixture.mount(${JSON.stringify(options)})`);
const waitTotal = (page: Page, total: number) => page.until(`${state}.total === ${total} && !${state}.refreshing`, `the updated total ${total}`);
const read = (page: Page) => page.evaluate<{ total: number; header: { from: string; to: string; notes: string[] }; periodDays: number }>(state);
const first = { total: 9, from: "2026-10-09", to: "2026-10-09" };
const grown = { total: 12, from: "2026-10-09", to: "2026-10-10" };

test("live Statistics changes do not re-query the UI after each turn", { skip: !edge, timeout: 60_000 }, async () => {
  await withCanvas(async page => {
    await mount(page, { ...first, sessionId: "root" });
    await waitTotal(page, 9);
    await page.evaluate(`lifetimeFixture.change(${JSON.stringify(grown)}, 20261010, 20261010, ['root'])`);
    await new Promise(resolve => setTimeout(resolve, 1400));
    assert.equal(await page.evaluate(`lifetimeFixture.calls().length`), 1, "live ingestion must not force a per-turn UI refresh");
    assert.equal((await read(page)).total, 9);
    await page.evaluate(`lifetimeFixture.advance(599999)`);
    assert.equal(await page.evaluate(`lifetimeFixture.calls().length`), 1);
    await page.evaluate(`lifetimeFixture.change({ total: 15, from: '2026-10-09', to: '2026-10-10' }, 20261010, 20261010, ['root']); lifetimeFixture.advance(1)`);
    await waitTotal(page, 15);
    assert.equal(await page.evaluate(`lifetimeFixture.calls().length`), 2, "later events do not postpone a scheduled refresh");
  }, harness);
});

test("manual refresh, interval changes and hiding preserve pending Statistics changes", { skip: !edge, timeout: 60_000 }, async () => {
  await withCanvas(async page => {
    await mount(page, { ...first, sessionId: "root" });
    await waitTotal(page, 9);
    await page.evaluate(`lifetimeFixture.status(99)`);
    assert.notEqual(await page.evaluate(`${state}.revision`), 99, "ordinary flow status does not rerender on each turn");
    await page.evaluate(`lifetimeFixture.change(${JSON.stringify(grown)}, 20261010, 20261010, ['root']); lifetimeFixture.refresh()`);
    await waitTotal(page, 12);
    assert.equal(await page.evaluate(`${state}.revision`), 99);
    await page.evaluate(`lifetimeFixture.advance(600000)`);
    assert.equal(await page.evaluate(`lifetimeFixture.calls().length`), 2, "manual refresh cancels the old timer");
    await page.evaluate(`lifetimeFixture.change({ total: 20, from: '2026-10-09', to: '2026-10-10' }, 20261010, 20261010, ['root']); lifetimeFixture.interval(1); lifetimeFixture.advance(60000)`);
    await waitTotal(page, 20);
    await page.evaluate(`lifetimeFixture.change({ total: 25, from: '2026-10-09', to: '2026-10-10' }, 20261010, 20261010, ['root']); lifetimeFixture.setVisible(false); lifetimeFixture.advance(600000)`);
    assert.equal(await page.evaluate(`lifetimeFixture.calls().length`), 3);
    await page.evaluate(`lifetimeFixture.setVisible(true)`);
    await waitTotal(page, 25);
  }, harness);
});

test("a session All-time query rereads its expanded lifetime, including a new child announced while hidden", { skip: !edge, timeout: 60_000 }, async () => {
  await withCanvas(async page => {
    await mount(page, { ...first, sessionId: "root" });
    await waitTotal(page, 9);
    assert.equal((await read(page)).periodDays, 1);
    const request = await page.evaluate(`lifetimeFixture.calls()[0]`);
    await page.evaluate(`lifetimeFixture.change(${JSON.stringify(grown)}, 20261010, 20261010, ['root'])`);
    await page.evaluate(`lifetimeFixture.advance(600000)`);
    await waitTotal(page, 12);
    assert.deepEqual((await read(page)).header, { period: "all", from: "2026-10-09", to: "2026-10-10", frequency: "day", timeZone: "UTC",
      coverage: { complete: true, historyState: "done" }, ignoredFilters: [], notes: [] });
    assert.equal((await read(page)).periodDays, 2);
    assert.deepEqual(await page.evaluate(`lifetimeFixture.calls()[1]`), request, "the request and its cache key did not change");

    await page.evaluate(`lifetimeFixture.setVisible(false); lifetimeFixture.change({ total: 17, from: '2026-10-08', to: '2026-10-10' }, 20261008, 20261008, ['new-child'])`);
    await page.evaluate(`lifetimeFixture.advance(600000)`);
    assert.equal(await page.evaluate(`lifetimeFixture.calls().length`), 2, "a hidden canvas asks nothing");
    await page.evaluate(`lifetimeFixture.setVisible(true)`);
    await waitTotal(page, 17);
    const child = await read(page);
    assert.deepEqual([child.header.from, child.header.to, child.periodDays], ["2026-10-08", "2026-10-10", 3]);
    assert.deepEqual(await page.evaluate(`lifetimeFixture.calls()[2]`), request, "only the new descendant was announced, not its root");
  }, harness);
});

test("an unknown session's empty All-time answer is replaced when that session is read outside the old range", { skip: !edge, timeout: 60_000 }, async () => {
  await withCanvas(async page => {
    await mount(page, { total: 0, from: "2026-10-09", to: "2026-10-10", unknown: true, sessionId: "unread" });
    await waitTotal(page, 0);
    assert.deepEqual((await read(page)).header.notes, ["session-not-found"]);
    await page.evaluate(`lifetimeFixture.change({ total: 3, from: '2026-10-08', to: '2026-10-08' }, 20261008, 20261008, ['unread'])`);
    await page.evaluate(`lifetimeFixture.advance(600000)`);
    await waitTotal(page, 3);
    const known = await read(page);
    assert.deepEqual([known.header.from, known.header.to, known.periodDays, known.header.notes], ["2026-10-08", "2026-10-08", 1, []]);
    assert.equal(await page.evaluate(`lifetimeFixture.calls().length`), 2);
  }, harness);
});

test("a scoped All-time answer in flight across lifetime growth is read again instead of cached as fresh", { skip: !edge, timeout: 60_000 }, async () => {
  await withCanvas(async page => {
    await mount(page, { ...first, sessionId: "root", deferFirst: true });
    await page.until(`lifetimeFixture.calls().length === 1`, "the old answer in flight");
    await page.evaluate(`lifetimeFixture.change(${JSON.stringify(grown)}, 20261010, 20261010, ['new-child'])`);
    await page.evaluate(`lifetimeFixture.advance(600000)`);
    await page.until(`lifetimeFixture.epoch() === 1`, "the change while the answer is in flight");
    await page.evaluate(`lifetimeFixture.resolve()`);
    await waitTotal(page, 12);
    assert.deepEqual([(await read(page)).header.to, (await read(page)).periodDays], ["2026-10-10", 2]);
    assert.equal(await page.evaluate(`lifetimeFixture.calls().length`), 2);
  }, harness);
});

test("lifetime changes keep fixed session periods and unrestricted All-time entries outside their days cached", { skip: !edge, timeout: 60_000 }, async () => {
  await withCanvas(async page => {
    for (const scope of [{ sessionId: "root", period: "2026-10-09..2026-10-09" }, { sessionId: null }]) {
      await mount(page, { ...first, ...scope });
      await waitTotal(page, 9);
      await page.evaluate(`lifetimeFixture.change(${JSON.stringify(grown)}, 20261010, 20261010, ['another-session'])`);
      await page.evaluate(`lifetimeFixture.advance(600000)`);
      await page.until(`lifetimeFixture.epoch() === 1`, "the unrelated change being applied");
      assert.equal(await page.evaluate(`lifetimeFixture.calls().length`), 1);
      assert.equal((await read(page)).total, 9);
    }
  }, harness);
});
