import { useEffect, useRef, useState } from "react";
import { sessionOperations as sessions, workspace, type SessionReceiptPage, type SessionSendRequest } from "#neoastra";
import { captureSubmission, createMutationCapability, refreshSubmissions, sendSubmission } from "./sessionOperations";
import { historyMessage, loadHistory, type HistoryState } from "./history";
import { LiveSessionPanel } from "./LiveSessionPanel";
import type { createSessionDisplayStore } from "./sessionDisplay";
import type { createRuntimeStateReader, RuntimeState } from "./runtimeState";

export function OwnedSessionPanel({ sessionId, epoch, drafts, capability, display, runtimeReader }: {
  sessionId: string; epoch: string; drafts: Map<string, SessionSendRequest>; capability: ReturnType<typeof createMutationCapability>;
  display: ReturnType<typeof createSessionDisplayStore>;
  runtimeReader: ReturnType<typeof createRuntimeStateReader>;
}) {
  const [text, setText] = useState("");
  const [message, setMessage] = useState("Refresh submissions to recover accepted receipts. Never automatically resend an uncertain request.");
  const [page, setPage] = useState<SessionReceiptPage>();
  const [history, setHistory] = useState<HistoryState>();
  const [runtimeState, setRuntimeState] = useState<RuntimeState>();
  const runtimeScope = useRef<ReturnType<typeof runtimeReader.forSelection> | null>(null);
  const [busy, setBusy] = useState(false);
  const [observedInvalidEpoch, setInvalidEpoch] = useState(!capability.canMutate());
  const invalidEpoch = observedInvalidEpoch || !capability.canMutate();
  const scope = useRef<AbortController | null>(null);
  const receiptRevision = useRef(0);
  const historyRevision = useRef(0);
  useEffect(() => {
    const controller = new AbortController();
    scope.current = controller;
    setText(drafts.get(sessionId)?.text ?? "");
    setPage(undefined);
    setHistory(undefined);
    setRuntimeState(undefined);
    runtimeScope.current = runtimeReader.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, setRuntimeState);
    setBusy(false);
    setMessage("Refresh submissions to recover accepted receipts. Uncertain requests are never resent automatically.");
    return () => { controller.abort(); scope.current = null; runtimeScope.current = null; };
  }, [sessionId, epoch, drafts, runtimeReader]);

  const pending = drafts.get(sessionId);
  function observeEpoch(result: { status: string; epoch: string | null }) {
    if (!capability.observe(result)) setInvalidEpoch(true);
  }
  function submit() {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || busy || invalidEpoch) return;
    if (pending && pending.expectedEpoch !== epoch) { setMessage("Old host epoch: this request cannot be retried against the new host."); return; }
    if (!pending && drafts.size >= 256) { setMessage("Uncertain draft limit reached. Review existing requests first."); return; }
    const request = pending ?? captureSubmission(epoch, sessionId, text, crypto.randomUUID());
    if (!capability.canSubmit(request)) return;
    drafts.set(sessionId, request); // Keep exact text/key/epoch until an admission result is known.
    setBusy(true);
    setMessage("Submission admission pending…");
    void sendSubmission(sessions.send, request, signal, result => {
      observeEpoch(result);
      setBusy(false);
      setMessage(result.status === "accepted" || result.status === "replay"
        ? "Submission accepted. Refresh submissions for dispatch outcome; this is not run completion."
        : `Submission: ${result.status}. Refresh receipts before considering an explicit retry.`);
      if (capability.canSubmit(request) && ["accepted", "replay", "conflict", "busy", "capacity", "closed", "invalid_request"].includes(result.status)) {
        drafts.delete(sessionId);
        if (result.status === "accepted" || result.status === "replay") setText("");
      }
    }, capability);
  }
  function refresh(offset = 0) {
    const signal = scope.current?.signal;
    if (!signal) return;
    const revision = ++receiptRevision.current;
    void refreshSubmissions(sessions.receipts, epoch, offset, signal, result => {
      if (revision !== receiptRevision.current) return;
      observeEpoch(result);
      setPage(result);
      const uncertain = drafts.get(sessionId);
      if (uncertain && result.status === "ok" && capability.canSubmit(uncertain) && result.epoch === uncertain.expectedEpoch && result.rows.some(row => row.clientRequestId === uncertain.clientRequestId && row.sessionId === uncertain.sessionId)) {
        drafts.delete(sessionId);
        setText("");
        setMessage("Accepted receipt recovered without exposing prompt text.");
      }
    });
  }
  function abort(operationId: string) {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    // Explicit control only. It cannot target a later send or serve as a general Stop-agent action.
    void sessions.abort({ expectedEpoch: epoch, clientRequestId: crypto.randomUUID(), targetOperationId: operationId },
      { signal, timeoutMilliseconds: 8_000 }).then(result => {
        if (!signal.aborted) {
          observeEpoch(result);
          setMessage(`Abort submission: ${result.status}. Refresh submissions for control outcome.`);
        }
      }).catch(() => { if (!signal.aborted) setMessage("Abort response uncertain. Refresh submissions; no automatic retry."); });
  }
  function readHistory(next = false) {
    const signal = scope.current?.signal;
    if (!signal) return;
    const cursor = next && history?.kind === "ready" ? history.page.next : null;
    const revision = ++historyRevision.current;
    void loadHistory(workspace.history, { sessionId, cursor }, signal, value => {
      if (revision === historyRevision.current) setHistory(value);
    });
  }
  return <section className="owned-session" aria-label="Owned text submission">
    <h3>Owned text-only submission</h3>
    <p className="detail">Existing session only. Permissions are denied; user input is cancelled; no tools or plugins. A bounded live status/text window is available below. A submitted receipt is not a completed run. Receipt capacity is 256 for this host lifetime.</p>
    <label>Text<textarea maxLength={32768} value={text} disabled={busy || !!pending} onChange={event => setText(event.target.value)} /></label>
    <div className="history-controls">
      <button type="button" disabled={invalidEpoch || busy || (!pending && !text.trim()) || (!!pending && pending.expectedEpoch !== epoch)} onClick={submit}>{pending ? "Retry exact request" : "Send text"}</button>
      <button type="button" onClick={() => refresh()}>Refresh submissions</button>
    </div>
    <p role="status">{message}</p>
    {invalidEpoch && <p role="alert">Host epoch changed. Reload required; mutations are disabled. The exact uncertain request is retained and will not be rebased or resent.</p>}
    {page && page.status !== "ok" && <p role="alert">Receipt snapshot: {page.status}</p>}
    {page?.rows.filter(row => row.sessionId.toLowerCase() === sessionId.toLowerCase()).map(row => <div key={row.operationId}>
      <p>{row.kind} · {row.outcome === "Completed" ? "submission submitted" : row.outcome === "Failed" ? "submission failed" : row.outcome === "Cancelled" ? "submission cancelled" : "submission pending"} {row.code ?? ""} · {row.operationId}</p>
      {row.kind === "Send" && row.state === "pending" && <button type="button" disabled={invalidEpoch} onClick={() => abort(row.operationId)}>Abort submission</button>}
    </div>)}
    {page?.next != null && <button type="button" onClick={() => refresh(page.next!)}>Next receipt page</button>}
    <LiveSessionPanel store={display} hostEpoch={epoch} sessionId={sessionId} />
    <h3>Current runtime — manual point-in-time observation</h3>
    <p className="detail">Recorded facts at the last refresh, not provider inactivity or successful run completion. Queue depth is unknown. This does not acknowledge effects or synchronize Display, receipts or persisted history.</p>
    <button type="button" disabled={runtimeState?.kind === "error" && ["stale_epoch", "stale_runtime"].includes(runtimeState.code)} onClick={() => void runtimeScope.current?.refresh()}>Refresh runtime state</button>
    {runtimeState?.kind === "loading" && <p role="status">Reading current runtime facts…</p>}
    {runtimeState?.kind === "error" && <p role="alert">Runtime observation unavailable ({runtimeState.code}). {["stale_epoch", "stale_runtime"].includes(runtimeState.code) ? "Reload the Desktop UI before continuing; the old epoch cannot be retried." : "No idle or completion state is inferred."}</p>}
    {runtimeState?.kind === "ready" && <>
      <p className="detail">Runtime instance {runtimeState.snapshot.runtimeInstanceId} · coordinator transition recorded: {runtimeState.snapshot.coordinatorTransitionInProgress ? "yes" : "no"}</p>
      {runtimeState.snapshot.entry ? <>
        <dl>
          <dt>Attachment generation (identity, not revision)</dt><dd>{runtimeState.snapshot.entry.attachmentGeneration}</dd>
          <dt>Active run recorded</dt><dd>{runtimeState.snapshot.entry.activeRunId ?? "No run recorded — provider activity unknown"}</dd>
          <dt>Shutdown observed on entry</dt><dd>{runtimeState.snapshot.entry.isTerminated ? "yes" : "no"}</dd>
          <dt>Attachment retiring</dt><dd>{runtimeState.snapshot.entry.isRetiring ? "yes" : "no"}</dd>
          <dt>Queue drain in progress</dt><dd>{runtimeState.snapshot.entry.queueDrainInProgress ? "yes" : "no"}</dd>
        </dl>
        <p className="detail">Captured configuration — not verified provider-effective settings.</p>
        <dl>
          <dt>Provider / configured key</dt><dd>{runtimeState.snapshot.entry.providerId} / {runtimeState.snapshot.entry.providerKey}</dd>
          <dt>Captured model</dt><dd>{runtimeState.snapshot.entry.modelId ?? "Not recorded"}</dd>
          <dt>Captured reasoning</dt><dd>{runtimeState.snapshot.entry.reasoningEffort ?? "Not recorded"}</dd>
          <dt>Captured prompt</dt><dd>{runtimeState.snapshot.entry.agentPromptId ?? "Not recorded"}</dd>
          <dt>Pending prompt (separate selection)</dt><dd>{runtimeState.snapshot.entry.pendingAgentPromptId ?? "None recorded"}</dd>
        </dl>
      </> : <p>No runtime entry observed. This does not imply idle, completion or absence of a durable session.</p>}
    </>}
    <h3>Persisted history — not live run state</h3>
    <p className="detail">Bounded journal pages; deltas and completed records remain separate. Actual cached-store reads are host-owned. Caller cancellation does not stop them. Copied paths/reparse points are not sandboxed.</p>
    <button type="button" onClick={() => readHistory()}>Restart history</button>
    {history?.kind === "error" && <p role="alert">{historyMessage(history.code)}</p>}
    {history?.kind === "loading" && <p role="status">Reading persisted history…</p>}
    {history?.kind === "ready" && <>
      {history.page.tailOmitted && <p role="status">Malformed tail omitted; history is incomplete.</p>}
      <ol className="history-records">{history.page.entries.map(entry => <li key={entry.offset}>
        <strong>{entry.eventType}</strong><span className="detail"> · {entry.timestamp} · byte {entry.offset}</span>
        {entry.text !== null && <pre>{entry.text}</pre>}
        {(entry.textTruncated || entry.bodyOmitted) && <p className="detail">Display preview shortened or payload omitted.</p>}
      </li>)}</ol>
      {history.page.next && <button type="button" onClick={() => readHistory(true)}>Next history page</button>}
    </>}
  </section>;
}
