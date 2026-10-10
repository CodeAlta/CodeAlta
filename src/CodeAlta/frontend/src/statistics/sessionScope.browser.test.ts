import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "./browserHarness";

// The canvas of one session and its sub-agents, and the button of the card of a turn that opens it, in headless Edge under the
// production content security policy and React StrictMode. Each test ends by checking that the page raised no error.

type Call = { method: string; request: any; args: unknown[]; aborted: boolean };
const tabTitles = ["Overview", "Activity", "Models", "Cost", "Tools", "Prompts", "Agents", "Code", "Projects", "Sessions", "Health"];
const settled = `document.querySelector('.statistics-canvas') && !document.querySelector('.stats-block[data-state="loading"]') && !document.querySelector('.stats-tile-skeleton') && document.querySelectorAll('.stats-block').length > 0`;
const controls = ["status", "chooseHistory", "pause", "resume", "stopHere", "forgetDeleted", "resetStatistics"];
const questions = (calls: Call[]) => calls.filter(call => !controls.includes(call.method));
const calls = (page: Page) => page.evaluate<Call[]>(`statsFixture.calls()`);
const render = (page: Page, options: object = {}) => page.evaluate(`statsFixture.render(${JSON.stringify(options)})`);
const openPage = async (page: Page, title: string) => {
  await page.clickText('[role="tab"]', title);
  await page.until(`document.querySelector('[role="tab"][aria-selected="true"]')?.textContent.trim() === ${JSON.stringify(title)} && ${settled}`, `the ${title} page`);
};
// A session of the fixture that created others, with its tree.
const parentOf = `(() => { const all = statsFixture.control.data.sessions; const parent = all.find(one => all.some(other => other.parent === one.id));
  const tree = [parent.id]; for (let grown = true; grown;) { grown = false; for (const one of all) if (one.parent && tree.includes(one.parent) && !tree.includes(one.id)) { tree.push(one.id); grown = true; } }
  return { id: parent.id, title: parent.title, tree }; })()`;

