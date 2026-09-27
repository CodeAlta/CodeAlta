import { useSyncExternalStore } from "react";
import { tabKey, type SessionTab } from "./sessionTabs";
import type { createRuntimeObservations } from "./runtimeObservations";
import { useShellLanguage } from "./shellLanguage";

export type RuntimeObservationControls = { store: ReturnType<typeof createRuntimeObservations>; enabled: boolean; canObserve: (tab: SessionTab) => boolean; refresh: (tabs: readonly SessionTab[]) => void };

export function RuntimeObservationBadge({ controls, tab }: { controls: RuntimeObservationControls; tab: SessionTab }) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(controls.store.subscribe, controls.store.getSnapshot);
  const row = state.rows.get(tabKey(tab));
  const eligible = controls.enabled && controls.canObserve(tab);
  return <span className="runtime-observation" title={eligible && row ? row.details : t("Not observed; archived/catalog-only/unverified rows are not queried. Never permission to send or abort.")}>
    {!eligible ? t("Unknown · archived/unverified or unavailable") : row ? `${row.stale ? t("Stale · ") : ""}${row.label}` : t("Unknown · not observed")}
  </span>;
}

export function RuntimeObservationRefresh({ controls, tabs, disabled = false }: { controls: RuntimeObservationControls; tabs: readonly SessionTab[]; disabled?: boolean }) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(controls.store.subscribe, controls.store.getSnapshot);
  return <div className="runtime-observation-controls"><button type="button" disabled={disabled || !controls.enabled || !tabs.length}
    onKeyDown={event => { if ((event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) && event.key === "Enter") event.preventDefault(); }}
    onClick={() => controls.refresh(tabs)}>{t("Refresh statuses")}</button><small role="status">{state.summary}</small></div>;
}
