import { useCallback, useContext, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type ReactNode, type Ref } from "react";
import { ProjectReferenceContext, ProjectReferencePicker } from "./ProjectReferencePicker";
import { sessionOperations as sessions, type ConfigurationSnapshot, type SessionReceiptPage, type SessionReceiptView, type SessionChoicesResponse, type SessionSelection, type ReminderListRequest, type ReminderListResponse } from "#neoastra";
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
import { changeSelection, validSelection } from "./sessionSelection";
import type { createNextSendSelectionStore } from "./nextSendSelection";
import { dispatchComposerKey, dispatchTransientComposerKey } from "./composerKeyboard";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";
import type { createReminderActions } from "./reminderActions";
import { validReminderList } from "./reminderListObservation";
import { SessionUsageInspector } from "./SessionUsageInspector";
import { RetainedRequestStrip } from "./RetainedRequestStrip";
import type { UsageTarget } from "./sessionUsage";
import { imageHelp, imageLimits, readPastedPng } from "./promptImages";
import { useShellLanguage } from "./shellLanguage";
import type { ClipboardEvent } from "react";

export function OwnedSessionPanel({ sessionId, epoch, projectId = null, usageTarget, infoControl, submissions, steering, compaction, abortRuns, queue, capability, runtimeReader, permissionReviewer, configuration, draftIndicators, selections, remindersTrigger, compactTrigger, onOpenReminders, onOpenHelp, onOpenPalette, reminderActions, readReminderCount, inputLifetime }: {
  sessionId: string; epoch: string; submissions: ReturnType<typeof createOwnedSubmissions>; capability: ReturnType<typeof createMutationCapability>;
  projectId?: string | null;
  inputLifetime?: { current: () => boolean };
  usageTarget?: UsageTarget | null;
  infoControl?: ReactNode;
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
  onOpenHelp?: () => void;
  onOpenPalette?: () => void;
  reminderActions?: ReturnType<typeof createReminderActions>;
  readReminderCount?: (request: ReminderListRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ReminderListResponse>;
}) {
  const [draft, setDraft] = useState(() => ({ text: restoreDraft(key => localStorage.getItem(key), sessionId), editGeneration: null as number | null }));
  const text = draft.text;
  const inputRevision = useRef(0);
  const { t } = useShellLanguage();
  const imageOwner = submissions.imageDrafts;
  useSyncExternalStore(imageOwner.subscribe, imageOwner.getSnapshot);
  const imageKey = JSON.stringify([epoch, sessionId, projectId, usageTarget?.expectedProjectPath ?? null]);
  const images = imageOwner.get(imageKey);
  const [imageNotice, setImageNotice] = useState("");
  const latestText = useRef(text);
  const restoredText = useRef(text);
  useLayoutEffect(() => { draftIndicators.clear(sessionId); }, [draftIndicators, sessionId, epoch]);
  function editText(value: string) {
    inputRevision.current++;
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
  const referenceScope = useContext(ProjectReferenceContext);
  const [expanded, setExpanded] = useState(false);
  const [choices, setChoices] = useState<SessionChoicesResponse>();
  const [selection, setSelection] = useState<SessionSelection | null>(null);
  const [choicesNotice, setChoicesNotice] = useState("Loading session choices…");
  const [choicesRevision, setChoicesRevision] = useState(0);
  useLayoutEffect(() => { inputRevision.current++; }, [choicesRevision, selection, choices]);
  const selectionRevision = useRef(0);
  function refreshChoices() {
    inputRevision.current++;
    selectionRevision.current++;
    setChoicesRevision(value => value + 1);
  }
  useEffect(() => selections.subscribe(value => {
    if (value.epoch !== epoch || value.sessionId !== sessionId || !capability.canMutate()) return;
    selectionRevision.current++;
    setChoices(value.choices);
    setSelection(value.selection);
    setChoicesNotice("Selections apply on Send; active runs and queued text are unchanged.");
  }), [epoch, sessionId, capability, selections]);
  useEffect(() => {
    const controller = new AbortController();
    const revision = selectionRevision.current;
    setChoices(undefined);
    setSelection(null);
    setChoicesNotice("Loading session choices…");
    void sessions.choices({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 15000 })
      .then(value => {
        capability.observe(value);
        if (controller.signal.aborted || revision !== selectionRevision.current || value.sessionId !== sessionId) return;
        if (value.status !== "ok" || value.epoch !== epoch || !value.current) {
          setChoicesNotice(`Session choices unavailable (${value.status}).`); return;
        }
        setChoices(value);
        setSelection(selections.get(epoch, sessionId, value));
        setChoicesNotice("Selections apply on Send; active runs and queued text are unchanged.");
      }).catch(() => { if (!controller.signal.aborted && revision === selectionRevision.current) setChoicesNotice("Session choices could not be loaded. Retry to refresh the provider catalog."); });
    return () => controller.abort();
  }, [epoch, sessionId, capability, selections, choicesRevision]);
  const [steerText, setSteerText] = useState("");
  const [steerMessage, setSteerMessage] = useState("Refresh runtime state explicitly before targeting a run.");
  const [compactMessage, setCompactMessage] = useState("Refresh runtime state explicitly before attempting idle compaction.");
  const [abortRunMessage, setAbortRunMessage] = useState("Refresh runtime state explicitly before targeting cancellation.");
  const [queueText, setQueueText] = useState(() => queue.draft(epoch, sessionId));
  function editQueueText(value: string) {
    queue.editDraft(epoch, sessionId, value);
    setQueueText(value);
  }
  function clearSubmittedQueueDraft(source: "composer" | "editor", revision: number | null) {
    // Neither toolbar-originated recovery nor a later secondary edit is the submitted editor draft.
    if (source !== "editor" || revision !== queue.draftRevision(epoch, sessionId)) return;
    editQueueText("");
  }
  const [queueMessage, setQueueMessage] = useState("Refresh runtime state explicitly before queueing text in this host.");
  const [message, setMessage] = useState("Ready to send to this owned session.");
  const [page, setPage] = useState<SessionReceiptPage>();
  const [runtimeState, setRuntimeState] = useState<RuntimeState>();
  const runtimeScope = useRef<ReturnType<typeof runtimeReader.forSelection> | null>(null);
  const [observedInvalidEpoch, setInvalidEpoch] = useState(!capability.canMutate());
  const canMutate = useSyncExternalStore(capability.subscribe, capability.canMutate);
  const invalidEpoch = observedInvalidEpoch || !canMutate;
  const reminderOperation = useSyncExternalStore(reminderActions?.subscribe ?? capability.subscribe,
    useCallback(() => reminderActions?.get({ epoch, sessionId }), [reminderActions, epoch, sessionId]));
  const [reminderReload, setReminderReload] = useState(0);
  const [reminderObservation, setReminderObservation] = useState<{
    epoch: string; sessionId: string; reload: number; count: number; operation: typeof reminderOperation;
  }>();
  const reminderRevision = useRef(0);
  useEffect(() => {
    const revision = ++reminderRevision.current;
    setReminderObservation(undefined);
    if (!onOpenReminders || !readReminderCount || !reminderActions || invalidEpoch) return;
    const controller = new AbortController();
    const operation = reminderActions.get({ epoch, sessionId });
    void readReminderCount({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 15000 })
      .then(value => {
        if (!controller.signal.aborted && reminderRevision.current === revision &&
          validReminderList({ epoch, sessionId }, value) && reminderActions.get({ epoch, sessionId }) === operation)
          setReminderObservation({ epoch, sessionId, reload: reminderReload, count: value.activeCount, operation });
      }).catch(() => { /* An error is unknown, never an observed zero or an automatic retry. */ });
    return () => { controller.abort(); reminderRevision.current++; };
  }, [epoch, sessionId, !!onOpenReminders, readReminderCount, reminderActions, reminderReload, invalidEpoch]);
  const observedReminderCount = !invalidEpoch && readReminderCount && reminderActions &&
    !reminderOperation?.pending && !reminderOperation?.hold && reminderObservation?.epoch === epoch &&
    reminderObservation.sessionId === sessionId && reminderObservation.reload === reminderReload &&
    reminderObservation.operation === reminderOperation ? reminderObservation.count : null;
  const reminderLabel = observedReminderCount === null ? t("Reminders for selected session: active count unknown")
    : t("Reminders for selected session: {count} active at last observation; may have changed", { count: observedReminderCount });
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
    setQueueText(queue.draft(epoch, sessionId));
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
  const availableComposerSteer = captureSteering(epoch, sessionId, observedTarget, text, "availability");
  const observedSteerRun = captureSteering(epoch, sessionId, observedTarget, "x", "availability");
  const availableCompact = captureCompaction(epoch, sessionId, observedTarget, "availability");
  const availableAbortRun = captureAbortRun(epoch, sessionId, observedTarget, "availability");
  // Emphasis only: original Send recovery wins; observations never become live-running state.
  const cancellationPrimary = !pending && !invalidEpoch && (pendingAbortRun
    ? capability.canSubmit(pendingAbortRun.request) : !!availableAbortRun && capability.canSubmit(availableAbortRun));
  const canCaptureQueue = captureQueue(epoch, sessionId, observedTarget, queueText, "availability") !== null;
  const availableComposerQueue = captureQueue(epoch, sessionId, observedTarget, text, "availability");
  const observedQueueAttachment = captureQueue(epoch, sessionId, observedTarget, "x", "availability");
  const showSteering = showContextAction(captureSteering(epoch, sessionId, observedTarget, "x", "availability") !== null,
    !!pendingSteer || steerMessage !== "Refresh runtime state explicitly before targeting a run.");
  const showQueue = showContextAction(observedQueueAttachment !== null,
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
    const latest = selections.current(epoch, sessionId);
    if (!retained && (latest || selection) && (!choices || choices.status !== "ok" || choices.epoch !== epoch || choices.sessionId !== sessionId
      || !validSelection(choices, latest ?? selection!) || latest && selection !== latest)) {
      setMessage("Next Send choices changed. Wait for the mounted composer to show the validated selection before sending.");
      return;
    }
    const references = referenceScope?.expectedEpoch === epoch && referenceScope.sessionId === sessionId
      ? { projectId: referenceScope.projectId, projectPath: referenceScope.projectPath } : null;
    const sendSelection = latest ?? selection ?? (images.length ? activeChoices?.current ?? null : null);
    if (!retained && images.length && (imageCapability !== true || !activeChoices || !sendSelection || !validSelection(activeChoices, sendSelection))) {
      setImageNotice("Image input is unsupported or unknown for the current selection. Remove attachments or choose an observed supported model."); return;
    }
    const revision = ++inputRevision.current;
    const capturedImages = images;
    const request = retained?.request ?? captureSubmission(epoch, sessionId, text, crypto.randomUUID(), sendSelection, references, images);
    if (!request && images.length) setImageNotice("Image Send requires empty or nonblank text up to 4096 characters and an explicit supported model.");
    if (!request || !capability.canSubmit(request)) return;
    draftIndicators.clear(sessionId);
    setMessage("Submission admission pending…");
    void submissions.submit(request, signal, capability, result => {
      observeEpoch(result);
      setMessage(result.status === "accepted" || result.status === "replay"
        ? "Submission accepted. Refresh submissions for dispatch outcome; this is not run completion."
        : `Submission: ${result.status}. Refresh receipts before considering an explicit retry.`);
      if ((!retained || !request.images?.length) && (result.status === "accepted" || result.status === "replay") && !signal.aborted && inputRevision.current === revision
        && imageOwner.get(imageKey) === capturedImages) { clearText(); imageOwner.replace(imageKey, capturedImages, []); }
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
      const recoveringImages = !!submissions.pending(sessionId)?.request.images?.length;
      const recovered = submissions.reconcile(sessionId, result, capability);
      // Receipt recovery cannot prove a remounted image draft's original input revision.
      if (recovered.sendRecovered && !signal.aborted && !recoveringImages) clearText();
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
      const retainedQueue = queue.pending(sessionId);
      const recoveredQueue = queue.reconcile(sessionId, result, capability);
      if (recoveredQueue.queueRecovered && retainedQueue)
        clearSubmittedQueueDraft(retainedQueue.source, retainedQueue.draftRevision);
      if (recoveredQueue.queueRecovered || recoveredQueue.cancellationsRecovered > 0) {
        setQueueMessage("Queue/cancellation receipt reconciled. Review reservation, host-only insertion and execution/cleanup separately.");
      }
    }, capability);
  }
  function steer(fromComposer = false) {
    if (images.length || pending?.request.images?.length) { setImageNotice("Steer refuses image attachments. Use normal Send or remove attachments first."); return; }
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    if (fromComposer && submissions.pending(sessionId)) return; // Disabled Send recovery is not the editable composer draft.
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
  function queueTextInHost(fromComposer: boolean) {
    if (images.length || pending?.request.images?.length) { setImageNotice("Queue refuses image attachments. Use normal Send or remove attachments first."); return; }
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = queue.pending(sessionId);
    // Only the secondary editor can explicitly retry its retained exact request.
    if (fromComposer && (submissions.pending(sessionId) || retained)) return;
    const request = retained?.request ?? captureQueue(epoch, sessionId, observedTarget, fromComposer ? text : queueText, crypto.randomUUID());
    if (!request || retained?.inFlight || !capability.canSubmit(request)) return;
    const source = retained?.source ?? (fromComposer ? "composer" : "editor");
    const draftRevision = retained?.draftRevision ?? queue.draftRevision(epoch, sessionId);
    setQueueMessage("Owner reservation pending; host-only insertion and execution are not yet confirmed.");
    if (fromComposer) setMessage("Owner queue reservation pending; composer draft retained.");
    void queue.submit(request, signal, capability, result => {
      observeEpoch(result);
      if (result.status === "accepted" || result.status === "replay") {
        clearSubmittedQueueDraft(source, draftRevision);
        setQueueMessage(`Owner reservation accepted. Refresh submissions manually for host-only insertion and execution/cleanup; neither durability nor run completion is implied.${fromComposer ? " Composer draft is retained." : ""}`);
      } else setQueueMessage(`Queue: ${result.status}. Uncertain requests retain exact text, key and attachment. No automatic retry.`);
      if (fromComposer) setMessage(result.status === "accepted" || result.status === "replay"
        ? "Queue reservation accepted; composer draft retained. Host-only insertion, durability and execution are not confirmed. Refresh receipts manually."
        : `Queue: ${result.status}. Composer draft retained. Review the exact queue request in the separate controls; no automatic retry.`);
    }, source);
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
  const imageCapability = activeChoices?.models.find(m => m.id === selected?.modelId)?.imageInput;
  async function pasteImages(event: ClipboardEvent<HTMLTextAreaElement>) {
    if (!event.clipboardData.files.length) return;
    event.preventDefault();
    const revision = ++inputRevision.current;
    if (event.clipboardData.files.length > imageLimits.count) { setImageNotice("Image paste refused. " + imageHelp); return; }
    const files = Array.from(event.clipboardData.files);
    const origin = event.currentTarget;
    if (pending || invalidEpoch || imageCapability !== true || !capability.canMutate()) {
      setImageNotice("Image paste unavailable: choose an explicitly supported observed model and an editable composer."); return;
    }
    const original = imageOwner.get(imageKey);
    const selectionVersion = selectionRevision.current; const lifetime = inputLifetime;
    const signal = scope.current?.signal;
    const current = () => !signal?.aborted && inputRevision.current === revision && selectionRevision.current === selectionVersion
      && origin.isConnected && !origin.closest("[inert]") && (!lifetime || lifetime.current()) && capability.canMutate()
      && imageOwner.get(imageKey) === original && !submissions.pending(sessionId);
    if (!current()) return;
    const finish = imageOwner.beginRead(imageKey);
    if (!finish) { setImageNotice("An image read is still pending, or the eight-read limit has been reached."); return; }
    try {
      if (original.length + files.length > imageLimits.count || files.some(file => file.type !== "image/png" || file.size > imageLimits.bytes)
        || original.reduce((sum, image) => sum + atob(image.base64).length, 0) + files.reduce((sum, file) => sum + file.size, 0) > imageLimits.total)
        throw new Error(imageHelp);
      const added = [];
      for (const file of files) { added.push(await readPastedPng(file, `Image ${original.length + added.length + 1}`)); if (!current()) return; }
      if (!imageOwner.replace(imageKey, original, [...original, ...added])) {
        setImageNotice("Image draft capacity reached (8 image-bearing drafts). Remove attachments from another draft first."); return;
      }
      setImageNotice("PNG attached. Original encoded bytes are retained until Send or removal.");
    } catch { if (current()) setImageNotice("Image paste refused. " + imageHelp); }
    finally { finish(); }
  }
  const imageCount = (pending?.request.images ?? images).length;
  const attachmentStrip = <div className="prompt-image-attachments" aria-label={t("Prompt image attachments")}>
    <details><summary>{t("PNG attachments")} — {t(imageCapability === true ? "available (observed)" : imageCapability === false ? "unsupported" : "unknown")}</summary><p>{t(imageHelp)}</p>
      <p>{imageCount ? t(imageCount === 1 ? "{count} image attached" : "{count} images attached", { count: imageCount }) : t("No images attached")}</p></details>
    {(pending?.request.images ?? images).map((image, index) => <figure key={index}>
      <img src={`data:image/png;base64,${image.base64}`} alt={image.title} width={80} height={80} />
      <figcaption>{image.title}</figcaption>
      <label>{t("Image title")}<input aria-label={t("Image title")} value={image.title} maxLength={80} disabled={!!pending || invalidEpoch}
        onCompositionStart={() => { inputRevision.current++; }}
        onChange={event => {
          inputRevision.current++;
          if (!event.currentTarget.isConnected || !scope.current || scope.current.signal.aborted
            || submissions.pending(sessionId) || !capability.canMutate() || imageOwner.get(imageKey) !== images) return;
          const title = event.target.value;
          if (!imageOwner.replace(imageKey, images, images.map((item, i) => i === index ? { ...item, title } : item)))
            setImageNotice(t("Image titles require 1–80 characters and no control characters."));
          else setImageNotice("");
        }} /></label>
      <button type="button" disabled={!!pending} onClick={() => { inputRevision.current++; imageOwner.replace(imageKey, images, images.filter((_, i) => i !== index)); }}>{t("Remove {title}", { title: image.title })}</button>
    </figure>)}
    {imageNotice && <p role="status">{imageNotice}</p>}
  </div>;
  return <section className="owned-session" aria-label={t("Owned text submission")}>
    {referenceScope && <p className="catalog-diagnostics">@ project references resolve once on normal Send (up to 32 paths, bounded ranges). Missing/unsafe/over-budget references stay literal. File contents are not uploaded. Queue and Steer always send literal text.</p>}
    {expanded && !pending && !invalidEpoch && <ExpandedPromptEditor text={text} onChange={editText} onPaste={pasteImages} attachments={attachmentStrip} onClose={() => { inputRevision.current++; setExpanded(false); }} />}
    {!expanded && attachmentStrip}
    <label className="sr-only" htmlFor="session-prompt">{t("Message")}</label>
    <textarea id="session-prompt" ref={promptInput} onPaste={pasteImages} className="prompt-input" rows={1} maxLength={32768} value={pending?.request.text ?? text} disabled={!!pending}
      onChange={event => editText(event.target.value)} placeholder={t("Ask CodeAlta to work on this project…")} onKeyDown={event => {
        if (dispatchTransientComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        event.currentTarget, onOpenHelp, onOpenPalette)) { event.preventDefault(); return; }
        if (dispatchComposerKey({ key: event.key, ctrlKey: event.ctrlKey,
          shiftKey: event.shiftKey, altKey: event.altKey, metaKey: event.metaKey,
          isComposing: event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode,
          repeat: event.repeat, defaultPrevented: event.defaultPrevented }, submit, () => steer(true))) event.preventDefault();
      }} />
    {!pending && !expanded && !invalidEpoch && <ProjectReferencePicker text={text} edit={editText} input={promptInput} />}
    <div className="composer-toolbar">
    <div className="prompt-options" aria-label={t("Session configuration")}>
      <label><span>{t("Agent prompt")}</span><select aria-label={t("Agent prompt")} value={selected?.agentPromptId ?? ""} disabled={selectionDisabled} onChange={event => select("agentPromptId", event.target.value)} title={t("Agent prompt for the next Send")}>
        {!activeChoices?.prompts.some(p => p.id === selected?.agentPromptId) && <option value={selected?.agentPromptId ?? ""}>{selected?.agentPromptId ?? t("Loading…")}</option>}
        {activeChoices?.prompts.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
      </select></label>
      <label><span>{t("Model")}</span><select aria-label={t("Model")} value={selected?.modelId ?? ""} disabled={selectionDisabled} onChange={event => select("modelId", event.target.value)} title={t("Model for the next Send · {provider}", { provider: selected?.providerKey ?? t("session provider") })}>
        <option value="">{t("Provider default")}</option>
        {selected?.modelId && !activeChoices?.models.some(m => m.id === selected.modelId) && <option value={selected.modelId}>{selected.modelId} ({t("not in catalog")})</option>}
        {activeChoices?.models.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
      </select></label>
      <label><span>{t("Reasoning")}</span><select aria-label={t("Reasoning")} value={selected?.reasoningEffort ?? ""} disabled={selectionDisabled || efforts.length === 0} onChange={event => select("reasoningEffort", event.target.value)} title={t("Supported reasoning effort for the selected model")}>
        <option value="">{t("Model default")}</option>
        {selected?.reasoningEffort && !efforts.includes(selected.reasoningEffort) && <option value={selected.reasoningEffort}>{selected.reasoningEffort} ({t("not in catalog")})</option>}
        {efforts.map(e => <option key={e} value={e}>{e}</option>)}
      </select></label>
    </div>
    <div className="history-controls">
      <span className="sr-only">{t("Enter to send · Shift+Enter for a new line · Ctrl+Enter to steer")}</span>
      {infoControl}
      {usageTarget && <SessionUsageInspector key={JSON.stringify(usageTarget)} target={usageTarget} capability={capability} />}
      {onOpenReminders && <button ref={remindersTrigger} type="button" className="composer-icon-button" data-reminder-count=""
        disabled={invalidEpoch} aria-label={reminderLabel} title={`${reminderLabel} (Ctrl+G, Ctrl+D)`}
        onClick={onOpenReminders}><AppIcon name="reminder" size={16} /><span className="reminder-count" aria-hidden="true">{observedReminderCount ?? "?"}</span></button>}
      <button id="expand-session-prompt" type="button" className="composer-icon-button" disabled={!!pending || invalidEpoch} aria-label={t("Expand prompt editor")} title={t("Edit prompt in a large window (F6)")} onClick={() => { inputRevision.current++; setExpanded(true); }}><AppIcon name="expand" size={16} /></button>
      {observedSteerRun && <button type="button" className="composer-icon-button" onClick={() => steer(true)}
        disabled={invalidEpoch || !!pending || !!pendingSteer || !availableComposerSteer || !capability.canSubmit(availableComposerSteer)}
        aria-label={t("Steer current composer to observed run")} aria-describedby="observed-steering-help"
        title={t("Steer current composer to observed run {run} (Ctrl+Enter; point-in-time observation, not run completion; retained steering requires separate manual review)", { run: observedSteerRun.expectedRunId })}>
        <AppIcon name="steer" size={16} /></button>}
      {observedQueueAttachment && <button type="button" className="composer-icon-button" onClick={() => queueTextInHost(true)}
        disabled={invalidEpoch || !!pending || !!pendingQueue || !availableComposerQueue || !capability.canSubmit(availableComposerQueue)}
        aria-label={t("Queue current composer in this host")} aria-describedby="observed-queue-help"
        title={t("Queue current composer for observed attachment {attachment} in this host only; no run target. Reservation does not confirm insertion or execution; composer draft stays editable.", { attachment: observedQueueAttachment.expectedAttachmentGeneration })}>
        <AppIcon name="queue" size={16} /></button>}
      {(availableCompact || pendingCompact) && <button ref={compactTrigger} type="button" className="composer-icon-button" onClick={compact}
        data-epoch={epoch} data-session-id={sessionId} data-project-id={projectId ?? ""}
        disabled={invalidEpoch || !!pendingCompact?.inFlight || (pendingCompact
          ? !capability.canSubmit(pendingCompact.request) : !availableCompact || !capability.canSubmit(availableCompact))}
        aria-label={pendingCompact ? t("Retry exact compaction request for attachment {attachment}", { attachment: pendingCompact.request.expectedAttachmentGeneration }) : t("Compact observed idle attachment")}
        aria-describedby="observed-compaction-help"
        title={pendingCompact ? `${t("Manual retry of exact compaction:")} ${t("epoch")} ${pendingCompact.request.expectedEpoch}, ${t("session")} ${pendingCompact.request.sessionId}, ${t("runtime")} ${pendingCompact.request.expectedRuntimeInstanceId}, ${t("attachment")} ${pendingCompact.request.expectedAttachmentGeneration}, ${t("request")} ${pendingCompact.request.clientRequestId}`
          : t("Compact observed idle attachment (Ctrl+F11; point-in-time idle observation permits only an attempt; provider must prove idle)")}>
        <AppIcon name="compact" size={16} /></button>}
      {(availableAbortRun || pendingAbortRun) && <button type="button" className={`cancel-run-button${cancellationPrimary ? " primary-button" : ""}`} onClick={abortRun}
        disabled={invalidEpoch || !!pendingAbortRun?.inFlight || (pendingAbortRun
          ? !capability.canSubmit(pendingAbortRun.request) : !availableAbortRun || !capability.canSubmit(availableAbortRun))}
        aria-label={pendingAbortRun ? t("Retry exact cancellation request for observed run {run}", { run: pendingAbortRun.request.expectedRunId }) : t("Cancel observed run")}
        aria-describedby="observed-run-cancellation-help"
        title={pendingAbortRun ? `${t("Manual retry of exact cancellation:")} ${t("epoch")} ${pendingAbortRun.request.expectedEpoch}, ${t("session")} ${pendingAbortRun.request.sessionId}, ${t("runtime")} ${pendingAbortRun.request.expectedRuntimeInstanceId}, ${t("attachment")} ${pendingAbortRun.request.expectedAttachmentGeneration}, ${t("run")} ${pendingAbortRun.request.expectedRunId}, ${t("request")} ${pendingAbortRun.request.clientRequestId}`
          : t("Cancel observed run {run} (point-in-time runtime observation, not original Send Abort; signalling does not confirm completion)", { run: availableAbortRun?.expectedRunId ?? "" })}>
        <AppIcon name="stop" size={16} /><span>{t(pendingAbortRun ? "Retry exact cancellation" : "Cancel observed run")}</span></button>}
      <button type="button" className={`send-button${cancellationPrimary ? "" : " primary-button"}`} disabled={invalidEpoch || !!pending?.inFlight || (pending ? !capability.canSubmit(pending.request) : (images.length > 0 && (imageCapability !== true || !activeChoices || !selected || !validSelection(activeChoices, selected))) || captureSubmission(epoch, sessionId, text, "availability", images.length ? selected : null, null, images) === null)} onClick={submit}>{pending ? t("Retry exact request") : <><span>{t("Send")}</span><AppIcon name="send" size={14} /></>}</button>
    </div>
    </div>
    <span id="observed-run-cancellation-help" className="sr-only">{t("Targets a point-in-time observed run, not the original Send receipt. Cancellation signalled does not confirm run completion. Retained requests are only retried manually against their original target after the previous wait settles.")}</span>
    <span id="observed-steering-help" className="sr-only">{t("Uses current composer text and the point-in-time observed run. Admission is not run completion. Retained steering is reviewed or retried separately, never from this button.")}</span>
    <span id="observed-queue-help" className="sr-only">{t("Uses current editable composer text and the point-in-time observed attachment, including busy or draining attachments; never targets a run. Reservation does not prove host-only insertion, durability or execution. The composer draft is preserved. Retained queue requests are reviewed or retried separately, never from this button.")}</span>
    <span id="observed-compaction-help" className="sr-only">{t("Point-in-time idle observation permits only an attempt; the provider must prove idle. Busy is a permanent outcome, not an automatic retry. Retained requests are retried manually against their original attachment after the previous wait settles.")}</span>
    {choicesNotice !== "Selections apply on Send; active runs and queued text are unchanged." && <p className="composer-notice" role={choicesNotice.includes("could not") || choicesNotice.includes("unavailable") ? "alert" : "status"}>{choicesNotice}
      {(choicesNotice.includes("could not") || choicesNotice.includes("unavailable")) && <button type="button" disabled={!!pending || invalidEpoch} onClick={refreshChoices}>{t("Retry choices")}</button>}</p>}
    {(message !== "Ready to send to this owned session." || pending || pendingAborts.length > 0) && <p className="composer-notice" role="status">{message}</p>}
    {(pending || pendingAborts.length > 0) && <button type="button" onClick={() => refresh()}>{t("Refresh receipts")}</button>}
    {invalidEpoch && <p role="alert">{t("Host/runtime identity changed. Reload required; mutations are disabled. The exact uncertain request is retained and will not be rebased or resent.")}</p>}
    {runtimeState?.kind === "error" && <p role="alert">{t("Runtime observation unavailable ({code}).", { code: runtimeState.code })} {t(['stale_epoch', 'stale_runtime'].includes(runtimeState.code) ? "Reload required." : "No idle or completion state is inferred.")}</p>}
    {mcpPlugin && /fail|error/i.test(mcpPlugin.state) && <p role="alert">{t("MCP plugin:")} {mcpPlugin.state}. {t("Check advanced diagnostics.")}</p>}
    {page && page.status !== "ok" && <p role="alert">{t("Receipt snapshot:")} {page.status}</p>}
    {(compactMessage !== "Refresh runtime state explicitly before attempting idle compaction." || pendingAbortRun) && <p className="composer-notice" role="status">{compactMessage !== "Refresh runtime state explicitly before attempting idle compaction." && compactMessage} {pendingAbortRun && abortRunMessage}</p>}
    {pendingCompact && <p className="composer-notice">{t("Manual exact compaction retry only:")} {t("epoch")} {pendingCompact.request.expectedEpoch} · {t("session")} {pendingCompact.request.sessionId} · {t("runtime")} {pendingCompact.request.expectedRuntimeInstanceId} · {t("attachment")} {pendingCompact.request.expectedAttachmentGeneration} · {t("request")} {pendingCompact.request.clientRequestId}. {t("Refresh never retargets this intent.")}</p>}
    {pendingAbortRun && <p className="composer-notice">{t("Manual exact cancellation retry only:")} {t("epoch")} {pendingAbortRun.request.expectedEpoch} · {t("session")} {pendingAbortRun.request.sessionId} · {t("runtime")} {pendingAbortRun.request.expectedRuntimeInstanceId} · {t("attachment")} {pendingAbortRun.request.expectedAttachmentGeneration} · {t("run")} {pendingAbortRun.request.expectedRunId} · {t("request")} {pendingAbortRun.request.clientRequestId}. {t("Refresh never retargets this intent.")}</p>}
    <RetainedRequestStrip epoch={epoch} sessionId={sessionId} queue={queue} steering={steering} />
    {(showSteering || showQueue) && <div className="context-actions">
      {showSteering && <div><label>{t("Steer observed run")} {pendingSteer?.request.expectedRunId ?? observedTarget?.entry?.activeRunId}<textarea maxLength={32768} value={pendingSteer?.request.text ?? steerText} disabled={!!pendingSteer} onChange={event => setSteerText(event.target.value)} /></label>
        {pendingSteer && <p className="detail">{t("Retained run")} {pendingSteer.request.expectedRunId} · {t("attachment")} {pendingSteer.request.expectedAttachmentGeneration} · {t("request")} {pendingSteer.request.clientRequestId}; {t("refresh never retargets this request.")}</p>}
        <button type="button" disabled={invalidEpoch || !!pendingSteer?.inFlight || (pendingSteer ? !capability.canSubmit(pendingSteer.request) : !canCaptureSteer)} onClick={() => steer()}>{t(pendingSteer ? "Retry exact steering request" : "Steer observed run")}</button>
        {steerMessage !== "Refresh runtime state explicitly before targeting a run." && <p role="status">{steerMessage}</p>}</div>}
      {showQueue && <div><label>{t("Host-only queued text")}<textarea maxLength={32768} value={pendingQueue?.request.text ?? queueText} disabled={!!pendingQueue} onChange={event => editQueueText(event.target.value)} /></label>
        <p className="detail">{t("Reservation is not insertion, execution or durable storage. Refresh receipts manually.")}</p>
        {pendingQueue && <p className="detail">{t("Retained attachment")} {pendingQueue.request.expectedAttachmentGeneration} · {t("request")} {pendingQueue.request.clientRequestId}; {t("no durable recovery or retargeting.")}</p>}
        <button type="button" disabled={invalidEpoch || !!pendingQueue?.inFlight || (pendingQueue ? !capability.canSubmit(pendingQueue.request) : !canCaptureQueue)} onClick={() => queueTextInHost(false)}>{t(pendingQueue ? "Retry exact host-only queue request" : "Queue text — this host only")}</button>
        {pendingQueueCancellations.map(value => <div key={value.intent.request.targetOperationId}>
          <p className="detail">{t("Retained cancellation")} · {t("original operation")} {value.intent.request.targetOperationId} · {t("request")} {value.intent.request.clientRequestId}</p>
          <button type="button" disabled={invalidEpoch || value.inFlight || !capability.canSubmit(value.intent.request)} onClick={() => cancelQueued(undefined, value.intent.request.targetOperationId)}>{t("Retry exact queued-operation cancellation")}</button>
        </div>)}
        {queueMessage !== "Refresh runtime state explicitly before queueing text in this host." && <p role="status">{queueMessage}</p>}</div>}
    </div>}
    {permissionReviewer && <CommandPermissionPanel reviewer={permissionReviewer} epoch={epoch} sessionId={sessionId} />}
    <details className="advanced-session-controls"><summary>{t("Advanced session controls and diagnostics")}</summary><div>
    <p className="detail">{t("Selections apply on Send; active runs and queued text are unchanged.")}</p>
    <button id="refresh-session-context" type="button" onClick={() => void runtimeScope.current?.refresh()} aria-label={t("Refresh context and runtime configuration")} title={`${t("Refresh context")} · ${runtimeConfiguration?.providerKey ?? t("session provider")}`}><AppIcon name="refresh" size={14} /> {t("Refresh context")}</button>
    <button type="button" disabled={!!pending || invalidEpoch} onClick={refreshChoices}><AppIcon name="refresh" size={14} /> {t("Refresh choices")}</button>
    {onOpenReminders && readReminderCount && reminderActions && <button type="button" disabled={invalidEpoch}
      aria-label={t("Refresh observed reminder count")} onClick={() => { reminderRevision.current++; setReminderObservation(undefined); setReminderReload(value => value + 1); }}>
      <AppIcon name="refresh" size={14} /> {t("Refresh reminder count")}</button>}
    <p className="detail">MCP: {mcpPlugin?.state ?? t(configuration?.pluginRuntimeAvailable ? "Off" : "Unavailable")} · {t(runtimeState?.kind === "loading" ? "Reading context…" : runtimeConfiguration?.activeRunId ? "Run active" : runtimeConfiguration ? "Context ready" : "Context unavailable")}.</p>
    <p className="detail">{t("Existing session only.")} {t(permissionReviewer ? "Supported plain commands require explicit review below; other permissions are denied." : "Permissions are denied by default. Relaunch with --review-owned-command-permissions in owned mode to opt in to supported plain command review.")} {t("User input is cancelled; plugins and host-contributed tools are disabled. A submitted receipt is not a completed run. Receipt capacity is 256 for this host lifetime.")}</p>
    <p className="detail">{t("Send/Abort retains at most 256 local intents combined. Selection changes retain exact requests and live waiter exclusion. After document reload, browse host receipts manually; lost text and retry keys are not reconstructed. No automatic retry.")}</p>
    <button type="button" onClick={() => refresh()}>{t("Refresh submissions")}</button>
    {(Array.isArray(page?.rows) ? page.rows : []).filter(row => row && typeof row.sessionId === "string" && row.sessionId.toLowerCase() === sessionId.toLowerCase()).map(row => <div key={row.operationId}>
      {row.kind === "Queue" ? <><p>Queue · {row.operationId}</p>
        {queueReceiptPhases(row)?.map((phase, index) => <p key={index}>{index + 1}. {phase}</p>) ?? <p>{t("Malformed queue receipt; not actionable.")}</p>}</>
        : row.kind === "CancelQueue" ? <p>CancelQueue · {queueCancellationStatus(row) ?? t("Malformed cancellation receipt; not actionable.")} · {t("target")} {row.targetOperationId}</p>
        : <p>{row.kind} · {t(row.outcome === "Completed" ? (row.kind === "Abort" ? "Original Send control settled; not rollback, decision retraction or run termination" : row.kind === "AbortRun" ? "Cancellation signalled; run completion is not confirmed" : row.kind === "Compact" ? "compaction settled successfully" : row.kind === "Steer" ? "steering input submitted" : "submission submitted") : row.outcome === "Failed" ? "operation failed" : row.outcome === "Cancelled" ? "operation cancelled" : "operation pending")} {row.code ?? ""} · {row.operationId}</p>}
      {row.kind === "Queue" && <button type="button" disabled={invalidEpoch || !!queue.cancelPending(row.operationId)
        || (!!pendingQueue?.inFlight && pendingQueue.request.clientRequestId === row.clientRequestId)
        || !page || captureQueueCancellation(epoch, sessionId, page, row, "availability") === null} onClick={() => cancelQueued(row)}>{t("Cancel this queued operation")}</button>}
      {row.kind === "Send" && row.state === "pending" && <button type="button" disabled={invalidEpoch || !!submissions.abortPending(row.operationId)
        || (!!pending?.inFlight && pending.request.clientRequestId === row.clientRequestId)
        || !page || captureSubmissionAbort(epoch, sessionId, page, row, "availability") === null} onClick={() => abort(row)}>{t("Abort original Send operation")}</button>}
    </div>)}
    {page?.next != null && <button type="button" onClick={() => refresh(page.next!)}>{t("Next receipt page")}</button>}
    {pendingAborts.map(value => <div key={value.intent.request.targetOperationId}>
      <p className="detail">{t("Retained Abort: original session")} {value.intent.sessionId} · {t("operation")} {value.intent.request.targetOperationId} · {t("request")} {value.intent.request.clientRequestId}</p>
      <button type="button" disabled={invalidEpoch || value.inFlight || !capability.canSubmit(value.intent.request)}
        onClick={() => abort(undefined, value.intent.request.targetOperationId)}>{t("Retry exact original Send Abort")}</button>
    </div>)}
    <h3>{t("Current runtime — manual point-in-time observation")}</h3>
    <p className="detail">{t("Recorded facts at the last refresh, not provider inactivity or successful run completion. Queue depth is unknown. This does not acknowledge effects or synchronize Display, receipts or persisted history.")}</p>
    <button type="button" disabled={runtimeState?.kind === "error" && ["stale_epoch", "stale_runtime"].includes(runtimeState.code)} onClick={() => void runtimeScope.current?.refresh()}>{t("Refresh runtime state")}</button>
    {runtimeState?.kind === "loading" && <p role="status">{t("Reading current runtime facts…")}</p>}
    {runtimeState?.kind === "ready" && <>
      <p className="detail">{t("Runtime instance")} {runtimeState.snapshot.runtimeInstanceId} · {t("coordinator transition recorded:")} {t(runtimeState.snapshot.coordinatorTransitionInProgress ? "yes" : "no")}</p>
      {runtimeState.snapshot.entry ? <>
        <dl>
          <dt>{t("Attachment generation (identity, not revision)")}</dt><dd>{runtimeState.snapshot.entry.attachmentGeneration}</dd>
          <dt>{t("Active run recorded")}</dt><dd>{runtimeState.snapshot.entry.activeRunId ?? t("No run recorded — provider activity unknown")}</dd>
          <dt>{t("Shutdown observed on entry")}</dt><dd>{t(runtimeState.snapshot.entry.isTerminated ? "yes" : "no")}</dd>
          <dt>{t("Attachment retiring")}</dt><dd>{t(runtimeState.snapshot.entry.isRetiring ? "yes" : "no")}</dd>
          <dt>{t("Queue drain in progress")}</dt><dd>{t(runtimeState.snapshot.entry.queueDrainInProgress ? "yes" : "no")}</dd>
        </dl>
        <p className="detail">{t("Captured configuration — not verified provider-effective settings.")}</p>
        <dl>
          <dt>{t("Provider / configured key")}</dt><dd>{runtimeState.snapshot.entry.providerId} / {runtimeState.snapshot.entry.providerKey}</dd>
          <dt>{t("Captured model")}</dt><dd>{runtimeState.snapshot.entry.modelId ?? t("Not recorded")}</dd>
          <dt>{t("Captured reasoning")}</dt><dd>{runtimeState.snapshot.entry.reasoningEffort ?? t("Not recorded")}</dd>
          <dt>{t("Captured prompt")}</dt><dd>{runtimeState.snapshot.entry.agentPromptId ?? t("Not recorded")}</dd>
          <dt>{t("Pending prompt (separate selection)")}</dt><dd>{runtimeState.snapshot.entry.pendingAgentPromptId ?? t("None recorded")}</dd>
        </dl>
      </> : <p>{t("No runtime entry observed. This does not imply idle, completion or absence of a durable session.")}</p>}
    </>}
    <h3>{t("Signal cancellation for observed run")}</h3>
    <p className="detail">{t("Targets only the explicitly observed runtime, attachment and run. Unsupported, stale, retiring, transitioning or draining targets fail closed without fallback. Signalling is not run completion or rollback; previously accepted decisions remain accepted. Failure can occur after signalling. Refreshes never retarget a retained request.")}</p>
    {pendingAbortRun && <p className="detail">{t("Retained target: runtime")} {pendingAbortRun.request.expectedRuntimeInstanceId} · {t("attachment")} {pendingAbortRun.request.expectedAttachmentGeneration} · {t("run")} {pendingAbortRun.request.expectedRunId} · {t("request")} {pendingAbortRun.request.clientRequestId}</p>}
    <p role="status">{abortRunMessage}</p>
    <h3>{t("Compact the observed attachment if idle now")}</h3>
    <p className="detail">{t("Recorded idleness only permits an attempt: the provider must prove idle without waiting. Compacts context current at provider admission, not the history from your observation. Stale, retiring, non-owned or unsupported targets are rejected without fallback. No new permission authority is created. A busy receipt is permanent; a new explicit action uses a fresh key. Refreshes never retarget an uncertain request.")}</p>
    {pendingCompact && <p className="detail">{t("Retained target: runtime")} {pendingCompact.request.expectedRuntimeInstanceId} · {t("attachment")} {pendingCompact.request.expectedAttachmentGeneration} · {t("request")} {pendingCompact.request.clientRequestId}</p>}
    <p role="status">{compactMessage}</p>
    </div></details>
  </section>;
}
