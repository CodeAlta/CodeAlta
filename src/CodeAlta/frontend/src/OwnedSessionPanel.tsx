import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type Ref } from "react";
import { sessionOperations as sessions, type ConfigurationSnapshot, type SessionReceiptPage, type SessionReceiptView, type SessionChoicesResponse, type SessionSelection } from "#neoastra";
import { captureSubmission, captureSubmissionAbort, createMutationCapability, refreshSubmissions, type createOwnedSubmissions } from "./sessionOperations";
import { captureSteering, type createSteeringSubmissions } from "./sessionSteering";
import { captureCompaction, type createCompactionSubmissions } from "./sessionCompaction";
import { captureAbortRun, type createAbortRunSubmissions } from "./sessionAbortRun";
import { captureQueue, captureQueueCancellation, queueReceiptPhases, queueCancellationStatus, type createQueueSubmissions } from "./sessionQueue";
import type { createRuntimeStateReader, RuntimeState } from "./runtimeState";
import type { createPermissionReviewer } from "./sessionPermissions";
import { CommandPermissionPanel } from "./CommandPermissionPanel";
import { createDraftIndicators, persistDraft, restoreDraft } from "./promptDraft";
import { AppIcon } from "./AppIcon";
import { promptEditorHeight, showContextAction } from "./workspacePresentation";
import { changeSelection } from "./sessionSelection";
import type { createNextSendSelectionStore } from "./nextSendSelection";
import { dispatchComposerKey } from "./composerKeyboard";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";

