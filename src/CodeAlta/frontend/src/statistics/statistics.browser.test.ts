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

test("the lines of a chart cut by a kind of tool, by who sent a prompt or by how it came are named", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    const legend = `[...document.querySelectorAll('.chart-legend-item')].map(item => item.textContent.trim())`;
    const named = async (title: string, expected: readonly string[]) => {
      await page.until(`${JSON.stringify(expected)}.every(name => ${legend}.includes(name))`, `${title}: ${expected.join(", ")} in a legend`);
      assert.deepEqual((await page.evaluate<string[]>(legend)).filter(name => /^\d*$/.test(name)), [], `${title}: no line is named by a number`);
    };
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    await openPage(page, "Tools");
    await named("Tools", ["Shell", "Files", "Search"]);
    await openPage(page, "Agents");
    await named("Agents", ["You", "An agent"]);
    await openPage(page, "Prompts");
    await named("Prompts by sender", ["You", "An agent"]);
    await page.clickText('.stats-choice button', "Kind");
    await named("Prompts by kind", ["New turn", "Queued"]);
  });
});

test("tools and providers are named as people know them, the MCP servers are listed, and the Models chart says what it counts", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    const block = (title: string) => `[...document.querySelectorAll('.stats-block')].find(item => item.querySelector('h3')?.textContent === ${JSON.stringify(title)})`;
    const texts = (title: string, selector: string) => page.evaluate<string[]>(`[...(${block(title)}?.querySelectorAll(${JSON.stringify(selector)}) ?? [])].map(item => item.textContent.trim())`);
    const raw = (names: readonly string[]) => names.filter(name => /^(ToolCall|Skill|McpToolCall):|mcp__/.test(name));
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");

    // The plugin files a provider under its key; the pages read it under the name the window gives it, and under its key when the window has none.
    const legend = `[...document.querySelectorAll('.chart-legend-item')].map(item => item.textContent.trim())`;
    await page.clickText('.stats-choice button', "Provider");
    await page.until(`${settled} && ${legend}.includes("Claude Code")`, "the providers of the legend");
    const providers = await page.evaluate<string[]>(legend);
    assert.ok(["Claude Code", "Codex", "GitHub Copilot", "gemini", "mistral"].every(name => providers.includes(name)), providers.join(", "));
    assert.ok(!providers.includes("claude-code") && !providers.includes("codex"), providers.join(", "));
    assert.deepEqual(raw(await texts("Top tools", ".stats-ranked-name span")), []);
    assert.ok((await texts("Top models", ".stats-ranked-name small")).includes("Claude Code"));
    await page.clickText('.stats-add-filter', "Filter");
    await page.clickText('.bp6-menu-item', "Provider");
    await page.until(`[...document.querySelectorAll('.stats-filter-list .bp6-menu-item')].some(item => item.textContent.trim() === "Claude Code")`, "the providers of the filter");
    await page.clickText('.stats-filter-list .bp6-menu-item', "Claude Code");
    await page.until(`document.querySelector('.stats-chip-text')?.textContent === "Provider: Claude Code"`, "the chip of the provider");
    await page.until(`${settled} && statsFixture.calls().some(call => call.request?.filter?.provider === "claude-code")`, "the questions filtered on the key");
    await page.click('.stats-chip-remove');

    await openPage(page, "Tools");
    const tools = await texts("The tools", "tbody td:first-child");
    assert.deepEqual(raw(tools), [], "no tool is named by the key of the plugin");
    assert.ok(tools.some(name => name.startsWith("read_file")) && tools.some(name => name.startsWith("shell")) && tools.some(name => name.startsWith("issue_read")), tools.join(" | "));
    assert.ok(tools.find(name => name.startsWith("issue_read"))!.includes("github"), "a tool of an MCP server says which server");
    assert.deepEqual(await texts("MCP servers", ".stats-ranked-name span"), ["issue_read"]);
    assert.deepEqual(await texts("MCP servers", ".stats-ranked-name small"), ["github"]);
    // A name cut to its column can be read whole.
    assert.deepEqual(await page.evaluate<string[]>(`[...${block("MCP servers")}.querySelectorAll('.stats-ranked-name span')].map(item => item.title)`), ["issue_read"]);
    for (const title of ["Where time goes", "Duration of one tool"]) {
      // A chart is drawn when it comes into view.
      await page.evaluate(`${block(title)}.scrollIntoView({ block: "center" })`);
      await page.until(`${block(title)}?.querySelectorAll('.chart-surface svg text').length > 2`, `the chart of ${title}`);
      const drawn = await texts(title, ".chart-surface svg text");
      assert.deepEqual(drawn.filter(text => /ToolCall|Skill:|mcp__/.test(text)), [], `${title}: ${drawn.join(" | ")}`);
    }
    // The marks of the axis of time: none runs into its neighbor, in a wide block and in a narrow one.
    const overlaps = `(() => { const marks = [...${block("Duration of one tool")}.querySelectorAll('.chart-surface svg text')].filter(item => /^[\\d.,]+ (ms|s|min|h)$/.test(item.textContent.trim())).map(item => item.getBoundingClientRect()).sort((a, b) => a.left - b.left);
      return { count: marks.length, overlaps: marks.filter((mark, index) => index > 0 && mark.left < marks[index - 1].right + 2).length }; })()`;
    for (const width of [1280, 640]) {
      await page.resize(width, 800);
      await page.evaluate(`${block("Duration of one tool")}.scrollIntoView({ block: "center" })`);
      await idle(700);
      const marks = await page.evaluate<{ count: number; overlaps: number }>(overlaps);
      assert.ok(marks.count >= 3, `${width}px: ${marks.count} marks on the axis`);
      assert.equal(marks.overlaps, 0, `${width}px: marks of the axis overlap`);
    }
    await page.resize(1280, 800);

    await openPage(page, "Models");
    const title = `document.querySelector('.stats-block h3')?.textContent`;
    assert.equal(await page.evaluate(title), "Tokens by model");
    await page.clickText('.stats-choice button', "Requests");
    await page.until(`${title} === "Requests by model"`, "the title follows Requests");
    await page.clickText('.stats-choice button', "Time");
    await page.until(`${title} === "Time by model"`, "the title follows Time");
    await page.clickText('.stats-choice button', "Tokens");
    await page.until(`${title} === "Tokens by model" && ${settled}`, "the title back to Tokens");
    assert.ok((await texts("The models", "tbody td:first-child small")).includes("Claude Code"));
    assert.ok(!(await texts("The models", "tbody td:first-child small")).includes("claude-code"));

    await openPage(page, "Sessions");
    const sessionProviders = await page.evaluate<string[]>(`[...document.querySelectorAll('.stats-table tbody .stats-cell-sub')].map(item => item.textContent.trim())`);
    assert.ok(sessionProviders.includes("Claude Code") && !sessionProviders.includes("claude-code"), sessionProviders.slice(0, 8).join(", "));
    // In a block too narrow for its table, the name of a model stays on one line: the table scrolls, the page does not.
    await page.resize(820, 800);
    await idle(500);
    const cut = await page.evaluate<string[]>(`[...document.querySelectorAll('.stats-table tbody td')].filter(cell => cell.querySelector('.stats-cell-sub') && cell.firstChild?.nodeType === 3)
      .filter(cell => { const range = document.createRange(); range.selectNodeContents(cell.firstChild); return range.getClientRects().length > 1; }).map(cell => cell.firstChild.textContent)`);
    assert.deepEqual([...new Set(cut)], [], "a model name is cut over two lines");
    assert.ok(await page.evaluate<number>(`document.documentElement.scrollWidth - document.documentElement.clientWidth`) <= 0, "the page does not scroll sideways");
    await page.resize(1280, 800);
    await openPage(page, "Health");
    assert.deepEqual(raw(await texts("Tools that fail", ".stats-ranked-name span")), []);
  });
});

