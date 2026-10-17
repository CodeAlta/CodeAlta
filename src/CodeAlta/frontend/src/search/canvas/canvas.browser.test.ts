import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../../statistics/browserHarness";

// The Search canvas in headless Edge, over a fake `localSearch` (canvas.mount.tsx): what the overview draws, what Explore asks and opens,
// the live updates and the quiet of a hidden tab, in both themes and at the narrow size of the window.

const options = { entry: "../search/canvas/canvas.mount.tsx", fixture: "searchFixture", styles: ["../search/canvas/canvas.css"], width: 1600, height: 1000 } as const;
const fixture = "window.searchFixture";

test("the overview counts what the space sees, draws the history, the kinds and the projects, and says where the indexer is", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.search-canvas .stat-tile').length === 4 && document.querySelector('.stat-tile-value')?.textContent === '20,455'`, "the tiles");
    const tiles = await page.evaluate<string[]>(`[...document.querySelectorAll('.stat-tile')].map(e => e.innerText.replace(/\\n+/g, ' | '))`);
    assert.deepEqual(tiles, ["Items | 20,455 | +312 this week", "Sessions | 915 | 1 project kept out", "Searchable | By words only | 52,000 passages", "Size on disk | 52 MB | The whole index"]);
    // The history: a square per day, the days with turns darker.
    assert.ok(await page.evaluate<number>(`document.querySelectorAll('.search-canvas .heat-cell[data-level]:not([data-level="0"])').length`) >= 3);
    // The kinds, the most first; the treemap, with the project kept out of search hatched and named.
    assert.deepEqual(await page.evaluate<string[]>(`[...document.querySelectorAll('.stats-ranked-name > span')].map(e => e.textContent)`),
      ["Messages", "Plans", "User guide", "Tools", "Skills", "MCP servers"]);
    await page.until(`document.querySelector('.search-canvas .chart-surface svg path, .search-canvas .chart-surface svg rect')`, "the treemap");
    await page.clickText(".chart-table-toggle", "Show as table");
    assert.deepEqual(await page.evaluate<string[]>(`[...document.querySelectorAll('.chart-table tbody tr')].map(e => e.innerText.replace(/\\s+/g, ' ').trim())`),
      ["CodeAlta 15,000", "NeoAstra 4,000", "Tomlyn kept out"]);
    // The indexer builds the index for the first time, and failed once.
    assert.equal(await page.evaluate<string>(`document.querySelector('.search-index-badge').textContent`), "Building the index");
    assert.match(await page.evaluate<string>(`document.querySelector('.search-index-line').innerText`), /12 of 40 sessions read/u);
    assert.ok(await page.evaluate<boolean>(`!!document.querySelector('.search-index-progress')`));
    assert.match(await page.evaluate<string>(`document.querySelector('.search-index-error').textContent`), /unreadable line in journal s9/u);
    assert.deepEqual(await page.evaluate<string[]>(`[...document.querySelectorAll('.search-canvas .stats-table tbody tr td:first-child')].map(e => e.textContent)`), ["Session journals", "User guide"]);
    await page.shot("search-canvas-overview-dark");

    // A change of the index is read again; a hidden tab reads nothing, and reads once it is shown again.
    const reads = await page.evaluate<number>(`${fixture}.state.reads`);
    await page.evaluate(`${fixture}.change()`);
    await page.until(`${fixture}.state.reads > ${reads}`, "a read after the change");
    await page.evaluate(`${fixture}.setVisible(false)`);
    await page.until(`document.querySelector('.search-canvas').dataset.visible === 'false'`, "the hidden tab");
    const hidden = await page.evaluate<number>(`${fixture}.state.reads`);
    await page.evaluate(`${fixture}.change()`);
    await new Promise(resolve => setTimeout(resolve, 400));
    assert.equal(await page.evaluate<number>(`${fixture}.state.reads`), hidden, "nothing is read while the tab is hidden");
    await page.evaluate(`${fixture}.setVisible(true)`);
    await page.until(`${fixture}.state.reads > ${hidden}`, "a read once shown");
  }, options);
});

