import type { SessionAdmission, SessionCancelQueueRequest, SessionQueueRequest, SessionReceiptPage, SessionReceiptView, SessionRuntimeStateResponse } from "#neoastra";
import type { createMutationCapability, SubmissionResult } from "./sessionOperations";
import { createOwnerChangeSignal } from "./ownerChangeSignal";
import { createComposerQueue } from "./composerQueue";

type Capability = ReturnType<typeof createMutationCapability>;
type WaitOptions = { signal: AbortSignal; timeoutMilliseconds: number };
type CancelIntent = Readonly<{ request: Readonly<SessionCancelQueueRequest>; sessionId: string }>;
type Pending = { request: Readonly<SessionQueueRequest>; inFlight: boolean; source: "composer" | "editor"; draftRevision: number | null };
type PendingCancel = { intent: CancelIntent; inFlight: boolean };

function wellFormed(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code < 0xd800 || code > 0xdfff) continue;
    if (code > 0xdbff || ++index === value.length) return false;
    const low = value.charCodeAt(index);
    if (low < 0xdc00 || low > 0xdfff) return false;
  }
  return true;
}
function identity(value: unknown, maximum: number, trim = true): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= maximum && !/^[\s\u0085]*$/u.test(value)
    && (!trim || !/^[\s\u0085]|[\s\u0085]$/u.test(value)) && wellFormed(value);
}
function guid(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
    && value !== "00000000-0000-0000-0000-000000000000";
}
function attachment(value: unknown): value is string {
  return typeof value === "string" && /^[1-9][0-9]{0,18}$/.test(value)
    && (value.length < 19 || value <= "9223372036854775807");
}
function validRequest(value: SessionQueueRequest): boolean {
  return identity(value.expectedEpoch, 64) && identity(value.clientRequestId, 256) && identity(value.sessionId, 256)
    && guid(value.expectedRuntimeInstanceId) && attachment(value.expectedAttachmentGeneration) && identity(value.text, 32768, false);
}

// Observations permit an attempt, not authority. Busy/draining is allowed; no expected run is invented.
export function captureQueue(epoch: string, sessionId: string, observation: SessionRuntimeStateResponse | undefined,
  text: string, key: string): Readonly<SessionQueueRequest> | null {
  const entry = observation?.entry;
  if (observation?.status !== "ok" || observation.hostEpoch !== epoch || observation.sessionId !== sessionId
    || observation.coordinatorTransitionInProgress !== false || !entry || entry.isRetiring !== false || entry.isTerminated !== false
    || typeof entry.queueDrainInProgress !== "boolean" || entry.pendingAgentPromptId !== null
    || (entry.activeRunId !== null && !identity(entry.activeRunId, 256))
    || !identity(entry.providerId, 256) || !identity(entry.providerKey, 256)
    || [entry.modelId, entry.reasoningEffort, entry.agentPromptId].some(value => value !== null
      && (typeof value !== "string" || value.length > 256 || !wellFormed(value)))
    || !guid(observation.runtimeInstanceId)) return null;
  const request = { expectedEpoch: epoch, clientRequestId: key, sessionId, expectedRuntimeInstanceId: observation.runtimeInstanceId,
    expectedAttachmentGeneration: entry.attachmentGeneration, text };
  return validRequest(request) ? Object.freeze(request) : null;
}

