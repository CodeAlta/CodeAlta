import { useSyncExternalStore } from "react";
import type { createAskActions } from "./sessionAsks";
import type { createUserInputReviewer } from "./sessionUserInput";
import type { createPermissionReviewer } from "./sessionPermissions";

// Local owner snapshots only. In particular, permission observeDecision acknowledges terminal
// state and must never be called from this archived read-only projection.
export function ArchivedInteractionRecovery({ epoch, sessionId, asks, inputs, permissions }: {
  epoch: string; sessionId: string; asks: ReturnType<typeof createAskActions>;
  inputs: ReturnType<typeof createUserInputReviewer>; permissions: ReturnType<typeof createPermissionReviewer>;
}) {
  useSyncExternalStore(asks.subscribe, asks.getSnapshot);
  useSyncExternalStore(inputs.subscribe, inputs.getSnapshot);
  useSyncExternalStore(permissions.subscribe, permissions.getSnapshot);
  const retainedAsks = asks.forSession(sessionId).filter(entry => entry.request.expectedHostEpoch === epoch);
  const input = inputs.readOriginal();
  const originalInput = input?.request.expectedHostEpoch === epoch && input.request.handle.sessionId === sessionId ? input : null;
  const permission = permissions.readOriginal();
  const originalPermission = permission?.origin.expectedHostEpoch === epoch && permission.origin.handle.sessionId === sessionId ? permission : null;
  if (!retainedAsks.length && !originalInput && !originalPermission) return null;
  return <section aria-label="Archived interaction recovery" className="archived-action-recovery">
    <h2>Archived interactions — original decisions (read-only)</h2>
    <p>Retained in this app instance only. Outcomes may be uncertain; no list, observation, acknowledgment, resolution or retry is started here.</p>
    {retainedAsks.map(entry => {
      const action = entry.request.action;
      const handle = action.handle;
      return <div key={action.actionId}><h3>Ask {entry.kind} · {entry.transport}</h3>
        <p>Host <code>{entry.request.expectedHostEpoch}</code> · session <code>{handle.sessionId}</code> · operation <code>{handle.operationId}</code>
          · runtime <code>{handle.runtimeInstanceId}</code> · attachment <code>{handle.attachmentGeneration}</code>
          · provider <code>{handle.providerId}</code> · run <code>{handle.runId}</code> · ask <code>{handle.askId}</code>
          · response generation <code>{handle.responseGeneration}</code> · action <code>{action.actionId}</code></p>
        {action.answers.map(answer => <div key={answer.questionIndex}>Question {answer.questionIndex} · choices {answer.selectedChoiceIndexes.join(", ") || "none"}
          {answer.freeformText !== null && <pre aria-label="Retained Ask answer text">{answer.freeformText}</pre>}</div>)}
        {entry.result && <p>Original response: {entry.result.status}{entry.result.runId && ` · run ${entry.result.runId}`}</p>}
        {entry.observed && <p>Earlier explicit observation: {entry.observed.status}{entry.observed.runId && ` · run ${entry.observed.runId}`}</p>}
      </div>;
    })}
    {originalInput && <div><h3>Provider input {originalInput.action} · {originalInput.kind} ({originalInput.status})</h3>
      <p>Host <code>{originalInput.request.expectedHostEpoch}</code> · session <code>{originalInput.request.handle.sessionId}</code>
        · operation <code>{originalInput.request.handle.operationId}</code> · runtime <code>{originalInput.request.handle.runtimeInstanceId}</code>
        · attachment <code>{originalInput.request.handle.attachmentGeneration}</code> · run <code>{originalInput.request.handle.runId ?? "none"}</code>
        · interaction <code>{originalInput.request.handle.interactionId}</code> · attempt <code>{originalInput.request.handle.attemptId}</code></p>
      {originalInput.request.answers.map(answer => <div key={answer.promptId}>Prompt {answer.promptId}<pre aria-label="Retained provider input text">{answer.value}</pre></div>)}
    </div>}
    {originalPermission && <div><h3>Permission {originalPermission.origin.decision} · {originalPermission.state}</h3>
      <p>Host <code>{originalPermission.origin.expectedHostEpoch}</code> · session <code>{originalPermission.origin.handle.sessionId}</code>
        · operation <code>{originalPermission.origin.handle.operationId}</code> · runtime <code>{originalPermission.origin.handle.runtimeInstanceId}</code>
        · attachment <code>{originalPermission.origin.handle.attachmentGeneration}</code> · run <code>{originalPermission.origin.handle.runId ?? "none"}</code>
        · interaction <code>{originalPermission.origin.handle.interactionId}</code> · attempt <code>{originalPermission.origin.handle.attemptId}</code></p>
      <p>Provider <code>{originalPermission.providerId}</code> · directory <code>{originalPermission.workingDirectory}</code></p>
      <pre aria-label="Retained permission command">{originalPermission.command}</pre>
      {originalPermission.reason !== null && <pre aria-label="Retained permission reason">{originalPermission.reason}</pre>}
      {originalPermission.code && <p>Original response: {originalPermission.code}</p>}
    </div>}
  </section>;
}
