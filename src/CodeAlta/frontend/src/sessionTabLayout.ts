import { Actions, DockLocation, Model, TabNode, type Action } from "flexlayout-react";
import type { WorkspaceSnapshot } from "#neoastra";
import { openSessionTab, reconcileSessionTabs, resolveSessionTab, selectedTab, sessionTabLimit, tabKey, type SessionTab, type SessionTabs } from "./sessionTabs";

export const draftTabId = "session-draft";
const panelId = "session-tabs-panel";
export const sessionNodeId = (tab: SessionTab) => `session:${tabKey(tab)}`;

// Project App's selection synchronously: its persistence effect can lag a render.
// Never rebind an invalid formerly active identity to a reused session ID.
export function sessionTabPresentation(state: SessionTabs, snapshot: WorkspaceSnapshot | undefined,
  projectId: string | null, sessionId: string | null): SessionTabs {
  if (!snapshot) return { ...state, active: null };
  const valid = reconcileSessionTabs(state, snapshot);
  if (state.active?.sessionId === sessionId && !resolveSessionTab(snapshot, state.active)) return { ...valid, active: null };
  const selected = selectedTab(snapshot, projectId, sessionId);
  return selected ? openSessionTab(valid, selected) : { ...valid, active: null };
}

export function createSessionTabModel() {
  return Model.fromJson({ global: {
    enableEdgeDock: false, enableEdgeDockIndicators: false,
    tabEnableDrag: false, tabEnableRename: false, tabEnablePin: false,
    tabEnableFloat: false, tabEnableFloatIcon: false,
    tabEnablePopout: false, tabEnablePopoutIcon: false, tabEnablePopoutOverlay: false,
    // Crucial: hidden factories must be invalidated too, to release the old owner.
    tabEnableRenderOnDemand: false, tabEnableScrollbars: false,
    tabSetEnableClose: false, tabSetEnableCloseButton: false, tabSetEnableDeleteWhenEmpty: false,
    tabSetEnableDivide: false, tabSetEnableDrag: false, tabSetEnableDrop: false,
    tabSetEnableMaximize: false, tabSetEnableActiveIcon: false, tabSetEnableTabGroups: false,
  }, borders: [], layout: { type: "row", id: "session-tabs-row", children: [
    { type: "tabset", id: panelId, selected: 0, children: [
      { type: "tab", id: draftTabId, name: "Prompt draft", component: "session", enableClose: false },
    ] },
  ] } });
}

// Only public actions; retained nodes (and their factory roots) are never rebuilt.
export function reconcileSessionTabModel(model: Model, state: SessionTabs, label: (tab: SessionTab | null) => string) {
  const open = state.open.slice(0, sessionTabLimit);
  const ids = new Set([draftTabId, ...open.map(sessionNodeId)]);
  for (const node of [...model.getNodeById(panelId)!.getChildren()]) {
    if (!ids.has(node.getId())) model.doAction(Actions.deleteTab(node.getId()));
  }
  for (const tab of [null, ...open]) {
    const id = tab ? sessionNodeId(tab) : draftTabId;
    const name = label(tab);
    const node = model.getNodeById(id);
    if (!node) model.doAction(Actions.addTab({ type: "tab", id, name, component: "session" }, panelId, DockLocation.CENTER, -1, false));
    else if (node instanceof TabNode && node.getName() !== name) model.doAction(Actions.renameTab(id, name));
  }
  const active = state.active ? sessionNodeId(state.active) : draftTabId;
  const node = model.getNodeById(ids.has(active) ? active : draftTabId);
  if (node instanceof TabNode && !node.isSelected()) model.doAction(Actions.selectTab(node.getId()));
}

export type SessionTabIntent = { kind: "draft" } | { kind: "select" | "close"; tab: SessionTab };
export function sessionTabAction(action: Action, state: SessionTabs, snapshot: WorkspaceSnapshot | undefined,
  current: () => boolean): SessionTabIntent | null {
  if (!current() || !snapshot || (action.type !== Actions.SELECT_TAB && action.type !== Actions.DELETE_TAB)) return null;
  const id = action.type === Actions.SELECT_TAB ? action.data.tabNode : action.data.node;
  if (id === draftTabId) return action.type === Actions.SELECT_TAB ? { kind: "draft" } : null;
  const matches = state.open.slice(0, sessionTabLimit).filter(tab => sessionNodeId(tab) === id);
  if (matches.length !== 1 || !resolveSessionTab(snapshot, matches[0])) return null;
  return { kind: action.type === Actions.SELECT_TAB ? "select" : "close", tab: matches[0] };
}

export function ownsSessionTabContent(id: string, state: SessionTabs) {
  return id === (state.active ? sessionNodeId(state.active) : draftTabId);
}
