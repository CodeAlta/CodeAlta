import type { ApplicationLogsClearRequest, ApplicationLogsClearResponse, ApplicationLogsResponse } from "#neoastra";

export type LogClearTarget = Readonly<{ captureId: string; boundary: string; grant: string;
  rows: number; captureOmitted: string; readOmitted: number }>;
export type LogClearState = Readonly<{ kind: "idle" | "pending" | "uncertain" | "confirmed" | "refused";
  target: LogClearTarget | null; result: ApplicationLogsClearResponse | null }>;

export function logClearTarget(page: ApplicationLogsResponse): LogClearTarget | null {
  if (page.status !== "ok" || !Array.isArray(page.rows) || page.rows.length === 0
    || typeof page.captureId !== "string" || !/^[0-9a-f-]{36}$/.test(page.captureId)
    || typeof page.grant !== "string" || !/^[0-9a-f-]{36}$/.test(page.grant)
    || typeof page.boundary !== "string" || !/^[1-9]\d{0,18}$/.test(page.boundary)
    || !/^\d{1,19}$/.test(page.captureOmitted) || !Number.isInteger(page.readOmitted)
    || page.readOmitted < 0 || page.readOmitted > 1000) return null;
  return { captureId: page.captureId, boundary: page.boundary, grant: page.grant,
    rows: page.rows.length, captureOmitted: page.captureOmitted, readOmitted: page.readOmitted };
}

export function createApplicationLogClearActions(clear: (request: ApplicationLogsClearRequest,
  options: { timeoutMilliseconds: number }) => Promise<ApplicationLogsClearResponse>) {
  let state: LogClearState = { kind: "idle", target: null, result: null };
  const listeners = new Set<() => void>();
  const publish = (next: LogClearState) => { state = next; for (const listener of listeners) {
    try { listener(); } catch { /* A broken observer cannot strand the original request. */ }
  } };
  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    snapshot: () => state,
    submit(target: LogClearTarget, confirmation: string): boolean {
      if (confirmation !== "CLEAR CAPTURED LOGS"
        || !/^[0-9a-f-]{36}$/.test(target.captureId) || !/^[0-9a-f-]{36}$/.test(target.grant)
        || !/^[1-9]\d{0,18}$/.test(target.boundary)
        || state.kind === "pending" || state.kind === "uncertain" || state.kind === "refused"
        || state.kind === "confirmed" && (target.captureId !== state.target?.captureId
          || BigInt(target.boundary) <= BigInt(state.target.boundary) || target.grant === state.target.grant)) return false;
      publish({ kind: "pending", target, result: null }); // Synchronous latch survives component disposal.
      const request: ApplicationLogsClearRequest = { captureId: target.captureId, boundary: target.boundary,
        grant: target.grant, confirmation };
      try {
        void clear(request, { timeoutMilliseconds: 8000 }).then(result => {
          if (result.status === "cleared" && result.captureId === target.captureId && result.boundary === target.boundary
            && Number.isInteger(result.clearedRows) && result.clearedRows >= 0 && result.clearedRows <= target.rows + target.readOmitted
            && /^\d{1,19}$/.test(result.coveredOmitted)) publish({ kind: "confirmed", target, result });
          else if ((result.status === "invalid_request" || result.status === "stale_snapshot" || result.status === "unavailable")
            && result.captureId === null && result.boundary === "" && result.clearedRows === 0 && result.coveredOmitted === "0")
            publish({ kind: "refused", target, result: null });
          else publish({ kind: "uncertain", target, result: null });
        }).catch(() => publish({ kind: "uncertain", target, result: null }));
      } catch { publish({ kind: "uncertain", target, result: null }); }
      return true;
    },
  };
}
