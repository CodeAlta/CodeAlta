import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { edge, withCanvas, type Page } from "../statistics/browserHarness";

// The workflow block of a run in the timeline and the tab of a run, drawn with React Flow, in headless Edge under React
// StrictMode, in both themes, in a 1600×1000 and a 900×650 window: the research sample left to right, a fan-out of 50
// sessions folded into one group that opens, the block outside the folded tool calls, a graph drawn only near the view
// among 30 runs, and the tab that pans and zooms with a minimap.

const options = { entry: "../workflows/runGraph.mount.tsx", fixture: "runFixture", rpcFixture: "../demo-api.ts", width: 1600, height: 1000,
  styles: ["../timelineActivity/timelineActivity.css", "../scriptCard/scriptCard.css", "../workflows/workflows.css", "../automations/automations.css",
    // React Flow's own sheet comes with its chunk, after the application's.
    "../../node_modules/@xyflow/react/dist/style.css"] } as const;

// Whether an element shows its whole content without a horizontal scroll bar, within the page.
const noSideScroll = (selector: string) => `(() => { const e = document.querySelector(${JSON.stringify(selector)}); return !!e && e.scrollWidth <= e.clientWidth + 1
  && e.getBoundingClientRect().right <= document.documentElement.clientWidth + 1; })()`;
