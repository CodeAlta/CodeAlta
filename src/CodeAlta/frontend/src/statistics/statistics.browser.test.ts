import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "./browserHarness";

// The canvas, mounted under React StrictMode over the fixture API in headless Edge, under the production content security policy.
// Every test ends by checking that the page raised no exception, no console error and no request outside itself.

type Call = { method: string; request: any; args: unknown[]; aborted: boolean };
const pageNames = ["overview", "activity", "models", "cost", "tools", "prompts", "agents", "code", "projects", "sessions", "health"];
const tabTitles = ["Overview", "Activity", "Models", "Cost", "Tools", "Prompts", "Agents", "Code", "Projects", "Sessions", "Health"];
const settled = `document.querySelector('.statistics-canvas') && !document.querySelector('.stats-block[data-state="loading"]') && !document.querySelector('.stats-tile-skeleton') && document.querySelectorAll('.stats-block').length > 0`;
const questions = (calls: Call[]) => calls.filter(call => !["status", "chooseHistory", "pause", "resume", "stopHere", "forgetDeleted", "resetStatistics"].includes(call.method));
const calls = (page: Page) => page.evaluate<Call[]>(`statsFixture.calls()`);
const idle = (milliseconds = 250) => new Promise(resolve => setTimeout(resolve, milliseconds));
const render = (page: Page, options: object = {}) => page.evaluate(`statsFixture.render(${JSON.stringify(options)})`);
const openPage = async (page: Page, title: string) => {
  await page.clickText('[role="tab"]', title);
  await page.until(`document.querySelector('[role="tab"][aria-selected="true"]')?.textContent.trim() === ${JSON.stringify(title)} && ${settled}`, `the ${title} page`);
};

test("every page draws without a console error, in both themes, narrow and wide, and at 125% zoom: the pictures are in tmp/statistics", { skip: !edge, timeout: 600_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    for (const [theme, dark] of [["dark", true], ["light", false]] as const) {
      await page.evaluate(`statsFixture.setTheme(${dark})`);
      for (const [width, label] of [[1280, "1280"], [640, "640"]] as const) {
        await page.resize(width, 800);
        for (const [index, title] of tabTitles.entries()) {
          await openPage(page, title);
          await idle(450);
          await page.shot(`${pageNames[index]}-${theme}-${label}`);
          const overflow = await page.evaluate<number>(`document.documentElement.scrollWidth - document.documentElement.clientWidth`);
          assert.ok(overflow <= 0, `${title} at ${width}px scrolls sideways by ${overflow}px`);
        }
      }
      // The whole page, for reading what is under the fold.
      await page.resize(1280, 2600);
      for (const [index, title] of tabTitles.entries()) {
        await openPage(page, title);
        await idle(450);
        await page.shot(`${pageNames[index]}-${theme}-tall`);
      }
    }
    await page.evaluate(`statsFixture.setTheme(true)`);
    await page.resize(1280, 800, 1.25);
    await openPage(page, "Overview");
    await idle(500);
    await page.shot("overview-dark-1280-zoom125");
    await page.resize(640, 800, 1.25);
    await idle(500);
    await page.shot("overview-dark-640-zoom125");
  });
});

test("every page loads its blocks, with a table behind each chart and a name on each", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    for (const title of tabTitles) {
      await openPage(page, title);
      const empties = await page.evaluate<string[]>(`[...document.querySelectorAll('.stats-block[data-state="empty"], .stats-block[data-state="error"]')].map(block => block.querySelector('h3').textContent)`);
      assert.deepEqual(empties, [], `${title} shows an empty or failed block over the fixture`);
      const unnamed = await page.evaluate<number>(`[...document.querySelectorAll('.stats-block')].filter(block => !block.getAttribute('aria-labelledby') || !document.getElementById(block.getAttribute('aria-labelledby'))?.textContent).length`);
      assert.equal(unnamed, 0, `${title}: every block has a title`);
      const charts = await page.evaluate<{ svg: number; toggles: number; named: number }>(`({ svg: document.querySelectorAll('.stat-chart .chart-surface svg').length, toggles: document.querySelectorAll('.stat-chart .chart-table-toggle').length, named: [...document.querySelectorAll('.stat-chart .chart-surface')].filter(e => e.getAttribute('aria-label')).length })`);
      assert.equal(charts.svg, charts.toggles, `${title}: a chart has its table`);
      assert.equal(charts.named, charts.toggles, `${title}: a chart has its accessible name`);
    }
    // The table of a chart: the same numbers, as a table with a caption.
    await openPage(page, "Overview");
    await page.clickText('.stat-chart .chart-table-toggle', "Show as table");
    await page.until(`document.querySelector('.stat-chart table.chart-table')`, "the table");
    assert.ok(await page.evaluate<number>(`document.querySelectorAll('.stat-chart table.chart-table tbody tr').length`) >= 30);
    assert.equal(await page.evaluate(`document.querySelector('.stat-chart table.chart-table thead th:first-child').textContent`), "");
  });
});