test("one tool is one row whatever keys it has, a model of two providers is told apart, and the chip of a provider follows its name", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    const block = (title: string) => `[...document.querySelectorAll('.stats-block')].find(item => item.querySelector('h3')?.textContent === ${JSON.stringify(title)})`;
    const texts = (title: string, selector: string) => page.evaluate<string[]>(`[...(${block(title)}?.querySelectorAll(${JSON.stringify(selector)}) ?? [])].map(item => item.textContent.trim())`);
    const tableOf = async (title: string) => {
      await page.evaluate(`${block(title)}.scrollIntoView({ block: "center" })`);
      await page.until(`${block(title)}?.querySelector('.chart-table-toggle')`, `the chart of ${title}`);
      await page.evaluate(`${block(title)}.querySelector('.chart-table-toggle').click()`);
      await page.until(`${block(title)}.querySelector('table.chart-table tbody tr')`, `the table of ${title}`);
      return page.evaluate<string[]>(`[...${block(title)}.querySelectorAll('table.chart-table tbody tr')].map(row => row.firstElementChild.textContent.trim())`);
    };
    const twice = (names: readonly string[]) => names.filter((name, index) => names.indexOf(name) !== index);

    // The window has not named its providers yet: a filter set now has the key as its only name.
    await render(page, { scenario: "ready", providers: false });
    await page.until(settled, "the overview");
    await page.clickText('.stats-add-filter', "Filter");
    await page.clickText('.bp6-menu-item', "Provider");
    await page.until(`[...document.querySelectorAll('.stats-filter-list .bp6-menu-item')].some(item => item.textContent.trim() === "claude-code")`, "the providers by their key");
    await page.clickText('.stats-filter-list .bp6-menu-item', "claude-code");
    await page.until(`document.querySelector('.stats-chip-text')?.textContent === "Provider: claude-code"`, "the chip with the key");
    // The names arrive: the chip reads the name, and the filter is still on the key.
    await page.evaluate(`statsFixture.update({ providers: true })`);
    await page.until(`document.querySelector('.stats-chip-text')?.textContent === "Provider: Claude Code"`, "the chip follows the name");
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-remove').getAttribute('aria-label')`), "Remove filter: Provider: Claude Code");
    assert.ok((await calls(page)).filter(call => call.request?.filter?.provider).every(call => call.request.filter.provider === "claude-code"));
    // A reload keeps the filter and reads the name again.
    await page.evaluate(`statsFixture.remount()`);
    await page.until(`document.querySelector('.stats-chip-text')?.textContent === "Provider: Claude Code" && ${settled}`, "the chip after a reload");
    await page.click('.stats-chip-remove');
    await page.until(`!document.querySelector('.stats-chip') && ${settled}`, "the filter removed");

    // The fixture has one tool of an MCP server under two keys: one row, one line of the list, one box, one cell.
    await openPage(page, "Tools");
    const tools = (await texts("The tools", "tbody td:first-child")).filter(name => name.startsWith("issue_read"));
    assert.equal(tools.length, 1, tools.join(" | "));
    assert.deepEqual(await texts("MCP servers", ".stats-ranked-name span"), ["issue_read"]);
    assert.deepEqual(twice(await tableOf("Where time goes")), []);
    await page.evaluate(`${block("Duration of one tool")}.scrollIntoView({ block: "center" })`);
    await page.until(`${block("Duration of one tool")}?.querySelectorAll('.chart-surface svg text').length > 2`, "the durations");
    const boxes = (await texts("Duration of one tool", ".chart-surface svg text")).filter(text => !/^[\d.,]+ (ms|s|min|h)$/.test(text));
    assert.deepEqual(twice(boxes), [], boxes.join(" | "));
    assert.ok(boxes.includes("issue_read (github)"), boxes.join(" | "));
    // Both keys were asked for the box of that tool.
    const asked = (await calls(page)).filter(call => call.method === "distribution" && call.args[0] === "tool-duration").map(call => call.args[1]);
    assert.ok(asked.includes("ToolCall:mcp__github__issue_read") && asked.includes("McpToolCall:mcp__github__issue_read"), asked.join(", "));

    // The fixture has one model under two providers: its two bars say which is which.
    await openPage(page, "Models");
    const bars = await tableOf("What tokens are made of");
    assert.deepEqual(twice(bars), [], bars.join(" | "));
    assert.ok(bars.includes("gpt-6.1 (Codex)") && bars.includes("gpt-6.1 (GitHub Copilot)") && bars.includes("claude-opus-5-5"), bars.join(" | "));
    await page.clickText('.stats-add-filter', "Filter");
    await page.clickText('.bp6-menu-item', "Model");
    await page.until(`document.querySelectorAll('.stats-filter-list .bp6-menu-item').length > 2`, "the models of the filter");
    const offered = await page.evaluate<string[]>(`[...document.querySelectorAll('.stats-filter-list .bp6-menu-item .bp6-text-overflow-ellipsis')].map(item => item.textContent.trim())`);
    assert.deepEqual(twice(offered), [], "a model is offered once");
    await page.key("Escape", 27);

    // The axis of a time chart: round times, from 0.
    await openPage(page, "Activity");
    await page.evaluate(`${block("Active time")}.scrollIntoView({ block: "center" })`);
    await page.until(`${block("Active time")}?.querySelectorAll('.chart-surface svg text').length > 4`, "the active time");
    const marks = (await texts("Active time", ".chart-surface svg text")).filter(text => /^\d[\d,.]* ?(ms|s|min|h)?( \d\d( s)?)?$/.test(text));
    assert.ok(marks.includes("0") && marks.length >= 3, marks.join(" | "));
    assert.deepEqual(marks.filter(mark => !/^(0|[\d,]+ (ms|s|min|h)|[\d,]+ h (15|30|45)|\d+ min 30 s|\d+\.5 s)$/.test(mark)), [], `marks that are not round: ${marks.join(" | ")}`);
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
    // What a screen reader is told is the state, once: not the whole bar, whose numbers and buttons change or stay at every step.
    assert.equal(await page.evaluate(`document.querySelector('.stats-history').getAttribute('role')`), null);
    assert.equal(await page.evaluate(`document.querySelector('.stats-history [role="status"]').textContent`), "Reading the history");
    assert.equal(await page.evaluate(`document.querySelectorAll('.stats-history [role="status"] button, .stats-history [role="status"] [role="progressbar"]').length`), 0);
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
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-history [role="status"]').textContent`), /^History paused at .*\. 606 sessions left\.$/);
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
    // Reset asks first: Cancel changes nothing, the answer yes deletes the numbers and asks again how much to read.
    const resets = () => page.evaluate<number>(`statsFixture.calls().filter(call => call.method === 'resetStatistics').length`);
    await page.click('button[aria-label="Statistics menu"]');
    await page.clickText('.bp6-menu-item', "Reset statistics…");
    await page.until(`document.querySelector('.stats-confirm')`, "the question");
    assert.equal(await page.evaluate(`document.querySelector('.stats-confirm strong').textContent`), "Reset the statistics?");
    assert.equal(await resets(), 0, "nothing is deleted before the answer");
    await page.clickText('.stats-confirm button', "Cancel");
    await page.until(`!document.querySelector('.stats-confirm')`, "the question closed");
    assert.equal(await resets(), 0);
    await page.click('button[aria-label="Statistics menu"]');
    await page.clickText('.bp6-menu-item', "Reset statistics…");
    await page.until(`document.querySelector('.stats-confirm')`, "the question again");
    await page.clickText('.stats-confirm button', "Reset");
    await page.until(`document.querySelector('.stats-first') && !document.querySelector('.stats-confirm')`, "the first-time card");
    assert.equal(await resets(), 1);
  });
});