const lefts = (selector: string) => `[...document.querySelectorAll(${JSON.stringify(selector)})].map(e => Math.round(e.getBoundingClientRect().left))`;
const tops = (selector: string) => `[...document.querySelectorAll(${JSON.stringify(selector)})].map(e => Math.round(e.getBoundingClientRect().top))`;
// The pictures of the run go with the other looks at Code Mode, outside the repository.
const pictures = join(process.env.TEMP ?? tmpdir(), "ca-codemode", "ui");
async function shot(page: Page, name: string) {
  await mkdir(pictures, { recursive: true });
  const picture = await page.command("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
  await writeFile(join(pictures, `${name}.png`), Buffer.from(picture.data, "base64"));
}
// Whether every element of a head is on one line: their middles are within a few pixels of one another.
const oneLine = (selector: string) => `(() => { const middles = [...document.querySelectorAll(${JSON.stringify(selector)} + ' > *')].filter(e => e.getBoundingClientRect().width > 0)
  .map(e => { const r = e.getBoundingClientRect(); return (r.top + r.bottom) / 2; }); return Math.max(...middles) - Math.min(...middles) <= 4; })()`;
// The nodes do not overlap: each pair of boxes is apart.
const apart = `(() => { const boxes = [...document.querySelectorAll('.react-flow__node')].map(e => e.getBoundingClientRect());
  return boxes.every((a, i) => boxes.every((b, j) => i === j || a.right <= b.left + 1 || b.right <= a.left + 1 || a.bottom <= b.top + 1 || b.bottom <= a.top + 1)); })()`;

async function drawn(page: Page, steps: number) {
  await page.until(`document.querySelectorAll('.workflow-block .react-flow__node .workflow-step').length === ${steps} && document.querySelectorAll('.workflow-edge-line').length > 0`, "the graph drawn");
}

for (const light of [false, true]) {
  const theme = light ? "light" : "dark";
  test(`the block of the research run: its head, its steps left to right, its sessions, its actions (${theme})`, { skip: !edge, timeout: 240_000 }, async () => {
    await withCanvas(async page => {
      await page.evaluate(`runFixture.theme(${light}); runFixture.renderBlock(1180, "research")`);
      await drawn(page, 3);
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.workflow-step')].map(e => e.dataset.step + ':' + e.dataset.state)`),
        ["split:completed", "research:completed", "write:completed"]);
      const x = await page.evaluate<number[]>(lefts(".workflow-step"));
      assert.ok(x[0] < x[1] && x[1] < x[2], `left to right: ${x}`);
      assert.ok(await page.evaluate<boolean>(apart), "no node over another");
      assert.equal(await page.evaluate(`document.querySelector('.workflow-block-title strong').textContent`), "Research a question");
      assert.equal(await page.evaluate(`document.querySelector('.workflow-pill').textContent`), "Completed");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.workflow-block-facts > span')].map(e => e.textContent)`), ["2", "45k", "1 min 32 s"]);
      // The sessions of the fan-out, inside their step: their title, model, duration and tokens.
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('[data-step="research"] .workflow-session')].map(e => e.textContent)`),
        ["Fix child-agpt-6.1-sol · 54 s · 20k tokens", "Fix child-bclaude-sonnet-5-5 · 1 min 08 s · 24k tokens"]);
      // A fan-out says its count in its facts: the title keeps the width of the node, whole.
      assert.equal(await page.evaluate(`document.querySelector('[data-step="research"] .workflow-step-titles small').textContent`), "research · 1 min 09 s · × 2/2 sessions");
      assert.ok(await page.evaluate<boolean>(`[...document.querySelectorAll('.workflow-step-titles strong')].every(e => e.scrollWidth <= e.clientWidth)`), "no title is cut");
      assert.ok(await page.evaluate<boolean>(oneLine(".workflow-block-head")), "the head is one line");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.workflow-block-actions button')].map(e => e.getAttribute('aria-label'))`),
        ["Rerun with recorded results", "Save as script…", "Copy as Mermaid", "Show the call", "Open run"]);
      // The graph fits the width of the block: its height follows, without a scroll bar.
      assert.ok(await page.evaluate<boolean>(noSideScroll(".workflow-block-canvas")), "the canvas does not scroll sideways");
      assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.workflow-block-canvas')).overflowY`), "hidden");
      await shot(page, `workflow-block-${theme}`);

      await page.click('[data-step="research"] .workflow-session', 1);
      await page.until(`runFixture.state.opened.length === 1`, "the session opened");
      assert.equal(await page.evaluate(`runFixture.state.opened[0]`), "child-b");
      await page.click('[aria-label="Copy as Mermaid"]');
      await page.until(`runFixture.state.copied.length === 1`, "the Mermaid text copied");
      assert.match(await page.evaluate<string>(`runFixture.state.copied[0]`), /^flowchart LR\n {2}s0\["Split the question · completed"\]:::completed\n/);
      await page.click('[data-step="research"] .workflow-step-action');
      await page.until(`runFixture.state.reruns.length === 1`, "the rerun from the step asked for");
      assert.equal(await page.evaluate(`runFixture.state.reruns[0]`), "run-5b81e0:research#0");
      await page.click('[aria-label="Show the call"]');
      await page.click('[aria-label="Open run"]');
      await page.until(`runFixture.state.calls.length === 1 && runFixture.state.runs.length === 1`, "the call and the tab of the run asked for");

      // Narrower: the graph still flows left to right while it fits at a readable scale.
      await page.resize(900, 650);
      await page.evaluate(`runFixture.renderBlock(860, "research")`);
      await drawn(page, 3);
      const wide = await page.evaluate<number[]>(lefts(".workflow-step"));
      assert.ok(wide[0] < wide[1] && wide[1] < wide[2], "left to right at 860 px");
      assert.ok(await page.evaluate<boolean>(noSideScroll(".workflow-block-canvas")), "without a sideways scroll");
      await shot(page, `workflow-block-860-${theme}`);

      // Too narrow for that: top to bottom, the same nodes, its height follows; never a sideways scroll that hides a step.
      for (const width of [560, 420]) {
        await page.evaluate(`runFixture.renderBlock(${width}, "research")`);
        await drawn(page, 3);
        await page.until(`document.querySelector('[data-step="research"]')?.closest('.react-flow__node')?.querySelector('.react-flow__handle-top') != null`, "edges come in from the top");
        const y = await page.evaluate<number[]>(tops(".workflow-step")), x = await page.evaluate<number[]>(lefts(".workflow-step"));
        assert.ok(y[0] < y[1] && y[1] < y[2] && x[0] === x[1] && x[1] === x[2], `top to bottom at ${width} px: ${x} ${y}`);
        assert.ok(await page.evaluate<boolean>(noSideScroll(".workflow-block-canvas")), `no sideways scroll at ${width} px`);
        assert.ok(await page.evaluate<boolean>(apart), "no node over another");
        assert.ok(await page.evaluate<boolean>(`(() => { const c = document.querySelector('.workflow-block-canvas').getBoundingClientRect();
          return [...document.querySelectorAll('.workflow-step')].every(e => { const r = e.getBoundingClientRect(); return r.left >= c.left && r.right <= c.right && r.bottom <= c.bottom; }); })()`),
          "every step is in the block");
        // The head stays one line: the state and the totals, the actions in the menu.
        assert.ok(await page.evaluate<boolean>(oneLine(".workflow-block-head")), `the head is one line at ${width} px`);
        assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.workflow-block-actions')).display`), "none");
        assert.ok(await page.evaluate<boolean>(`document.querySelector('.workflow-pill').getBoundingClientRect().width > 0`), "the state stays");
        await shot(page, `workflow-block-${width}-${theme}`);
      }
      await page.click(".workflow-block-more button");
      await page.clickText(".bp6-menu-item", "Open run");
      await page.until(`runFixture.state.runs.length === 2`, "Open run from the menu");
      await page.until(`document.querySelector('.bp6-menu') === null`, "the menu closed");
      await page.click(".workflow-block-more button");
      await page.clickText(".bp6-menu-item", "Save as script…");
      await page.until(`document.querySelector('.script-save-form input') !== null`, "Save as script from the menu");
      await shot(page, `workflow-block-menu-save-${theme}`);
    }, { ...options, colorScheme: light ? "light" : "dark" });
  });

  test(`a fan-out of 50 sessions is one group with a status bar, which opens on its list (${theme})`, { skip: !edge, timeout: 240_000 }, async () => {
    await withCanvas(async page => {
      await page.evaluate(`runFixture.theme(${light}); runFixture.renderBlock(860, "fanOut")`);
      await drawn(page, 3);
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.workflow-step')].map(e => e.dataset.step + ':' + e.dataset.state)`),
        ["find:completed", "fix:running", "report:pending"], "the step not reached is there, pending");
      assert.equal(await page.evaluate(`document.querySelector('.workflow-pill').textContent.startsWith('Running')`), true);
      assert.equal(await page.evaluate(`document.querySelector('.workflow-group-line strong').textContent`), "50 sessions");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.workflow-statusbar i')].map(e => e.dataset.state + ':' + e.style.flexGrow)`),
        ["completed:41", "running:6", "failed:3"]);
      assert.equal(await page.evaluate(`document.querySelectorAll('.workflow-session').length`), 0, "no row per session while it is closed");
      assert.ok(await page.evaluate(`!!document.querySelector('[data-step="fix"] .workflow-ring')`), "the running step fills its ring as its sessions end");
      assert.ok(await page.evaluate(`!!document.querySelector('.workflow-edge[data-animated] .workflow-edge-current')`), "the edge toward the running step flows");
      // A run that goes on is stopped from its block, its first action.
      assert.equal(await page.evaluate(`[...document.querySelectorAll('.workflow-block-actions button')][0]?.getAttribute('aria-label')`), "Stop");
      await page.click('.workflow-block-actions [aria-label="Stop"]');
      await page.until(`runFixture.state.stops.length === 1`, "the run asked to stop");
      assert.equal(await page.evaluate(`runFixture.state.stops[0]`), "run-7f3c2a");
      const closed =await page.evaluate<number>(`document.querySelector('.workflow-block').getBoundingClientRect().height`);
      await shot(page, `workflow-fanout-${theme}`);

      await page.click(".workflow-group-head");
      await page.until(`document.querySelectorAll('.workflow-group .workflow-session').length === 50`, "the 50 sessions listed");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.workflow-group .workflow-session')].slice(0, 10).map(e => e.dataset.state)`),
        [...Array(6).fill("running"), ...Array(3).fill("failed"), "completed"]);
      await page.until(`document.querySelector('.workflow-block').getBoundingClientRect().height > ${closed} + 100`, "the block grows with the open group");
      assert.ok(await page.evaluate<boolean>(apart), "the open group pushes no node over another");
      assert.ok(await page.evaluate<boolean>(`(() => { const e = document.querySelector('.workflow-group-list'); return e.scrollHeight > e.clientHeight; })()`), "the list scrolls");
      await page.click(".workflow-group .workflow-session", 6);
      await page.until(`runFixture.state.opened.length === 1`, "the session opened");
      assert.equal(await page.evaluate(`runFixture.state.opened[0]`), "child-47", "the first failed session");
      await shot(page, `workflow-fanout-open-${theme}`);
    }, { ...options, width: 900, height: 650, colorScheme: light ? "light" : "dark" });
  });

  test(`the block is in the timeline outside the folded tool calls; the call goes to it (${theme})`, { skip: !edge, timeout: 240_000 }, async () => {
    await withCanvas(async page => {
      await page.evaluate(`runFixture.theme(${light}); runFixture.renderTimeline(false)`);
      await drawn(page, 3);
      assert.equal(await page.evaluate(`document.querySelectorAll('.tool-tile-title').length`), 0, "the calls are folded");
      assert.ok(await page.evaluate<boolean>(`document.querySelector('.workflow-block').getBoundingClientRect().width > 1400`), "the block takes the width of the conversation");
      await shot(page, `workflow-timeline-${theme}`);

      await page.evaluate(`runFixture.renderTimeline(true)`);
      await drawn(page, 3);
      assert.equal(await page.evaluate(`document.querySelectorAll('.workflow-block').length`), 1, "one block, under the open calls");
      assert.equal(await page.evaluate(`document.querySelectorAll('.timeline-activity-body .script-run, .timeline-activity-body .workflow-block').length`), 0,
        "the tile of the call is one line");
      await page.click(".tool-tile-title");
      await page.until(`document.activeElement?.classList.contains('workflow-block')`, "the call went to its block");
      assert.equal(await page.evaluate(`runFixture.state.calls.length`), 0, "it did not open the window of the call");
      await page.click('.workflow-block [aria-label="Show the call"]');
      await page.until(`runFixture.state.calls.length === 1`, "the block opens the window of its call");
    }, { ...options, colorScheme: light ? "light" : "dark" });
  });
}