test("Explore lists a kind, searches as words are typed, explains the ranks and opens a hit as the search window would", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.stats-ranked-row').length > 0`, "the kinds");
    // A kind of the overview lists what is indexed of it, in Explore.
    await page.click(`.stats-ranked-row`, 1);
    await page.until(`document.querySelector('.search-hit-title')?.textContent === 'Pelican migration'`, "the plans");
    assert.deepEqual(await page.evaluate(`${fixture}.state.browses`), [{ kind: "plan", projectId: null }]);
    assert.equal(await page.evaluate<string>(`document.querySelector('.search-kind-chip[aria-pressed=true]').textContent`), "Plans");
    await page.click(`.search-hit`);
    await page.until(`${fixture}.state.actions.includes('file:/code/codealta/.alta/plans/pelican.md')`, "the plan opened in the editor");

    // A query waits for the last key, and asks with the word being typed.
    await page.click(`.search-kind-chip`, 0);
    await page.click(`.search-explore-input input`);
    await page.type("retry");
    await page.until(`document.querySelectorAll('.search-hit').length === 3`, "the hits");
    const queries = await page.evaluate<{ query: string; typing: boolean; kind: string | null; mode: string }[]>(`${fixture}.state.queries`);
    assert.deepEqual(queries.map(query => [query.query, query.typing, query.kind, query.mode]), [["retry", true, null, "words"]], "one query for the five keys, by words while meaning is off");
    assert.equal(await page.evaluate<number>(`document.querySelectorAll('.search-explore .stats-choice').length`), 0, "no switch of the mode while meaning is off");
    assert.deepEqual(await page.evaluate<string[]>(`[...document.querySelectorAll('.search-hit-rank')].map(e => e.innerText.replace(/\\n+/g, ' '))`), ["#1 words #3 meaning #2", "#2 words #1", "#3 ≈ meaning #1"]);
    assert.deepEqual(await page.evaluate<string[]>(`[...document.querySelectorAll('.search-hit-snippet mark')].map(e => e.textContent)`), ["retry"]);
    assert.match(await page.evaluate<string>(`document.querySelectorAll('.search-hit-meta')[0].textContent`), /Messages · Answer · CodeAlta · /u);
    assert.equal(await page.evaluate<string>(`document.querySelectorAll('.search-hit-meta')[1].textContent`), "User guide", "what is installed shows no date");
    assert.equal(await page.evaluate<boolean>(`document.querySelectorAll('.search-hit')[2].disabled`), true, "a tool leads nowhere");
    await page.shot("search-canvas-explore-dark");

    // Enter opens the hit the keyboard is on: a message at its place in its session.
    await page.key("ArrowDown", 40);
    await page.key("Enter", 13);
    await page.until(`${fixture}.state.actions.includes('message:s1@4242')`, "the message opened at its place");
    assert.deepEqual(await page.evaluate(`${fixture}.state.located`), ["msg:s1/c1"]);
    // A page of the guide opens at its heading.
    await page.click(`.search-hit`, 1);
    await page.until(`${fixture}.state.actions.includes('doc:worktrees.md#cleanup')`, "the guide at its heading");
  }, options);
});

test("the canvas reads in the light theme and in the narrow tab of a 900×650 window without overflowing", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`${fixture}.setTheme(false)`);
    await page.until(`document.querySelectorAll('.search-canvas .stat-tile').length === 4 && document.querySelector('.search-canvas .chart-surface svg')`, "the overview");
    await page.shot("search-canvas-overview-light");
    // In a window of 900×650 the tab beside the sidebar is about 552 pixels wide (measured in the application): the page is the tab here.
    await page.resize(560, 650);
    await page.until(`getComputedStyle(document.querySelector('.search-canvas .stats-tiles')).gridTemplateColumns.split(' ').length === 2`, "two tiles a row");
    // The chart follows its box a moment later.
    await page.until(`(() => { const surface = document.querySelector('.search-canvas .chart-surface'); return surface.firstElementChild.getBoundingClientRect().width <= surface.clientWidth + 1; })()`, "the treemap at the new width");
    const overflow = `[...document.querySelectorAll('.search-canvas *')].filter(e => e.scrollWidth > e.clientWidth + 1 && !['visible', 'clip'].includes(getComputedStyle(e).overflowX)
      && !e.closest('.stats-calendar, .bp6-tab-list, .stats-table-wrap, .chart-title-row')).map(e => e.className)`;
    assert.deepEqual(await page.evaluate<string[]>(overflow), []);
    assert.ok(await page.evaluate<boolean>(`document.querySelector('.search-canvas').scrollWidth <= document.querySelector('.search-canvas').clientWidth`));
    await page.shot("search-canvas-overview-narrow-light");
    await page.clickText(`.search-canvas [role=tab]`, "Explore");
    await page.click(`.search-explore-input input`);
    await page.type("retry");
    await page.until(`document.querySelectorAll('.search-hit').length === 3`, "the hits");
    assert.deepEqual(await page.evaluate<string[]>(overflow), []);
    const hitBox = await page.evaluate<{ right: number; canvas: number }>(`(() => ({ right: document.querySelector('.search-hit').getBoundingClientRect().right, canvas: document.querySelector('.search-canvas').getBoundingClientRect().right }))()`);
    assert.ok(hitBox.right <= hitBox.canvas + 0.5, "a hit fits in the canvas");
    await page.shot("search-canvas-explore-narrow-light");
  }, { ...options, colorScheme: "light" });
});

