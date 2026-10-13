import assert from "node:assert/strict";
import test from "node:test";
import { Actions, DockLocation, RowNode, TabNode, TabSetNode } from "flexlayout-react";
import { sessionTabDrop } from "./sessionTabDrag";
import type { WorkspaceSnapshot } from "#neoastra";
import { closeDraftTab, closePreviewTab, closeSessionTab, emptySessionTabs, openDraftTab, openSessionTab, pinSessionTab, sessionTabLimit, type DraftTab, type SessionTab, type SessionTabs } from "./sessionTabs";
import { createSessionTabModel, draftNodeId, draftTabAction, fileTabAction, ownsSessionTabContent, reconcileSessionTabModel, sessionDraftNodeId, sessionLayoutActionAllowed, sessionNodeId, sessionTabAction, sessionTabPresentation, shownDraftNodeId, tabMiddleClickAction } from "./sessionTabLayout";
import { activateFileTab, canvasTab, changesTab, closeFileTab, documentationTab, editorTab, emptyFileTabs, fileNodeId, isChangesTab, openFileTab, terminalTab, type FileTab } from "./fileTabs";

const tab = (id: string) => ({ projectId: "p", sessionId: id, path: "/p" });
const snapshot: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  projects: [{ id: "p", name: "Project", path: "/p", archived: false }], sessions: ["one", "two"].map(id => ({
    id, title: id, fullTitle: id, fullTitleTruncated: false, workspacePath: "/p", scopeKind: "project", projectId: "p",
    messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, createdAt: null, updatedAt: "2026-09-27T00:00:00Z", parentSessionId: null, lineageIssue: null, providerKey: null,
  })) };
const label = (value: SessionTab | null) => value?.sessionId ?? "Prompt draft";
const both = () => openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"));

test("pointer drop geometry splits in all four directions, merges and reorders through public actions", () => {
  for (const [x, y, location] of [[10, 200, DockLocation.LEFT], [590, 200, DockLocation.RIGHT],
    [300, 10, DockLocation.TOP], [300, 390, DockLocation.BOTTOM], [300, 200, DockLocation.CENTER]] as const) {
    const model = createSessionTabModel(), state = both();
    reconcileSessionTabModel(model, state, label);
    const source = model.getNodeById(sessionNodeId(tab("two"))) as TabNode;
    const target = source.getParent() as TabSetNode;
    const drop = sessionTabDrop(source, target, { x: 0, y: 0, width: 600, height: 400 }, { x, y });
    assert.equal(drop?.location, location);
    assert.ok(drop && sessionLayoutActionAllowed(model, drop.action, state, snapshot, () => true));
    model.doAction(drop!.action);
    assert.equal(model.getNodeById(source.getId()), source);
    assert.equal(model.getActiveTabset()?.getSelectedNode(), source);
    assert.equal(source.getParent() === target, location === DockLocation.CENTER);
  }
  const model = createSessionTabModel(), state = both();
  reconcileSessionTabModel(model, state, label);
  const source = model.getNodeById(sessionNodeId(tab("two"))) as TabNode;
  const target = source.getParent() as TabSetNode;
  const rect = { x: 0, y: 0, width: 600, height: 400 };
  const reorder = sessionTabDrop(source, target, rect, { x: 10, y: 10 }, { index: 0, rect: { x: 0, y: 0, width: 3, height: 30 } });
  assert.equal(reorder?.location, DockLocation.CENTER);
  model.doAction(reorder!.action);
  assert.equal(target.getTabNodes()[0], source);
  model.doAction(Actions.moveNode(source.getId(), target.getId(), DockLocation.RIGHT, -1, true));
  const merge = sessionTabDrop(source, target, rect, { x: 300, y: 200 });
  model.doAction(merge!.action);
  assert.equal(source.getParent(), target);
  assert.equal(model.getRootRow()!.getChildren().length, 1);
});

test("pointer drops refuse outside/zero geometry, temporary tabs, lone self splits and disabled targets", () => {
  const model = createSessionTabModel();
  reconcileSessionTabModel(model, { ...both(), active: null }, label);
  const source = model.getNodeById(sessionNodeId(tab("one"))) as TabNode;
  const target = source.getParent() as TabSetNode;
  const draft = model.getNodeById(sessionDraftNodeId) as TabNode;
  const rect = { x: 0, y: 0, width: 600, height: 400 };
  assert.equal(sessionTabDrop(draft, target, rect, { x: 590, y: 200 }), null);
  for (const point of [{ x: -1, y: 200 }, { x: 601, y: 200 }, { x: 300, y: 401 }])
    assert.equal(sessionTabDrop(source, target, rect, point), null);
  assert.equal(sessionTabDrop(source, target, { ...rect, width: 0 }, { x: 0, y: 0 }), null);
  model.doAction(Actions.updateNodeAttributes(target.getId(), { enableDivide: false }));
  assert.equal(sessionTabDrop(source, target, rect, { x: 590, y: 200 }), null);
  model.doAction(Actions.updateNodeAttributes(target.getId(), { enableDrop: false }));
  assert.equal(sessionTabDrop(source, target, rect, { x: 300, y: 200 }), null);
  const lone = createSessionTabModel();
  reconcileSessionTabModel(lone, openSessionTab(emptySessionTabs(), tab("one")), label);
  const only = lone.getNodeById(sessionNodeId(tab("one"))) as TabNode;
  assert.equal(sessionTabDrop(only, only.getParent() as TabSetNode, rect, { x: 590, y: 200 }), null);
});

