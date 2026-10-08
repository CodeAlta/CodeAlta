import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Actions, DockLocation, Layout, TabNode, TabSetNode, type Action } from "flexlayout-react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { SessionTabMenu, type SessionMenuEntry } from "./SessionTabMenu";
import { useShellLanguage } from "./shellLanguage";
import type { TerminalItem, WorkspaceSnapshot } from "#neoastra";
import { resolveSessionTab, type SessionTab, type SessionTabs as Tabs } from "./sessionTabs";
import { SessionTabActivity, type RuntimeObservationControls } from "./RuntimeObservation";
import { createSessionTabModel, fileTabAction, reconcileSessionTabModel, sessionDraftNodeId, sessionLayoutActionAllowed, sessionNodeId, sessionTabAction } from "./sessionTabLayout";
import { emptyFileTabs, fileNodeId, isAutomationsTab, isChangesTab, isIssuesTab, isPluginTab, isSkillTab, isTerminalTab, isWorkItemsTab, sameFileTab, type FileTab, type FileTabs } from "./fileTabs";
import { ActivitySpinner } from "./ActivitySpinner";
import { terminalTabLabel } from "./terminal/terminals";
import { useSessionTabDrag } from "./useSessionTabDrag";
import { plainTitle } from "./sessionTitle";
import { useDraftIndicator, type DraftIndicators } from "./SessionDraftBadge";