function validRow(row: SessionReceiptView): boolean {
  if (!row || !identity(row.clientRequestId, 256, false) || !identity(row.sessionId, 256, false) || !guid(row.operationId)
    || (row.targetOperationId !== null && !guid(row.targetOperationId)) || !["pending", "terminal"].includes(row.state)
    || (row.outcome !== null && !["Completed", "Cancelled", "Failed"].includes(row.outcome))
    || (row.code !== null && !identity(row.code, 64, false)) || (row.runId !== null && !identity(row.runId, 256, false))) return false;
  if (row.kind !== "Queue" && row.kind !== "CancelQueue")
    return ["Send", "Abort", "Steer", "Compact", "AbortRun"].includes(row.kind) && row.queueInsertion === null;
  if (row.state === "pending" && (row.outcome !== null || row.code !== null || row.runId !== null)) return false;
  if (row.state === "terminal" && (row.outcome === null || !identity(row.code, 64, false))) return false;
  if (row.kind === "CancelQueue") return row.queueInsertion === null && row.runId === null && guid(row.targetOperationId) && row.targetOperationId !== row.operationId
    && (row.state === "pending" || row.outcome === "Failed"
      || row.outcome === "Completed" && ["queue_cancellation_signalled", "already_terminal"].includes(row.code!));
  const insertion = row.queueInsertion;
  if (row.targetOperationId !== null || !insertion) return false;
  if (insertion.state === "pending") return row.state === "pending" && insertion.accepted === null && insertion.code === null;
  if (insertion.state !== "terminal" || typeof insertion.accepted !== "boolean" || !identity(insertion.code, 64, false)
    || (insertion.accepted ? insertion.code !== "queue_accepted" : insertion.code === "queue_accepted")) return false;
  if (row.state === "pending") return true;
  return row.outcome === "Completed" ? insertion.accepted && row.code === "queue_dispatched" && row.runId !== null
    : row.runId === null && (row.outcome === "Failed" || row.outcome === "Cancelled" && row.code === "queue_cancelled");
}
function validPage(page: SessionReceiptPage): boolean {
  return !!page && page.status === "ok" && identity(page.epoch, 64) && Array.isArray(page.rows) && page.rows.length <= 64
    && (page.next === null || Number.isInteger(page.next) && page.next >= 64 && page.next <= 256 && page.next % 64 === 0 && page.rows.length === 64)
    && page.rows.every(validRow) && new Set(page.rows.map(row => row.operationId)).size === page.rows.length
    && new Set(page.rows.map(row => row.clientRequestId)).size === page.rows.length;
}

export function captureQueueCancellation(epoch: string, sessionId: string, page: SessionReceiptPage, row: SessionReceiptView,
  key: string): CancelIntent | null {
  if (!validPage(page) || page.epoch !== epoch || !page.rows.includes(row) || row.kind !== "Queue" || row.state !== "pending"
    || row.sessionId !== sessionId || !identity(row.sessionId, 256) || !identity(key, 256)) return null;
  return Object.freeze({ sessionId: row.sessionId,
    request: Object.freeze({ expectedEpoch: epoch, clientRequestId: key, targetOperationId: row.operationId }) });
}

export function queueReceiptPhases(row: SessionReceiptView): readonly string[] | null {
  if (row.kind !== "Queue" || !validRow(row)) return null;
  const insertion = row.queueInsertion!;
  return ["Owner reservation accepted", insertion.state === "pending" ? "Host-only insertion pending"
    : insertion.accepted ? "Retained IN THIS HOST ONLY; not durable" : `Host-only insertion refused (${insertion.code})`,
  row.state === "pending" ? "Execution/cleanup pending" : row.outcome === "Completed"
    ? "Dispatch/cleanup settled; run completion is not confirmed" : `Execution/cleanup settled: ${row.outcome} (${row.code})`];
}

export function queueCancellationStatus(row: SessionReceiptView): string | null {
  if (row.kind !== "CancelQueue" || !validRow(row)) return null;
  if (row.state === "pending") return "Cancellation reserved; execution/cleanup pending";
  if (row.outcome === "Failed") return `Cancellation/cleanup failed (${row.code}); effects may already have occurred`;
  return row.code === "already_terminal" ? "Original operation already terminal; no rollback or run completion inferred"
    : "Cancellation signalled; not rollback, cleanup of the target, or run completion";
}

