import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Actions, DockLocation, Layout, TabNode, TabSetNode, type Action } from "flexlayout-react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { SessionTabMenu, type SessionMenuEntry } from "./SessionTabMenu";
import { useShellLanguage } from "./shellLanguage";
import type { WorkspaceSnapshot } from "#neoastra";
import { resolveSessionTab, type SessionTab, type SessionTabs as Tabs } from "./sessionTabs";
import { SessionTabActivity, type RuntimeObservationControls } from "./RuntimeObservation";
import { createSessionTabModel, fileTabAction, reconcileSessionTabModel, sessionDraftNodeId, sessionLayoutActionAllowed, sessionNodeId, sessionTabAction } from "./sessionTabLayout";
import { emptyFileTabs, fileNodeId, isChangesTab, sameFileTab, type FileTab, type FileTabs } from "./fileTabs";
import { useSessionTabDrag } from "./useSessionTabDrag";
import { plainTitle } from "./sessionTitle";

export function SessionTabLabel({ label, path, dirty }: { label: string; path: string | null; dirty: boolean }) {
  const { t } = useShellLanguage();
  return <span className="session-tab-title"><span className="session-tab-label" title={`${label}\n${path ?? ""}`}>{label}</span>
    {dirty && <span className="session-tab-dirty" role="img" title={t("Draft edited in this window")} aria-label={t("Draft edited in this window")} />}</span>;
}

/**
 * The header text of a project's tab: what it shows (its code editor or its changes) and the name of the project,
 * with the folder as tooltip. The editor carries the unsaved mark while one of its files holds edits.
 */
export function FileTabLabel({ tab, project, dirty }: { tab: FileTab; project: string; dirty: boolean }) {
  const { t } = useShellLanguage();
  const name = t(isChangesTab(tab) ? "Changes" : "Editor");
  return <span className="session-tab-title"><span className="session-tab-label" title={`${name} · ${project}\n${tab.projectPath}`}>
    {name} <span className="session-tab-project">{project}</span></span>
    {dirty && !isChangesTab(tab) && <span className="session-tab-dirty" role="img" title={t("Unsaved changes")} aria-label={t("Unsaved changes")} />}</span>;
}

const noFiles = emptyFileTabs();

// Each pane retains its own live factory payload. App owns session authority and drafts.
// The code editors and the changes of projects are tabs of the same dock; App owns which are open and which one is active.
export function SessionTabStrip({ state, snapshot, dirty, select, close, reopen, observations, capture, children, renderSession, newSessionLabel,
  files = noFiles, renderFile, selectFile, closeFile, fileDirty, onSessionTabClick }: {
  state: Tabs; snapshot?: WorkspaceSnapshot; dirty: (id: string) => boolean;
  select: (tab: SessionTab) => void; close: (tab: SessionTab) => void; reopen: () => void;
  observations?: RuntimeObservationControls;
  capture: () => () => boolean; children: ReactNode;
  renderSession?: (tab: SessionTab, visible: boolean) => ReactNode;
  newSessionLabel?: string;
  files?: FileTabs; renderFile?: (tab: FileTab, visible: boolean) => ReactNode;
  /** Activates a file tab, or with null returns to the session selection. */
  selectFile?: (tab: FileTab | null) => void;
  closeFile?: (tab: FileTab) => void; fileDirty?: (tab: FileTab) => boolean;
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
    tab.projectId === null ? t("Global") : snapshot?.projects.find(project => project.id === tab.projectId)?.name ?? t("Unavailable project")}` : newSessionLabel ?? t("New session");
  const projectName = (file: FileTab) => snapshot?.projects.find(project => project.id === file.projectId)?.name ?? t("Unavailable project");
  const fileLabel = (file: FileTab) => `${t(isChangesTab(file) ? "Changes" : "Editor")} · ${projectName(file)}`;
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
          const look = isChangesTab(file) ? { icon: "changes" as const, tone: "orange" } : { icon: "code" as const, tone: "azure" };
          values.leading = <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>;
          values.content = <span data-session-node={node.getId()}><FileTabLabel tab={file} dirty={!!fileDirty?.(file)} project={projectName(file)} /></span>;
          return;
        }
        const tab = state.open.find(value => sessionNodeId(value) === node.getId());
        values.content = <span data-session-node={node.getId()}><SessionTabLabel label={label(tab ?? null)} path={tab?.path ?? null} dirty={!!tab && dirty(tab.sessionId)} /></span>;
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
