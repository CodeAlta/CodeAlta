import { useEffect, useRef, useState } from "react";
import type { createPermissionReviewer, PermissionDecisionObservation, PermissionReviewState } from "./sessionPermissions";

export function CommandPermissionPanel({ reviewer, epoch, sessionId }: {
  reviewer: ReturnType<typeof createPermissionReviewer>; epoch: string; sessionId: string;
}) {
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
  return <section aria-label="Pending command permissions">
    <h3>Pending plain commands — explicit review enabled</h3>
    <p className="detail">Manual refresh only, at most four pending commands for this exact session. Review the complete command and directory before allowing it. Allow once can execute with the host's privileges; these roots are not a sandbox. Unsupported permission kinds/extensions remain denied. Only in-process built-in tools honor the per-send callback; other providers and custom tools are not implicitly rebound.</p>
    <p className="detail">Deny and Cancel resolve this permission, not the entire run. Switching sessions or closing the review does not cancel pending permissions or revoke an accepted decision. Use the exact submission's Abort control separately. There is no durable recovery or execution acknowledgment.</p>
    <p className="detail">One original decision response is retained in this renderer across selection and remount. Observe it explicitly before refreshing for another review; pending or uncertain decisions cannot be replaced or resent. Observation is local only. Renderer reload loses this record and permits only fresh manual pending reads in the same host; an empty list cannot recover a decision. Host restart recovers no old authority.</p>
    <button type="button" onClick={() => setObservation(scope.current?.observeDecision() ?? null)}>Observe retained decision</button>
    {observation === null && <p role="status">No decision is retained in this renderer. No host state was read or inferred.</p>}
    {observation && <section aria-label="Original permission decision observation">
      <h4>Original decision — last explicit local observation</h4>
      <dl>
        <dt>Original session (not the selected review)</dt><dd>{observation.origin.handle.sessionId}</dd>
        <dt>Clicked decision</dt><dd>{observation.origin.decision}</dd>
        <dt>Host epoch</dt><dd>{observation.origin.expectedHostEpoch}</dd>
        <dt>Submission operation</dt><dd>{observation.origin.handle.operationId}</dd>
        <dt>Runtime / attachment</dt><dd>{observation.origin.handle.runtimeInstanceId} / {observation.origin.handle.attachmentGeneration}</dd>
        <dt>Run</dt><dd>{observation.origin.handle.runId ?? "Not supplied by provider"}</dd>
        <dt>Interaction / attempt</dt><dd>{observation.origin.handle.interactionId} / {observation.origin.handle.attemptId}</dd>
      </dl>
      <p role="status">{observation.state === "pending" ? "Original response still pending. This observation does not acknowledge a terminal result or enable another decision. Observe again explicitly to check local state."
        : observation.state === "error" ? `Original response unavailable (${observation.code}). Observation cannot clear uncertainty or epoch invalidation; review remains disabled until reload. Reload does not revoke a decision.`
        : observation.state === "resolved" ? "Host accepted this original decision, not proof of command execution, run completion, rollback or revocation. Terminal response now explicitly observed; refresh the selected session for a fresh review."
        : "Host rejected this original decision request; this does not identify an earlier decision. Terminal response now explicitly observed; refresh the selected session for a fresh review."}</p>
    </section>}
    <h4>Selected-session pending review: {sessionId}</h4>
    <button type="button" disabled={state?.kind === "resolving" || (state?.kind === "error" && state.reloadRequired)} onClick={() => void scope.current?.refresh()}>Refresh pending commands</button>
    {state?.kind === "loading" && <p role="status">Reading pending commands…</p>}
    {state?.kind === "resolving" && <p role="status">Decision response pending; no retry. Use local observation after selection or remount.</p>}
    {state?.kind === "error" && <p role="alert">Command review unavailable ({state.code}). {state.reloadRequired ? "Reload the renderer, then refresh pending commands for the selected session. Until reload, refresh and review actions are disabled across selections. An uncertain response may already have committed. Reload does not revoke it." : "No permission was inferred. Refresh explicitly to retry the read."}</p>}
    {state?.kind === "result" && <p role="status">{state.code === "resolved" ? "Decision accepted — not proof of command execution or run completion." : "Decision rejected — the attempt may be stale, canceled or already resolved."} Live publication is not acknowledgment. Explicitly observe the retained decision, then refresh for a fresh review.</p>}
    {state?.kind === "ready" && <>
      {state.entries.length === 0 && <p role="status">No pending supported commands observed for this session. This does not mean the provider is idle.</p>}
      {state.hasMore && <p role="status">More commands are pending. Resolve entries and refresh to see the next window.</p>}
      <ol className="history-records">{state.entries.map(entry => <li key={entry.handle.attemptId}>
        <dl>
          <dt>Provider</dt><dd>{entry.providerId}</dd>
          <dt>Submission operation</dt><dd>{entry.handle.operationId}</dd>
          <dt>Run</dt><dd>{entry.handle.runId ?? "Not supplied by provider"}</dd>
          <dt>Working directory</dt><dd><pre>{entry.workingDirectory}</pre></dd>
        </dl>
        <p>Command (complete)</p><pre>{entry.command}</pre>
        {entry.reason !== null && <><p>Reason</p><pre>{entry.reason}</pre></>}
        <div className="history-controls">
          <button type="button" onClick={() => { setObservation(undefined); void scope.current?.decide(entry, "allow_once"); }}>Allow once</button>
          <button type="button" onClick={() => { setObservation(undefined); void scope.current?.decide(entry, "deny"); }}>Deny</button>
          <button type="button" onClick={() => { setObservation(undefined); void scope.current?.decide(entry, "cancel"); }}>Cancel</button>
        </div>
      </li>)}</ol>
    </>}
  </section>;
}