test("projects reuse one temporary new-session tab without replacing real panes or split geometry", () => {
  const other = { projectId: "q", sessionId: "other", path: "/q" };
  const catalog = { ...snapshot, projects: [...snapshot.projects, { id: "q", name: "Other project", path: "/q", archived: false }],
    sessions: [...snapshot.sessions, { ...snapshot.sessions[0], id: "other", projectId: "q", workspacePath: "/q" }] };
  const model = createSessionTabModel();
  let state = openSessionTab(both(), other);
  reconcileSessionTabModel(model, state, label);
  const one = model.getNodeById(sessionNodeId(tab("one"))) as TabNode;
  const otherNode = model.getNodeById(sessionNodeId(other)) as TabNode;
  model.doAction(Actions.moveNode(otherNode.getId(), one.getParent()!.getId(), DockLocation.RIGHT, -1, true));
  const parents = [one.getParent(), otherNode.getParent()];
  let draft: TabNode | undefined;
  for (const projectId of ["p", "q", null, "p"]) {
    state = sessionTabPresentation(state, catalog, projectId, null);
    reconcileSessionTabModel(model, state, label);
    assert.equal(state.open.length, 3);
    assert.equal(state.active, null);
    const current = model.getNodeById(sessionDraftNodeId) as TabNode;
    assert.ok(current);
    draft ??= current;
    assert.equal(current, draft);
    assert.equal(model.getActiveTabset()?.getSelectedNode(), current);
    assert.equal(current.isEnableDrag(), false);
    assert.equal(model.getNodeById(one.getId()), one);
    assert.equal(model.getNodeById(otherNode.getId()), otherNode);
    assert.deepEqual([one.getParent(), otherNode.getParent()], parents);
  }
  const returned = sessionTabPresentation(state, catalog, "p", "one");
  assert.equal(returned.open.length, 3);
  reconcileSessionTabModel(model, returned, label);
  assert.equal(model.getNodeById(sessionDraftNodeId), undefined);
  assert.equal(model.getActiveTabset()?.getSelectedNode(), one);
  assert.deepEqual([one.getParent(), otherNode.getParent()], parents);
  assert.equal(returned.closed.some(tab => tab.sessionId === sessionDraftNodeId), false);
});

test("empty workspaces have a real new-session tab; starting a session removes it", () => {
  const model = createSessionTabModel();
  reconcileSessionTabModel(model, emptySessionTabs(), label);
  const draft = model.getNodeById(sessionDraftNodeId) as TabNode;
  assert.equal(draft.isSelected(), true);
  assert.equal(draft.isEnableClose(), false);
  reconcileSessionTabModel(model, openSessionTab(emptySessionTabs(), tab("one")), label);
  assert.equal(model.getNodeById(sessionDraftNodeId), undefined);
  assert.equal(model.getActiveTabset()?.getSelectedNode()?.getId(), sessionNodeId(tab("one")));
});

test("GUI moves/selects/weights are admitted to FlexLayout before App focus, with lifetime and scope guards", () => {
  const model = createSessionTabModel();
  const state = both();
  reconcileSessionTabModel(model, state, label);
  const one = model.getNodeById(sessionNodeId(tab("one"))) as TabNode;
  const two = model.getNodeById(sessionNodeId(tab("two"))) as TabNode;
  const move = Actions.moveNode(one.getId(), two.getParent()!.getId(), DockLocation.RIGHT, -1, true);
  assert.equal(sessionLayoutActionAllowed(model, move, state, snapshot, () => true), true);
  assert.equal(sessionLayoutActionAllowed(model, move, state, snapshot, () => false), false);
  assert.equal(sessionLayoutActionAllowed(model, move, state, { ...snapshot, sessions: [] }, () => true), false);
  model.doAction(move);
  assert.notEqual(one.getParent(), two.getParent());
  assert.equal(model.getActiveTabset()?.getSelectedNode(), one);
  const activate = Actions.setActiveTabset(two.getParent()!.getId());
  assert.equal(sessionLayoutActionAllowed(model, activate, state, snapshot, () => true), true);
  model.doAction(activate);
  assert.equal(model.getActiveTabset()?.getSelectedNode(), two);
  const select = Actions.selectTab(one.getId());
  assert.equal(sessionLayoutActionAllowed(model, select, state, snapshot, () => true), true);
  assert.equal(sessionLayoutActionAllowed(model,
    Actions.moveNode(two.getParent()!.getId(), one.getParent()!.getId(), DockLocation.BOTTOM, -1), state, snapshot, () => true), true);
  assert.equal(sessionLayoutActionAllowed(model, Actions.adjustWeights("session-tabs-row", [50, 50]), state, snapshot, () => true), true);
  for (const action of [Actions.moveNode("notes", two.getParent()!.getId(), DockLocation.RIGHT, -1),
    Actions.moveNode(one.getId(), "missing", DockLocation.RIGHT, -1), Actions.deleteTab(one.getId()),
    Actions.renameTab(one.getId(), "Renamed"), Actions.setActiveTabset("missing")])
    assert.equal(sessionLayoutActionAllowed(model, action, state, snapshot, () => true), false);
});

test("three simultaneously selected split panes survive reconciliation and closing one", () => {
  const model = createSessionTabModel();
  let state = openSessionTab(both(), tab("three"));
  reconcileSessionTabModel(model, state, label);
  const one = model.getNodeById(sessionNodeId(tab("one"))) as TabNode;
  const two = model.getNodeById(sessionNodeId(tab("two"))) as TabNode;
  const three = model.getNodeById(sessionNodeId(tab("three"))) as TabNode;
  model.doAction(Actions.moveNode(two.getId(), one.getParent()!.getId(), DockLocation.RIGHT, -1, true));
  model.doAction(Actions.moveNode(three.getId(), two.getParent()!.getId(), DockLocation.BOTTOM, -1, true));
  const parents = [one, two, three].map(node => node.getParent());
  assert.equal(new Set(parents).size, 3);
  reconcileSessionTabModel(model, state, label);
  assert.deepEqual([one, two, three].map(node => node.getParent()), parents);
  assert.ok([one, two, three].every(node => node.isSelected()));
  state = closeSessionTab(state, tab("two"));
  reconcileSessionTabModel(model, state, label);
  assert.equal(model.getNodeById(two.getId()), undefined);
  assert.equal(model.getNodeById(one.getId()), one);
  assert.equal(model.getNodeById(three.getId()), three);
});