test("the frame sets what the page asks: period, frequency, comparison and filters, and a reload keeps them", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    const requests = async () => questions(await calls(page));
    // No question is asked twice, and none is canceled, while the page loads: the effects run twice under StrictMode and ask once.
    const first = await requests();
    assert.ok(first.length >= 7, `${first.length} questions at the start`);
    assert.equal(first.filter(call => call.aborted).length, 0);
    const keys = first.map(call => JSON.stringify([call.method, call.request, call.args]));
    assert.equal(new Set(keys).size, keys.length, "no question twice");
    assert.ok(first.filter(call => call.request?.period !== "365d").every(call => call.request.period === "30d" && call.request.frequency === "auto"));

    await page.evaluate(`statsFixture.clearCalls()`);
    await page.click('button[aria-label^="Period:"]');
    await page.clickText('.bp6-menu-item', "Last 7 days");
    await page.until(`${settled} && statsFixture.calls().some(call => call.request?.period === '7d')`, "the 7-day questions");
    await page.until(`document.querySelector('button[aria-label^="Period:"]').getAttribute('aria-label') === 'Period: Last 7 days'`, "the period shown");
    let now = (await requests()).filter(call => call.request?.period !== "365d");
    assert.ok(now.length > 0 && now.every(call => call.request.period === "7d"), "every question follows the period");
    assert.equal(await page.evaluate(`document.querySelector('.stat-chart .chart-surface')?.getAttribute('aria-label')`) !== null, true);

    await page.evaluate(`statsFixture.clearCalls()`);
    await page.click('button[aria-label^="Frequency:"]');
    await page.clickText('.bp6-menu-item', "Hour");
    await page.until(`${settled} && statsFixture.calls().some(call => call.request?.frequency === 'hour')`, "the hourly questions");
    assert.equal(await page.evaluate(`document.querySelector('button[aria-label^="Frequency:"]').getAttribute('aria-label')`), "Frequency: Hour");
    // A frequency that no longer fits the period is offered no more: a year has no hours.
    await page.click('button[aria-label^="Period:"]');
    await page.clickText('.bp6-menu-item', "This year");
    await page.until(`document.querySelector('button[aria-label^="Frequency:"]').getAttribute('aria-label').startsWith('Frequency: Auto')`, "the frequency back to auto");

    await page.click('button[aria-label^="Period:"]');
    await page.clickText('.bp6-menu-item', "Last 90 days");
    await page.until(`document.querySelector('button[aria-label^="Period:"]').getAttribute('aria-label') === 'Period: Last 90 days' && ${settled}`, "90 days");
    await page.evaluate(`statsFixture.clearCalls()`);
    await page.click('button[aria-label^="Compare:"]');
    await page.clickText('.bp6-menu-item', "Previous period");
    await page.until(`${settled} && statsFixture.calls().some(call => call.request?.comparison === 'previousPeriod')`, "the comparison");
    await page.until(`document.querySelector('.stat-tile-change[data-direction]')`, "the change on a tile");
    assert.match(await page.evaluate<string>(`document.querySelector('.stat-tile-change').textContent`), /[▲▼▬]/, "the direction is an arrow, not only a color");

    await page.evaluate(`statsFixture.clearCalls()`);
    await page.click('.stats-add-filter');
    await page.clickText('.bp6-menu-item', "Project");
    await page.until(`[...document.querySelectorAll('.bp6-menu-item')].some(item => item.textContent.trim() === 'CodeAlta')`, "the projects");
    await page.clickText('.bp6-menu-item', "CodeAlta");
    await page.until(`${settled} && statsFixture.calls().some(call => call.request?.filter?.project === 'proj-codealta')`, "the filtered questions");
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-text').textContent`), "Project: CodeAlta");
    now = (await requests()).filter(call => call.request?.period !== "365d" && call.request?.limit === undefined);
    assert.ok(now.every(call => call.request.filter?.project === "proj-codealta"), "every question carries the filter");
    // A reload: the same canvas instance opens on the same frame.
    const saved = await page.evaluate<string>(`[...statsFixture.storage.values()][0]`);
    assert.match(saved, /page=overview/);
    assert.match(saved, /period=90d/);
    assert.match(saved, /cmp=previousPeriod/);
    assert.match(saved, /project=proj-codealta/);
    await page.evaluate(`statsFixture.clearCalls(); statsFixture.remount()`);
    await page.until(`${settled}`, "the canvas again");
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-text').textContent`), "Project: CodeAlta");
    assert.equal(await page.evaluate(`document.querySelector('button[aria-label^="Period:"]').getAttribute('aria-label')`), "Period: Last 90 days");
    assert.ok((await requests()).some(call => call.request?.period === "90d" && call.request?.filter?.project === "proj-codealta"));
    // Reset puts the canvas back as it opened; a chip is removed by its button.
    await page.clickText('button', "Reset");
    await page.until(`!document.querySelector('.stats-chip') && document.querySelector('button[aria-label^="Period:"]').getAttribute('aria-label') === 'Period: Last 30 days'`, "the frame as it opened");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Reset')`), false);
    // A custom range.
    await page.click('button[aria-label^="Period:"]');
    await page.until(`document.querySelectorAll('.stats-range input').length === 2`, "the range form");
    await page.evaluate(`(() => { const set = (input, value) => { const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set; setter.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })); }; const [from, to] = document.querySelectorAll('.stats-range input'); set(from, '2026-08-01'); set(to, '2026-08-31'); })()`);
    await page.clickText('.stats-range button', "Apply");
    await page.until(`statsFixture.calls().some(call => call.request?.period === '2026-08-01..2026-08-31')`, "the custom period");
    assert.equal(await page.evaluate(`document.querySelector('button[aria-label^="Period:"]').getAttribute('aria-label')`), "Period: Custom range");
  });
});

