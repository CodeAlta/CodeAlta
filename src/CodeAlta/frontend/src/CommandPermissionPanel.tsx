import { useEffect, useRef, useState } from "react";
import type { createPermissionReviewer, PermissionReviewState } from "./sessionPermissions";

export function CommandPermissionPanel({ reviewer, epoch, sessionId }: {
  reviewer: ReturnType<typeof createPermissionReviewer>; epoch: string; sessionId: string;
}) {
  const [state, setState] = useState<PermissionReviewState>();
  const scope = useRef<ReturnType<typeof reviewer.forSelection> | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setState(undefined);
    scope.current = reviewer.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, setState);
    return () => { controller.abort(); scope.current = null; };
  }, [reviewer, epoch, sessionId]);
  return <section aria-label="Pending command permissions">
    <h3>Pending plain commands — explicit review enabled</h3>
    <p className="detail">Manual refresh only, at most four pending commands for this exact session. Review the complete command and directory before allowing it. Allow once can execute with the host's privileges; these roots are not a sandbox. Unsupported permission kinds/extensions remain denied. Only in-process built-in tools honor the per-send callback; other providers and custom tools are not implicitly rebound.</p>
    <p className="detail">Deny and Cancel resolve this permission, not the entire run. Switching sessions or closing the review does not cancel pending permissions or revoke an accepted decision. Use the exact submission's Abort control separately. There is no durable recovery or execution acknowledgment.</p>
    <button type="button" disabled={state?.kind === "resolving" || (state?.kind === "error" && state.reloadRequired)} onClick={() => void scope.current?.refresh()}>Refresh pending commands</button>
    {state?.kind === "loading" && <p role="status">Reading pending commands…</p>}
    {state?.kind === "resolving" && <p role="status">Decision response pending; no automatic retry.</p>}
    {state?.kind === "error" && <p role="alert">Command review unavailable ({state.code}). {state.reloadRequired ? "Reload the renderer, then refresh pending commands for the selected session. Until reload, refresh and review actions are disabled across selections. An uncertain response may already have committed. Reload does not revoke it." : "No permission was inferred. Refresh explicitly to retry the read."}</p>}
    {state?.kind === "result" && <p role="status">{state.code === "resolved" ? "Decision accepted — not proof of command execution or run completion." : "Decision rejected — the attempt may be stale, canceled or already resolved."} Refresh to review current pending commands.</p>}
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
          <button type="button" onClick={() => void scope.current?.decide(entry, "allow_once")}>Allow once</button>
          <button type="button" onClick={() => void scope.current?.decide(entry, "deny")}>Deny</button>
          <button type="button" onClick={() => void scope.current?.decide(entry, "cancel")}>Cancel</button>
        </div>
      </li>)}</ol>
    </>}
  </section>;
}