test("public model has only draggable session identities, retaining nodes across reconciliation and label/geometry updates", () => {
  const model = createSessionTabModel();
  const state = both();
  reconcileSessionTabModel(model, state, label);
  const one = model.getNodeById(sessionNodeId(tab("one"))) as TabNode;
  const two = model.getNodeById(sessionNodeId(tab("two"))) as TabNode;
  assert.equal(two.isSelected(), true);
  assert.equal(one.isSelected(), false);
  assert.equal(one.isEnableRenderOnDemand(), false);
  assert.equal(one.isEnableDrag(), true);
  assert.equal(model.getNodeById("session-draft"), undefined);
  reconcileSessionTabModel(model, state, value => `${label(value)} localized`);
  model.doAction(Actions.adjustWeights("session-tabs-row", [100]));
  assert.equal(model.getNodeById(one.getId()), one);
  assert.equal(model.getNodeById(two.getId()), two);
  assert.equal(two.getName(), "two localized");
  assert.equal(two.isSelected(), true);
  reconcileSessionTabModel(model, { ...state, active: null }, label);
  assert.ok(model.getNodeById(sessionDraftNodeId));
  assert.equal(model.getNodeById(two.getId()), two);
});

test("select/delete map to App intents without changing model, invalid actions and scope refuse", () => {
  const state = both();
  const model = createSessionTabModel();
  reconcileSessionTabModel(model, state, label);
  const before = model.toJson();
  for (const [action, kind] of [[Actions.selectTab(sessionNodeId(tab("one"))), "select"],
    [Actions.deleteTab(sessionNodeId(tab("one"))), "close"]] as const) {
    assert.deepEqual(sessionTabAction(action, state, snapshot, () => true), { kind, tab: tab("one") });
    assert.equal(sessionTabAction(action, state, snapshot, () => false), null);
    for (const catalog of [undefined, { ...snapshot, sessions: [] },
      { ...snapshot, sessions: [...snapshot.sessions, snapshot.sessions[0]] },
      { ...snapshot, projects: [...snapshot.projects, snapshot.projects[0]] },
      { ...snapshot, projects: [{ ...snapshot.projects[0], path: "/elsewhere" }] }])
      assert.equal(sessionTabAction(action, state, catalog, () => true), null);
  }
  assert.equal(sessionTabAction(Actions.selectTab("session-draft"), state, snapshot, () => true), null);
  for (const action of [Actions.deleteTab("session-draft"), Actions.selectTab("unknown"), Actions.deleteTabset("session-tabs-panel"),
    Actions.renameTab(sessionNodeId(tab("one")), "renamed")])
    assert.equal(sessionTabAction(action, state, snapshot, () => true), null);
  assert.deepEqual(model.toJson(), before);
});

test("factory ownership is exactly one active identity; App selection leads effects without re-binding path ABA", () => {
  let state = both();
  const ids = ["session-draft", ...state.open.map(sessionNodeId)];
  assert.deepEqual(ids.filter(id => ownsSessionTabContent(id, state)), [sessionNodeId(tab("two"))]);
  state = sessionTabPresentation(state, snapshot, "p", "one");
  assert.deepEqual(ids.filter(id => ownsSessionTabContent(id, state)), [sessionNodeId(tab("one"))]);
  state = sessionTabPresentation(state, snapshot, "p", null);
  assert.deepEqual(ids.filter(id => ownsSessionTabContent(id, state)), []);
  const changed = { ...snapshot, sessions: snapshot.sessions.map(row => ({ ...row, workspacePath: "/changed" })),
    projects: [{ ...snapshot.projects[0], path: "/changed" }] };
  assert.equal(sessionTabPresentation(both(), changed, "p", "two").active, null);
});

test("close/reopen and 32 identity cap retain inactive owner metadata without a draft tab", () => {
  const model = createSessionTabModel();
  let state = both();
  reconcileSessionTabModel(model, state, label);
  const active = model.getNodeById(sessionNodeId(tab("two")));
  state = closeSessionTab(state, tab("one"));
  reconcileSessionTabModel(model, state, label);
  assert.equal(model.getNodeById(sessionNodeId(tab("one"))), undefined);
  assert.equal(model.getNodeById(sessionNodeId(tab("two"))), active);
  state = openSessionTab(state, state.closed.at(-1)!);
  for (let i = 0; i < 50; i++) state = openSessionTab(state, tab(String(i)));
  reconcileSessionTabModel(model, state, label);
  assert.equal(model.getActiveTabset()!.getChildren().length, sessionTabLimit);
  assert.equal(model.getActiveTabset()!.getChildren().filter(node => ownsSessionTabContent(node.getId(), state)).length, 1);
});

test("captured menu lifetimes reject late and ABA actions even when the same exact identity is current again", () => {
  let generation = 1;
  const captured = generation;
  const current = () => generation === captured;
  const action = Actions.deleteTab(sessionNodeId(tab("one")));
  assert.ok(sessionTabAction(action, both(), snapshot, current));
  generation++;
  assert.equal(sessionTabAction(action, both(), snapshot, current), null);
  generation++;
  assert.equal(sessionTabAction(action, both(), snapshot, current), null);
});