test("among 30 runs, a graph is drawn only near the view, in a place of its size", { skip: !edge, timeout: 240_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`runFixture.renderMany(30)`);
    await page.until(`document.querySelectorAll('.workflow-block .react-flow').length > 0`, "the graphs in view drawn");
    const drawnFirst = await page.evaluate<number>(`document.querySelectorAll('.workflow-block .react-flow').length`);
    assert.ok(drawnFirst < 30, `${drawnFirst} graphs drawn, not 30`);
    const heights = await page.evaluate<number[]>(`[...document.querySelectorAll('.workflow-block')].map(e => Math.round(e.getBoundingClientRect().height))`);
    assert.equal(new Set(heights).size, 1, `a place of the same size, drawn or not: ${[...new Set(heights)]}`);
    await page.evaluate(`document.querySelector('.fixture-frame').scrollTop = 1e9`);
    await page.until(`!!document.querySelectorAll('.workflow-block')[29].querySelector('.react-flow')`, "the last graph drawn once in view");
    assert.ok(await page.evaluate<boolean>(`!document.querySelectorAll('.workflow-block')[0].querySelector('.react-flow')`), "the first one left with the view");
  }, { ...options, width: 900, height: 650 });
});

for (const light of [false, true]) {
  const theme = light ? "light" : "dark";
  test(`the tab of a run: its graph pans and zooms with a minimap, its sessions, its timeline, Copy as Mermaid (${theme})`, { skip: !edge, timeout: 240_000 }, async () => {
    await withCanvas(async page => {
      await page.evaluate(`runFixture.theme(${light}); runFixture.renderTab()`);
      await page.until(`document.querySelectorAll('.workflow-run-graph .workflow-step').length === 3 && document.querySelectorAll('.workflow-run-graph .workflow-edge-line').length === 2`, "the graph of the tab");
      // Fitted, the whole graph is in view: no minimap over it, and the controls cover no step.
      assert.ok(await page.evaluate(`!!document.querySelector('.workflow-run-graph .react-flow__controls')`));
      await page.until(`(() => { const c = document.querySelector('.workflow-run-graph .react-flow__controls').getBoundingClientRect();
        return [...document.querySelectorAll('.workflow-run-graph .react-flow__node')].every(e => { const r = e.getBoundingClientRect();
          return r.right <= c.left || c.right <= r.left || r.bottom <= c.top || c.bottom <= r.top; }); })()`, "the controls over no step");
      assert.equal(await page.evaluate(`document.querySelector('.workflow-run-graph .react-flow__minimap')`), null, "no minimap while the graph is whole in view");
      // Zoomed in, the graph is larger than the view: the minimap shows, small, to move around it.
      for (let index = 0; index < 4; index++) await page.click(".workflow-run-graph .react-flow__controls-zoomin");
      await page.until(`!!document.querySelector('.workflow-run-graph .react-flow__minimap')`, "the minimap of a graph larger than the view");
      assert.ok(await page.evaluate<boolean>(`document.querySelector('.workflow-run-graph .react-flow__minimap').getBoundingClientRect().width <= 164`), "a small minimap");
      await page.click(".workflow-run-graph .react-flow__controls-fitview");
      await page.until(`document.querySelector('.workflow-run-graph .react-flow__minimap') === null`, "fitted again, no minimap");
      assert.equal(await page.evaluate(`document.querySelector('.workflow-run-title h2').textContent`), "Fix failing tests");
      assert.equal(await page.evaluate(`document.querySelectorAll('.workflow-run-sessions tbody tr').length`), 50);
      assert.deepEqual(await page.evaluate(`[...document.querySelector('.workflow-run-sessions tbody tr').children].map(e => e.textContent)`),
        ["Fix child-0", "fix", "Done", "20 s", "1,500"]);
      // The timeline of the run is drawn once it is in view, under the 50 sessions.
      await page.evaluate(`document.querySelector('.workflow-run-tab .chart').scrollIntoView({ block: "nearest" })`);
      await page.until(`document.querySelector('.workflow-run-tab .chart-surface svg path, .workflow-run-tab .chart-surface canvas')`, "the timeline drawn");
      // Its bars have the colors of the graph: done in green, running in the accent, failed in red.
      for (const variable of ["--green", "--red", "--accent"]) {
        assert.ok(await page.evaluate<boolean>(`(() => { const probe = document.createElement('span'); document.body.append(probe);
          const color = value => { probe.style.color = ''; probe.style.color = value; return getComputedStyle(probe).color; };
          const want = color('var(${variable})');
          const found = [...document.querySelectorAll('.workflow-run-tab .chart-surface svg path')].some(p => color(p.getAttribute('fill') || '') === want);
          probe.remove(); return found; })()`), `a bar in the color of ${variable}`);
      }
      await page.evaluate(`document.querySelector('.workflow-run-tab').scrollTop = 0`);
      assert.match(await page.evaluate<string>(`document.querySelector('.workflow-run-source').textContent`), /fix-failing-tests/);
      // The script in the colors of JavaScript, its lines numbered: an error names its line.
      assert.ok(await page.evaluate<boolean>(`!!document.querySelector('.workflow-run-source .hljs-keyword')`), "the script is highlighted");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.workflow-run-source .tool-code-number')].map(e => e.textContent)`), ["1", "2"]);
      assert.ok(await page.evaluate<boolean>(noSideScroll(".workflow-run-tab")), "the tab does not scroll sideways");
      // Its group opens there too.
      await page.click(".workflow-run-graph .workflow-group-head");
      await page.until(`document.querySelectorAll('.workflow-run-graph .workflow-session').length === 50`, "the group open in the tab");
      await shot(page, `run-tab-${theme}`);

      await page.clickText("button", "Copy as Mermaid");
      await page.until(`runFixture.state.copied.length === 1`, "the Mermaid text copied");
      assert.match(await page.evaluate<string>(`runFixture.state.copied[0]`), /^flowchart LR\n/);
      await page.until(`[...document.querySelectorAll('button')].some(e => e.textContent.trim() === 'Copied')`, "the button says it was copied");
      await page.until(`runFixture.state.reads >= 2`, "a second read of the running run");
      assert.equal(await page.evaluate(`runFixture.state.gone`), 0);
      // The run goes on: its tab stops it.
      await page.clickText("button", "Stop");
      await page.until(`runFixture.state.stops.length === 1`, "the run asked to stop from its tab");
      assert.equal(await page.evaluate(`runFixture.state.stops[0]`), "run-7f3c2a");
      // The window narrows while the timeline is out of view: it follows the width all the same.
      await page.evaluate(`document.querySelector('.workflow-run-tab').scrollTop = 0`);
      await page.resize(900, 650);
      await page.until(noSideScroll(".workflow-run-tab"), "the tab does not scroll sideways at 900×650");
      await shot(page, `run-tab-narrow-${theme}`);
    }, { ...options, colorScheme: light ? "light" : "dark" });
  });
}
