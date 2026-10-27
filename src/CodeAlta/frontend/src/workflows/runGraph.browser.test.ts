import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

// The graph of a run on the card of its call, and the tab of the run, in headless Edge under React StrictMode, in both
// themes and in a 900×650 window: a fan-out of 50 sessions folds into one group, which a click opens; the tab draws the
// graph large, the sessions, the timeline (ECharts), the script, and copies the graph as Mermaid.

const options = { entry: "../workflows/runGraph.mount.tsx", fixture: "runFixture", rpcFixture: "../demo-api.ts", width: 900, height: 650,
  styles: ["../scriptCard/scriptCard.css", "../workflows/workflows.css", "../automations/automations.css"] } as const;

// Whether an element shows its whole content without a horizontal scroll bar, within the page.
const noSideScroll = (selector: string) => `(() => { const e = document.querySelector(${JSON.stringify(selector)}); return !!e && e.scrollWidth <= e.clientWidth + 1
  && e.getBoundingClientRect().right <= document.documentElement.clientWidth + 1; })()`;

for (const light of [false, true]) {
  const theme = light ? "light" : "dark";
  test(`the card draws the declared steps from the start and folds 50 sessions into one group (${theme})`, { skip: !edge, timeout: 240_000 }, async () => {
    await withCanvas(async page => {
      await page.evaluate(`runFixture.theme(${light}); runFixture.renderCard(640)`);
      await page.until(`document.querySelectorAll('.run-graph-step').length === 3`, "the three declared steps");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.run-graph-step')].map(e => e.dataset.step + ':' + e.dataset.state)`),
        ["find:completed", "fix:running", "report:pending"], "the step not reached is there, pending");
      assert.equal(await page.evaluate(`document.querySelectorAll('.script-run-steps').length`), 0, "no line of marks beside the graph");
      // The fan-out: one group with its counts and a dot per session, no box per session.
      assert.equal(await page.evaluate(`document.querySelector('.run-graph-group-summary').textContent`), "50 sessions · 41 done · 6 running · 3 failed");
      assert.equal(await page.evaluate(`document.querySelectorAll('.run-graph-dots i').length`), 50);
      assert.equal(await page.evaluate(`document.querySelectorAll('.run-graph-step .script-session').length`), 0);
      assert.ok(await page.evaluate<boolean>(noSideScroll(".run-graph")), "the card does not scroll sideways");
      await page.shot(`run-card-${theme}`);

      // Opened: the running sessions first, then the failed ones; a box opens its session.
      await page.click(".run-graph-group-head");
      await page.until(`document.querySelectorAll('.run-graph-group .script-session').length === 50`, "the 50 sessions listed");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.run-graph-group .script-session')].slice(0, 10).map(e => e.dataset.state)`),
        [...Array(6).fill("running"), ...Array(3).fill("failed"), "completed"]);
      await page.click(".run-graph-group .script-session", 6);
      await page.until(`runFixture.state.opened.length === 1`, "the session opened");
      assert.equal(await page.evaluate(`runFixture.state.opened[0]`), "child-47", "the first failed session");
      await page.shot(`run-card-open-${theme}`);

      // Open run asks for the tab of the run.
      await page.clickText("button", "Open run");
      await page.until(`runFixture.state.runs.length === 1`, "the tab of the run asked for");

      // A narrow card stacks the columns and still never scrolls sideways.
      await page.evaluate(`runFixture.renderCard(320)`);
      await page.until(`document.querySelectorAll('.run-graph-step').length === 3`, "the narrow card");
      assert.equal(await page.evaluate(`getComputedStyle(document.querySelector('.run-graph-columns')).flexDirection`), "column");
      assert.ok(await page.evaluate<boolean>(noSideScroll(".run-graph")), "the narrow card does not scroll sideways");
    }, { ...options, colorScheme: light ? "light" : "dark" });
  });

  test(`the tab of a run: its graph large, its sessions, its timeline, its script, and Copy as Mermaid (${theme})`, { skip: !edge, timeout: 240_000 }, async () => {
    await withCanvas(async page => {
      await page.evaluate(`runFixture.theme(${light}); runFixture.renderTab()`);
      await page.until(`document.querySelector('.workflow-run-tab .run-graph[data-size="large"]')`, "the graph of the tab");
      assert.equal(await page.evaluate(`document.querySelector('.workflow-run-title h2').textContent`), "Fix failing tests");
      assert.equal(await page.evaluate(`document.querySelectorAll('.workflow-run-sessions tbody tr').length`), 50);
      assert.deepEqual(await page.evaluate(`[...document.querySelector('.workflow-run-sessions tbody tr').children].map(e => e.textContent)`),
        ["Fix child-0", "fix", "Done", "20 s", "1,500"]);
      await page.until(`document.querySelector('.workflow-run-tab svg path, .workflow-run-tab canvas')`, "the timeline drawn");
      assert.match(await page.evaluate<string>(`document.querySelector('.workflow-run-source').textContent`), /fix-failing-tests/);
      assert.ok(await page.evaluate<boolean>(noSideScroll(".workflow-run-tab")), "the tab does not scroll sideways at 900×650");
      await page.shot(`run-tab-${theme}`);

      await page.clickText("button", "Copy as Mermaid");
      await page.until(`runFixture.state.copied.length === 1`, "the Mermaid text copied");
      assert.match(await page.evaluate<string>(`runFixture.state.copied[0]`), /^flowchart LR\n/);
      await page.until(`[...document.querySelectorAll('button')].some(e => e.textContent.trim() === 'Copied')`, "the button says it was copied");
      // The tab reads the run again while it runs.
      await page.until(`runFixture.state.reads >= 2`, "a second read of the running run");
      assert.equal(await page.evaluate(`runFixture.state.gone`), 0);
    }, { ...options, colorScheme: light ? "light" : "dark" });
  });
}