export function OwnedSessionPanel({ sessionId, epoch, projectId = null, submissions, steering, compaction, abortRuns, queue, capability, runtimeReader, permissionReviewer, configuration, draftIndicators, selections, remindersTrigger, compactTrigger, onOpenReminders }: {
  sessionId: string; epoch: string; submissions: ReturnType<typeof createOwnedSubmissions>; capability: ReturnType<typeof createMutationCapability>;
  projectId?: string | null;
  runtimeReader: ReturnType<typeof createRuntimeStateReader>;
  permissionReviewer: ReturnType<typeof createPermissionReviewer> | null;
  steering: ReturnType<typeof createSteeringSubmissions>;
  compaction: ReturnType<typeof createCompactionSubmissions>;
  abortRuns: ReturnType<typeof createAbortRunSubmissions>;
  queue: ReturnType<typeof createQueueSubmissions>;
  draftIndicators: ReturnType<typeof createDraftIndicators>;
  selections: ReturnType<typeof createNextSendSelectionStore>;
  configuration?: ConfigurationSnapshot;
  remindersTrigger?: Ref<HTMLButtonElement>;
  compactTrigger?: Ref<HTMLButtonElement>;
  onOpenReminders?: () => void;
}) {
  const [draft, setDraft] = useState(() => ({ text: restoreDraft(key => localStorage.getItem(key), sessionId), editGeneration: null as number | null }));
  const text = draft.text;
  const latestText = useRef(text);
  const restoredText = useRef(text);
  useLayoutEffect(() => { draftIndicators.clear(sessionId); }, [draftIndicators, sessionId, epoch]);
  function editText(value: string) {
    latestText.current = value;
    const editGeneration = draftIndicators.edit(sessionId, value, restoredText.current);
    setDraft({ text: value, editGeneration });
  }
  function clearText() {
    latestText.current = "";
    restoredText.current = "";
    draftIndicators.clear(sessionId);
    setDraft({ text: "", editGeneration: null });
  }
  const promptInput = useRef<HTMLTextAreaElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [choices, setChoices] = useState<SessionChoicesResponse>();
  const [selection, setSelection] = useState<SessionSelection | null>(null);
  const [choicesNotice, setChoicesNotice] = useState("Loading session choices…");
  const [choicesRevision, setChoicesRevision] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setChoices(undefined);
    setSelection(null);
    setChoicesNotice("Loading session choices…");
    void sessions.choices({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 15000 })
      .then(value => {
        capability.observe(value);
        if (controller.signal.aborted || value.sessionId !== sessionId) return;
        if (value.status !== "ok" || value.epoch !== epoch || !value.current) {
          setChoicesNotice(`Session choices unavailable (${value.status}).`); return;
        }
        setChoices(value);
        setSelection(selections.get(epoch, sessionId, value));
        setChoicesNotice("Selections apply on Send; active runs and queued text are unchanged.");
      }).catch(() => { if (!controller.signal.aborted) setChoicesNotice("Session choices could not be loaded. Retry to refresh the provider catalog."); });
    return () => controller.abort();
  }, [epoch, sessionId, capability, selections, choicesRevision]);
  const [steerText, setSteerText] = useState("");
  const [steerMessage, setSteerMessage] = useState("Refresh runtime state explicitly before targeting a run.");
  const [compactMessage, setCompactMessage] = useState("Refresh runtime state explicitly before attempting idle compaction.");
  const [abortRunMessage, setAbortRunMessage] = useState("Refresh runtime state explicitly before targeting cancellation.");
  const [queueText, setQueueText] = useState("");
  const [queueMessage, setQueueMessage] = useState("Refresh runtime state explicitly before queueing text in this host.");
  const [message, setMessage] = useState("Ready to send to this owned session.");
  const [page, setPage] = useState<SessionReceiptPage>();
  const [runtimeState, setRuntimeState] = useState<RuntimeState>();
  const runtimeScope = useRef<ReturnType<typeof runtimeReader.forSelection> | null>(null);
  const [observedInvalidEpoch, setInvalidEpoch] = useState(!capability.canMutate());
  const canMutate = useSyncExternalStore(capability.subscribe, capability.canMutate);
  const invalidEpoch = observedInvalidEpoch || !canMutate;
  const scope = useRef<AbortController | null>(null);
  const receiptRevision = useRef(0);
  useEffect(() => {
    const controller = new AbortController();
    scope.current = controller;
    const restored = submissions.pending(sessionId)?.request.text ?? restoreDraft(key => localStorage.getItem(key), sessionId);
    restoredText.current = restored;
    latestText.current = restored;
    setDraft({ text: restored, editGeneration: null });
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
    setAbortRunMessage(abortRuns.pending(sessionId)
      ? "A retained exact cancellation request exists. Refresh submissions or explicitly retry its original key and run after the previous wait settles."
      : "Refresh runtime state explicitly before targeting cancellation.");
    setRuntimeState(undefined);
    runtimeScope.current = runtimeReader.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, setRuntimeState, capability.observe);
    void runtimeScope.current.refresh();
    setMessage(submissions.pending(sessionId) || submissions.aborts(sessionId).length
      ? "Retained Send/Abort intent exists. Refresh receipts manually or retry the exact request after its original waiter settles."
      : "Ready to send to this owned session.");
    return () => { controller.abort(); scope.current = null; runtimeScope.current = null; };
  }, [sessionId, epoch, submissions, steering, compaction, abortRuns, queue, runtimeReader, capability]);

  useEffect(() => {
    if (!submissions.pending(sessionId)) draftIndicators.persisted(sessionId, draft.editGeneration,
      persistDraft((key, value) => localStorage.setItem(key, value), key => localStorage.removeItem(key), sessionId, draft.text));
    else draftIndicators.clear(sessionId);
  }, [sessionId, draft, submissions, draftIndicators]);

  const pending = submissions.pending(sessionId);
  const pendingAborts = submissions.aborts(sessionId);
  const pendingSteer = steering.pending(sessionId);
  const pendingCompact = compaction.pending(sessionId);
  const pendingAbortRun = abortRuns.pending(sessionId);
  const pendingQueue = queue.pending(sessionId);
  const pendingQueueCancellations = queue.cancellations(sessionId);
  const observedTarget = runtimeState?.kind === "ready" ? runtimeState.snapshot : undefined;
  const runtimeConfiguration = observedTarget?.entry;
  const mcpPlugin = configuration?.plugins.find(plugin => `${plugin.id} ${plugin.name}`.toLowerCase().includes("mcp"));
  const canCaptureSteer = captureSteering(epoch, sessionId, observedTarget, steerText, "availability") !== null;
  const availableCompact = captureCompaction(epoch, sessionId, observedTarget, "availability");
  const availableAbortRun = captureAbortRun(epoch, sessionId, observedTarget, "availability");
  const canCaptureQueue = captureQueue(epoch, sessionId, observedTarget, queueText, "availability") !== null;
  const showSteering = showContextAction(captureSteering(epoch, sessionId, observedTarget, "x", "availability") !== null,
    !!pendingSteer || steerMessage !== "Refresh runtime state explicitly before targeting a run.");
  const showQueue = showContextAction(captureQueue(epoch, sessionId, observedTarget, "x", "availability") !== null,
    !!pendingQueue || pendingQueueCancellations.length > 0 || queueMessage !== "Refresh runtime state explicitly before queueing text in this host.");
  useLayoutEffect(() => {
    const input = promptInput.current;
    if (!input) return;
    const measure = () => {
      input.style.height = "auto";
      input.style.height = `${promptEditorHeight(input.scrollHeight, window.innerHeight)}px`;
    };
    measure();
    const workspace = input.closest<HTMLElement>(".session-workspace");
    let width = workspace?.clientWidth;
    const observer = new ResizeObserver(() => {
      if (workspace && workspace.clientWidth !== width) { width = workspace.clientWidth; measure(); }
    });
    if (workspace) observer.observe(workspace);
    window.addEventListener("resize", measure);
    return () => { observer.disconnect(); window.removeEventListener("resize", measure); };
  }, [text, pending?.request.text]);
  function observeEpoch(result: { status: string; epoch: string | null }) {
    if (!capability.observe(result)) setInvalidEpoch(true);
  }
  function submit() {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = submissions.pending(sessionId);
    if (retained?.inFlight) return;
    const request = retained?.request ?? captureSubmission(epoch, sessionId, text, crypto.randomUUID(), selection);
    if (!request || !capability.canSubmit(request)) return;
    draftIndicators.clear(sessionId);
    setMessage("Submission admission pending…");
    void submissions.submit(request, signal, capability, result => {
      observeEpoch(result);
      setMessage(result.status === "accepted" || result.status === "replay"
        ? "Submission accepted. Refresh submissions for dispatch outcome; this is not run completion."
        : `Submission: ${result.status}. Refresh receipts before considering an explicit retry.`);
      if ((result.status === "accepted" || result.status === "replay") && !signal.aborted) clearText();
    });
  }
  function refresh(offset = 0) {
    const signal = scope.current?.signal;
    if (!signal) return;
    const revision = ++receiptRevision.current;
    void refreshSubmissions(sessions.receipts, epoch, offset, signal, result => {
      if (revision !== receiptRevision.current) return;
      observeEpoch(result);
      setPage(result);
      const recovered = submissions.reconcile(sessionId, result, capability);
      if (recovered.sendRecovered && !signal.aborted) clearText();
      if (recovered.sendRecovered || recovered.abortsRecovered > 0)
        setMessage("Send/Abort receipt reconciled without prompt text. Admission/control settlement is not rollback or run completion.");
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
    }, capability);
  }
  function steer(fromComposer = false) {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = steering.pending(sessionId);
    if (fromComposer && retained) {
      setMessage("A steering request is retained. Review or retry that exact request in the steering controls first.");
      return;
    }
    if (retained?.inFlight) return;
    const request = retained?.request ?? captureSteering(epoch, sessionId, observedTarget, fromComposer ? text : steerText, crypto.randomUUID());
    if (!request || !capability.canSubmit(request)) {
      if (fromComposer) setMessage("Steering needs a non-empty prompt and a refreshed, eligible active run. Nothing was sent.");
      return;
    }
    if (fromComposer) draftIndicators.clear(sessionId);
    // The App-owned helper latches synchronously before its first await, across panel remounts.
    setSteerMessage("Steering admission pending…");
    if (fromComposer) setMessage("Steering admission pending…");
    void steering.submit(request, signal, capability, result => {
      observeEpoch(result);
      if (fromComposer) setMessage(`Steering: ${result.status}. Review steering controls for outcome or recovery.`);
      if (result.status === "accepted" || result.status === "replay") {
        setSteerText("");
        if (fromComposer && latestText.current === request.text && !signal.aborted) clearText();
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
        : result.status === "busy" ? "Compaction: busy. This attempt cannot be retried; refresh runtime state for a new explicit attempt."
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
  function abort(row?: SessionReceiptView, retainedOperationId?: string) {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = submissions.abortPending(retainedOperationId ?? row?.operationId ?? "");
    const intent = retained?.intent ?? (page && row ? captureSubmissionAbort(epoch, sessionId, page, row, crypto.randomUUID()) : null);
    if (!intent || retained?.inFlight || !capability.canSubmit(intent.request)) return;
    setMessage("Abort admission pending for the original Send operation only.");
    void submissions.abort(intent, signal, capability, result => {
      observeEpoch(result);
      setMessage(`Abort original Send: ${result.status}. Refresh receipts for control outcome, not rollback, decision retraction or run termination. Uncertainty retains the exact operation and key.`);
    });
  }
  const activeChoices = choices?.epoch === epoch && choices.sessionId === sessionId && choices.status === "ok" ? choices : undefined;
  const selected = pending?.request.selection ?? (activeChoices ? selection ?? activeChoices.current : null);
  const selectionDisabled = !activeChoices?.current || invalidEpoch || !!pending;
  function select(field: "agentPromptId" | "modelId" | "reasoningEffort", value: string) {
    if (!activeChoices || !selected || selectionDisabled) return;
    const next = changeSelection(activeChoices, selected, field, value);
    if (!next) { setChoicesNotice("Choose an available model and prompt before sending with changed settings."); return; }
    if (!selections.set(epoch, sessionId, activeChoices, next)) return;
    setSelection(next);
    setChoicesNotice("Selection saved for the next Send; active runs and queued text are unchanged.");
  }
  const efforts = activeChoices?.models.find(m => m.id === selected?.modelId)?.efforts ?? [];
  return <section className="owned-session" aria-label="Owned text submission">
    {expanded && !pending && !invalidEpoch && <ExpandedPromptEditor text={text} onChange={editText} onClose={() => setExpanded(false)} />}
    <label className="sr-only" htmlFor="session-prompt">Message</label>
    <textarea id="session-prompt" ref={promptInput} className="prompt-input" rows={1} maxLength={32768} value={pending?.request.text ?? text} disabled={!!pending}
      onChange={event => editText(event.target.value)} placeholder="Ask CodeAlta to work on this project…" onKeyDown={event => {
        if (dispatchComposerKey({ key: event.key, ctrlKey: event.ctrlKey,
          shiftKey: event.shiftKey, altKey: event.altKey, metaKey: event.metaKey,
          isComposing: event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode,
          repeat: event.repeat, defaultPrevented: event.defaultPrevented }, submit, () => steer(true))) event.preventDefault();
      }} />
    <div className="composer-toolbar">
    <div className="prompt-options" aria-label="Session configuration">
      <label><span>Agent prompt</span><select aria-label="Agent prompt" value={selected?.agentPromptId ?? ""} disabled={selectionDisabled} onChange={event => select("agentPromptId", event.target.value)} title="Agent prompt for the next Send">
        {!activeChoices?.prompts.some(p => p.id === selected?.agentPromptId) && <option value={selected?.agentPromptId ?? ""}>{selected?.agentPromptId ?? "Loading…"}</option>}
        {activeChoices?.prompts.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
      </select></label>
      <label><span>Model</span><select aria-label="Model" value={selected?.modelId ?? ""} disabled={selectionDisabled} onChange={event => select("modelId", event.target.value)} title={`Model for the next Send · ${selected?.providerKey ?? "session provider"}`}>
        <option value="">Provider default</option>
        {selected?.modelId && !activeChoices?.models.some(m => m.id === selected.modelId) && <option value={selected.modelId}>{selected.modelId} (not in catalog)</option>}
        {activeChoices?.models.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
      </select></label>
      <label><span>Reasoning</span><select aria-label="Reasoning" value={selected?.reasoningEffort ?? ""} disabled={selectionDisabled || efforts.length === 0} onChange={event => select("reasoningEffort", event.target.value)} title="Supported reasoning effort for the selected model">
        <option value="">Model default</option>
        {selected?.reasoningEffort && !efforts.includes(selected.reasoningEffort) && <option value={selected.reasoningEffort}>{selected.reasoningEffort} (not in catalog)</option>}
        {efforts.map(e => <option key={e} value={e}>{e}</option>)}
      </select></label>
    </div>
    <div className="history-controls">
      <span className="sr-only">Enter to send · Shift+Enter for a new line · Ctrl+Enter to steer</span>
      {onOpenReminders && <button ref={remindersTrigger} type="button" className="composer-icon-button" disabled={invalidEpoch}
        aria-label="Reminders for selected session" title="Reminders for selected session (Ctrl+G, Ctrl+D)"
        onClick={onOpenReminders}><AppIcon name="reminder" size={16} /></button>}
      <button id="expand-session-prompt" type="button" className="composer-icon-button" disabled={!!pending || invalidEpoch} aria-label="Expand prompt editor" title="Edit prompt in a large window (F6)" onClick={() => setExpanded(true)}><AppIcon name="expand" size={16} /></button>
      {(availableCompact || pendingCompact) && <button ref={compactTrigger} type="button" className="composer-icon-button" onClick={compact}
        data-epoch={epoch} data-session-id={sessionId} data-project-id={projectId ?? ""}
        disabled={invalidEpoch || !!pendingCompact?.inFlight || (pendingCompact
          ? !capability.canSubmit(pendingCompact.request) : !availableCompact || !capability.canSubmit(availableCompact))}
        aria-label={pendingCompact ? `Retry exact compaction request for attachment ${pendingCompact.request.expectedAttachmentGeneration}` : "Compact observed idle attachment"}
        aria-describedby="observed-compaction-help"
        title={pendingCompact ? `Manual retry of exact compaction: epoch ${pendingCompact.request.expectedEpoch}, session ${pendingCompact.request.sessionId}, runtime ${pendingCompact.request.expectedRuntimeInstanceId}, attachment ${pendingCompact.request.expectedAttachmentGeneration}, request ${pendingCompact.request.clientRequestId}`
          : `Compact observed idle attachment (Ctrl+F11; point-in-time idle observation permits only an attempt; provider must prove idle)`}>
        <AppIcon name="compact" size={16} /></button>}
      {(availableAbortRun || pendingAbortRun) && <button type="button" className="composer-icon-button" onClick={abortRun}
        disabled={invalidEpoch || !!pendingAbortRun?.inFlight || (pendingAbortRun
          ? !capability.canSubmit(pendingAbortRun.request) : !availableAbortRun || !capability.canSubmit(availableAbortRun))}
        aria-label={pendingAbortRun ? `Retry exact cancellation request for observed run ${pendingAbortRun.request.expectedRunId}` : "Cancel observed run"}
        aria-describedby="observed-run-cancellation-help"
        title={pendingAbortRun ? `Manual retry of exact cancellation: epoch ${pendingAbortRun.request.expectedEpoch}, session ${pendingAbortRun.request.sessionId}, runtime ${pendingAbortRun.request.expectedRuntimeInstanceId}, attachment ${pendingAbortRun.request.expectedAttachmentGeneration}, run ${pendingAbortRun.request.expectedRunId}, request ${pendingAbortRun.request.clientRequestId}`
          : `Cancel observed run ${availableAbortRun?.expectedRunId} (point-in-time runtime observation, not original Send Abort; signalling does not confirm completion)`}>
        <AppIcon name="stop" size={16} /></button>}
      <button type="button" className="primary-button send-button" disabled={invalidEpoch || !!pending?.inFlight || (pending ? !capability.canSubmit(pending.request) : captureSubmission(epoch, sessionId, text, "availability") === null)} onClick={submit}>{pending ? "Retry exact request" : <><span>Send</span><AppIcon name="send" size={14} /></>}</button>
    </div>
    </div>
    <span id="observed-run-cancellation-help" className="sr-only">Targets a point-in-time observed run, not the original Send receipt. Cancellation signalled does not confirm run completion. Retained requests are only retried manually against their original target after the previous wait settles.</span>
    <span id="observed-compaction-help" className="sr-only">Point-in-time idle observation permits only an attempt; the provider must prove idle. Busy is a permanent outcome, not an automatic retry. Retained requests are retried manually against their original attachment after the previous wait settles.</span>
    {choicesNotice !== "Selections apply on Send; active runs and queued text are unchanged." && <p className="composer-notice" role={choicesNotice.includes("could not") || choicesNotice.includes("unavailable") ? "alert" : "status"}>{choicesNotice}
      {(choicesNotice.includes("could not") || choicesNotice.includes("unavailable")) && <button type="button" disabled={!!pending || invalidEpoch} onClick={() => setChoicesRevision(value => value + 1)}>Retry choices</button>}</p>}
    {(message !== "Ready to send to this owned session." || pending || pendingAborts.length > 0) && <p className="composer-notice" role="status">{message}</p>}
    {(pending || pendingAborts.length > 0) && <button type="button" onClick={() => refresh()}>Refresh receipts</button>}
    {invalidEpoch && <p role="alert">Host/runtime identity changed. Reload required; mutations are disabled. The exact uncertain request is retained and will not be rebased or resent.</p>}
    {runtimeState?.kind === "error" && <p role="alert">Runtime observation unavailable ({runtimeState.code}). {['stale_epoch', 'stale_runtime'].includes(runtimeState.code) ? "Reload required." : "No idle or completion state is inferred."}</p>}
    {mcpPlugin && /fail|error/i.test(mcpPlugin.state) && <p role="alert">MCP plugin: {mcpPlugin.state}. Check advanced diagnostics.</p>}
    {page && page.status !== "ok" && <p role="alert">Receipt snapshot: {page.status}</p>}
    {(compactMessage !== "Refresh runtime state explicitly before attempting idle compaction." || pendingAbortRun) && <p className="composer-notice" role="status">{compactMessage !== "Refresh runtime state explicitly before attempting idle compaction." && compactMessage} {pendingAbortRun && abortRunMessage}</p>}
    {pendingCompact && <p className="composer-notice">Manual exact compaction retry only: epoch {pendingCompact.request.expectedEpoch} · session {pendingCompact.request.sessionId} · runtime {pendingCompact.request.expectedRuntimeInstanceId} · attachment {pendingCompact.request.expectedAttachmentGeneration} · request {pendingCompact.request.clientRequestId}. Refresh never retargets this intent.</p>}
    {pendingAbortRun && <p className="composer-notice">Manual exact cancellation retry only: epoch {pendingAbortRun.request.expectedEpoch} · session {pendingAbortRun.request.sessionId} · runtime {pendingAbortRun.request.expectedRuntimeInstanceId} · attachment {pendingAbortRun.request.expectedAttachmentGeneration} · run {pendingAbortRun.request.expectedRunId} · request {pendingAbortRun.request.clientRequestId}. Refresh never retargets this intent.</p>}
    {(showSteering || showQueue) && <div className="context-actions">
      {showSteering && <div><label>Steer observed run {pendingSteer?.request.expectedRunId ?? observedTarget?.entry?.activeRunId}<textarea maxLength={32768} value={pendingSteer?.request.text ?? steerText} disabled={!!pendingSteer} onChange={event => setSteerText(event.target.value)} /></label>
        {pendingSteer && <p className="detail">Retained run {pendingSteer.request.expectedRunId} · attachment {pendingSteer.request.expectedAttachmentGeneration} · request {pendingSteer.request.clientRequestId}; refresh never retargets this request.</p>}
        <button type="button" disabled={invalidEpoch || !!pendingSteer?.inFlight || (pendingSteer ? !capability.canSubmit(pendingSteer.request) : !canCaptureSteer)} onClick={() => steer()}>{pendingSteer ? "Retry exact steering request" : "Steer observed run"}</button>
        {steerMessage !== "Refresh runtime state explicitly before targeting a run." && <p role="status">{steerMessage}</p>}</div>}
      {showQueue && <div><label>Host-only queued text<textarea maxLength={32768} value={pendingQueue?.request.text ?? queueText} disabled={!!pendingQueue} onChange={event => setQueueText(event.target.value)} /></label>
        <p className="detail">Reservation is not insertion, execution or durable storage. Refresh receipts manually.</p>
        {pendingQueue && <p className="detail">Retained attachment {pendingQueue.request.expectedAttachmentGeneration} · request {pendingQueue.request.clientRequestId}; no durable recovery or retargeting.</p>}
        <button type="button" disabled={invalidEpoch || !!pendingQueue?.inFlight || (pendingQueue ? !capability.canSubmit(pendingQueue.request) : !canCaptureQueue)} onClick={queueTextInHost}>{pendingQueue ? "Retry exact host-only queue request" : "Queue text — this host only"}</button>
        {pendingQueueCancellations.map(value => <div key={value.intent.request.targetOperationId}>
          <p className="detail">Retained cancellation · original operation {value.intent.request.targetOperationId} · request {value.intent.request.clientRequestId}</p>
          <button type="button" disabled={invalidEpoch || value.inFlight || !capability.canSubmit(value.intent.request)} onClick={() => cancelQueued(undefined, value.intent.request.targetOperationId)}>Retry exact queued-operation cancellation</button>
        </div>)}
        {queueMessage !== "Refresh runtime state explicitly before queueing text in this host." && <p role="status">{queueMessage}</p>}</div>}
    </div>}
    {permissionReviewer && <CommandPermissionPanel reviewer={permissionReviewer} epoch={epoch} sessionId={sessionId} />}
    <details className="advanced-session-controls"><summary>Advanced session controls and diagnostics</summary><div>
    <p className="detail">Selections apply on Send; active runs and queued text are unchanged.</p>
    <button id="refresh-session-context" type="button" onClick={() => void runtimeScope.current?.refresh()} aria-label="Refresh context and runtime configuration" title={`Refresh context · ${runtimeConfiguration?.providerKey ?? "session provider"}`}><AppIcon name="refresh" size={14} /> Refresh context</button>
    <button type="button" disabled={!!pending || invalidEpoch} onClick={() => setChoicesRevision(value => value + 1)}><AppIcon name="refresh" size={14} /> Refresh choices</button>
    <p className="detail">MCP: {mcpPlugin?.state ?? (configuration?.pluginRuntimeAvailable ? "Off" : "Unavailable")} · {runtimeState?.kind === "loading" ? "Reading context…" : runtimeConfiguration?.activeRunId ? "Run active" : runtimeConfiguration ? "Context ready" : "Context unavailable"}.</p>
    <p className="detail">Existing session only. {permissionReviewer ? "Supported plain commands require explicit review below; other permissions are denied." : "Permissions are denied by default. Relaunch with --review-owned-command-permissions in owned mode to opt in to supported plain command review."} User input is cancelled; plugins and host-contributed tools are disabled. A submitted receipt is not a completed run. Receipt capacity is 256 for this host lifetime.</p>
    <p className="detail">Send/Abort retains at most 256 local intents combined. Selection changes retain exact requests and live waiter exclusion. After document reload, browse host receipts manually; lost text and retry keys are not reconstructed. No automatic retry.</p>
    <button type="button" onClick={() => refresh()}>Refresh submissions</button>
    {(Array.isArray(page?.rows) ? page.rows : []).filter(row => row && typeof row.sessionId === "string" && row.sessionId.toLowerCase() === sessionId.toLowerCase()).map(row => <div key={row.operationId}>
      {row.kind === "Queue" ? <><p>Queue · {row.operationId}</p>
        {queueReceiptPhases(row)?.map((phase, index) => <p key={index}>{index + 1}. {phase}</p>) ?? <p>Malformed queue receipt; not actionable.</p>}</>
        : row.kind === "CancelQueue" ? <p>CancelQueue · {queueCancellationStatus(row) ?? "Malformed cancellation receipt; not actionable."} · target {row.targetOperationId}</p>
        : <p>{row.kind} · {row.outcome === "Completed" ? (row.kind === "Abort" ? "Original Send control settled; not rollback, decision retraction or run termination" : row.kind === "AbortRun" ? "Cancellation signalled; run completion is not confirmed" : row.kind === "Compact" ? "compaction settled successfully" : row.kind === "Steer" ? "steering input submitted" : "submission submitted") : row.outcome === "Failed" ? "operation failed" : row.outcome === "Cancelled" ? "operation cancelled" : "operation pending"} {row.code ?? ""} · {row.operationId}</p>}
      {row.kind === "Queue" && <button type="button" disabled={invalidEpoch || !!queue.cancelPending(row.operationId)
        || (!!pendingQueue?.inFlight && pendingQueue.request.clientRequestId === row.clientRequestId)
        || !page || captureQueueCancellation(epoch, sessionId, page, row, "availability") === null} onClick={() => cancelQueued(row)}>Cancel this queued operation</button>}
      {row.kind === "Send" && row.state === "pending" && <button type="button" disabled={invalidEpoch || !!submissions.abortPending(row.operationId)
        || (!!pending?.inFlight && pending.request.clientRequestId === row.clientRequestId)
        || !page || captureSubmissionAbort(epoch, sessionId, page, row, "availability") === null} onClick={() => abort(row)}>Abort original Send operation</button>}
    </div>)}
    {page?.next != null && <button type="button" onClick={() => refresh(page.next!)}>Next receipt page</button>}
    {pendingAborts.map(value => <div key={value.intent.request.targetOperationId}>
      <p className="detail">Retained Abort: original session {value.intent.sessionId} · operation {value.intent.request.targetOperationId} · request {value.intent.request.clientRequestId}</p>
      <button type="button" disabled={invalidEpoch || value.inFlight || !capability.canSubmit(value.intent.request)}
        onClick={() => abort(undefined, value.intent.request.targetOperationId)}>Retry exact original Send Abort</button>
    </div>)}
    <h3>Current runtime — manual point-in-time observation</h3>
    <p className="detail">Recorded facts at the last refresh, not provider inactivity or successful run completion. Queue depth is unknown. This does not acknowledge effects or synchronize Display, receipts or persisted history.</p>
    <button type="button" disabled={runtimeState?.kind === "error" && ["stale_epoch", "stale_runtime"].includes(runtimeState.code)} onClick={() => void runtimeScope.current?.refresh()}>Refresh runtime state</button>
    {runtimeState?.kind === "loading" && <p role="status">Reading current runtime facts…</p>}
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
    <h3>Signal cancellation for observed run</h3>
    <p className="detail">Targets only the explicitly observed runtime, attachment and run. Unsupported, stale, retiring, transitioning or draining targets fail closed without fallback. Signalling is not run completion or rollback; previously accepted decisions remain accepted. Failure can occur after signalling. Refreshes never retarget a retained request.</p>
    {pendingAbortRun && <p className="detail">Retained target: runtime {pendingAbortRun.request.expectedRuntimeInstanceId} · attachment {pendingAbortRun.request.expectedAttachmentGeneration} · run {pendingAbortRun.request.expectedRunId} · request {pendingAbortRun.request.clientRequestId}</p>}
    <p role="status">{abortRunMessage}</p>
    <h3>Compact the observed attachment if idle now</h3>
    <p className="detail">Recorded idleness only permits an attempt: the provider must prove idle without waiting. Compacts context current at provider admission, not the history from your observation. Stale, retiring, non-owned or unsupported targets are rejected without fallback. No new permission authority is created. A busy receipt is permanent; a new explicit action uses a fresh key. Refreshes never retarget an uncertain request.</p>
    {pendingCompact && <p className="detail">Retained target: runtime {pendingCompact.request.expectedRuntimeInstanceId} · attachment {pendingCompact.request.expectedAttachmentGeneration} · request {pendingCompact.request.clientRequestId}</p>}
    <p role="status">{compactMessage}</p>
    </div></details>
  </section>;
}
