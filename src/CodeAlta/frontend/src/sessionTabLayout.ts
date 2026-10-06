import { Actions, DockLocation, Model, RowNode, TabNode, TabSetNode, type Action } from "flexlayout-react";
import type { WorkspaceSnapshot } from "#neoastra";
import { openSessionTab, reconcileSessionTabs, resolveSessionTab, selectedTab, sessionTabLimit, tabKey, type SessionTab, type SessionTabs } from "./sessionTabs";
import { emptyFileTabs, fileNodeId, fileTabLimit, isChangesTab, isTerminalTab, type FileTab, type FileTabs } from "./fileTabs";

const panelId = "session-tabs-panel";
// Presentation only: never persisted in the session list, recent history or draft owners.
export const sessionDraftNodeId = "session-draft";
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
    tabEnableDrag: true, tabEnableRename: false, tabEnablePin: false,
    tabEnableFloat: false, tabEnableFloatIcon: false,
    tabEnablePopout: false, tabEnablePopoutIcon: false, tabEnablePopoutOverlay: false,
    // Keep open panes mounted so in-flight waits and editor drafts survive tab changes.
    tabEnableRenderOnDemand: false, tabEnableScrollbars: false,
    tabSetEnableClose: true, tabSetEnableCloseButton: false, tabSetEnableDeleteWhenEmpty: true,
    tabSetEnableDivide: true, tabSetEnableDrag: false, tabSetEnableDrop: true,
    tabSetEnableMaximize: false, tabSetEnableActiveIcon: false, tabSetEnableTabGroups: false,
  }, borders: [], layout: { type: "row", id: "session-tabs-row", children: [
    { type: "tabset", id: panelId, selected: 0, children: [] },
  ] } });
}

const noFiles = emptyFileTabs();

