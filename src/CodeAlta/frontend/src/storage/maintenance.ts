import type { storage, StorageMaintenance, StorageRetentionMaintenance } from "#neoastra";
import type { MessageKey } from "../localization";

export type MaintenanceApi = Pick<typeof storage, "watchMaintenance" | "watchRetention">;
export interface MaintenanceState {
  scratchpad?: StorageMaintenance;
  retention?: StorageRetentionMaintenance;
}

/** Window-owned observation only. Disposing cancels the two streams, never host maintenance. */
export function followMaintenance(api: MaintenanceApi, epoch: string, changed: (state: MaintenanceState) => void, initial: MaintenanceState = {}): () => void {
  const abort = new AbortController();
  let state = initial;
  const publish = (update: MaintenanceState) => {
    if (!abort.signal.aborted) { state = { ...state, ...update }; changed(state); }
  };
  async function follow<T extends { state: string }>(open: () => Promise<AsyncIterable<T>>, receive: (value: T) => void, unavailable: () => void, last: T | undefined) {
    try {
      for await (const value of await open()) {
        if (abort.signal.aborted) return;
        // These passes run once per host epoch, so replay cannot invalidate a known final result.
        if (!isFinal(last?.state)) { last = value; receive(value); }
      }
      if (!abort.signal.aborted && !isFinal(last?.state)) unavailable();
    } catch {
      // The host pass is single-shot: a later transport failure cannot invalidate an already-final outcome.
      if (!abort.signal.aborted && !isFinal(last?.state)) unavailable();
    }
  }
  void follow(() => api.watchMaintenance({ expectedEpoch: epoch }, { signal: abort.signal }),
    scratchpad => publish({ scratchpad }), () => publish({ scratchpad: {
      state: "unavailable", removedFiles: "0", removedDirectories: "0", skippedEntries: "0", reasons: [], startedAt: null, completedAt: null,
    } }), initial.scratchpad);
  void follow(() => api.watchRetention({ expectedEpoch: epoch }, { signal: abort.signal }),
    retention => publish({ retention }), () => publish({ retention: {
      state: "unavailable", examined: 0, deleted: 0, skipped: 0, reason: null,
    } }), initial.retention);
  return () => abort.abort();
}

function isFinal(state: string | undefined): boolean {
  return state === "completed" || state === "partial" || state === "skipped" || state === "canceled" || state === "failed";
}

/** Disconnects stop observation, not maintenance: keep final results and mark unfinished work unavailable. */
export function disconnectedMaintenance(value: MaintenanceState): MaintenanceState {
  return {
    scratchpad: value.scratchpad && !isFinal(value.scratchpad.state) ? { ...value.scratchpad, state: "unavailable" } : value.scratchpad,
    retention: value.retention && !isFinal(value.retention.state) ? { ...value.retention, state: "unavailable" } : value.retention,
  };
}

export function scratchpadReason(reasons: readonly string[]): MessageKey | undefined {
  if (reasons.includes("invalid-configuration")) return "Check the storage configuration.";
  if (reasons.includes("disabled")) return "Scratchpad cleanup is off.";
  if (reasons.includes("canceled")) return "Scratchpad cleanup was canceled.";
  if (reasons.length) return "Some scratchpad items were kept.";
  return undefined;
}

/** Only known, bounded host codes become text. Unknown codes never masquerade as success. */
export function maintenanceNotice(kind: "scratchpad" | "retention", value: { state: string; reason?: string | null })
  : { key: MessageKey; intent: "primary" | "success" | "warning" | "none" } | undefined {
  if (value.state === "not_started" || value.state === "notstarted") return undefined;
  if (kind === "scratchpad") {
    switch (value.state) {
      case "running": return { key: "Clearing scratchpad…", intent: "primary" };
      case "completed": return { key: "Scratchpad cleared.", intent: "success" };
      case "partial": return { key: "Scratchpad cleanup was partial.", intent: "warning" };
      case "skipped": return { key: "Scratchpad cleanup was skipped.", intent: "none" };
      case "canceled": return { key: "Scratchpad cleanup was canceled.", intent: "warning" };
      default: return { key: "Scratchpad cleanup status is unavailable.", intent: "warning" };
    }
  }
  switch (value.state) {
    case "running": return { key: "Session cleanup is running…", intent: "primary" };
    case "completed": return { key: "Session cleanup completed.", intent: "success" };
    case "partial": return { key: "Session cleanup was partial.", intent: "warning" };
    case "skipped":
      switch (value.reason) {
        case "disabled": return { key: "Session cleanup is off.", intent: "none" };
        case "archive_unavailable": return { key: "Saved Statistics are unavailable.", intent: "warning" };
        case "invalid_cutoff": return { key: "Check the retention duration.", intent: "warning" };
        case "external_admission_unqualified": case "desktop_admission_unqualified": case "protection_unavailable":
          return { key: "Session cleanup is not available yet.", intent: "none" };
        default: return { key: "Session cleanup was skipped.", intent: "none" };
      }
    case "canceled": return { key: "Session cleanup was canceled.", intent: "warning" };
    default: return { key: "Session cleanup status is unavailable.", intent: "warning" };
  }
}
