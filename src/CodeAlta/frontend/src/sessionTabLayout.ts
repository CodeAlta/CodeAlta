import { Actions, DockLocation, Model, RowNode, TabNode, TabSetNode, type Action } from "flexlayout-react";
import type { WorkspaceSnapshot } from "#neoastra";
import { openSessionTab, reconcileSessionTabs, resolveSessionTab, selectedTab, sessionTabLimit, tabKey, type SessionTab, type SessionTabs } from "./sessionTabs";

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
    enableEdgeDock: true, enableEdgeDockIndicators: true,
    tabEnableDrag: true, tabEnableRename: false, tabEnablePin: false,
    tabEnableFloat: false, tabEnableFloatIcon: false,
    tabEnablePopout: false, tabEnablePopoutIcon: false, tabEnablePopoutOverlay: false,
    // Keep open panes mounted so in-flight waits and editor drafts survive tab changes.
    tabEnableRenderOnDemand: false, tabEnableScrollbars: false,
    tabSetEnableClose: true, tabSetEnableCloseButton: false, tabSetEnableDeleteWhenEmpty: true,
    tabSetEnableDivide: true, tabSetEnableDrag: true, tabSetEnableDrop: true,
    tabSetEnableMaximize: false, tabSetEnableActiveIcon: false, tabSetEnableTabGroups: false,
  }, borders: [], layout: { type: "row", id: "session-tabs-row", children: [
    { type: "tabset", id: panelId, selected: 0, children: [] },
  ] } });
}

// Only public actions; retained nodes (and their factory roots) are never rebuilt.
export function reconcileSessionTabModel(model: Model, state: SessionTabs, label: (tab: SessionTab | null) => string) {
  const open = state.open.slice(0, sessionTabLimit);
  const ids = new Set(open.map(sessionNodeId));
  const existing: TabNode[] = [];
  model.visitNodes(node => { if (node instanceof TabNode) existing.push(node); });
  for (const node of existing) {
    if (!ids.has(node.getId())) model.doAction(Actions.deleteTab(node.getId()));
  }
  for (const tab of open) {
    const id = sessionNodeId(tab);
    const name = label(tab);
    const node = model.getNodeById(id);
    if (!node) model.doAction(Actions.addTab({ type: "tab", id, name, component: "session" }, model.getActiveTabset()?.getId() ?? panelId, DockLocation.CENTER, -1, false));
    else if (node instanceof TabNode && node.getName() !== name) model.doAction(Actions.renameTab(id, name));
  }
  const active = state.active ? sessionNodeId(state.active) : "";
  const node = ids.has(active) ? model.getNodeById(active) : undefined;
  if (node instanceof TabNode && !node.isSelected()) model.doAction(Actions.selectTab(node.getId()));
  if (node instanceof TabNode && node.getParent() && model.getActiveTabset() !== node.getParent())
    model.doAction(Actions.setActiveTabset(node.getParent()!.getId()));
}

export type SessionTabIntent = { kind: "select" | "close"; tab: SessionTab };
export function sessionTabAction(action: Action, state: SessionTabs, snapshot: WorkspaceSnapshot | undefined,
  current: () => boolean): SessionTabIntent | null {
  if (!current() || !snapshot || (action.type !== Actions.SELECT_TAB && action.type !== Actions.DELETE_TAB)) return null;
  const id = action.type === Actions.SELECT_TAB ? action.data.tabNode : action.data.node;
  const matches = state.open.slice(0, sessionTabLimit).filter(tab => sessionNodeId(tab) === id);
  if (matches.length !== 1 || !resolveSessionTab(snapshot, matches[0])) return null;
  return { kind: action.type === Actions.SELECT_TAB ? "select" : "close", tab: matches[0] };
}

export function ownsSessionTabContent(id: string, state: SessionTabs) {
  return !!state.active && id === sessionNodeId(state.active);
}

// Layout owns geometry and must apply accepted GUI actions before App projects focus.
// Unrecognized/mutation actions still cannot bypass catalog or captured-lifetime checks.
export function sessionLayoutActionAllowed(model: Model, action: Action, state: SessionTabs,
  snapshot: WorkspaceSnapshot | undefined, current: () => boolean): boolean {
  if (!current() || !snapshot) return false;
  const valid = (node: TabNode) => !!sessionTabAction(Actions.selectTab(node.getId()), state, snapshot, current);
  if (action.type === Actions.SELECT_TAB) return !!sessionTabAction(action, state, snapshot, current);
  if (action.type === Actions.ADJUST_WEIGHTS) return model.getNodeById(action.data.nodeId) instanceof RowNode;
  if (action.type === Actions.SET_ACTIVE_TABSET) {
    const node = model.getNodeById(action.data.tabsetNode);
    const selected = node instanceof TabSetNode ? node.getSelectedNode() : undefined;
    return !!selected && valid(selected);
  }
  if (action.type !== Actions.MOVE_NODE) return false;
  const source = model.getNodeById(action.data.fromNode);
  const target = model.getNodeById(action.data.toNode);
  if (!(target instanceof RowNode || target instanceof TabSetNode)) return false;
  return source instanceof TabNode ? valid(source)
    : source instanceof TabSetNode && source.getTabNodes().length > 0 && source.getTabNodes().every(valid);
}

// A project prompt is presentation in the focused pane, not a project-scoped tab/model.
// Keep all session headers and other split panes available while the prompt is shown.
export function sessionBlankNodeId(model: Model, state: SessionTabs): string | undefined {
  if (state.active) return undefined;
  const node = model.getActiveTabset()?.getSelectedNode();
  if (node) return node.getId();
  let first: string | undefined;
  model.visitNodes(node => { if (first === undefined && node instanceof TabNode && node.isSelected()) first = node.getId(); });
  return first;
}
