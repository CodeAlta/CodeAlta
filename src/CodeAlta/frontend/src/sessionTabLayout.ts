import { Actions, DockLocation, Model, RowNode, TabNode, TabSetNode, type Action, type Node as LayoutNode } from "flexlayout-react";
import type { WorkspaceSnapshot } from "#neoastra";
import { draftKey, draftTabLimit, draftTabOf, hasDraftTab, isPreviewTab, openSessionTab, reconcileSessionTabs, resolveDraftTab, resolveSessionTab, selectedTab, sessionTabLimit, singleSessionTab, tabKey,
  type DraftTab, type SessionTab, type SessionTabMode, type SessionTabs } from "./sessionTabs";
import { emptyFileTabs, fileNodeId, fileTabLimit, isChangesTab, isClaudeCodeTab, isTerminalTab, type FileTab, type FileTabs } from "./fileTabs";

const panelId = "session-tabs-panel";
// Presentation only: never persisted in the session list, recent history or draft owners.
export const sessionDraftNodeId = "session-draft";
export const sessionNodeId = (tab: SessionTab) => `session:${tabKey(tab)}`;
/** The node of the new-session prompt of a scope that has a tab of its own. */
export const draftNodeId = (draft: DraftTab) => `draft:${draftKey(draft)}`;

/**
 * The tabs as the dock shows them: the tabs of the window, and `scope`, the scope whose new-session prompt is the one
 * shown when no session is. That prompt is in the tab of its own when its scope has one, in the transient one otherwise.
 * With `single` the sessions share one tab: the session shown or that prompt, and nothing in it is transient.
 */
export type SessionTabView = SessionTabs & Readonly<{ scope?: DraftTab | null; single?: boolean }>;
/** The node that shows the new-session prompt when no session is shown: the tab of the scope, or the transient one. */
export const shownDraftNodeId = (state: SessionTabView) => state.scope && hasDraftTab(state, state.scope) ? draftNodeId(state.scope) : sessionDraftNodeId;

