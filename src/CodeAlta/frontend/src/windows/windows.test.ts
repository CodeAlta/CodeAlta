import assert from "node:assert/strict";
import test from "node:test";
import type { WindowsCurrentResponse, WindowsEvent, WindowsWindow } from "#neoastra";
import { automationsTab, canvasTab, changesTab, documentationTab, editorTab, terminalTab } from "../fileTabs";
import { fileTabRef, readTransfer, sessionTabRef, spaceBound, tabRefKind, transferOf } from "./tabRefs";
import { createWindowsHub, readWindowPage, writeWindowPage, type WindowsApi } from "./windowsHub";

const project = { id: "p1", path: "C:\\code\\p1" };

test("a tab is named as the host and alta window name it", () => {
  assert.equal(sessionTabRef("s1"), "session:s1");
  assert.equal(fileTabRef(editorTab(project)), "editor:p1");
  assert.equal(fileTabRef(changesTab(project)), "changes:p1");
  assert.equal(fileTabRef(terminalTab({ id: "t1", projectId: "p1", folder: "C:\\code\\p1" })), "terminal:t1");
  assert.equal(fileTabRef(documentationTab), "documentation");
  assert.equal(fileTabRef(automationsTab), "automations");
  assert.equal(fileTabRef(canvasTab({ pluginKey: "git", canvasId: "board", project: { id: "p1", path: "C:\\code\\p1" } })), "canvas:git/board/p1//");
  assert.equal(tabRefKind("session:s1"), "session");
  assert.equal(tabRefKind("documentation"), "documentation");
});

test("what is of a project stays in a window of its space; what is of the application goes anywhere", () => {
  assert.equal(spaceBound({ session: { sessionId: "s1", projectId: "p1", path: "C:\\code\\p1" } }), true);
  assert.equal(spaceBound({ file: editorTab(project) }), true);
  assert.equal(spaceBound({ file: changesTab(project) }), true);
  assert.equal(spaceBound({ file: documentationTab }), false);
  assert.equal(spaceBound({ file: terminalTab({ id: "t1", projectId: "p1", folder: "C:\\" }) }), false);
  assert.equal(spaceBound({ file: canvasTab({ pluginKey: "git", canvasId: "board" }) }), false);
});

test("a moved tab carries what the window that receives it opens", () => {
  const session = { session: { sessionId: "s1", projectId: "p1", path: "C:\\code\\p1" } };
  assert.deepEqual(readTransfer(transferOf(session)), session);
  assert.deepEqual(readTransfer(transferOf({ file: editorTab(project) })), { file: editorTab(project) });
  // What is not one is nothing.
  assert.equal(readTransfer(null), null);
  assert.equal(readTransfer("{"), null);
  assert.equal(readTransfer(JSON.stringify({ version: 2, session: session.session })), null);
  assert.equal(readTransfer(JSON.stringify({ version: 1, session: { sessionId: "" } })), null);
});

test("a secondary window keeps its tabs in its window", () => {
  const page = { sessionTabs: "{\"version\":1,\"open\":[]}", fileTabs: null };
  assert.deepEqual(readWindowPage(writeWindowPage(page)), page);
  assert.deepEqual(readWindowPage(null), { sessionTabs: null, fileTabs: null });
  assert.deepEqual(readWindowPage("not json"), { sessionTabs: null, fileTabs: null });
});

const window = (id: string, changes: Partial<WindowsWindow> = {}): WindowsWindow => ({ id, main: id === "main", name: id === "main" ? "Main" : "Window 2", icon: null, color: null,
  saved: false, open: true, spaceId: "default", displayId: null, displayName: null, displayPrimary: false, pinnedDisplayId: null, pinnedDisplayName: null, pinnedConnected: false,
  state: "normal", focused: false, tabs: [], activeTab: null, ...changes });

