import assert from "node:assert/strict";
import test from "node:test";
import { canvasTab, editorTab, emptyFileTabs, fileTabLimit, openFileTab, restoreFileTabs, type FileTab } from "../fileTabs";
import { createCanvasInstances } from "./canvasInstances";
import { addCanvasTabToSpace } from "./canvasPages";

const project = { id: "p1", path: "/code/one" };
const board = canvasTab({ pluginKey: "k", canvasId: "board" }, { title: "Board" });
const notes = canvasTab({ pluginKey: "k", canvasId: "notes", project });

/** The owner of the tabs as the window is: the tabs each space has open, and what the host was asked. */
function owner(spaces: Record<string, readonly FileTab[]>) {
  const calls: string[] = [];
  const instances = createCanvasInstances({
    close: async instanceId => { calls.push(`close:${instanceId}`); },
    setVisible: async (instanceId, visible) => { calls.push(`visible:${instanceId}:${visible}`); return "ok"; },
  }, space => spaces[space] ?? []);
  /** The tab is taken out of the tabs of its space, as closing it does. */
  const remove = (space: string, tab: FileTab) => { spaces[space] = (spaces[space] ?? []).filter(value => value !== tab); };
  return { instances, calls, remove };
}

test("a tab that is closed closes the instance it shows, once", () => {
  const { instances, calls, remove } = owner({ work: [editorTab(project), board] });
  instances.opened(board, "work", "i1");

  // The tab is closed: the owner closes its instance, then the tab leaves the page.
  instances.closed(board, "work");
  remove("work", board);
  instances.released(board, "work");

  assert.deepEqual(calls, ["close:i1"]);
  instances.closed(board, "work");
  assert.deepEqual(calls, ["close:i1"], "a tab that has no instance left closes nothing");
});

test("a tab closed while the host opens its instance closes the instance, whenever the answer comes", () => {
  // The answer comes after the tab left the page: the tab gives the instance to its owner.
  const late = owner({ work: [board] });
  late.instances.closed(board, "work");
  late.remove("work", board);
  late.instances.abandoned(board, "work", "i1");
  assert.deepEqual(late.calls, ["close:i1"]);

  // The answer comes between the close and the moment the tab leaves the page: the tab took the instance as its own, and no one had
  // closed it. It is closed when the tab leaves.
  const between = owner({ work: [board] });
  between.instances.closed(board, "work");
  assert.deepEqual(between.calls, [], "the tab has no instance yet");
  between.remove("work", board);
  between.instances.opened(board, "work", "i1");
  between.instances.released(board, "work");
  assert.deepEqual(between.calls, ["close:i1"], "an instance that no tab shows is not left open");
  between.instances.released(board, "work");
  assert.deepEqual(between.calls, ["close:i1"], "once");
});

test("a tab that leaves the page with its space, or asks for its instance again, keeps the instance", () => {
  const { instances, calls, remove } = owner({ work: [board, notes], play: [board] });
  instances.opened(board, "work", "w1");
  instances.opened(notes, "work", "w2");
  instances.opened(board, "play", "p1");

  // Another space is shown, or the tab asks again: the tabs are still those of their space.
  instances.released(board, "work");
  instances.released(notes, "work");
  assert.deepEqual(calls, []);
  // The host opened an instance for a tab that left with its space meanwhile: it is hidden, not closed.
  instances.abandoned(notes, "work", "w2");
  assert.deepEqual(calls, ["visible:w2:false"]);

  // The same canvas is a tab of two spaces: closing one closes its own instance.
  instances.closed(board, "work");
  remove("work", board);
  instances.released(board, "work");
  assert.deepEqual(calls, ["visible:w2:false", "close:w1"]);
  instances.released(board, "play");
  assert.deepEqual(calls, ["visible:w2:false", "close:w1"], "the tab of the other space keeps its instance");
  // A space that is gone has no tabs: what its tabs showed is closed with them.
  instances.released(notes, "gone");
  instances.opened(notes, "gone", "g1");
  instances.released(notes, "gone");
  assert.deepEqual(calls.at(-1), "close:g1");
});

