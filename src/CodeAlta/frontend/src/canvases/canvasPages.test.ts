import assert from "node:assert/strict";
import test from "node:test";
import type { CanvasItem } from "#neoastra";
import { canvasTab, canvasesTab, changesTab, editorTab, emptyFileTabs, fileTabKey, isCanvasesTab, openFileTab, persistFileTabs, reconcileFileTabs, restoreFileTabs, type FileTabs } from "../fileTabs";
import { defaultSpace, type Space } from "../spaces/spaces";
import {
  abandonedCanvas, addCanvasTabToSpace, bringCanvasTab, canvasCommandName, canvasCommands, canvasMenuItems, canvasRef, canvasRequestSpace, canvasScope, canvasTabOf, closedCanvasTab, defaultCanvasTarget,
  matchesCanvasCommand, newCanvasPrompt, openCanvases, removeCanvasTabFromSpace, showsCanvas,
} from "./canvasPages";

const item = (id: string, scope: string, fields: Partial<CanvasItem> = {}): CanvasItem => ({ pluginKey: "global:tools", plugin: "Tools", package: "plugin:global:tools", id, title: id[0].toUpperCase() + id.slice(1),
  description: null, icon: null, iconData: null, scope, input: false, actions: 0, describes: false, ...fields });
const project = { id: "p1", path: "/code/one" };
const space = (id: string, projectIds: string[]): Space => ({ ...defaultSpace, id, name: id, isDefault: false, projectIds });

test("a canvas has a scope, and a ref that names its plugin", () => {
  assert.deepEqual(["Application", "Project", "Session", "Odd"].map(scope => canvasScope({ scope })), ["Application", "Project", "Session", "Application"]);
  assert.equal(canvasRef(item("board", "Session")), "global:tools/board");
});

test("a canvas opened without a choice is about what is selected, and cannot be opened when the scope has nothing", () => {
  const selected = { project, session: { id: "s1", project } };
  assert.deepEqual(defaultCanvasTarget(item("stats", "Application"), { project: null, session: null }), { project: null, sessionId: null });
  assert.deepEqual(defaultCanvasTarget(item("list", "Project"), selected), { project, sessionId: null });
  assert.equal(defaultCanvasTarget(item("list", "Project"), { project: null, session: null }), null);
  assert.deepEqual(defaultCanvasTarget(item("run", "Session"), selected), { project, sessionId: "s1" });
  assert.deepEqual(defaultCanvasTarget(item("run", "Session"), { project: null, session: { id: "chat", project: null } }), { project: null, sessionId: "chat" }, "A chat has no project.");
  assert.equal(defaultCanvasTarget(item("run", "Session"), { project, session: null }), null);
});

test("the tab of a canvas is named by what its scope says, and shows the declaration until the plugin says better", () => {
  const board = item("board", "Application", { icon: "tick" });
  assert.deepEqual(canvasTabOf(board, { project, sessionId: "s1" }), canvasTab({ pluginKey: "global:tools", canvasId: "board" }, { title: "Board", icon: "tick", plugin: "plugin:global:tools" }),
    "An application canvas is about no project and no session, whatever the window has selected.");
  const list = canvasTabOf(item("list", "Project"), { project, sessionId: "s1" });
  assert.deepEqual([list.projectId, list.sessionId], ["p1", undefined]);
  const run = canvasTabOf(item("run", "Session"), { project, sessionId: "s1" });
  assert.deepEqual([run.projectId, run.sessionId], ["p1", "s1"]);
  assert.equal(fileTabKey(canvasTabOf(board, { project: null, sessionId: null })), fileTabKey(canvasTabOf(board, { project, sessionId: "s1" })), "It is one tab.");
});

test("the tabs of a canvas are the ones of its plugin and its id", () => {
  const board = item("board", "Application");
  const tabs = [canvasTabOf(board, { project: null, sessionId: null }), canvasTabOf(item("list", "Project"), { project, sessionId: null }), editorTab(project)];
  assert.deepEqual(openCanvases(tabs, board), [tabs[0]]);
  assert.ok(showsCanvas(tabs[1], item("list", "Project")) && !showsCanvas(tabs[2], board));
  assert.ok(!showsCanvas(tabs[0], { pluginKey: "other", id: "board" }));
});

