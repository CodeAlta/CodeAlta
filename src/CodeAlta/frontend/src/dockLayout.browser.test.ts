import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { dockLayoutKey } from "./dockLayout";
import { sessionDraftNodeId, sessionNodeId } from "./sessionTabLayout";
import { sessionTabsKey } from "./sessionTabs";
import { withSessionWindow, type SessionWindow } from "./sessionWindowHarness";
import { testLimit } from "./testTime";

const panes = `[...document.querySelectorAll('.session-dock .flexlayout__tabset')].map(pane => [...pane.querySelectorAll('.flexlayout__tab_button')]
  .map(button => (button.textContent ?? '').trim() + (button.classList.contains('flexlayout__tab_button--selected') ? '*' : '')).join(' | '))`;
const showing = (window: SessionWindow, expected: readonly string[], what: string) =>
  window.until(`JSON.stringify(${panes})===${JSON.stringify(JSON.stringify(expected))}`, what);
const entry = (text: string) => `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].find(item => item.textContent.trim() === ${JSON.stringify(text)})`;
const kept = `localStorage.getItem(${JSON.stringify(dockLayoutKey)})`;
const splitPanes = ["one - Project*", "two - Project*"] as const;
const storage = { settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" } as const;

async function splitRight(window: SessionWindow) {
  await window.press(window.title("two - Project"), 1, "right");
  await window.until(`!!${entry("Split right")}`, "the entry Split right");
  await window.press(entry("Split right"));
  await showing(window, splitPanes, "two panes");
}

// Hold the production save timers so the transition, not elapsed wall time, must persist the change.
async function holdSaveTimers({ evaluate }: SessionWindow) {
  await evaluate(`(() => { const schedule = window.setTimeout;
    window.setTimeout = (callback, delay, ...args) => delay === 400 || delay === 200 ? -1 : schedule(callback, delay, ...args);
  })()`);
}

test("the panes of the dock come back at the next start", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow(storage, async window => {
    const { command, until, press, session, exceptions } = window;
    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await press(session("two"), 2);
    await splitRight(window);
    await until(`(() => { try { return JSON.parse(${kept}).layout.children.length === 2; } catch { return false; } })()`, "the two panes kept");

    await command("Page.reload");
    await until(`!!${session("two")}`, "the window loaded again");
    await showing(window, splitPanes, "the two panes at the next start, each with its tab in front");
    assert.deepEqual(exceptions, []);
  });
});

test("leaving a space keeps its new split without waiting for a save timer", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow(storage, async window => {
    const { evaluate, until, press, session, exceptions } = window;
    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the Default space with session one open");
    await press(session("two"), 2);
    await holdSaveTimers(window);
    await splitRight(window);
    assert.notEqual(await evaluate(`JSON.parse(${kept})?.layout.children.length`), 2, "the debounce has not saved the split");

    await evaluate(`document.querySelector('.space-chip[aria-label^="Work"]').click()`);
    await until(`document.querySelector('.space-switch-name')?.textContent === 'Work'`, "the window shows Work");
    assert.equal(await evaluate(`JSON.parse(${kept})?.layout.children.length`), 2, "leaving saved the Default space, not Work");
    assert.deepEqual(exceptions, []);
  }, new URL("./spaces/spaces.neoastra.mount.ts", import.meta.url));
});

test("exiting flushes the latest split before asking the host to close the page", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow(storage, async window => {
    const { evaluate, until, press, session, exceptions } = window;
    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await press(session("two"), 2);
    await holdSaveTimers(window);
    await splitRight(window);
    assert.notEqual(await evaluate(`JSON.parse(${kept})?.layout.children.length`), 2, "the debounce has not saved the split");

    await evaluate(`projectFocusFixture.notify({ kind: 'exit-requested', runningSessions: 0, busyTerminals: 0 })`);
    await until(`projectFocusFixture.shell.includes('exit')`, "the application requested exit");
    assert.equal(await evaluate(`JSON.parse(${kept})?.layout.children.length`), 2, "the split is already persisted, even without pagehide");
    assert.deepEqual(exceptions, []);
  });
});

test("a prompt that is not restored cannot keep an obsolete split pane alive", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  const savedTabs = ["one", "two"].map(sessionId => ({ sessionId, projectId: "project", path: "/fixture/project" }));
  const oldLayout = JSON.stringify({ version: 1, layout: { type: "row", children: [
    { type: "tabset", active: true, children: [{ type: "tab", id: sessionDraftNodeId, config: { preview: true }, enableDrag: false }] },
    { type: "tabset", children: [{ type: "tab", id: sessionNodeId(savedTabs[0]) }] },
  ] } });
  // Session two was opened after that layout was saved, replacing the transient prompt.
  await withSessionWindow({ ...storage, [dockLayoutKey]: oldLayout,
    [sessionTabsKey]: JSON.stringify({ version: 1, open: savedTabs, active: savedTabs[1] }) }, async window => {
    await showing(window, ["one - Project | two - Project*"], "the new tab joins the pane that still exists");
    assert.deepEqual(window.exceptions, []);
  });
});

test("the secondary window reports its new panes and selection before unload", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow(storage, async window => {
    const { command, evaluate, exceptions } = window;
    await showing(window, ["one - Project* | two - Project"], "two saved tabs in the secondary window");
    await holdSaveTimers(window);
    await splitRight(window);
    const page = `JSON.parse(localStorage.getItem('dockLayoutFixtureWindowPage'))`;
    assert.equal(await evaluate(`JSON.parse(${page}?.layout ?? null)?.layout.children.length`), 2, "the latest layout reached the secondary window's store");
    assert.equal(await evaluate(`JSON.parse(${page}?.sessionTabs ?? null)?.active?.sessionId`), "two", "the window's selected tab was reported with its layout");
    assert.equal(await evaluate(kept), null, "a secondary window does not write the main window's layout");

    await command("Page.reload");
    await showing(window, splitPanes, "the secondary window restored both panes");
    assert.deepEqual(exceptions, []);
  }, new URL("./dockLayout.neoastra.mount.ts", import.meta.url));
});
