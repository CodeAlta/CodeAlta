import type { SessionPermissionCommand, SessionPermissionCommandHandle, SessionPermissionResolution,
  SessionPermissionResolveRequest, SessionPermissionsPage, SessionPermissionsRequest } from "#neoastra";
import { createOwnerChangeSignal } from "./ownerChangeSignal";

export type CommandDecision = "allow_once" | "deny" | "cancel";
export type PermissionReviewState =
  | { kind: "loading" | "resolving" }
  | { kind: "ready"; entries: readonly SessionPermissionCommand[]; hasMore: boolean }
  | { kind: "result"; code: "resolved" | "rejected" }
  | { kind: "error"; code: string; reloadRequired: boolean };
type CallOptions = { signal: AbortSignal; timeoutMilliseconds: number };
type DecisionOrigin = Readonly<{
  expectedHostEpoch: string; handle: SessionPermissionCommandHandle; decision: CommandDecision;
}>;
export type PermissionDecisionObservation = Readonly<{
  origin: DecisionOrigin; state: "pending" | "resolved" | "rejected" | "error"; code: string | null;
}>;
type RetainedDecision = {
  readonly origin: DecisionOrigin;
  readonly providerId: string; readonly command: string; readonly workingDirectory: string; readonly reason: string | null;
  readonly controller: AbortController;
  waiter: Promise<SessionPermissionResolution> | null;
  state: PermissionDecisionObservation["state"];
  code: string | null;
  observed: boolean;
};

// One App-owned original decision/waiter, not a replay or completion ledger. List reads remain selection-owned.
export function createPermissionReviewer(
  list: (request: SessionPermissionsRequest, options: CallOptions) => Promise<SessionPermissionsPage>,
  resolve: (request: SessionPermissionResolveRequest, options: CallOptions) => Promise<SessionPermissionResolution>,
) {
  let selection = 0;
  let hostEpoch: string | null = null;
  let runtime: string | null = null;
  let reloadCode: string | null = null;
  let retained: RetainedDecision | null = null;
  const change = createOwnerChangeSignal();
  const latch = (code: string) => {
    // Epoch invalidation is permanent, including when a former selection's reply arrives late.
    if (code === "stale_epoch") reloadCode = code;
    else reloadCode ??= code;
  };
  const replaceable = () => retained === null
    || ((retained.state === "resolved" || retained.state === "rejected") && retained.observed);
  return {
    subscribe: change.subscribe, getSnapshot: change.getSnapshot,
    // Non-acknowledging projection: observeDecision intentionally remains an explicit, gate-releasing action.
    readOriginal: () => retained && Object.freeze({ origin: retained.origin, providerId: retained.providerId, command: retained.command,
      workingDirectory: retained.workingDirectory, reason: retained.reason, state: retained.state, code: retained.code }),
    forSelection(request: SessionPermissionsRequest, signal: AbortSignal, publish: (value: PermissionReviewState) => void) {
      // Caller-owned request objects and later selections cannot change captured authority.
      const selectedRequest = Object.freeze({ expectedHostEpoch: request.expectedHostEpoch, sessionId: request.sessionId });
      const validSelection = guid(selectedRequest.expectedHostEpoch) && identity(selectedRequest.sessionId);
      const selected = ++selection;
      if (validSelection) {
        if (hostEpoch !== null && hostEpoch !== selectedRequest.expectedHostEpoch) latch("stale_epoch");
        hostEpoch ??= selectedRequest.expectedHostEpoch;
      }
      let generation = 0;
      let entries: readonly SessionPermissionCommand[] = [];
      const active = () => !signal.aborted && selected === selection;
      const error = (code: string) => {
        entries = [];
        if (["stale_epoch", "stale_runtime", "uncertain"].includes(code)) latch(code);
        if (active()) publish({ kind: "error", code: reloadCode ?? code, reloadRequired: reloadCode !== null });
      };
      signal.addEventListener("abort", () => {
        entries = [];
      }, { once: true });
      if (reloadCode) error(reloadCode);
      return {
        async refresh(): Promise<void> {
          if (!active()) return;
          if (!validSelection) { error("invalid_request"); return; }
          if (reloadCode) { error(reloadCode); return; }
          if (!replaceable()) return;
          const current = ++generation;
          entries = [];
          publish({ kind: "loading" });
          try {
            const page = await list(selectedRequest, { signal, timeoutMilliseconds: 8_000 });
            if (!active() || current !== generation) return;
            if (reloadCode) { error(reloadCode); return; }
            if (page.status === "stale_epoch" || page.hostEpoch !== selectedRequest.expectedHostEpoch) { error("stale_epoch"); return; }
            if (page.status !== "ok") { error(page.status); return; }
            if (page.sessionId !== selectedRequest.sessionId || !Array.isArray(page.entries) || page.entries.length > 4
              || typeof page.hasMore !== "boolean" || page.entries.some(entry => !validCommand(entry, selectedRequest.sessionId))
              || new Set(page.entries.map(entry => entry.handle.attemptId)).size !== page.entries.length) {
              error("invalid_response"); return;
            }
            for (const entry of page.entries) {
              if (runtime !== null && runtime !== entry.handle.runtimeInstanceId) { error("stale_runtime"); return; }
              runtime = entry.handle.runtimeInstanceId;
            }
            entries = Object.freeze(page.entries.map(entry => Object.freeze({ ...entry, handle: Object.freeze({ ...entry.handle }) })));
            publish({ kind: "ready", entries, hasMore: page.hasMore });
          } catch { if (active() && current === generation) error("read_failed"); }
        },
        observeDecision(): PermissionDecisionObservation | null {
          if (!active() || retained === null) return null;
          // A local observation never queries pending entries, resubmits, or acknowledges a live waiter.
          if (reloadCode) return Object.freeze({ origin: retained.origin, state: "error", code: reloadCode });
          if ((retained.state === "resolved" || retained.state === "rejected") && !retained.observed) {
            retained.observed = true;
            ++generation;
            entries = []; // Replacement requires a NEW explicit list/review after acknowledgment.
          }
          return Object.freeze({ origin: retained.origin, state: retained.state, code: retained.code });
        },
        async decide(entry: SessionPermissionCommand, decision: CommandDecision): Promise<void> {
          if (!active() || !validSelection || !replaceable() || reloadCode || !entries.includes(entry)
            || !["allow_once", "deny", "cancel"].includes(decision)) return;
          // Retire the entire review window before dispatch. Old closures/refreshes cannot resubmit it.
          ++generation;
          entries = [];
          const original: RetainedDecision = {
            origin: Object.freeze({ expectedHostEpoch: selectedRequest.expectedHostEpoch,
              handle: Object.freeze({ ...entry.handle }), decision }),
            providerId: entry.providerId, command: entry.command, workingDirectory: entry.workingDirectory, reason: entry.reason,
            controller: new AbortController(), waiter: null, state: "pending", code: null, observed: false,
          };
          retained = original; // Synchronous exclusion, before any callback or transport can reenter.
          change.changed();
          try {
            publish({ kind: "resolving" });
            if (reloadCode) {
              original.state = "error"; original.code = reloadCode;
            } else {
              original.waiter = resolve(original.origin, { signal: original.controller.signal, timeoutMilliseconds: 8_000 });
              const response = await original.waiter;
              // Validate the ORIGINAL reply independently of presentation. Unknown/malformed data is not epoch evidence.
              if (!response || !["resolved", "rejected", "stale_epoch", "disabled", "uncertain", "invalid_request"].includes(response.status)
                || !guid(response.hostEpoch) || !sameHandle(response.handle, original.origin.handle)) {
                latch("uncertain");
              } else if (response.hostEpoch !== original.origin.expectedHostEpoch) {
                latch("stale_epoch");
              } else if (response.status === "resolved" || response.status === "rejected") {
                original.state = response.status;
              } else {
                latch("uncertain");
              }
            }
          } catch { latch("uncertain"); }
          if (reloadCode) { original.state = "error"; original.code = reloadCode; }
          change.changed();
          // Neither live terminal publication nor mounting acknowledges a result for replacement.
          if (!active()) return;
          if (original.state === "resolved" || original.state === "rejected") publish({ kind: "result", code: original.state });
          else error(original.code ?? "uncertain");
        },
      };
    },
  };
}