export type SessionTabDrafts = Readonly<{ indicators: DraftIndicators; selectedId: string | null }>;

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
export function FileTabLabel({ tab, project, dirty, terminal }: {
  tab: FileTab; project: string; dirty: boolean;
  /** What the host says of the terminal of a terminal tab. */
  terminal?: TerminalItem;
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
  if (isWorkItemsTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={t("Work items")}>{t("Work items")}</span></span>;
  if (isIssuesTab(tab)) return <span className="session-tab-title"><span className="session-tab-label" title={t("Issues")}>{t("Issues")}</span></span>;
  const name = t(isChangesTab(tab) ? "Changes" : isPluginTab(tab) ? "Plugin" : isSkillTab(tab) ? "Skill" : "Editor");
  return <span className="session-tab-title"><span className="session-tab-label" title={`${name} · ${project}\n${tab.projectPath}`}>
    {name} <span className="session-tab-project">{project}</span></span>
    {dirty && !isChangesTab(tab) && <span className="session-tab-dirty" role="img" title={t("Unsaved changes")} aria-label={t("Unsaved changes")} />}</span>;
}

const noFiles = emptyFileTabs();

// Each pane retains its own live factory payload. App owns session authority and drafts.
// The code editors and the changes of projects are tabs of the same dock; App owns which are open and which one is active.
export function SessionTabStrip({ state, snapshot, drafts, select, close, reopen, observations, capture, children, renderSession, newSessionLabel,
  files = noFiles, renderFile, selectFile, closeFile, fileDirty, terminal, onSessionTabClick }: {
  state: Tabs; snapshot?: WorkspaceSnapshot;
  /** The edited prompts of the window and the selected session, for the mark of a session tab. */
  drafts?: SessionTabDrafts;
  select: (tab: SessionTab) => void; close: (tab: SessionTab) => void; reopen: () => void;
  observations?: RuntimeObservationControls;
  capture: () => () => boolean; children: ReactNode;
  renderSession?: (tab: SessionTab, visible: boolean) => ReactNode;
  newSessionLabel?: string;
  files?: FileTabs; renderFile?: (tab: FileTab, visible: boolean) => ReactNode;
  /** Activates a file tab, or with null returns to the session selection. */
  selectFile?: (tab: FileTab | null) => void;
  closeFile?: (tab: FileTab) => void; fileDirty?: (tab: FileTab) => boolean;
  /** What the host says of a terminal, for the tab that shows it. */
  terminal?: (id: string) => TerminalItem | undefined;
  /** A session tab or the New session tab was clicked (not its close button): the shell moves the focus to its prompt. */
  onSessionTabClick?: () => void;
}) {
  const { t } = useShellLanguage();
  const [model] = useState(createSessionTabModel);
  const root = useRef<HTMLDivElement>(null);
  const alive = useRef(true);
  const [menu, setMenu] = useState<{ anchor: HTMLElement; items: SessionMenuEntry[]; current: () => boolean } | null>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  // The tab bars along the top edge of the dock are the window's title bar: their empty space moves the
  // window, and the first and last one leave room for the application mark and the window controls.
  const markTitleBar = useRef(() => { });
  useLayoutEffect(() => {
    const host = root.current;
    if (!host) return;
    let frame = 0;
    const mark = () => {
      frame = 0;
      const bounds = host.getBoundingClientRect();
      host.querySelectorAll<HTMLElement>(".flexlayout__tabset_tabbar_outer").forEach(bar => {
        const box = bar.getBoundingClientRect();
        const top = box.width > 0 && Math.abs(box.top - bounds.top) < 2;
        bar.toggleAttribute("data-neoastra-drag-region", top);
        bar.toggleAttribute("data-titlebar-start", top && Math.abs(box.left - bounds.left) < 2);
        bar.toggleAttribute("data-titlebar-end", top && Math.abs(box.right - bounds.right) < 2);
      });
    };
    markTitleBar.current = () => { frame ||= requestAnimationFrame(mark); };
    mark();
    const resized = new ResizeObserver(markTitleBar.current);
    resized.observe(host);
    return () => { markTitleBar.current = () => { }; cancelAnimationFrame(frame); resized.disconnect(); };
  }, []);
  const label = (tab: SessionTab | null) => tab ? `${plainTitle(snapshot && resolveSessionTab(snapshot, tab)?.title || t("Unavailable session"))} - ${
    tab.projectId === null ? t("Chat") : snapshot?.projects.find(project => project.id === tab.projectId)?.name ?? t("Unavailable project")}` : newSessionLabel ?? t("New session");
  const projectName = (file: FileTab) => file.name ?? snapshot?.projects.find(project => project.id === file.projectId)?.name ?? t("Unavailable project");
  const shownTerminal = (file: FileTab) => isTerminalTab(file) && file.terminalId ? terminal?.(file.terminalId) : undefined;
  const fileLabel = (file: FileTab) => {
    if (isAutomationsTab(file)) return t("Automations");
    if (isWorkItemsTab(file)) return t("Work items");
    if (isIssuesTab(file)) return t("Issues");
    if (!isTerminalTab(file)) return `${t(isChangesTab(file) ? "Changes" : isPluginTab(file) ? "Plugin" : isSkillTab(file) ? "Skill" : "Editor")} · ${projectName(file)}`;
    const shown = shownTerminal(file);
    return shown ? terminalTabLabel(shown, t("Terminal")) : t("Terminal");
  };
  useLayoutEffect(() => { reconcileSessionTabModel(model, state, label, files, fileLabel); });
  useLayoutEffect(() => { if (menu && !menu.current()) setMenu(null); });
  function guard() {
    const current = capture();
    return () => alive.current && current();
  }
  function dispatch(action: Action, current = guard()) {
    if (action.type === Actions.DELETE_TAB && action.data.node === sessionDraftNodeId && !state.active && current()) {
      const tab = state.open.find(tab => snapshot && resolveSessionTab(snapshot, tab));
      if (tab) select(tab);
      return undefined;
    }
    if (sessionLayoutActionAllowed(model, action, state, snapshot, current, files)) return action;
    const file = fileTabAction(action, files, current);
    if (file?.kind === "close") { closeFile?.(file.file); return undefined; }
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
    const intent = sessionTabAction(Actions.selectTab(selected.getId()), state, snapshot, current);
    if (intent?.kind === "select" && (files.active || !state.active || sessionNodeId(state.active) !== selected.getId())) select(intent.tab);
    else if (files.active && selected.getId() === sessionDraftNodeId && !state.active && current()) selectFile?.(null);
  }
  function apply(action: Action, current = guard()) {
    const accepted = dispatch(action, current);
    // Layout's model listener calls onModelChange after the mutation, exactly once.
    if (accepted) model.doAction(accepted);
  }
  const drag = useSessionTabDrag(root, model, guard,
    (action, current) => sessionLayoutActionAllowed(model, action, state, snapshot, current, files), apply);
  function returnToSession(target: HTMLElement) {
    if (target.closest(".flexlayout__tab_button_trailing")) return false;
    const id = target.closest('[role="tab"]')?.querySelector<HTMLElement>('[data-session-node]')?.dataset.sessionNode;
    if (!id) return false;
    const current = guard();
    const file = fileTabAction(Actions.selectTab(id), files, current);
    if (file) { if (!sameFileTab(files.active, file.file)) selectFile?.(file.file); return true; }
    if (state.active && !files.active) return false;
    const intent = sessionTabAction(Actions.selectTab(id), state, snapshot, current);
    if (intent?.kind === "select") select(intent.tab);
    return intent?.kind === "select";
  }
  function more(anchor: HTMLElement, node: unknown) {
    const current = guard();
    if (!current()) return;
    const selected = node instanceof TabSetNode ? node.getSelectedNode() : undefined;
    const split = (location: DockLocation) => selected instanceof TabNode && node instanceof TabSetNode
      ? Actions.moveNode(selected.getId(), node.getId(), location, -1, true) : undefined;
    const right = split(DockLocation.RIGHT), below = split(DockLocation.BOTTOM);
    const canSplit = node instanceof TabSetNode && node.isEnableDivide() && node.getTabNodes().length > 1 && selected?.isEnableDrag();
    setMenu({ anchor, current, items: [
      { key: "split-right", label: t("Split right"), disabled: !canSplit || !right || !sessionLayoutActionAllowed(model, right, state, snapshot, current, files),
        onSelect: () => { if (right) apply(right, current); } },
      { key: "split-below", label: t("Split below"), disabled: !canSplit || !below || !sessionLayoutActionAllowed(model, below, state, snapshot, current, files),
        onSelect: () => { if (below) apply(below, current); } },
      { key: "reopen", label: t("Reopen closed tab"), disabled: !snapshot || !state.closed.length && !files.closed.length,
        onSelect: () => { if (current()) reopen(); } },
      { key: "refresh", label: t("Refresh statuses"), disabled: !observations?.enabled || !state.open.length,
        onSelect: () => { if (current()) observations?.refresh(state.open); } },
    ] });
  }
  return <div className="session-tabs workspace-layout" ref={root} data-dragging={!!drag.preview}
    onPointerDownCapture={drag.down} onPointerMoveCapture={drag.move} onPointerUpCapture={event => {
      // A click on a session tab (or the New session tab) hands the keyboard to that session's prompt.
      const clicked = drag.up(event);
      if (clicked && !clicked.startsWith("file:")) onSessionTabClick?.();
    }}
    onPointerCancelCapture={drag.end} onLostPointerCapture={drag.end}
    onDragStartCapture={event => {
      if ((event.target as HTMLElement).closest('.flexlayout__tab_button')) { event.preventDefault(); event.stopPropagation(); }
    }} onClickCapture={event => {
      if (drag.click(event.detail)) { event.preventDefault(); event.stopPropagation(); return; }
      returnToSession(event.target as HTMLElement);
    }} onKeyDownCapture={event => {
    if (drag.keyDown(event)) return;
    // Do not intercept the editor inside a factory; only the tab/menu chrome.
    const target = event.target as HTMLElement;
    if (!target.closest('[role="tablist"], [role="menu"], .session-tab-more')) return;
    if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) {
      event.preventDefault(); event.stopPropagation();
    } else if (event.key === "Enter" && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey
      && target.closest('[role="tab"]') && (returnToSession(target) || !state.active)) {
      event.preventDefault(); event.stopPropagation();
    }
  }}>
    <div className="session-dock"><Layout model={model} supportsPopout={false} invalidateTabContentOnParentRender={true}
      keyMap={{ closeTab: undefined, renameTab: undefined, focusTabToggle: undefined,
        focusNextTabset: undefined, focusPreviousTabset: undefined, closeOverlayBorder: undefined }}
      onAction={action => dispatch(action)} onModelChange={(_model, action) => changed(action)}
      onContextMenu={(_node, event) => event.preventDefault()}
      onRenderTab={(node, values) => {
        if (node.getId() === sessionDraftNodeId) { values.content = <span data-session-node={node.getId()}>{label(null)}</span>; return; }
        const file = files.open.find(value => fileNodeId(value) === node.getId());
        if (file) {
          const shown = shownTerminal(file);
          const look = isTerminalTab(file) ? { icon: "terminal" as const, tone: shown && !shown.running ? "muted" : "green" }
            : isChangesTab(file) ? { icon: "changes" as const, tone: "orange" }
            : isAutomationsTab(file) ? { icon: "automation" as const, tone: "gold" }
            : isWorkItemsTab(file) ? { icon: "task" as const, tone: "teal" }
            : isIssuesTab(file) ? { icon: "issueOpen" as const, tone: "green" }
            : isPluginTab(file) ? { icon: "plugin" as const, tone: "purple" }
            : isSkillTab(file) ? { icon: "skill" as const, tone: "teal" } : { icon: "code" as const, tone: "azure" };
          // A terminal whose shell runs a command shows it where its icon is.
          values.leading = shown?.running && shown.busy ? <span className="file-tab-icon" data-file-tone={look.tone}><ActivitySpinner size={13} /></span>
            : <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>;
          values.content = <span data-session-node={node.getId()}><FileTabLabel tab={file} dirty={!!fileDirty?.(file)} project={projectName(file)} terminal={shown} /></span>;
          return;
        }
        const tab = state.open.find(value => sessionNodeId(value) === node.getId());
        values.content = <span data-session-node={node.getId()}><SessionTabLabel label={label(tab ?? null)} path={tab?.path ?? null} drafts={drafts} sessionId={tab?.sessionId ?? null} /></span>;
        if (tab && observations) values.leading = <SessionTabActivity controls={observations} tab={tab} />;
      }}
      onRenderTabSet={(node, values) => values.buttons.push(<Button key="more" variant="minimal" size="small" className="session-tab-more"
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
        const file = files.open.find(value => fileNodeId(value) === node.getId());
        if (file) return renderFile ? <SessionTabContent node={node}>{visible => renderFile(file, visible)}</SessionTabContent> : null;
        const tab = state.open.find(tab => sessionNodeId(tab) === node.getId());
        return tab && renderSession ? <SessionTabContent node={node}>{visible => renderSession(tab, visible)}</SessionTabContent> : null;
      }} /></div>
    {drag.preview && <div className="session-drop-preview" aria-hidden="true" style={{ left: drag.preview.rect.x, top: drag.preview.rect.y,
      width: drag.preview.rect.width, height: drag.preview.rect.height }} />}
    {menu && root.current && <SessionTabMenu anchor={menu.anchor} items={menu.items} title={t("Open sessions")}
      container={root.current} current={menu.current} onClose={() => setMenu(null)} />}
  </div>;
}

function SessionTabContent({ node, children }: { node: TabNode; children: (visible: boolean) => ReactNode }) {
  const [visible, setVisible] = useState(node.isVisible());
  useLayoutEffect(() => {
    // FlexLayout memoizes hidden content, so factory props alone cannot stop its readers.
    const update = () => setVisible(node.isVisible());
    node.setEventListener("visibility", update);
    update();
    return () => node.removeEventListener("visibility");
  }, [node]);
  return <div className="session-tab-content">{children(visible)}</div>;
}