test("unchanged App identity makes repeated presentation reconciliation inert; switching releases old payload eligibility", () => {
  const model = createSessionTabModel();
  const state = both();
  reconcileSessionTabModel(model, state, label);
  const activeId = sessionNodeId(tab("two"));
  const activeNode = model.getNodeById(activeId);
  const actions: string[] = [];
  const listener = (action: { type: string }) => { actions.push(action.type); };
  model.addChangeListener(listener);
  // Geometry/overlay parent renders do not alter the projected App identity.
  for (let i = 0; i < 10; i++) {
    const projection = sessionTabPresentation(state, snapshot, "p", "two");
    reconcileSessionTabModel(model, projection, label);
    assert.equal(model.getNodeById(activeId), activeNode);
    assert.equal(ownsSessionTabContent(activeId, projection), true);
  }
  assert.deepEqual(actions, []);
  const switched = sessionTabPresentation(state, snapshot, "p", "one");
  reconcileSessionTabModel(model, switched, label);
  assert.deepEqual(actions, [Actions.SELECT_TAB]);
  assert.equal(model.getNodeById(activeId), activeNode);
  assert.equal(ownsSessionTabContent(activeId, switched), false);
  assert.equal(ownsSessionTabContent(sessionNodeId(tab("one")), switched), true);
  const closed = closeSessionTab(switched, tab("one"));
  reconcileSessionTabModel(model, closed, label);
  assert.equal(model.getNodeById(sessionNodeId(tab("one"))), undefined);
  assert.equal(ownsSessionTabContent(sessionNodeId(tab("one")), closed), false);
  model.removeChangeListener(listener);
});

// The code editor of a project, named here like the file that used to be a tab of its own.
const file = (project: string): FileTab => editorTab({ id: project, path: `/${project}` });

test("editor tabs join the session strip: one node per project, selected while active, removed when closed", () => {
  const model = createSessionTabModel(), state = both();
  let files = openFileTab(emptyFileTabs(), file("src/a.ts"));
  reconcileSessionTabModel(model, state, label, files);
  const two = model.getNodeById(sessionNodeId(tab("two"))) as TabNode;
  const a = model.getNodeById(fileNodeId(file("src/a.ts"))) as TabNode;
  assert.equal(a.getName(), "editor");
  assert.equal(a.getComponent(), "editor");
  assert.equal(a.isEnableDrag(), true);
  assert.equal(model.getActiveTabset()?.getSelectedNode(), a);
  assert.equal(model.getNodeById(sessionDraftNodeId), undefined);
  // Opening the same editor again does not add a tab; another project's does and keeps the first node.
  files = openFileTab(openFileTab(files, file("src/a.ts")), file("b.md"));
  reconcileSessionTabModel(model, state, label, files);
  assert.equal(model.getNodeById(a.getId()), a);
  const b = model.getNodeById(fileNodeId(file("b.md"))) as TabNode;
  assert.equal(model.getActiveTabset()?.getSelectedNode(), b);
  assert.equal((a.getParent() as TabSetNode).getTabNodes().length, 4);
  // Leaving the file shows the session selection again; the file tabs stay open.
  reconcileSessionTabModel(model, state, label, activateFileTab(files, null));
  assert.equal(model.getActiveTabset()?.getSelectedNode(), two);
  assert.equal(model.getNodeById(b.getId()), b);
  files = closeFileTab(files, file("b.md"));
  reconcileSessionTabModel(model, state, label, files);
  assert.equal(model.getNodeById(b.getId()), undefined);
  assert.equal(model.getNodeById(a.getId()), a);
  assert.equal(model.getActiveTabset()?.getSelectedNode(), two);
  // Without files the strip is what it was.
  reconcileSessionTabModel(model, state, label);
  assert.equal(model.getNodeById(a.getId()), undefined);
  assert.equal(model.getNodeById(two.getId()), two);
});

test("a file tab stays beside the new-session tab when no session is selected", () => {
  const model = createSessionTabModel();
  const files = openFileTab(emptyFileTabs(), file("a.ts"));
  reconcileSessionTabModel(model, emptySessionTabs(), label, files);
  const draft = model.getNodeById(sessionDraftNodeId) as TabNode;
  const a = model.getNodeById(fileNodeId(file("a.ts"))) as TabNode;
  assert.ok(draft && a);
  assert.equal(model.getActiveTabset()?.getSelectedNode(), a);
  assert.equal(draft.isEnableClose(), false);
  assert.equal(sessionLayoutActionAllowed(model, Actions.selectTab(sessionDraftNodeId), emptySessionTabs(), snapshot, () => true, files), true);
  reconcileSessionTabModel(model, emptySessionTabs(), label, activateFileTab(files, null));
  assert.equal(model.getActiveTabset()?.getSelectedNode(), draft);
  assert.equal(model.getNodeById(a.getId()), a);
});