test("a click on a bar, a row, a day or a session drills down", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    await page.until(`document.querySelector('.stats-ranked-row[aria-label^="Filter on"]')`, "the ranked rows");
    // The first project of the ranking becomes a filter.
    const name = await page.evaluate<string>(`document.querySelector('.stats-ranked-row[aria-label^="Filter on"] .stats-ranked-name span').textContent`);
    await page.click('.stats-ranked-row[aria-label^="Filter on"]');
    await page.until(`document.querySelector('.stats-chip-text')?.textContent === ${JSON.stringify(`Project: ${name}`)}`, "the project chip");
    await page.click('.stats-chip-remove');
    await page.until(`!document.querySelector('.stats-chip')`, "the chip removed");
    // A day of the calendar becomes the period.
    await page.evaluate(`statsFixture.clearCalls()`);
    await page.click('.heat-calendar .heat-cell[data-level="3"]');
    await page.until(`statsFixture.calls().some(call => /^\\d{4}-\\d\\d-\\d\\d\\.\\.\\d{4}-\\d\\d-\\d\\d$/.test(call.request?.period ?? '') && call.request.period.slice(0, 10) === call.request.period.slice(12))`, "the one-day period");
    assert.equal(await page.evaluate(`document.querySelector('button[aria-label^="Period:"]').getAttribute('aria-label')`), "Period: Custom range");
    await page.until(`statsFixture.calls().some(call => call.request?.frequency === 'auto' && call.method === 'series')`, "the day's series");
    // A session of the table opens in the window.
    await openPage(page, "Sessions");
    await page.click('.stats-table .stats-cell-link');
    await page.until(`statsFixture.opened.length === 1`, "the session opened");
    assert.match(await page.evaluate<string>(`statsFixture.opened[0]`), /^[0-9a-f]{8}-c20c-/);
    // The records open their session too.
    await page.clickText('button', "Reset");
    await openPage(page, "Overview");
    await page.click('.stats-record');
    await page.until(`statsFixture.opened.length === 2`, "the session of a record opened");
  });
});

