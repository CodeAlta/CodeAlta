import type { SessionAdmission, SessionAbortRequest, SessionReceiptPage, SessionReceiptRequest, SessionReceiptView, SessionSendRequest, SessionSelection, SessionReferenceScope } from "#neoastra";
import { createOwnerChangeSignal } from "./ownerChangeSignal";
import { createImageDrafts, freezeImages, validImages } from "./promptImages";
import { diagnosticRequestId, rpcFailureCode } from "./rpcDiagnostics";
import { sendDiagnostics } from "./sendDiagnostics";

type WaitOptions = { signal: AbortSignal; timeoutMilliseconds: number };
/** `reason` says why an admission stayed unconfirmed: the host's status, or the transport failure code. */
export type SubmissionResult = SessionAdmission | { status: "uncertain"; epoch: string; receipt: null; reason?: string };

export function createMutationCapability(epoch: string) {
  let valid = true;
  const listeners = new Set<() => void>();
  let notificationFailure: unknown;
  return {
    canMutate: () => valid,
    canSubmit: (request: { expectedEpoch: string }) => valid && request.expectedEpoch === epoch,
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    // Retain the first observer fault for diagnostics, never as public status or authority.
    notificationFailure: () => notificationFailure,
    observe(result: { status: string; epoch: string | null }): boolean {
      if (valid && (result.status === "stale_epoch" || result.status === "stale_runtime" || (result.epoch !== null && result.epoch !== epoch))) {
        valid = false; // Commit before any reentrant subscriber or event handler can inspect authority.
        for (const listener of [...listeners]) {
          try { listener(); } catch (error) { notificationFailure ??= error; }
        }
      }
      return valid;
    },
  };
}

type Capability = ReturnType<typeof createMutationCapability>;
type AbortIntent = Readonly<{ request: Readonly<SessionAbortRequest>; sessionId: string }>;
type PendingSend = { request: Readonly<SessionSendRequest>; inFlight: boolean; waiter?: Promise<SessionAdmission> };
type PendingAbort = { intent: AbortIntent; inFlight: boolean; waiter?: Promise<SessionAdmission> };

export type OutgoingMessage = Readonly<{ key: string; epoch: string; sessionId: string; text: string;
  imageCount: number; timestamp: string; runId: string | null; state: "sending" | "accepted" | "uncertain" | "failed";
  /** The images of the prompt, for its card: each URL is built once and shares the request's content. */
  images?: readonly Readonly<{ title: string; mediaType: string; url: string }>[] }>;
/** No prompt is being sent: one value, so a timeline that takes it has nothing to compare. */
export const noOutgoing: readonly OutgoingMessage[] = Object.freeze([]);

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
function validSend(request: SessionSendRequest): boolean {
  return !!request && identity(request.expectedEpoch, 64) && identity(request.clientRequestId, 256)
    && identity(request.sessionId, 256) && (identity(request.text, 32768, false) || request.text === "" && !!request.images?.length) && validImages(request.images)
    && (!request.images?.length || !!request.selection?.modelId)
    && (request.references == null || identity(request.references.projectId, 256) && identity(request.references.projectPath, 4096))
    && (request.selection == null || identity(request.selection.providerKey, 256) && identity(request.selection.agentPromptId, 256)
      && (request.selection.modelId === null || identity(request.selection.modelId, 256))
      && (request.selection.reasoningEffort === null || identity(request.selection.reasoningEffort, 32))
      && (request.selection.permissionMode == null || identity(request.selection.permissionMode, 256)));
}
/** The key of the timeline echo a Send shows while the host has not reported its message yet. */
export function outgoingKey(request: Pick<SessionSendRequest, "expectedEpoch" | "sessionId" | "clientRequestId">): string {
  return JSON.stringify([request.expectedEpoch, request.sessionId, request.clientRequestId]);
}
export function captureSubmission(epoch: string, sessionId: string, text: string, key: string, selection: SessionSelection | null = null,
  references: SessionReferenceScope | null = null, images: SessionSendRequest["images"] = null): Readonly<SessionSendRequest> | null {
  if (!validSend({ expectedEpoch: epoch, clientRequestId: key, sessionId, text, selection, references, images })) return null;
  return Object.freeze({ expectedEpoch: epoch, clientRequestId: key, sessionId, text, selection: freezeSelection(selection), references: freezeReferences(references), images: images?.length ? freezeImages(images) : null });
}