test("file tabs select, move and split like session tabs; closing is an App intent and lifetimes still guard", () => {
  const model = createSessionTabModel(), state = both();
  const files = openFileTab(openFileTab(emptyFileTabs(), file("a.ts")), file("b.ts"));
  reconcileSessionTabModel(model, state, label, files);
  const a = model.getNodeById(fileNodeId(file("a.ts"))) as TabNode;
  const one = model.getNodeById(sessionNodeId(tab("one"))) as TabNode;
  const select = Actions.selectTab(a.getId()), close = Actions.deleteTab(a.getId());
  assert.deepEqual(fileTabAction(select, files, () => true), { kind: "select", file: file("a.ts") });
  assert.deepEqual(fileTabAction(close, files, () => true), { kind: "close", file: file("a.ts") });
  assert.equal(fileTabAction(select, files, () => false), null);
  assert.equal(fileTabAction(Actions.selectTab(one.getId()), files, () => true), null);
  assert.equal(fileTabAction(Actions.selectTab(fileNodeId(file("missing.ts"))), files, () => true), null);
  assert.equal(sessionTabAction(select, state, snapshot, () => true), null);
  assert.equal(sessionLayoutActionAllowed(model, select, state, snapshot, () => true, files), true);
  assert.equal(sessionLayoutActionAllowed(model, select, state, snapshot, () => false, files), false);
  assert.equal(sessionLayoutActionAllowed(model, select, state, undefined, () => true, files), false);
  // A file node the App no longer owns, or an App that passes no files, admits nothing for it.
  assert.equal(sessionLayoutActionAllowed(model, select, state, snapshot, () => true), false);
  assert.equal(sessionLayoutActionAllowed(model, close, state, snapshot, () => true, files), false);
  assert.equal(sessionLayoutActionAllowed(model, Actions.renameTab(a.getId(), "renamed"), state, snapshot, () => true, files), false);
  const split = Actions.moveNode(a.getId(), a.getParent()!.getId(), DockLocation.RIGHT, -1, true);
  assert.equal(sessionLayoutActionAllowed(model, split, state, snapshot, () => true, files), true);
  assert.equal(sessionLayoutActionAllowed(model, split, state, snapshot, () => true), false);
  const drop = sessionTabDrop(a, a.getParent() as TabSetNode, { x: 0, y: 0, width: 600, height: 400 }, { x: 590, y: 200 });
  assert.equal(drop?.location, DockLocation.RIGHT);
  model.doAction(split);
  assert.notEqual(a.getParent(), one.getParent());
  assert.equal(model.getActiveTabset()?.getSelectedNode(), a);
  // Session and file panes are selected side by side; either tabset can become the active one.
  const sessions = Actions.setActiveTabset(one.getParent()!.getId());
  assert.equal(sessionLayoutActionAllowed(model, sessions, state, snapshot, () => true, files), true);
  assert.equal(sessionLayoutActionAllowed(model, Actions.setActiveTabset(a.getParent()!.getId()), state, snapshot, () => true, files), true);
  assert.equal(sessionLayoutActionAllowed(model, Actions.moveNode(a.getParent()!.getId(), one.getParent()!.getId(), DockLocation.BOTTOM, -1), state, snapshot, () => true, files), true);
  // Reconciling keeps the split; the active file's pane is the active tabset.
  model.doAction(sessions);
  const parents = [a.getParent(), one.getParent()];
  reconcileSessionTabModel(model, state, label, activateFileTab(files, file("a.ts")));
  assert.deepEqual([a.getParent(), one.getParent()], parents);
  assert.equal(model.getActiveTabset(), a.getParent());
  reconcileSessionTabModel(model, state, label, activateFileTab(files, null));
  assert.equal(model.getActiveTabset(), one.getParent());
  assert.equal(a.isSelected(), true);
});

test("a changes tab opens in a pane on the right; the next one joins it and both are tabs like any other", () => {
  const model = createSessionTabModel(), state = both();
  const first = changesTab({ id: "p", path: "/p" }), second = changesTab({ id: "q", path: "/q" });
  const named = (file: FileTab) => `${isChangesTab(file) ? "Changes" : "Editor"} · ${file.projectId}`;
  reconcileSessionTabModel(model, state, label, emptyFileTabs(), named);
  const sessions = model.getActiveTabset()!;
  let files = openFileTab(emptyFileTabs(), first);
  reconcileSessionTabModel(model, state, label, files, named);
  const node = model.getNodeById(fileNodeId(first)) as TabNode;
  assert.equal(node.getName(), "Changes · p");
  assert.equal(node.getComponent(), "changes");
  const pane = node.getParent() as TabSetNode;
  assert.notEqual(pane, sessions, "Its own pane.");
  const row = pane.getParent()!.getChildren();
  assert.equal(pane.getParent(), sessions.getParent());
  assert.ok(row.indexOf(pane) > row.indexOf(sessions), "On the right.");
  assert.equal(model.getActiveTabset(), pane);
  assert.equal(sessions.getTabNodes().length, 2, "The sessions stay where they were.");
  // The changes of another project open beside the first; a file opens where the keyboard is.
  files = openFileTab(files, second);
  reconcileSessionTabModel(model, state, label, files, named);
  assert.equal((model.getNodeById(fileNodeId(second)) as TabNode).getParent(), pane);
  assert.equal(pane.getSelectedNode()?.getId(), fileNodeId(second));
  // It moves like any tab, and a new name follows its project.
  const move = Actions.moveNode(node.getId(), sessions.getId(), DockLocation.CENTER, -1, true);
  assert.equal(sessionLayoutActionAllowed(model, move, state, snapshot, () => true, files), true);
  model.doAction(move);
  reconcileSessionTabModel(model, state, label, files, file => `${isChangesTab(file) ? "Changes" : "Editor"} · renamed ${file.projectId}`);
  assert.equal(node.getName(), "Changes · renamed p");
  assert.equal(node.getParent(), sessions);
  // Closed, its node goes and the pane with its last tab.
  files = closeFileTab(closeFileTab(files, first), second);
  reconcileSessionTabModel(model, state, label, files, named);
  assert.equal(model.getNodeById(fileNodeId(first)), undefined);
  assert.equal(model.getNodeById(pane.getId()), undefined);
});