function fakeApi(windows: () => WindowsWindow[]) {
  const calls: Array<[string, unknown]> = [];
  const events: WindowsEvent[] = [];
  let push: (() => void) | null = null;
  const api = {
    list: async () => ({ status: "ok", windows: windows(), restoreAtStartup: true }),
    watch: async (_request: object, options?: { signal?: AbortSignal }) => ({
      async *[Symbol.asyncIterator]() {
        while (!options?.signal?.aborted) {
          if (events.length) { yield events.shift()!; continue; }
          await new Promise<void>(resolve => { push = resolve; options?.signal?.addEventListener("abort", () => resolve(), { once: true }); });
        }
      },
    }),
    report: async (request: unknown) => { calls.push(["report", request]); return { status: "ok" }; },
    moveTab: async (request: unknown) => { calls.push(["moveTab", request]); return { status: "ok", windowId: "w2" }; },
    refuseMove: async (request: unknown) => { calls.push(["refuseMove", request]); return { status: "ok" }; },
    focusTab: async (request: unknown) => { calls.push(["focusTab", request]); return { status: "ok", windowId: "w2" }; },
    focus: async (request: unknown) => { calls.push(["focus", request]); return { status: "ok" }; },
    close: async (request: unknown) => { calls.push(["close", request]); return { status: "ok" }; },
    open: async (request: unknown) => { calls.push(["open", request]); return { status: "ok", window: null }; },
    setIdentity: async (request: unknown) => { calls.push(["setIdentity", request]); return { status: "ok", window: null }; },
    forget: async (request: unknown) => { calls.push(["forget", request]); return { status: "ok" }; },
    setRestoreAtStartup: async (request: unknown) => { calls.push(["setRestoreAtStartup", request]); return { status: "ok" }; },
    displays: async () => ({ status: "ok", displays: [] }),
    dragOver: async (request: unknown) => { calls.push(["dragOver", request]); return { status: "ok", windowId: null }; },
    drop: async (request: unknown) => { calls.push(["drop", request]); return { status: "ok", windowId: null }; },
  } as unknown as WindowsApi;
  return { api, calls, send(event: WindowsEvent) { events.push(event); push?.(); } };
}
const current = (value: WindowsWindow): WindowsCurrentResponse => ({ status: "ok", window: value, pageState: null, restoreAtStartup: true, canDrag: true, windows: [] });
const settle = () => new Promise(resolve => setTimeout(resolve, 0));

test("a page that is no window of the host is the main one, and asks nothing", () => {
  const { api } = fakeApi(() => []);
  const hub = createWindowsHub(api, null);
  assert.equal(hub.secondary, false);
  assert.equal(hub.heldElsewhere("session:s1"), false);
  assert.equal(hub.handles("terminal:t1"), true);
});

test("a tab that another window holds is shown there; a request that reaches every window is the holder's, else the main one's", async () => {
  let windows = [window("main", { tabs: ["session:s1"] }), window("w2", { tabs: ["session:s2", "terminal:t2"] })];
  const main = fakeApi(() => windows);
  const hub = createWindowsHub(main.api, current(windows[0]));
  const stop = hub.connect();
  await settle(); await settle();
  assert.equal(hub.secondary, false);
  assert.equal(hub.heldElsewhere("session:s2"), true);
  assert.equal(hub.heldElsewhere("session:s1"), false);
  assert.equal(hub.handles("terminal:t2"), false, "the window that holds the terminal shows it");
  assert.equal(hub.handles("terminal:t9"), true, "a terminal no window holds: the main window");

  const second = createWindowsHub(fakeApi(() => windows).api, current(windows[1]));
  assert.equal(second.secondary, true);
  assert.equal(second.handles("terminal:t9"), false);

  // The window closed: its tabs are no longer held anywhere once the host lists the windows again.
  windows = [window("main", { tabs: ["session:s1"] }), window("w2", { open: false, saved: true, tabs: ["session:s2"] })];
  main.send({ kind: "changed", tab: null, payload: null, requestId: null, target: null, x: null, y: null, refused: false });
  await settle(); await settle();
  assert.equal(hub.heldElsewhere("session:s2"), false);
  stop();
});

test("what the host asks of the window reaches the shell, and the tabs are reported once", async () => {
  const fake = fakeApi(() => [window("main")]);
  const hub = createWindowsHub(fake.api, current(window("main")));
  const asked: WindowsEvent[] = [];
  hub.onRequest(value => asked.push(value));
  const stop = hub.connect();
  await settle();
  fake.send({ kind: "focus", tab: "session:s1", payload: null, requestId: null, target: null, x: null, y: null, refused: false });
  await settle(); await settle();
  assert.deepEqual(asked.map(value => value.kind), ["focus"]);

  hub.report(["session:s1"], "session:s1", null);
  hub.report(["session:s1"], "session:s1", null);
  await new Promise(resolve => setTimeout(resolve, 250));
  assert.equal(fake.calls.filter(([name]) => name === "report").length, 1);
  assert.equal(hub.ownerOf("session:s1"), "main", "the tabs of the window are its own at once");

  hub.claim("editor:p1");
  assert.equal(hub.ownerOf("editor:p1"), "main");
  stop();
});