// Only public actions; retained nodes (and their factory roots) are never rebuilt.
// The tabs of projects (their code editor, their changes) share the strip: an active one is the selected tab,
// over the session selection. An editor opens in the pane of the tab it was asked from.
// A changes tab opens beside the tab it was asked from: in the pane that already holds one, or in a new pane on
// the right. A terminal opens under it: in the pane that already holds one, or in a new pane at the bottom, a
// third of the height. From there each is a tab like any other and can be moved or closed. The pane of the
// terminals stays theirs: what is opened while the keyboard is in a terminal opens in a pane of the sessions.
export function reconcileSessionTabModel(model: Model, state: SessionTabs, label: (tab: SessionTab | null) => string, files: FileTabs = noFiles,
  fileLabel: (file: FileTab) => string = file => file.view) {
  const open = state.open.slice(0, sessionTabLimit);
  const openFiles = files.open.slice(0, fileTabLimit);
  const ids = new Set([...open.map(sessionNodeId), ...openFiles.map(fileNodeId)]);
  if (!state.active) ids.add(sessionDraftNodeId);
  const existing: TabNode[] = [];
  model.visitNodes(node => { if (node instanceof TabNode) existing.push(node); });
  const changeIds = new Set(openFiles.filter(isChangesTab).map(fileNodeId));
  const terminalIds = new Set(openFiles.filter(isTerminalTab).map(fileNodeId));
  // The pane a new tab opens in: the active one, unless it holds nothing but terminals; then one that holds something else.
  const home = () => {
    const terminalsOnly = (pane: TabSetNode) => pane.getChildren().length > 0 && pane.getChildren().every(child => terminalIds.has(child.getId()));
    const active = model.getActiveTabset();
    if (active && !terminalsOnly(active)) return active.getId();
    let other: string | undefined;
    model.visitNodes(node => { if (node instanceof TabSetNode && !terminalsOnly(node)) other ??= node.getId(); });
    return other ?? active?.getId() ?? panelId;
  };
  for (const tab of open) {
    const id = sessionNodeId(tab);
    const name = label(tab);
    const node = model.getNodeById(id);
    if (!node) model.doAction(Actions.addTab({ type: "tab", id, name, component: "session" }, home(), DockLocation.CENTER, -1, false));
    else if (node instanceof TabNode && node.getName() !== name) model.doAction(Actions.renameTab(id, name));
  }
  for (const file of openFiles) {
    const id = fileNodeId(file);
    const name = fileLabel(file);
    const node = model.getNodeById(id);
    if (node instanceof TabNode) { if (node.getName() !== name) model.doAction(Actions.renameTab(id, name)); continue; }
    const target = home();
    if (!isChangesTab(file) && !isTerminalTab(file)) { model.doAction(Actions.addTab({ type: "tab", id, name, component: "editor" }, target, DockLocation.CENTER, -1, false)); continue; }
    // The pane that already holds a tab of the same kind.
    const alike = isChangesTab(file) ? changeIds : terminalIds;
    let beside: string | undefined;
    model.visitNodes(other => { if (other instanceof TabNode && other.getId() !== id && alike.has(other.getId())) beside ??= other.getParent()?.getId(); });
    if (isChangesTab(file)) {
      model.doAction(Actions.addTab({ type: "tab", id, name, component: "changes" }, beside ?? target, beside ? DockLocation.CENTER : DockLocation.RIGHT, -1, false));
      continue;
    }
    model.doAction(Actions.addTab({ type: "tab", id, name, component: "terminal" }, beside ?? target, beside ? DockLocation.CENTER : DockLocation.BOTTOM, -1, false));
    if (beside) continue;
    // The new pane takes a third of the height it shares with the pane above it.
    const pane = model.getNodeById(id)?.getParent();
    const row = pane?.getParent();
    if (pane instanceof TabSetNode && row instanceof RowNode && row.getChildren().length === 2) {
      model.doAction(Actions.adjustWeights(row.getId(), row.getChildren().map(child => child === pane ? 34 : 66)));
    }
  }
  if (!state.active) {
    const name = label(null);
    const draft = model.getNodeById(sessionDraftNodeId);
    if (!draft) model.doAction(Actions.addTab({ type: "tab", id: sessionDraftNodeId, name, component: "new-session",
      enableDrag: false, enableClose: open.length > 0 }, home(), DockLocation.CENTER, -1, true));
    else if (draft instanceof TabNode) {
      if (draft.getName() !== name) model.doAction(Actions.renameTab(sessionDraftNodeId, name));
      if (draft.isEnableClose() !== (open.length > 0)) model.doAction(Actions.updateNodeAttributes(sessionDraftNodeId, { enableClose: open.length > 0 }));
    }
  }
  // Add the replacement before removing the last tab of a tabset: FlexLayout may
  // otherwise delete the target tabset when a lone temporary tab becomes a session.
  for (const node of existing) {
    if (!ids.has(node.getId())) model.doAction(Actions.deleteTab(node.getId()));
  }
  const activeFile = files.active && openFiles.find(file => fileNodeId(file) === fileNodeId(files.active!));
  const active = activeFile ? fileNodeId(activeFile) : state.active ? sessionNodeId(state.active) : sessionDraftNodeId;
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

export type FileTabIntent = { kind: "select" | "close"; file: FileTab };
export function fileTabAction(action: Action, files: FileTabs, current: () => boolean): FileTabIntent | null {
  if (!current() || (action.type !== Actions.SELECT_TAB && action.type !== Actions.DELETE_TAB)) return null;
  const id = action.type === Actions.SELECT_TAB ? action.data.tabNode : action.data.node;
  const matches = files.open.slice(0, fileTabLimit).filter(file => fileNodeId(file) === id);
  return matches.length === 1 ? { kind: action.type === Actions.SELECT_TAB ? "select" : "close", file: matches[0] } : null;
}

export function ownsSessionTabContent(id: string, state: SessionTabs) {
  return !!state.active && id === sessionNodeId(state.active);
}

// Layout owns geometry and must apply accepted GUI actions before App projects focus.
// Unrecognized/mutation actions still cannot bypass catalog or captured-lifetime checks.
export function sessionLayoutActionAllowed(model: Model, action: Action, state: SessionTabs,
  snapshot: WorkspaceSnapshot | undefined, current: () => boolean, files: FileTabs = noFiles): boolean {
  if (!current() || !snapshot) return false;
  const valid = (node: TabNode) => node.getId() === sessionDraftNodeId && !state.active
    || !!fileTabAction(Actions.selectTab(node.getId()), files, current)
    || !!sessionTabAction(Actions.selectTab(node.getId()), state, snapshot, current);
  if (action.type === Actions.SELECT_TAB) return action.data.tabNode === sessionDraftNodeId && !state.active
    || !!fileTabAction(action, files, current) || !!sessionTabAction(action, state, snapshot, current);
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
  const movable = (node: TabNode) => node.getId() !== sessionDraftNodeId && valid(node);
  return source instanceof TabNode ? movable(source)
    : source instanceof TabSetNode && source.getTabNodes().length > 0 && source.getTabNodes().every(movable);
}