test("a terminal opens in a pane under the tabs, a third of the height; the next one joins it and closing the last one gives the room back", () => {
  const model = createSessionTabModel(), state = both();
  const first = terminalTab({ id: "t1", projectId: "p", folder: "/p" }), second = terminalTab({ id: "t2", projectId: "p", folder: "/p/src" });
  const named = (file: FileTab) => `Terminal ${file.terminalId}`;
  reconcileSessionTabModel(model, state, label, emptyFileTabs(), named);
  const sessions = model.getActiveTabset()!;
  let files = openFileTab(emptyFileTabs(), first);
  reconcileSessionTabModel(model, state, label, files, named);
  const node = model.getNodeById(fileNodeId(first)) as TabNode;
  assert.equal(node.getName(), "Terminal t1");
  assert.equal(node.getComponent(), "terminal");
  const pane = node.getParent() as TabSetNode;
  assert.notEqual(pane, sessions, "Its own pane.");
  const column = pane.getParent() as RowNode;
  assert.equal(column, sessions.getParent());
  const stacked = column.getChildren();
  assert.ok(stacked.indexOf(pane) > stacked.indexOf(sessions), "Under the sessions.");
  // One above the other: the pane of the sessions and the one of the terminals share a column of the window.
  assert.notEqual(column, model.getRootRow());
  assert.equal(column.getParent(), model.getRootRow());
  assert.deepEqual(stacked.map(child => Math.round((child as TabSetNode).getWeight())), [66, 34]);
  assert.equal(sessions.getTabNodes().length, 2, "The sessions stay where they were.");
  // The next terminal is a tab of the same pane, wherever the keyboard is.
  model.doAction(Actions.setActiveTabset(sessions.getId()));
  files = openFileTab(files, second);
  reconcileSessionTabModel(model, state, label, files, named);
  assert.equal((model.getNodeById(fileNodeId(second)) as TabNode).getParent(), pane);
  assert.equal(pane.getTabNodes().length, 2);
  // A changes tab still opens on the right of where it is asked from, not in the pane of the terminals.
  const changes = changesTab({ id: "p", path: "/p" });
  files = openFileTab(files, changes);
  reconcileSessionTabModel(model, state, label, files, named);
  assert.notEqual((model.getNodeById(fileNodeId(changes)) as TabNode).getParent(), pane);
  // The pane of the terminals stays theirs: what is opened while the keyboard is in a terminal opens with the sessions.
  model.doAction(Actions.setActiveTabset(pane.getId()));
  const more = openSessionTab(state, tab("three")), editor = editorTab({ id: "p", path: "/p" });
  files = openFileTab(files, editor);
  reconcileSessionTabModel(model, more, label, files, named);
  assert.equal((model.getNodeById(sessionNodeId(tab("three"))) as TabNode).getParent(), sessions);
  assert.equal((model.getNodeById(fileNodeId(editor)) as TabNode).getParent(), sessions);
  model.doAction(Actions.setActiveTabset(pane.getId()));
  reconcileSessionTabModel(model, { ...more, active: null }, label, activateFileTab(files, null), named);
  assert.equal((model.getNodeById(sessionDraftNodeId) as TabNode).getParent(), sessions, "A new session is not started in the pane of the terminals.");
  assert.deepEqual(pane.getTabNodes().map(child => child.getId()), [fileNodeId(first), fileNodeId(second)]);
  reconcileSessionTabModel(model, more, label, files, named);
  // The title follows the terminal, and closed tabs take their pane with them.
  reconcileSessionTabModel(model, state, label, files, file => `renamed ${file.terminalId ?? file.view}`);
  assert.equal(node.getName(), "renamed t1");
  files = closeFileTab(closeFileTab(files, first), second);
  reconcileSessionTabModel(model, state, label, files, named);
  assert.equal(model.getNodeById(fileNodeId(first)), undefined);
  assert.equal(model.getNodeById(pane.getId()), undefined);
});

test("a middle click closes a tab as its close button does, and nothing else does", () => {
  const model = createSessionTabModel();
  reconcileSessionTabModel(model, { ...both(), active: null }, label);
  const one = model.getNodeById(sessionNodeId(tab("one"))) as TabNode;
  const draft = model.getNodeById(sessionDraftNodeId) as TabNode;
  assert.deepEqual(tabMiddleClickAction(one, 1), Actions.deleteTab(one.getId()));
  assert.deepEqual(tabMiddleClickAction(draft, 1), Actions.deleteTab(sessionDraftNodeId), "The new session goes back to an open session.");
  assert.equal(tabMiddleClickAction(one, 0), null, "A click with a key held is not a close.");
  assert.equal(tabMiddleClickAction(one, 2), null);
  assert.equal(tabMiddleClickAction(one.getParent()!, 1), null, "The bar of a pane closes nothing.");
  const alone = createSessionTabModel();
  reconcileSessionTabModel(alone, emptySessionTabs(), label);
  assert.equal(tabMiddleClickAction(alone.getNodeById(sessionDraftNodeId)!, 1), null, "With no session open, the new session cannot be closed.");
});

test("middle-click canvas and Documentation closes still go through file-tab ownership", () => {
  const tabs = [canvasTab({ pluginKey: "builtin:statistics", canvasId: "statistics" }), documentationTab];
  const files = tabs.reduce((state, file) => openFileTab(state, file), emptyFileTabs());
  const model = createSessionTabModel();
  reconcileSessionTabModel(model, emptySessionTabs(), label, files);
  for (const file of tabs) {
    const action = tabMiddleClickAction(model.getNodeById(fileNodeId(file))!, 1);
    assert.ok(action);
    assert.deepEqual(fileTabAction(action, files, () => true), { kind: "close", file });
    assert.equal(fileTabAction(action, files, () => false), null, "An obsolete owner cannot close a tab.");
  }
});

// The ids of the tabs of the first pane, in the order they are shown.
const pane = (model: ReturnType<typeof createSessionTabModel>) => { let found: TabSetNode | undefined; model.visitNodes(node => { if (node instanceof TabSetNode) found ??= node; }); return found!; };
const strip = (model: ReturnType<typeof createSessionTabModel>) => pane(model).getChildren().map(node => node.getId());
const transient = (model: ReturnType<typeof createSessionTabModel>) => {
  const ids: string[] = [];
  model.visitNodes(node => { if (node instanceof TabNode && node.getConfig()?.preview === true) ids.push(node.getId()); });
  return ids;
};
const scopeP: DraftTab = { projectId: "p", path: "/p" }, chats: DraftTab = { projectId: null, path: null };

