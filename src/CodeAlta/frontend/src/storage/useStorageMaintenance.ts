import { useEffect, useRef, useState } from "react";
import { showToast } from "../appToaster";
import { translate, type Locale } from "../localization";
import { disconnectedMaintenance, followMaintenance, maintenanceNotice, retentionProgress, scratchpadReason, type MaintenanceApi, type MaintenanceState } from "./maintenance";

/**
 * App owns the subscription: navigating away from Settings must not lose maintenance outcomes. `toasts` is false in a
 * window that does not say what the maintenance does: a secondary window, whose main window says it.
 */
export function useStorageMaintenance(api: MaintenanceApi, epoch: string | null, locale: Locale, available = true, toasts = true) {
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
        // The session cleanup toast says how far it is; the keyed toast is updated in place, never added again.
        const progress = kind === "retention" && "examined" in current ? retentionProgress(language.current, current) : undefined;
        const message = translate(language.current, notice.key) + (progress ? " " + progress : "");
        if (!toasts || (kind === "scratchpad" ? scratchpadToast : retentionToast) === message) continue;
        if (kind === "scratchpad") scratchpadToast = message; else retentionToast = message;
        showToast({ message, intent: notice.intent,
          timeout: current.state === "running" ? 0 : 6000 }, `storage-${kind}`);
      }
      previous = value;
    }, previous);
    return () => {
      stop();
      for (const kind of ["scratchpad", "retention"] as const) {
        const key = kind === "scratchpad" ? "Scratchpad cleanup status is unavailable." : "Session cleanup status is unavailable.";
        if (toasts && previous[kind]?.state === "running") showToast({ message: translate(language.current, key),
          intent: "warning", timeout: 6000 }, `storage-${kind}`);
      }
    };
  }, [api, epoch, available, toasts]);
  const value = snapshot?.epoch === epoch ? snapshot.value : undefined;
  const scratchpad = value?.scratchpad;
  const reason = scratchpad ? scratchpadReason(scratchpad.reasons) : undefined;
  const state = scratchpad?.state;
  const summaryState = state === "running" || state === "completed" || state === "skipped" || state === "partial" || state === "canceled"
    ? state : "unavailable";
  const maintenance = scratchpad && state !== "notstarted" && state !== "not_started" ? {
    state: summaryState,
    processedFiles: scratchpad.processedFiles,
    processedDirectories: scratchpad.processedDirectories,
    reasons: reason && state !== "canceled" ? [translate(locale, reason)] : [],
  } as const : undefined;
  return { maintenance, retention: value?.retention?.state === "not_started" ? undefined : value?.retention };
}
