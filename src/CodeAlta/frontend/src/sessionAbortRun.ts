import type { SessionAbortRunRequest, SessionAdmission, SessionReceiptPage, SessionReceiptView, SessionRuntimeStateResponse } from "#neoastra";
import type { createMutationCapability, SubmissionResult } from "./sessionOperations";
import { createOwnerChangeSignal } from "./ownerChangeSignal";
import type { MessageKey } from "./localization";

type Capability = ReturnType<typeof createMutationCapability>;
type WaitOptions = { signal: AbortSignal; timeoutMilliseconds: number };
type Pending = { request: SessionAbortRunRequest; inFlight: boolean };

// Explicit observation supplies a target, not run/permission authority. Only the provider admits cancellation.
// A queue drain does not withhold the target: the run a drain started (the answer of a child session, a queued
// prompt) is draining for as long as it runs.
export function captureAbortRun(epoch: string, sessionId: string, observation: SessionRuntimeStateResponse | undefined,
  key: string): SessionAbortRunRequest | null {
  const entry = observation?.entry;
  if (observation?.status !== "ok" || observation.hostEpoch !== epoch || observation.sessionId !== sessionId
    || !observation.runtimeInstanceId || observation.coordinatorTransitionInProgress !== false
    || !entry || entry.isRetiring || entry.isTerminated || !entry.activeRunId?.trim()) return null;
  return Object.freeze({ expectedEpoch: epoch, clientRequestId: key, sessionId, expectedRuntimeInstanceId: observation.runtimeInstanceId,
    expectedAttachmentGeneration: entry.attachmentGeneration, expectedRunId: entry.activeRunId });
}

/** What the composer says of a Stop that did not stop the run: a message and its parameters. */
export type StopFailure = Readonly<{ key: MessageKey; parameters?: Readonly<Record<string, string>> }>;

/** The end of a Stop whose receipt settled without stopping the run; null when it stopped it, or when there was no run left to stop. */
export function settledStopFailure(row: Pick<SessionReceiptView, "state" | "outcome" | "code">): StopFailure | null {
  if (row.state !== "terminal" || row.outcome === "Completed" || row.outcome === "Cancelled") return null;
  // A run that ended before the Stop reached it needs no Stop.
  if (row.code === "abort_run_not_active") return null;
  return { key: row.code === "abort_run_unsupported" ? "This provider cannot stop a run."
    : row.code === "abort_run_target_unavailable" ? "The run to stop is no longer known. Refresh the session and try again."
    : "The Stop failed: the run may still be going on." };
}

/** What the composer says when the host did not take a Stop, or took it and ended it without stopping the run; null otherwise. */
export function abortRunAdmissionFailure(result: SubmissionResult): StopFailure | null {
  if (result.status === "accepted" || result.status === "replay") return result.receipt ? settledStopFailure(result.receipt) : null;
  switch (result.status) {
    case "uncertain": return { key: "CodeAlta did not confirm the Stop. Check whether the run still goes on." };
    case "closed": case "stale_epoch": return { key: "CodeAlta restarted. Reload the window to continue." };
    default: return { key: "The Stop was not accepted ({status}).", parameters: { status: result.status } };
  }
}

/** The end of a Stop, once a page lists its settled receipt: null while it waits, or when it stopped the run. */
export function watchedStopFailure(key: string, sessionId: string, page: SessionReceiptPage | undefined): StopFailure | null {
  if (page?.status !== "ok") return null;
  const row = page.rows.find(item => item.kind === "AbortRun" && item.sessionId === sessionId && item.clientRequestId === key);
  return row ? settledStopFailure(row) : null;
}

function matches(request: SessionAbortRunRequest, row: SessionReceiptView): boolean {
  return row.kind === "AbortRun" && row.clientRequestId === request.clientRequestId && row.sessionId === request.sessionId;
}

// App-owned bounded uncertainty/latches survive panel remount. No background reader, retry or retargeting.
export function createAbortRunSubmissions(invoke: (request: SessionAbortRunRequest, options: WaitOptions) => Promise<SessionAdmission>) {
  const pending = new Map<string, Pending>();
  const change = createOwnerChangeSignal();
  const sessionKey = (sessionId: string) => sessionId.toLowerCase();
  return {
    subscribe: change.subscribe, getSnapshot: change.getSnapshot,
    pending(sessionId: string): Readonly<Pending> | undefined {
      const value = pending.get(sessionKey(sessionId));
      return value ? Object.freeze({ ...value }) : undefined;
    },
    reconcile(sessionId: string, page: SessionReceiptPage, capability: Capability): boolean {
      capability.observe(page);
      const key = sessionKey(sessionId);
      const entry = pending.get(key);
      if (!entry || entry.inFlight || !capability.canSubmit(entry.request) || page.status !== "ok"
        || page.epoch !== entry.request.expectedEpoch || !page.rows.some(row => matches(entry.request, row))) return false;
      pending.delete(key);
      change.changed();
      return true;
    },
    async submit(request: SessionAbortRunRequest, signal: AbortSignal, capability: Capability, publish: (result: SubmissionResult) => void): Promise<void> {
      if (signal.aborted || !capability.canSubmit(request)) return;
      const key = sessionKey(request.sessionId);
      let entry = pending.get(key);
      if (entry && (entry.inFlight || entry.request !== request)) return;
      if (!entry) {
        if (pending.size >= 256) { publish({ status: "capacity", epoch: request.expectedEpoch, receipt: null }); return; }
        entry = { request: Object.isFrozen(request) ? request : Object.freeze({ ...request }), inFlight: false };
        pending.set(key, entry);
      }
      entry.inFlight = true; // Synchronous exclusion, before invoking transport or yielding.
      change.changed();
      const retained = entry;
      const captured = retained.request;
      let result: SubmissionResult = { status: "uncertain", epoch: captured.expectedEpoch, receipt: null };
      try {
        const admission = await invoke(captured, { signal, timeoutMilliseconds: 8_000 });
        capability.observe(admission); // Late obsolete-panel responses can still invalidate the shared epoch.
        if (!signal.aborted && capability.canSubmit(captured) && admission.epoch === captured.expectedEpoch) {
          if ((admission.status === "accepted" || admission.status === "replay") && admission.receipt && matches(captured, admission.receipt)) {
            pending.delete(key);
            result = admission;
          } else if (["conflict", "expired", "busy", "capacity", "closed", "invalid_request"].includes(admission.status)) {
            pending.delete(key);
            result = admission;
          }
        } else if (!signal.aborted && !capability.canMutate()) result = admission;
      } catch {
        // Wait cancellation/timeout is not proof of non-admission. Keep every immutable target field.
      } finally {
        retained.inFlight = false;
        change.changed();
      }
      if (!signal.aborted) publish(result);
    },
  };
}
