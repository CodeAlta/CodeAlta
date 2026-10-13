import assert from "node:assert/strict";
import test from "node:test";
import { emptySessionTabs, openDraftTab, openSessionTab, type SessionTabs } from "./sessionTabs";
import { changesTab, editorTab, fileNodeId, openFileTab, emptyFileTabs } from "./fileTabs";
import { draftNodeId, sessionDraftNodeId, sessionNodeId } from "./sessionTabLayout";
import { closeSessionTabs, dockTab, dockTabs, shownAfterClosing, tabsToClose } from "./sessionTabClosing";

const tab = (id: string, projectId: string | null = "p") => ({ sessionId: id, projectId, path: projectId === null ? null : "/p" });
const prompt = (projectId: string | null) => ({ projectId, path: projectId === null ? null : `/${projectId}` });
const project = { id: "p", path: "/p" };

test("a closing command of a tab names the other tabs of its pane, the ones after it or all of them, never the transient prompt", () => {
  const pane = ["a", "b", sessionDraftNodeId, "c", "d"];
  assert.deepEqual(tabsToClose(pane, "b", "others"), ["a", "c", "d"]);
  assert.deepEqual(tabsToClose(pane, "b", "right"), ["c", "d"]);
  assert.deepEqual(tabsToClose(pane, "d", "right"), []);
  assert.deepEqual(tabsToClose(pane, "b", "all"), ["a", "b", "c", "d"]);
  assert.deepEqual(tabsToClose(pane, null, "all"), ["a", "b", "c", "d"]);
  // The transient prompt can be the tab the command is asked from: the others close, it stays.
  assert.deepEqual(tabsToClose(pane, sessionDraftNodeId, "others"), ["a", "b", "c", "d"]);
  assert.deepEqual(tabsToClose(pane, sessionDraftNodeId, "right"), ["c", "d"]);
  // A tab that is not in the pane closes nothing but what "all" names.
  assert.deepEqual(tabsToClose(pane, "elsewhere", "others"), []);
  assert.deepEqual(tabsToClose(pane, "elsewhere", "right"), []);
  assert.deepEqual(tabsToClose([sessionDraftNodeId], sessionDraftNodeId, "all"), []);
});

test("the nodes of the dock are told apart: sessions, prompts with a tab of their own, tabs of projects", () => {
  const state = openDraftTab(openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two")), prompt("p"));
  const files = openFileTab(openFileTab(emptyFileTabs(), editorTab(project)), changesTab(project));
  assert.deepEqual(dockTab(sessionNodeId(tab("two")), state, files), { session: tab("two") });
  assert.deepEqual(dockTab(draftNodeId(prompt("p")), state, files), { draft: prompt("p") });
  assert.deepEqual(dockTab(fileNodeId(changesTab(project)), state, files), { file: changesTab(project) });
  assert.equal(dockTab(sessionDraftNodeId, state, files), null);
  assert.equal(dockTab(sessionNodeId(tab("gone")), state, files), null);
  assert.deepEqual(dockTabs([fileNodeId(editorTab(project)), sessionNodeId(tab("one")), "unknown", draftNodeId(prompt("p")), sessionNodeId(tab("two"))], state, files),
    { sessions: [tab("one"), tab("two")], drafts: [prompt("p")], files: [editorTab(project)] });
});

test("several tabs close in one change of the state: the tab the command was asked from is shown when the one in front closes", () => {
  let state: SessionTabs = emptySessionTabs();
  for (const id of ["one", "two", "three", "four"]) state = openSessionTab(state, tab(id));
  state = openDraftTab(openDraftTab(state, prompt("p")), prompt(null));
  assert.deepEqual(state.active, tab("four"));

  // The others of "three" close, the one in front among them: "three" is shown, and each closed tab can be reopened.
  const others = closeSessionTabs(state, [tab("one"), tab("two"), tab("four")], [prompt(null)], tab("three"));
  assert.deepEqual(others.open, [tab("three")]);
  assert.deepEqual(others.active, tab("three"));
  assert.deepEqual(others.closed, [tab("one"), tab("two"), tab("four")]);
  assert.deepEqual(others.drafts, [prompt("p")]);

  // The session in front stays in front when it is not closed and no other is asked for.
  const kept = closeSessionTabs(state, [tab("one"), tab("two")]);
  assert.deepEqual(kept.open, [tab("three"), tab("four")]);
  assert.equal(kept.active, state.active);
  // One that is asked for is shown, as when a tab of a project was in front of the sessions and closed.
  assert.deepEqual(closeSessionTabs(state, [tab("one"), tab("two")], [], tab("three")).active, tab("three"));
  assert.equal(closeSessionTabs(state, [tab("one")], [], tab("four")).active, state.active);

  // Without a tab to show, the first session that stays is; with none left, no session is shown.
  assert.deepEqual(closeSessionTabs(state, [tab("four"), tab("three")]).active, tab("one"));
  const all = closeSessionTabs(state, state.open, state.drafts);
  assert.deepEqual([all.open, all.active, all.drafts], [[], null, []]);
  // A tab that closes with the others is not the one shown.
  assert.deepEqual(closeSessionTabs(state, [tab("four"), tab("three")], [], tab("three")).active, tab("one"));

  // The transient tab that closes is no longer the transient one.
  const looked = openSessionTab(state, tab("five"), "preview");
  assert.deepEqual(looked.preview, tab("five"));
  assert.equal(closeSessionTabs(looked, [tab("five")], [], tab("one")).preview, null);
  // Nothing to close is the same state.
  assert.equal(closeSessionTabs(state, [tab("gone")], [prompt("other")], tab("four")), state);
  assert.equal(closeSessionTabs(state, []), state);
});

test("the tab a closing command was asked from comes in front only when what was in front closes", () => {
  const editor = editorTab(project), changes = changesTab(project);
  const closing = { sessions: [tab("one")], drafts: [prompt("p")], files: [editor] };
  const from = { session: tab("two") };
  // A tab of a project is in front of the session behind it: only its own closing matters.
  assert.equal(shownAfterClosing(closing, { file: changes, session: tab("one"), scope: prompt("p") }, from), null);
  assert.equal(shownAfterClosing(closing, { file: editor, session: tab("three"), scope: null }, from), from);
  // Then the session shown, then the prompt of the selected scope.
  assert.equal(shownAfterClosing(closing, { file: null, session: tab("one"), scope: prompt("p") }, from), from);
  assert.equal(shownAfterClosing(closing, { file: null, session: tab("three"), scope: prompt("p") }, from), null);
  assert.equal(shownAfterClosing(closing, { file: null, session: null, scope: prompt("p") }, from), from);
  assert.equal(shownAfterClosing(closing, { file: null, session: null, scope: prompt(null) }, from), null);
  // A command that names no tab (all the tabs of a pane) leaves the choice to the window.
  assert.equal(shownAfterClosing(closing, { file: null, session: tab("one"), scope: null }, null), null);
});
