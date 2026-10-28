import type { SessionAdmission, SessionCompactRequest, SessionReceiptPage, SessionReceiptView, SessionRuntimeStateResponse } from "#neoastra";
import type { createMutationCapability, SubmissionResult } from "./sessionOperations";
import { createOwnerChangeSignal } from "./ownerChangeSignal";
import type { MessageKey } from "./localization";

type Capability = ReturnType<typeof createMutationCapability>;
type WaitOptions = { signal: AbortSignal; timeoutMilliseconds: number };
type Pending = { request: SessionCompactRequest; inFlight: boolean };

/** Why an observed session cannot be compacted now. */
export type CompactionBlock = "unknown" | "unattached" | "changing" | "closing" | "running";

// The order says the most useful reason first: a session that is not attached has no run to wait for.
export function compactionBlock(epoch: string, sessionId: string, observation: SessionRuntimeStateResponse | undefined): CompactionBlock | null {
  if (observation?.status !== "ok" || observation.hostEpoch !== epoch || observation.sessionId !== sessionId) return "unknown";
  const entry = observation.entry;
  if (!observation.runtimeInstanceId || !entry) return "unattached";
  if (observation.coordinatorTransitionInProgress !== false) return "changing";
  if (entry.isRetiring || entry.isTerminated) return "closing";
  if (entry.activeRunId !== null || entry.queueDrainInProgress) return "running";
  return null;
}

// Explicit observation supplies attachment eligibility, not proof that the provider is still idle.
export function captureCompaction(epoch: string, sessionId: string, observation: SessionRuntimeStateResponse | undefined,
  key: string): SessionCompactRequest | null {
  if (compactionBlock(epoch, sessionId, observation) !== null) return null;
  return Object.freeze({ expectedEpoch: epoch, clientRequestId: key, sessionId, expectedRuntimeInstanceId: observation!.runtimeInstanceId!,
    expectedAttachmentGeneration: observation!.entry!.attachmentGeneration });
}

/** Why the Compact control of a composer does nothing now, or null when it compacts (or retries its retained request). */
export type CompactControlBlock = "reload" | "compacting" | CompactionBlock;

export function compactControlBlock(state: Readonly<{ invalidEpoch: boolean; compacting: boolean; retained: SessionCompactRequest | null;
  canSubmit: (request: SessionCompactRequest) => boolean }>, epoch: string, sessionId: string,
  observation: SessionRuntimeStateResponse | undefined): CompactControlBlock | null {
  const available = state.retained ?? captureCompaction(epoch, sessionId, observation, "availability");
  if (state.invalidEpoch || available && !state.canSubmit(available)) return "reload";
  if (state.compacting) return "compacting";
  return available ? null : compactionBlock(epoch, sessionId, observation);
}

/** What the control, the command picker and the shortcut say of each reason. */
export const compactControlBlockMessages = Object.freeze({
  reload: "Reload required.",
  compacting: "The conversation is being compacted.",
  unknown: "The state of the session is not known yet. Try again in a moment.",
  unattached: "Available once the session has started: send it a prompt first.",
  changing: "The session is starting or switching its provider. Try again in a moment.",
  closing: "The session is closing.",
  running: "Available once the session is idle.",
} as const satisfies Record<CompactControlBlock, MessageKey>);

/** What the composer says of a compaction that did not complete: a message and its parameters. */
export type CompactionFailure = Readonly<{ key: MessageKey; parameters?: Readonly<Record<string, string>> }>;

// A settled receipt carries a stable code; the message of the provider is in the timeline, where the host recorded it.
export function settledCompactionFailure(row: Pick<SessionReceiptView, "state" | "outcome" | "code">): CompactionFailure | null {
  if (row.state !== "terminal" || row.outcome === "Completed") return null;
  if (row.outcome === "Cancelled") return { key: "The compaction was cancelled." };
  return { key: row.code === "compact_busy" ? "The session is busy: compact it once it is idle."
    : row.code === "compact_unsupported" ? "This provider cannot compact the conversation."
    : row.code === "compact_plugin_cancelled" ? "A plugin cancelled the compaction. The timeline says why."
    : "The compaction failed. The timeline says why." };
}

/** What the composer says when the host did not take a compaction, or took it and has already ended it; null otherwise. */
export function compactionAdmissionFailure(result: SubmissionResult): CompactionFailure | null {
  if (result.status === "accepted" || result.status === "replay") return result.receipt ? settledCompactionFailure(result.receipt) : null;
  switch (result.status) {
    case "busy": return { key: "The session is busy: compact it once it is idle." };
    case "uncertain": return { key: "CodeAlta did not confirm the compaction. Check the timeline before compacting again." };
    case "closed": case "stale_epoch": return { key: "CodeAlta restarted. Reload the window to continue." };
    default: return { key: "The compaction was not accepted ({status}).", parameters: { status: result.status } };
  }
}

/** The end of a watched compaction that did not complete, once a page lists its settled receipt. */
export function watchedCompactionFailure(watch: CompactionWatch, sessionId: string, page: SessionReceiptPage | undefined): CompactionFailure | null {
  if (page?.status !== "ok") return null;
  const row = page.rows.find(item => item.kind === "Compact" && item.sessionId === sessionId && item.clientRequestId === watch.key);
  return row ? settledCompactionFailure(row) : null;
}

function matches(request: SessionCompactRequest, row: SessionReceiptView): boolean {
  return row.kind === "Compact" && row.clientRequestId === request.clientRequestId && row.sessionId === request.sessionId;
}

// A compaction is not a run: its receipt, pending until the host ends it, is what says one is still going on.
export function hasPendingCompaction(sessionId: string, page: SessionReceiptPage | undefined): boolean {
  return page?.status === "ok" && page.rows.some(row => row.kind === "Compact" && row.sessionId === sessionId && row.state === "pending");
}

/** A compaction this composer submitted, followed until the receipts report its end. */
export type CompactionWatch = Readonly<{ key: string; listed: boolean }>;

// Covers the time between the submission and the first page that lists its receipt: a page read before
// the admission does not end the watch. Returns the same watch when the page says nothing new, null at the end.
export function watchCompaction(watch: CompactionWatch, sessionId: string, page: SessionReceiptPage | undefined): CompactionWatch | null {
  if (page?.status !== "ok") return watch;
  const row = page.rows.find(item => item.kind === "Compact" && item.sessionId === sessionId && item.clientRequestId === watch.key);
  if (!row) return watch.listed ? null : watch;
  return row.state === "terminal" ? null : watch.listed ? watch : { ...watch, listed: true };
}

// App-owned bounded retention survives selection loss/remount. No automatic retry or refresh.
export function createCompactionSubmissions(invoke: (request: SessionCompactRequest, options: WaitOptions) => Promise<SessionAdmission>) {
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
      change.changed();
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
          } else if (["conflict", "expired", "busy", "capacity", "closed", "invalid_request"].includes(admission.status)) {
            pending.delete(key);
            result = admission;
          }
        } else if (!signal.aborted && !capability.canMutate()) result = admission;
      } catch {
        // A lost response cannot prove non-admission. Only exact explicit retry or receipt reconciliation releases it.
      } finally {
        retained.inFlight = false;
        change.changed();
      }
      if (!signal.aborted) publish(result);
    },
  };
}
