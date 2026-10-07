import { Button, HTMLSelect } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { createPortal } from "react-dom";
import { PromptImageAttachments } from "./PromptImageAttachments";
import { formatThinkingElapsed, useThinkingElapsed } from "./thinkingElapsed";
import type { DisplayState } from "./sessionDisplay";
import { useCallback, useContext, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type ReactNode, type Ref } from "react";
import { ProjectReferenceContext, ProjectReferencePicker } from "./ProjectReferencePicker";
import { IssuePicker } from "./IssuePicker";
import { PluginPromptPickers } from "./PluginPromptPicker";
import { pluginComposerEvent, type PluginComposerRequest } from "./pluginUi";
import { modelCatalog, sessionOperations as sessions, type ConfigurationSnapshot, type SessionReceiptPage, type SessionReceiptView, type SessionChoicesResponse, type SessionSelection, type ReminderListRequest, type ReminderListResponse } from "#neoastra";
import { activateSessionModels } from "./activateSessionModels";
import { captureSubmission, captureSubmissionAbort, createMutationCapability, outgoingKey, refreshSubmissions, type SubmissionResult, type createOwnedSubmissions } from "./sessionOperations";
import { captureSteering, type createSteeringSubmissions } from "./sessionSteering";
import { captureCompaction, hasPendingCompaction, watchCompaction, type CompactionWatch, type createCompactionSubmissions } from "./sessionCompaction";
import { captureAbortRun, type createAbortRunSubmissions } from "./sessionAbortRun";
import { captureQueue, captureQueueCancellation, queueReceiptPhases, queueCancellationStatus, type createQueueSubmissions } from "./sessionQueue";
import type { createRuntimeStateReader, RuntimeState } from "./runtimeState";
import type { createPermissionReviewer } from "./sessionPermissions";
import { CommandPermissionPanel } from "./CommandPermissionPanel";
import { createDraftIndicators, persistDraft, restoreDraft } from "./promptDraft";
import { AppIcon } from "./AppIcon";
import type { PromptInput } from "./PromptEditor";
import { createPromptHistory, noPromptRecall, recallPrompt } from "./promptHistory";
import { changeSelection, validSelection } from "./sessionSelection";
import { ProviderChooser } from "./ProviderChooser";
import type { createNextSendSelectionStore } from "./nextSendSelection";
import { ComposerSurface, ComposerSelectionFields, ReasoningSlider, SendSplitButton } from "./ComposerSurface";
import { dispatchComposerKey, dispatchTransientComposerKey } from "./composerKeyboard";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";
import type { createReminderActions } from "./reminderActions";
import { validReminderList } from "./reminderListObservation";
import { SessionUsageInspector } from "./SessionUsageInspector";
import { ComposerQueueStrip } from "./ComposerQueueStrip";
import { queuePreview, type ComposerQueueItem } from "./composerQueue";
import { ActiveProviderStatus } from "./ActiveProviderStatus";
import { ObservationStatus } from "./ObservationStatus";
import type { UsageTarget } from "./sessionUsage";
import { imagePasteFailure, readPastedImage } from "./promptImages";
import { useShellLanguage } from "./shellLanguage";
import type { ClipboardEvent } from "react";
import { sendDiagnostics } from "./sendDiagnostics";

// Sent prompts of this window, for Alt+Up / Alt+Down in the prompt editor.
const promptHistory = createPromptHistory();

/** What to tell the user when a Send was not accepted; the prompt stays in the composer in every case. */
export function sendFailureMessage(status: string, reason?: string): string {
  switch (status) {
    case "uncertain": return reason === "admission_failed" ? "The send could not start (Settings → Application Logs has the reason). Check the timeline before sending it again."
      : `CodeAlta did not confirm this send${reason ? ` (${reason})` : ""}. Check the timeline before sending it again.`;
    case "invalid_request": return "The prompt or the selected model was not accepted.";
    case "conflict": return "This prompt was already sent with different content.";
    case "expired": return "This prompt was already sent earlier. Check the timeline before sending it again.";
    case "capacity": return "Too many requests are pending. Wait for one to finish.";
    case "closed": case "stale_epoch": return "CodeAlta restarted. Reload the window to continue.";
    default: return `The send was not accepted (${status}).`;
  }
}

