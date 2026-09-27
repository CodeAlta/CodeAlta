import { useSyncExternalStore } from "react";
import type { createOwnedSubmissions } from "./sessionOperations";
import type { createSteeringSubmissions } from "./sessionSteering";
import type { createCompactionSubmissions } from "./sessionCompaction";
import type { createAbortRunSubmissions } from "./sessionAbortRun";
import type { createQueueSubmissions } from "./sessionQueue";
import type { createAskActions } from "./sessionAsks";
import type { createUserInputReviewer } from "./sessionUserInput";
import type { createPermissionReviewer } from "./sessionPermissions";
import { ArchivedInteractionRecovery } from "./ArchivedInteractionRecovery";
import { useShellLanguage } from "./shellLanguage";

// Read-only projection of exact app-owned intents. No RPCs, receipt reconciliation or retry controls.
export function ArchivedActionRecovery({ epoch, sessionId, submissions, steering, compaction, abortRuns, queue, asks, inputs, permissions }: {
  epoch: string; sessionId: string;
  submissions: ReturnType<typeof createOwnedSubmissions>; steering: ReturnType<typeof createSteeringSubmissions>;
  compaction: ReturnType<typeof createCompactionSubmissions>; abortRuns: ReturnType<typeof createAbortRunSubmissions>;
  queue: ReturnType<typeof createQueueSubmissions>;
  asks: ReturnType<typeof createAskActions>; inputs: ReturnType<typeof createUserInputReviewer>;
  permissions: ReturnType<typeof createPermissionReviewer>;
}) {
  const { t } = useShellLanguage();
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
  const interactions = <ArchivedInteractionRecovery epoch={epoch} sessionId={sessionId} asks={asks} inputs={inputs} permissions={permissions} />;
  if (!capturedSend && !capturedSteer && !capturedCompact && !capturedCancel && !capturedQueue && !aborts.length && !cancellations.length) return interactions;
  const state = (inFlight: boolean) => t(inFlight ? "Original waiter pending" : "Outcome uncertain; original waiter settled");
  return <><section aria-label={t("Archived owned action recovery")} className="archived-action-recovery">
    <h2>{t("Archived session — retained owner evidence (read-only)")}</h2>
    <p>{t("No new submissions, cancellation, retries or retargeting are available here. This evidence is in this app instance only; an admitted action may have completed independently. Inspect receipts outside this archived scope.")}</p>
    {capturedSend && <div><h3>{t("Send")} · {state(capturedSend.inFlight)}</h3>
      <p>{t("Host")} <code>{epoch}</code> · {t("session")} <code>{sessionId}</code> · {t("request")} <code>{capturedSend.request.clientRequestId}</code></p>
      <pre aria-label={t("Retained Send text")}>{capturedSend.request.text}</pre>
      {capturedSend.request.selection && <p>{t("Captured selection:")} {JSON.stringify(capturedSend.request.selection)}</p>}</div>}
    {capturedSteer && <div><h3>{t("Steer")} · {state(capturedSteer.inFlight)}</h3>
      <p>{t("Host")} <code>{epoch}</code> · {t("session")} <code>{sessionId}</code> · {t("runtime")} <code>{capturedSteer.request.expectedRuntimeInstanceId}</code>
        · {t("attachment")} <code>{capturedSteer.request.expectedAttachmentGeneration}</code> · {t("run")} <code>{capturedSteer.request.expectedRunId}</code>
        · {t("request")} <code>{capturedSteer.request.clientRequestId}</code></p><pre aria-label={t("Retained Steer text")}>{capturedSteer.request.text}</pre></div>}
    {capturedQueue && <div><h3>{t("Host-only Queue")} · {state(capturedQueue.inFlight)}</h3>
      <p>{t("Host")} <code>{epoch}</code> · {t("session")} <code>{sessionId}</code> · {t("runtime")} <code>{capturedQueue.request.expectedRuntimeInstanceId}</code>
        · {t("attachment")} <code>{capturedQueue.request.expectedAttachmentGeneration}</code> · {t("request")} <code>{capturedQueue.request.clientRequestId}</code></p>
      <pre aria-label={t("Retained Queue text")}>{capturedQueue.request.text}</pre></div>}
    {capturedCompact && <div><h3>{t("Compact")} · {state(capturedCompact.inFlight)}</h3>
      <p>{t("Host")} <code>{epoch}</code> · {t("session")} <code>{sessionId}</code> · {t("runtime")} <code>{capturedCompact.request.expectedRuntimeInstanceId}</code>
        · {t("attachment")} <code>{capturedCompact.request.expectedAttachmentGeneration}</code> · {t("request")} <code>{capturedCompact.request.clientRequestId}</code></p></div>}
    {capturedCancel && <div><h3>{t("Observed-run cancellation")} · {state(capturedCancel.inFlight)}</h3>
      <p>{t("Host")} <code>{epoch}</code> · {t("session")} <code>{sessionId}</code> · {t("runtime")} <code>{capturedCancel.request.expectedRuntimeInstanceId}</code>
        · {t("attachment")} <code>{capturedCancel.request.expectedAttachmentGeneration}</code> · {t("run")} <code>{capturedCancel.request.expectedRunId}</code>
        · {t("request")} <code>{capturedCancel.request.clientRequestId}</code></p></div>}
    {aborts.map(item => <div key={item.intent.request.targetOperationId}><h3>{t("Send Abort")} · {state(item.inFlight)}</h3>
      <p>{t("Host")} <code>{epoch}</code> · {t("session")} <code>{sessionId}</code> · {t("original operation")} <code>{item.intent.request.targetOperationId}</code>
        · {t("request")} <code>{item.intent.request.clientRequestId}</code></p></div>)}
    {cancellations.map(item => <div key={item.intent.request.targetOperationId}><h3>{t("Queue cancellation")} · {state(item.inFlight)}</h3>
      <p>{t("Host")} <code>{epoch}</code> · {t("session")} <code>{sessionId}</code> · {t("original operation")} <code>{item.intent.request.targetOperationId}</code>
        · {t("request")} <code>{item.intent.request.clientRequestId}</code></p></div>)}
  </section>{interactions}</>;
}
