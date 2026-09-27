import { useEffect, useRef, useState } from "react";
import type { createPermissionReviewer, PermissionDecisionObservation, PermissionReviewState } from "./sessionPermissions";
import { useShellLanguage } from "./shellLanguage";

export function CommandPermissionPanel({ reviewer, epoch, sessionId }: {
  reviewer: ReturnType<typeof createPermissionReviewer>; epoch: string; sessionId: string;
}) {
  const { t } = useShellLanguage();
  const [state, setState] = useState<PermissionReviewState>();
  const [observation, setObservation] = useState<PermissionDecisionObservation | null>();
  const scope = useRef<ReturnType<typeof reviewer.forSelection> | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setState(undefined);
    setObservation(undefined); // Presentation only; never acknowledge the App-owned record on mount/selection.
    scope.current = reviewer.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, setState);
    return () => { controller.abort(); scope.current = null; };
  }, [reviewer, epoch, sessionId]);
  return <section aria-label={t("Pending command permissions")}>
    <h3>{t("Pending plain commands — explicit review enabled")}</h3>
    <p className="detail">{t("Manual refresh only, at most four pending commands for this exact session. Review the complete command and directory before allowing it. Allow once can execute with the host's privileges; these roots are not a sandbox. Unsupported permission kinds/extensions remain denied. Only in-process built-in tools honor the per-send callback; other providers and custom tools are not implicitly rebound.")}</p>
    <p className="detail">{t("Deny and Cancel resolve this permission, not the entire run. Switching sessions or closing the review does not cancel pending permissions or revoke an accepted decision. Use the exact submission's Abort control separately. There is no durable recovery or execution acknowledgment.")}</p>
    <p className="detail">{t("One original decision response is retained in this renderer across selection and remount. Observe it explicitly before refreshing for another review; pending or uncertain decisions cannot be replaced or resent. Observation is local only. Renderer reload loses this record and permits only fresh manual pending reads in the same host; an empty list cannot recover a decision. Host restart recovers no old authority.")}</p>
    <button type="button" onClick={() => setObservation(scope.current?.observeDecision() ?? null)}>{t("Observe retained decision")}</button>
    {observation === null && <p role="status">{t("No decision is retained in this renderer. No host state was read or inferred.")}</p>}
    {observation && <section aria-label={t("Original permission decision observation")}>
      <h4>{t("Original decision — last explicit local observation")}</h4>
      <dl>
        <dt>{t("Original session (not the selected review)")}</dt><dd>{observation.origin.handle.sessionId}</dd>
        <dt>{t("Clicked decision")}</dt><dd>{observation.origin.decision}</dd>
        <dt>{t("Host epoch")}</dt><dd>{observation.origin.expectedHostEpoch}</dd>
        <dt>{t("Submission operation")}</dt><dd>{observation.origin.handle.operationId}</dd>
        <dt>{t("Runtime / attachment")}</dt><dd>{observation.origin.handle.runtimeInstanceId} / {observation.origin.handle.attachmentGeneration}</dd>
        <dt>{t("Run")}</dt><dd>{observation.origin.handle.runId ?? t("Not supplied by provider")}</dd>
        <dt>{t("Interaction / attempt")}</dt><dd>{observation.origin.handle.interactionId} / {observation.origin.handle.attemptId}</dd>
      </dl>
      <p role="status">{observation.state === "pending" ? t("Original response still pending. This observation does not acknowledge a terminal result or enable another decision. Observe again explicitly to check local state.")
        : observation.state === "error" ? t("Original response unavailable ({code}). Observation cannot clear uncertainty or epoch invalidation; review remains disabled until reload. Reload does not revoke a decision.", { code: String(observation.code) })
        : observation.state === "resolved" ? t("Host accepted this original decision, not proof of command execution, run completion, rollback or revocation. Terminal response now explicitly observed; refresh the selected session for a fresh review.")
        : t("Host rejected this original decision request; this does not identify an earlier decision. Terminal response now explicitly observed; refresh the selected session for a fresh review.")}</p>
    </section>}
    <h4>{t("Selected-session pending review: {session}", { session: sessionId })}</h4>
    <button type="button" disabled={state?.kind === "resolving" || (state?.kind === "error" && state.reloadRequired)} onClick={() => void scope.current?.refresh()}>{t("Refresh pending commands")}</button>
    {state?.kind === "loading" && <p role="status">{t("Reading pending commands…")}</p>}
    {state?.kind === "resolving" && <p role="status">{t("Decision response pending; no retry. Use local observation after selection or remount.")}</p>}
    {state?.kind === "error" && <p role="alert">{t("Command review unavailable ({code}).", { code: state.code })} {state.reloadRequired ? t("Reload the renderer, then refresh pending commands for the selected session. Until reload, refresh and review actions are disabled across selections. An uncertain response may already have committed. Reload does not revoke it.") : t("No permission was inferred. Refresh explicitly to retry the read.")}</p>}
    {state?.kind === "result" && <p role="status">{t(state.code === "resolved" ? "Decision accepted — not proof of command execution or run completion." : "Decision rejected — the attempt may be stale, canceled or already resolved.")} {t("Live publication is not acknowledgment. Explicitly observe the retained decision, then refresh for a fresh review.")}</p>}
    {state?.kind === "ready" && <>
      {state.entries.length === 0 && <p role="status">{t("No pending supported commands observed for this session. This does not mean the provider is idle.")}</p>}
      {state.hasMore && <p role="status">{t("More commands are pending. Resolve entries and refresh to see the next window.")}</p>}
      <ol className="history-records">{state.entries.map(entry => <li key={entry.handle.attemptId}>
        <dl>
          <dt>{t("Provider")}</dt><dd>{entry.providerId}</dd>
          <dt>{t("Submission operation")}</dt><dd>{entry.handle.operationId}</dd>
          <dt>{t("Run")}</dt><dd>{entry.handle.runId ?? t("Not supplied by provider")}</dd>
          <dt>{t("Working directory")}</dt><dd><pre>{entry.workingDirectory}</pre></dd>
        </dl>
        <p>{t("Command (complete)")}</p><pre>{entry.command}</pre>
        {entry.reason !== null && <><p>{t("Reason")}</p><pre>{entry.reason}</pre></>}
        <div className="history-controls">
          <button type="button" onClick={() => { setObservation(undefined); void scope.current?.decide(entry, "allow_once"); }}>{t("Allow once")}</button>
          <button type="button" onClick={() => { setObservation(undefined); void scope.current?.decide(entry, "deny"); }}>{t("Deny")}</button>
          <button type="button" onClick={() => { setObservation(undefined); void scope.current?.decide(entry, "cancel"); }}>{t("Cancel")}</button>
        </div>
      </li>)}</ol>
    </>}
  </section>;
}
