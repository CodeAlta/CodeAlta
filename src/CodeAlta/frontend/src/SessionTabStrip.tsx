import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Actions, Layout, PopupMenu, type Action, type PopupMenuEntry } from "flexlayout-react";
import { useShellLanguage } from "./shellLanguage";
import type { WorkspaceSnapshot } from "#neoastra";
import { resolveSessionTab, type SessionTab, type SessionTabs as Tabs } from "./sessionTabs";
import { RuntimeObservationBadge, type RuntimeObservationControls } from "./RuntimeObservation";
import { createSessionTabModel, draftTabId, ownsSessionTabContent, reconcileSessionTabModel, sessionNodeId, sessionTabAction } from "./sessionTabLayout";

export function SessionTabLabel({ label, path, dirty }: { label: string; path: string | null; dirty: boolean }) {
  const { t } = useShellLanguage();
  return <><span className="session-tab-label" title={`${label}\n${path ?? ""}`}>{label}</span>
    {dirty && <span title={t("Draft edited in this window")} aria-label={t("Draft edited in this window")}>●</span>}</>;
}

// Real session nodes, but only one live factory payload. App owns all session state.
export function SessionTabStrip({ state, snapshot, dirty, select, close, reopen, selectDraft, observations, capture, children }: {
  state: Tabs; snapshot?: WorkspaceSnapshot; dirty: (id: string) => boolean;
  select: (tab: SessionTab) => void; close: (tab: SessionTab) => void; reopen: () => void;
  selectDraft: () => void; observations?: RuntimeObservationControls;
  capture: () => () => boolean; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  const [model] = useState(createSessionTabModel);
  const root = useRef<HTMLDivElement>(null);
  const alive = useRef(true);
  const [menu, setMenu] = useState<{ anchor: HTMLElement; items: PopupMenuEntry[]; current: () => boolean } | null>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const label = (tab: SessionTab | null) => tab ? `${snapshot && resolveSessionTab(snapshot, tab)?.title || t("Unavailable session")} - ${
    tab.projectId === null ? t("Global") : snapshot?.projects.find(project => project.id === tab.projectId)?.name ?? t("Unavailable project")}` : t("Prompt draft");
  useLayoutEffect(() => { reconcileSessionTabModel(model, state, label); });
  useLayoutEffect(() => { if (menu && !menu.current()) setMenu(null); });
  function guard() {
    const current = capture();
    return () => alive.current && current();
  }
  function dispatch(action: Action, current = guard()) {
    const intent = sessionTabAction(action, state, snapshot, current);
    if (intent?.kind === "draft") selectDraft();
    else if (intent?.kind === "select") select(intent.tab);
    else if (intent?.kind === "close") close(intent.tab);
    // Never let FlexLayout select/delete independently of App acceptance.
    return undefined;
  }
  function more(anchor: HTMLElement) {
    const current = guard();
    if (!current()) return;
    setMenu({ anchor, current, items: [
      { key: "reopen", label: t("Reopen closed tab"), disabled: !snapshot || !state.closed.length,
        onSelect: () => { if (current()) reopen(); } },
      { key: "refresh", label: t("Refresh statuses"), disabled: !observations?.enabled || !state.open.length,
        onSelect: () => { if (current()) observations?.refresh(state.open); } },
    ] });
  }
  return <div className="session-tabs workspace-layout" ref={root} onKeyDownCapture={event => {
    // Do not intercept the editor inside a factory; only the tab/menu chrome.
    const target = event.target as HTMLElement;
    if (!target.closest('[role="tablist"], [role="menu"], .session-tab-more')) return;
    if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) {
      event.preventDefault(); event.stopPropagation();
    }
  }}>
    <Layout model={model} supportsPopout={false} invalidateTabContentOnParentRender={true}
      keyMap={{ closeTab: undefined, renameTab: undefined, focusTabToggle: undefined,
        focusNextTabset: undefined, focusPreviousTabset: undefined, closeOverlayBorder: undefined }}
      onAction={action => dispatch(action)} onContextMenu={(_node, event) => event.preventDefault()}
      onRenderTab={(node, values) => {
        const tab = state.open.find(value => sessionNodeId(value) === node.getId());
        values.content = <SessionTabLabel label={label(tab ?? null)} path={tab?.path ?? null} dirty={!!tab && dirty(tab.sessionId)} />;
        if (tab) values.leading = <>
          {observations && <RuntimeObservationBadge controls={observations} tab={tab} compact />}
        </>;
      }}
      onRenderTabSet={(_node, values) => values.buttons.push(<button key="more" type="button" className="session-tab-more"
        aria-label={t("Open sessions")} aria-haspopup="menu" onClick={event => more(event.currentTarget)}>⋯</button>)}
      onShowOverflowMenu={(_node, event, items) => {
        const current = guard();
        if (!current()) return;
        setMenu({ anchor: event.currentTarget as HTMLElement, current, items: items.map(item => ({
          key: item.node.getId(), label: item.node.getId() === draftTabId ? label(null) : item.node.getName(),
          onSelect: () => dispatch(Actions.selectTab(item.node.getId()), current),
        })) });
      }}
      factory={node => ownsSessionTabContent(node.getId(), state) ? children : null} />
    {menu && <PopupMenu anchor={menu.anchor} items={menu.items} title={t("Open sessions")}
      container={root.current ?? undefined} onClose={() => setMenu(null)} />}
  </div>;
}