test("two Statistics tabs in one window share no element id, and each names its own parts", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    const repeated = `(() => { const ids = [...document.querySelectorAll('.statistics-canvas [id]')].map(element => element.id); return [...new Set(ids.filter((id, index) => ids.indexOf(id) !== index))]; })()`;
    // The application canvas and the canvas of a project stay mounted together: the card of the first time, twice.
    await render(page, { scenario: "first-time", copies: 2 });
    await page.until(`document.querySelectorAll('.stats-first').length === 2`, "two cards");
    assert.deepEqual(await page.evaluate(repeated), []);
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.stats-first')].every(card => card.getAttribute('aria-labelledby') === card.querySelector('h2').id && document.getElementById(card.querySelector('h2').id) === card.querySelector('h2'))`), true);
    // And the pages, twice: every tab controls its own panel.
    await render(page, { scenario: "ready", copies: 2 });
    await page.until(`document.querySelectorAll('[role="tablist"]').length === 2 && ${settled}`, "two canvases");
    assert.deepEqual(await page.evaluate(repeated), []);
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.statistics-canvas')].every(canvas => { const tab = canvas.querySelector('[role="tab"][aria-selected="true"]'); const panel = canvas.querySelector('[role="tabpanel"]'); return panel.id === tab.getAttribute('aria-controls') && panel.getAttribute('aria-labelledby') === tab.id; })`), true);
  });
});

