import type { SessionAdmission, SessionCompactRequest, SessionReceiptPage, SessionReceiptView, SessionRuntimeStateResponse } from "#neoastra";
import type { createMutationCapability, SubmissionResult } from "./sessionOperations";

type Capability = ReturnType<typeof createMutationCapability>;
type WaitOptions = { signal: AbortSignal; timeoutMilliseconds: number };
type Pending = { request: SessionCompactRequest; inFlight: boolean };

// Explicit observation supplies attachment eligibility, not proof that the provider is still idle.
export function captureCompaction(epoch: string, sessionId: string, observation: SessionRuntimeStateResponse | undefined,
  key: string): SessionCompactRequest | null {
  const entry = observation?.entry;
  if (observation?.status !== "ok" || observation.hostEpoch !== epoch || observation.sessionId !== sessionId
    || !observation.runtimeInstanceId || observation.coordinatorTransitionInProgress !== false
    || !entry || entry.isRetiring || entry.isTerminated || entry.activeRunId !== null || entry.queueDrainInProgress) return null;
  return Object.freeze({ expectedEpoch: epoch, clientRequestId: key, sessionId, expectedRuntimeInstanceId: observation.runtimeInstanceId,
    expectedAttachmentGeneration: entry.attachmentGeneration });
}

function matches(request: SessionCompactRequest, row: SessionReceiptView): boolean {
  return row.kind === "Compact" && row.clientRequestId === request.clientRequestId && row.sessionId === request.sessionId;
}

// App-owned bounded retention survives selection loss/remount. No automatic retry or refresh.
export function createCompactionSubmissions(invoke: (request: SessionCompactRequest, options: WaitOptions) => Promise<SessionAdmission>) {
  const pending = new Map<string, Pending>();
  const sessionKey = (sessionId: string) => sessionId.toLowerCase();
  return {
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
      return true;
    },
    async submit(request: SessionCompactRequest, signal: AbortSignal, capability: Capability, publish: (result: SubmissionResult) => void): Promise<void> {
      if (signal.aborted || !capability.canSubmit(request)) return;
      const key = sessionKey(request.sessionId);
      let entry = pending.get(key);
      if (entry && (entry.inFlight || entry.request !== request)) return;
      if (!entry) {
        if (pending.size >= 256) { publish({ status: "capacity", epoch: request.expectedEpoch, receipt: null }); return; }
        entry = { request: Object.isFrozen(request) ? request : Object.freeze({ ...request }), inFlight: false };
        pending.set(key, entry);
      }
      entry.inFlight = true; // Synchronous before transport invocation, independent of React renders.
      const retained = entry;
      const captured = retained.request;
      let result: SubmissionResult = { status: "uncertain", epoch: captured.expectedEpoch, receipt: null };
      try {
        const admission = await invoke(captured, { signal, timeoutMilliseconds: 8_000 });
        capability.observe(admission);
        if (!signal.aborted && capability.canSubmit(captured) && admission.epoch === captured.expectedEpoch) {
          if ((admission.status === "accepted" || admission.status === "replay") && admission.receipt && matches(captured, admission.receipt)) {
            pending.delete(key);
            result = admission;
          } else if (["conflict", "busy", "capacity", "closed", "invalid_request"].includes(admission.status)) {
            pending.delete(key);
            result = admission;
          }
        } else if (!signal.aborted && !capability.canMutate()) result = admission;
      } catch {
        // A lost response cannot prove non-admission. Only exact explicit retry or receipt reconciliation releases it.
      } finally {
        retained.inFlight = false;
      }
      if (!signal.aborted) publish(result);
    },
  };
}