function freezeReferences(value: SessionReferenceScope | null): Readonly<SessionReferenceScope> | null {
  return value == null ? null : Object.freeze({ projectId: value.projectId, projectPath: value.projectPath });
}

function freezeSelection(value: SessionSelection | null): Readonly<SessionSelection> | null {
  // A selection without a mode keeps the session's: the field is left out, as the host leaves it out.
  return value == null ? null : Object.freeze({ providerKey: value.providerKey, agentPromptId: value.agentPromptId,
    modelId: value.modelId, reasoningEffort: value.reasoningEffort, ...(value.permissionMode != null ? { permissionMode: value.permissionMode } : {}) });
}

// Match the existing wire validation. Legacy outcome/code/run nullability is deliberately independent
// of state; Queue phases apply only to Queue/CancelQueue rows in a legitimately mixed receipt page.
function validRow(row: SessionReceiptView): boolean {
  if (!row || !identity(row.clientRequestId, 256, false) || !identity(row.sessionId, 256, false) || !guid(row.operationId)
    || (row.targetOperationId !== null && !guid(row.targetOperationId)) || !["pending", "terminal"].includes(row.state)
    || (row.outcome !== null && !["Completed", "Cancelled", "Failed"].includes(row.outcome))
    || (row.code !== null && !identity(row.code, 64, false)) || (row.runId !== null && !identity(row.runId, 256, false))) return false;
  if (row.kind !== "Queue" && row.kind !== "CancelQueue")
    return ["Send", "Abort", "Steer", "Compact", "AbortRun"].includes(row.kind) && row.queueInsertion === null;
  if (row.state === "pending" && (row.outcome !== null || row.code !== null || row.runId !== null)) return false;
  if (row.state === "terminal" && (row.outcome === null || !identity(row.code, 64, false))) return false;
  if (row.kind === "CancelQueue") return row.queueInsertion === null && row.runId === null && guid(row.targetOperationId)
    && row.targetOperationId !== row.operationId && (row.state === "pending" || row.outcome === "Failed"
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
function validEnvelope(value: { status: string; epoch: string | null }): boolean {
  return !!value && identity(value.status, 64) && (value.epoch === null || identity(value.epoch, 64));
}
function validPage(page: SessionReceiptPage): boolean {
  if (!validEnvelope(page) || !Array.isArray(page.rows) || page.rows.length > 64) return false;
  if (page.status !== "ok") return page.rows.length === 0 && page.next === null;
  return identity(page.epoch, 64)
    && (page.next === null || Number.isInteger(page.next) && page.next >= 64 && page.next <= 256 && page.next % 64 === 0 && page.rows.length === 64)
    && Array.from(page.rows).every(validRow) && new Set(page.rows.map(row => row.operationId)).size === page.rows.length
    && new Set(page.rows.map(row => row.clientRequestId)).size === page.rows.length;
}
function observeAdmission(admission: SessionAdmission, capability: Capability): boolean {
  if (!validEnvelope(admission) || !(admission.receipt === null || validRow(admission.receipt))) return false;
  capability.observe(admission); // Valid late identity evidence revokes authority even after selection cancellation.
  return true;
}
function matchesSend(request: SessionSendRequest, row: SessionReceiptView): boolean {
  return validRow(row) && row.kind === "Send" && row.targetOperationId === null
    && row.clientRequestId === request.clientRequestId && row.sessionId === request.sessionId;
}
function matchesAbort(intent: AbortIntent, row: SessionReceiptView): boolean {
  return validRow(row) && row.kind === "Abort" && row.clientRequestId === intent.request.clientRequestId
    && row.sessionId === intent.sessionId && row.targetOperationId === intent.request.targetOperationId && row.operationId !== row.targetOperationId;
}
function definiteRefusal(admission: SessionAdmission, abort: boolean): boolean {
  return admission.receipt === null && (["conflict", "expired", "busy", "capacity", "closed", "invalid_request"].includes(admission.status)
    || abort && admission.status === "unknowntarget");
}
export function captureSubmissionAbort(epoch: string, sessionId: string, page: SessionReceiptPage, row: SessionReceiptView, key: string): AbortIntent | null {
  if (!validPage(page) || page.status !== "ok" || page.epoch !== epoch || !page.rows.includes(row)
    || row.kind !== "Send" || row.state !== "pending" || row.targetOperationId !== null || row.sessionId !== sessionId
    || !identity(row.sessionId, 256) || !identity(key, 256)) return null;
  return Object.freeze({ sessionId: row.sessionId,
    request: Object.freeze({ expectedEpoch: epoch, clientRequestId: key, targetOperationId: row.operationId }) });
}

// App-owned Send/Abort intent only, not runtime execution. The original transport waiter owns its
// latch through settlement even after remount; cancelling a document waiter never proves non-admission.
export function createOwnedSubmissions(invokeSend: (request: SessionSendRequest, options: WaitOptions) => Promise<SessionAdmission>,
  invokeAbort: (request: SessionAbortRequest, options: WaitOptions) => Promise<SessionAdmission>) {
  const sends = new Map<string, PendingSend>();
  const aborts = new Map<string, PendingAbort>();
  // Window-owned display echoes only; never used as admission or execution authority.
  const outgoing = new Map<string, OutgoingMessage>();
  const change = createOwnerChangeSignal();
  const sessionKey = (sessionId: string) => sessionId.toLowerCase();
  // What outgoing() last answered for a session: the same array is given again while its rows are the same,
  // so the timeline that takes it is not rendered again on every render of its panel.
  const shownOutgoing = new Map<string, readonly OutgoingMessage[]>();
  return {
    imageDrafts: createImageDrafts(),
    subscribe: change.subscribe, getSnapshot: change.getSnapshot,
    outgoing(epoch: string, sessionId: string): readonly OutgoingMessage[] {
      const key = sessionKey(sessionId);
      const rows = outgoing.size === 0 ? noOutgoing
        : [...outgoing.values()].filter(row => row.epoch === epoch && sessionKey(row.sessionId) === key);
      if (rows.length === 0) { shownOutgoing.delete(key); return noOutgoing; }
      const shown = shownOutgoing.get(key);
      if (shown && shown.length === rows.length && shown.every((row, index) => row === rows[index])) return shown;
      shownOutgoing.set(key, rows);
      return rows;
    },
    acknowledgeOutgoing(keys: readonly string[]) {
      let changed = false;
      for (const key of keys) changed = outgoing.delete(key) || changed;
      if (changed) change.changed();
    },
    pending(sessionId: string) {
      const entry = sends.get(sessionKey(sessionId));
      return entry ? Object.freeze({ request: entry.request, inFlight: entry.inFlight }) : undefined;
    },
    abortPending(operationId: string) {
      const entry = aborts.get(operationId);
      return entry ? Object.freeze({ intent: entry.intent, inFlight: entry.inFlight }) : undefined;
    },
    aborts(sessionId: string) {
      return Object.freeze([...aborts.values()].filter(entry => sessionKey(entry.intent.sessionId) === sessionKey(sessionId))
        .map(entry => Object.freeze({ intent: entry.intent, inFlight: entry.inFlight })));
    },
    reconcile(sessionId: string, page: SessionReceiptPage, capability: Capability): { sendRecovered: boolean; abortsRecovered: number } {
      const recovered = { sendRecovered: false, abortsRecovered: 0 };
      if (!validPage(page)) return recovered;
      capability.observe(page);
      if (page.status !== "ok") return recovered;
      const key = sessionKey(sessionId); const entry = sends.get(key);
      if (entry && !entry.inFlight && capability.canSubmit(entry.request) && page.epoch === entry.request.expectedEpoch
        && page.rows.some(row => matchesSend(entry.request, row))) { sends.delete(key); recovered.sendRecovered = true; }
      for (const [operation, value] of aborts) {
        if (sessionKey(value.intent.sessionId) === key && !value.inFlight && capability.canSubmit(value.intent.request)
          && page.epoch === value.intent.request.expectedEpoch && page.rows.some(row => matchesAbort(value.intent, row))) {
          aborts.delete(operation); recovered.abortsRecovered++;
        }
      }
      if (recovered.sendRecovered || recovered.abortsRecovered) change.changed();
      return recovered;
    },
    // `whenAccepted` is for a prompt leaving the queue: its echo appears once the host took it, so that a
    // refusal (the session was busy after all) shows nothing and the prompt simply stays queued.
    async submit(request: SessionSendRequest, signal: AbortSignal, capability: Capability, publish: (result: SubmissionResult) => void,
      echo: "always" | "whenAccepted" = "always"): Promise<void> {
      if (!validSend(request) || signal.aborted || !capability.canSubmit(request)) return;
      const key = sessionKey(request.sessionId); let entry = sends.get(key);
      if (entry && (entry.inFlight || entry.request !== request)) return;
      if (!entry) {
        if (sends.size + aborts.size >= 256) { publish({ status: "capacity", epoch: request.expectedEpoch, receipt: null }); return; }
        entry = { request: Object.freeze({ expectedEpoch: request.expectedEpoch, clientRequestId: request.clientRequestId,
          sessionId: request.sessionId, text: request.text, selection: freezeSelection(request.selection), references: freezeReferences(request.references), images: request.images?.length ? freezeImages(request.images) : null }), inFlight: false };
        sends.set(key, entry);
      }
      entry.inFlight = true; // Synchronous ownership precedes transport, not a React render-time guard.
      const echoKey = outgoingKey(request);
      const captured = entry.request;
      const showEcho = (state: OutgoingMessage["state"]) => {
        const previousEcho = outgoing.get(echoKey);
        if (!previousEcho && outgoing.size >= 256) outgoing.delete(outgoing.keys().next().value!);
        outgoing.set(echoKey, Object.freeze({ key: echoKey, epoch: request.expectedEpoch, sessionId: request.sessionId,
          text: request.text, imageCount: request.images?.length ?? 0, timestamp: previousEcho?.timestamp ?? new Date().toISOString(), runId: previousEcho?.runId ?? null, state,
          images: previousEcho?.images ?? Object.freeze((captured.images ?? []).map(image =>
            Object.freeze({ title: image.title, mediaType: image.mediaType, url: `data:${image.mediaType};base64,${image.base64}` }))) }));
        // Only the newest eight echoes keep their images: a card that stays failed or pending does not hold them forever.
        let withImages = 0;
        for (const [key, row] of [...outgoing].reverse())
          if (row.images?.length && ++withImages > 8) outgoing.set(key, Object.freeze({ ...row, images: undefined }));
      };
      if (echo === "always" || outgoing.has(echoKey)) showEcho("sending");
      change.changed();
      const started = Date.now();
      const diagnosticId = diagnosticRequestId(captured.clientRequestId);
      let result: SubmissionResult = { status: "uncertain", epoch: captured.expectedEpoch, receipt: null };
      try {
        sendDiagnostics.info("dispatch", { requestId: diagnosticId });
        entry.waiter = invokeSend(captured, { signal, timeoutMilliseconds: 30_000 });
        const admission = await entry.waiter;
        if (observeAdmission(admission, capability) && !signal.aborted && capability.canSubmit(captured) && admission.epoch === captured.expectedEpoch) {
          if ((["accepted", "replay"].includes(admission.status) && admission.receipt && matchesSend(captured, admission.receipt)) || definiteRefusal(admission, false)) {
            sends.delete(key); result = admission;
          } else result = { status: "uncertain", epoch: captured.expectedEpoch, receipt: null, reason: admission.status };
        } else if (!signal.aborted && !capability.canMutate()) result = { status: "stale_epoch", epoch: captured.expectedEpoch, receipt: null };
      } catch (error) {
        sendDiagnostics.warn("transport failure; original request retained, not replayed", {
          requestId: diagnosticId, code: rpcFailureCode(error), elapsedMs: Date.now() - started, aborted: signal.aborted,
        });
        // Transport failure/cancellation is not non-admission. Preserve exact uncertainty.
        result = { status: "uncertain", epoch: captured.expectedEpoch, receipt: null, reason: rpcFailureCode(error) };
      }
      finally {
        sendDiagnostics.info("settled", { requestId: diagnosticId, uncertain: result.status === "uncertain", elapsedMs: Date.now() - started });
        entry.inFlight = false; entry.waiter = undefined;
        const accepted = result.status === "accepted" || result.status === "replay";
        if (accepted && !outgoing.has(echoKey)) showEcho("accepted");
        const shown = outgoing.get(echoKey);
        if (shown) outgoing.set(echoKey, Object.freeze({ ...shown, runId: result.receipt?.runId ?? shown.runId,
          state: accepted ? "accepted" : result.status === "uncertain" ? "uncertain" : "failed" }));
        change.changed();
      }
      if (!signal.aborted) publish(result);
    },
    async abort(intent: AbortIntent, signal: AbortSignal, capability: Capability, publish: (result: SubmissionResult) => void): Promise<void> {
      if (!intent?.request || !identity(intent.sessionId, 256) || !identity(intent.request.expectedEpoch, 64)
        || !identity(intent.request.clientRequestId, 256) || !guid(intent.request.targetOperationId)
        || signal.aborted || !capability.canSubmit(intent.request)) return;
      const key = intent.request.targetOperationId; let entry = aborts.get(key);
      if (entry && (entry.inFlight || entry.intent !== intent)) return;
      if (!entry) {
        if (sends.size + aborts.size >= 256) { publish({ status: "capacity", epoch: intent.request.expectedEpoch, receipt: null }); return; }
        entry = { intent: Object.freeze({ sessionId: intent.sessionId, request: Object.freeze({ expectedEpoch: intent.request.expectedEpoch,
          clientRequestId: intent.request.clientRequestId, targetOperationId: intent.request.targetOperationId }) }), inFlight: false };
        aborts.set(key, entry);
      }
      entry.inFlight = true;
      change.changed();
      const captured = entry.intent;
      let result: SubmissionResult = { status: "uncertain", epoch: captured.request.expectedEpoch, receipt: null };
      try {
        entry.waiter = invokeAbort(captured.request, { signal, timeoutMilliseconds: 8_000 });
        const admission = await entry.waiter;
        if (observeAdmission(admission, capability) && !signal.aborted && capability.canSubmit(captured.request) && admission.epoch === captured.request.expectedEpoch) {
          if ((["accepted", "replay"].includes(admission.status) && admission.receipt && matchesAbort(captured, admission.receipt)) || definiteRefusal(admission, true)) {
            aborts.delete(key); result = admission;
          }
        } else if (!signal.aborted && !capability.canMutate()) result = { status: "stale_epoch", epoch: captured.request.expectedEpoch, receipt: null };
      } catch { /* Keep the original operation/key/session; never retarget cancellation. */ }
      finally { entry.inFlight = false; entry.waiter = undefined; change.changed(); }
      if (!signal.aborted) publish(result);
    },
  };
}

export async function refreshSubmissions(
  invoke: (request: SessionReceiptRequest, options: WaitOptions) => Promise<SessionReceiptPage>,
  epoch: string, offset: number, signal: AbortSignal, publish: (result: SessionReceiptPage) => void,
  capability: Capability,
): Promise<void> {
  if (signal.aborted || !identity(epoch, 64) || !Number.isInteger(offset) || offset < 0 || offset > 256 || offset % 64 !== 0) return;
  try {
    const result = await invoke({ expectedEpoch: epoch, offset }, { signal, timeoutMilliseconds: 8_000 });
    if (!validPage(result)) {
      if (!signal.aborted) publish({ status: "invalid_response", epoch, rows: [], next: null });
      return;
    }
    capability.observe(result); // Invalidate independently of obsolete presentation, including manual reads.
    if (!signal.aborted) publish(result);
  } catch {
    if (!signal.aborted) publish({ status: "read_failed", epoch, rows: [], next: null });
  }
}