test("the first time asks how much history to read, then shows the progress, the pause and the end", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "first-time" });
    await page.until(`document.querySelector('.stats-first')`, "the choice");
    assert.equal(await page.evaluate(`document.querySelector('.stats-first h2').textContent`), "Statistics of your sessions");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-first p').textContent`), /^906 sessions since Apr 20, 2026 can be read to build your statistics\. It takes less than a minute and runs in the background\.$/);
    assert.equal(await page.evaluate(`document.querySelector('[role="tablist"]')`), null, "no empty charts");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.stats-first button')].map(button => button.textContent.trim())`), ["Read all the history", "Last 90 days", "Start from today"]);
    await page.shot("first-time-dark");
    assert.deepEqual(questions(await calls(page)), [], "nothing is asked before the choice");

    await page.clickText('.stats-first button', "Read all the history");
    await page.until(`document.querySelector('.stats-history[data-view="reading"]') && document.querySelector('[role="tablist"]')`, "the bar and the pages");
    assert.deepEqual(await page.evaluate(`statsFixture.calls().filter(call => call.method === 'chooseHistory').map(call => call.args)`), [[{ kind: "all" }]]);
    const text = await page.evaluate<string>(`document.querySelector('.stats-history .stats-history-line').textContent`);
    assert.match(text, /Reading the history: 0 of 906 sessions, back to Oct 9\./);
    assert.match(text, /left\./);
    await page.until(settled, "the first numbers");
    // While it reads, the canvas is in use, and what is not read is hatched.
    await page.evaluate(`statsFixture.control.advance(300)`);
    await page.until(`document.querySelector('.stats-history .stats-history-line').textContent.includes('300 of 906 sessions')`, "the progress");
    assert.equal(await page.evaluate(`document.querySelector('.stats-progress').getAttribute('aria-valuenow')`), "33");
    await page.until(`document.querySelector('.stat-hatch')`, "the hatch over what is not read");
    assert.equal(await page.evaluate(`document.querySelector('.stat-hatch').textContent`), "Not read yet");
    await page.shot("reading-dark");
    // Pause, then resume, then stop here.
    await page.clickText('.stats-history button', "Pause");
    await page.until(`document.querySelector('.stats-history[data-view="paused"]')`, "the pause");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-history-line').textContent`), /^History paused at .*\. 606 sessions left\.ResumeStop here$/);
    await page.shot("paused-dark");
    await page.clickText('.stats-history button', "Resume");
    await page.until(`document.querySelector('.stats-history[data-view="reading"]')`, "reading again");
    await page.clickText('.stats-history button', "Pause");
    await page.until(`document.querySelector('.stats-history[data-view="paused"]')`, "paused again");
    await page.clickText('.stats-history button', "Stop here");
    await page.until(`document.querySelector('.stats-history[data-view="stopped"]')`, "stopped");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-history-line').textContent`), /^The charts start on [A-Z][a-z]{2} \d+, 2026\.$/);
    assert.deepEqual(await page.evaluate(`['pause', 'resume', 'pause', 'stopHere'].every((name, index) => statsFixture.calls().filter(call => ['pause', 'resume', 'stopHere'].includes(call.method))[index].method === name)`), true);
    // Reading more history from the menu of the canvas, then the end: the bar goes, a notification stays a while.
    await page.click('button[aria-label="Statistics menu"]');
    await page.clickText('.bp6-menu-item', "All the history");
    await page.until(`document.querySelector('.stats-history[data-view="reading"]')`, "reading more");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-history-line').textContent`), /^Reading more history/);
    await page.evaluate(`statsFixture.control.advance(10000)`);
    await page.until(`!document.querySelector('.stats-history') && !document.querySelector('.stat-hatch')`, "the end");
    await page.until(`document.querySelector('.bp6-toast')?.textContent.includes('Your statistics are ready: 906 sessions since Apr 20, 2026.')`, "the notification");
    assert.equal(await page.evaluate(`document.querySelectorAll('.bp6-toast').length`), 1, "once");
  });
});

