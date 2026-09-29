import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { Actions, TabNode } from "flexlayout-react";
import type { WorkspaceSnapshot } from "#neoastra";
import { closeSessionTab, emptySessionTabs, openSessionTab, sessionTabLimit, type SessionTab } from "./sessionTabs";
import { createSessionTabModel, draftTabId, ownsSessionTabContent, reconcileSessionTabModel, sessionNodeId, sessionTabAction, sessionTabPresentation } from "./sessionTabLayout";

const tab = (id: string) => ({ projectId: "p", sessionId: id, path: "/p" });
const snapshot: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  projects: [{ id: "p", name: "Project", path: "/p", archived: false }], sessions: ["one", "two"].map(id => ({
    id, title: id, fullTitle: id, fullTitleTruncated: false, workspacePath: "/p", scopeKind: "project", projectId: "p",
    messageCount: null, createdAt: null, updatedAt: "2026-09-27T00:00:00Z", parentSessionId: null, lineageIssue: null, providerKey: null,
  })) };
const label = (value: SessionTab | null) => value?.sessionId ?? "Prompt draft";
const both = () => openSessionTab(openSessionTab(emptySessionTabs(), tab("one")), tab("two"));

test("public model projects real identities and draft, retaining nodes across reconciliation and label/geometry updates", () => {
  const model = createSessionTabModel();
  const state = both();
  reconcileSessionTabModel(model, state, label);
  const one = model.getNodeById(sessionNodeId(tab("one"))) as TabNode;
  const two = model.getNodeById(sessionNodeId(tab("two"))) as TabNode;
  assert.equal(two.isSelected(), true);
  assert.equal(one.isSelected(), false);
  assert.equal(one.isEnableRenderOnDemand(), false);
  assert.equal(one.isEnableDrag(), false);
  assert.equal((model.getNodeById(draftTabId) as TabNode).isEnableClose(), false);
  reconcileSessionTabModel(model, state, value => `${label(value)} localized`);
  model.doAction(Actions.adjustWeights("session-tabs-row", [100]));
  assert.equal(model.getNodeById(one.getId()), one);
  assert.equal(model.getNodeById(two.getId()), two);
  assert.equal(two.getName(), "two localized");
  assert.equal(two.isSelected(), true);
  reconcileSessionTabModel(model, { ...state, active: null }, label);
  assert.equal((model.getNodeById(draftTabId) as TabNode).isSelected(), true);
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
  assert.deepEqual(sessionTabAction(Actions.selectTab(draftTabId), state, snapshot, () => true), { kind: "draft" });
  for (const action of [Actions.deleteTab(draftTabId), Actions.selectTab("unknown"), Actions.deleteTabset("session-tabs-panel"),
    Actions.renameTab(sessionNodeId(tab("one")), "renamed")])
    assert.equal(sessionTabAction(action, state, snapshot, () => true), null);
  assert.deepEqual(model.toJson(), before);
});

test("factory ownership is exactly one active identity; App selection leads effects without re-binding path ABA", () => {
  let state = both();
  const ids = [draftTabId, ...state.open.map(sessionNodeId)];
  assert.deepEqual(ids.filter(id => ownsSessionTabContent(id, state)), [sessionNodeId(tab("two"))]);
  state = sessionTabPresentation(state, snapshot, "p", "one");
  assert.deepEqual(ids.filter(id => ownsSessionTabContent(id, state)), [sessionNodeId(tab("one"))]);
  state = sessionTabPresentation(state, snapshot, "p", null);
  assert.deepEqual(ids.filter(id => ownsSessionTabContent(id, state)), [draftTabId]);
  const changed = { ...snapshot, sessions: snapshot.sessions.map(row => ({ ...row, workspacePath: "/changed" })),
    projects: [{ ...snapshot.projects[0], path: "/changed" }] };
  assert.equal(sessionTabPresentation(both(), changed, "p", "two").active, null);
});

test("close/reopen and 32 identity cap retain inactive owner metadata without hidden workspace payloads", () => {
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
  assert.equal(model.getNodeById("session-tabs-panel")!.getChildren().length, sessionTabLimit + 1);
  assert.equal(model.getNodeById("session-tabs-panel")!.getChildren().filter(node => ownsSessionTabContent(node.getId(), state)).length, 1);
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
  assert.match(component, /ownsSessionTabContent\(node\.getId\(\), state\) \? children : null/);
  assert.match(component, /onShowOverflowMenu=/);
  assert.match(component, /isComposing.*keyCode === 229.*repeat/);
  assert.doesNotMatch(component, /getMoveableElement|appendChild|setAttribute|createRoot/);
});
