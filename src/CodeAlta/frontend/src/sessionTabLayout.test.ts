import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { Actions, DockLocation, TabNode, TabSetNode } from "flexlayout-react";
import { sessionTabDrop } from "./sessionTabDrag";
import type { WorkspaceSnapshot } from "#neoastra";
import { closeSessionTab, emptySessionTabs, openSessionTab, sessionTabLimit, type SessionTab } from "./sessionTabs";
import { createSessionTabModel, ownsSessionTabContent, reconcileSessionTabModel, sessionDraftNodeId, sessionLayoutActionAllowed, sessionNodeId, sessionTabAction, sessionTabPresentation } from "./sessionTabLayout";

const tab = (id: string) => ({ projectId: "p", sessionId: id, path: "/p" });
const snapshot: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  projects: [{ id: "p", name: "Project", path: "/p", archived: false }], sessions: ["one", "two"].map(id => ({
    id, title: id, fullTitle: id, fullTitleTruncated: false, workspacePath: "/p", scopeKind: "project", projectId: "p",
    messageCount: null, createdAt: null, updatedAt: "2026-09-27T00:00:00Z", parentSessionId: null, lineageIssue: null, providerKey: null,
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

test("installed 0.11 content memoization requires eager hidden factory invalidation; integration uses public hooks", () => {
  const source = readFileSync(new URL("../node_modules/flexlayout-react/dist/index.js", import.meta.url), "utf8");
  // Characterize the installed renderer, not an application dependency on internals.
  assert.match(source, /const visible = isSelected \|\| !isEnableRenderOnDemand/);
  assert.match(source, /nextProps\.visible &&/);
  assert.match(source, /const key = tabNode\.getId\(\)/);
  const component = readFileSync(new URL("./SessionTabStrip.tsx", import.meta.url), "utf8");
  assert.match(component, /invalidateTabContentOnParentRender=\{true\}/);
  assert.match(component, /node\.getId\(\) === sessionDraftNodeId/);
  assert.match(component, /onShowOverflowMenu=/);
  assert.match(component, /onModelChange=/);
  assert.doesNotMatch(component, /hidden=\{!state\.open\.length\}/);
  assert.doesNotMatch(component, /hidden=\{!state\.active\}/);
  assert.match(component, /if \(accepted\) model\.doAction\(accepted\)/);
  assert.match(component, /isComposing.*keyCode === 229.*repeat/);
  const notes = readFileSync(new URL("./SessionNotesOverlay.tsx", import.meta.url), "utf8");
  assert.doesNotMatch(notes, /flexlayout-react|<Layout/);
  assert.match(notes, /hidden=\{collapsed\}/);
  assert.match(notes, /aria-expanded=\{!collapsed\}/);
  assert.match(component, /onPointerDownCapture=\{drag\.down\}/);
  assert.match(component, /onDragStartCapture=/);
  assert.doesNotMatch(component, /getMoveableElement|appendChild|setAttribute|createRoot/);
});