test("a menu lists the first canvases of its scope and says when the page has more", () => {
  const items = [item("a", "Project"), item("b", "Session"), item("c", "Project"), item("d", "Project"), item("e", "Project"), item("f", "Project"), item("g", "Application")];
  const menu = canvasMenuItems(items, "Project");
  assert.deepEqual(menu.items.map(value => value.id), ["a", "c", "d", "e"]);
  assert.equal(menu.more, true);
  assert.deepEqual(canvasMenuItems(items, "Session"), { items: [items[1]], more: false });
  assert.deepEqual(canvasMenuItems(items, "Project", 5).more, false);
  assert.deepEqual(canvasMenuItems([], "Session"), { items: [], more: false });
});

test("every canvas is a command, found by its words, refreshed with the declarations", () => {
  const commands = canvasCommands([item("release-board", "Project", { description: "Steps of a release." }), item("stats", "Application", { plugin: "Statistics" })],
    title => `Open canvas: ${title}`, plugin => `A canvas of ${plugin}.`);
  assert.deepEqual(commands.map(command => [command.key, command.name, command.label, command.description, command.group]), [
    ["canvas:global:tools/release-board", "open_release_board", "Open canvas: Release-board", "Steps of a release.", "Tools"],
    ["canvas:global:tools/stats", "open_stats", "Open canvas: Stats", "A canvas of Statistics.", "Statistics"]]);
  assert.equal(canvasCommandName({ id: "a.b-c" }), "open_a_b_c");
  assert.ok(matchesCanvasCommand(commands[0], ["release"]) && matchesCanvasCommand(commands[0], ["steps", "open"]) && matchesCanvasCommand(commands[1], ["statistics"]));
  assert.ok(!matchesCanvasCommand(commands[0], ["statistics"]));
  assert.ok(matchesCanvasCommand(commands[0], ["canvas"]), "The word that names the page finds every canvas.");
  assert.deepEqual(canvasCommands([], title => title, plugin => plugin), []);
});

test("the prompt of a new canvas names the skill that creates one", () => {
  assert.match(newCanvasPrompt, /codealta-plugin-runtime/u);
  assert.match(newCanvasPrompt, /canvas/u);
});

test("the page the plugin or the agent names holds the tab when it shows the project; the shown space otherwise", () => {
  const spaces = [defaultSpace, space("work", ["p1"]), space("play", ["p2"])];
  assert.equal(canvasRequestSpace(spaces, "play", "work", "p2"), "play", "Another space that has the project.");
  assert.equal(canvasRequestSpace(spaces, "play", "work", "p1"), "work", "A space without the project: the shown one, which has it.");
  assert.equal(canvasRequestSpace(spaces, "play", "work", "p3"), null, "No space has it: nothing is opened.");
  assert.equal(canvasRequestSpace(spaces, null, "play", null), "play", "An application canvas goes where the window is.");
  assert.equal(canvasRequestSpace(spaces, "gone", "work", "p1"), "work", "A space the window does not have is not named.");
  assert.equal(canvasRequestSpace(spaces, "default", "work", "p2"), "default", "The default space has every project.");
});

test("a tab comes to the front only when asked", () => {
  const board = canvasTab({ pluginKey: "k", canvasId: "board" });
  const start = openFileTab(emptyFileTabs(), editorTab(project));
  assert.equal(bringCanvasTab(start, board, true).active, board);
  const quiet = bringCanvasTab(start, board, false);
  assert.deepEqual(quiet.open, [editorTab(project), board]);
  assert.equal(quiet.active, start.active, "The tab in front stays.");
  // Asked again, the same tab is not opened twice, and not brought to the front.
  assert.equal(bringCanvasTab(quiet, { ...board }, false).open.length, 2);
  assert.equal(bringCanvasTab(quiet, { ...board }, true).active?.canvasId, "board");
});

