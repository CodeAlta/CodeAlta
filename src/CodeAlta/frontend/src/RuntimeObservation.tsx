import { useSyncExternalStore } from "react";
import { ActivitySpinner } from "./ActivitySpinner";
import { tabKey, type SessionTab } from "./sessionTabs";
import { projectRunning, sessionRunning, type createRuntimeObservations } from "./runtimeObservations";
import { useShellLanguage } from "./shellLanguage";

export type RuntimeObservationControls = { store: ReturnType<typeof createRuntimeObservations>; enabled: boolean; canObserve: (tab: SessionTab) => boolean; refresh: (tabs: readonly SessionTab[]) => void };

/** Sidebar activity: a spinner while an observed session (or any observed session of a project) is running. */
export function RunningSessionBadge({ controls, tab, projectId }: { controls: RuntimeObservationControls; tab?: SessionTab; projectId?: string | null }) {
  const { t } = useShellLanguage();
  // The answer is followed, not the observations: a refresh that changes nothing of it renders nothing.
  const running = useSyncExternalStore(controls.store.subscribe,
    () => tab ? sessionRunning(controls.store.getSnapshot(), tab) : projectRunning(controls.store.getSnapshot(), projectId ?? null));
  if (!controls.enabled || !running || tab && !controls.canObserve(tab)) return null;
  return <span className="session-running"><ActivitySpinner size={12} label={t("Running")} /></span>;
}

/** Tab-title activity: a spinner while the session runs, nothing otherwise. */
export function SessionTabActivity({ controls, tab }: { controls: RuntimeObservationControls; tab: SessionTab }) {
  const { t } = useShellLanguage();
  const running = useSyncExternalStore(controls.store.subscribe, () => sessionRunning(controls.store.getSnapshot(), tab));
  if (!controls.enabled || !running || !controls.canObserve(tab)) return null;
  return <span className="session-running"><ActivitySpinner size={12} label={t("Running")} /></span>;
}

export function RuntimeObservationBadge({ controls, tab }: { controls: RuntimeObservationControls; tab: SessionTab }) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(controls.store.subscribe, controls.store.getSnapshot);
  const row = state.rows.get(tabKey(tab));
  const eligible = controls.enabled && controls.canObserve(tab);
  const label = !eligible ? t("Unknown · archived/unverified or unavailable") : row ? `${row.stale ? t("Stale · ") : ""}${row.label}` : t("Unknown · not observed");
  return <span className="runtime-observation"
    title={`${label}\n${eligible && row ? row.details : t("Not observed; archived/catalog-only/unverified rows are not queried. Never permission to send or abort.")}`}>{label}</span>;
}

export function RuntimeObservationRefresh({ controls, tabs, disabled = false }: { controls: RuntimeObservationControls; tabs: readonly SessionTab[]; disabled?: boolean }) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(controls.store.subscribe, controls.store.getSnapshot);
  return <div className="runtime-observation-controls"><button type="button" disabled={disabled || !controls.enabled || !tabs.length}
    onKeyDown={event => { if ((event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) && event.key === "Enter") event.preventDefault(); }}
    onClick={() => controls.refresh(tabs)}>{t("Refresh statuses")}</button><small role="status">{state.summary}</small></div>;
}