export function OwnedSessionPanel({ onRunActivity, sessionId, epoch, projectId = null, usageTarget, persistedUsage = null, infoControl, submissions, steering, compaction, abortRuns, queue, capability, runtimeReader, permissionReviewer, configuration, draftIndicators, selections, remindersTrigger, compactTrigger, onOpenReminders, onOpenHelp, onOpenCommands, reminderActions, readReminderCount, activeReminderCount = null, autoSend = null, inputLifetime, liveState, timelineNotices, onOpenCatalog, active = true, observing = true }: {
  active?: boolean;
  observing?: boolean;
  sessionId: string; epoch: string; submissions: ReturnType<typeof createOwnedSubmissions>; capability: ReturnType<typeof createMutationCapability>;
  projectId?: string | null;
  inputLifetime?: { current: () => boolean };
  liveState?: DisplayState | null;
  timelineNotices?: HTMLElement | null;
  usageTarget?: UsageTarget | null;
  persistedUsage?: string | null;
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
  /** Reports whether this session is working while the panel watches it, and null once it no longer does. */
  onRunActivity?: (running: boolean | null) => void;
  onOpenReminders?: () => void;
  /** A prompt from the New session tab: sent once, when this composer holds exactly that text and its choices are validated. */
  autoSend?: { text: string; consume: () => void } | null;
  /** Active reminders of this session as last reported by the host for the explorer markers; null while unknown. */
  activeReminderCount?: number | null;
  onOpenHelp?: () => void;
  onOpenCommands?: () => void;
  onOpenCatalog?: (page: "models" | "prompts" | "providers") => void;
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
    setSendFailure("");
    recall.current = noPromptRecall;
    inputRevision.current++;
    latestText.current = value;
    const editGeneration = draftIndicators.edit(sessionId, value, restoredText.current);
    setDraft({ text: value, editGeneration });
  }
  const recall = useRef(noPromptRecall);
  function recallSentPrompt(direction: -1 | 1) {
    const next = recallPrompt(promptHistory.list(sessionId), recall.current, direction, latestText.current);
    if (!next) return false;
    const state = next.state;
    editText(next.text);
    recall.current = state;
    return true;
  }
  function clearText() {
    latestText.current = "";
    restoredText.current = "";
    draftIndicators.clear(sessionId);
    setDraft({ text: "", editGeneration: null });
  }
  const promptInput = useRef<PromptInput>(null);
  const referenceScope = useContext(ProjectReferenceContext);
  const [expanded, setExpanded] = useState(false);
  const [enqueue, setEnqueue] = useState(false);
  const [providerBusy, setProviderBusy] = useState(false);
  const [receiptUnavailable, setReceiptUnavailable] = useState(false);
  const stagedRevision = useSyncExternalStore(queue.composer.subscribe, queue.composer.getSnapshot);
  const staged = queue.composer.list(epoch, sessionId);
  // A session with prompts waiting is followed even while its tab is hidden: they leave when it is idle.
  const watching = observing || staged.length > 0;
  // What a Send that was not taken says, in the status line: a prompt is never answered with a toast.
  const [sendFailure, setSendFailure] = useState("");
  const [retryTick, setRetryTick] = useState(0);
  const holdUntil = useRef(0);
  // The user messages on screen when a steering prompt left: a later one with its text is the agent taking it up.
  const steerBaseline = useRef(new Map<string, Set<string>>());
  const queueRevision = useSyncExternalStore(queue.subscribe, queue.getSnapshot);
  const steeringRevision = useSyncExternalStore(steering.subscribe, steering.getSnapshot);
  const [choices, setChoices] = useState<SessionChoicesResponse>();
  const [selection, setSelection] = useState<SessionSelection | null>(null);
  const [choicesNotice, setChoicesNotice] = useState("Loading session choices…");
  const [choicesRevision, setChoicesRevision] = useState(0);
  const [loadingChoices, setLoadingChoices] = useState(true);
  useLayoutEffect(() => { inputRevision.current++; }, [choicesRevision, selection, choices]);
  const selectionRevision = useRef(0);
  function loadModelChoices() {
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
    if (!observing) { setLoadingChoices(false); return; }
    const controller = new AbortController();
    const revision = selectionRevision.current;
    setChoicesNotice("Loading session choices…");
    setLoadingChoices(true);
    const current = () => !controller.signal.aborted && revision === selectionRevision.current && capability.canMutate();
    const read = async () => {
      const value = await sessions.choices({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 15000 });
      capability.observe(value);
      return value;
    };
    void activateSessionModels(epoch, sessionId, read, async providerId => {
      const result = await modelCatalog.models({ expectedEpoch: epoch, providerId }, { signal: controller.signal, timeoutMilliseconds: 15000 });
      capability.observe(result);
      return result;
    }, current)
      .then(value => {
        capability.observe(value);
        if (controller.signal.aborted || revision !== selectionRevision.current || value.sessionId !== sessionId) return;
        if (value.status !== "ok" || value.epoch !== epoch || !value.current) {
          setChoicesNotice(`Session choices unavailable (${value.status}).`); return;
        }
        setChoices(value);
        const verified = selections.get(epoch, sessionId, value);
        const retained = selections.current(epoch, sessionId);
        // A temporarily empty catalog must not silently replace an unsent preference.
        // Keep it visibly unverified; normal Send still requires validSelection.
        setSelection(verified ?? (retained?.providerKey === value.current.providerKey ? retained : null));
        setChoicesNotice("Selections apply on Send; active runs and queued text are unchanged.");
      }).catch(() => { if (!controller.signal.aborted && revision === selectionRevision.current) setChoicesNotice("Session choices could not be loaded. Reopen the Model selector to try again."); })
      .finally(() => { if (!controller.signal.aborted) setLoadingChoices(false); });
    return () => controller.abort();
  }, [epoch, sessionId, capability, selections, choicesRevision, observing]);
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
  const [submittedThinking, setSubmittedThinking] = useState<{ key: string; runId: string | null } | null>(null);
  const [submittedCompact, setSubmittedCompact] = useState<CompactionWatch | null>(null);
  const [runtimeState, setRuntimeState] = useState<RuntimeState>();
  const observedProvider = runtimeState?.kind === "ready" ? runtimeState.snapshot.entry?.providerKey : undefined;
  const observedTransition = runtimeState?.kind === "ready" ? runtimeState.snapshot.coordinatorTransitionInProgress : undefined;
  // Lifecycle observations, not polling: reconcile persisted selection after attach/detach/switch.
  const catalogObservation = useRef<string | null>(null);
  useEffect(() => {
    if (runtimeState?.kind !== "ready") return;
    const signature = JSON.stringify([epoch, sessionId, observedProvider, observedTransition]);
    if (catalogObservation.current === signature) return;
    catalogObservation.current = signature;
    setChoicesRevision(value => value + 1);
  }, [runtimeState, observedProvider, observedTransition, epoch, sessionId]);
  const queueReviewRuntime = useRef(runtimeState); queueReviewRuntime.current = runtimeState;
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
    if (!observing || !onOpenReminders || !readReminderCount || !reminderActions || invalidEpoch) return;
    const controller = new AbortController();
    const operation = reminderActions.get({ epoch, sessionId });
    void readReminderCount({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 15000 })
      .then(value => {
        if (!controller.signal.aborted && reminderRevision.current === revision &&
          validReminderList({ epoch, sessionId }, value) && reminderActions.get({ epoch, sessionId }) === operation)
          setReminderObservation({ epoch, sessionId, reload: reminderReload, count: value.activeCount, operation });
      }).catch(() => { /* An error is unknown, never an observed zero or an automatic retry. */ });
    return () => { controller.abort(); reminderRevision.current++; };
  }, [epoch, sessionId, !!onOpenReminders, readReminderCount, reminderActions, reminderReload, invalidEpoch, observing]);
  const listedReminderCount = !invalidEpoch && readReminderCount && reminderActions &&
    !reminderOperation?.pending && !reminderOperation?.hold && reminderObservation?.epoch === epoch &&
    reminderObservation.sessionId === sessionId && reminderObservation.reload === reminderReload &&
    reminderObservation.operation === reminderOperation ? reminderObservation.count : null;
  // The host-wide marker read follows every reminder change; the per-session list read covers the time before it.
  const observedReminderCount = invalidEpoch ? null : activeReminderCount ?? listedReminderCount;
  const reminderLabel = observedReminderCount === null ? t("Reminders")
    : t("Reminders: {count} active", { count: observedReminderCount });
  const scope = useRef<AbortController | null>(null);
  const receiptRevision = useRef(0);
  useEffect(() => {
    const controller = new AbortController();
    scope.current = controller;
    const restored = ownPending()?.request.text ?? restoreDraft(key => localStorage.getItem(key), sessionId);
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
    setSubmittedThinking(null);
    setSubmittedCompact(null);
    setMessage(submissions.pending(sessionId) || submissions.aborts(sessionId).length
      ? "Retained Send/Abort intent exists. Refresh receipts manually or retry the exact request after its original waiter settles."
      : "Ready to send to this owned session.");
    return () => { controller.abort(); scope.current = null; };
  }, [sessionId, epoch, submissions, steering, compaction, abortRuns, queue, runtimeReader, capability]);

  useEffect(() => {
    setRuntimeState(undefined);
    if (!watching) return;
    const controller = new AbortController();
    const runtime = runtimeReader.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, setRuntimeState, capability.observe);
    runtimeScope.current = runtime;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const observe = async () => {
      if (controller.signal.aborted || !capability.canMutate()) return;
      await runtime.refresh(true);
      if (!controller.signal.aborted) timer = setTimeout(observe, 1000);
    };
    void observe();
    return () => { clearTimeout(timer); controller.abort(); if (runtimeScope.current === runtime) runtimeScope.current = null; };
  }, [epoch, sessionId, capability, runtimeReader, watching]);

  useEffect(() => {
    if (!watching) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const read = async () => {
      if (stopped || !capability.canMutate()) return;
      await refresh();
      if (!stopped) timer = setTimeout(read, 1500);
    };
    void read();
    return () => { stopped = true; clearTimeout(timer); };
  }, [epoch, sessionId, capability, watching]);

  useEffect(() => {
    if (!submissions.pending(sessionId)) draftIndicators.persisted(sessionId, draft.editGeneration,
      persistDraft((key, value) => localStorage.setItem(key, value), key => localStorage.removeItem(key), sessionId, draft.text));
    else draftIndicators.clear(sessionId);
  }, [sessionId, draft, submissions, draftIndicators]);

  // A queued prompt on its way is not the composer's own request: the editor stays the user's.
  function ownPending() {
    const retained = submissions.pending(sessionId);
    return retained && queue.composer.list(epoch, sessionId).some(item => item.request?.clientRequestId === retained.request.clientRequestId) ? undefined : retained;
  }
  const retainedSend = submissions.pending(sessionId);
  const pending = ownPending();
  useEffect(() => {
    if (active) sendDiagnostics.info("composer availability", { invalidEpoch,
      capabilityValid: capability.canMutate(), retainedRequest: !!pending, inFlight: pending?.inFlight ?? false });
  }, [active, invalidEpoch, !!pending, pending?.inFlight, capability]);
  const pendingAborts = submissions.aborts(sessionId);
  const pendingSteer = steering.pending(sessionId);
  const pendingCompact = compaction.pending(sessionId);
  const pendingAbortRun = abortRuns.pending(sessionId);
  const pendingQueue = queue.pending(sessionId);
  const pendingQueueCancellations = queue.cancellations(sessionId);
  const observedTarget = runtimeState?.kind === "ready" ? runtimeState.snapshot : undefined;
  const runtimeConfiguration = observedTarget?.entry;
  // Presentation only: live run updates never authorize composer operations.
  const currentLive = liveState?.hostEpoch === epoch && liveState.sessionId === sessionId ? liveState : null;
  const liveConnected = currentLive?.kind === "connected" && !currentLive.snapshot?.isClosed;
  const [observedBusy, setObservedBusy] = useState(false);
  useEffect(() => {
    if (runtimeState?.kind === "ready") setObservedBusy(!!runtimeState.snapshot.entry?.activeRunId);
  }, [runtimeState]);
  const liveLifecycle = liveConnected ? currentLive.snapshot?.session?.lifecycle : null;
  const runActive = !invalidEpoch && (runtimeState === undefined ? liveLifecycle?.kind === "RunSubmitted" : observedBusy);
  useEffect(() => {
    if (!submittedThinking) return;
    const receipt = page?.rows.find(row => row.sessionId === sessionId && row.clientRequestId === submittedThinking.key && row.kind === "Send");
    if (invalidEpoch || receipt?.state === "terminal") { setSubmittedThinking(null); return; }
    if (runtimeState?.kind !== "ready") return;
    const runId = runtimeState.snapshot.entry?.activeRunId ?? null;
    if (runId && !submittedThinking.runId) setSubmittedThinking({ ...submittedThinking, runId });
    else if (!runId && submittedThinking.runId) setSubmittedThinking(null);
  }, [page, runtimeState, invalidEpoch, sessionId, submittedThinking]);
  const composerBusy = !invalidEpoch && (!!retainedSend?.inFlight || !!submittedThinking || runActive);
  useEffect(() => {
    if (!submittedCompact) return;
    const next = invalidEpoch ? null : watchCompaction(submittedCompact, sessionId, page);
    if (next !== submittedCompact) setSubmittedCompact(next);
  }, [page, invalidEpoch, sessionId, submittedCompact]);
  // Presentation only, like the run above: the host decides what a compaction refuses.
  const compacting = !invalidEpoch && (!!pendingCompact?.inFlight || !!submittedCompact || hasPendingCompaction(sessionId, page));
  const thinkingSeconds = useThinkingElapsed(composerBusy);
  const runActivity = useRef(onRunActivity); runActivity.current = onRunActivity;
  useEffect(() => {
    if (!watching) return;
    runActivity.current?.(composerBusy);
    return () => runActivity.current?.(null);
  }, [composerBusy, watching, sessionId]);
  const mcpPlugin = configuration?.plugins.find(plugin => `${plugin.id} ${plugin.name}`.toLowerCase().includes("mcp"));
  const availableCompact = captureCompaction(epoch, sessionId, observedTarget, "availability");
  const availableAbortRun = captureAbortRun(epoch, sessionId, observedTarget, "availability");
  // Above the composer and never refused: a queued prompt waits for the session to be idle, a steering prompt
  // for the running turn. Only the composer's own text takes its images along.
  function stagePrompt(kind: "Queue" | "Steer", value = text, attached = value === latestText.current ? images : []) {
    if (pending || invalidEpoch) return false;
    if (!queue.composer.add(kind, epoch, sessionId, value, attached.length ? attached : null)) return false;
    if (value === latestText.current) {
      inputRevision.current++; clearText();
      if (attached.length) imageOwner.replace(imageKey, attached, []);
    }
    setSendFailure("");
    return true;
  }
  function retryLater() {
    holdUntil.current = Date.now() + 1200;
    window.setTimeout(() => setRetryTick(value => value + 1), 1300);
  }
  // The outcome of a queued prompt's Send. Its request does not belong to this panel: the row is settled even
  // when the tab was closed meanwhile.
  function settleQueued(id: string, key: string) {
    return (result: SubmissionResult) => {
      observeEpoch(result);
      const accepted = result.status === "accepted" || result.status === "replay";
      if (!accepted || result.receipt?.state === "terminal") setSubmittedThinking(current => current?.key === key ? null : current);
      if (accepted) { queue.composer.sent(id); void runtimeScope.current?.refresh(true); }
      else if (result.status === "uncertain") queue.composer.uncertain(id, "reason" in result && result.reason ? result.reason : "uncertain");
      // The session was working after all: the prompt keeps its place and leaves at the next idle moment.
      else if (result.status === "busy" || result.status === "capacity") { queue.composer.release(id); retryLater(); }
      else queue.composer.fail(id, result.status);
    };
  }
  function settleSteering(id: string) {
    return (result: SubmissionResult) => {
      observeEpoch(result);
      if (result.status === "accepted" || result.status === "replay") queue.composer.sent(id);
      else if (result.status === "uncertain") queue.composer.uncertain(id, "uncertain");
      else if (result.status === "busy") { queue.composer.release(id); retryLater(); }
      // A request the host no longer keeps the receipt of was delivered earlier: it is not sent a second time.
      else if (result.status === "closed" || result.status === "stale_epoch" || result.status === "expired") queue.composer.fail(id, result.status);
      // The turn cannot be steered: the prompt is sent as the next one instead.
      else queue.composer.requeue(id);
    };
  }
  function retryStaged(item: ComposerQueueItem) {
    if (invalidEpoch) return;
    if (item.state === "failed") { queue.composer.release(item.id); return; }
    if (item.state !== "uncertain" || !item.request) return;
    // Only the retained request itself is sent again: the same key, so the host cannot run it twice.
    if (item.kind === "Queue") {
      const retained = submissions.pending(sessionId);
      if (retained?.request.clientRequestId !== item.request.clientRequestId) queue.composer.sent(item.id);
      else if (!retained.inFlight && capability.canSubmit(retained.request))
        void submissions.submit(retained.request, queue.composer.signal, capability, settleQueued(item.id, retained.request.clientRequestId), "whenAccepted");
    } else {
      const retained = steering.pending(sessionId);
      if (retained?.request.clientRequestId !== item.request.clientRequestId) queue.composer.sent(item.id);
      else if (!retained.inFlight && capability.canSubmit(retained.request))
        void steering.submit(retained.request, queue.composer.signal, capability, settleSteering(item.id));
    }
  }
  const liveTexts = liveConnected ? currentLive.snapshot?.session?.text : undefined;
  useEffect(() => {
    if (invalidEpoch || runtimeState?.kind !== "ready") return;
    const target = runtimeState.snapshot;
    if (target.status !== "ok" || target.hostEpoch !== epoch) return;
    const entry = target.entry;
    const runId = entry?.activeRunId ?? null;
    const userRows = (liveTexts ?? []).filter(row => row.kind.toLowerCase() === "user");
    // A steering prompt leaves the list when its message shows in the timeline, or with the turn it was for.
    for (const item of staged) {
      if (item.kind !== "Steer" || item.state !== "delivering") continue;
      const baseline = steerBaseline.current.get(item.id);
      const taken = userRows.some(row => !baseline?.has(row.contentId) && queuePreview(row.text) === queuePreview(item.text));
      if (taken || runId !== (item.request as { expectedRunId?: string } | undefined)?.expectedRunId) {
        steerBaseline.current.delete(item.id);
        queue.composer.delivered(item.id);
      }
    }
    const idle = !composerBusy && !retainedSend && !runId && (!entry || target.coordinatorTransitionInProgress === false
      && !entry.queueDrainInProgress && !entry.isRetiring && !entry.pendingAgentPromptId);
    if (Date.now() < holdUntil.current) return;
    const steerItem = staged.find(item => item.kind === "Steer" && item.state === "waiting");
    if (steerItem && !steering.pending(sessionId) && !staged.some(item => item.kind === "Steer" && (item.state === "sending" || item.state === "uncertain"))) {
      const request = captureSteering(epoch, sessionId, target, steerItem.text, crypto.randomUUID());
      if (request && capability.canSubmit(request)) {
        const claimed = queue.composer.claim(steerItem, request);
        if (claimed) {
          steerBaseline.current.set(claimed.id, new Set(userRows.map(row => row.contentId)));
          void steering.submit(request, queue.composer.signal, capability, settleSteering(claimed.id));
        }
      // Nothing runs any more: the prompt is the next one sent.
      } else if (idle) queue.composer.requeue(steerItem.id);
      return;
    }
    const next = staged.find(item => item.kind === "Queue" && item.state === "waiting");
    if (!next || !idle || loadingChoices || queue.composer.isHeld(next)
      || staged.some(item => item.kind === "Queue" && (item.state === "sending" || item.state === "uncertain"))) return;
    // The prompt leaves as a Send of this moment: the selection, the project references and the images of one.
    const chosen = selections.current(epoch, sessionId) ?? selection;
    const usable = chosen && activeChoices && validSelection(activeChoices, chosen) ? chosen : null;
    const sendSelection = usable ?? (next.images ? activeChoices?.current ?? null : null);
    if (next.images && (!sendSelection || activeChoices?.models.find(model => model.id === sendSelection.modelId)?.imageInput !== true)) {
      queue.composer.fail(next.id, "image_unsupported"); return;
    }
    const references = referenceScope?.expectedEpoch === epoch && referenceScope.sessionId === sessionId
      ? { projectId: referenceScope.projectId, projectPath: referenceScope.projectPath } : null;
    const request = captureSubmission(epoch, sessionId, next.text, crypto.randomUUID(), sendSelection, references, next.images);
    if (!request) { queue.composer.fail(next.id, "invalid_request"); return; }
    if (!capability.canSubmit(request)) return;
    const claimed = queue.composer.claim(next, request);
    if (!claimed) return;
    promptHistory.add(sessionId, request.text);
    setSubmittedThinking({ key: request.clientRequestId, runId: null });
    void submissions.submit(request, queue.composer.signal, capability, settleQueued(claimed.id, request.clientRequestId), "whenAccepted");
  }, [stagedRevision, steeringRevision, runtimeState, invalidEpoch, !!retainedSend, composerBusy, epoch, sessionId, liveTexts, loadingChoices, retryTick, selection, choices]);
  function observeEpoch(result: { status: string; epoch: string | null }) {
    if (!capability.observe(result)) setInvalidEpoch(true);
  }
  function submit() {
    const signal = scope.current?.signal;
    sendDiagnostics.info("composer action", { mounted: !!signal, aborted: signal?.aborted ?? false,
      capabilityValid: capability.canMutate(), pending: !!submissions.pending(sessionId),
      inFlight: submissions.pending(sessionId)?.inFlight ?? false, enqueue, busy: composerBusy, hasImages: images.length > 0 });
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = ownPending();
    // While the session works, or behind prompts that already wait, the prompt joins the queue: it is never refused.
    if (!retained && (enqueue || composerBusy || !!submissions.pending(sessionId)
      || staged.some(item => item.kind === "Queue" && item.state !== "failed"))) { stagePrompt("Queue"); return; }
    if (retained?.inFlight) return;
    const latest = selections.current(epoch, sessionId);
    if (!retained && (latest || selection) && (!choices || choices.status !== "ok" || choices.epoch !== epoch || choices.sessionId !== sessionId
      || !validSelection(choices, latest ?? selection!) || latest && selection !== latest)) {
      sendDiagnostics.warn("blocked: next-send selection has not been validated");
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
    if (!request && images.length) setImageNotice("Image Send requires empty or nonblank text up to 32768 characters and an explicit supported model.");
    if (!request || !capability.canSubmit(request)) {
      sendDiagnostics.warn("blocked: invalid request or revoked capability"); return;
    }
    draftIndicators.clear(sessionId);
    promptHistory.add(sessionId, request.text);
    setSubmittedThinking({ key: request.clientRequestId, runId: null });
    setMessage("Ready to send to this owned session.");
    setSendFailure("");
    void submissions.submit(request, signal, capability, result => {
      observeEpoch(result);
      if (!["accepted", "replay"].includes(result.status) || result.receipt?.state === "terminal") setSubmittedThinking(null);
      const unchanged = (inputRevision.current === revision || latestText.current === request.text) && imageOwner.get(imageKey) === capturedImages;
      // The session started working between the keystroke and the host: the prompt joins the queue instead,
      // without a failed message in the timeline.
      if (result.status === "busy" && !retained && queue.composer.add("Queue", epoch, sessionId, request.text, request.images?.length ? request.images : null)) {
        submissions.acknowledgeOutgoing([outgoingKey(request)]);
        if (!signal.aborted && unchanged) { clearText(); imageOwner.replace(imageKey, capturedImages, []); }
        return;
      }
      const failure = result.status === "accepted" || result.status === "replay" ? null : sendFailureMessage(result.status, "reason" in result ? result.reason : undefined);
      setMessage(failure ?? "Ready to send to this owned session.");
      // The prompt stays in the composer and the status line says why: a prompt is never answered with a toast.
      setSendFailure(failure ?? "");
      if (result.status === "accepted" || result.status === "replay") void runtimeScope.current?.refresh(true);
      if ((!retained || !request.images?.length) && (result.status === "accepted" || result.status === "replay") && !signal.aborted
        // The composer still holds exactly what was sent (a choices refresh alone also bumps the revision).
        && (inputRevision.current === revision || latestText.current === request.text)
        && imageOwner.get(imageKey) === capturedImages) { clearText(); imageOwner.replace(imageKey, capturedImages, []); }
    });
  }
  // Commands addressed to the active session's composer (keyboard shortcuts and the command palette).
  useEffect(() => {
    if (!active) return;
    const run = (event: Event) => {
      const command = (event as CustomEvent<string>).detail;
      if (invalidEpoch) return;
      if (command === "send") submit();
      else if (command === "abort") { if (composerBusy || availableAbortRun || pendingAbortRun) abortRun(); }
      else if (command === "clearQueue") queue.composer.clear(epoch, sessionId);
      else if (command === "nextPrompt") {
        const prompts = activeChoices?.prompts ?? [];
        if (prompts.length > 1 && selected && !selectionDisabled)
          select("agentPromptId", prompts[(prompts.findIndex(prompt => prompt.id === selected.agentPromptId) + 1) % prompts.length].id);
      }
    };
    window.addEventListener("codealta:composer", run);
    return () => window.removeEventListener("codealta:composer", run);
  });
  // What a plugin asks of this composer: the shell names the session, or none for the focused pane.
  const [pluginSend, setPluginSend] = useState<string | null>(null);
  useEffect(() => {
    const run = (event: Event) => {
      const request = (event as CustomEvent<PluginComposerRequest>).detail;
      if (request.handled || (request.sessionId ? request.sessionId !== sessionId : !active)) return;
      request.handled = true;
      if (request.kind === "state") { request.state = { sessionId, draftText: latestText.current, busy: composerBusy }; request.result = true; return; }
      if (invalidEpoch || pending) return;
      const value = request.text ?? "";
      // A prompt that names its agent prompt goes with it or not at all: a plan is carried out by the default one.
      if (request.agentPromptId && (request.kind === "send" || request.kind === "enqueue") && selected?.agentPromptId !== request.agentPromptId) {
        if (selectionDisabled || !activeChoices?.prompts.some(prompt => prompt.id === request.agentPromptId)) return;
        select("agentPromptId", request.agentPromptId);
      }
      if (request.kind === "draft") { editText(value); request.result = true; }
      else if (request.kind === "enqueue") request.result = stagePrompt("Queue", value, []);
      else if (request.kind === "steer" && composerBusy) request.result = stagePrompt("Steer", value, []);
      else if (request.kind === "compact") { if (!composerBusy && capability.canMutate()) { compact(); request.result = true; } }
      // Steering an idle session is a Send, as it is from the keyboard.
      else if ((request.kind === "send" || request.kind === "steer") && value.trim() && capability.canMutate()) {
        // The prompt goes through this composer's own Send, once the editor holds it.
        editText(value); setPluginSend(value); request.result = true;
      }
    };
    window.addEventListener(pluginComposerEvent, run);
    return () => window.removeEventListener(pluginComposerEvent, run);
  });
  useEffect(() => {
    if (pluginSend === null) return;
    if (text !== pluginSend) { setPluginSend(null); return; } // The user changed the text: it is theirs to send.
    if (pending || invalidEpoch || !scope.current || scope.current.signal.aborted) return;
    setPluginSend(null);
    submit();
  }, [pluginSend, text, pending, invalidEpoch]);
  // One shot per mounted composer: the parent's marker is a ref, so this prop outlives its consumption.
  const autoSent = useRef(false);
  useEffect(() => {
    if (!autoSend || autoSent.current || text !== autoSend.text || pending || invalidEpoch || !scope.current || scope.current.signal.aborted
      || !choices || choices.status !== "ok" || choices.epoch !== epoch || choices.sessionId !== sessionId) return;
    const latest = selections.current(epoch, sessionId);
    if ((latest || selection) && (!validSelection(choices, latest ?? selection!) || latest && selection !== latest)) return;
    autoSent.current = true;
    autoSend.consume();
    submit();
  }, [autoSend, text, choices, selection, pending, invalidEpoch]);
  async function refresh(offset = 0) {
    const signal = scope.current?.signal;
    if (!signal) return;
    const revision = ++receiptRevision.current;
    const input = inputRevision.current;
    const originalText = submissions.pending(sessionId)?.request.text;
    await refreshSubmissions(sessions.receipts, epoch, offset, signal, result => {
      if (revision !== receiptRevision.current) return;
      observeEpoch(result);
      setReceiptUnavailable(result.status !== "ok");
      if (result.status !== "ok") return; // Keep the last receipts; a missed read is not settlement.
      setPage(result);
      queue.composer.reconcile(result);
      const recoveringImages = !!submissions.pending(sessionId)?.request.images?.length;
      const recovered = submissions.reconcile(sessionId, result, capability);
      // Receipt recovery cannot prove a remounted image draft's original input revision.
      if (recovered.sendRecovered && !signal.aborted && !recoveringImages && inputRevision.current === input && latestText.current === originalText) clearText();
      if (recovered.sendRecovered || recovered.abortsRecovered > 0)
        setMessage("Ready to send to this owned session.");
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
  // Ctrl+Enter. With a turn running the prompt steers it; without one there is nothing to steer and it is sent.
  // An empty prompt steers with the first prompt of the queue, as the terminal UI does.
  function steerFromComposer() {
    if (pending || invalidEpoch) return;
    if (!latestText.current.trim() && !images.length) {
      const next = staged.find(item => item.kind === "Queue" && item.state === "waiting" && !queue.composer.isHeld(item));
      if (next && composerBusy) queue.composer.steerNow(next);
      return;
    }
    if (!composerBusy) { submit(); return; }
    // Steering carries text only: a prompt with images waits for the next turn.
    stagePrompt(images.length ? "Queue" : "Steer");
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
    const key = request.clientRequestId;
    setSubmittedCompact({ key, listed: false });
    void compaction.submit(request, signal, capability, result => {
      observeEpoch(result);
      // An admission that is not known leaves the receipts to say whether a compaction is going on.
      if (result.status !== "accepted" && result.status !== "replay" || result.receipt?.state === "terminal")
        setSubmittedCompact(current => current?.key === key ? null : current);
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
  const selectionDisabled = loadingChoices || !activeChoices?.current || invalidEpoch || !!pending;
  function select(field: "agentPromptId" | "modelId" | "reasoningEffort", value: string) {
    if (!activeChoices || !selected || selectionDisabled) return;
    const next = changeSelection(activeChoices, selected, field, value);
    if (!next) { setChoicesNotice("Choose an available model and prompt before sending with changed settings."); return; }
    if (!selections.set(epoch, sessionId, activeChoices, next)) return;
    setSelection(next);
    setChoicesNotice("");
  }
  const efforts = activeChoices?.models.find(m => m.id === selected?.modelId)?.efforts ?? null;
  const imageCapability = activeChoices?.models.find(m => m.id === selected?.modelId)?.imageInput;
  async function pasteImages(event: ClipboardEvent<HTMLElement>) {
    if (!event.clipboardData.files.length) return;
    event.preventDefault();
    const revision = ++inputRevision.current;
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
      const added = [];
      for (const file of files) { added.push(await readPastedImage(file, `Image ${original.length + added.length + 1}`)); if (!current()) return; }
      if (!imageOwner.replace(imageKey, original, [...original, ...added])) {
        setImageNotice("Image draft capacity reached (8 image-bearing drafts). Remove attachments from another draft first."); return;
      }
      setImageNotice("");
    } catch { if (current()) setImageNotice(t(imagePasteFailure)); }
    finally { finish(); }
  }
  function canEditImages() {
    return !!scope.current && !scope.current.signal.aborted && !submissions.pending(sessionId)
      && capability.canMutate() && (inputLifetime?.current() ?? true) && imageOwner.get(imageKey) === images;
  }
  const attachmentStrip = <PromptImageAttachments images={pending?.request.images ?? images}
    disabled={!!pending || invalidEpoch} notice={imageNotice}
    rename={(index, title) => {
      inputRevision.current++;
      if (!canEditImages()) return;
      if (!imageOwner.replace(imageKey, images, images.map((item, i) => i === index ? { ...item, title } : item)))
        setImageNotice(t("Image titles require 1–80 characters and no control characters."));
      else setImageNotice("");
    }} remove={index => {
      inputRevision.current++;
      if (canEditImages() && imageOwner.replace(imageKey, images, images.filter((_, i) => i !== index))) setImageNotice("");
    }} />;
  return <>
    <ComposerQueueStrip owner={queue.composer} epoch={epoch} sessionId={sessionId} disabled={invalidEpoch} running={composerBusy} retry={retryStaged} />
    {pendingSteer && !staged.some(item => item.request?.clientRequestId === pendingSteer.request.clientRequestId) &&
      <div className="composer-queue-row"><AppIcon name="steer" size={15} /><span className="composer-queue-preview">{pendingSteer.request.text}</span>
        <Button icon={<AppIcon name="refresh" size={14} />} aria-label={t("Retry exact request")} disabled={invalidEpoch || pendingSteer.inFlight} onClick={() => steer()} /></div>}
    {pendingQueue &&
      <div className="composer-queue-row"><AppIcon name="queue" size={15} /><span className="composer-queue-preview">{pendingQueue.request.text}</span>
        <Button icon={<AppIcon name="refresh" size={14} />} aria-label={t("Retry exact request")} disabled={invalidEpoch || pendingQueue.inFlight} onClick={() => queueTextInHost(false)} /></div>}
    {!expanded && attachmentStrip}
    <ComposerSurface busy={composerBusy || compacting} status={<>
      {composerBusy || compacting ? <ActivitySpinner size={14} /> : <AppIcon name={invalidEpoch || sendFailure ? "error" : "prompt"} size={14} />}
      {composerBusy ? thinkingSeconds > 0 ? t("Thinking for {elapsed}...", { elapsed: formatThinkingElapsed(thinkingSeconds) }) : t("Thinking…")
        : compacting ? t("Compacting…")
        : !invalidEpoch && sendFailure ? sendFailure : t(invalidEpoch ? "Reload required." : pending ? "Exact-request waiter pending" : currentLive && !liveConnected ? "Run status unavailable" : draft.editGeneration !== null ? "Draft edited..." : "Prompt ready")}</>}
    expandedEditor={expanded && !pending && !invalidEpoch && <ExpandedPromptEditor text={text} onChange={editText} onPaste={pasteImages} onCompositionStart={() => { inputRevision.current++; }} attachments={attachmentStrip} onClose={() => { inputRevision.current++; setExpanded(false); }} />}
    editor={{ id: active ? "session-prompt" : `session-prompt-${sessionId}`, ref: promptInput, onPaste: pasteImages, label: t("Message"), value: pending?.request.text ?? text, disabled: !!pending || invalidEpoch || expanded,
      onChange: editText, onCompositionStart: () => { inputRevision.current++; }, placeholder: t("Ask CodeAlta to work on this project…"), onKeyDown: event => {
        if (event.altKey && !event.ctrlKey && !event.shiftKey && !event.metaKey && (event.key === "ArrowUp" || event.key === "ArrowDown")
          && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) {
          event.preventDefault(); event.stopPropagation();
          recallSentPrompt(event.key === "ArrowUp" ? -1 : 1);
          return;
        }
        if (dispatchTransientComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        promptInput.current!, onOpenHelp, onOpenCommands)) { event.preventDefault(); event.stopPropagation(); return; }
        if (dispatchComposerKey({ key: event.key, ctrlKey: event.ctrlKey,
          shiftKey: event.shiftKey, altKey: event.altKey, metaKey: event.metaKey,
          isComposing: event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode,
           repeat: event.repeat, defaultPrevented: event.defaultPrevented }, submit, steerFromComposer)) { event.preventDefault(); event.stopPropagation(); }
      } }}
    options={<ComposerSelectionFields sessionId={sessionId} onOpenCatalog={onOpenCatalog} locked={providerBusy}
      summary={{ agent: activeChoices?.prompts.find(p => p.id === selected?.agentPromptId)?.name ?? selected?.agentPromptId ?? "…",
        provider: selected?.providerKey ?? t("session provider"),
        model: selected?.modelId ? activeChoices?.models.find(m => m.id === selected.modelId)?.name ?? selected.modelId : t(loadingChoices ? "Loading…" : "No model"),
        reasoning: selected?.reasoningEffort ?? (loadingChoices ? t("Loading…") : null) }}
      agent={<HTMLSelect fill id={`composer-agent-${sessionId}`} aria-label={t("Agent prompt")} value={selected?.agentPromptId ?? ""} disabled={selectionDisabled} onChange={event => select("agentPromptId", event.target.value)} title={t("Agent prompt for the next Send")}>
        {!activeChoices?.prompts.some(p => p.id === selected?.agentPromptId) && <option value={selected?.agentPromptId ?? ""}>{selected?.agentPromptId ?? t("Loading…")}</option>}
        {activeChoices?.prompts.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
      </HTMLSelect>}
      provider={<ProviderChooser epoch={epoch} sessionId={sessionId} providerKey={selected?.providerKey ?? t("session provider")}
        disabled={selectionDisabled || !capability.canMutate() || runtimeState?.kind !== "ready"
          || runtimeState.snapshot.coordinatorTransitionInProgress || !!runtimeState.snapshot.entry?.activeRunId
          || !!runtimeState.snapshot.entry?.queueDrainInProgress || !!runtimeState.snapshot.entry?.isRetiring}
        current={() => capability.canMutate() && !pending && !scope.current?.signal.aborted}
        onBusyChange={setProviderBusy}
        onSelected={async () => {
          const signal = scope.current?.signal;
          const value = await sessions.choices({ expectedEpoch: epoch, sessionId }, { signal });
          if (signal?.aborted || !capability.canMutate() || value.epoch !== epoch || value.sessionId !== sessionId || value.status !== "ok" || !value.current) return;
          const next = { ...value.current, agentPromptId: selected?.agentPromptId ?? value.current.agentPromptId };
          if (!selections.set(epoch, sessionId, value, next)) selections.set(epoch, sessionId, value, value.current);
          setChoices(value); setChoicesNotice("");
          loadModelChoices();
          void runtimeScope.current?.refresh();
        }} />}
      model={<HTMLSelect fill id={`composer-model-${sessionId}`} data-model-selector aria-label={t("Model")} value={selected?.modelId ?? ""} disabled={invalidEpoch || !!pending || loadingChoices}
        onChange={event => select("modelId", event.target.value)} title={selected?.modelId && !activeChoices?.models.some(m => m.id === selected.modelId) ? t("Saved selection; not verified by this host's observed model catalog.") : t("Model for the next Send · {provider}", { provider: selected?.providerKey ?? t("session provider") })}>
        {!selected?.modelId && <option value="">{t(loadingChoices ? "Loading…" : "No model")}</option>}
        {selected?.modelId && !activeChoices?.models.some(m => m.id === selected.modelId) && <option value={selected.modelId}>{loadingChoices ? t("Loading…") : `${selected.modelId} · ${t("Unverified")}`}</option>}
        {activeChoices?.models.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
      </HTMLSelect>}
      reasoning={<ReasoningSlider value={selected?.reasoningEffort ?? null} efforts={efforts} disabled={selectionDisabled}
        onChange={value => select("reasoningEffort", value)} />} />}>
      {!pending && !expanded && !invalidEpoch && <ProjectReferencePicker text={text} edit={editText} input={promptInput} />}
      {!pending && !expanded && !invalidEpoch && <IssuePicker edit={editText} input={promptInput} />}
      {!pending && !expanded && !invalidEpoch && <PluginPromptPickers edit={editText} input={promptInput} sessionId={sessionId} />}
      <ActiveProviderStatus epoch={epoch} onOpen={() => onOpenCatalog?.("providers")} />
      {(!activeChoices?.models.length || choicesNotice.includes("could not")) && <Button variant="minimal" icon={<AppIcon name="refresh" size={16} />}
        disabled={invalidEpoch || !!pending || loadingChoices} aria-label={t("Refresh composer choices")} title={t("Refresh composer choices")} onClick={loadModelChoices} />}
      <ObservationStatus unavailable={!invalidEpoch && (receiptUnavailable || runtimeState?.kind === "error")} />
      <span className="sr-only">{t("Enter to send · Shift+Enter for a new line · Ctrl+Enter to steer")}</span>
      {infoControl}
      {usageTarget && <SessionUsageInspector key={JSON.stringify(usageTarget)} target={usageTarget} capability={capability} persisted={persistedUsage}
        provider={selected?.providerKey ?? observedProvider ?? null} model={selected?.modelId ? activeChoices?.models.find(m => m.id === selected.modelId)?.name ?? selected.modelId : null}
        refreshKey={`${observing}:${composerBusy}:${compacting}:${runtimeState?.kind === "ready" ? runtimeState.snapshot.entry?.activeRunId ?? "" : ""}:${liveState?.snapshot?.revision ?? ""}`} />}
      {onOpenReminders && <Button ref={remindersTrigger} variant="minimal" icon={<AppIcon name="reminder" size={16} />} data-reminder-count=""
        disabled={invalidEpoch} aria-label={reminderLabel} title={`${reminderLabel} (Ctrl+G, Ctrl+D)`}
        onClick={onOpenReminders}><span className="reminder-count" aria-hidden="true">{observedReminderCount ?? "?"}</span></Button>}
      <Button id={active ? "expand-session-prompt" : `expand-session-prompt-${sessionId}`} variant="minimal" icon={<AppIcon name="expand" size={16} />} disabled={!!pending || invalidEpoch} aria-label={t("Expand prompt editor")} title={t("Edit prompt in a large window (F6)")} onClick={() => { inputRevision.current++; setExpanded(true); }} />
      {composerBusy && <Button variant="minimal" onClick={steerFromComposer}
        disabled={invalidEpoch || !!pending || !text.trim() && !staged.some(item => item.kind === "Queue" && item.state === "waiting")}
        aria-label={t("Steer the running turn")}
        title={t("Steer the running turn with this prompt (Ctrl+Enter)")}>
        <AppIcon name="steer" size={16} /></Button>}
      {composerBusy && <Button variant="minimal" onClick={() => stagePrompt("Queue")}
        disabled={invalidEpoch || !!pending || !text.trim() && images.length === 0}
        aria-label={t("Enqueue for the next turn")}
        title={t("Enqueue this prompt for the next turn (Enter)")}>
        <AppIcon name="queue" size={16} /></Button>}
      <Button ref={compactTrigger} variant="minimal" onClick={compact}
        data-epoch={epoch} data-session-id={sessionId} data-project-id={projectId ?? ""}
        disabled={invalidEpoch || compacting || (pendingCompact
          ? !capability.canSubmit(pendingCompact.request) : !availableCompact || !capability.canSubmit(availableCompact))}
        aria-label={pendingCompact ? t("Retry exact compaction request for attachment {attachment}", { attachment: pendingCompact.request.expectedAttachmentGeneration }) : t("Compact observed idle attachment")}
        title={pendingCompact ? `${t("Manual retry of exact compaction:")} ${t("epoch")} ${pendingCompact.request.expectedEpoch}, ${t("session")} ${pendingCompact.request.sessionId}, ${t("runtime")} ${pendingCompact.request.expectedRuntimeInstanceId}, ${t("attachment")} ${pendingCompact.request.expectedAttachmentGeneration}, ${t("request")} ${pendingCompact.request.clientRequestId}`
          : t("Compact the conversation (Ctrl+F11)")}>
        <AppIcon name="compact" size={16} /></Button>
      <SendSplitButton enqueue={enqueue} onEnqueueChange={setEnqueue} optionsDisabled={invalidEpoch || !!pending}>
      {(composerBusy || availableAbortRun || pendingAbortRun) ? <Button intent="danger" icon={<AppIcon name="stop" size={16} fill="currentColor" />} onClick={abortRun}
        disabled={invalidEpoch || !!pendingAbortRun?.inFlight || (pendingAbortRun
          ? !capability.canSubmit(pendingAbortRun.request) : !availableAbortRun || !capability.canSubmit(availableAbortRun))}
        aria-label={pendingAbortRun ? t("Retry exact cancellation request for observed run {run}", { run: pendingAbortRun.request.expectedRunId }) : t("Cancel observed run")}
        title={pendingAbortRun ? `${t("Manual retry of exact cancellation:")} ${t("epoch")} ${pendingAbortRun.request.expectedEpoch}, ${t("session")} ${pendingAbortRun.request.sessionId}, ${t("runtime")} ${pendingAbortRun.request.expectedRuntimeInstanceId}, ${t("attachment")} ${pendingAbortRun.request.expectedAttachmentGeneration}, ${t("run")} ${pendingAbortRun.request.expectedRunId}, ${t("request")} ${pendingAbortRun.request.clientRequestId}`
          : t("Stop the running turn")} />
      : <Button aria-label={t(pending ? "Retry exact request" : enqueue ? "Enqueue" : "Send")} title={t(pending ? "Retry exact request" : enqueue ? "Enqueue until idle" : "Send")} intent="primary" icon={<AppIcon name={pending ? "refresh" : enqueue ? "queue" : "send"} size={16} />} disabled={invalidEpoch || !!pending?.inFlight || (pending ? !capability.canSubmit(pending.request) : (images.length > 0 && (imageCapability !== true || !activeChoices || !selected || !validSelection(activeChoices, selected))) || captureSubmission(epoch, sessionId, text, "availability", images.length ? selected : null, null, images) === null)} onClick={submit} />}
      </SendSplitButton>
    </ComposerSurface>
    {timelineNotices && createPortal(<>
    {pendingQueueCancellations.map(value => <Button key={value.intent.request.clientRequestId} icon={<AppIcon name="refresh" size={14} />}
      disabled={invalidEpoch || value.inFlight || !capability.canSubmit(value.intent.request)} onClick={() => cancelQueued(undefined, value.intent.request.targetOperationId)}>
      {t("Retry exact queued-operation cancellation")}</Button>)}
    {permissionReviewer && <CommandPermissionPanel reviewer={permissionReviewer} epoch={epoch} sessionId={sessionId}
      canReview={() => capability.canMutate() && (inputLifetime?.current() ?? true)} />}
    {pendingAborts.length > 0 && <div className="retained-send-recovery">
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
    {pendingAborts.map(value => <div key={value.intent.request.targetOperationId}>
      <p className="detail">{t("Retained Abort: original session")} {value.intent.sessionId} · {t("operation")} {value.intent.request.targetOperationId} · {t("request")} {value.intent.request.clientRequestId}</p>
      <button type="button" disabled={invalidEpoch || value.inFlight || !capability.canSubmit(value.intent.request)}
        onClick={() => abort(undefined, value.intent.request.targetOperationId)}>{t("Retry exact original Send Abort")}</button>
    </div>)}
    </div>}
    </>, timelineNotices)}
  </>;
}
