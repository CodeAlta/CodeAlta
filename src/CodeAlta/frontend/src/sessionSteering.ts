import type { SessionAdmission, SessionReceiptPage, SessionReceiptView, SessionRuntimeStateResponse, SessionSteerRequest } from "#neoastra";
import type { createMutationCapability, SubmissionResult } from "./sessionOperations";
import { createOwnerChangeSignal } from "./ownerChangeSignal";

type Capability = ReturnType<typeof createMutationCapability>;
type WaitOptions = { signal: AbortSignal; timeoutMilliseconds: number };
type Pending = { request: SessionSteerRequest; inFlight: boolean };

// Only explicitly refreshed runtime facts supply a target. No run is inferred from a send receipt or Display.
export function captureSteering(epoch: string, sessionId: string, observation: SessionRuntimeStateResponse | undefined,
  text: string, key: string): SessionSteerRequest | null {
  const entry = observation?.entry;
  if (observation?.status !== "ok" || observation.hostEpoch !== epoch || observation.sessionId !== sessionId
    || !observation.runtimeInstanceId || observation.coordinatorTransitionInProgress !== false
    || !entry || entry.isRetiring || entry.isTerminated || !entry.activeRunId || !text.trim() || text.length > 32768) return null;
  return Object.freeze({ expectedEpoch: epoch, clientRequestId: key, sessionId, expectedRuntimeInstanceId: observation.runtimeInstanceId,
    expectedAttachmentGeneration: entry.attachmentGeneration, expectedRunId: entry.activeRunId, text });
}

function matches(request: SessionSteerRequest, row: SessionReceiptView): boolean {
  return row.kind === "Steer" && row.clientRequestId === request.clientRequestId && row.sessionId === request.sessionId;
}

// App-owned, bounded uncertain requests and synchronous in-flight latches, surviving panel remount.
// There is no background reader or automatic retry. A live waiter keeps its latch until it settles.
export function createSteeringSubmissions(invoke: (request: SessionSteerRequest, options: WaitOptions) => Promise<SessionAdmission>) {
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
    async submit(request: SessionSteerRequest, signal: AbortSignal, capability: Capability, publish: (result: SubmissionResult) => void): Promise<void> {
      if (signal.aborted || !capability.canSubmit(request)) return;
      const key = sessionKey(request.sessionId);
      let entry = pending.get(key);
      // Explicit retry must use the retained immutable object, never fresh text or a fresh observation.
      if (entry && (entry.inFlight || entry.request !== request)) return;
      if (!entry) {
        if (pending.size >= 256) { publish({ status: "capacity", epoch: request.expectedEpoch, receipt: null }); return; }
        entry = { request: Object.isFrozen(request) ? request : Object.freeze({ ...request }), inFlight: false };
        pending.set(key, entry);
      }
      entry.inFlight = true; // Before invoking transport or yielding, not a React render-time guard.
      change.changed();
      const retained = entry;
      const captured = retained.request;
      let result: SubmissionResult = { status: "uncertain", epoch: captured.expectedEpoch, receipt: null };
      try {
        const admission = await invoke(captured, { signal, timeoutMilliseconds: 8_000 });
        // Even a late response invalidates the App-shared epoch authority; never publish to an old panel.
        capability.observe(admission);
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
        // Cancellation/timeout or transport failure is not non-admission; retain every target/text field.
      } finally {
        retained.inFlight = false;
        change.changed();
      }
      if (!signal.aborted) publish(result);
    },
  };
}
