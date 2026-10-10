import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "./browserHarness";

// The module of the Statistics plugin (canvas.tsx) in headless Edge, under the production content security policy, drawn under StrictMode with an `alta` object
// whose calls go to a plugin played by the fixture, through JSON. Every test ends by checking that the page raised no exception and no console error.

type Call = { name: string; input: any };
const settled = `document.querySelector('.statistics-canvas') && !document.querySelector('.stats-block[data-state="loading"]') && !document.querySelector('.stats-tile-skeleton') && document.querySelectorAll('.stats-block').length > 0`;
const options = { entry: "./canvas.mount.tsx", fixture: "canvasFixture" };
const calls = (page: Page) => page.evaluate<Call[]>(`canvasFixture.calls()`);
const names = (list: Call[]) => list.map(call => call.name);
const render = (page: Page, parameters: object = {}) => page.evaluate(`canvasFixture.render(${JSON.stringify(parameters)})`);
const idle = (milliseconds = 300) => new Promise(resolve => setTimeout(resolve, milliseconds));

test("the module asks the plugin for the spaces first, draws the pages over what it answers, and asks nothing twice under StrictMode", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready", spaceId: "work" });
    await page.until(settled, "the overview");

    const all = await calls(page);
    assert.equal(all[0].name, "statistics.context", "the directory comes before the first question");
    assert.ok(names(all).includes("statistics.status"));
    const questions = all.filter(call => !["statistics.context", "statistics.status"].includes(call.name));
    assert.ok(questions.length >= 7, `${questions.length} questions`);
    const keys = questions.map(call => JSON.stringify(call));
    assert.equal(new Set(keys).size, keys.length, "no question twice");
    // The space of the tab is the filter the canvas starts with, and the name is the one the plugin gave.
    assert.ok(questions.filter(call => call.input.request?.period === "30d").every(call => call.input.request.filter?.space === "work"));
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-text').textContent`), "Space: Work");
    // The events of the plugin are listened to once, and the double run of the effects leaves nothing listening twice.
    const counts = await page.evaluate<{ open: number; closed: number }>(`({ ...canvasFixture.subscriptions })`);
    assert.equal(counts.open - counts.closed, 1, "one listening is left");
    await page.shot("canvas-module-dark");
  }, options);
});

test("the space that holds every project is no filter, and a canvas opened with the key of a project starts on that project", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready", spaceId: "default" });
    await page.until(settled, "the overview");
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-text')?.textContent ?? null`), null, "no chip: the default space has every project");

    await page.evaluate(`canvasFixture.clearCalls()`);
    await render(page, { scenario: "ready", spaceId: "work", key: "project:proj-codealta", instanceId: "instance-2" });
    await page.until(settled, "the overview of a project");
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-text').textContent`), "Project: CodeAlta");
    const summary = (await calls(page)).filter(call => call.name === "statistics.summary" && call.input.request.period === "30d");
    assert.ok(summary.length > 0 && summary.every(call => call.input.request.filter.project === "proj-codealta" && call.input.request.filter.space === undefined));
  }, options);
});

test("the choice of the first time is one call of the plugin, and the status the plugin tells moves the bar", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "first-time" });
    await page.until(`document.querySelector('.stats-first')`, "the card of the first time");
    await page.shot("canvas-module-first-time");
    await page.evaluate(`canvasFixture.clearCalls()`);
    await page.clickText('.stats-first button', "Last 90 days");
    await page.until(`document.querySelector('.stats-history[data-view="reading"]')`, "the bar of the reading");
    const chosen = (await calls(page)).filter(call => call.name === "statistics.choose-history");
    assert.deepEqual(chosen.map(call => call.input), [{ kind: "days", days: 90 }]);

    // Paused by the plugin: the bar says so; the page was not asked.
    await page.evaluate(`canvasFixture.control.setStatus({ state: "paused" })`);
    await page.until(`document.querySelector('.stats-history[data-view="paused"]')`, "the paused bar");
    await page.evaluate(`canvasFixture.clearCalls()`);
    await page.clickText('.stats-history button', "Resume");
    await page.until(`document.querySelector('.stats-history[data-view="reading"]')`, "the reading again");
    assert.ok(names(await calls(page)).includes("statistics.resume"));
  }, options);
});

test("a tab that is hidden asks for nothing and one that is shown again catches up; a plugin that was reloaded makes the canvas listen again and ask again", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");

    await page.evaluate(`canvasFixture.setVisible(false); canvasFixture.clearCalls()`);
    await idle(200);
    await page.evaluate(`canvasFixture.control.emitData()`);
    await idle(900);
    const hidden = (await calls(page)).filter(call => call.name !== "statistics.status");
    assert.deepEqual(names(hidden), [], "no question while the tab is hidden");
    await page.evaluate(`canvasFixture.setVisible(true)`);
    await page.until(`canvasFixture.calls().some(call => call.name === 'statistics.summary')`, "the questions of a tab that is shown again");

    await page.evaluate(`canvasFixture.clearCalls()`);
    const before = await page.evaluate<{ open: number; closed: number }>(`({ ...canvasFixture.subscriptions })`);
    await page.evaluate(`canvasFixture.reconnect()`);
    await page.until(`canvasFixture.calls().some(call => call.name === 'statistics.status') && canvasFixture.calls().some(call => call.name === 'statistics.summary')`, "the status and the questions after a reconnection");
    const after = await page.evaluate<{ open: number; closed: number }>(`({ ...canvasFixture.subscriptions })`);
    assert.equal(after.open, before.open + 1, "it listens again");
    assert.equal(after.open - after.closed, 1, "and only once");
  }, options);
});

test("a session of the Sessions page is opened through the window, and a failure of the plugin is told in the block, not thrown", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    await page.clickText('[role="tab"]', "Sessions");
    await page.until(`document.querySelector('.stats-cell-link')`, "the sessions");
    await page.click('.stats-cell-link');
    await page.until(`canvasFixture.opened.length === 1`, "the session opened through the window");

    await page.evaluate(`canvasFixture.control.failNext("tools", "The plugin is busy.")`);
    await page.clickText('[role="tab"]', "Tools");
    await page.until(`document.querySelector('.stats-block[data-state="error"]')`, "the failure shown in its block");
  }, options);
});