function sameHandle(a: SessionPermissionCommandHandle | null, b: SessionPermissionCommandHandle): boolean {
  return !!a && a.operationId === b.operationId && a.runtimeInstanceId === b.runtimeInstanceId
    && a.attachmentGeneration === b.attachmentGeneration && a.sessionId === b.sessionId
    && a.runId === b.runId && a.interactionId === b.interactionId && a.attemptId === b.attemptId;
}
function guid(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
    && value !== "00000000-0000-0000-0000-000000000000";
}
function text(value: unknown, limit: number, required: boolean): value is string {
  if (typeof value !== "string" || value.length > limit || value.includes("\0") || (required && !value.trim())) return false;
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const low = value.charCodeAt(++i);
      if (!(low >= 0xdc00 && low <= 0xdfff)) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) return false;
  }
  return true;
}
function identity(value: unknown): value is string {
  return text(value, 128, true) && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}
function validCommand(entry: SessionPermissionCommand, session: string): boolean {
  const h = entry?.handle;
  return !!h && h.sessionId === session && identity(h.sessionId) && identity(h.interactionId)
    && (h.runId === null || identity(h.runId)) && guid(h.operationId) && guid(h.runtimeInstanceId) && guid(h.attemptId)
    && typeof h.attachmentGeneration === "string" && /^[1-9][0-9]{0,18}$/.test(h.attachmentGeneration)
    && BigInt(h.attachmentGeneration) <= 9223372036854775807n && identity(entry.providerId)
    && text(entry.command, 4096, true) && text(entry.workingDirectory, 1024, true)
    && (entry.reason === null || text(entry.reason, 1024, false));
}