// Project App's selection synchronously: its persistence effect can lag a render.
// Never rebind an invalid formerly active identity to a reused session ID.
// A selected session that has no tab yet is looked at: it takes the transient tab, unless it is opened to be kept.
// With a single tab (`single`) the selected session is the only one open, and the prompt shown without one leaves none.
export function sessionTabPresentation(state: SessionTabs, snapshot: WorkspaceSnapshot | undefined,
  projectId: string | null, sessionId: string | null, mode: SessionTabMode = "preview"): SessionTabView {
  const single = mode === "single";
  // While the catalog is not read the tabs stay what they are: nothing says yet which session is shown.
  if (!snapshot) return single ? { ...state, active: null, single } : { ...state, active: null };
  const shape = (view: SessionTabView): SessionTabView => single ? { ...singleSessionTab(view), single } : view;
  const valid = reconcileSessionTabs(state, snapshot);
  const scope = draftTabOf(snapshot, projectId);
  if (state.active?.sessionId === sessionId && !resolveSessionTab(snapshot, state.active)) return shape({ ...valid, active: null, scope });
  const selected = selectedTab(snapshot, projectId, sessionId);
  return shape({ ...(selected ? openSessionTab(valid, selected, mode) : { ...valid, active: null }), scope });
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
//
// One tab is transient: the session that is looked at, or the new-session prompt of the selected scope. The preview of a
// Claude Code conversation is another one: the next preview takes its place, and so does the session it becomes. What becomes
// the transient tab takes the place of the one that leaves, in its pane and at its position, as the preview tab of a
// code editor does; so does the prompt that is given a tab of its own. Nodes say whether they are transient in their
// `config`, which is how the one that leaves is found. The single tab of the sessions is replaced the same way: the
// session or the prompt that takes it is where the one that leaves was.
export function reconcileSessionTabModel(model: Model, state: SessionTabView, label: (tab: SessionTab | null) => string, files: FileTabs = noFiles,
  fileLabel: (file: FileTab) => string = file => file.view, draftLabel: (draft: DraftTab) => string = () => label(null)) {
  const open = state.open.slice(0, sessionTabLimit);
  const openFiles = files.open.slice(0, fileTabLimit);
  const drafts = (state.drafts ?? []).slice(0, draftTabLimit);
  const ids = new Set([...open.map(sessionNodeId), ...openFiles.map(fileNodeId), ...drafts.map(draftNodeId)]);
  const shownDraft = shownDraftNodeId(state);
  // The transient prompt: no session is shown, and the prompt of the scope has no tab of its own.
  const transientDraft = !state.active && shownDraft === sessionDraftNodeId;
  if (transientDraft) ids.add(sessionDraftNodeId);
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
  // Where the transient tab that leaves is: what takes its place is added there, once.
  let left = existing.find(node => !ids.has(node.getId()) && node.getConfig()?.preview === true);
  const place = () => {
    const pane = left?.getParent();
    const at = pane instanceof TabSetNode ? { pane: pane.getId(), index: pane.getChildren().indexOf(left!) } : { pane: home(), index: -1 };
    left = undefined;
    return at;
  };
  // Where the preview of a Claude Code conversation that leaves is: the next preview takes its place, and so does the
  // session that continues it once it is imported.
  let leftConversation = existing.find(node => !ids.has(node.getId()) && node.getConfig()?.claudeCodePreview === true);
  const conversationPlace = () => {
    const pane = leftConversation?.getParent();
    const at = pane instanceof TabSetNode ? { pane: pane.getId(), index: pane.getChildren().indexOf(leftConversation!) } : { pane: home(), index: -1 };
    leftConversation = undefined;
    return at;
  };
  for (const tab of open) {
    const id = sessionNodeId(tab);
    const name = label(tab);
    const preview = !!state.single || isPreviewTab(state, tab);
    const node = model.getNodeById(id);
    if (!node) {
      const at = preview ? place() : leftConversation ? conversationPlace() : { pane: home(), index: -1 };
      model.doAction(Actions.addTab({ type: "tab", id, name, component: "session", config: { preview } }, at.pane, DockLocation.CENTER, at.index, false));
    } else if (node instanceof TabNode) {
      if (node.getName() !== name) model.doAction(Actions.renameTab(id, name));
      if ((node.getConfig()?.preview === true) !== preview) model.doAction(Actions.updateNodeAttributes(id, { config: { preview } }));
    }
  }
  // What is in the transient tab now: the prompt of the scope, or the session that is looked at. A prompt that gets a
  // tab of its own while it is not the one shown is the one that was just left with something typed in it: it takes
  // that place, and what replaced it comes after it.
  const lookedAt = open.find(tab => isPreviewTab(state, tab));
  const replaced = model.getNodeById(transientDraft ? sessionDraftNodeId : lookedAt ? sessionNodeId(lookedAt) : "");
  for (const draft of drafts) {
    const id = draftNodeId(draft);
    const name = draftLabel(draft);
    const node = model.getNodeById(id);
    if (node instanceof TabNode) { if (node.getName() !== name) model.doAction(Actions.renameTab(id, name)); continue; }
    // The prompt that was in the transient tab keeps its place when it is given a tab of its own.
    const beside = replaced?.getParent();
    const at = !state.active && id === shownDraft ? place()
      : beside instanceof TabSetNode ? { pane: beside.getId(), index: beside.getChildren().indexOf(replaced!) } : { pane: home(), index: -1 };
    model.doAction(Actions.addTab({ type: "tab", id, name, component: "new-session", config: { preview: false } }, at.pane, DockLocation.CENTER, at.index, false));
  }
  for (const file of openFiles) {
    const id = fileNodeId(file);
    const name = fileLabel(file);
    const node = model.getNodeById(id);
    if (node instanceof TabNode) { if (node.getName() !== name) model.doAction(Actions.renameTab(id, name)); continue; }
    if (isClaudeCodeTab(file)) {
      const at = leftConversation ? conversationPlace() : { pane: home(), index: -1 };
      model.doAction(Actions.addTab({ type: "tab", id, name, component: "claude-code-preview", config: { claudeCodePreview: true } }, at.pane, DockLocation.CENTER, at.index, false));
      continue;
    }
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
  if (transientDraft) {
    const name = label(null);
    const closable = open.length + drafts.length > 0;
    const draft = model.getNodeById(sessionDraftNodeId);
    if (!draft) {
      const at = place();
      model.doAction(Actions.addTab({ type: "tab", id: sessionDraftNodeId, name, component: "new-session", config: { preview: true },
        enableDrag: false, enableClose: closable }, at.pane, DockLocation.CENTER, at.index, true));
    } else if (draft instanceof TabNode) {
      if (draft.getName() !== name) model.doAction(Actions.renameTab(sessionDraftNodeId, name));
      if (draft.isEnableClose() !== closable) model.doAction(Actions.updateNodeAttributes(sessionDraftNodeId, { enableClose: closable }));
    }
  }
  // Add the replacement before removing the last tab of a tabset: FlexLayout may
  // otherwise delete the target tabset when a lone temporary tab becomes a session.
  for (const node of existing) {
    if (!ids.has(node.getId())) model.doAction(Actions.deleteTab(node.getId()));
  }
  const activeFile = files.active && openFiles.find(file => fileNodeId(file) === fileNodeId(files.active!));
  const active = activeFile ? fileNodeId(activeFile) : state.active ? sessionNodeId(state.active) : shownDraft;
  const node = ids.has(active) ? model.getNodeById(active) : undefined;
  if (node instanceof TabNode && !node.isSelected()) model.doAction(Actions.selectTab(node.getId()));
  if (node instanceof TabNode && node.getParent() && model.getActiveTabset() !== node.getParent())
    model.doAction(Actions.setActiveTabset(node.getParent()!.getId()));
}

/** The node of the tab in front: the active tab of a project, else the session shown, else the prompt shown. */
export function activeDockNodeId(state: SessionTabView, files: FileTabs): string {
  return files.active ? fileNodeId(files.active) : state.active ? sessionNodeId(state.active) : shownDraftNodeId(state);
}

/**
 * The tab `delta` places after `current` among the tabs of the dock, around them: in the order they are shown, pane
 * after pane as the layout places them, and in a pane from left to right. Only tabs that are there are gone through,
 * so going through them opens none. Null when there is no other tab.
 */
export function cycleDockTab(model: Model, current: string, delta: 1 | -1): string | null {
  const order: string[] = [];
  model.visitNodes(node => { if (node instanceof TabNode && node.getParent() instanceof TabSetNode) order.push(node.getId()); });
  const at = order.indexOf(current);
  if (order.length === 0 || at >= 0 && order.length === 1) return null;
  return order[at < 0 ? (delta > 0 ? 0 : order.length - 1) : (at + delta + order.length) % order.length];
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

/** What an action of the dock asks of the new-session prompt of a scope that has a tab of its own. */
export type DraftTabIntent = { kind: "select" | "close"; draft: DraftTab };
export function draftTabAction(action: Action, state: SessionTabs, snapshot: WorkspaceSnapshot | undefined, current: () => boolean): DraftTabIntent | null {
  if (!current() || !snapshot || (action.type !== Actions.SELECT_TAB && action.type !== Actions.DELETE_TAB)) return null;
  const id = action.type === Actions.SELECT_TAB ? action.data.tabNode : action.data.node;
  const matches = (state.drafts ?? []).slice(0, draftTabLimit).filter(draft => draftNodeId(draft) === id);
  return matches.length === 1 && resolveDraftTab(snapshot, matches[0]) ? { kind: action.type === Actions.SELECT_TAB ? "select" : "close", draft: matches[0] } : null;
}

export type FileTabIntent = { kind: "select" | "close"; file: FileTab };
export function fileTabAction(action: Action, files: FileTabs, current: () => boolean): FileTabIntent | null {
  if (!current() || (action.type !== Actions.SELECT_TAB && action.type !== Actions.DELETE_TAB)) return null;
  const id = action.type === Actions.SELECT_TAB ? action.data.tabNode : action.data.node;
  const matches = files.open.slice(0, fileTabLimit).filter(file => fileNodeId(file) === id);
  return matches.length === 1 ? { kind: action.type === Actions.SELECT_TAB ? "select" : "close", file: matches[0] } : null;
}

/**
 * What a click of another button than the main one on a node of the dock does: the middle button closes a tab
 * as its close button does, where it has one. A click with a key held, another button or the bar of a pane does nothing.
 */
export function tabMiddleClickAction(node: LayoutNode, button: number): Action | null {
  return button === 1 && node instanceof TabNode && node.isEnableClose() ? Actions.deleteTab(node.getId()) : null;
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
    || !!draftTabAction(Actions.selectTab(node.getId()), state, snapshot, current)
    || !!sessionTabAction(Actions.selectTab(node.getId()), state, snapshot, current);
  if (action.type === Actions.SELECT_TAB) return action.data.tabNode === sessionDraftNodeId && !state.active
    || !!fileTabAction(action, files, current) || !!draftTabAction(action, state, snapshot, current)
    || !!sessionTabAction(action, state, snapshot, current);
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
