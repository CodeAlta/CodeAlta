import { useEffect, useRef, useSyncExternalStore } from "react";
import { createSessionDisplayStore } from "./sessionDisplay";
import type { SessionDisplayText, SessionDisplayToolActivity } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { TimelineMessage } from "./TimelineMessage";
import { liveTextItem, liveToolItem } from "./liveTimeline";
import { useShellLanguage } from "./shellLanguage";

export function LiveSessionPanel({ store, hostEpoch, sessionId, capability }: {
  store: ReturnType<typeof createSessionDisplayStore>; hostEpoch: string; sessionId: string;
  capability: ReturnType<typeof createMutationCapability>;
}) {
  const { t } = useShellLanguage();
  const observed = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const canMutate = useSyncExternalStore(capability.subscribe, capability.canMutate);
  const scope = useRef<{ hostEpoch: string; sessionId: string; selection: ReturnType<typeof store.select> } | null>(null);
  useEffect(() => {
    const owned = { hostEpoch, sessionId, selection: store.select(hostEpoch, sessionId, capability.observe) };
    scope.current = owned;
    return () => { owned.selection.detach(); if (scope.current === owned) scope.current = null; };
  }, [store, hostEpoch, sessionId, capability]);
  // Never flash the previous selection during the render preceding effect cleanup/admission.
  const state = observed.hostEpoch === hostEpoch && observed.sessionId === sessionId ? observed : null;
  const snapshot = state?.snapshot;
  // Keep the observation owner mounted, but don't put runtime diagnostics into
  // the conversation. The composer presents current run activity instead.
  if (!state || (state.kind !== "error" && !state.code && !state.cleanupBlocked
    && state.kind !== "closed" && !snapshot?.isClosed)) return null;
  return <section className="live-indicator" aria-label={t("Selected session live status")}>
    {state?.code === "stale_epoch" && <p role="alert">{t("The host has changed. Reload the Desktop UI before continuing; reconnecting with this old host identity will not work.")}</p>}
    {state.kind === "error" && state.code !== "stale_epoch" && !state.cleanupBlocked && <p role="alert">{t("Live observation unavailable. No idle or completion state is inferred.")}</p>}
    {state?.cleanupBlocked && <p role="alert">{t("Previous observation cleanup failed. Its owner is retained; no successor can open here. Reconnect cannot prove cleanup or recover effects.")}</p>}
    {(state.kind === "error" || state.kind === "closed" || snapshot?.isClosed || state.code || state.cleanupBlocked) && <button type="button" disabled={!canMutate || state.code === "stale_epoch" || state.cleanupBlocked} onClick={() => {
      const owned = scope.current;
      if (!owned || owned.hostEpoch !== hostEpoch || owned.sessionId !== sessionId || !capability.canMutate() || store.getSnapshot().cleanupBlocked) return;
      owned.selection = store.select(hostEpoch, sessionId, capability.observe);
    }}>{t("Reconnect live activity")}</button>}
    {(state.kind === "closed" || snapshot?.isClosed) && <p role="status">{t("Runtime display closed. No further updates will arrive on this observation.")}</p>}
  </section>;
}

export function LiveToolMessage({ row }: { row: SessionDisplayToolActivity }) {
  return <TimelineMessage item={liveToolItem(row)} toolTile />;
}

export function LiveTextMessage({ row }: { row: SessionDisplayText }) {
  return <TimelineMessage item={liveTextItem(row)} />;
}