test("the states of the history: the sessions that could not be read, a reading that failed, and a choice to start from today", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "skipped" });
    await page.until(`document.querySelector('.stats-history[data-view="skipped"]')`, "the skipped sessions");
    assert.equal(await page.evaluate(`document.querySelector('.stats-history button').textContent.trim()`), "3 sessions could not be read");
    await page.click('.stats-history button');
    await page.until(`document.querySelector('.stats-skipped li')`, "the list");
    assert.equal(await page.evaluate(`document.querySelectorAll('.stats-skipped li').length`), 3);
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-skipped li').textContent`), /damaged/);
    await page.shot("skipped-dark");
    await render(page, { scenario: "failed" });
    await page.until(`document.querySelector('.stats-failed')`, "the failure");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-failed').textContent`), /The statistics could not start.*The statistics database could not be opened\./);
    await render(page, { scenario: "first-time" });
    await page.until(`document.querySelector('.stats-first')`, "the choice");
    await page.clickText('.stats-first button', "Start from today");
    await page.until(`document.querySelector('[role="tablist"]') && ${settled}`, "the pages");
    assert.equal(await page.evaluate(`document.querySelector('.stats-history')`), null, "nothing is read, so no bar");
    assert.deepEqual(await page.evaluate(`statsFixture.calls().filter(call => call.method === 'chooseHistory').map(call => call.args)`), [[{ kind: "fromToday" }]]);
  });
});

test("a hidden canvas asks nothing, and shows what it missed when it is shown again", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready", visible: false });
    await idle(600);
    assert.deepEqual(questions(await calls(page)), [], "a hidden canvas asks nothing");
    assert.equal(await page.evaluate(`document.querySelector('.stat-chart .chart-surface svg')`), null, "and draws nothing");
    await page.evaluate(`statsFixture.update({ visible: true })`);
    await page.until(settled, "the canvas shown");
    const shown = questions(await calls(page)).length;
    assert.ok(shown >= 7);
    // Hidden again: a change of the numbers is kept, not read.
    await page.evaluate(`statsFixture.update({ visible: false })`);
    await page.evaluate(`statsFixture.control.emitData(20261009, 20261009)`);
    await idle(1500);
    assert.equal(questions(await calls(page)).length, shown, "nothing is read while it is hidden");
    const before = (await calls(page)).length;
    await page.evaluate(`statsFixture.update({ visible: true })`);
    await page.until(`statsFixture.calls().length > ${before}`, "the stale numbers read again");
    await page.until(settled, "settled");
    assert.ok(questions(await calls(page)).length > shown);
    assert.equal(await page.evaluate(`statsFixture.control.listenerCount()`), 1, "one listener, however many times the effects ran");
  });
});

test("a change announced by the plugin makes the page ask again for those days only, once for a burst", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    await page.evaluate(`statsFixture.clearCalls()`);
    // A change of a day six months back touches no period shown: the calendar of a year does not reach it either.
    await page.evaluate(`statsFixture.control.emitData(20250101, 20250102)`);
    await idle(1500);
    assert.deepEqual(questions(await calls(page)), [], "no period shown holds those days");
    // The year of the calendar does hold a day of April.
    await page.evaluate(`statsFixture.control.emitData(20260420, 20260421)`);
    await page.until(`statsFixture.calls().some(call => call.method === 'calendar')`, "the calendar asked again");
    await idle(300);
    assert.deepEqual(questions(await calls(page)).map(call => call.method), ["calendar"], "only what holds those days");
    await page.evaluate(`statsFixture.clearCalls()`);
    // A burst of changes to today: one round of questions, after a second.
    await page.evaluate(`for (let index = 0; index < 6; index++) statsFixture.control.emitData(20261009, 20261009)`);
    await idle(400);
    assert.deepEqual(questions(await calls(page)), [], "the burst is gathered for a second");
    await page.until(`statsFixture.calls().length > 0`, "the questions again");
    await page.until(settled, "settled");
    const again = questions(await calls(page));
    const keys = again.map(call => JSON.stringify([call.method, call.request, call.args]));
    assert.equal(new Set(keys).size, keys.length, "each question once");
    assert.ok(again.some(call => call.method === "summary") && again.some(call => call.method === "series"));
  });
});

test("a question that fails is told with a way to try again, and an empty period says so", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    // The first summary fails.
    await page.evaluate(`window.__failFirst = true`);
    await render(page, { scenario: "ready" });
    await page.evaluate(`statsFixture.control.failNext('summary', 'The plugin is not running.')`);
    await page.evaluate(`statsFixture.clearCalls(); statsFixture.remount()`);
    await page.until(`document.querySelector('.stats-tiles .stats-block[data-state="error"]')`, "the error");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-tiles .stats-block').textContent`), /This could not be read.*The plugin is not running\./);
    await page.shot("error-dark");
    await page.clickText('.stats-tiles button', "Try again");
    await page.until(`document.querySelector('.stats-tiles .stat-tile') && !document.querySelector('.stats-tiles .stats-block')`, "the tiles after the retry");
    // A period before any data.
    await page.click('button[aria-label^="Period:"]');
    await page.until(`document.querySelectorAll('.stats-range input').length === 2`, "the range form");
    await page.evaluate(`(() => { const set = (input, value) => { const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set; setter.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })); }; const [from, to] = document.querySelectorAll('.stats-range input'); set(from, '2026-01-01'); set(to, '2026-01-05'); })()`);
    await page.clickText('.stats-range button', "Apply");
    await page.until(`document.querySelectorAll('.stats-block[data-state="empty"]').length >= 3`, "the empty blocks");
    assert.equal(await page.evaluate(`document.querySelector('.stats-block[data-state="empty"] .stats-empty').textContent`), "Nothing in this period.");
    await page.shot("empty-dark");
  });
});