test("a canvas tab joins the tabs of a space that is not shown, in memory or in storage, without a window moving", () => {
  const board = canvasTab({ pluginKey: "k", canvasId: "board" });
  const stored = new Map<string, string>();
  const read = () => stored.get("work") ?? null;
  const write = (value: string) => { stored.set("work", value); };

  // The space was not shown in this run and has nothing stored: it gets the tab, and keeps it for the next time it is shown.
  const first = addCanvasTabToSpace({ kept: undefined, read, write }, board, true);
  assert.deepEqual(first.open, [board]);
  assert.deepEqual(restoreFileTabs(read), { open: [board], active: board, closed: [] });

  // What was stored comes first: the tabs of the space stay, and the new one is not in front when it was not asked to be.
  const other = canvasTab({ pluginKey: "k", canvasId: "second", key: "x" });
  persistFileTabs(write, { open: [changesTab(project)], active: changesTab(project), closed: [] });
  const second = addCanvasTabToSpace({ kept: undefined, read, write }, other, false);
  assert.deepEqual(second.open.map(tab => tab.view), ["changes", "canvas"]);
  assert.equal(second.active?.view, "changes");
  assert.deepEqual(restoreFileTabs(read)?.open.map(tab => tab.view), ["changes", "canvas"]);

  // What the window kept of the space in this run wins over what was stored: it is what the space will show.
  const kept: FileTabs = { open: [editorTab(project)], active: editorTab(project), closed: [] };
  const third = addCanvasTabToSpace({ kept, read, write }, board, true);
  assert.deepEqual(third.open.map(tab => tab.view), ["editor", "canvas"]);
  assert.equal(third.active?.view, "canvas");
  assert.deepEqual(restoreFileTabs(read), third);

  // A stored value that cannot be read is replaced by the tab alone.
  stored.set("work", "{not json");
  assert.deepEqual(addCanvasTabToSpace({ kept: undefined, read, write }, board, false).open, [board]);
});

test("the tab of an instance that a plugin closed leaves the tabs of a space that is not shown, in memory or in storage", () => {
  const board = canvasTab({ pluginKey: "k", canvasId: "board", key: "x" }, { title: "Board", plugin: "plugin:global:k" });
  const notes = canvasTab({ pluginKey: "k", canvasId: "notes", project }, { title: "Notes" });
  const run = canvasTab({ pluginKey: "k", canvasId: "run", project, sessionId: "s1" });
  const stored = new Map<string, string>();
  const read = () => stored.get("work") ?? null;
  const write = (value: string) => { stored.set("work", value); };
  const closed = (canvasId: string, fields: Partial<Parameters<typeof closedCanvasTab>[0]> = {}) => closedCanvasTab({ pluginKey: "k", canvasId, projectId: null, sessionId: null, key: null, ...fields });

  // The host names the instance by its ids: it is the tab the window has, with its title and the folder of its project.
  assert.equal(fileTabKey(closed("board", { key: "x" })), fileTabKey(board));
  assert.equal(fileTabKey(closed("notes", { projectId: "p1" })), fileTabKey(notes));
  assert.equal(fileTabKey(closed("run", { projectId: "p1", sessionId: "s1" })), fileTabKey(run));
  assert.equal(fileTabKey(closed("run", { sessionId: "s1" })), fileTabKey(run), "A session names its tab, whether or not the host knows its project.");
  assert.notEqual(fileTabKey(closed("board")), fileTabKey(board), "Another key is another tab.");

  // The space was not shown in this run: its stored tabs lose the tab, and the others stay with the one that was in front.
  persistFileTabs(write, { open: [changesTab(project), board, notes, run], active: notes, closed: [] });
  const first = removeCanvasTabFromSpace({ kept: undefined, read, write }, closed("board", { key: "x" }));
  assert.deepEqual(first?.open, [changesTab(project), notes, run]);
  assert.deepEqual(restoreFileTabs(read), { open: [changesTab(project), notes, run], active: notes, closed: [] });
  // The tab that was in front of its space leaves no canvas in front.
  assert.equal(removeCanvasTabFromSpace({ kept: undefined, read, write }, closed("notes", { projectId: "p1" }))?.active, null);
  assert.deepEqual(restoreFileTabs(read), { open: [changesTab(project), run], active: null, closed: [] });

  // What the window kept of the space in this run is what the space will show: it loses the tab, and can open it again as a tab the user closed.
  const kept: FileTabs = { open: [editorTab(project), run], active: run, closed: [] };
  const second = removeCanvasTabFromSpace({ kept, read, write }, closed("run", { sessionId: "s1" }));
  assert.deepEqual(second, { open: [editorTab(project)], active: null, closed: [run] });
  assert.deepEqual(restoreFileTabs(read)?.open, [editorTab(project)]);

  // A tab that is not there changes nothing, and nothing is written for a space that has no tabs.
  const before = stored.get("work");
  assert.equal(removeCanvasTabFromSpace({ kept: second!, read, write }, closed("board", { key: "x" })), second);
  assert.equal(stored.get("work"), before);
  stored.clear();
  assert.equal(removeCanvasTabFromSpace({ kept: undefined, read, write }, closed("board", { key: "x" })), null);
  assert.equal(stored.size, 0);
});

