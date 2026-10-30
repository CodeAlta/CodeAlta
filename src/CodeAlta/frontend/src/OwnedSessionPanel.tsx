import { Button, HTMLSelect } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { createPortal } from "react-dom";
import { AttachMenu, AttachMenuItems, PromptImageAttachments } from "./PromptImageAttachments";
import { pickFolder } from "./folderPicker";
import { folderLink } from "./projectReferences";
import { insertIssueReferences } from "./issueReferences";
import { formatThinkingElapsed, useThinkingElapsed } from "./thinkingElapsed";
import { BackgroundJobDialog } from "./BackgroundJobDialog";
import { BackgroundTasksStatus } from "./BackgroundTaskViews";
import { backgroundTasks, sameBackgroundTasks, type BackgroundTask } from "./backgroundTasks";
import type { ToolOutputs } from "./toolOutput";
import type { DisplayState } from "./sessionDisplay";
import { useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode, type Ref } from "react";
import { ProjectReferenceContext, ProjectReferencePicker } from "./ProjectReferencePicker";
import { IssuePicker } from "./IssuePicker";
import { PluginPromptPickers } from "./PluginPromptPicker";
import { pluginComposerEvent, type PluginComposerRequest } from "./pluginUi";
import { desktopShell, markdownLinks, modelCatalog, toolCalls, sessionOperations as sessions, sessionPermissions, sandboxSettings as sandboxSessions, type ConfigurationSnapshot, type SessionReceiptPage, type SessionReceiptView, type SessionSteerRequest, type SessionChoicesResponse, type SessionSelection, type ReminderListRequest, type ReminderListResponse, type SessionOptionsChoice } from "#neoastra";
import { SessionOptionsFields } from "./sessionSettings/SessionOptionsFields";
import { changedSessionOptions, sessionContextSize, sessionOptionsOf } from "./sessionSettings/sessionOptions";
import { activateSessionModels } from "./activateSessionModels";
import { captureSubmission, captureSubmissionAbort, createMutationCapability, outgoingKey, pluginPromptOutcome, refreshSubmissions, sendAvailable, type PluginPromptOutcome, type SubmissionResult, type createOwnedSubmissions } from "./sessionOperations";
import { captureSteering, type createSteeringSubmissions } from "./sessionSteering";
import { captureCompaction, compactControlBlock, compactControlBlockMessages, compactionAdmissionFailure, hasPendingCompaction, watchCompaction, watchedCompactionFailure,
  type CompactionFailure, type CompactionWatch, type createCompactionSubmissions } from "./sessionCompaction";
import { abortRunAdmissionFailure, captureAbortRun, watchedStopFailure, type createAbortRunSubmissions, type StopFailure } from "./sessionAbortRun";
import type { createRuntimeStateReader, RuntimeState } from "./runtimeState";
import type { createPermissionReviewer } from "./sessionPermissions";
import { CommandPermissionPanel, permissionSkillReader } from "./CommandPermissionPanel";
import { ConversationHeldPanel } from "./conversationHeld/ConversationHeldPanel";
import type { createConversationHeldReviewer } from "./conversationHeld/conversationHeld";
import { WaitingSubSessionsPanel } from "./WaitingSubSessionsPanel";
import { HostReadsContext } from "./hostReadsContext";
import type { WaitingSubSession } from "./spaces/spaces";
import { RemoteControlButton } from "./RemoteControlButton";
import { MicButton } from "./speech/MicButton";
import { SpeechContext } from "./speech/speech";
import { entryRemoteControl, remoteControlView, sameRemoteControl, type RemoteControlOpenRequest, type RemoteControlView } from "./remoteControl";
import { entryPromptSuggestion, shownPromptSuggestion, takesPromptSuggestion } from "./promptSuggestion";
import { createDraftIndicators, persistDraft, restoreDraft } from "./promptDraft";
import { AppIcon } from "./AppIcon";
import { equationKey, type PromptInput } from "./PromptEditor";
import { createPromptHistory, noPromptRecall, recallPrompt } from "./promptHistory";
import { changeSelection, validSelection } from "./sessionSelection";
import { ProviderChooser } from "./ProviderChooser";
import { useProviderName } from "./ProviderIcon";
import type { createNextSendSelectionStore } from "./nextSendSelection";
import { ComposerModelPicker } from "./ComposerModelPicker";
import { composerTierEntries, tierLabels, useTiers, type TierName } from "./modelTiers";
import { ComposerSurface, ComposerSelectionFields, ModelOptionFields, ReasoningSlider, SendSplitButton } from "./ComposerSurface";
import { showToast } from "./appToaster";
import { PermissionModeSelect } from "./PermissionModeSelect";
import { ReviewBeforeCommitSelect } from "./ReviewBeforeCommitSelect";
import { followScopes, nextSessionSandbox, sessionSandboxSummary, type SessionSandboxState } from "./sandbox/sandboxChoices";
import { SessionSandboxSelect } from "./sandbox/SessionSandboxSelect";
import { chosenPermissionMode, offeredPermissionModes, permissionModeName } from "./permissionModes";
import { dispatchComposerKey, dispatchTransientComposerKey } from "./composerKeyboard";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";
import type { createReminderActions } from "./reminderActions";
import { validReminderList } from "./reminderListObservation";
import { SessionUsageInspector } from "./SessionUsageInspector";
import { ComposerQueueStrip } from "./ComposerQueueStrip";
import { reasoningShown } from "./promptSurface/promptSurface";
import { ClaudeCliFirstUse } from "./claudeCli/ClaudeCliFirstUse";
import { queuePreview, type ComposerQueueItem, type createComposerQueue } from "./composerQueue";
import { ActiveProviderStatus, type ActiveProviderState } from "./ActiveProviderStatus";
import { ObservationStatus } from "./ObservationStatus";
import type { UsageTarget } from "./sessionUsage";
import { AttachmentError, composerImageKey, attachmentBytes, attachmentLimits, imagePasteFailure, isImageAttachment, isImageFile, linkAttachment, readAttachedFile } from "./promptImages";
import { useShellLanguage } from "./shellLanguage";
import type { ClipboardEvent } from "react";
import { sendDiagnostics } from "./sendDiagnostics";
import { revealBackgroundTask } from "./timelineReveal";
import { OpenRunContext } from "./workflows/openRun";

// Sent prompts of this window, for Alt+Up / Alt+Down in the prompt editor.
const promptHistory = createPromptHistory();
// What the model list holds while the choices of the session are not known.
const noModels: readonly { id: string; name: string }[] = [];

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

const noWaitingSubSessions: readonly WaitingSubSession[] = [];

