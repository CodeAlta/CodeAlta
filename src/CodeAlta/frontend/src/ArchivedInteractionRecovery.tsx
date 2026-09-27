import { useSyncExternalStore } from "react";
import type { createAskActions } from "./sessionAsks";
import type { createUserInputReviewer } from "./sessionUserInput";
import type { createPermissionReviewer } from "./sessionPermissions";
import { useShellLanguage } from "./shellLanguage";

// Local owner snapshots only. In particular, permission observeDecision acknowledges terminal
// state and must never be called from this archived read-only projection.
export function ArchivedInteractionRecovery({ epoch, sessionId, asks, inputs, permissions }: {
  epoch: string; sessionId: string; asks: ReturnType<typeof createAskActions>;
  inputs: ReturnType<typeof createUserInputReviewer>; permissions: ReturnType<typeof createPermissionReviewer>;
}) {
  const { t } = useShellLanguage();
  useSyncExternalStore(asks.subscribe, asks.getSnapshot);
  useSyncExternalStore(inputs.subscribe, inputs.getSnapshot);
  useSyncExternalStore(permissions.subscribe, permissions.getSnapshot);
  const retainedAsks = asks.forSession(sessionId).filter(entry => entry.request.expectedHostEpoch === epoch);
  const input = inputs.readOriginal();
  const originalInput = input?.request.expectedHostEpoch === epoch && input.request.handle.sessionId === sessionId ? input : null;
  const permission = permissions.readOriginal();
  const originalPermission = permission?.origin.expectedHostEpoch === epoch && permission.origin.handle.sessionId === sessionId ? permission : null;
  if (!retainedAsks.length && !originalInput && !originalPermission) return null;
  return <section aria-label={t("Archived interaction recovery")} className="archived-action-recovery">
    <h2>{t("Archived interactions — original decisions (read-only)")}</h2>
    <p>{t("Retained in this app instance only. Outcomes may be uncertain; no list, observation, acknowledgment, resolution or retry is started here.")}</p>
    {retainedAsks.map(entry => {
      const action = entry.request.action;
      const handle = action.handle;
      return <div key={action.actionId}><h3>{t("Ask")} {entry.kind} · {entry.transport}</h3>
        <p>{t("Host")} <code>{entry.request.expectedHostEpoch}</code> · {t("session")} <code>{handle.sessionId}</code> · {t("operation")} <code>{handle.operationId}</code>
          · {t("runtime")} <code>{handle.runtimeInstanceId}</code> · {t("attachment")} <code>{handle.attachmentGeneration}</code>
          · {t("provider")} <code>{handle.providerId}</code> · {t("run")} <code>{handle.runId}</code> · {t("ask")} <code>{handle.askId}</code>
          · {t("response generation")} <code>{handle.responseGeneration}</code> · {t("action")} <code>{action.actionId}</code></p>
        {action.answers.map(answer => <div key={answer.questionIndex}>{t("Question")} {answer.questionIndex} · {t("choices")} {answer.selectedChoiceIndexes.join(", ") || t("none")}
          {answer.freeformText !== null && <pre aria-label={t("Retained Ask answer text")}>{answer.freeformText}</pre>}</div>)}
        {entry.result && <p>{t("Original response:")} {entry.result.status}{entry.result.runId && ` · ${t("run")} ${entry.result.runId}`}</p>}
        {entry.observed && <p>{t("Earlier explicit observation:")} {entry.observed.status}{entry.observed.runId && ` · ${t("run")} ${entry.observed.runId}`}</p>}
      </div>;
    })}
    {originalInput && <div><h3>{t("Provider input")} {originalInput.action} · {originalInput.kind} ({originalInput.status})</h3>
      <p>{t("Host")} <code>{originalInput.request.expectedHostEpoch}</code> · {t("session")} <code>{originalInput.request.handle.sessionId}</code>
        · {t("operation")} <code>{originalInput.request.handle.operationId}</code> · {t("runtime")} <code>{originalInput.request.handle.runtimeInstanceId}</code>
        · {t("attachment")} <code>{originalInput.request.handle.attachmentGeneration}</code> · {t("run")} <code>{originalInput.request.handle.runId ?? t("none")}</code>
        · {t("interaction")} <code>{originalInput.request.handle.interactionId}</code> · {t("attempt")} <code>{originalInput.request.handle.attemptId}</code></p>
      {originalInput.request.answers.map(answer => <div key={answer.promptId}>{t("Prompt")} {answer.promptId}<pre aria-label={t("Retained provider input text")}>{answer.value}</pre></div>)}
    </div>}
    {originalPermission && <div><h3>{t("Permission")} {originalPermission.origin.decision} · {originalPermission.state}</h3>
      <p>{t("Host")} <code>{originalPermission.origin.expectedHostEpoch}</code> · {t("session")} <code>{originalPermission.origin.handle.sessionId}</code>
        · {t("operation")} <code>{originalPermission.origin.handle.operationId}</code> · {t("runtime")} <code>{originalPermission.origin.handle.runtimeInstanceId}</code>
        · {t("attachment")} <code>{originalPermission.origin.handle.attachmentGeneration}</code> · {t("run")} <code>{originalPermission.origin.handle.runId ?? t("none")}</code>
        · {t("interaction")} <code>{originalPermission.origin.handle.interactionId}</code> · {t("attempt")} <code>{originalPermission.origin.handle.attemptId}</code></p>
      <p>{t("Provider")} <code>{originalPermission.providerId}</code> · {t("directory")} <code>{originalPermission.workingDirectory}</code></p>
      <pre aria-label={t("Retained permission command")}>{originalPermission.command}</pre>
      {originalPermission.reason !== null && <pre aria-label={t("Retained permission reason")}>{originalPermission.reason}</pre>}
      {originalPermission.code && <p>{t("Original response:")} {originalPermission.code}</p>}
    </div>}
  </section>;
}