test("a tab that a plugin asked for and closed in a space that is not shown is there or not by what came last", () => {
  const board = canvasTab({ pluginKey: "k", canvasId: "board" });
  const stored = new Map<string, string>();
  const source = { kept: undefined, read: () => stored.get("work") ?? null, write: (value: string) => { stored.set("work", value); } };
  const closed = closedCanvasTab({ pluginKey: "k", canvasId: "board", projectId: null, sessionId: null, key: null });

  addCanvasTabToSpace(source, board, true);
  removeCanvasTabFromSpace(source, closed);
  assert.deepEqual(restoreFileTabs(source.read)?.open, [], "asked for, then closed");
  addCanvasTabToSpace(source, board, true);
  assert.deepEqual(restoreFileTabs(source.read)?.open, [board], "closed, then asked for again");
});

test("an instance opened for a tab that went away is closed with a tab that was closed, and hidden with one that is still in its space", () => {
  const board = canvasTab({ pluginKey: "k", canvasId: "board" }, { title: "Board" });
  const run = canvasTab({ pluginKey: "k", canvasId: "run", project, sessionId: "s1" });
  // The tab left the page with its space, or asks again: it is still one of the tabs of the space, whatever its title became.
  assert.equal(abandonedCanvas([editorTab(project), canvasTab({ pluginKey: "k", canvasId: "board" }, { title: "Renamed" })], board), "hide");
  // The tab was closed while the host opened its instance: nothing shows the instance, and nothing else would close it.
  assert.equal(abandonedCanvas([editorTab(project), run], board), "close");
  assert.equal(abandonedCanvas([], run), "close");
});

test("the Canvases page is one tab, stored and restored with the others, and it lasts", () => {
  assert.deepEqual(canvasesTab, { projectId: "", projectPath: "", view: "canvases" });
  assert.ok(isCanvasesTab(canvasesTab) && !isCanvasesTab(canvasTab({ pluginKey: "k", canvasId: "board" })));
  const state = openFileTab(openFileTab(emptyFileTabs(), editorTab(project)), canvasesTab);
  assert.equal(openFileTab(state, { ...canvasesTab }).open.length, 2, "Asked again, the one that is open is shown.");
  let written = "";
  persistFileTabs(value => { written = value; }, state);
  assert.deepEqual(restoreFileTabs(() => written), state);
  // It stays when a project goes, as the application's own tabs do.
  const catalog = { projects: [], sessions: [] } as unknown as Parameters<typeof reconcileFileTabs>[1];
  assert.deepEqual(reconcileFileTabs(state, catalog).open, [canvasesTab]);
  // A page of this kind that names a project is not understood.
  assert.equal(restoreFileTabs(() => JSON.stringify({ version: 1, open: [{ projectId: "p1", projectPath: "/x", view: "canvases" }], active: null }))?.open.length, 0);
});