test("switching pages a few hundred times neither asks again nor grows the page", { skip: !edge, timeout: 600_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    for (const title of tabTitles) await openPage(page, title);
    await openPage(page, "Overview");
    const before = await page.evaluate<{ nodes: number; calls: number; listeners: number; heap: number }>(`({ nodes: document.querySelectorAll('*').length, calls: statsFixture.calls().length, listeners: statsFixture.control.listenerCount(), heap: (gc(), gc(), performance.memory ? performance.memory.usedJSHeapSize : 0) })`);
    const titles = JSON.stringify(tabTitles);
    // 220 switches inside the page, as fast as it follows.
    await page.evaluate(`(async () => { const titles = ${titles}; for (let index = 0; index < 220; index++) { const title = titles[(index * 7) % titles.length]; [...document.querySelectorAll('[role="tab"]')].find(tab => tab.textContent.trim() === title).click(); await new Promise(resolve => setTimeout(resolve, 12)); } })()`);
    await openPage(page, "Overview");
    await idle(600);
    const after = await page.evaluate<{ nodes: number; calls: number; listeners: number; heap: number }>(`({ nodes: document.querySelectorAll('*').length, calls: statsFixture.calls().length, listeners: statsFixture.control.listenerCount(), heap: (gc(), gc(), performance.memory ? performance.memory.usedJSHeapSize : 0) })`);
    assert.equal(after.calls, before.calls, "the results were kept: coming back to a page asks nothing");
    assert.equal(after.listeners, 1);
    assert.ok(Math.abs(after.nodes - before.nodes) <= before.nodes * 0.05, `the page keeps its size: ${before.nodes} -> ${after.nodes} nodes`);
    if (before.heap > 0) assert.ok(after.heap < before.heap * 1.3 + 4_000_000, `the heap does not grow with the switches: ${before.heap} -> ${after.heap}`);
    assert.equal(await page.evaluate(`document.querySelectorAll('.statistics-canvas').length`), 1);
    // Only the page that is shown has charts.
    assert.equal(await page.evaluate(`document.querySelectorAll('[role="tabpanel"]').length`), 1);
  }, { exposeGc: true });
});

test("the canvas is used with the keyboard: tabs, menus, the table of a chart, the legend", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    // The pages are a tab list: the arrow keys move between them.
    await page.evaluate(`document.querySelector('[role="tab"][aria-selected="true"]').focus()`);
    await page.key("ArrowRight", 39);
    await page.until(`document.activeElement.textContent.trim() === 'Activity'`, "the focus on the next page");
    await page.key("Enter", 13, "\r");
    await page.until(`document.querySelector('[role="tab"][aria-selected="true"]')?.textContent.trim() === 'Activity' && ${settled}`, "the next page");
    await page.key("ArrowLeft", 37);
    await page.key("Enter", 13, "\r");
    await page.until(`document.querySelector('[role="tab"][aria-selected="true"]')?.textContent.trim() === 'Overview' && ${settled}`, "the previous page");
    assert.equal(await page.evaluate(`document.querySelector('[role="tablist"]').getAttribute('role')`), "tablist");
    // A menu opens with Enter, moves with the arrows, and closes with Escape.
    await page.evaluate(`document.querySelector('button[aria-label^="Compare:"]').focus()`);
    await page.key("Enter", 13, "\r");
    await page.until(`document.querySelector('.bp6-menu')`, "the menu");
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.bp6-menu')`, "the menu closed");
    assert.equal(await page.evaluate(`document.activeElement.getAttribute('aria-label')?.startsWith('Compare:')`), true, "the focus comes back to the button");
    // The legend is a row of buttons with a pressed state, and the year is one tab stop.
    assert.ok(await page.evaluate<number>(`document.querySelectorAll('.chart-legend-item[aria-pressed]').length`) >= 2);
    assert.equal(await page.evaluate(`document.querySelectorAll('.heat-calendar .heat-cell[tabindex="0"]').length`), 1);
    // Every control has a name.
    const unnamed = await page.evaluate<string[]>(`[...document.querySelectorAll('.statistics-canvas button, .statistics-canvas [role="tab"], .statistics-canvas input')].filter(e => !(e.getAttribute('aria-label') || e.textContent.trim() || e.title)).map(e => e.outerHTML.slice(0, 80))`);
    assert.deepEqual(unnamed, []);
    // Sort a table by its header, by keyboard.
    await openPage(page, "Models");
    await page.evaluate(`document.querySelector('.stats-table th[aria-sort="none"] button').focus()`);
    await page.key("Enter", 13, "\r");
    await page.until(`document.querySelector('.stats-table th[aria-sort="descending"], .stats-table th[aria-sort="ascending"]')`, "the sorted column");
  });
});