function observe(result: SessionAdmission | SessionReceiptPage, capability: Capability): boolean {
  // Treat wire values as untrusted even though the generated API provides compile-time types.
  if (!result || typeof result.status !== "string" || !(result.epoch === null || typeof result.epoch === "string")) return false;
  capability.observe(result); // Includes responses from a selection whose waiter was cancelled.
  return true;
}
function matches(request: SessionQueueRequest, row: SessionReceiptView): boolean {
  return validRow(row) && row.kind === "Queue" && row.clientRequestId === request.clientRequestId && row.sessionId === request.sessionId;
}
function matchesCancel(intent: CancelIntent, row: SessionReceiptView): boolean {
  return validRow(row) && row.kind === "CancelQueue" && row.clientRequestId === intent.request.clientRequestId
    && row.sessionId === intent.sessionId && row.targetOperationId === intent.request.targetOperationId;
}
function definiteRejection(admission: SessionAdmission, cancel: boolean): boolean {
  return admission.receipt === null && (["conflict", "busy", "capacity", "closed", "invalid_request"].includes(admission.status)
    || cancel && admission.status === "unknowntarget");
}

// App-owned volatile retained intents. Combined bound; synchronous exclusion precedes transport.
// No persistence, reconstruction, polling, implicit refresh, retargeting or automatic retry.
export function createQueueSubmissions(invoke: (request: SessionQueueRequest, options: WaitOptions) => Promise<SessionAdmission>,
  invokeCancel: (request: SessionCancelQueueRequest, options: WaitOptions) => Promise<SessionAdmission>) {
  const queues = new Map<string, Pending>();
  const cancellations = new Map<string, PendingCancel>();
  // Volatile, session-scoped secondary editor drafts are not the retained immutable queue requests.
  const drafts = new Map<string, { text: string; revision: number }>();
  const change = createOwnerChangeSignal();
  const sessionKey = (sessionId: string) => sessionId.toLowerCase();
  const draftKey = (epoch: string, sessionId: string) => JSON.stringify([epoch, sessionKey(sessionId)]);
  return {
    composer: createComposerQueue(),
    subscribe: change.subscribe, getSnapshot: change.getSnapshot,
    draft(epoch: string, sessionId: string): string { return drafts.get(draftKey(epoch, sessionId))?.text ?? ""; },
    draftRevision(epoch: string, sessionId: string): number { return drafts.get(draftKey(epoch, sessionId))?.revision ?? 0; },
    editDraft(epoch: string, sessionId: string, text: string): void {
      const key = draftKey(epoch, sessionId);
      drafts.set(key, { text, revision: (drafts.get(key)?.revision ?? 0) + 1 });
    },
    pending(sessionId: string): Readonly<Pending> | undefined {
      const entry = queues.get(sessionKey(sessionId)); return entry ? Object.freeze({ ...entry }) : undefined;
    },
    cancellations(sessionId: string): readonly Readonly<PendingCancel>[] {
      return Object.freeze([...cancellations.values()].filter(entry => sessionKey(entry.intent.sessionId) === sessionKey(sessionId))
        .map(entry => Object.freeze({ ...entry })));
    },
    cancelPending(operationId: string): Readonly<PendingCancel> | undefined {
      const entry = cancellations.get(operationId); return entry ? Object.freeze({ ...entry }) : undefined;
    },
    reconcile(sessionId: string, page: SessionReceiptPage, capability: Capability): { queueRecovered: boolean; cancellationsRecovered: number } {
      const recovered = { queueRecovered: false, cancellationsRecovered: 0 };
      if (!observe(page, capability) || !validPage(page)) return recovered;
      const key = sessionKey(sessionId); const entry = queues.get(key);
      if (entry && !entry.inFlight && capability.canSubmit(entry.request) && page.epoch === entry.request.expectedEpoch
        && page.rows.some(row => matches(entry.request, row))) { queues.delete(key); recovered.queueRecovered = true; }
      for (const [operation, value] of cancellations) {
        if (sessionKey(value.intent.sessionId) === key && !value.inFlight && capability.canSubmit(value.intent.request)
          && page.epoch === value.intent.request.expectedEpoch && page.rows.some(row => matchesCancel(value.intent, row))) {
          cancellations.delete(operation); recovered.cancellationsRecovered++;
        }
      }
      if (recovered.queueRecovered || recovered.cancellationsRecovered) change.changed();
      return recovered;
    },
    async submit(request: SessionQueueRequest, signal: AbortSignal, capability: Capability, publish: (result: SubmissionResult) => void,
      source: "composer" | "editor" = "editor"): Promise<void> {
      if (signal.aborted || !capability.canSubmit(request) || !validRequest(request)) return;
      const key = sessionKey(request.sessionId); let entry = queues.get(key);
      if (entry && (entry.inFlight || entry.request !== request)) return;
      if (!entry) {
        if (queues.size + cancellations.size >= 256) { publish({ status: "capacity", epoch: request.expectedEpoch, receipt: null }); return; }
        entry = { request: Object.freeze({ ...request }), inFlight: false, source,
          draftRevision: source === "editor" ? drafts.get(draftKey(request.expectedEpoch, request.sessionId))?.revision ?? 0 : null };
        queues.set(key, entry);
      }
      entry.inFlight = true;
      change.changed();
      const captured = entry.request;
      let result: SubmissionResult = { status: "uncertain", epoch: captured.expectedEpoch, receipt: null };
      try {
        const admission = await invoke(captured, { signal, timeoutMilliseconds: 8_000 });
        if (observe(admission, capability) && !signal.aborted && capability.canSubmit(captured) && admission.epoch === captured.expectedEpoch) {
          if ((["accepted", "replay"].includes(admission.status) && admission.receipt && matches(captured, admission.receipt)) || definiteRejection(admission, false)) {
            queues.delete(key); result = admission;
          }
        } else if (!signal.aborted && !capability.canMutate()) result = { status: "stale_epoch", epoch: captured.expectedEpoch, receipt: null };
      } catch { /* Transport loss is not proof of non-admission. Retain the exact request. */ }
      finally { entry.inFlight = false; change.changed(); }
      if (!signal.aborted) publish(result);
    },
    async cancel(intent: CancelIntent, signal: AbortSignal, capability: Capability, publish: (result: SubmissionResult) => void): Promise<void> {
      if (signal.aborted || !capability.canSubmit(intent.request) || !identity(intent.sessionId, 256)
        || !identity(intent.request.expectedEpoch, 64) || !identity(intent.request.clientRequestId, 256) || !guid(intent.request.targetOperationId)) return;
      const key = intent.request.targetOperationId; let entry = cancellations.get(key);
      if (entry && (entry.inFlight || entry.intent !== intent)) return;
      if (!entry) {
        if (queues.size + cancellations.size >= 256) { publish({ status: "capacity", epoch: intent.request.expectedEpoch, receipt: null }); return; }
        entry = { intent: Object.freeze({ sessionId: intent.sessionId, request: Object.freeze({ ...intent.request }) }), inFlight: false };
        cancellations.set(key, entry);
      }
      entry.inFlight = true;
      change.changed();
      const captured = entry.intent;
      let result: SubmissionResult = { status: "uncertain", epoch: captured.request.expectedEpoch, receipt: null };
      try {
        const admission = await invokeCancel(captured.request, { signal, timeoutMilliseconds: 8_000 });
        if (observe(admission, capability) && !signal.aborted && capability.canSubmit(captured.request) && admission.epoch === captured.request.expectedEpoch) {
          if ((["accepted", "replay"].includes(admission.status) && admission.receipt && matchesCancel(captured, admission.receipt)) || definiteRejection(admission, true)) {
            cancellations.delete(key); result = admission;
          }
        } else if (!signal.aborted && !capability.canMutate()) result = { status: "stale_epoch", epoch: captured.request.expectedEpoch, receipt: null };
      } catch { /* The original target/key/session survives uncertain cancellation. */ }
      finally { entry.inFlight = false; change.changed(); }
      if (!signal.aborted) publish(result);
    },
  };
}