test("one session is one session, in the card of the first time and in the notification of the end", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "first-time", sessionCount: 1 });
    await page.until(`document.querySelector('.stats-first')`, "the choice");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-first p').textContent`), /^1 session since [A-Z][a-z]{2} \d+, 2026 can be read /);
    await page.clickText('.stats-first button', "Read all the history");
    await page.until(`document.querySelector('.stats-history[data-view="reading"]')`, "the reading");
    await page.evaluate(`statsFixture.control.advance(10000)`);
    await page.until(`document.querySelector('.bp6-toast')?.textContent.includes('Your statistics are ready: 1 session since')`, "the notification of one session");
    assert.doesNotMatch(await page.evaluate<string>(`document.querySelector('.bp6-toast').textContent`), /1 sessions/);
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
    await page.clickText('.stats-skipped button', "Try again");
    await page.until(`!document.querySelector('.stats-history')`, "the sessions read at the second try");

    // A history the user stopped says where the charts start, and offers the same list beside it.
    await render(page, { scenario: "stopped-skipped" });
    await page.until(`document.querySelector('.stats-history[data-view="stopped"] button')`, "the skipped sessions of a stopped history");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-history [role="status"]').textContent`), /^The charts start on /);
    assert.equal(await page.evaluate(`document.querySelector('.stats-history button').textContent.trim()`), "3 sessions could not be read");
    await page.click('.stats-history button');
    await page.until(`document.querySelectorAll('.stats-skipped li').length === 3`, "the list of a stopped history");
    await page.clickText('.stats-skipped button', "Try again");
    await page.until(`document.querySelector('.stats-history[data-view="stopped"]') && !document.querySelector('.stats-history button')`, "the stopped history without skipped sessions");
    assert.equal(await page.evaluate(`statsFixture.calls().filter(call => call.method === 'resume').length`), 2);

    await render(page, { scenario: "failed" });
    await page.until(`document.querySelector('.stats-failed')`, "the failure");
    assert.match(await page.evaluate<string>(`document.querySelector('.stats-failed').textContent`), /The statistics could not start.*The statistics database could not be opened\./);
    // "Try again" starts the statistics again: here the start works, and the first choice is asked.
    await page.clickText('.stats-failed button', "Try again");
    await page.until(`document.querySelector('.stats-first') && !document.querySelector('.stats-failed')`, "the choice after a start that works");
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

