import { useRef } from "react";
import { useShellLanguage } from "./shellLanguage";
import type { WorkspaceSnapshot } from "#neoastra";
import { resolveSessionTab, tabKey, type SessionTab, type SessionTabs as Tabs } from "./sessionTabs";
import { RuntimeObservationBadge, RuntimeObservationRefresh, type RuntimeObservationControls } from "./RuntimeObservation";

// Navigation inside the existing FlexLayout content slot, not hidden live workspaces
// or a second docking model. The App remains the sole selection/content owner.
export function SessionTabStrip({ state, snapshot, dirty, select, close, reopen, selectDraft, observations }: {
  state: Tabs; snapshot?: WorkspaceSnapshot; dirty: (id: string) => boolean;
  select: (tab: SessionTab) => void; close: (tab: SessionTab) => void; reopen: () => void;
  selectDraft: () => void;
  observations?: RuntimeObservationControls;
}) {
  const { t } = useShellLanguage();
  const strip = useRef<HTMLDivElement>(null);
  return <div className="session-tabs">
    <div role="tablist" aria-label={t("Open sessions")} ref={strip} onKeyDown={event => {
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat || event.defaultPrevented ||
        event.ctrlKey || event.metaKey || event.altKey || event.shiftKey || !(event.target as HTMLElement).matches('[role="tab"]')) return;
      const buttons = Array.from(strip.current!.querySelectorAll<HTMLButtonElement>('[role="tab"]:not(:disabled)'));
      const index = buttons.indexOf(event.target as HTMLButtonElement);
      const next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 :
        event.key === "ArrowRight" ? (index + 1) % buttons.length : event.key === "ArrowLeft" ? (index + buttons.length - 1) % buttons.length : -1;
      if (next < 0 || !buttons[next]) return;
      event.preventDefault(); event.stopPropagation(); buttons[next].focus(); buttons[next].click();
    }}>
      <span className="session-tab"><button type="button" role="tab" aria-selected={!state.active}
        aria-controls="active-session-content" tabIndex={!state.active ? 0 : -1} onClick={selectDraft}>{t("Prompt draft")}</button></span>
      {state.open.map(tab => {
        const row = snapshot && resolveSessionTab(snapshot, tab);
        const active = state.active && tabKey(state.active) === tabKey(tab);
        const scope = tab.projectId === null ? t("Global") : snapshot?.projects.find(p => p.id === tab.projectId)?.name ?? t("Unavailable project");
        const label = `${row?.title ?? t("Unavailable session")} — ${scope}`;
        return <span className="session-tab" key={tabKey(tab)}>
          <button type="button" role="tab" aria-selected={!!active} aria-controls="active-session-content"
            tabIndex={active ? 0 : -1} disabled={!row}
            title={`${label}\n${tab.path}\n${t("Runtime badges are point-in-time observations, not live status or command permission.")}`} onClick={() => select(tab)}>
            {label}{dirty(tab.sessionId) && <span aria-label={t("Draft edited in this window")}> • {t("Draft")}</span>}
            {observations && <RuntimeObservationBadge controls={observations} tab={tab} />}
          </button>
          <button type="button" className="close-session-tab" aria-label={t("Close tab: {label}", { label })} onClick={() => {
            close(tab); requestAnimationFrame(() => strip.current?.querySelector<HTMLButtonElement>('[role="tab"][aria-selected="true"]')?.focus());
          }}>×</button>
        </span>;
      })}
    </div>
    <button type="button" className="quiet-button" disabled={!snapshot || !state.closed.length} onClick={reopen}>{t("Reopen closed tab")}</button>
    {observations && <RuntimeObservationRefresh controls={observations} tabs={state.open} />}
  </div>;
}
