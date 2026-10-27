import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Actions, DockLocation, Layout, TabNode, TabSetNode, type Action, type Model } from "flexlayout-react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { SessionTabMenu, type SessionMenuEntry } from "./SessionTabMenu";
import { useShellLanguage } from "./shellLanguage";
import type { TerminalItem, WorkspaceSnapshot } from "#neoastra";
import { isPreviewTab, resolveSessionTab, type DraftTab, type SessionTab } from "./sessionTabs";
import { SessionTabActivity, type RuntimeObservationControls } from "./RuntimeObservation";
import { SessionWaitingBadge } from "./WaitingBadge";
import { ElsewhereBadge } from "./sessionLeases/ElsewhereBadge";
import type { ElsewhereKind } from "./sessionLeases/sessionLeases";
import { createSessionTabModel, draftNodeId, draftTabAction, fileTabAction, reconcileSessionTabModel, sessionDraftNodeId, sessionLayoutActionAllowed, sessionNodeId, sessionTabAction, shownDraftNodeId, tabMiddleClickAction,
  type SessionTabView as Tabs } from "./sessionTabLayout";
import { ProviderIcon } from "./ProviderIcon";
import { emptyFileTabs, fileNodeId, isAutomationsTab, isWorkflowsTab, isWorkflowRunTab, isClaudeCodeTab, isBookmarksTab, isCanvasTab, isCanvasesTab, isDocumentationTab, isChangesTab, isIssuesTab, isPluginTab, isSkillTab, isTerminalTab, isWorkItemsTab, sameFileTab, type FileTab, type FileTabs } from "./fileTabs";
import { ActivitySpinner } from "./ActivitySpinner";
import { CanvasIcon } from "./canvases/CanvasIcon";
import { terminalTabLabel } from "./terminal/terminals";
import { useSessionTabDrag } from "./useSessionTabDrag";
import { plainTitle } from "./sessionTitle";
import { useDraftIndicator, type DraftIndicators } from "./SessionDraftBadge";
import { dockTab, dockTabs, tabsToClose, type DockTab, type DockTabs, type TabCloseCommand } from "./sessionTabClosing";
import { isSessionContextKey } from "./sessionRowActions";
import { takeKeyContextMenu } from "./keyContextMenu";

export type SessionTabDrafts = Readonly<{ indicators: DraftIndicators; selectedId: string | null }>;

/** The room the tabs of a pane need in the title row, beside the controls of the title bar; with less, they go below them. */
export const titleBarTabsRoom = 200;

type Edges = Readonly<{ left: number; right: number; top: number; width: number }>;

/**
 * Where the tab bar of a pane goes: in the title row when the pane is at the top of the window, padded at the start
 * under the brand and at the end under the window actions; below the title row, crowded, when what is left between
 * them is too narrow for its tabs (a narrow pane docked on the right would show a tab without its close button).
 * @param brandRight The right edge of the brand when it lies over the title row, else null.
 * @param actionsLeft The left edge of the window actions and controls, else null.
 */
export function titleBarPlace(box: Edges, bounds: Edges, brandRight: number | null, actionsLeft: number | null) {
  const top = box.width > 0 && Math.abs(box.top - bounds.top) < 2;
  const start = top && Math.abs(box.left - bounds.left) < 2;
  const end = top && Math.abs(box.right - bounds.right) < 2;
  const covered = (start && brandRight !== null ? Math.max(0, brandRight - box.left) : 0) + (end && actionsLeft !== null ? Math.max(0, box.right - actionsLeft) : 0);
  const crowded = top && box.width - covered < titleBarTabsRoom;
  return { top, start: start && !crowded, end: end && !crowded, crowded };
}

function markTitleBars(host: HTMLElement) {
  const bounds = host.getBoundingClientRect();
  const shell = host.closest(".workspace-shell");
  const over = (element: Element | null | undefined) => {
    const box = element?.getBoundingClientRect();
    return box && box.width > 0 && Math.abs(box.top - bounds.top) < 2 ? box : null;
  };
  const brand = over(shell?.querySelector(":scope > .window-brand"));
  const actions = [over(shell?.querySelector(":scope > .window-actions")), over(shell?.querySelector(":scope > .window-controls"))].filter(box => !!box);
  host.querySelectorAll<HTMLElement>(".flexlayout__tabset_tabbar_outer").forEach(bar => {
    // Reserved title-bar padding can exceed a narrow pane. Its stable bounds, not the padded bar's,
    // determine the edges; otherwise the end marker repeatedly removes and reapplies its own padding.
    const box = (bar.closest(".flexlayout__tabset") ?? bar).getBoundingClientRect();
    const place = titleBarPlace(box, bounds, brand ? brand.right : null, actions.length ? Math.min(...actions.map(value => value.left)) : null);
    bar.toggleAttribute("data-neoastra-drag-region", place.top);
    bar.toggleAttribute("data-titlebar-start", place.start);
    bar.toggleAttribute("data-titlebar-end", place.end);
    bar.toggleAttribute("data-titlebar-crowded", place.crowded);
  });
}

