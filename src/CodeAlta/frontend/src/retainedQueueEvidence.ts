import type { createQueueSubmissions } from "./sessionQueue";

// Local unresolved originals only. Receipts cannot reconstruct text or a queue inventory.
export function retainedQueueEvidence(owner: ReturnType<typeof createQueueSubmissions>, epoch: string, sessionId: string) {
  const pending = owner.pending(sessionId);
  const queue = pending?.request.expectedEpoch === epoch ? pending : undefined;
  const cancellations = owner.cancellations(sessionId).filter(value => value.intent.request.expectedEpoch === epoch);
  return queue || cancellations.length ? { queue, cancellations, revision: owner.getSnapshot() } : null;
}