test("a change that arrives while a question is in flight is not lost: the block asks again when its answer comes back stale", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready" });
    await page.until(settled, "the overview");
    await page.evaluate(`statsFixture.clearCalls()`);
    // The answers take two and a half seconds from now on. A change makes the page ask, a second later, and another change of the
    // same days arrives while those questions are in flight: their answers are older than it.
    await page.evaluate(`statsFixture.control.setLatency(2500)`);
    await page.evaluate(`statsFixture.control.emitData(20261009, 20261009)`);
    await page.until(`statsFixture.calls().some(call => call.method === 'summary')`, "the first round of questions");
    await page.evaluate(`statsFixture.control.emitData(20261009, 20261009)`);
    await idle(1300);
    await page.evaluate(`statsFixture.control.setLatency(0)`);
    await page.until(`statsFixture.calls().filter(call => call.method === 'summary').length >= 2`, "the summary asked again once its stale answer came back");
    await page.until(settled, "settled");
    await idle(1500);
    const again = questions(await calls(page));
    assert.equal(again.filter(call => call.method === "summary").length, 2, "asked once for each change, and not a third time");
    assert.ok(again.filter(call => call.method === "series").length >= 2, "the charts too");
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
    // The popover ignores an Escape while it is still opening.
    await idle(400);
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
    // The days under a time chart are regular, however narrow the tab: the same step from one to the next, none crowded.
    const labelSteps = await page.evaluate<number[]>(`(() => { const xs = [...document.querySelector('.stat-chart .chart-surface').querySelectorAll('svg text')].filter(text => /^[A-Z][a-z]{2}\\s\\d+$/.test(text.textContent)).map(text => { const box = text.getBoundingClientRect(); return (box.left + box.right) / 2; }).sort((a, b) => a - b); return xs.slice(1).map((x, index) => Math.round(x - xs[index])); })()`);
    assert.ok(labelSteps.length >= 2, `some days are named under the chart: ${labelSteps.join()}`);
    assert.ok(Math.max(...labelSteps) - Math.min(...labelSteps) <= 3, `the days under the chart are evenly spaced: ${labelSteps.join()}`);
    // The row of the pages scrolls and says on which side there is more; the page in front is brought into view.
    assert.equal(await page.evaluate(`document.querySelector('.stats-tabs > .bp6-tab-list').dataset.end`), "true", "the row of pages has more on its right");
    assert.equal(await page.evaluate(`document.querySelector('.stats-tabs > .bp6-tab-list').dataset.start`), "false");
    await page.evaluate(`[...document.querySelectorAll('.stats-tabs [role=tab]')].find(tab => tab.textContent === 'Health').click()`);
    await page.until(`document.querySelector('.stats-tabs > .bp6-tab-list').dataset.start === 'true' && document.querySelector('.stats-tabs > .bp6-tab-list').dataset.end === 'false'`, "the last page in view");
    const edges = await page.evaluate<number[]>(`(() => { const list = document.querySelector('.stats-tabs > .bp6-tab-list').getBoundingClientRect(), tab = document.querySelector('.stats-tabs [aria-selected=true]').getBoundingClientRect(); return [tab.left - list.left, list.right - tab.right]; })()`);
    assert.ok(edges[0] >= -1 && edges[1] >= -1, `the selected page is inside the row: ${edges.join(", ")}`);
    // The bar keeps its menu on the first row: the dates of the period give way before it wraps alone.
    await page.evaluate(`statsFixture.update({ width: 700 })`);
    await idle(400);
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.stats-control-sub')).display`), "none", "the dates of the period are hidden in a narrow tab");
    assert.equal(await page.evaluate(`(() => { const menu = document.querySelector('.stats-frame [aria-label="Statistics menu"]').getBoundingClientRect(), first = document.querySelector('.stats-frame .stats-control').getBoundingClientRect(); return menu.top - first.top < first.height; })()`), true, "the menu is on the first row");
    // A name in a narrow column of a table stays whole: the window breaks words anywhere in the HTML of a plugin, a table scrolls instead.
    await page.evaluate(`document.querySelector('.statistics-canvas').parentElement.style.overflowWrap = 'anywhere'`);
    await openPage(page, "Sessions");
    await page.until(`document.querySelector('.stats-table td:not([data-wide])')`, "the table of sessions");
    assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.stats-table td:not([data-wide])')).overflowWrap`), "normal");
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