export function SessionTabLabel({ label, path, dirty: shown, drafts, sessionId = null }: {
  label: string; path: string | null;
  /** Whether the prompt of the session was edited, when the caller knows. */
  dirty?: boolean;
  /** The edited prompts of the window: the label follows the one of its session by itself. */
  drafts?: SessionTabDrafts; sessionId?: string | null;
}) {
  const { t } = useShellLanguage();
  const edited = useDraftIndicator(drafts?.indicators, sessionId, drafts?.selectedId ?? null);
  const dirty = shown ?? edited;
  return <span className="session-tab-title"><span className="session-tab-label" title={`${label}\n${path ?? ""}`}>{label}</span>
    {dirty && <span className="session-tab-dirty" role="img" title={t("Draft edited in this window")} aria-label={t("Draft edited in this window")} />}</span>;
}

/**
 * The header text of a project's tab: what it shows (its code editor or its changes) and the name of the project,
 * with the folder as tooltip. The editor carries the unsaved mark while one of its files holds edits.
 */
export function FileTabLabel({ tab, project, dirty, terminal, status }: {
  tab: FileTab; project: string; dirty: boolean;
  /** What the host says of the terminal of a terminal tab. */
  terminal?: TerminalItem;
  /** The status text that the plugin of a canvas tab gave: shown beside its title. */
  status?: string;
}) {
  const { t } = useShellLanguage();
  if (isTerminalTab(tab)) {
    // The title a terminal was given, or the folder it is in; a mark when it has ended or asks for attention.
    const label = terminal ? terminalTabLabel(terminal, t("Terminal")) : t("Terminal");
    const mark = !terminal ? null : !terminal.running ? ["ended", t("Ended (exit code {code})", { code: terminal.exitCode ?? 0 })] : terminal.attention ? ["attention", t("Asked for attention")] : null;
    return <span className="session-tab-title"><span className="session-tab-label" title={terminal ? `${label}\n${terminal.folder}\n${terminal.profileName}` : label}>{label}</span>
      {mark && <span className="terminal-tab-mark" data-kind={mark[0]} role="img" title={mark[1]} aria-label={mark[1]} />}</span>;
  }
  if (isAutomationsTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={t("Automations")}>{t("Automations")}</span></span>;
  if (isWorkflowsTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={t("Workflows")}>{t("Workflows")}</span></span>;
  if (isWorkflowRunTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={`${tab.name ?? tab.key ?? ""}
${tab.key ?? ""}`}>{tab.name ?? tab.key}</span></span>;
  if (isWorkItemsTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={t("Work items")}>{t("Work items")}</span></span>;
  if (isIssuesTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={t("Issues")}>{t("Issues")}</span></span>;
  if (isCanvasesTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={t("Canvases")}>{t("Canvases")}</span></span>;
  if (isDocumentationTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={t("Documentation")}>{t("Documentation")}</span></span>;
  if (isBookmarksTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={t("Bookmarks")}>{t("Bookmarks")}</span></span>;
  if (isClaudeCodeTab(tab)) {
    // A preview: the title of the conversation, in italics as the transient tab, the project in the tooltip.
    const title = plainTitle(tab.name || t("Claude Code"));
    return <span className="session-tab-title" data-transient="true"><span className="session-tab-label"
      title={`${title} - ${project}\n${t("Preview of a Claude Code conversation, not in CodeAlta yet")}`}>{title}</span></span>;
  }
  if (isCanvasTab(tab)) {
    // The title the plugin gave, with the project when the canvas is about one; the status beside it.
    const title = tab.name ?? t("Canvas"), about = tab.projectId ? project : "";
    return <span className="session-tab-title"><span className="session-tab-label" title={[`${title}${about ? ` · ${about}` : ""}`, status, tab.projectPath].filter(Boolean).join("\n")}>
      {title} {about && <span className="session-tab-project">{about}</span>}</span>
      {status && <span className="canvas-tab-status">{status}</span>}</span>;
  }
  const name = t(isChangesTab(tab) ? "Changes" : isPluginTab(tab) ? "Plugin" : isSkillTab(tab) ? "Skill" : "Editor");
  return <span className="session-tab-title"><span className="session-tab-label" title={`${name} · ${project}\n${tab.projectPath}`}>
    {name} <span className="session-tab-project">{project}</span></span>
    {dirty && !isChangesTab(tab) && <span className="session-tab-dirty" role="img" title={t("Unsaved changes")} aria-label={t("Unsaved changes")} />}</span>;
}

const noFiles = emptyFileTabs();

// Each pane retains its own live factory payload. App owns session authority and drafts.
// The code editors and the changes of projects are tabs of the same dock; App owns which are open and which one is active.
export function SessionTabStrip({ state, snapshot, drafts, select, close, reopen, observations, waiting, waitingBelow, elsewhere, capture, children, renderSession, newSessionLabel,
  files = noFiles, renderFile, selectFile, closeFile, fileDirty, fileStatus, terminal, onSessionTabClick, layout,
  keep, draftLabel, selectDraft, closeDraft, keepDraft, closePrompt, closeTabs, background, windowEntries, dragOut, externalDrag, arrival, onArrived }: {
  state: Tabs; snapshot?: WorkspaceSnapshot;
  /**
   * Sessions that have no tab and keep their pane all the same, out of sight: with a single tab, those that left it
   * while prompts wait above their composer. Their pane is rendered as the one of a tab that is not in front.
   */
  background?: readonly SessionTab[];
  /**
   * The model of the dock, when its owner keeps one: a dock that is taken away and shown again (the tabs of a
   * space) then has its panes where they were. Without it the dock has a model of its own.
   */
  layout?: Model;
  /** The edited prompts of the window and the selected session, for the mark of a session tab. */
  drafts?: SessionTabDrafts;
  select: (tab: SessionTab) => void; close: (tab: SessionTab) => void; reopen: () => void;
  observations?: RuntimeObservationControls;
  /** The sessions that wait for the user: their tab says so. */
  waiting?: ReadonlySet<string>;
  /** How many sessions under each session wait for the user. */
  waitingBelow?: ReadonlyMap<string, readonly unknown[]>;
  /** The sessions another process holds, with its kind: their tab says so. */
  elsewhere?: ReadonlyMap<string, Readonly<{ kind: ElsewhereKind }>>;
  capture: () => () => boolean; children: ReactNode;
  renderSession?: (tab: SessionTab, visible: boolean) => ReactNode;
  newSessionLabel?: string;
  files?: FileTabs; renderFile?: (tab: FileTab, visible: boolean) => ReactNode;
  /** Activates a file tab, or with null returns to the session selection. */
  selectFile?: (tab: FileTab | null) => void;
  closeFile?: (tab: FileTab) => void; fileDirty?: (tab: FileTab) => boolean;
  /** The status text that the plugin of a canvas tab gave it, shown beside its title. */
  fileStatus?: (tab: FileTab) => string | undefined;
  /** What the host says of a terminal, for the tab that shows it. */
  terminal?: (id: string) => TerminalItem | undefined;
  /** A session tab or the New session tab was clicked (not its close button): the shell moves the focus to its prompt. */
  onSessionTabClick?: () => void;
  /** Keeps the session of the transient tab, whose title was double-clicked: its tab is its own from now on. */
  keep?: (tab: SessionTab) => void;
  /** The title of the tab of the new-session prompt of a scope. */
  draftLabel?: (draft: DraftTab) => string;
  /** Shows the new-session prompt of a scope that has a tab of its own. */
  selectDraft?: (draft: DraftTab) => void;
  closeDraft?: (draft: DraftTab) => void;
  /** Gives the new-session prompt of the transient tab, whose title was double-clicked, a tab of its own. */
  keepDraft?: () => void;
  /** The transient tab of the new-session prompt is being closed: what is typed in it does not keep it open. */
  closePrompt?: () => void;
  /**
   * Closes several tabs of a pane at once, from the menu of a tab or of the pane. `from` is the tab the command was
   * asked from, which is shown when the tab in front is among those closed; null when every tab of the pane closes.
   */
  closeTabs?: (closing: DockTabs, from: DockTab | null) => void;
  /** What the menu of a tab offers of the windows of the application: moving the tab to another one, or to a new one. */
  windowEntries?: (tab: DockTab) => SessionMenuEntry[];
  /** What a tab dragged out of the window does: the window under the pointer is told, and a release there moves the tab. */
  dragOut?: Readonly<{ over: (tab: DockTab, point: Readonly<{ x: number; y: number }> | null) => void; drop: (tab: DockTab, point: Readonly<{ x: number; y: number }>) => void }>;
  /** A tab of another window dragged over this one, at a point of the page: where it would go, or why it would not. */
  externalDrag?: Readonly<{ x: number; y: number; refused: string | null }> | null;
  /** A tab that moved into this window from a drop at a point of the page: it goes to the pane there, once it is in the dock. */
  arrival?: Readonly<{ nodeId: string; x: number; y: number }> | null;
  onArrived?: () => void;
}) {
  const { t } = useShellLanguage();
  const [model] = useState(() => layout ?? createSessionTabModel());
  const root = useRef<HTMLDivElement>(null);
  const alive = useRef(true);
  const [menu, setMenu] = useState<{ anchor: HTMLElement; at?: Readonly<{ x: number; y: number }>; title?: string; items: SessionMenuEntry[]; current: () => boolean } | null>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  // The tab bars along the top edge of the dock are the window's title bar: their empty space moves the
  // window, and the first and last one leave room for the application mark and the window controls.
  const markTitleBar = useRef(() => { });
  // FlexLayout mounts its tab bars after our layout effect. Mark them in the commit that inserts their
  // toolbar, before its geometry observer measures them: a later 34 -> title-bar-height change makes
  // that observer resize an ancestor panel during descendant delivery, leaving a skipped notification.
  const markMountedTitleBar = useCallback((button: HTMLButtonElement | null) => {
    const host = button?.closest<HTMLElement>(".workspace-layout");
    if (host) markTitleBars(host);
  }, []);
  useLayoutEffect(() => {
    const host = root.current;
    if (!host) return;
    let frame = 0;
    const mark = () => {
      frame = 0;
      markTitleBars(host);
    };
    markTitleBar.current = () => { frame ||= requestAnimationFrame(mark); };
    mark();
    const resized = new ResizeObserver(markTitleBar.current);
    resized.observe(host);
    return () => { markTitleBar.current = () => { }; cancelAnimationFrame(frame); resized.disconnect(); };
  }, []);
  const label = (tab: SessionTab | null) => tab ? `${plainTitle(snapshot && resolveSessionTab(snapshot, tab)?.title || t("Unavailable session"))} - ${
    tab.projectId === null ? t("Chat") : snapshot?.projects.find(project => project.id === tab.projectId)?.name ?? t("Unavailable project")}` : newSessionLabel ?? t("New session");
  // The name of a canvas tab is its title: the project is the one of the catalog.
  const projectName = (file: FileTab) => (isCanvasTab(file) ? undefined : file.name) ?? snapshot?.projects.find(project => project.id === file.projectId)?.name ?? t("Unavailable project");
  const shownTerminal = (file: FileTab) => isTerminalTab(file) && file.terminalId ? terminal?.(file.terminalId) : undefined;
  const fileLabel = (file: FileTab) => {
    if (isAutomationsTab(file)) return t("Automations");
    if (isWorkflowsTab(file)) return t("Workflows");
    if (isWorkflowRunTab(file)) return `${file.name ?? file.key ?? ""} · ${t("Workflows")}`;
    if (isWorkItemsTab(file)) return t("Work items");
    if (isIssuesTab(file)) return t("Issues");
    if (isCanvasesTab(file)) return t("Canvases");
    if (isDocumentationTab(file)) return t("Documentation");
    if (isBookmarksTab(file)) return t("Bookmarks");
    if (isClaudeCodeTab(file)) return `${plainTitle(file.name || t("Claude Code"))} - ${projectName(file)}`;
    if (isCanvasTab(file)) return `${file.name ?? t("Canvas")}${file.projectId ? ` · ${projectName(file)}` : ""}`;
    if (!isTerminalTab(file)) return `${t(isChangesTab(file) ? "Changes" : isPluginTab(file) ? "Plugin" : isSkillTab(file) ? "Skill" : "Editor")} · ${projectName(file)}`;
    const shown = shownTerminal(file);
    return shown ? terminalTabLabel(shown, t("Terminal")) : t("Terminal");
  };
  const promptLabel = (draft: DraftTab) => draftLabel?.(draft) ?? label(null);
  useLayoutEffect(() => { reconcileSessionTabModel(model, state, label, files, fileLabel, promptLabel); });
  useLayoutEffect(() => { if (menu && !menu.current()) setMenu(null); });
  function guard() {
    const current = capture();
    return () => alive.current && current();
  }
  function dispatch(action: Action, current = guard()) {
    if (action.type === Actions.DELETE_TAB && action.data.node === sessionDraftNodeId && !state.active && current()) {
      // Closing the transient prompt shows another tab: a session, else a prompt that has a tab of its own.
      closePrompt?.();
      const tab = state.open.find(tab => snapshot && resolveSessionTab(snapshot, tab));
      if (tab) select(tab);
      else if (state.drafts?.length) selectDraft?.(state.drafts[0]);
      return undefined;
    }
    if (sessionLayoutActionAllowed(model, action, state, snapshot, current, files)) {
      // An asynchronous selection can still be pending while App owns the old tab.
      // Reselecting that tab is a new intent too; changed() deliberately ignores it.
      if (action.type === Actions.SELECT_TAB && !files.active && state.active && action.data.tabNode === sessionNodeId(state.active)) select(state.active);
      return action;
    }
    const file = fileTabAction(action, files, current);
    if (file?.kind === "close") { closeFile?.(file.file); return undefined; }
    const prompt = draftTabAction(action, state, snapshot, current);
    if (prompt?.kind === "close") { closeDraft?.(prompt.draft); return undefined; }
    const intent = sessionTabAction(action, state, snapshot, current);
    if (intent?.kind === "close") close(intent.tab);
    // App must accept closing a session or a file; unsupported actions never reach the model.
    return undefined;
  }
  function changed(action: Action) {
    markTitleBar.current();
    if (![Actions.SELECT_TAB, Actions.MOVE_NODE, Actions.SET_ACTIVE_TABSET].includes(action.type)) return;
    const selected = model.getActiveTabset()?.getSelectedNode();
    if (!selected) return;
    const current = guard();
    const file = fileTabAction(Actions.selectTab(selected.getId()), files, current);
    if (file) { if (!sameFileTab(files.active, file.file)) selectFile?.(file.file); return; }
    // The prompt of a scope that has a tab of its own: shown when it is not the one shown already.
    const prompt = draftTabAction(Actions.selectTab(selected.getId()), state, snapshot, current);
    if (prompt) { if (files.active || state.active || shownDraftNodeId(state) !== selected.getId()) selectDraft?.(prompt.draft); return; }
    const intent = sessionTabAction(Actions.selectTab(selected.getId()), state, snapshot, current);
    if (intent?.kind === "select" && (files.active || !state.active || sessionNodeId(state.active) !== selected.getId())) select(intent.tab);
    else if (files.active && selected.getId() === sessionDraftNodeId && !state.active && current()) selectFile?.(null);
  }
  function apply(action: Action, current = guard()) {
    const accepted = dispatch(action, current);
    // Layout's model listener calls onModelChange after the mutation, exactly once.
    if (accepted) model.doAction(accepted);
  }
  // The pane of the dock at a point of the page, and its rectangle in the dock.
  function paneAt(x: number, y: number): { pane: TabSetNode; rect: { x: number; y: number; width: number; height: number } } | null {
    const container = root.current;
    if (!container) return null;
    const bounds = container.getBoundingClientRect(), point = { x: x - bounds.x, y: y - bounds.y };
    let found: { pane: TabSetNode; rect: { x: number; y: number; width: number; height: number } } | null = null;
    model.visitNodes(node => {
      if (found || !(node instanceof TabSetNode)) return;
      const rect = node.getRect();
      if (point.x >= rect.x && point.x < rect.x + rect.width && point.y >= rect.y && point.y < rect.y + rect.height) found = { pane: node, rect: { x: rect.x, y: rect.y, width: rect.width, height: rect.height } };
    });
    return found;
  }
  const outTab = (node: TabNode) => dockTab(node.getId(), state, files);
  const drag = useSessionTabDrag(root, model, guard,
    (action, current) => sessionLayoutActionAllowed(model, action, state, snapshot, current, files), apply,
    dragOut ? { over: (node, point) => { const tab = outTab(node); if (tab) dragOut.over(tab, point); },
      drop: (node, point) => { const tab = outTab(node); if (tab) dragOut.drop(tab, point); } } : undefined);
  // A tab dropped here from another window goes to the pane it was dropped on, once the dock has it.
  useLayoutEffect(() => {
    if (!arrival) return;
    const node = model.getNodeById(arrival.nodeId);
    if (!(node instanceof TabNode)) return;
    const target = paneAt(arrival.x, arrival.y);
    if (target && node.getParent() !== target.pane) model.doAction(Actions.moveNode(node.getId(), target.pane.getId(), DockLocation.CENTER, -1, true));
    onArrived?.();
  });
  const external = externalDrag ? paneAt(externalDrag.x, externalDrag.y) : null;
  // A double click on the title of the transient tab keeps what it shows, as in a code editor: the session that was
  // looked at, or the prompt of a new session. The single tab of the sessions has nothing to keep.
  const lastClick = useRef<{ id: string; at: number } | null>(null);
  function keepNode(id: string) {
    if (state.single || !guard()()) return;
    if (id === sessionDraftNodeId) { if (!state.active) keepDraft?.(); return; }
    const tab = state.open.find(value => sessionNodeId(value) === id);
    if (tab && isPreviewTab(state, tab)) keep?.(tab);
  }
  function returnToSession(target: HTMLElement) {
    if (target.closest(".flexlayout__tab_button_trailing")) return false;
    const id = target.closest('[role="tab"]')?.querySelector<HTMLElement>('[data-session-node]')?.dataset.sessionNode;
    if (!id) return false;
    const current = guard();
    const file = fileTabAction(Actions.selectTab(id), files, current);
    if (file) { if (!sameFileTab(files.active, file.file)) selectFile?.(file.file); return true; }
    const prompt = draftTabAction(Actions.selectTab(id), state, snapshot, current);
    if (prompt) { if (files.active || state.active || shownDraftNodeId(state) !== id) selectDraft?.(prompt.draft); return true; }
    if (state.active && !files.active) return false;
    const intent = sessionTabAction(Actions.selectTab(id), state, snapshot, current);
    if (intent?.kind === "select") select(intent.tab);
    return intent?.kind === "select";
  }
  // Moving a tab of a pane to a new pane beside it or under it.
  function splitEntries(pane: TabSetNode | undefined, tab: TabNode | undefined, current: () => boolean): SessionMenuEntry[] {
    const split = (location: DockLocation) => tab && pane ? Actions.moveNode(tab.getId(), pane.getId(), location, -1, true) : undefined;
    const right = split(DockLocation.RIGHT), below = split(DockLocation.BOTTOM);
    const canSplit = !!pane && pane.isEnableDivide() && pane.getTabNodes().length > 1 && tab?.isEnableDrag();
    return [
      { key: "split-right", label: t("Split right"), disabled: !canSplit || !right || !sessionLayoutActionAllowed(model, right, state, snapshot, current, files),
        onSelect: () => { if (right) apply(right, current); } },
      { key: "split-below", label: t("Split below"), disabled: !canSplit || !below || !sessionLayoutActionAllowed(model, below, state, snapshot, current, files),
        onSelect: () => { if (below) apply(below, current); } },
    ];
  }
  // Closing several tabs of a pane, counted from one of them: the others, the ones after it, or all of them.
  function closeEntry(pane: TabSetNode, tab: TabNode | undefined, command: TabCloseCommand, label: string, current: () => boolean): SessionMenuEntry {
    const closing = dockTabs(tabsToClose(pane.getChildren().map(child => child.getId()), tab?.getId() ?? null, command), state, files);
    const from = tab && command !== "all" ? dockTab(tab.getId(), state, files) : null;
    return { key: `close-${command}`, label, disabled: !closeTabs || !snapshot || closing.sessions.length + closing.drafts.length + closing.files.length === 0,
      onSelect: () => { if (current()) closeTabs?.(closing, from); } };
  }
  function more(anchor: HTMLElement, node: unknown) {
    const current = guard();
    if (!current()) return;
    const pane = node instanceof TabSetNode ? node : undefined;
    const selected = pane?.getSelectedNode();
    const tab = selected instanceof TabNode ? selected : undefined;
    setMenu({ anchor, current, items: [
      ...splitEntries(pane, tab, current),
      ...(pane ? [{ key: "d-close", divider: true as const }, closeEntry(pane, tab, "others", t("Close others"), current), closeEntry(pane, tab, "all", t("Close all"), current)] : []),
      { key: "d-reopen", divider: true },
      { key: "reopen", label: t("Reopen closed tab"), disabled: !snapshot || !state.closed.length && !files.closed.length,
        onSelect: () => { if (current()) reopen(); } },
      { key: "refresh", label: t("Refresh statuses"), disabled: !observations?.enabled || !state.open.length,
        onSelect: () => { if (current()) observations?.refresh(state.open); } },
    ] });
  }
  // The menu of a tab, asked for with the other button of the pointer or the menu key: closing it, the others of its
  // pane, the ones after it or all of them; keeping the transient tab; moving the tab to a pane of its own.
  function tabMenu(node: TabNode, anchor: HTMLElement, pointer: Readonly<{ x: number; y: number }> | null) {
    const current = guard();
    const pane = node.getParent();
    if (!current() || !(pane instanceof TabSetNode)) return;
    const id = node.getId();
    const shown = dockTab(id, state, files);
    const transient = !state.single && (id === sessionDraftNodeId ? !state.active : !!shown && "session" in shown && isPreviewTab(state, shown.session));
    // The menu key asks at the tab itself: it has no place of the pointer.
    const box = anchor.getBoundingClientRect();
    const at = pointer && (pointer.x || pointer.y) ? pointer : { x: box.left, y: box.bottom };
    const items: SessionMenuEntry[] = [
      { key: "close", label: t("Close"), icon: "close", disabled: !node.isEnableClose(), onSelect: () => apply(Actions.deleteTab(id), current) },
      closeEntry(pane, node, "others", t("Close others"), current),
      closeEntry(pane, node, "right", t("Close to the right"), current),
      closeEntry(pane, node, "all", t("Close all"), current),
      ...(transient ? [{ key: "d-keep", divider: true as const }, { key: "keep", label: t("Keep open"), onSelect: () => keepNode(id) }] : []),
      ...(node.isEnableDrag() ? [{ key: "d-split", divider: true as const }, ...splitEntries(pane, node, current)] : []),
      ...(shown && windowEntries ? windowEntries(shown).map(entry => "divider" in entry ? entry : { ...entry, onSelect: () => { if (current()) entry.onSelect(); } }) : []),
    ];
    // The single tab of the sessions, alone in its pane with the prompt of a new session, has nothing to offer.
    if (state.single && items.every(item => "divider" in item || item.disabled)) return;
    setMenu({ anchor, at, title: node.getName(), current, items });
  }
  return <div className="session-tabs workspace-layout" ref={root} data-dragging={!!drag.preview}
    onPointerDownCapture={event => {
      // A press on a tab is the dock's, which may drag it: no mouse press follows for an open menu to close on.
      if (menu && !(event.target as HTMLElement).closest(".session-tab-popup")) setMenu(null);
      drag.down(event);
      drag.keepTabPress(event);
    }} onPointerMoveCapture={drag.move} onPointerUpCapture={event => {
      // A click on a session tab (or the New session tab) hands the keyboard to that session's prompt.
      const clicked = drag.up(event);
      if (clicked && !clicked.startsWith("file:")) onSessionTabClick?.();
      // A press on a tab is captured by the dock, which may be dragging it: no double click names the tab, so two
      // clicks of the same tab in a short time are one.
      const twice = !!clicked && lastClick.current?.id === clicked && event.timeStamp - lastClick.current.at < 500;
      lastClick.current = clicked && !twice ? { id: clicked, at: event.timeStamp } : null;
      if (twice) keepNode(clicked!);
    }}
    onPointerCancelCapture={drag.end} onLostPointerCapture={drag.end}
    onMouseDownCapture={event => {
      // A middle press on a tab closes it when released: it does not start scrolling the page.
      if (event.button === 1 && (event.target as HTMLElement).closest(".flexlayout__tab_button")) event.preventDefault();
    }}
    onDragStartCapture={event => {
      if ((event.target as HTMLElement).closest('.flexlayout__tab_button')) { event.preventDefault(); event.stopPropagation(); }
    }} onClickCapture={event => {
      if (drag.click(event.detail)) { event.preventDefault(); event.stopPropagation(); return; }
      returnToSession(event.target as HTMLElement);
    }} onDoubleClick={event => {
      // The tab of the transient prompt is not dragged, so its press is not captured: its double click arrives as one.
      const target = event.target as HTMLElement;
      if (target.closest(".flexlayout__tab_button_trailing")) return;
      const id = target.closest('[role="tab"]')?.querySelector<HTMLElement>('[data-session-node]')?.dataset.sessionNode;
      if (id) keepNode(id);
    }} onKeyDownCapture={event => {
    if (drag.keyDown(event)) return;
    // Do not intercept the editor inside a factory; only the tab/menu chrome.
    const target = event.target as HTMLElement;
    if (!target.closest('[role="tablist"], [role="menu"], .session-tab-more')) return;
    const tab = target.closest<HTMLElement>('[role="tab"]');
    if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) {
      event.preventDefault(); event.stopPropagation();
    } else if (tab && isSessionContextKey(event.key, event.shiftKey, false, false)) {
      // The menu key and Shift+F10 are read here: only some systems turn them into a request for a context menu,
      // and macOS never does. Where one follows, it is taken: the menu is open already, and asked a second time it
      // loses the keyboard to the prompt.
      const node = model.getNodeById(tab.querySelector<HTMLElement>("[data-session-node]")?.dataset.sessionNode ?? "");
      event.preventDefault(); event.stopPropagation();
      if (node instanceof TabNode) { takeKeyContextMenu(); tabMenu(node, tab, null); }
    } else if (event.key === "Enter" && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey
      && target.closest('[role="tab"]') && (returnToSession(target) || !state.active)) {
      event.preventDefault(); event.stopPropagation();
    }
  }}>
    <div className="session-dock"><Layout model={model} supportsPopout={false} invalidateTabContentOnParentRender={true}
      keyMap={{ closeTab: undefined, renameTab: undefined, focusTabToggle: undefined,
        focusNextTabset: undefined, focusPreviousTabset: undefined, closeOverlayBorder: undefined }}
      onAction={action => dispatch(action)} onModelChange={(_model, action) => changed(action)}
      onContextMenu={(node, event) => {
        event.preventDefault();
        if (node instanceof TabNode) tabMenu(node, event.currentTarget as HTMLElement, { x: event.clientX, y: event.clientY });
      }}
      onAuxMouseClick={(node, event) => {
        // A middle click closes the tab, as its close button does.
        const close = tabMiddleClickAction(node, event.button);
        if (!close) return;
        event.preventDefault();
        apply(close);
      }}
      onRenderTab={(node, values) => {
        // The transient tab says so with its title in italics: the prompt of the selected scope, or the session looked at.
        // The single tab of the sessions is not one: what it shows stays until something else is opened.
        if (node.getId() === sessionDraftNodeId) { values.content = <span data-session-node={node.getId()} data-transient={state.single ? undefined : "true"}>{label(null)}</span>; return; }
        const prompt = state.drafts?.find(value => draftNodeId(value) === node.getId());
        if (prompt) {
          values.leading = <span className="file-tab-icon" data-file-tone="teal"><AppIcon name="newSession" size={14} /></span>;
          values.content = <span data-session-node={node.getId()}><span className="session-tab-title"><span className="session-tab-label" title={`${promptLabel(prompt)}\n${prompt.path ?? ""}`}>{promptLabel(prompt)}</span></span></span>;
          return;
        }
        const file = files.open.find(value => fileNodeId(value) === node.getId());
        if (file) {
          const shown = shownTerminal(file);
          const look = isTerminalTab(file) ? { icon: "terminal" as const, tone: shown && !shown.running ? "muted" : "green" }
            : isChangesTab(file) ? { icon: "changes" as const, tone: "orange" }
            : isAutomationsTab(file) ? { icon: "automation" as const, tone: "gold" }
            : isWorkflowsTab(file) || isWorkflowRunTab(file) ? { icon: "workflow" as const, tone: "blue" }
            : isWorkItemsTab(file) ? { icon: "task" as const, tone: "teal" }
            : isIssuesTab(file) ? { icon: "issueOpen" as const, tone: "green" }
            : isCanvasesTab(file) ? { icon: "canvases" as const, tone: "purple" }
            : isDocumentationTab(file) ? { icon: "documentation" as const, tone: "azure" }
            : isBookmarksTab(file) ? { icon: "bookmark" as const, tone: "gold" }
            : isPluginTab(file) ? { icon: "plugin" as const, tone: "purple" }
            : isSkillTab(file) ? { icon: "skill" as const, tone: "teal" } : { icon: "code" as const, tone: "azure" };
          // A terminal whose shell runs a command shows it where its icon is.
          values.leading = shown?.running && shown.busy ? <span className="file-tab-icon" data-file-tone={look.tone}><ActivitySpinner size={13} /></span>
            : isCanvasTab(file) ? <span className="file-tab-icon" data-file-tone="purple"><CanvasIcon name={file.icon} pluginKey={file.pluginKey} size={14} /></span>
            // Not a session of CodeAlta yet: the logo of its provider, dimmed, as in the list.
            : isClaudeCodeTab(file) ? <span className="file-tab-icon claude-code-tab-icon"><ProviderIcon providerKey={file.icon ?? null} size={14} fallback="assistant" /></span>
            : <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>;
          values.content = <span data-session-node={node.getId()}><FileTabLabel tab={file} dirty={!!fileDirty?.(file)} project={projectName(file)} terminal={shown} status={fileStatus?.(file)} /></span>;
          return;
        }
        const tab = state.open.find(value => sessionNodeId(value) === node.getId());
        values.content = <span data-session-node={node.getId()} data-transient={isPreviewTab(state, tab) ? "true" : undefined}><SessionTabLabel label={label(tab ?? null)} path={tab?.path ?? null} drafts={drafts} sessionId={tab?.sessionId ?? null} /></span>;
        // A session that waits for the user says so before anything else: its run goes nowhere until it is answered.
        if (tab?.sessionId && waiting?.has(tab.sessionId)) values.leading = <SessionWaitingBadge waiting />;
        // One another process holds says so: it is read-only here meanwhile.
        else if (tab?.sessionId && elsewhere?.has(tab.sessionId)) values.leading = <ElsewhereBadge kind={elsewhere.get(tab.sessionId)!.kind} />;
        // So does one whose sub-sessions wait: its work waits for theirs.
        else if (tab?.sessionId && waitingBelow?.get(tab.sessionId)?.length) values.leading = <SessionWaitingBadge waiting={false} below={waitingBelow.get(tab.sessionId)!.length} />;
        else if (tab && observations) values.leading = <SessionTabActivity controls={observations} tab={tab} />;
      }}
      onRenderTabSet={(node, values) => values.buttons.push(<Button key="more" ref={markMountedTitleBar} variant="minimal" size="small" className="session-tab-more"
        icon={<AppIcon name="ellipsis" size={16} />} aria-label={t("Open sessions")} aria-haspopup="menu"
        aria-expanded={!!menu && menu.anchor.dataset.tabset === node.getId()} data-tabset={node.getId()}
        onClick={event => more(event.currentTarget, node)} />)}
      onShowOverflowMenu={(_node, event, items) => {
        const current = guard();
        if (!current()) return;
        setMenu({ anchor: event.currentTarget as HTMLElement, current, items: items.map(item => ({
          key: item.node.getId(), label: item.node.getName(),
          onSelect: () => {
            // External menus are outside Layout's onAction pipeline; apply the accepted action here.
            apply(Actions.selectTab(item.node.getId()), current);
          },
        })) });
      }}
      factory={node => {
        if (node.getId() === sessionDraftNodeId) return <div className="session-tab-content">{children}</div>;
        // A prompt with a tab of its own is written in when its scope is the selected one and no session is shown;
        // otherwise its tab says what it is, and showing it selects its scope.
        const prompt = state.drafts?.find(value => draftNodeId(value) === node.getId());
        if (prompt) return <div className="session-tab-content">{!state.active && shownDraftNodeId(state) === node.getId() ? children
          : <div className="session-draft-placeholder"><AppIcon name="newSession" size={28} /><span>{promptLabel(prompt)}</span></div>}</div>;
        const file = files.open.find(value => fileNodeId(value) === node.getId());
        if (file) return renderFile ? <SessionTabContent node={node}>{visible => renderFile(file, visible)}</SessionTabContent> : null;
        const tab = state.open.find(tab => sessionNodeId(tab) === node.getId());
        return tab && renderSession ? <SessionTabContent node={node} retained={!state.active || sessionNodeId(state.active) !== node.getId()}>{visible => renderSession(tab, visible)}</SessionTabContent> : null;
      }} /></div>
    {renderSession && !!background?.length && <div className="session-background-panes" hidden>
      {background.map(tab => <div key={sessionNodeId(tab)} className="session-tab-content">{renderSession(tab, false)}</div>)}</div>}
    {drag.preview && <div className="session-drop-preview" aria-hidden="true" style={{ left: drag.preview.rect.x, top: drag.preview.rect.y,
      width: drag.preview.rect.width, height: drag.preview.rect.height }} />}
    {externalDrag && external && <div className="window-drop-indicator" data-refused={externalDrag.refused ? "true" : undefined} role="status"
      style={{ left: external.rect.x, top: external.rect.y, width: external.rect.width, height: external.rect.height }}>
      {externalDrag.refused && <span>{externalDrag.refused}</span>}</div>}
    {menu && root.current && <SessionTabMenu anchor={menu.anchor} at={menu.at} items={menu.items} title={menu.title ?? t("Open sessions")}
      container={root.current} current={menu.current} onClose={() => setMenu(null)} />}
  </div>;
}

function SessionTabContent({ node, children, retained = false }: {
  node: TabNode; children: (visible: boolean) => ReactNode;
  /**
   * A hidden tab keeps what it rendered when it was hidden, until it is shown again. The window is rendered
   * for many reasons that change nothing of a session nobody sees, and each hidden pane took its share of
   * every one of them: the cost of a render of the window grew with the number of open tabs.
   * The tab of the selected session is never kept, even behind the tab of a file: it holds what the window
   * shares with the selected session (the triggers of its dialogs), and gives it up in the render where
   * another session takes it.
   */
  retained?: boolean;
}) {
  const [visible, setVisible] = useState(node.isVisible());
  // What is kept is a pane that was rendered: while there is nothing to show yet (the catalog is being read),
  // the tab is asked again.
  const hidden = useRef<ReactNode>(null);
  if (visible || !retained) hidden.current = null;
  else hidden.current ??= children(false);
  useLayoutEffect(() => {
    // FlexLayout memoizes hidden content, so factory props alone cannot stop its readers.
    const update = () => setVisible(node.isVisible());
    node.setEventListener("visibility", update);
    update();
    return () => node.removeEventListener("visibility");
  }, [node]);
  return <div className="session-tab-content">{hidden.current ?? children(visible)}</div>;
}
