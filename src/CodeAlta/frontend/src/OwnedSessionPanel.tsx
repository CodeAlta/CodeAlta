import { useEffect, useRef, useState } from "react";
import { sessionOperations as sessions, workspace, type SessionReceiptPage, type SessionReceiptView, type SessionSendRequest } from "#neoastra";
import { captureSubmission, createMutationCapability, refreshSubmissions, sendSubmission, hasSubmissionReceipt } from "./sessionOperations";
import { captureSteering, type createSteeringSubmissions } from "./sessionSteering";
import { captureCompaction, type createCompactionSubmissions } from "./sessionCompaction";
import { captureAbortRun, type createAbortRunSubmissions } from "./sessionAbortRun";
import { captureQueue, captureQueueCancellation, queueReceiptPhases, queueCancellationStatus, type createQueueSubmissions } from "./sessionQueue";
import { historyMessage, loadHistory, type HistoryState } from "./history";
import { LiveSessionPanel } from "./LiveSessionPanel";
import type { createSessionDisplayStore } from "./sessionDisplay";
import type { createRuntimeStateReader, RuntimeState } from "./runtimeState";
import type { createPermissionReviewer } from "./sessionPermissions";
import { CommandPermissionPanel } from "./CommandPermissionPanel";

export function OwnedSessionPanel({ sessionId, epoch, drafts, steering, compaction, abortRuns, queue, capability, display, runtimeReader, permissionReviewer }: {
  sessionId: string; epoch: string; drafts: Map<string, SessionSendRequest>; capability: ReturnType<typeof createMutationCapability>;
  display: ReturnType<typeof createSessionDisplayStore>;
  runtimeReader: ReturnType<typeof createRuntimeStateReader>;
  permissionReviewer: ReturnType<typeof createPermissionReviewer> | null;
  steering: ReturnType<typeof createSteeringSubmissions>;
  compaction: ReturnType<typeof createCompactionSubmissions>;
  abortRuns: ReturnType<typeof createAbortRunSubmissions>;
  queue: ReturnType<typeof createQueueSubmissions>;
}) {
  const [text, setText] = useState("");
  const [steerText, setSteerText] = useState("");
  const [steerMessage, setSteerMessage] = useState("Refresh runtime state explicitly before targeting a run.");
  const [compactMessage, setCompactMessage] = useState("Refresh runtime state explicitly before attempting idle compaction.");
  const [abortRunMessage, setAbortRunMessage] = useState("Refresh runtime state explicitly before targeting cancellation.");
  const [queueText, setQueueText] = useState("");
  const [queueMessage, setQueueMessage] = useState("Refresh runtime state explicitly before queueing text in this host.");
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
    setSteerText(steering.pending(sessionId)?.request.text ?? "");
    setQueueText(queue.pending(sessionId)?.request.text ?? "");
    setQueueMessage(queue.pending(sessionId) || queue.cancellations(sessionId).length
      ? "Retained queue/cancellation intent exists. Manually refresh receipts or explicitly retry the exact request after its original waiter settles."
      : "Refresh runtime state explicitly before queueing text in this host.");
    setSteerMessage(steering.pending(sessionId)
      ? "A retained steering request exists. Refresh submissions to reconcile it, or explicitly retry its exact key and target once the previous wait settles."
      : "Refresh runtime state explicitly before targeting a run.");
    setPage(undefined);
    setCompactMessage(compaction.pending(sessionId)
      ? "A retained compaction request exists. Refresh submissions to reconcile it, or explicitly retry its exact key and attachment once the previous wait settles."
      : "Refresh runtime state explicitly before attempting idle compaction.");
    setHistory(undefined);
    setAbortRunMessage(abortRuns.pending(sessionId)
      ? "A retained exact cancellation request exists. Refresh submissions or explicitly retry its original key and run after the previous wait settles."
      : "Refresh runtime state explicitly before targeting cancellation.");
    setRuntimeState(undefined);
    runtimeScope.current = runtimeReader.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, value => {
      if (value.kind === "error" && ["stale_epoch", "stale_runtime"].includes(value.code)) observeEpoch({ status: value.code, epoch: null });
      setRuntimeState(value);
    });
    setBusy(false);
    setMessage("Refresh submissions to recover accepted receipts. Uncertain requests are never resent automatically.");
    return () => { controller.abort(); scope.current = null; runtimeScope.current = null; };
  }, [sessionId, epoch, drafts, steering, compaction, abortRuns, queue, runtimeReader, capability]);

  const pending = drafts.get(sessionId);
  const pendingSteer = steering.pending(sessionId);
  const pendingCompact = compaction.pending(sessionId);
  const pendingAbortRun = abortRuns.pending(sessionId);
  const pendingQueue = queue.pending(sessionId);
  const pendingQueueCancellations = queue.cancellations(sessionId);
  const observedTarget = runtimeState?.kind === "ready" ? runtimeState.snapshot : undefined;
  const canCaptureSteer = captureSteering(epoch, sessionId, observedTarget, steerText, "availability") !== null;
  const canCaptureCompact = captureCompaction(epoch, sessionId, observedTarget, "availability") !== null;
  const canCaptureAbortRun = captureAbortRun(epoch, sessionId, observedTarget, "availability") !== null;
  const canCaptureQueue = captureQueue(epoch, sessionId, observedTarget, queueText, "availability") !== null;
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
    void refreshSubmissions(async (request, options) => {
      const result = await sessions.receipts(request, options);
      capability.observe(result); // Even a late obsolete selection must invalidate shared mutation authority.
      return result;
    }, epoch, offset, signal, result => {
      if (revision !== receiptRevision.current) return;
      observeEpoch(result);
      setPage(result);
      const uncertain = drafts.get(sessionId);
      if (uncertain && capability.canSubmit(uncertain) && hasSubmissionReceipt(uncertain, result)) {
        drafts.delete(sessionId);
        setText("");
        setMessage("Accepted receipt recovered without exposing prompt text.");
      }
      if (steering.reconcile(sessionId, result, capability)) {
        setSteerText("");
        setSteerMessage("Steering receipt recovered. Input submission is not run completion.");
      }
      if (compaction.reconcile(sessionId, result, capability))
        setCompactMessage("Compaction receipt recovered. Review its settled outcome; a busy outcome requires a new explicit action.");
      if (abortRuns.reconcile(sessionId, result, capability))
        setAbortRunMessage("Exact cancellation receipt recovered. Review its outcome; cancellation signalling is not run completion.");
      const recoveredQueue = queue.reconcile(sessionId, result, capability);
      if (recoveredQueue.queueRecovered) setQueueText("");
      if (recoveredQueue.queueRecovered || recoveredQueue.cancellationsRecovered > 0) {
        setQueueMessage("Queue/cancellation receipt reconciled. Review reservation, host-only insertion and execution/cleanup separately.");
      }
    });
  }
  function steer() {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = steering.pending(sessionId);
    if (retained?.inFlight) return;
    const request = retained?.request ?? captureSteering(epoch, sessionId, observedTarget, steerText, crypto.randomUUID());
    if (!request || !capability.canSubmit(request)) return;
    // The App-owned helper latches synchronously before its first await, across panel remounts.
    setSteerMessage("Steering admission pending…");
    void steering.submit(request, signal, capability, result => {
      observeEpoch(result);
      if (result.status === "accepted" || result.status === "replay") {
        setSteerText("");
        setSteerMessage("Steering accepted. Refresh submissions for input dispatch outcome, not run completion.");
      } else setSteerMessage(`Steering: ${result.status}. Refresh submissions; uncertain requests retain their original text and target and are never retried automatically.`);
    });
  }
  function compact() {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = compaction.pending(sessionId);
    if (retained?.inFlight) return;
    const request = retained?.request ?? captureCompaction(epoch, sessionId, observedTarget, crypto.randomUUID());
    if (!request || !capability.canSubmit(request)) return;
    setCompactMessage("Idle compaction admission pending…");
    void compaction.submit(request, signal, capability, result => {
      observeEpoch(result);
      setCompactMessage(result.status === "accepted" || result.status === "replay"
        ? "Compaction attempt accepted. Refresh submissions for its settled outcome; busy requires a new explicit action, not replay."
        : `Compaction: ${result.status}. Refresh submissions; uncertain requests retain their exact attachment and key. No automatic retry.`);
    });
  }
  function abortRun() {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = abortRuns.pending(sessionId);
    if (retained?.inFlight) return;
    const request = retained?.request ?? captureAbortRun(epoch, sessionId, observedTarget, crypto.randomUUID());
    if (!request || !capability.canSubmit(request)) return;
    setAbortRunMessage("Exact cancellation admission pending…");
    void abortRuns.submit(request, signal, capability, result => {
      observeEpoch(result);
      setAbortRunMessage(result.status === "accepted" || result.status === "replay"
        ? "Cancellation request accepted. Refresh submissions for signalling outcome; run completion is not confirmed."
        : `Exact cancellation: ${result.status}. Uncertain requests retain their original run and key. No automatic retry.`);
    });
  }
  function queueTextInHost() {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = queue.pending(sessionId);
    const request = retained?.request ?? captureQueue(epoch, sessionId, observedTarget, queueText, crypto.randomUUID());
    if (!request || retained?.inFlight || !capability.canSubmit(request)) return;
    setQueueMessage("Owner reservation pending; host-only insertion and execution are not yet confirmed.");
    void queue.submit(request, signal, capability, result => {
      observeEpoch(result);
      if (result.status === "accepted" || result.status === "replay") {
        setQueueText("");
        setQueueMessage("Owner reservation accepted. Refresh submissions manually for host-only insertion and execution/cleanup; neither durability nor run completion is implied.");
      } else setQueueMessage(`Queue: ${result.status}. Uncertain requests retain exact text, key and attachment. No automatic retry.`);
    });
  }
  function cancelQueued(row?: SessionReceiptView, retainedOperationId?: string) {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = queue.cancelPending(retainedOperationId ?? row?.operationId ?? "");
    const intent = retained?.intent ?? (page && row ? captureQueueCancellation(epoch, sessionId, page, row, crypto.randomUUID()) : null);
    if (!intent || retained?.inFlight || !capability.canSubmit(intent.request)) return;
    setQueueMessage("Cancellation owner reservation pending for the original queued operation only.");
    void queue.cancel(intent, signal, capability, result => {
      observeEpoch(result);
      setQueueMessage(result.status === "accepted" || result.status === "replay"
        ? "Cancellation reservation accepted. Refresh receipts manually. Signalling is not rollback, cleanup completion or run completion; previously accepted decisions remain accepted."
        : `Queued-operation cancellation: ${result.status}. Uncertainty retains the original operation, session and key. No automatic retry.`);
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
    <p className="detail">Existing session only. {permissionReviewer ? "Supported plain commands require explicit review below; other permissions are denied." : "Permissions are denied by default. Relaunch with --review-owned-command-permissions in owned mode to opt in to supported plain command review."} User input is cancelled; plugins and host-contributed tools are disabled. A bounded live status/text window is available below. A submitted receipt is not a completed run. Receipt capacity is 256 for this host lifetime.</p>
    <label>Text<textarea maxLength={32768} value={text} disabled={busy || !!pending} onChange={event => setText(event.target.value)} /></label>
    <div className="history-controls">
      <button type="button" disabled={invalidEpoch || busy || (!pending && !text.trim()) || (!!pending && pending.expectedEpoch !== epoch)} onClick={submit}>{pending ? "Retry exact request" : "Send text"}</button>
      <button type="button" onClick={() => refresh()}>Refresh submissions</button>
    </div>
    <p role="status">{message}</p>
    {invalidEpoch && <p role="alert">Host/runtime identity changed. Reload required; mutations are disabled. The exact uncertain request is retained and will not be rebased or resent.</p>}
    {page && page.status !== "ok" && <p role="alert">Receipt snapshot: {page.status}</p>}
    {(Array.isArray(page?.rows) ? page.rows : []).filter(row => row && typeof row.sessionId === "string" && row.sessionId.toLowerCase() === sessionId.toLowerCase()).map(row => <div key={row.operationId}>
      {row.kind === "Queue" ? <><p>Queue · {row.operationId}</p>
        {queueReceiptPhases(row)?.map((phase, index) => <p key={index}>{index + 1}. {phase}</p>) ?? <p>Malformed queue receipt; not actionable.</p>}</>
        : row.kind === "CancelQueue" ? <p>CancelQueue · {queueCancellationStatus(row) ?? "Malformed cancellation receipt; not actionable."} · target {row.targetOperationId}</p>
        : <p>{row.kind} · {row.outcome === "Completed" ? (row.kind === "AbortRun" ? "Cancellation signalled; run completion is not confirmed" : row.kind === "Compact" ? "compaction settled successfully" : row.kind === "Steer" ? "steering input submitted" : "submission submitted") : row.outcome === "Failed" ? "operation failed" : row.outcome === "Cancelled" ? "operation cancelled" : "operation pending"} {row.code ?? ""} · {row.operationId}</p>}
      {row.kind === "Queue" && <button type="button" disabled={invalidEpoch || !!queue.cancelPending(row.operationId)
        || (!!pendingQueue?.inFlight && pendingQueue.request.clientRequestId === row.clientRequestId)
        || !page || captureQueueCancellation(epoch, sessionId, page, row, "availability") === null} onClick={() => cancelQueued(row)}>Cancel this queued operation</button>}
      {row.kind === "Send" && row.state === "pending" && <button type="button" disabled={invalidEpoch} onClick={() => abort(row.operationId)}>Abort submission</button>}
    </div>)}
    {page?.next != null && <button type="button" onClick={() => refresh(page.next!)}>Next receipt page</button>}
    {permissionReviewer && <CommandPermissionPanel reviewer={permissionReviewer} epoch={epoch} sessionId={sessionId} />}
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
    <h3>Queue text — this host only</h3>
    <p className="detail">Volatile, bounded, exact-attachment text. Busy/draining observations permit an attempt; the backend checks the exact attachment and owned-default policy. No retargeting or durable/restart recovery. Owner reservation, insertion retained IN THIS HOST ONLY, and execution/cleanup are separate phases. queue_accepted is not executed; queue_dispatched is not run completed. After document reload, browse receipts manually: lost local text and retry keys are not reconstructed.</p>
    {pendingQueue && <p className="detail">Retained target: runtime {pendingQueue.request.expectedRuntimeInstanceId} · attachment {pendingQueue.request.expectedAttachmentGeneration} · request {pendingQueue.request.clientRequestId}</p>}
    <label>Host-only queued text<textarea maxLength={32768} value={pendingQueue?.request.text ?? queueText} disabled={!!pendingQueue}
      onChange={event => setQueueText(event.target.value)} /></label>
    <button type="button" disabled={invalidEpoch || !!pendingQueue?.inFlight || (pendingQueue ? !capability.canSubmit(pendingQueue.request) : !canCaptureQueue)} onClick={queueTextInHost}>
      {pendingQueue ? "Retry exact host-only queue request" : "Queue text — this host only"}
    </button>
    {pendingQueueCancellations.map(value => <div key={value.intent.request.targetOperationId}>
      <p className="detail">Retained cancellation: original session {value.intent.sessionId} · operation {value.intent.request.targetOperationId} · request {value.intent.request.clientRequestId}</p>
      <button type="button" disabled={invalidEpoch || value.inFlight || !capability.canSubmit(value.intent.request)}
        onClick={() => cancelQueued(undefined, value.intent.request.targetOperationId)}>Retry exact queued-operation cancellation</button>
    </div>)}
    <p role="status">{queueMessage}</p>
    <h3>Steer the explicitly observed run</h3>
    <p className="detail">Exact-target text only; the host rejects stale, retiring or non-owned targets. No run ID recorded means steering is unavailable. Steering does not create permission authority or reopen a closed review window. Refreshes never change a retained request's target.</p>
    {pendingSteer && <p className="detail">Retained target: runtime {pendingSteer.request.expectedRuntimeInstanceId} · attachment {pendingSteer.request.expectedAttachmentGeneration} · run {pendingSteer.request.expectedRunId} · request {pendingSteer.request.clientRequestId}</p>}
    <label>Steering text<textarea maxLength={32768} value={pendingSteer?.request.text ?? steerText} disabled={!!pendingSteer}
      onChange={event => setSteerText(event.target.value)} /></label>
    <button type="button" disabled={invalidEpoch || !!pendingSteer?.inFlight || (pendingSteer ? !capability.canSubmit(pendingSteer.request) : !canCaptureSteer)} onClick={steer}>
      {pendingSteer ? "Retry exact steering request" : "Steer observed run"}
    </button>
    <p role="status">{steerMessage}</p>
    <h3>Signal cancellation for observed run</h3>
    <p className="detail">Targets only the explicitly observed runtime, attachment and run. Unsupported, stale, retiring, transitioning or draining targets fail closed without fallback. Signalling is not run completion or rollback; previously accepted decisions remain accepted. Failure can occur after signalling. Refreshes never retarget a retained request.</p>
    {pendingAbortRun && <p className="detail">Retained target: runtime {pendingAbortRun.request.expectedRuntimeInstanceId} · attachment {pendingAbortRun.request.expectedAttachmentGeneration} · run {pendingAbortRun.request.expectedRunId} · request {pendingAbortRun.request.clientRequestId}</p>}
    <button type="button" disabled={invalidEpoch || !!pendingAbortRun?.inFlight || (pendingAbortRun ? !capability.canSubmit(pendingAbortRun.request) : !canCaptureAbortRun)} onClick={abortRun}>
      {pendingAbortRun ? "Retry exact cancellation request" : "Signal cancellation for observed run"}
    </button>
    <p role="status">{abortRunMessage}</p>
    <h3>Compact the observed attachment if idle now</h3>
    <p className="detail">Recorded idleness only permits an attempt: the provider must prove idle without waiting. Compacts context current at provider admission, not the history from your observation. Stale, retiring, non-owned or unsupported targets are rejected without fallback. No new permission authority is created. A busy receipt is permanent; a new explicit action uses a fresh key. Refreshes never retarget an uncertain request.</p>
    {pendingCompact && <p className="detail">Retained target: runtime {pendingCompact.request.expectedRuntimeInstanceId} · attachment {pendingCompact.request.expectedAttachmentGeneration} · request {pendingCompact.request.clientRequestId}</p>}
    <button type="button" disabled={invalidEpoch || !!pendingCompact?.inFlight || (pendingCompact ? !capability.canSubmit(pendingCompact.request) : !canCaptureCompact)} onClick={compact}>
      {pendingCompact ? "Retry exact compaction request" : "Compact observed attachment if idle"}
    </button>
    <p role="status">{compactMessage}</p>
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