test("the canvas of a session asks every page for that session and its sub-agents, and neither a chip nor Reset lifts it", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    // The fixture first, to learn a session with sub-agents; then the canvas of that session, in a window that shows a named space.
    await render(page, { scenario: "ready" });
    const session = await page.evaluate<{ id: string; title: string; tree: string[] }>(parentOf);
    assert.ok(session.tree.length >= 2, "the session has sub-agents");
    await page.until(settled, "the canvas of every session");
    await page.evaluate(`statsFixture.clearCalls()`);
    await render(page, { scenario: "ready", sessionId: session.id, spaceId: "space-work", instanceId: "canvas-session" });
    await page.until(settled, "the overview of the session");

    // The session is named, and it is the only chip: the space of the window is not a filter of this canvas.
    await page.until(`document.querySelector('.stats-chip-scope .stats-chip-text')?.textContent === ${JSON.stringify(`Session: ${session.title}`)}`, "the name of the session");
    assert.equal(await page.evaluate(`document.querySelectorAll('.stats-chip').length`), 1);
    assert.equal(await page.evaluate(`document.querySelectorAll('.stats-chip-scope .stats-chip-remove').length`), 0, "the session cannot be removed");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.stats-frame button')].some(button => button.textContent.trim() === 'Reset')`), false, "nothing to reset in a canvas that just opened");

    // It opens on the life of the session, not on the last 30 days.
    assert.equal(await page.evaluate(`document.querySelector('.stats-frame .stats-control-main').textContent`), "All time");
    assert.ok(questions(await calls(page)).some(call => call.method === "summary" && call.request.period === "all"));

    // Every page asks inside the session.
    for (const title of tabTitles) await openPage(page, title);
    const asked = questions(await calls(page));
    assert.ok(asked.length > 30, `the pages asked ${asked.length} questions`);
    for (const call of asked) {
      assert.equal(call.request?.filter?.session, session.id, `${call.method} ${JSON.stringify(call.request)}`);
      assert.equal(call.request.filter.space, undefined, call.method);
      // The chip asks for the row of the session alone; everything else is the session with its sub-agents.
      const chip = call.method === "sessions" && call.request.limit === 1;
      assert.equal(call.request.filter.withChildren, chip ? undefined : true, `${call.method} ${JSON.stringify(call.request)}`);
    }
    assert.deepEqual([...new Set(asked.map(call => call.method))].filter(method => !["summary", "series", "top", "tools", "models", "projects", "sessions", "distribution", "calendar", "weekHour", "records", "health", "details", "runs", "costEstimate"].includes(method)), []);

    // The table of sessions lists the session and its sub-agents, and each row opens its chat.
    await openPage(page, "Sessions");
    await page.until(`document.querySelectorAll('.stats-table tbody tr').length === ${session.tree.length}`, "the rows of the tree");

    // A filter is added and removed, then Reset: the session is still on every request.
    await page.evaluate(`statsFixture.clearCalls()`);
    await page.clickText('.stats-add-filter', 'Filter');
    await page.clickText('.bp6-menu-item', 'Started by');
    await page.clickText('.bp6-menu-item', 'You');
    await page.until(`document.querySelectorAll('.stats-chip').length === 2 && statsFixture.calls().length > 0 && ${settled}`, "the filter beside the session");
    const filtered = questions(await calls(page));
    assert.ok(filtered.every(call => call.request?.filter?.session === session.id && call.request.filter.withChildren === true), "a filter narrows the session, it does not replace it");
    assert.ok(filtered.some(call => call.request.filter.origin === "you"));
    await page.clickText('.stats-frame button', 'Reset');
    await page.until(`document.querySelectorAll('.stats-chip').length === 1 && ${settled}`, "the frame put back");
    // What Reset shows was read before; a change of the numbers makes the page ask again, and it asks inside the session.
    await page.evaluate(`statsFixture.clearCalls(); statsFixture.control.emitData()`);
    await page.until(`statsFixture.calls().length > 0 && ${settled}`, "the page asking again");
    const afterReset = questions(await calls(page));
    assert.ok(afterReset.length > 0);
    assert.ok(afterReset.every(call => call.request?.filter?.session === session.id && call.request.filter.origin === undefined), "Reset drops the filter and keeps the session");
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-scope .stats-chip-text').textContent`), `Session: ${session.title}`);

    // The list of values of a filter is asked inside the session too.
    await page.evaluate(`statsFixture.clearCalls()`);
    await page.clickText('.stats-add-filter', 'Filter');
    await page.clickText('.bp6-menu-item', 'Model');
    await page.until(`statsFixture.calls().some(call => call.method === 'models' && call.request.limit === 500)`, "the models to choose among");
    const wide = (await calls(page)).find(call => call.method === "models" && call.request.limit === 500)!;
    assert.deepEqual(wide.request.filter, { session: session.id, withChildren: true });
    await page.key("Escape", 27);

    // A reload keeps the session: it comes from the canvas, not from what the canvas stored.
    await page.evaluate(`statsFixture.clearCalls(); statsFixture.remount()`);
    await page.until(settled, "the canvas after a reload");
    assert.ok(questions(await calls(page)).every(call => call.request?.filter?.session === session.id));
    assert.ok(![...await page.evaluate<string[]>(`[...statsFixture.storage.values()]`)].some(value => value.includes(session.id)), "the stored frame does not hold the session");

    // The chip opens the session, with the pointer and with the keyboard.
    await page.click('.stats-chip-scope button.stats-chip-text');
    assert.deepEqual(await page.evaluate(`statsFixture.opened.slice()`), [session.id]);
    await page.evaluate(`document.querySelector('.stats-chip-scope button.stats-chip-text').focus()`);
    await page.key("Enter", 13, "\r");
    assert.deepEqual(await page.evaluate(`statsFixture.opened.slice()`), [session.id, session.id]);
    await page.shot("session-scope-dark-1280");

    // A narrow canvas keeps the name in reach without a sideways scroll.
    await page.resize(520, 800);
    await page.until(settled, "the narrow canvas");
    assert.ok(await page.evaluate<number>(`document.documentElement.scrollWidth - document.documentElement.clientWidth`) <= 0);
    await page.shot("session-scope-dark-520");
  });
});

