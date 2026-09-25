import { useSyncExternalStore } from "react";
import type { createOwnedSubmissions } from "./sessionOperations";
import type { createSteeringSubmissions } from "./sessionSteering";
import type { createCompactionSubmissions } from "./sessionCompaction";
import type { createAbortRunSubmissions } from "./sessionAbortRun";
import type { createQueueSubmissions } from "./sessionQueue";

// Read-only projection of exact app-owned intents. No RPCs, receipt reconciliation or retry controls.
export function ArchivedActionRecovery({ epoch, sessionId, submissions, steering, compaction, abortRuns, queue }: {
  epoch: string; sessionId: string;
  submissions: ReturnType<typeof createOwnedSubmissions>; steering: ReturnType<typeof createSteeringSubmissions>;
  compaction: ReturnType<typeof createCompactionSubmissions>; abortRuns: ReturnType<typeof createAbortRunSubmissions>;
  queue: ReturnType<typeof createQueueSubmissions>;
}) {
  useSyncExternalStore(submissions.subscribe, submissions.getSnapshot);
  useSyncExternalStore(steering.subscribe, steering.getSnapshot);
  useSyncExternalStore(compaction.subscribe, compaction.getSnapshot);
  useSyncExternalStore(abortRuns.subscribe, abortRuns.getSnapshot);
  useSyncExternalStore(queue.subscribe, queue.getSnapshot);
  const matching = (target: { expectedEpoch: string; sessionId?: string }, id = target.sessionId) =>
    target.expectedEpoch === epoch && id === sessionId;
  const send = submissions.pending(sessionId);
  const steer = steering.pending(sessionId);
  const compact = compaction.pending(sessionId);
  const cancel = abortRuns.pending(sessionId);
  const queued = queue.pending(sessionId);
  const aborts = submissions.aborts(sessionId).filter(item => matching(item.intent.request, item.intent.sessionId));
  const cancellations = queue.cancellations(sessionId).filter(item => matching(item.intent.request, item.intent.sessionId));
  const capturedSend = send && matching(send.request) ? send : null;
  const capturedSteer = steer && matching(steer.request) ? steer : null;
  const capturedCompact = compact && matching(compact.request) ? compact : null;
  const capturedCancel = cancel && matching(cancel.request) ? cancel : null;
  const capturedQueue = queued && matching(queued.request) ? queued : null;
  if (!capturedSend && !capturedSteer && !capturedCompact && !capturedCancel && !capturedQueue && !aborts.length && !cancellations.length) return null;
  const state = (inFlight: boolean) => inFlight ? "Original waiter pending" : "Outcome uncertain; original waiter settled";
  return <section aria-label="Archived owned action recovery" className="archived-action-recovery">
    <h2>Archived session — retained owner evidence (read-only)</h2>
    <p>No new submissions, cancellation, retries or retargeting are available here. This evidence is in this app instance only;
      an admitted action may have completed independently. Inspect receipts outside this archived scope.</p>
    {capturedSend && <div><h3>Send · {state(capturedSend.inFlight)}</h3>
      <p>Host <code>{epoch}</code> · session <code>{sessionId}</code> · request <code>{capturedSend.request.clientRequestId}</code></p>
      <pre aria-label="Retained Send text">{capturedSend.request.text}</pre>
      {capturedSend.request.selection && <p>Captured selection: {JSON.stringify(capturedSend.request.selection)}</p>}</div>}
    {capturedSteer && <div><h3>Steer · {state(capturedSteer.inFlight)}</h3>
      <p>Host <code>{epoch}</code> · session <code>{sessionId}</code> · runtime <code>{capturedSteer.request.expectedRuntimeInstanceId}</code>
        · attachment <code>{capturedSteer.request.expectedAttachmentGeneration}</code> · run <code>{capturedSteer.request.expectedRunId}</code>
        · request <code>{capturedSteer.request.clientRequestId}</code></p><pre aria-label="Retained Steer text">{capturedSteer.request.text}</pre></div>}
    {capturedQueue && <div><h3>Host-only Queue · {state(capturedQueue.inFlight)}</h3>
      <p>Host <code>{epoch}</code> · session <code>{sessionId}</code> · runtime <code>{capturedQueue.request.expectedRuntimeInstanceId}</code>
        · attachment <code>{capturedQueue.request.expectedAttachmentGeneration}</code> · request <code>{capturedQueue.request.clientRequestId}</code></p>
      <pre aria-label="Retained Queue text">{capturedQueue.request.text}</pre></div>}
    {capturedCompact && <div><h3>Compact · {state(capturedCompact.inFlight)}</h3>
      <p>Host <code>{epoch}</code> · session <code>{sessionId}</code> · runtime <code>{capturedCompact.request.expectedRuntimeInstanceId}</code>
        · attachment <code>{capturedCompact.request.expectedAttachmentGeneration}</code> · request <code>{capturedCompact.request.clientRequestId}</code></p></div>}
    {capturedCancel && <div><h3>Observed-run cancellation · {state(capturedCancel.inFlight)}</h3>
      <p>Host <code>{epoch}</code> · session <code>{sessionId}</code> · runtime <code>{capturedCancel.request.expectedRuntimeInstanceId}</code>
        · attachment <code>{capturedCancel.request.expectedAttachmentGeneration}</code> · run <code>{capturedCancel.request.expectedRunId}</code>
        · request <code>{capturedCancel.request.clientRequestId}</code></p></div>}
    {aborts.map(item => <div key={item.intent.request.targetOperationId}><h3>Send Abort · {state(item.inFlight)}</h3>
      <p>Host <code>{epoch}</code> · session <code>{sessionId}</code> · original operation <code>{item.intent.request.targetOperationId}</code>
        · request <code>{item.intent.request.clientRequestId}</code></p></div>)}
    {cancellations.map(item => <div key={item.intent.request.targetOperationId}><h3>Queue cancellation · {state(item.inFlight)}</h3>
      <p>Host <code>{epoch}</code> · session <code>{sessionId}</code> · original operation <code>{item.intent.request.targetOperationId}</code>
        · request <code>{item.intent.request.clientRequestId}</code></p></div>)}
  </section>;
}