test("opening the 33rd canvas in a hidden space closes only its evicted owner, without moving the shown space", () => {
  const tabs = Array.from({ length: fileTabLimit }, (_, index) => canvasTab({ pluginKey: "k", canvasId: "board", key: String(index) }));
  const hidden = tabs.reduce((state, tab) => openFileTab(state, tab), emptyFileTabs());
  const shown = openFileTab(openFileTab(emptyFileTabs(), tabs[0]), editorTab(project));
  const spaces = { work: hidden.open, play: shown.open };
  const { instances, calls } = owner(spaces);
  tabs.forEach((tab, index) => instances.opened(tab, "work", `w${index}`));
  instances.opened(tabs[0], "play", "p0");

  // Switch from work to play: all work panels have already released, but their owners remain open.
  tabs.forEach(tab => instances.released(tab, "work"));
  assert.deepEqual(calls, []);
  let stored: string | null = null;
  const removed: FileTab[] = [];
  const source = { kept: hidden, read: () => { assert.fail("the hidden tabs are kept in memory"); }, write: (value: string) => { stored = value; },
    closed: (tab: FileTab) => { removed.push(tab); instances.closed(tab, "work"); } };
  const requested = canvasTab({ pluginKey: "k", canvasId: "board", key: "32" });
  const next = addCanvasTabToSpace(source, requested, true);
  spaces.work = next.open;

  assert.deepEqual(calls, ["close:w0"], "the evicted panel cannot release again; the tab owner must close it now");
  assert.deepEqual(removed, [tabs[0]], "other keys of the same canvas are retained");
  assert.equal(next.open.length, fileTabLimit);
  assert.deepEqual(next.open, [...tabs.slice(1), requested]);
  assert.equal(next.active, requested, "focus belongs to the hidden space only");
  assert.deepEqual(restoreFileTabs(() => stored), next);
  assert.equal(spaces.play, shown.open);
  assert.equal(shown.active?.view, "editor", "the shown space and its focused pane stay unchanged");

  // A duplicate close or release of the removed tab does not close another instance or another space.
  instances.closed(tabs[0], "work");
  tabs.forEach(tab => instances.released(tab, "work"));
  instances.released(tabs[0], "play");
  assert.deepEqual(calls, ["close:w0"]);
  instances.closed(tabs[1], "work");
  instances.closed(tabs[0], "play");
  assert.deepEqual(calls, ["close:w0", "close:w1", "close:p0"], "retained keys and the other space still own their instances");
});

test("hidden-space capacity closes late evicted opens, but not retained opens, editors or closed history", () => {
  const tabs = Array.from({ length: fileTabLimit }, (_, index) => canvasTab({ pluginKey: "k", canvasId: "board", key: String(index) }));
  const hidden = { ...tabs.reduce((state, tab) => openFileTab(state, tab), emptyFileTabs()), closed: [notes] };
  const spaces = { work: hidden.open };
  const { instances, calls } = owner(spaces);
  const removed: FileTab[] = [];
  const source = { kept: hidden, read: () => null, write: (_value: string) => {},
    closed: (tab: FileTab) => { removed.push(tab); instances.closed(tab, "work"); } };
  const next = addCanvasTabToSpace(source, board, false);
  spaces.work = next.open;
  assert.deepEqual(removed, [tabs[0]]);
  assert.deepEqual(next.closed, [notes], "closed history is not an open owner to release");
  assert.equal(next.active, hidden.active, "a non-focusing request preserves the hidden active tab");
  instances.abandoned(tabs[0], "work", "late-evicted");
  instances.abandoned(tabs[1], "work", "late-retained");
  assert.deepEqual(calls, ["close:late-evicted", "visible:late-retained:false"]);

  removed.length = 0;
  const withEditor = { ...hidden, open: [editorTab(project), ...tabs.slice(1)] };
  const editorEvicted = addCanvasTabToSpace({ ...source, kept: withEditor }, board, false);
  assert.deepEqual(removed, [], "evicting an editor does not notify canvas owners");
  assert.equal(editorEvicted.open.length, fileTabLimit);
  addCanvasTabToSpace({ ...source, kept: next }, { ...board, name: "Renamed board" }, true);
  assert.deepEqual(removed, [], "finding an existing identity closes nothing");
});