test("a session the statistics have not read is named by its id and shows nothing of the others", { skip: !edge, timeout: 300_000 }, async () => {
  await withCanvas(async page => {
    await render(page, { scenario: "ready", sessionId: "01a00000-0000-7000-8000-000000000000", instanceId: "canvas-unread" });
    await page.until(`document.querySelector('.statistics-canvas') && !document.querySelector('.stats-block[data-state="loading"]') && !document.querySelector('.stats-tile-skeleton')`, "the canvas of a session that is not read");
    assert.equal(await page.evaluate(`document.querySelector('.stats-chip-scope .stats-chip-text').textContent`), "Session: 01a00000");
    const asked = questions(await calls(page));
    assert.ok(asked.length > 0 && asked.every(call => call.request?.filter?.session === "01a00000-0000-7000-8000-000000000000"));
    // Every page settles on "nothing": no block waits for ever for a question that has nothing to ask (the durations of no tool).
    for (const title of tabTitles) {
      await openPage(page, title);
      assert.equal(await page.evaluate(`document.querySelectorAll('.stats-block[data-state="loading"], .stats-block[data-state="error"]').length`), 0, title);
    }

    await openPage(page, "Sessions");
    assert.equal(await page.evaluate(`document.querySelectorAll('.stats-table tbody tr').length`), 0, "no session of anyone else");
  });
});

test("the button at the foot of the card of a turn runs for the session of the card and closes the details", { skip: !edge, timeout: 180_000 }, async () => {
  await withCanvas(async page => {
    const opened = `document.querySelector('dialog.timeline-details-dialog[open]')`;
    const button = `[...document.querySelectorAll('dialog.timeline-details-dialog button')].find(one => one.textContent.trim() === 'Session statistics')`;

    // In the timeline of a session: the pane of the timeline, not the focused one.
    await page.evaluate(`cardFixture.open({ sessionId: "card-session", projectId: "p-card" })`);
    await page.until(`${opened} && ${button}`, "the details of the card");
    // The details are the Markdown of the card, drawn by the window, and end with the button.
    await page.until(`document.querySelector('dialog.timeline-details-dialog .plugin-detail table')`, "the table of the details");
    assert.equal(await page.evaluate(`document.querySelector('dialog.timeline-details-dialog .plugin-detail h3').textContent`), "Detailed statistics");
    assert.equal(await page.evaluate(`(() => { const all = [...document.querySelectorAll('dialog.timeline-details-dialog .plugin-detail .plugin-html *')]; return all.at(-1).textContent.trim(); })()`), "Session statistics", "the button is the foot of the card");
    assert.equal(await page.evaluate(`${button}.getAttribute('type')`), "button");
    await page.shot("turn-card-details-dark");
    await page.evaluate(`${button}.scrollIntoView({ block: 'center' }); ${button}.setAttribute('data-test', 'session-statistics')`);
    await page.click('[data-test="session-statistics"]');
    await page.until(`cardFixture.state.ran.length === 1`, "the command of the card");
    assert.deepEqual(await page.evaluate(`cardFixture.state.ran[0]`), { name: "statistics-session", pluginKey: "statistics", pane: { sessionId: "card-session", projectId: "p-card" } });
    await page.until(`cardFixture.state.closed === 1 && !${opened}`, "the details closed: the canvas is not opened behind them");

    // With the keyboard.
    await page.evaluate(`cardFixture.open({ sessionId: "chat-session", projectId: null })`);
    await page.until(`${opened} && ${button}`, "the details again");
    await page.evaluate(`${button}.focus()`);
    await page.key("Enter", 13, "\r");
    await page.until(`cardFixture.state.ran.length === 2`, "the command by the keyboard");
    assert.deepEqual(await page.evaluate(`cardFixture.state.ran[1].pane`), { sessionId: "chat-session", projectId: null }, "a chat has no project, and still its session");

    // Shown where nothing names a pane, a command falls back to the focused pane, as it did.
    await page.evaluate(`cardFixture.open(null)`);
    await page.until(`${opened} && ${button}`, "the details without a pane around");
    await page.evaluate(`${button}.click()`);
    await page.until(`cardFixture.state.ran.length === 3`, "the command without a pane");
    assert.equal(await page.evaluate(`cardFixture.state.ran[2].pane`), null);
  }, { entry: "./turnCard.mount.tsx", fixture: "cardFixture" });
});