test("the canvas speaks the language of the window and writes numbers in it", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    for (const [locale, overview, health] of [["fr", "Vue d’ensemble", "Santé"], ["de", "Übersicht", "Zustand"], ["ja", "概要", "状態"], ["es", "Resumen", "Salud"], ["zh-CN", "概览", "健康"]] as const) {
      await render(page, { scenario: "ready", locale });
      await page.until(settled, `the ${locale} overview`);
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('[role="tab"]')].map(tab => tab.textContent.trim()).filter((text, index) => index === 0 || index === 10)`), [overview, health]);
      assert.ok(!(await page.evaluate<string>(`document.querySelector('.statistics-canvas').innerText`)).includes("Activity over time"), `${locale} has no English left in the blocks`);
      await page.shot(`overview-${locale}`);
    }
    await render(page, { scenario: "first-time", locale: "fr" });
    await page.until(`document.querySelector('.stats-first')`, "the choice in French");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-first p').textContent`), /^906 sessions depuis le 20 avr\. 2026 peuvent être lues pour construire vos statistiques\. Cela prend moins d’une minute et se fait en arrière-plan\.$/);
  });
});

test("the canvas is calm for people who ask for less motion, and keeps working at 200% zoom and in a narrow tab", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    await page.until(`document.querySelector('.stat-chart .chart-surface svg')`, "a chart");
    assert.equal(await page.evaluate(`(() => { const e = document.querySelector('.stats-block-body'); return getComputedStyle(e).transitionDuration; })()`), "0s", "no transition");
    await page.resize(1280, 800, 2);
    await idle(400);
    assert.ok(await page.evaluate<number>(`document.documentElement.scrollWidth - document.documentElement.clientWidth`) <= 0);
    await page.shot("overview-dark-zoom200");
    await page.resize(1280, 800, 1);
    await page.evaluate(`statsFixture.update({ width: 420 })`);
    await idle(400);
    assert.ok(await page.evaluate<number>(`document.querySelector('.statistics-canvas').scrollWidth - document.querySelector('.statistics-canvas').clientWidth`) <= 0);
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.stats-block')).getPropertyValue('--span').trim()`), "12", "a narrow tab stacks its blocks");
    await page.shot("overview-dark-420");
  }, { reducedMotion: true });
});

test("the spaces and the project of a canvas open filtered, and a color scheme restyles its charts", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready", spaceId: "space-work", instanceId: "canvas-space" });
    await page.until(settled, "the space");
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-text').textContent`), "Space: Work");
    assert.ok(questions(await calls(page)).every(call => call.request?.filter?.space === "space-work"));
    await page.click('.stats-chip-remove');
    await page.until(`!document.querySelector('.stats-chip') && ${settled}`, "all spaces");
    await render(page, { scenario: "ready", projectId: "proj-tomlyn", instanceId: "canvas-project" });
    await page.until(settled, "the project");
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-text').textContent`), "Project: proj-tomlyn");
    // A color scheme that redefines the palette changes the colors of the series.
    await page.evaluate(`document.documentElement.style.setProperty('--chart-1', '#ff0000')`);
    await page.until(`[...document.querySelectorAll('.stat-chart .chart-surface svg path')].some(path => (path.getAttribute('fill') || '').toLowerCase() === '#ff0000')`, "the series redrawn in the new color");
  });
});