export function OwnedSessionPanel({ canAdmit = () => true, onRunActivity, toolOutputs, sessionId, epoch, projectId = null, usageTarget, persistedUsage = null, infoControl, submissions, steering, compaction, abortRuns, queue, capability, runtimeReader, permissionReviewer, heldReviewer = null, configuration, draftIndicators, selections, remindersTrigger, compactTrigger, onOpenReminders, onOpenHelp, onOpenCommands, reminderActions, readReminderCount, activeReminderCount = null, archive = null, autoSend = null, inputLifetime, liveState, timelineNotices, onOpenCatalog, onOpenDocumentation, active = true, observing = true, remoteControlRequest = null, waitingSubSessions = noWaitingSubSessions, createSubSessionReviewer = null, sessionFolder = null }: {
  /** The folder the session works in: the folder dialog of the attach menu opens there, and a folder chosen in it is a project reference. */
  sessionFolder?: string | null;
  /** Live retention claim, checked by programmatic admissions as well as the shell's inert DOM boundary. */
  canAdmit?: () => boolean;
  active?: boolean;
  /** The request of the Actions menu of the session to show its Remote Control, until it is shown. */
  remoteControlRequest?: RemoteControlOpenRequest | null;
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
  /** Reads and answers the question of a run whose Claude Code conversation another process holds. */
  heldReviewer?: ReturnType<typeof createConversationHeldReviewer> | null;
  /** The sessions under this one that wait for the user: named above the composer, their permissions answered there. */
  waitingSubSessions?: readonly WaitingSubSession[];
  /** Makes a reviewer for the permissions of a session under this one; null when the host reviews no command. */
  createSubSessionReviewer?: (() => ReturnType<typeof createPermissionReviewer>) | null;
  steering: ReturnType<typeof createSteeringSubmissions>;
  compaction: ReturnType<typeof createCompactionSubmissions>;
  abortRuns: ReturnType<typeof createAbortRunSubmissions>;
  queue: ReturnType<typeof createComposerQueue>;
  draftIndicators: ReturnType<typeof createDraftIndicators>;
  selections: ReturnType<typeof createNextSendSelectionStore>;
  configuration?: ConfigurationSnapshot;
  remindersTrigger?: Ref<HTMLButtonElement>;
  compactTrigger?: Ref<HTMLButtonElement>;
  /** Reports whether this session is working while the panel watches it, and null once it no longer does. */
  /** What the panel sees of the run of the session and of the tasks of its provider; null when it stops watching. */
  onRunActivity?: (running: boolean | null, background?: readonly BackgroundTask[]) => void;
  /** The live output of the session, for what its background jobs write. */
  toolOutputs?: ToolOutputs;
  onOpenReminders?: () => void;
  /** A prompt from the New session tab: sent once, when this composer holds exactly that text and its choices are validated. */
  /** A first message to send once the composer holds it: its text, and how many images were pasted with it. */
  autoSend?: { text: string; images?: number; consume: () => void } | null;
  /** Active reminders of this session as last reported by the host for the explorer markers; null while unknown. */
  activeReminderCount?: number | null;
  /**
   * Archives the session, as its menu in the Explorer does; `asks` when it has child sessions, and `question` is that
   * question while it is asked from here, shown above the `⋯` menu. Null where it cannot be archived.
   */
  archive?: Readonly<{ asks: boolean; run: () => void; question?: ReactNode }> | null;
  onOpenHelp?: () => void;
  onOpenCommands?: () => void;
  onOpenCatalog?: (page: "models" | "prompts" | "providers") => void;
  /** Opens a page of the user guide at a heading. */
  onOpenDocumentation?: (page: string, anchor: string | null) => void;
  reminderActions?: ReturnType<typeof createReminderActions>;
  readReminderCount?: (request: ReminderListRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ReminderListResponse>;
}) {
  const [draft, setDraft] = useState(() => ({ text: restoreDraft(key => localStorage.getItem(key), sessionId), editGeneration: null as number | null }));
  const text = draft.text;
  const inputRevision = useRef(0);
  const { t } = useShellLanguage();
  const imageOwner = submissions.imageDrafts;
  useSyncExternalStore(imageOwner.subscribe, imageOwner.getSnapshot);
  const imageKey = composerImageKey(epoch, sessionId, projectId, usageTarget?.expectedProjectPath ?? null);
  const images = imageOwner.get(imageKey);
  // The attachments are images and files; only an image needs a model that reads images.
  const hasImages = images.some(isImageAttachment);
  const [imageNotice, setImageNotice] = useState("");
  // Each change opens the issue picker at the caret: Add issues of the attach menu.
  const [issueRequest, setIssueRequest] = useState(0);
  const [providerState, setProviderState] = useState<ActiveProviderState>({ ready: 0, errors: 0, detecting: true });
  const latestText = useRef(text);
  const restoredText = useRef(text);
  useLayoutEffect(() => { draftIndicators.clear(sessionId); }, [draftIndicators, sessionId, epoch]);
  function editText(value: string) {
    if (!canAdmit() || !scope.current || scope.current.signal.aborted) return;
    setSendFailure("");
    setPluginOutcome(null);
    recall.current = noPromptRecall;
    inputRevision.current++;
    latestText.current = value;
    const editGeneration = draftIndicators.edit(sessionId, value, restoredText.current);
    setDraft({ text: value, editGeneration });
  }
  const recall = useRef(noPromptRecall);
  function recallSentPrompt(direction: -1 | 1) {
    if (!canAdmit() || !scope.current || scope.current.signal.aborted) return false;
    const next = recallPrompt(promptHistory.list(sessionId), recall.current, direction, latestText.current);
    if (!next) return false;
    const state = next.state;
    editText(next.text);
    recall.current = state;
    return true;
  }
  function clearText() {
    if (!canAdmit() || !scope.current || scope.current.signal.aborted) return;
    latestText.current = "";
    restoredText.current = "";
    draftIndicators.clear(sessionId);
    setDraft({ text: "", editGeneration: null });
  }
  const promptInput = useRef<PromptInput>(null);
  // The dictation of the window. A send asked while this composer dictates waits for the last words: the dictation
  // is stopped, they are written in the prompt, and the send that was asked is then done by the composer as it is by
  // then (the dictated text changed the prompt, and `inputRevision` with it).
  const speech = useContext(SpeechContext);
  const speechOwner = `session:${sessionId}`;
  type SendActions = { submit: (queuePrompt?: boolean) => void; stagePrompt: (kind: "Queue" | "Steer") => boolean; steerFromComposer: () => void };
  const sendActions = useRef<SendActions | null>(null);
  const sendWaits = useRef(false);
  function afterDictation(next: (actions: SendActions) => void): boolean {
    const written = speech?.finishForSend(speechOwner);
    if (!written) return false;
    // One send at a time waits: a second one asked meanwhile is the same send.
    if (!sendWaits.current) {
      sendWaits.current = true;
      void written.then(() => { sendWaits.current = false; if (sendActions.current) next(sendActions.current); });
    }
    return true;
  }
  const referenceScope = useContext(ProjectReferenceContext);
  const [expanded, setExpanded] = useState(false);
  const [enqueue, setEnqueue] = useState(false);
  const [providerBusy, setProviderBusy] = useState(false);
  const [receiptUnavailable, setReceiptUnavailable] = useState(false);
  const stagedRevision = useSyncExternalStore(queue.subscribe, queue.getSnapshot);
  const staged = queue.list(epoch, sessionId);
  // A session with prompts waiting is followed even while its tab is hidden: they leave when it is idle.
  const watching = observing || staged.length > 0;
  // The receipts are those of the window: the panes that read them at the same time share one read.
  const hostReads = useContext(HostReadsContext);
  // What a Send that was not taken says, in the status line: a prompt is never answered with a toast.
  const [sendFailure, setSendFailure] = useState("");
  // A Send or a steering prompt a plugin answered itself (handled or cancelled): nothing reached the agent, and the status
  // line says so, also while the turn it was steered into goes on (runId).
  const [pluginOutcome, setPluginOutcome] = useState<(PluginPromptOutcome & { runId: string | null }) | null>(null);
  // Asks the form of the session configuration to open: a send that needs another provider opens it.
  const [selectionOpenRequest, setSelectionOpenRequest] = useState(0);
  const [retryTick, setRetryTick] = useState(0);
  const holdUntil = useRef(0);
  // The user messages on screen when a steering prompt left: a later one with its text is the agent taking it up.
  const steerBaseline = useRef(new Map<string, Set<string>>());
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
    setChoicesNotice("The agent, the model and the effort apply on Send; the permission mode applies at once, also to a running turn.");
  }), [epoch, sessionId, capability, selections]);
  // A pane out of sight reads them too while prompts wait above it: a queued prompt goes with the selection it was
  // queued with, which is checked against them before it is sent.
  useEffect(() => {
    if (!watching) { setLoadingChoices(false); return; }
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
        setChoicesNotice("The agent, the model and the effort apply on Send; the permission mode applies at once, also to a running turn.");
      }).catch(() => { if (!controller.signal.aborted && revision === selectionRevision.current) setChoicesNotice("Session choices could not be loaded. Reopen the Model selector to try again."); })
      .finally(() => { if (!controller.signal.aborted) setLoadingChoices(false); });
    return () => controller.abort();
  }, [epoch, sessionId, capability, selections, choicesRevision, watching]);
  // A session shown in the window shows the turns made outside CodeAlta (in VS Code, in a terminal) on the conversation
  // of its provider: the host adds them to its journal, which the timeline then reads. Nothing is read again when
  // nothing changed, and the next message adds them all the same.
  useEffect(() => {
    if (!observing || !capability.canMutate()) return;
    const controller = new AbortController();
    void Promise.resolve()
      .then(() => sessions.catchUp({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 30_000 }))
      .catch(() => { /* The next message adds them. */ });
    return () => controller.abort();
  }, [epoch, sessionId, observing, capability]);
  const [steerText, setSteerText] = useState("");
  const [steerMessage, setSteerMessage] = useState("Refresh runtime state explicitly before targeting a run.");
  // A compaction asked here that did not complete: the status line says so until the next prompt or compaction.
  const [compactFailure, setCompactFailure] = useState<CompactionFailure | null>(null);
  const [abortRunMessage, setAbortRunMessage] = useState("Refresh runtime state explicitly before targeting cancellation.");
  // A Stop that did not stop the run says so in the status line, while the run goes on; its key, while its receipt waits.
  const [stopFailure, setStopFailure] = useState<StopFailure | null>(null);
  const [submittedStop, setSubmittedStop] = useState<string | null>(null);
  const [message, setMessage] = useState("Ready to send to this owned session.");
  const [page, setPage] = useState<SessionReceiptPage>();
  const [submittedThinking, setSubmittedThinking] = useState<{ key: string; runId: string | null } | null>(null);
  const [submittedCompact, setSubmittedCompact] = useState<CompactionWatch | null>(null);
  const [runtimeState, setRuntimeState] = useState<RuntimeState>();
  const observedProvider = runtimeState?.kind === "ready" ? runtimeState.snapshot.entry?.providerKey : undefined;
  const observedTransition = runtimeState?.kind === "ready" ? runtimeState.snapshot.coordinatorTransitionInProgress : undefined;
  // What the session runs with as the host has it: the agent switches its own agent prompt (`alta session set_agent`),
  // the alta tool or a tier gives it another model or effort. The composer shows it at once.
  const observedEntry = runtimeState?.kind === "ready" ? runtimeState.snapshot.entry : undefined;
  const observedSettings = observedEntry ? [observedEntry.pendingAgentPromptId ?? observedEntry.agentPromptId, observedEntry.modelId, observedEntry.reasoningEffort] : null;
  // Lifecycle observations, not polling: reconcile persisted selection after attach/detach/switch, and after a change
  // of the host to the agent prompt, the model or the effort of the session.
  const catalogObservation = useRef<string | null>(null);
  useEffect(() => {
    if (runtimeState?.kind !== "ready") return;
    const signature = JSON.stringify([epoch, sessionId, observedProvider, observedTransition, observedSettings]);
    if (catalogObservation.current === signature) return;
    catalogObservation.current = signature;
    setChoicesRevision(value => value + 1);
  }, [runtimeState, observedProvider, observedTransition, epoch, sessionId]);
  const runtimeScope = useRef<ReturnType<typeof runtimeReader.forSelection> | null>(null);
  const [observedInvalidEpoch, setInvalidEpoch] = useState(!capability.canMutate());
  const canMutate = useSyncExternalStore(capability.subscribe, capability.canMutate);
  const invalidEpoch = observedInvalidEpoch || !canMutate || !canAdmit();
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
    setSteerMessage(steering.pending(sessionId)
      ? "A retained steering request exists. Refresh submissions to reconcile it, or explicitly retry its exact key and target once the previous wait settles."
      : "Refresh runtime state explicitly before targeting a run.");
    setPage(undefined);
    setCompactFailure(null);
    setStopFailure(null);
    setSubmittedStop(null);
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
      try { await runtime.refresh(true); } catch { /* A failed read is shown by the reader; the observation goes on. */ }
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
      try { await refresh(); } catch { /* A failed read leaves no pending state; the next one comes. */ }
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
    return retained && queue.list(epoch, sessionId).some(item => item.request?.clientRequestId === retained.request.clientRequestId) ? undefined : retained;
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
  const observedTarget = runtimeState?.kind === "ready" ? runtimeState.snapshot : undefined;
  const runtimeConfiguration = observedTarget?.entry;
  // What a plugin answered belongs to the turn it came in: a turn that starts after it has the status line to itself.
  const activeRunId = observedTarget?.entry?.activeRunId ?? null;
  useEffect(() => {
    if (activeRunId) setPluginOutcome(current => current && current.runId !== activeRunId ? null : current);
  }, [activeRunId]);
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
    if (invalidEpoch || receipt?.state === "terminal") {
      if (receipt && !invalidEpoch) settlePluginOutcome(receipt);
      setSubmittedThinking(null); return;
    }
    if (runtimeState?.kind !== "ready") return;
    const runId = runtimeState.snapshot.entry?.activeRunId ?? null;
    if (runId && !submittedThinking.runId) setSubmittedThinking({ ...submittedThinking, runId });
    else if (!runId && submittedThinking.runId) setSubmittedThinking(null);
  }, [page, runtimeState, invalidEpoch, sessionId, submittedThinking]);
  const composerBusy = !invalidEpoch && (!!retainedSend?.inFlight || !!submittedThinking || runActive);
  useEffect(() => {
    if (!submittedCompact) return;
    const next = invalidEpoch ? null : watchCompaction(submittedCompact, sessionId, page);
    if (next === submittedCompact) return;
    // The receipt settled: a compaction that did not complete says so, whatever else waits for recovery.
    if (!invalidEpoch) setCompactFailure(watchedCompactionFailure(submittedCompact, sessionId, page));
    setSubmittedCompact(next);
  }, [page, invalidEpoch, sessionId, submittedCompact]);
  useEffect(() => {
    if (!submittedStop) return;
    if (invalidEpoch) { setSubmittedStop(null); return; }
    const row = page?.status === "ok" ? page.rows.find(item => item.kind === "AbortRun" && item.sessionId === sessionId && item.clientRequestId === submittedStop) : undefined;
    if (row?.state !== "terminal") return;
    setStopFailure(watchedStopFailure(submittedStop, sessionId, page));
    setSubmittedStop(null);
  }, [page, invalidEpoch, sessionId, submittedStop]);
  // A Stop that failed matters while the run goes on: once it ended, there is nothing left to stop.
  useEffect(() => { if (!runActive) setStopFailure(null); }, [runActive]);
  const stopShown = invalidEpoch ? null : stopFailure;
  // Presentation only, like the run above: the host decides what a compaction refuses.
  const compacting = !invalidEpoch && (!!pendingCompact?.inFlight || !!submittedCompact || hasPendingCompaction(sessionId, page));
  const thinkingSeconds = useThinkingElapsed(composerBusy);
  // What the provider goes on doing in the background, outside its runs. The list is kept while a reading
  // changes nothing of it, so that what follows it is not shown again every second.
  const [background, setBackground] = useState<readonly BackgroundTask[]>([]);
  useEffect(() => {
    const read = invalidEpoch || runtimeState?.kind !== "ready" ? [] : backgroundTasks(runtimeState.snapshot.entry);
    setBackground(current => sameBackgroundTasks(current, read) ? current : read);
  }, [runtimeState, invalidEpoch]);
  // Remote Control: what the runtime state says, and meanwhile what the host answered when it was turned on or off.
  const observedRemote = invalidEpoch || runtimeState?.kind !== "ready" ? null : entryRemoteControl(runtimeState.snapshot.entry);
  const [answeredRemote, setAnsweredRemote] = useState<RemoteControlView | null>(null);
  const [shownRemote, setShownRemote] = useState<RemoteControlView>(remoteControlView(null));
  useEffect(() => { setAnsweredRemote(null); }, [observedRemote?.status, observedRemote?.url, observedRemote?.error]);
  useEffect(() => {
    const next = answeredRemote ?? observedRemote ?? remoteControlView(null);
    setShownRemote(current => sameRemoteControl(current, next) ? current : next);
  }, [answeredRemote, observedRemote?.status, observedRemote?.url, observedRemote?.error]);
  // What the provider suggests as the next prompt (Claude Code), in place of the placeholder of an empty prompt.
  // It comes back whenever the prompt is empty again. A prompt the host accepted ends it, before the host says so:
  // the turn that follows brings another one, or none. It is ended where a prompt is accepted and not by the busy
  // state, which lags the runtime state: a session shown again after its run ended would lose its suggestion.
  const observedSuggestion = invalidEpoch || runtimeState?.kind !== "ready" ? null : entryPromptSuggestion(runtimeState.snapshot.entry);
  const latestSuggestion = useRef(observedSuggestion); latestSuggestion.current = observedSuggestion;
  const [answeredSuggestion, setAnsweredSuggestion] = useState<string | null>(null);
  // Only a state that was read ends it: a reading that failed says nothing of the suggestion.
  const suggestionRead = runtimeState?.kind === "ready";
  useEffect(() => { if (suggestionRead && !observedSuggestion) setAnsweredSuggestion(null); }, [suggestionRead, observedSuggestion]);
  const promptAccepted = useRef(() => {}); promptAccepted.current = () => {
    if (latestSuggestion.current) setAnsweredSuggestion(latestSuggestion.current);
    void runtimeScope.current?.refresh(true);
  };
  const suggestion = shownPromptSuggestion(observedSuggestion,
    { text, images: images.length, busy: composerBusy || compacting || !!pending || expanded, answered: answeredSuggestion });
  const setRemoteControl = useCallback(async (enabled: boolean) => {
    if (!capability.canMutate()) return "unavailable";
    const result = await sessions.setRemoteControl({ expectedEpoch: epoch, sessionId, enabled }, { timeoutMilliseconds: 90_000 });
    if (result.status === "ok") setAnsweredRemote(remoteControlView(result.remoteControl));
    return result.status;
  }, [capability, epoch, sessionId]);
  const openRemoteLink = useCallback((url: string) => {
    void markdownLinks.open({ expectedHostEpoch: epoch, address: url, sessionId, projectId: null, directory: null }, { timeoutMilliseconds: 8_000 }).catch(() => { /* The link is shown to copy. */ });
  }, [epoch, sessionId]);
  const readBackgroundJob = useCallback((jobId: string, signal: AbortSignal) => toolCalls.readJob({ expectedEpoch: epoch, sessionId, jobId }, { signal }), [epoch, sessionId]);
  const stopBackgroundTask = useCallback(async (taskId: string) => {
    if (!capability.canMutate()) return;
    // The answer is not shown: the task leaves the list when its provider stopped it.
    await sessions.stopBackgroundTask({ expectedEpoch: epoch, sessionId, taskId }, { timeoutMilliseconds: 15_000 });
  }, [capability, epoch, sessionId]);
  // A task of the list goes to its place in the timeline of this session; a run whose block is not there (older than the
  // history read) opens in its tab.
  const openRun = useContext(OpenRunContext);
  const revealBackground = useCallback((task: BackgroundTask, origin: HTMLElement) => {
    if (revealBackgroundTask(origin.closest(".session-workspace") ?? document, task)) return;
    if (task.kind === "workflow") openRun?.({ runId: task.id, title: task.description });
  }, [openRun]);
  const runActivity = useRef(onRunActivity); runActivity.current = onRunActivity;
  useEffect(() => {
    if (!watching) return;
    runActivity.current?.(composerBusy, background);
    return () => runActivity.current?.(null);
  }, [composerBusy, background, watching, sessionId]);
  const mcpPlugin = configuration?.plugins.find(plugin => `${plugin.id} ${plugin.name}`.toLowerCase().includes("mcp"));
  // The Compact control, the command picker and Ctrl+F11 all follow this: it is enabled exactly when compact() submits.
  const compactBlock = compactControlBlock({ invalidEpoch, compacting, retained: pendingCompact?.request ?? null, canSubmit: capability.canSubmit },
    epoch, sessionId, observedTarget);
  const compactUnavailable = compactBlock && t(compactControlBlockMessages[compactBlock]);
  const availableAbortRun = captureAbortRun(epoch, sessionId, observedTarget, "availability");
  // The latest stagePrompt, for what an answer outside this render sends: the text of a denied permission.
  const stageLatest = useRef(stagePrompt);
  const editLatest = useRef<(value: string) => void>(() => {});
  // Above the composer and never refused: a queued prompt waits for the session to be idle, a steering prompt
  // for the running turn. Only the composer's own text takes its images along.
  function stagePrompt(kind: "Queue" | "Steer", value = text, attached = value === latestText.current ? images : []) {
    if (pending || invalidEpoch || !canAdmit() || !scope.current || scope.current.signal.aborted) return false;
    if (!queue.add(kind, epoch, sessionId, value, attached.length ? attached : null)) return false;
    if (value === latestText.current) {
      inputRevision.current++; clearText();
      if (attached.length) imageOwner.replace(imageKey, attached, []);
    }
    setSendFailure("");
    return true;
  }
  // The prompt of a Send a plugin answered itself never reaches the conversation: its echo leaves the timeline. A steering
  // prompt has no echo; runId is the turn it was for.
  function settlePluginOutcome(row: SessionReceiptView, runId: string | null = null): boolean {
    const outcome = row.sessionId === sessionId ? pluginPromptOutcome(row) : null;
    if (!outcome) return false;
    setPluginOutcome({ ...outcome, runId });
    if (row.kind === "Send") submissions.acknowledgeOutgoing([outgoingKey({ expectedEpoch: epoch, sessionId, clientRequestId: row.clientRequestId })]);
    return true;
  }
  function retryLater() {
    holdUntil.current = Date.now() + 1200;
    window.setTimeout(() => setRetryTick(value => value + 1), 1300);
  }
  // The outcome of a queued prompt's Send. Its request does not belong to this panel: the row is settled even
  // when the tab was closed meanwhile.
  function settleQueued(id: string, request: Readonly<{ clientRequestId: string; selection?: SessionSelection | null }>) {
    const key = request.clientRequestId;
    return (result: SubmissionResult) => {
      observeEpoch(result);
      const accepted = result.status === "accepted" || result.status === "replay";
      if (!accepted || result.receipt?.state === "terminal") setSubmittedThinking(current => current?.key === key ? null : current);
      if (accepted) {
        selections.sent(epoch, sessionId, request.selection); queue.sent(id); promptAccepted.current();
        if (result.receipt) settlePluginOutcome(result.receipt);
      }
      else if (result.status === "uncertain" || result.status === "stale_epoch") queue.uncertain(id, "reason" in result && result.reason ? result.reason : result.status);
      // The session was working after all: the prompt keeps its place and leaves at the next idle moment.
      else if (result.status === "busy" || result.status === "capacity") { queue.release(id); retryLater(); }
      else queue.fail(id, result.status);
    };
  }
  function settleSteering(id: string, runId: string) {
    return (result: SubmissionResult) => {
      observeEpoch(result);
      if (result.status === "accepted" || result.status === "replay") {
        queue.sent(id);
        // A plugin answered it already (a replay of a settled request): it never reaches the turn, and leaves the list.
        if (result.receipt && settlePluginOutcome(result.receipt, runId)) queue.delivered(id);
      }
      else if (result.status === "uncertain") queue.uncertain(id, "uncertain");
      else if (result.status === "busy") { queue.release(id); retryLater(); }
      // A request the host no longer keeps the receipt of was delivered earlier: it is not sent a second time.
      else if (result.status === "closed" || result.status === "stale_epoch" || result.status === "expired") queue.fail(id, result.status);
      // The turn cannot be steered: the prompt is sent as the next one instead.
      else queue.requeue(id);
    };
  }
  function retryStaged(item: ComposerQueueItem) {
    if (invalidEpoch) return;
    if (item.state === "failed") { queue.release(item.id); return; }
    if (item.state !== "uncertain" || !item.request) return;
    // Only the retained request itself is sent again: the same key, so the host cannot run it twice.
    if (item.kind === "Queue") {
      const retained = submissions.pending(sessionId);
      if (retained?.request.clientRequestId !== item.request.clientRequestId) queue.sent(item.id);
      else if (!retained.inFlight && capability.canSubmit(retained.request))
        void submissions.submit(retained.request, queue.signal, capability, settleQueued(item.id, retained.request), "whenAccepted");
    } else {
      const retained = steering.pending(sessionId);
      if (retained?.request.clientRequestId !== item.request.clientRequestId) queue.sent(item.id);
      else if (!retained.inFlight && capability.canSubmit(retained.request))
        void steering.submit(retained.request, queue.signal, capability, settleSteering(item.id, retained.request.expectedRunId));
    }
  }
  const liveTexts = liveConnected ? currentLive.snapshot?.session?.text : undefined;
  useEffect(() => {
    if (invalidEpoch || !canAdmit() || !scope.current || scope.current.signal.aborted || runtimeState?.kind !== "ready") return;
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
        queue.delivered(item.id);
      }
    }
    // A pending next-turn agent prompt is consumed by Send, not a reason to hold an idle queue.
    const idle = !composerBusy && !retainedSend && !runId && (!entry || target.coordinatorTransitionInProgress === false
      && !entry.queueDrainInProgress && !entry.isRetiring);
    if (Date.now() < holdUntil.current) return;
    const steerItem = staged.find(item => item.kind === "Steer" && item.state === "waiting");
    if (steerItem && !steering.pending(sessionId) && !staged.some(item => item.kind === "Steer" && (item.state === "sending" || item.state === "uncertain"))) {
      const request = captureSteering(epoch, sessionId, target, steerItem.text, crypto.randomUUID());
      if (request && capability.canSubmit(request)) {
        const claimed = queue.claim(steerItem, request);
        if (claimed) {
          steerBaseline.current.set(claimed.id, new Set(userRows.map(row => row.contentId)));
          void steering.submit(request, queue.signal, capability, settleSteering(claimed.id, request.expectedRunId));
        }
      // Nothing runs any more: the prompt is the next one sent.
      } else if (idle) queue.requeue(steerItem.id);
      return;
    }
    const next = staged.find(item => item.kind === "Queue" && item.state === "waiting");
    if (!next || !idle || loadingChoices || queue.isHeld(next)
      || staged.some(item => item.kind === "Queue" && (item.state === "sending" || item.state === "uncertain"))) return;
    // The prompt leaves as a Send of this moment: the selection, the project references and the images of one.
    const chosen = selections.current(epoch, sessionId) ?? selection;
    const usable = chosen && activeChoices && validSelection(activeChoices, chosen) ? chosen : null;
    const sendSelection = next.selection ?? usable ?? (next.images ? activeChoices?.current ?? null : null);
    // The choices of the session are read before a selection is judged: the row waits for them, it is not refused.
    if (next.selection && !activeChoices) return;
    if (next.selection && !validSelection(activeChoices!, next.selection)) {
      queue.fail(next.id, "invalid_selection"); return;
    }
    // A file goes to any model; an image needs one that reads images.
    if (next.images && (!sendSelection || next.images.some(isImageAttachment)
      && activeChoices?.models.find(model => model.id === sendSelection.modelId)?.imageInput !== true)) {
      queue.fail(next.id, "image_unsupported"); return;
    }
    const references = referenceScope?.expectedEpoch === epoch && referenceScope.sessionId === sessionId
      ? { projectId: referenceScope.projectId, projectPath: referenceScope.projectPath } : null;
    const request = captureSubmission(epoch, sessionId, next.text, crypto.randomUUID(), sendSelection, references, next.images);
    if (!request) { queue.fail(next.id, "invalid_request"); return; }
    if (!capability.canSubmit(request)) return;
    const claimed = queue.claim(next, request);
    if (!claimed) return;
    promptHistory.add(sessionId, request.text);
    setSubmittedThinking({ key: request.clientRequestId, runId: null });
    setPluginOutcome(null);
    void submissions.submit(request, queue.signal, capability, settleQueued(claimed.id, request), "whenAccepted");
  }, [stagedRevision, steeringRevision, runtimeState, invalidEpoch, !!retainedSend, composerBusy, epoch, sessionId, liveTexts, loadingChoices, retryTick, selection, choices]);
  // A steering prompt a plugin answered itself never shows in the timeline: its settled receipt says so, and its row
  // leaves the list without waiting for the turn to end.
  useEffect(() => {
    if (invalidEpoch || !page) return;
    for (const item of staged) {
      const request = item.kind === "Steer" && item.state === "delivering" ? item.request as SessionSteerRequest | undefined : undefined;
      const row = request && page.rows.find(value => value.kind === "Steer" && value.sessionId === sessionId && value.clientRequestId === request.clientRequestId);
      if (row && settlePluginOutcome(row, request!.expectedRunId)) {
        steerBaseline.current.delete(item.id);
        queue.delivered(item.id);
      }
    }
  }, [page, stagedRevision, invalidEpoch, sessionId]);
  function observeEpoch(result: { status: string; epoch: string | null }) {
    if (!capability.observe(result)) setInvalidEpoch(true);
  }
  function submit(queuePrompt = enqueue) {
    if (!canAdmit()) return;
    if (afterDictation(actions => actions.submit(queuePrompt))) return;
    const signal = scope.current?.signal;
    sendDiagnostics.info("composer action", { mounted: !!signal, aborted: signal?.aborted ?? false,
      capabilityValid: capability.canMutate(), pending: !!submissions.pending(sessionId),
      inFlight: submissions.pending(sessionId)?.inFlight ?? false, enqueue: queuePrompt, busy: composerBusy, hasImages: images.length > 0 });
    if (!signal || signal.aborted || !capability.canMutate()) return;
    // The provider was taken out of the space of the session: another one is chosen before anything is sent.
    // The status line already says why; it goes once the session has another provider.
    if (spaceProblem) { setSelectionOpenRequest(value => value + 1); return; }
    const retained = ownPending();
    if (!retained && !queuePrompt && composerBusy) { stagePrompt(images.length ? "Queue" : "Steer"); return; }
    // Enqueue waits for idle; normal Send keeps its place behind prompts already waiting.
    if (!retained && (queuePrompt || composerBusy || !!submissions.pending(sessionId)
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
    if (!retained && images.length && (hasImages && imageCapability !== true || !activeChoices || !sendSelection || !validSelection(activeChoices, sendSelection))) {
      setImageNotice(hasImages ? "Image input is unsupported or unknown for the current selection. Remove attachments or choose an observed supported model."
        : "Attachments require a model of the observed catalog. Choose a model or remove the attachments."); return;
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
    setPluginOutcome(null);
    setCompactFailure(null);
    void submissions.submit(request, signal, capability, result => {
      observeEpoch(result);
      if (["accepted", "replay"].includes(result.status) && result.receipt) settlePluginOutcome(result.receipt);
      if (!["accepted", "replay"].includes(result.status) || result.receipt?.state === "terminal") setSubmittedThinking(null);
      const unchanged = (inputRevision.current === revision || latestText.current === request.text) && imageOwner.get(imageKey) === capturedImages;
      // The session started working between the keystroke and the host: the prompt joins the queue instead,
      // without a failed message in the timeline.
      if (result.status === "busy" && !retained && canAdmit() && !signal.aborted
        && queue.add("Queue", epoch, sessionId, request.text, request.images?.length ? request.images : null)) {
        submissions.acknowledgeOutgoing([outgoingKey(request)]);
        if (!signal.aborted && unchanged) { clearText(); imageOwner.replace(imageKey, capturedImages, []); }
        return;
      }
      const failure = result.status === "accepted" || result.status === "replay" ? null : sendFailureMessage(result.status, "reason" in result ? result.reason : undefined);
      setMessage(failure ?? "Ready to send to this owned session.");
      // The prompt stays in the composer and the status line says why: a prompt is never answered with a toast.
      setSendFailure(failure ?? "");
      if (result.status === "accepted" || result.status === "replay") { selections.sent(epoch, sessionId, request.selection); promptAccepted.current(); }
      if ((!retained || !request.images?.length) && (result.status === "accepted" || result.status === "replay") && !signal.aborted && canAdmit()
        // The composer still holds exactly what was sent (a choices refresh alone also bumps the revision).
        && (inputRevision.current === revision || latestText.current === request.text)
        && imageOwner.get(imageKey) === capturedImages) { clearText(); imageOwner.replace(imageKey, capturedImages, []); }
    });
  }
  // Commands addressed to the active session's composer that no control carries out (keyboard shortcuts and the
  // command picker). Send, Abort, Clear Queue and Compact press their control, marked with data-composer-command.
  useEffect(() => {
    if (!active) return;
    const run = (event: Event) => {
      const command = (event as CustomEvent<string>).detail;
      if (invalidEpoch || !canAdmit()) return;
      if (command === "nextPrompt") {
        const prompts = activeChoices?.prompts ?? [];
        if (prompts.length > 1 && selected && !selectionDisabled)
          select("agentPromptId", prompts[(prompts.findIndex(prompt => prompt.id === selected.agentPromptId) + 1) % prompts.length].id);
      }
    };
    window.addEventListener("codealta:composer", run);
    return () => window.removeEventListener("codealta:composer", run);
  });
  // What a plugin asks of this composer: the shell names the session, or none for the focused pane.
  async function submitPlugin(request: PluginComposerRequest): Promise<boolean | null> {
    const lifetime = scope.current?.signal;
    if (!lifetime || lifetime.aborted || request.signal?.aborted || !canAdmit() || !capability.canMutate()) return false;
    const signal = request.signal ? AbortSignal.any([lifetime, request.signal]) : lifetime;
    // A queued prompt is the queue's, not this composer's: it outlives the pane (a closed tab, another space shown), and
    // whichever pane of the session is mounted sends it. Only the one who asked can take it back before it leaves.
    const queued = request.signal ?? queue.signal;
    const value = request.text ?? "";
    let result: boolean | null = null;
    const received = (reply: SubmissionResult) => {
      observeEpoch(reply);
      result = reply.status === "accepted" || reply.status === "replay" ? true
        : reply.status === "uncertain" || reply.status === "stale_epoch" ? null : false;
      if (result) promptAccepted.current();
    };
    // Do not acknowledge a React state update or a page-local queue as downstream ownership. These
    // helpers latch before dispatch and settle the original command's receipt. An aborted transport is
    // uncertain, not cancellation of host work; the plugin handoff keeps its claim in that case.
    if (request.kind === "compact") {
      const command = captureCompaction(epoch, sessionId, observedTarget, crypto.randomUUID());
      if (!command || compaction.pending(sessionId)) return false;
      await compaction.submit(command, signal, capability, received);
    } else if (request.kind === "steer" && composerBusy) {
      const command = captureSteering(epoch, sessionId, observedTarget, value, crypto.randomUUID());
      if (!command || steering.pending(sessionId)) return false;
      await steering.submit(command, signal, capability, received);
    } else if (request.kind === "enqueue" || composerBusy || enqueue
      || queue.list(epoch, sessionId).some(item => item.kind === "Queue" && item.state !== "failed")) {
      const chosen = selections.current(epoch, sessionId) ?? selected;
      if (chosen && (!activeChoices || !validSelection(activeChoices, chosen))) return false;
      // Enqueue has always meant a deferred Send, including when this saved session is unattached.
      // Keep the requested next-send selection and await the row's original downstream receipt.
      return await queue.admit(epoch, sessionId, value, queued, chosen);
    } else {
      const chosen = selections.current(epoch, sessionId) ?? selected;
      if (chosen && (!activeChoices || !validSelection(activeChoices, chosen))) return false;
      const references = referenceScope?.expectedEpoch === epoch && referenceScope.sessionId === sessionId
        ? { projectId: referenceScope.projectId, projectPath: referenceScope.projectPath } : null;
      const command = captureSubmission(epoch, sessionId, value, crypto.randomUUID(), chosen, references);
      if (!command || submissions.pending(sessionId)) return false;
      let becameBusy = false;
      await submissions.submit(command, signal, capability, reply => {
        becameBusy = reply.status === "busy";
        if (reply.status === "accepted" || reply.status === "replay") selections.sent(epoch, sessionId, command.selection);
        received(reply);
      });
      // Preserve ordinary Send's busy-race behavior, but still await actual downstream admission of
      // the deferred Send. A Close by the one who asked, after the refusal, conclusively prevents the new attempt.
      if (becameBusy && !queued.aborted && canAdmit() && capability.canMutate()) {
        submissions.acknowledgeOutgoing([outgoingKey(command)]);
        return await queue.admit(epoch, sessionId, value, queued, chosen);
      }
    }
    return result;
  }
  useEffect(() => {
    const run = (event: Event) => {
      const request = (event as CustomEvent<PluginComposerRequest>).detail;
      if (request.handled || (request.sessionId ? request.sessionId !== sessionId : !active)) return;
      request.handled = true;
      if (request.kind === "state") { request.state = { sessionId, draftText: latestText.current, busy: composerBusy }; request.result = true; return; }
      if (invalidEpoch || pending || !canAdmit() || !scope.current || scope.current.signal.aborted || request.signal?.aborted) return;
      const value = request.text ?? "";
      // A prompt that names its agent prompt goes with it or not at all: a plan is carried out by the default one.
      if (request.agentPromptId && (request.kind === "send" || request.kind === "enqueue") && selected?.agentPromptId !== request.agentPromptId) {
        if (selectionDisabled || !activeChoices?.prompts.some(prompt => prompt.id === request.agentPromptId)) return;
        select("agentPromptId", request.agentPromptId);
      }
      if (request.kind === "draft") { editText(value); request.result = true; }
      else if (request.kind === "compact" || value.trim()) { request.completion = submitPlugin(request); request.result = true; }
    };
    window.addEventListener(pluginComposerEvent, run);
    return () => window.removeEventListener(pluginComposerEvent, run);
  });
  // One shot per mounted composer: the parent's marker is a ref, so this prop outlives its consumption.
  const autoSent = useRef(false);
  useEffect(() => {
    if (!autoSend || autoSent.current || text !== autoSend.text || images.length !== (autoSend.images ?? 0) || pending || invalidEpoch || !canAdmit() || !scope.current || scope.current.signal.aborted
      || !choices || choices.status !== "ok" || choices.epoch !== epoch || choices.sessionId !== sessionId) return;
    const latest = selections.current(epoch, sessionId);
    if ((latest || selection) && (!validSelection(choices, latest ?? selection!) || latest && selection !== latest)) return;
    autoSent.current = true;
    autoSend.consume();
    submit();
  }, [autoSend, text, images, choices, selection, pending, invalidEpoch]);
  async function refresh(offset = 0) {
    const signal = scope.current?.signal;
    if (!signal) return;
    const revision = ++receiptRevision.current;
    const input = inputRevision.current;
    const originalText = submissions.pending(sessionId)?.request.text;
    await refreshSubmissions(hostReads?.receipts ?? sessions.receipts, epoch, offset, signal, result => {
      if (revision !== receiptRevision.current) return;
      observeEpoch(result);
      setReceiptUnavailable(result.status !== "ok");
      if (result.status !== "ok") return; // Keep the last receipts; a missed read is not settlement.
      setPage(result);
      queue.reconcile(result);
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
      compaction.reconcile(sessionId, result, capability);
      if (abortRuns.reconcile(sessionId, result, capability))
        setAbortRunMessage("Exact cancellation receipt recovered. Review its outcome; cancellation signalling is not run completion.");
    }, capability);
  }
  // The explicit Steer button targets the running turn; without one the prompt is sent.
  // An empty prompt steers with the first prompt of the queue, as the terminal UI does.
  function steerFromComposer() {
    if (pending || invalidEpoch) return;
    if (afterDictation(actions => actions.steerFromComposer())) return;
    if (!latestText.current.trim() && !images.length) {
      const next = staged.find(item => item.kind === "Queue" && item.state === "waiting" && !queue.isHeld(item));
      if (next && composerBusy) queue.steerNow(next);
      return;
    }
    if (!composerBusy) { submit(); return; }
    // Steering carries text only: a prompt with images waits for the next turn.
    stagePrompt(images.length ? "Queue" : "Steer");
  }
  function steer(fromComposer = false) {
    if (!canAdmit()) return;
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
    if (!canAdmit()) return;
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = compaction.pending(sessionId);
    if (retained?.inFlight) return;
    const request = retained?.request ?? captureCompaction(epoch, sessionId, observedTarget, crypto.randomUUID());
    if (!request || !capability.canSubmit(request)) return;
    setCompactFailure(null);
    const key = request.clientRequestId;
    setSubmittedCompact({ key, listed: false });
    void compaction.submit(request, signal, capability, result => {
      observeEpoch(result);
      // An admission that is not known leaves the receipts to say whether a compaction is going on.
      if (result.status !== "accepted" && result.status !== "replay" || result.receipt?.state === "terminal")
        setSubmittedCompact(current => current?.key === key ? null : current);
      setCompactFailure(compactionAdmissionFailure(result));
    });
  }
  function abortRun() {
    if (!canAdmit()) return;
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !capability.canMutate()) return;
    const retained = abortRuns.pending(sessionId);
    if (retained?.inFlight) return;
    const request = retained?.request ?? captureAbortRun(epoch, sessionId, observedTarget, crypto.randomUUID());
    if (!request || !capability.canSubmit(request)) return;
    setAbortRunMessage("Exact cancellation admission pending…");
    setStopFailure(null);
    setSubmittedStop(request.clientRequestId);
    void abortRuns.submit(request, signal, capability, result => {
      observeEpoch(result);
      setStopFailure(abortRunAdmissionFailure(result));
      if (result.status !== "accepted" && result.status !== "replay" || result.receipt?.state === "terminal")
        setSubmittedStop(current => current === request.clientRequestId ? null : current);
      setAbortRunMessage(result.status === "accepted" || result.status === "replay"
        ? "Cancellation request accepted. Refresh submissions for signalling outcome; run completion is not confirmed."
        : `Exact cancellation: ${result.status}. Uncertain requests retain their original run and key. No automatic retry.`);
    });
  }
  function abort(row?: SessionReceiptView, retainedOperationId?: string) {
    if (!canAdmit()) return;
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
  // The provider as Settings names it ("Claude Business"): the configuration's name, else the one the host gives the
  // provider of the session, else its key.
  const configuredProviderName = useProviderName(selected?.providerKey);
  const selectedProviderName = configuredProviderName
    ?? (selected?.providerKey && selected.providerKey === activeChoices?.current?.providerKey ? activeChoices.providerName : null)
    ?? selected?.providerKey ?? t("session provider");
  const spaceProblem = activeChoices?.providerOutsideSpace
    ? t("{provider} is not available in the space of this session: choose another provider to send.", { provider: activeChoices.providerName ?? activeChoices.current?.providerKey ?? t("session provider") })
    : activeChoices?.providerUnavailable
      ? t("{provider} is no longer available: choose another provider to send.", { provider: activeChoices.providerName ?? activeChoices.current?.providerKey ?? t("session provider") })
      : null;
  // Such a session opens the provider chooser once when it is shown: it sends nothing before it has another provider.
  // One whose provider was removed from the configuration takes the default provider instead, when one is configured.
  const outsideOpened = useRef<string | null>(null);
  const outsideSpace = !!activeChoices?.providerOutsideSpace;
  const providerGone = outsideSpace || !!activeChoices?.providerUnavailable;
  const replacement = outsideSpace ? null : activeChoices?.replacementProvider ?? null;
  const switchable = runtimeState?.kind === "ready" && !runtimeState.snapshot.coordinatorTransitionInProgress && !runtimeState.snapshot.entry?.activeRunId
    && !runtimeState.snapshot.entry?.queueDrainInProgress && !runtimeState.snapshot.entry?.isRetiring;
  useEffect(() => {
    if (!active || !providerGone || outsideOpened.current === sessionId) return;
    if (replacement) {
      if (!switchable || selectionDisabled || providerBusy) return;
      outsideOpened.current = sessionId;
      void adoptReplacement(replacement).then(done => { if (!done) setSelectionOpenRequest(value => value + 1); });
      return;
    }
    outsideOpened.current = sessionId;
    setSelectionOpenRequest(value => value + 1);
  }, [active, providerGone, replacement, switchable, selectionDisabled, providerBusy, sessionId]);
  // Switches a session whose provider was removed to the configured default provider. Its history and draft are kept;
  // the provider connects on the next Send, which the host holds back while the switch runs. False when the switch
  // did not happen: the user chooses then. The form is not held open meanwhile: nothing is asked of the user.
  async function adoptReplacement(provider: string) {
    const signal = scope.current?.signal;
    const removed = activeChoices?.current?.providerKey;
    if (!signal || signal.aborted || !canAdmit() || !capability.canMutate() || !removed) return false;
    try {
      const offered = await sessions.providerChoices({ expectedEpoch: epoch, sessionId }, { signal });
      if (signal.aborted || offered.status !== "ok" || offered.epoch !== epoch || offered.sessionId !== sessionId || offered.providerKey !== removed
        || !offered.runtimeInstanceId || !offered.revision || !offered.providers.some(value => value.id === provider)) return false;
      const switched = await sessions.selectProvider({ expectedEpoch: epoch, sessionId, providerKey: provider, expectedProviderKey: removed,
        runtimeInstanceId: offered.runtimeInstanceId, attachmentGeneration: offered.attachmentGeneration, revision: offered.revision }, { signal });
      if (signal.aborted || switched.status !== "ok" || switched.epoch !== epoch) return false;
      const name = offered.providers.find(value => value.id === provider)?.name ?? provider;
      showToast({ message: t("{removed} is no longer configured: this session now uses {provider}.", { removed, provider: name }), intent: "primary", icon: "info-sign", timeout: 8000 });
      const value = await sessions.choices({ expectedEpoch: epoch, sessionId }, { signal });
      if (signal.aborted || !capability.canMutate() || value.epoch !== epoch || value.sessionId !== sessionId || value.status !== "ok" || !value.current) return true;
      const next = { ...value.current, agentPromptId: selected?.agentPromptId ?? value.current.agentPromptId };
      if (!selections.set(epoch, sessionId, value, next)) selections.set(epoch, sessionId, value, value.current);
      setChoices(value); setChoicesNotice("");
      loadModelChoices();
      void runtimeScope.current?.refresh();
      // The tier of the session gave a model of the removed provider.
      if (value.tier) void recordTier(null);
      return true;
    } catch { return false; }
  }
  function select(field: "agentPromptId" | "modelId" | "reasoningEffort" | "permissionMode", value: string) {
    if (!canAdmit() || !scope.current || scope.current.signal.aborted || !activeChoices || !selected || selectionDisabled) return;
    if (field === "permissionMode") { void applyPermissionMode(activeChoices, selected, value, scope.current.signal); return; }
    const next = changeSelection(activeChoices, selected, field, value);
    // What the session holds is no longer what its provider offers (a model or an agent prompt that is gone, an
    // effort its model does not list any more): the choice is refused, and said, since nothing else shows it.
    if (!next) {
      showToast({ message: t("The choice was not changed: choose an available model and agent prompt first."), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }
    if (!selections.set(epoch, sessionId, activeChoices, next)) return;
    setSelection(next);
    setChoicesNotice("");
    // A model chosen by hand is not the one of a tier any more.
    if (field === "modelId" && activeChoices.tier) void recordTier(null);
  }
  // The tiers of the space of the session: its own, or the global ones.
  const tierState = useTiers(epoch, { sessionId });
  const composerTiers = composerTierEntries(tierState, t);
  // Records the tier the session runs on, or that its model was chosen by hand; the choices then name it.
  async function recordTier(tier: string | null) {
    const signal = scope.current?.signal;
    const result = await sessions.setTier({ expectedEpoch: epoch, sessionId, tier }, { signal }).catch(() => null);
    if (signal?.aborted) return;
    if (result?.status !== "ok" || result.epoch !== epoch || result.sessionId !== sessionId) {
      showToast({ message: t("The tier was not changed ({status}).", { status: result?.status ?? "no_answer" }), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }
    setChoices(current => current && current.sessionId === sessionId ? { ...current, tier: result.tier } : current);
  }
  // A tier takes the model it gives now: of the session's provider at once, of another provider through a switch of
  // provider, as the provider list does. The session then falls back along that tier.
  async function chooseTier(tier: string) {
    if (!canAdmit() || !scope.current || scope.current.signal.aborted || !activeChoices || !selected || selectionDisabled) return;
    const choice = tierState?.choices[tier as TierName];
    if (!choice) {
      showToast({ message: t("No model of the {tier} tier is ready.", { tier: t(tierLabels[tier as TierName] ?? "Standard") }), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }
    const signal = scope.current.signal;
    if (choice.providerKey === selected.providerKey) {
      const model = changeSelection(activeChoices, selected, "modelId", choice.modelId);
      const next = model && choice.reasoning ? changeSelection(activeChoices, model, "reasoningEffort", choice.reasoning) ?? model : model;
      if (!next || !selections.set(epoch, sessionId, activeChoices, next)) {
        showToast({ message: t("The choice was not changed: choose an available model and agent prompt first."), intent: "warning", icon: "warning-sign", timeout: 6000 });
        return;
      }
      setSelection(next); setChoicesNotice("");
      await recordTier(tier);
      return;
    }
    const entry = runtimeState?.kind === "ready" ? runtimeState.snapshot : null;
    if (!entry || entry.coordinatorTransitionInProgress || entry.entry?.activeRunId || entry.entry?.queueDrainInProgress || entry.entry?.isRetiring) {
      showToast({ message: t("The tier was not changed ({status}).", { status: "busy" }), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }
    setProviderBusy(true);
    try {
      const offered = await sessions.providerChoices({ expectedEpoch: epoch, sessionId }, { signal });
      if (signal.aborted) return;
      const status = offered.status !== "ok" || offered.epoch !== epoch || !offered.runtimeInstanceId || !offered.revision ? offered.status
        : !offered.providers.some(provider => provider.id === choice.providerKey) ? "provider_unavailable" : null;
      const switched = status ? null : await sessions.selectProvider({ expectedEpoch: epoch, sessionId, providerKey: choice.providerKey,
        expectedProviderKey: offered.providerKey!, runtimeInstanceId: offered.runtimeInstanceId!, attachmentGeneration: offered.attachmentGeneration,
        revision: offered.revision! }, { signal });
      if (signal.aborted) return;
      if (status || switched?.status !== "ok") {
        showToast({ message: t("The tier was not changed ({status}).", { status: status ?? switched?.status ?? "no_answer" }), intent: "warning", icon: "warning-sign", timeout: 6000 });
        return;
      }
      const value = await sessions.choices({ expectedEpoch: epoch, sessionId }, { signal });
      if (signal.aborted || !capability.canMutate() || value.epoch !== epoch || value.sessionId !== sessionId || value.status !== "ok" || !value.current) return;
      const base = { ...value.current, agentPromptId: selected.agentPromptId ?? value.current.agentPromptId };
      const model = changeSelection(value, base, "modelId", choice.modelId) ?? base;
      const next = choice.reasoning ? changeSelection(value, model, "reasoningEffort", choice.reasoning) ?? model : model;
      if (!selections.set(epoch, sessionId, value, next)) selections.set(epoch, sessionId, value, value.current);
      setChoices(value); setChoicesNotice("");
      loadModelChoices();
      void runtimeScope.current?.refresh();
      await recordTier(tier);
    } catch {
      if (!signal.aborted) showToast({ message: t("The tier was not changed ({status}).", { status: "selection_unconfirmed" }), intent: "warning", icon: "warning-sign", timeout: 6000 });
    } finally { setProviderBusy(false); }
  }
  // The permission mode applies at once, also to a turn that runs: what waits for an answer and the new mode grants is
  // allowed. The button shows the choice while the host takes it, and the mode in force again when it refuses.
  // The mode is the session's and is asked for alone: a session whose model or effort is no longer one its provider
  // lists takes it too. What the next Send carries is then left as it was, since a Send refuses a selection that is
  // not valid, and the button shows the mode once the host has it.
  // Whether the user reviews the changes of the session before an agent commits them: read with the session, again
  // after each turn (the setting may have changed meanwhile), and changed at once from the form of the Agent chip.
  const [review, setReview] = useState<Readonly<{ key: string; own: boolean | null; effective: boolean; setting: boolean }> | null>(null);
  const reviewKey = `${epoch}:${sessionId}`;
  useEffect(() => {
    if (invalidEpoch) return;
    const controller = new AbortController();
    void sessions.reviewBeforeCommit({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8000 }).then(result => {
      if (!controller.signal.aborted && result.status === "ok") setReview({ key: reviewKey, own: result.own ?? null, effective: result.effective, setting: result.setting });
    }).catch(() => { /* The row is shown once the choice is known. */ });
    return () => controller.abort();
  }, [epoch, sessionId, reviewKey, invalidEpoch, composerBusy]);
  const shownReview = review?.key === reviewKey ? review : null;
  async function applyReview(value: boolean | null) {
    const result = await sessions.setReviewBeforeCommit({ expectedEpoch: epoch, sessionId, reviewBeforeCommit: value }, { timeoutMilliseconds: 30_000 }).catch(() => null);
    if (result?.status !== "ok" || result.sessionId !== sessionId) {
      showToast({ message: t("The review before commit was not changed ({status}).", { status: result?.status ?? "no_answer" }), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }

    setReview({ key: reviewKey, own: result.own ?? null, effective: result.effective, setting: result.setting });
  }
  // The options of the session (sub-sessions): read with the session, and changed from the form of the Agent chip. Its
  // tools and its instructions follow them from its next prompt.
  const [sessionOptions, setSessionOptions] = useState<Readonly<{ key: string; options: SessionOptionsChoice }> | null>(null);
  useEffect(() => {
    if (invalidEpoch) return;
    const controller = new AbortController();
    void sessions.options({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8000 }).then(result => {
      if (!controller.signal.aborted && result.status === "ok" && result.options) setSessionOptions({ key: reviewKey, options: result.options });
    }).catch(() => { /* The section is shown once the options are known. */ });
    return () => controller.abort();
  }, [epoch, sessionId, reviewKey, invalidEpoch]);
  const shownOptions = sessionOptions?.key === reviewKey ? sessionOptions.options : null;
  async function applyOptions(value: SessionOptionsChoice | null) {
    const result = await sessions.setOptions({ expectedEpoch: epoch, sessionId, options: sessionOptionsOf(value) }, { timeoutMilliseconds: 30_000 }).catch(() => null);
    if (result?.status !== "ok" || !result.options) {
      showToast({ message: t("The options were not changed ({status}).", { status: result?.status ?? "no_answer" }), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }

    setSessionOptions({ key: reviewKey, options: result.options });
  }
  // The sandbox the session runs in: read with the session and again after each turn (the settings may have changed
  // meanwhile), and changed at once from the form of the Agent chip. What the settings give is known while the session
  // follows them, and kept once it has a choice of its own.
  const [sandbox, setSandbox] = useState<SessionSandboxState | null>(null);
  useEffect(() => {
    if (invalidEpoch) return;
    const controller = new AbortController();
    void sandboxSessions.session({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8000 }).then(reply => {
      if (!controller.signal.aborted && reply.status === "ok") setSandbox(current => nextSessionSandbox(current, reviewKey, reply));
    }).catch(() => { /* The row is shown once the sandbox is known. */ });
    return () => controller.abort();
  }, [epoch, sessionId, reviewKey, invalidEpoch, composerBusy]);
  const shownSandbox = sandbox?.key === reviewKey ? sandbox : null;
  async function applySandbox(value: string | null) {
    const reply = await sandboxSessions.setSession({ expectedEpoch: epoch, sessionId, value: value ?? followScopes }, { timeoutMilliseconds: 30_000 }).catch(() => null);
    if (reply?.status !== "ok") {
      showToast({ message: t("The sandbox was not changed ({status}).", { status: reply?.status ?? "no_answer" }), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }
    setSandbox(current => nextSessionSandbox(current, reviewKey, reply));
  }

  async function applyPermissionMode(current: SessionChoicesResponse, chosen: SessionSelection, value: string, signal: AbortSignal) {
    const next = changeSelection(current, chosen, "permissionMode", value);
    if (!next) return;
    const sendable = validSelection(current, next);
    if (sendable) setSelection(next);
    // The mode the session has already is no change to ask for.
    if (next.permissionMode == null) { if (sendable) selections.set(epoch, sessionId, current, next); return; }
    const result = await sessions.setPermissionMode({ expectedEpoch: epoch, sessionId, permissionMode: next.permissionMode }, { signal }).catch(() => null);
    if (signal.aborted) return;
    if (result?.status !== "ok" || result.epoch !== epoch || result.sessionId !== sessionId) {
      if (sendable) setSelection(chosen);
      showToast({ message: t("The permission mode was not changed ({status}).", { status: result?.status ?? "no_answer" }), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }
    // The session has it now: the next Send carries none, and the choices name it as the session's.
    const updated = { ...current, current: { ...current.current!, permissionMode: result.permissionMode ?? null } };
    const kept = { ...next, permissionMode: null };
    if (!sendable) setChoices(updated);
    else if (!selections.set(epoch, sessionId, updated, kept)) { setChoices(updated); setSelection(kept); }
    setChoicesNotice("");
  }
  // An option of a model (its context, a fast mode) is the model's, in every session of its provider: the host keeps it,
  // and the choices it answers have it.
  // One change at a time: the controls wait for the answer, which names every option of the model.
  const [optionBusy, setOptionBusy] = useState(false);
  async function applyModelOption(modelId: string, optionId: string, value: string) {
    const signal = scope.current?.signal;
    if (!signal || signal.aborted || !canAdmit() || selectionDisabled || optionBusy) return;
    setOptionBusy(true);
    const result = await sessions.setModelOption({ expectedEpoch: epoch, sessionId, modelId, optionId, value }, { signal }).catch(() => null)
      .finally(() => { if (!signal.aborted) setOptionBusy(false); });
    if (signal.aborted) return;
    if (result?.status !== "ok" || result.epoch !== epoch || result.sessionId !== sessionId) {
      showToast({ message: t("The option was not changed ({status}).", { status: result?.status ?? "no_answer" }), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }
    setChoices(current => current && current.sessionId === sessionId ? { ...current, models: result.models } : current);
  }
  const selectedModel = activeChoices?.models.find(m => m.id === selected?.modelId);
  const efforts = selectedModel?.efforts ?? null;
  // The size of context the session uses where its model has several: the meter shows it at once.
  const contextLimit = sessionContextSize(selectedModel, shownOptions);
  const reasoning = reasoningShown(selected?.reasoningEffort ?? null, selectedModel);
  // The permission mode is shown where the host offers modes.
  const permissionModes = offeredPermissionModes(activeChoices);
  const shownPermission = activeChoices && selected && permissionModes.length > 0 ? chosenPermissionMode(activeChoices, selected) ?? activeChoices.defaultPermissionMode ?? null : undefined;
  const permissionSummary = shownPermission === undefined ? null
    : { mode: shownPermission, name: shownPermission === null ? t("The setting of the CLI") : permissionModeName(shownPermission) ? t(permissionModeName(shownPermission)!) : shownPermission };
  // What the `⋯` menu holds that needs the user: active reminders (their count), a Remote Control that is on or failed.
  const activeReminders = observedReminderCount ?? 0;
  const remoteMark = activeChoices?.supportsRemoteControl === true && shownRemote.status !== "off" ? shownRemote.status : null;
  const moreAttention = remoteMark === "failed" ? { tone: "warning" as const, count: activeReminders || undefined }
    : activeReminders > 0 || remoteMark ? { tone: "info" as const, count: activeReminders || undefined } : null;
  const moreLabel = [activeReminders > 0 && reminderLabel,
    remoteMark && t(remoteMark === "connected" ? "Remote Control: on" : remoteMark === "connecting" ? "Remote Control: connecting" : "Remote Control: failed")].filter(Boolean).join(" · ") || undefined;
  const imageCapability = activeChoices?.models.find(m => m.id === selected?.modelId)?.imageInput;
  function pasteImages(event: ClipboardEvent<HTMLElement>) {
    if (!event.clipboardData.files.length) return;
    event.preventDefault();
    void attachFiles(Array.from(event.clipboardData.files), event.currentTarget, true);
  }
  // Attaches the files the user chose, dropped or pasted: an image as a PNG, any other file as it is. Images wait for
  // a model that reads them; the other files go to any model.
  async function attachFiles(files: readonly File[], origin: HTMLElement, pasted: boolean) {
    if (!files.length) return;
    const revision = ++inputRevision.current;
    if (pending || invalidEpoch || !capability.canMutate()) {
      setImageNotice(t("Attachments are unavailable: the composer cannot be edited now.")); return;
    }
    const refusedImages = imageCapability !== true && files.some(isImageFile);
    const accepted = refusedImages ? files.filter(file => !isImageFile(file)) : files;
    if (!accepted.length) {
      setImageNotice("Image paste unavailable: choose an explicitly supported observed model and an editable composer."); return;
    }
    const original = imageOwner.get(imageKey);
    const selectionVersion = selectionRevision.current; const lifetime = inputLifetime;
    const signal = scope.current?.signal;
    const current = () => !signal?.aborted && inputRevision.current === revision && selectionRevision.current === selectionVersion
      && canAdmit() && origin.isConnected && !origin.closest("[inert]") && (!lifetime || lifetime.current()) && capability.canMutate()
      && imageOwner.get(imageKey) === original && !submissions.pending(sessionId);
    if (!current()) return;
    const finish = imageOwner.beginRead(imageKey);
    if (!finish) { setImageNotice("An image read is still pending, or the eight-read limit has been reached."); return; }
    try {
      const added = [];
      let total = attachmentBytes(original);
      for (const file of accepted) {
        const attachment = await readAttachedFile(file, pasted ? `Image ${original.length + added.length + 1}` : null);
        if (!current()) return;
        total += attachmentBytes([attachment]);
        if (total > attachmentLimits.totalBytes) { setImageNotice(t("The attachments of a prompt are limited to {size} MB in all.", { size: attachmentLimits.totalBytes / 1024 / 1024 })); return; }
        added.push(attachment);
      }
      if (!imageOwner.replace(imageKey, original, [...original, ...added])) {
        setImageNotice("Image draft capacity reached (8 image-bearing drafts). Remove attachments from another draft first."); return;
      }
      setImageNotice(refusedImages ? "Image input is unsupported or unknown for the current selection. Remove attachments or choose an observed supported model." : "");
    } catch (error) {
      if (!current()) return;
      setImageNotice(error instanceof AttachmentError
        ? error.unreadable ? t("Could not read {name}: a folder cannot be attached.", { name: error.message })
          : t("Could not attach {name}: a file of 1 byte to {size} MB is required.", { name: error.message, size: attachmentLimits.fileBytes / 1024 / 1024 })
        : t(imagePasteFailure));
    }
    finally { finish(); }
  }
  function canEditImages() {
    return canAdmit() && !!scope.current && !scope.current.signal.aborted && !submissions.pending(sessionId)
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
  // Adds the links of the attach menu (issues, a folder) to the attachments, once each: they become text when the prompt is sent.
  function addLinks(links: readonly ReturnType<typeof linkAttachment>[]) {
    inputRevision.current++;
    const current = imageOwner.get(imageKey);
    const added = links.filter(link => !current.some(item => item.mediaType === link.mediaType && item.base64 === link.base64));
    if (!added.length || pending || invalidEpoch || !canAdmit() || !capability.canMutate() || submissions.pending(sessionId)) return;
    if (!imageOwner.replace(imageKey, current, [...current, ...added]))
      setImageNotice("Image draft capacity reached (8 image-bearing drafts). Remove attachments from another draft first.");
  }
  // A folder of the session's folder is a project reference, as with `@`; a chat has no project to resolve one in.
  const folderRoot = referenceScope ? sessionFolder ?? referenceScope.projectPath : null;
  async function addFolder() {
    if (pending || invalidEpoch || !capability.canMutate()) return;
    const pick = await pickFolder(desktopShell.pickFolder, t("Add a folder to the prompt"), sessionFolder ?? referenceScope?.projectPath ?? null);
    const link = pick.status === "ok" ? folderLink(pick.path, folderRoot) : null;
    if (pick.status === "ok" && link) addLinks([linkAttachment("folder", pick.path.replace(/[\\/]+$/u, "").split(/[\\/]/u).at(-1) || pick.path, link)]);
  }
  const attachIssues = (issues: readonly { number: number; url: string; title: string }[]) => addLinks(issues.flatMap(issue => {
    const link = insertIssueReferences("", 0, 0, [issue])?.text;
    return link ? [linkAttachment("issue", `#${issue.number} ${issue.title}`, link)] : [];
  }));
  const attachActions = { onFiles: (files: File[], origin: HTMLElement) => void attachFiles(files, origin, false),
    onFolder: () => void addFolder(), onIssues: referenceScope ? () => setIssueRequest(value => value + 1) : null };
  stageLatest.current = stagePrompt;
  editLatest.current = editText;
  sendActions.current = { submit, stagePrompt, steerFromComposer };
  // The cards of the requests to load a skill of an MCP server read its SKILL.md from the host, for the user to read first.
  const showPermissionSkill = permissionSkillReader(sessionPermissions.skillContent, epoch);
  return <>
    {permissionReviewer && <CommandPermissionPanel reviewer={permissionReviewer} epoch={epoch} sessionId={sessionId}
      canReview={() => capability.canMutate() && (inputLifetime?.current() ?? true)} running={composerBusy}
      instruct={(kind, value) => {
        // Text that cannot be staged (a request is pending, the queue is full) goes back to the composer, never lost.
        if (!stageLatest.current(kind, value, [])) editLatest.current(latestText.current.trim() ? `${latestText.current}
${value}` : value);
      }} takeFocus={() => !latestText.current.trim() && !images.length} onAnswered={() => promptInput.current?.focus()} visible={active}
      showSkill={showPermissionSkill} />}
    <WaitingSubSessionsPanel sessions={waitingSubSessions} epoch={epoch} createReviewer={createSubSessionReviewer}
      canReview={() => capability.canMutate() && (inputLifetime?.current() ?? true)} onAnswered={() => promptInput.current?.focus()} showSkill={showPermissionSkill} />
    <ComposerQueueStrip owner={queue} epoch={epoch} sessionId={sessionId} disabled={invalidEpoch} running={composerBusy} retry={retryStaged} commandTarget={active} />
    {pendingSteer && !staged.some(item => item.request?.clientRequestId === pendingSteer.request.clientRequestId) &&
      <div className="composer-queue-row"><AppIcon name="steer" size={15} /><span className="composer-queue-preview">{pendingSteer.request.text}</span>
        <Button icon={<AppIcon name="refresh" size={14} />} aria-label={t("Retry exact request")} disabled={invalidEpoch || pendingSteer.inFlight} onClick={() => steer()} /></div>}
    {/* A Claude Code provider without a CLI proposes its download where it is chosen. */}
    <ClaudeCliFirstUse epoch={epoch} providerKey={selected?.providerKey ?? observedProvider} />
    {!expanded && attachmentStrip}
    <ComposerSurface busy={composerBusy || compacting} status={<>
      <span className="composer-foreground-status" data-tone={stopShown || !composerBusy && !compacting && (invalidEpoch || sendFailure || compactFailure || spaceProblem) || !compacting && !invalidEpoch && pluginOutcome?.cancelled ? "danger"
        : !compacting && !invalidEpoch && pluginOutcome ? "notice" : undefined}>
        {stopShown ? <AppIcon name="error" size={14} /> : composerBusy || compacting ? <ActivitySpinner size={14} /> : <AppIcon name={invalidEpoch || sendFailure || compactFailure || spaceProblem || pluginOutcome?.cancelled ? "error" : pluginOutcome ? "plugin" : "prompt"} size={14} />}
        {/* A steering prompt a plugin answered says so while its turn goes on. */}
        <span className="composer-foreground-text">{stopShown ? t(stopShown.key, stopShown.parameters) : composerBusy && !invalidEpoch && !compacting && pluginOutcome ? t(pluginOutcome.key, pluginOutcome.parameters)
          : composerBusy ? thinkingSeconds > 0 ? t("Thinking · {elapsed}", { elapsed: formatThinkingElapsed(thinkingSeconds) }) : t("Thinking…")
          : compacting ? t("Compacting…")
          : !invalidEpoch && (sendFailure || spaceProblem) ? sendFailure || spaceProblem
          : !invalidEpoch && pluginOutcome ? t(pluginOutcome.key, pluginOutcome.parameters)
          : !invalidEpoch && compactFailure ? t(compactFailure.key, compactFailure.parameters) : t(invalidEpoch ? "Reload required." : pending ? "Exact-request waiter pending" : currentLive && !liveConnected ? "Run status unavailable" : draft.editGeneration !== null ? "Draft edited..." : suggestion ? "Tab or → takes the suggestion" : "Prompt ready")}</span></span>
      {/* A prompt that waits for a reload offers it: the window would otherwise have no way out. */}
      {invalidEpoch && !composerBusy && !compacting && <Button variant="minimal" size="small" intent="danger" className="composer-reload"
        icon={<AppIcon name="refresh" size={13} />} onClick={() => window.location.reload()}>{t("Reload")}</Button>}
      <BackgroundTasksStatus tasks={background} disabled={invalidEpoch} onStop={stopBackgroundTask} onReveal={revealBackground}
        renderOutput={props => <BackgroundJobDialog key={props.task.id} {...props} outputs={toolOutputs} readJob={readBackgroundJob} />} />
      {providerState.errors > 0 && <Button variant="minimal" size="small" className="composer-provider-alert" icon={<AppIcon name="warning" size={13} />}
        onClick={() => onOpenCatalog?.("providers")} title={t("Providers")}>{t(providerState.errors === 1 ? "{count} provider error" : "{count} provider errors", { count: providerState.errors })}</Button>}
      <ObservationStatus unavailable={!invalidEpoch && (receiptUnavailable || runtimeState?.kind === "error")} /></>}
    expandedEditor={expanded && !pending && !invalidEpoch && <ExpandedPromptEditor text={text} onChange={editText} onPaste={pasteImages} onCompositionStart={() => { inputRevision.current++; }} attachments={attachmentStrip} onClose={() => { inputRevision.current++; setExpanded(false); }} />}
    editor={{ id: active ? "session-prompt" : `session-prompt-${sessionId}`, ref: promptInput, onPaste: pasteImages, label: t("Message"), value: pending?.request.text ?? text, disabled: !!pending || invalidEpoch || expanded,
      onChange: editText, onCompositionStart: () => { inputRevision.current++; }, placeholder: suggestion ?? t("Ask CodeAlta to work on this project…"), suggested: !!suggestion, onKeyDown: event => {
        if (suggestion && takesPromptSuggestion({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey, altKey: event.altKey,
          metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode })) {
          event.preventDefault(); event.stopPropagation();
          editText(suggestion);
          // The prompt goes on after what was taken.
          requestAnimationFrame(() => promptInput.current?.setSelectionRange(suggestion.length, suggestion.length));
          return;
        }
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
           repeat: event.repeat, defaultPrevented: event.defaultPrevented }, () => submit(composerBusy), () => submit(false),
        navigator.platform.startsWith("Mac"))) { event.preventDefault(); event.stopPropagation(); }
      } }}
    onDropFiles={!pending && !invalidEpoch ? (files, origin) => void attachFiles(files, origin, false) : null}
    options={<><AttachMenu id={`attach-files-${sessionId}`} disabled={!!pending || invalidEpoch} {...attachActions} />
      <ComposerSelectionFields sessionId={sessionId} onOpenCatalog={onOpenCatalog} locked={providerBusy} openRequest={selectionOpenRequest} notice={spaceProblem}
      summary={{ agent: activeChoices?.prompts.find(p => p.id === selected?.agentPromptId)?.name ?? selected?.agentPromptId ?? "…",
        provider: selectedProviderName, providerKey: selected?.providerKey, modelId: selected?.modelId,
        model: selected?.modelId ? activeChoices?.models.find(m => m.id === selected.modelId)?.name ?? selected.modelId : t(loadingChoices ? "Loading…" : "No model"),
        reasoning: reasoning.effort ?? (loadingChoices ? t("Loading…") : null), permission: permissionSummary, review: shownReview?.effective ?? null,
        sandbox: sessionSandboxSummary(shownSandbox?.reply), subSessions: shownOptions?.subSessions ?? null,
        reasoningNote: reasoning.note === "none" ? t("{model} has no reasoning effort", { model: selectedModel!.name })
          : reasoning.note === "unused" ? `${t("Reasoning")}: ${selected?.reasoningEffort} · ${t("Not used")}` : null }}
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
          // A provider chosen by hand is not a model of a tier.
          if (value.tier) void recordTier(null);
        }} />}
      model={<ComposerModelPicker id={`composer-model-${sessionId}`} value={selected?.modelId ?? null} models={activeChoices?.models ?? noModels} providerKey={selected?.providerKey}
        disabled={invalidEpoch || !!pending} loading={loadingChoices} onChange={modelId => select("modelId", modelId)}
        tiers={composerTiers} currentTier={activeChoices?.tier ?? null} onChooseTier={tier => void chooseTier(tier)}
        title={selected?.modelId && !activeChoices?.models.some(m => m.id === selected.modelId) ? t("Saved selection; not verified by this host's observed model catalog.") : t("Model for the next Send · {provider}", { provider: selected?.providerKey ?? t("session provider") })} />}
      reasoning={<ReasoningSlider value={selected?.reasoningEffort ?? null} efforts={efforts} complete={!!selectedModel?.effortsComplete} model={selectedModel?.name} disabled={selectionDisabled}
        onChange={value => select("reasoningEffort", value)} />}
      modelOptions={selectedModel?.options?.length ? <ModelOptionFields sessionId={sessionId} options={selectedModel.options} disabled={selectionDisabled || optionBusy}
        onChange={(optionId, value) => void applyModelOption(selectedModel.id, optionId, value)} /> : undefined}
      review={shownReview && <ReviewBeforeCommitSelect id={`composer-review-${sessionId}`} own={shownReview.own} setting={shownReview.setting}
        disabled={invalidEpoch} onChange={value => void applyReview(value)} />}
      sandbox={shownSandbox && <SessionSandboxSelect id={`composer-sandbox-${sessionId}`} own={shownSandbox.reply.own} inherited={shownSandbox.inherited}
        choices={shownSandbox.reply.choices} disabled={invalidEpoch} onChange={value => void applySandbox(value)} />}
      options={shownOptions && { changed: changedSessionOptions(shownOptions, selectedModel),
        content: <SessionOptionsFields id={sessionId} value={shownOptions} context={selectedModel} disabled={invalidEpoch} onChange={value => void applyOptions(value)} /> }}
      permission={activeChoices && selected && permissionModes.length > 0 ? <PermissionModeSelect id={`composer-permission-${sessionId}`}
        value={chosenPermissionMode(activeChoices, selected)} modes={permissionModes} defaultMode={activeChoices.defaultPermissionMode ?? null}
        disabled={selectionDisabled} onChange={value => select("permissionMode", value)} /> : undefined} /></>}
    meters={usageTarget && <SessionUsageInspector key={JSON.stringify(usageTarget)} target={usageTarget} capability={capability} persisted={persistedUsage} onOpenDocumentation={onOpenDocumentation}
        limit={contextLimit === null ? null : String(contextLimit)}
        provider={selected?.providerKey ?? observedProvider ?? null} model={selected?.modelId ? activeChoices?.models.find(m => m.id === selected.modelId)?.name ?? selected.modelId : null}
        refreshKey={`${observing}:${composerBusy}:${compacting}:${runtimeState?.kind === "ready" ? runtimeState.snapshot.entry?.activeRunId ?? "" : ""}:${liveState?.snapshot?.revision ?? ""}`} />}
    more={{ attention: moreAttention, label: moreLabel, question: archive?.question, items: <>
      <AttachMenuItems inputId={`attach-files-${sessionId}`} disabled={!!pending || invalidEpoch} onFolder={attachActions.onFolder} onIssues={attachActions.onIssues} />
      {infoControl}
      {onOpenReminders && <Button ref={remindersTrigger} variant="minimal" icon={<AppIcon name="reminder" size={16} />} data-reminder-count=""
        disabled={invalidEpoch} aria-label={reminderLabel} title={`${reminderLabel} (Ctrl+G, Ctrl+D)`}
        onClick={onOpenReminders} endIcon={<span className="reminder-count" aria-hidden="true">{observedReminderCount ?? "?"}</span>}>{t("Reminders")}</Button>}
      {activeChoices?.supportsRemoteControl === true && <RemoteControlButton state={shownRemote} disabled={invalidEpoch}
        onSet={setRemoteControl} onOpenLink={openRemoteLink} openRequest={remoteControlRequest} />}
      {composerBusy && <Button variant="minimal" icon={<AppIcon name="steer" size={16} />} onClick={steerFromComposer}
        disabled={invalidEpoch || !!pending || !text.trim() && !staged.some(item => item.kind === "Queue" && item.state === "waiting")}
        aria-label={t("Steer the running turn")}
        title={t("Steer the running turn with this prompt")} endIcon={<kbd>{navigator.platform.startsWith("Mac") ? "Cmd+Enter" : "Ctrl+Enter"}</kbd>}>{t("Steer the running turn")}</Button>}
      {composerBusy && <Button variant="minimal" icon={<AppIcon name="queue" size={16} />} onClick={() => { if (!afterDictation(actions => { actions.stagePrompt("Queue"); })) stagePrompt("Queue"); }}
        disabled={invalidEpoch || !!pending || !text.trim() && images.length === 0}
        aria-label={t("Enqueue for the next turn")}
        title={t("Enqueue this prompt for the next turn ({shortcut})", { shortcut: "Enter" })} endIcon={<kbd>Enter</kbd>}>{t("Enqueue for the next turn")}</Button>}
      {/* The fold, since the box of "compact" is also the one of Archive below it. */}
      <Button ref={compactTrigger} variant="minimal" icon={<AppIcon name="fold" size={16} />} onClick={compact}
        data-epoch={epoch} data-session-id={sessionId} data-project-id={projectId ?? ""}
        data-composer-command={active ? "compact" : undefined} data-unavailable={compactUnavailable || undefined} disabled={!!compactBlock}
        aria-label={pendingCompact ? t("Retry exact compaction request for attachment {attachment}", { attachment: pendingCompact.request.expectedAttachmentGeneration }) : t("Compact observed idle attachment")}
        title={compactUnavailable ? `${t("Compact the conversation")}: ${compactUnavailable}` : pendingCompact ? `${t("Manual retry of exact compaction:")} ${t("epoch")} ${pendingCompact.request.expectedEpoch}, ${t("session")} ${pendingCompact.request.sessionId}, ${t("runtime")} ${pendingCompact.request.expectedRuntimeInstanceId}, ${t("attachment")} ${pendingCompact.request.expectedAttachmentGeneration}, ${t("request")} ${pendingCompact.request.clientRequestId}`
          : t("Compact the conversation (Ctrl+F11)")} endIcon={<kbd>Ctrl+F11</kbd>}>{t("Compact the conversation")}</Button>
      {/* Archive, as in the menu of the session in the Explorer: one with child sessions asks there whether they go with it. */}
      {archive && <Button variant="minimal" icon={<AppIcon name="archive" size={16} />} disabled={invalidEpoch || composerBusy}
        title={composerBusy ? t("Available once the session is idle.") : undefined} onClick={archive.run}>{t(archive.asks ? "Archive…" : "Archive")}</Button>}
      <Button id={active ? "expand-session-prompt" : `expand-session-prompt-${sessionId}`} variant="minimal" icon={<AppIcon name="expand" size={16} />} disabled={!!pending || invalidEpoch}
        aria-label={t("Expand prompt editor")} title={t("Edit prompt in a large window (F6)")} onClick={() => { inputRevision.current++; setExpanded(true); }} endIcon={<kbd>F6</kbd>}>{t("Expand prompt editor")}</Button>
      <Button id={active ? "insert-equation" : `insert-equation-${sessionId}`} variant="minimal" icon={<AppIcon name="sigma" size={16} />} disabled={!!pending || invalidEpoch || expanded}
        aria-label={t("Insert equation")} title={t("Write an equation and insert it in the prompt (Alt+=)")} onClick={() => promptInput.current?.editEquation()}
        endIcon={<kbd>{equationKey}</kbd>}>{t("Insert equation")}</Button>
      {(!activeChoices?.models.length || choicesNotice.includes("could not")) && <Button variant="minimal" icon={<AppIcon name="refresh" size={16} />}
        disabled={invalidEpoch || !!pending || loadingChoices} aria-label={t("Refresh composer choices")} title={t("Refresh composer choices")} onClick={loadModelChoices}>{t("Refresh composer choices")}</Button>}
      <ActiveProviderStatus epoch={epoch} onOpen={() => onOpenCatalog?.("providers")} onState={setProviderState} providers={activeChoices?.spaceProviders ?? null} />
    </> }}>
      {!pending && !expanded && !invalidEpoch && <ProjectReferencePicker text={text} edit={editText} input={promptInput} />}
      {!pending && !expanded && !invalidEpoch && <IssuePicker edit={editText} input={promptInput} openRequest={issueRequest} onAttach={attachIssues} />}
      {!pending && !expanded && !invalidEpoch && <PluginPromptPickers edit={editText} input={promptInput} sessionId={sessionId} />}
      <span className="sr-only">{t("Enter to send or enqueue while busy · Shift+Enter for a new line · {shortcut} to send now (steer)", { shortcut: navigator.platform.startsWith("Mac") ? "Cmd+Enter" : "Ctrl+Enter" })}</span>
      {(composerBusy || availableAbortRun || pendingAbortRun) && <Button intent="danger" icon={<AppIcon name="stop" size={16} fill="currentColor" />} onClick={abortRun}
        data-composer-command={active ? "abort" : undefined}
        data-unavailable={invalidEpoch ? t("Reload required.") : !pendingAbortRun && !availableAbortRun ? t("The state of the session is not known yet. Try again in a moment.") : undefined}
        disabled={invalidEpoch || !!pendingAbortRun?.inFlight || (pendingAbortRun
          ? !capability.canSubmit(pendingAbortRun.request) : !availableAbortRun || !capability.canSubmit(availableAbortRun))}
        aria-label={pendingAbortRun ? t("Retry exact cancellation request for observed run {run}", { run: pendingAbortRun.request.expectedRunId }) : t("Cancel observed run")}
        title={pendingAbortRun ? `${t("Manual retry of exact cancellation:")} ${t("epoch")} ${pendingAbortRun.request.expectedEpoch}, ${t("session")} ${pendingAbortRun.request.sessionId}, ${t("runtime")} ${pendingAbortRun.request.expectedRuntimeInstanceId}, ${t("attachment")} ${pendingAbortRun.request.expectedAttachmentGeneration}, ${t("run")} ${pendingAbortRun.request.expectedRunId}, ${t("request")} ${pendingAbortRun.request.clientRequestId}`
          : t("Stop the running turn")} />}
      <MicButton owner={speechOwner} input={promptInput} disabled={!!pending || invalidEpoch || expanded} visible={observing}
        id={active ? "dictate-prompt" : `dictate-prompt-${sessionId}`} onSend={() => submit(composerBusy)} />
      <SendSplitButton enqueue={enqueue} busy={composerBusy} onEnqueueChange={setEnqueue} optionsDisabled={invalidEpoch || !!pending}>
      <Button data-composer-command={active ? "send" : undefined}
        data-unavailable={invalidEpoch ? t("Reload required.") : !pending && !text.trim() && !images.length ? t("Type a prompt first.") : undefined}
        aria-label={t(pending ? "Retry exact request" : enqueue ? "Enqueue" : composerBusy ? "Send now (steer)" : "Send")} title={t(pending ? "Retry exact request" : enqueue ? "Enqueue until idle" : composerBusy ? "Send now (steer)" : "Send")} intent="primary" icon={<AppIcon name={pending ? "refresh" : enqueue ? "queue" : composerBusy ? "steer" : "send"} size={16} />} disabled={invalidEpoch || !!pending?.inFlight || (pending ? !capability.canSubmit(pending.request) : (images.length > 0 && (hasImages && imageCapability !== true || !activeChoices || !selected || !validSelection(activeChoices, selected))) || !sendAvailable(epoch, sessionId, text, images.length ? selected : null, images))} onClick={() => submit()} />
      </SendSplitButton>
    </ComposerSurface>
    {timelineNotices && createPortal(<>
    {heldReviewer && <ConversationHeldPanel reviewer={heldReviewer} epoch={epoch} sessionId={sessionId} running={composerBusy}
      wait={liveConnected ? currentLive.snapshot?.session?.conversationWait ?? null : null}
      canReview={() => capability.canMutate() && (inputLifetime?.current() ?? true)} onStop={abortRun}
      stopDisabled={invalidEpoch || !!pendingAbortRun?.inFlight || (pendingAbortRun
        ? !capability.canSubmit(pendingAbortRun.request) : !availableAbortRun || !capability.canSubmit(availableAbortRun))} />}
    {pendingAborts.length > 0 && <div className="retained-send-recovery">
    {(Array.isArray(page?.rows) ? page.rows : []).filter(row => row && typeof row.sessionId === "string" && row.sessionId.toLowerCase() === sessionId.toLowerCase()).map(row => <div key={row.operationId}>
      <p>{row.kind} · {t(row.outcome === "Completed" ? (row.kind === "Abort" ? "Original Send control settled; not rollback, decision retraction or run termination" : row.kind === "AbortRun" ? "Cancellation signalled; run completion is not confirmed" : row.kind === "Compact" ? "compaction settled successfully" : row.kind === "Steer" ? "steering input submitted" : "submission submitted") : row.outcome === "Failed" ? "operation failed" : row.outcome === "Cancelled" ? "operation cancelled" : "operation pending")} {row.code ?? ""} · {row.operationId}</p>
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