test("a selected session without a tab is looked at in the transient tab, unless it is opened to be kept", () => {
  const kept = openSessionTab(emptySessionTabs(), tab("one"));
  const looked = sessionTabPresentation(kept, snapshot, "p", "two");
  assert.deepEqual(looked.open, [tab("one"), tab("two")]);
  assert.deepEqual([looked.active, looked.preview, looked.scope], [tab("two"), tab("two"), scopeP]);
  assert.equal(sessionTabPresentation(kept, snapshot, "p", "two", "permanent").preview ?? null, null);
  // A session that has a tab is shown as it is, whatever it is opened for.
  assert.equal(sessionTabPresentation(kept, snapshot, "p", "one").preview ?? null, null);
  assert.deepEqual(sessionTabPresentation(looked, snapshot, "p", "two", "preview").preview, tab("two"));
  // No session: the prompt of the selected scope is the one shown.
  assert.deepEqual(sessionTabPresentation(kept, snapshot, "p", null).scope, scopeP);
  assert.deepEqual(sessionTabPresentation(kept, snapshot, null, null).scope, chats);
  assert.equal(sessionTabPresentation(kept, snapshot, "missing", null).scope, null);
});

test("what becomes the transient tab takes the place of the one that leaves; a kept tab stays where it is", () => {
  const model = createSessionTabModel();
  // A kept session, then one that is looked at, then another kept one after it.
  let state = openSessionTab(openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"), "preview"), tab("three"));
  const three = { ...snapshot, sessions: [...snapshot.sessions, ...["three", "four"].map(id => ({ ...snapshot.sessions[0], id, title: id, fullTitle: id }))] };
  reconcileSessionTabModel(model, { ...state, active: tab("two") }, label);
  assert.deepEqual(strip(model), [sessionNodeId(tab("one")), sessionNodeId(tab("two")), sessionNodeId(tab("three"))]);
  assert.deepEqual(transient(model), [sessionNodeId(tab("two"))]);

  // Another session is looked at: it is where the transient tab was, between the two kept ones.
  state = openSessionTab({ ...state, active: tab("two") }, tab("four"), "preview");
  reconcileSessionTabModel(model, state, label);
  assert.deepEqual(strip(model), [sessionNodeId(tab("one")), sessionNodeId(tab("four")), sessionNodeId(tab("three"))]);
  assert.deepEqual(transient(model), [sessionNodeId(tab("four"))]);
  assert.equal(model.getActiveTabset()?.getSelectedNode()?.getId(), sessionNodeId(tab("four")));
  assert.ok(sessionTabAction(Actions.selectTab(sessionNodeId(tab("four"))), state, three, () => true));

  // Kept: the same node, in the same place, no longer transient.
  const node = model.getNodeById(sessionNodeId(tab("four")));
  state = pinSessionTab(state, tab("four"));
  reconcileSessionTabModel(model, state, label);
  assert.equal(model.getNodeById(sessionNodeId(tab("four"))), node);
  assert.deepEqual(strip(model), [sessionNodeId(tab("one")), sessionNodeId(tab("four")), sessionNodeId(tab("three"))]);
  assert.deepEqual(transient(model), []);

  // The next session looked at has no transient tab to take: it is added after the others.
  state = openSessionTab(state, tab("two"), "preview");
  reconcileSessionTabModel(model, state, label);
  assert.deepEqual(strip(model).at(-1), sessionNodeId(tab("two")));
  assert.deepEqual(transient(model), [sessionNodeId(tab("two"))]);
});

test("the prompt of a new session takes the transient tab from the session looked at, and gives it back", () => {
  const model = createSessionTabModel();
  let state = openSessionTab(openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"), "preview"), tab("three"));
  state = { ...state, active: tab("two") };
  reconcileSessionTabModel(model, { ...state, scope: scopeP }, label);
  // The row of the project: the session that was looked at leaves, and the prompt is where it was.
  state = closePreviewTab(state);
  reconcileSessionTabModel(model, { ...state, scope: scopeP }, label);
  assert.deepEqual(strip(model), [sessionNodeId(tab("one")), sessionDraftNodeId, sessionNodeId(tab("three"))]);
  assert.deepEqual(transient(model), [sessionDraftNodeId]);
  assert.equal(model.getActiveTabset()?.getSelectedNode()?.getId(), sessionDraftNodeId);
  assert.equal(shownDraftNodeId({ ...state, scope: scopeP }), sessionDraftNodeId);
  // A session is looked at again: it takes the place of the prompt.
  state = openSessionTab(state, tab("two"), "preview");
  reconcileSessionTabModel(model, { ...state, scope: scopeP }, label);
  assert.deepEqual(strip(model), [sessionNodeId(tab("one")), sessionNodeId(tab("two")), sessionNodeId(tab("three"))]);
  assert.equal(model.getNodeById(sessionDraftNodeId), undefined);
});

test("a prompt that is left with something typed keeps the place of the transient tab, and what replaces it comes after", () => {
  const names = (draft: DraftTab) => draft.projectId === null ? "New chat" : "New session - Project";
  // The prompt of the project is in the transient tab, between two sessions.
  const start = () => {
    const model = createSessionTabModel();
    const state: SessionTabs = { ...openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two")), active: null };
    reconcileSessionTabModel(model, { ...state, scope: scopeP }, label, undefined, undefined, names);
    model.doAction(Actions.moveNode(sessionDraftNodeId, pane(model).getId(), DockLocation.CENTER, 1, true));
    return { model, state };
  };

  // The chats are selected: the prompt of the project gets its tab where it was, and the one of the chats is after it.
  let { model, state } = start();
  state = openDraftTab(state, scopeP);
  reconcileSessionTabModel(model, { ...state, scope: chats }, label, undefined, undefined, names);
  assert.deepEqual(strip(model), [sessionNodeId(tab("one")), draftNodeId(scopeP), sessionDraftNodeId, sessionNodeId(tab("two"))]);
  assert.deepEqual(transient(model), [sessionDraftNodeId]);
  assert.equal(model.getActiveTabset()?.getSelectedNode()?.getId(), sessionDraftNodeId);

  // A session is looked at instead: it takes the transient tab, after the prompt that is kept.
  ({ model, state } = start());
  state = openSessionTab(openDraftTab(state, scopeP), tab("three"), "preview");
  reconcileSessionTabModel(model, { ...state, scope: scopeP }, label, undefined, undefined, names);
  assert.deepEqual(strip(model), [sessionNodeId(tab("one")), draftNodeId(scopeP), sessionNodeId(tab("three")), sessionNodeId(tab("two"))]);
  assert.deepEqual(transient(model), [sessionNodeId(tab("three"))]);
  assert.equal(model.getActiveTabset()?.getSelectedNode()?.getId(), sessionNodeId(tab("three")));
});

test("a prompt that is kept has a tab of its own, where the transient one was, beside the prompts of other scopes", () => {
  const model = createSessionTabModel();
  const names = (draft: DraftTab) => draft.projectId === null ? "New chat" : "New session - Project";
  let state: SessionTabs = { ...openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two")), active: null };
  reconcileSessionTabModel(model, { ...state, scope: scopeP }, label, undefined, undefined, names);
  model.doAction(Actions.moveNode(sessionDraftNodeId, pane(model).getId(), DockLocation.CENTER, 1, true));
  assert.deepEqual(strip(model), [sessionNodeId(tab("one")), sessionDraftNodeId, sessionNodeId(tab("two"))]);

  // Kept: the transient prompt is gone, and the tab of the scope is in its place, shown.
  state = openDraftTab(state, scopeP);
  reconcileSessionTabModel(model, { ...state, scope: scopeP }, label, undefined, undefined, names);
  assert.deepEqual(strip(model), [sessionNodeId(tab("one")), draftNodeId(scopeP), sessionNodeId(tab("two"))]);
  assert.deepEqual(transient(model), []);
  const own = model.getNodeById(draftNodeId(scopeP)) as TabNode;
  assert.deepEqual([own.getName(), own.isEnableClose(), own.isSelected()], ["New session - Project", true, true]);
  assert.equal(shownDraftNodeId({ ...state, scope: scopeP }), draftNodeId(scopeP));

  // The chats are selected: their prompt is the transient one, and the tab of the project stays.
  reconcileSessionTabModel(model, { ...state, scope: chats }, label, undefined, undefined, names);
  assert.ok(model.getNodeById(draftNodeId(scopeP)) && model.getNodeById(sessionDraftNodeId));
  assert.equal(model.getActiveTabset()?.getSelectedNode()?.getId(), sessionDraftNodeId);
  // Kept as well: two prompts have their own tab. A session shown leaves both.
  state = openDraftTab(state, chats);
  reconcileSessionTabModel(model, { ...state, scope: chats }, label, undefined, undefined, names);
  assert.equal((model.getNodeById(draftNodeId(chats)) as TabNode).getName(), "New chat");
  reconcileSessionTabModel(model, { ...state, active: tab("one"), scope: scopeP }, label, undefined, undefined, names);
  assert.ok(model.getNodeById(draftNodeId(scopeP)) && model.getNodeById(draftNodeId(chats)));
  assert.equal(model.getNodeById(sessionDraftNodeId), undefined);
  assert.equal(model.getActiveTabset()?.getSelectedNode()?.getId(), sessionNodeId(tab("one")));

  // The dock selects, moves and closes the tab of a prompt as it does a session; an unknown one is refused.
  const current = () => true;
  assert.deepEqual(draftTabAction(Actions.selectTab(draftNodeId(scopeP)), state, snapshot, current), { kind: "select", draft: scopeP });
  assert.deepEqual(draftTabAction(Actions.deleteTab(draftNodeId(chats)), state, snapshot, current), { kind: "close", draft: chats });
  assert.equal(draftTabAction(Actions.selectTab(draftNodeId({ projectId: "other", path: "/o" })), state, snapshot, current), null);
  assert.equal(draftTabAction(Actions.selectTab(draftNodeId(scopeP)), state, { ...snapshot, projects: [] }, current), null, "a prompt whose project is gone");
  assert.equal(draftTabAction(Actions.selectTab(draftNodeId(scopeP)), state, snapshot, () => false), null);
  assert.ok(sessionLayoutActionAllowed(model, Actions.selectTab(draftNodeId(scopeP)), state, snapshot, current));
  assert.ok(sessionLayoutActionAllowed(model, Actions.moveNode(draftNodeId(scopeP), pane(model).getId(), DockLocation.RIGHT, -1, true), state, snapshot, current));
  assert.equal(sessionLayoutActionAllowed(model, Actions.deleteTab(draftNodeId(scopeP)), state, snapshot, current), false, "closing is asked of the window");

  // Closed: its tab goes, and the other prompt keeps its own.
  state = closeDraftTab(state, scopeP);
  reconcileSessionTabModel(model, { ...state, active: tab("one"), scope: scopeP }, label, undefined, undefined, names);
  assert.equal(model.getNodeById(draftNodeId(scopeP)), undefined);
  assert.ok(model.getNodeById(draftNodeId(chats)));
});