test("with search by meaning on, the canvas says how much is searchable by meaning, hatches the days by words only, and Explore searches by meaning", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.search-canvas .stat-tile').length === 4`, "the tiles");
    // Off, the canvas offers it: the button runs the command of the plugin that opens the dialog in the window.
    await page.evaluate(`${fixture}.meaningOffered()`);
    await page.clickText(`.search-meaning-offer button`, "Search by meaning…");
    await page.until(`${fixture}.state.actions.includes('command:search_meaning')`, "the command of the dialog");
    await page.evaluate(`${fixture}.meaningOn()`);
    await page.until(`document.querySelectorAll('.stat-tile')[2]?.innerText.startsWith('By meaning')`, "the tile by meaning");
    assert.equal(await page.evaluate<string>(`document.querySelectorAll('.stat-tile')[2].innerText.replace(/\\n+/g, ' | ')`), "By meaning | 25% | about 10 min left");
    // Yesterday is searchable by words only: it is hatched, and says so.
    const hatched = await page.evaluate<string[]>(`[...document.querySelectorAll('.search-canvas .heat-hatch')].map(e => e.dataset.key)`);
    assert.equal(hatched.length, 1);
    assert.match(await page.evaluate<string>(`document.querySelector('.heat-cell[data-key="${hatched[0]}"] title').textContent`), /by words only/u);
    // The indexing by meaning, with its speed, beside the sources.
    assert.match(await page.evaluate<string>(`document.querySelector('.search-meaning-state').innerText`), /Indexing — 13,000 of 52,000[\s\S]*41\.2 passages a second/u);
    await page.shot("search-canvas-meaning-dark");

    // Explore: both by default, then by meaning only.
    await page.clickText(`.search-canvas [role=tab]`, "Explore");
    await page.click(`.search-explore-input input`);
    await page.type("retry");
    await page.until(`document.querySelectorAll('.search-hit').length === 3`, "the hits");
    assert.equal(await page.evaluate<string>(`document.querySelector('.search-results-mode').textContent`), "By words and meaning");
    await page.clickText(`.search-explore .stats-choice button`, "Meaning");
    await page.until(`${fixture}.state.queries.at(-1)?.mode === 'meaning'`, "a query by meaning");
    assert.deepEqual(await page.evaluate<string[]>(`${fixture}.state.queries.map(query => query.mode)`), ["auto", "meaning"]);
    await page.until(`document.querySelector('.search-results-mode').textContent === 'By meaning'`, "the mode shown");
    assert.equal(await page.evaluate<string>(`document.querySelector('.search-hit-part[data-approximate]').textContent`), "≈ meaning #1");
    await page.shot("search-canvas-meaning-explore-dark");
  }, options);
});

test("the Activity page counts the searches of 90 days by who asked, the share that found nothing and the median times", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.search-canvas .stat-tile').length === 4`, "the overview");
    assert.equal(await page.evaluate<number>(`${fixture}.state.activityReads`), 0, "the activity is read when its page is shown");
    await page.clickText(`.search-canvas [role=tab]`, "Activity");
    await page.until(`document.querySelectorAll('[data-page="activity"] .stat-tile').length === 5`, "the tiles of the activity");
    assert.deepEqual(await page.evaluate<string[]>(`[...document.querySelectorAll('[data-page="activity"] .stat-tile')].map(e => e.innerText.replace(/\\n+/g, ' | '))`), [
      "Searches | 30 | 19 today, 90 days in all", "Found nothing | 10% | 3 searches", "Median time by words | 4 ms", "Median time by meaning | 38 ms | Search by meaning is off",
      "Kept out of prompts | 182K tokens | By tool search, estimated"]);
    await page.until(`document.querySelector('[data-page="activity"] .chart-surface svg')`, "the chart");
    await page.clickText(`[data-page="activity"] .chart-table-toggle`, "Show as table");
    assert.deepEqual(await page.evaluate<string[]>(`[...document.querySelectorAll('[data-page="activity"] .chart-table thead th')].map(e => e.textContent)`), ["", "Window", "Agents", "Tool search", "Other"]);
    assert.equal(await page.evaluate<number>(`document.querySelectorAll('[data-page="activity"] .chart-table tbody tr').length`), 3);
    await page.clickText(`[data-page="activity"] .chart-table-toggle`, "Show as chart");
    await page.shot("search-canvas-activity-dark");
  }, options);
});

