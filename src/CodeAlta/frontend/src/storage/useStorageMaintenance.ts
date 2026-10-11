import { useEffect, useRef, useState } from "react";
import { showToast } from "../appToaster";
import { translate, type Locale } from "../localization";
import { disconnectedMaintenance, followMaintenance, maintenanceNotice, scratchpadReason, type MaintenanceApi, type MaintenanceState } from "./maintenance";

/** App owns the subscription: navigating away from Settings must not lose maintenance outcomes. */
export function useStorageMaintenance(api: MaintenanceApi, epoch: string | null, locale: Locale, available = true) {
  const [snapshot, setSnapshot] = useState<{ epoch: string; value: MaintenanceState }>();
  const latest = useRef(snapshot);
  const language = useRef(locale); language.current = locale;
  useEffect(() => {
    if (latest.current?.epoch !== epoch) { latest.current = undefined; setSnapshot(undefined); }
    if (!epoch) return;
    let previous: MaintenanceState = latest.current?.value ?? {};
    const publish = (value: MaintenanceState) => {
      latest.current = { epoch, value };
      setSnapshot(latest.current);
    };
    if (!available) { publish(disconnectedMaintenance(previous)); return; }
    let scratchpadToast = "";
    let retentionToast = "";
    const stop = followMaintenance(api, epoch, value => {
      publish(value);
      for (const kind of ["scratchpad", "retention"] as const) {
        const current = value[kind];
        if (!current || current === previous[kind]) continue;
        const notice = maintenanceNotice(kind, current);
        if (!notice) continue;
        // Progress counts can change frequently; update state without repeatedly reannouncing the same toast.
        if ((kind === "scratchpad" ? scratchpadToast : retentionToast) === notice.key) continue;
        if (kind === "scratchpad") scratchpadToast = notice.key; else retentionToast = notice.key;
        showToast({ message: translate(language.current, notice.key), intent: notice.intent,
          timeout: current.state === "running" ? 0 : 6000 }, `storage-${kind}`);
      }
      previous = value;
    }, previous);
    return () => {
      stop();
      for (const kind of ["scratchpad", "retention"] as const) {
        const key = kind === "scratchpad" ? "Scratchpad cleanup status is unavailable." : "Session cleanup status is unavailable.";
        if (previous[kind]?.state === "running") showToast({ message: translate(language.current, key),
          intent: "warning", timeout: 6000 }, `storage-${kind}`);
      }
    };
  }, [api, epoch, available]);
  const value = snapshot?.epoch === epoch ? snapshot.value : undefined;
  const scratchpad = value?.scratchpad;
  const reason = scratchpad ? scratchpadReason(scratchpad.reasons) : undefined;
  const state = scratchpad?.state;
  const summaryState = state === "running" || state === "completed" || state === "skipped" || state === "partial" || state === "canceled"
    ? state : "unavailable";
  const maintenance = scratchpad && state !== "notstarted" && state !== "not_started" ? {
    state: summaryState,
    reasons: reason && state !== "canceled" ? [translate(locale, reason)] : [],
  } as const : undefined;
  return { maintenance, retention: value?.retention?.state === "not_started" ? undefined : value?.retention };
}
