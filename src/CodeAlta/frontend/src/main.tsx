import { Button, Classes, HTMLSelect, InputGroup, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext } from "@blueprintjs/core";
import { connect, onDiagnostic } from "@neoastra/client";
import { rpcFailureCode } from "./rpcDiagnostics";
import { StrictMode, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent, type RefObject, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import { ComposerStatus } from "./ComposerStatus";
import { settingsNavigation } from "./settingsNavigation";
import { colorSchemeAttribute } from "./colorSchemes";
import {
  boot, configuration, applicationLogs, modelCatalog, reminder, workspace, sessionDisplay, sessionRuntimeState, sessionPermissions, sessionOperations,
  sessionAsks, sessionNotes, sessionPluginEvents, projectGit, composerStatus, sessionUserInput, type BootStatus,
  type ReminderListRequest,
  type ReminderListResponse,
  type ReminderDetailRequest,
  type ConfigurationSnapshot, type WorkspaceSession, type WorkspaceSnapshot,
} from "#neoastra";
import { loadWorkspace, sessionsForProject, workspaceNotice, type WorkspaceState } from "./workspace";
import { History } from "./HistoryPanel";
import { readTimeline } from "./readTimeline";
import { SessionContentLayout } from "./SessionContentLayout";
import { SessionTabStrip } from "./SessionTabStrip";
import { sessionTabPresentation } from "./sessionTabLayout";
import { createReferencePopupLifetime } from "./referencePopup";
import { SessionBrowser } from "./SavedSessionBrowser";
import { ProjectArchiveDialog } from "./ProjectArchiveDialog";
import { createRuntimeObservations, maximumRuntimeRows, runtimeTarget } from "./runtimeObservations";
import { createProjectArchive } from "./projectArchive";
import { browserActivation } from "./sessionBrowser";
import { closeSessionTab, emptySessionTabs, openSessionTab, persistSessionTabs, reconcileSessionTabs, resolveSessionTab, restoreSessionTabs, selectedTab, sessionTabsKey, tabKey, type SessionTab, type SessionTabs as SessionTabsState } from "./sessionTabs";
import { activateFileTab, closeFileTab, cycleTab, emptyFileTabs, fileTabKey, fileTabName, fileTabsKey, openFileTab, persistFileTabs, reconcileFileTabs, reopenTabKind, restoreFileTabs, sameFileTab, type FileTab, type TabKind, type TabPosition } from "./fileTabs";
import { createFileEditors } from "./fileEditors";
import { OpenFileDialog } from "./OpenFileDialog";
import { ProjectFileEditor, UnsavedFileDialog } from "./ProjectFileEditor";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { ReadOnlyComposer } from "./ReadOnlyComposer";
import { useLocalDraftImages } from "./useLocalDraftImages";
import { imageLimits } from "./promptImages";
import { ModelCatalogPanel } from "./ModelCatalogPanel";
import { ProvidersPanel } from "./ProvidersPanel";
import { AgentPromptSettings } from "./AgentPromptSettings";
import { McpServerSettings } from "./McpServerSettings";
import { PluginSettings } from "./PluginSettings";
import { SkillSettings } from "./SkillSettings";
import { archivedProjectScope, ReminderScopeGate, SessionComposerGate } from "./ArchivedScopeGates";
import { RemindersDialog } from "./RemindersDialog";
import { ArchivedActionRecovery } from "./ArchivedActionRecovery";
import { createReminderActions } from "./reminderActions";
import { activeReminderCounts, sameActiveReminders, scopeReminderCount, type ActiveReminders } from "./activeReminders";
import { ReminderBadge } from "./ReminderBadge";
import { verifiedReminderCountTarget } from "./reminderListObservation";
import { applyCatalogNextSend, createNextSendSelectionStore } from "./nextSendSelection";
import { createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import { createSessionDisplayStore } from "./sessionDisplay";
import { createRuntimeStateReader } from "./runtimeState";
import { createPermissionReviewer } from "./sessionPermissions";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { AskPanel } from "./AskPanel";
import { askWireRequest, createAskActions } from "./sessionAsks";
import { SessionNotesOverlay } from "./SessionNotesOverlay";
import { RunningSessionBadge } from "./RuntimeObservation";
import { createNotesReader } from "./sessionNotes";
import { createUserInputReviewer } from "./sessionUserInput";
import { UserInputPanel } from "./UserInputPanel";
import { LiveSessionPanel } from "./LiveSessionPanel";
import { createTimelineScrollMemory, useExplicitNewestHistory, useTimelinePosition, timelineNotice, type TimelineNotice, type MessageNavigation } from "./timelineScroll";
import { workspaceEditingSelector, type ShortcutAction } from "./shortcuts";
import { createDraftIndicators, draftStorageKey, persistDraft, restoreDraft, transferPromptDraft } from "./promptDraft";
import { SessionDraftBadge } from "./SessionDraftBadge";
import { collapsedSessionWidth, constrainPaneLayout, persistPaneLayout, restorePaneLayout } from "./paneLayout";
import { composerSizeKey, rememberComposerHeight } from "./composerHeight";
import { ComposerSplitter, useComposerLayout } from "./ComposerLayout";
import { NewSessionWorkspace } from "./NewSessionWorkspace";
import { useNewSessionChoices } from "./newSessionChoices";
import { ComposerSelectionFields, ReasoningSlider, SendSplitButton } from "./ComposerSurface";
import { validSelection } from "./sessionSelection";
import { AppIcon, type IconName } from "./AppIcon";
import { AppWindow } from "./AppWindow";
import { ConfigEditorPanel } from "./ConfigEditorPanel";
import { ProviderSettings } from "./ProviderSettings";
import { sessionTime } from "./sessionTime";
import { createProjectOpening } from "./projectOpening";
import { OpenProjectDialog } from "./OpenProjectDialog";
import { savedProjectSelection } from "./savedProjectSelection";
import { createSessionCreation, createdSessionSelection, sessionCreationMessage, type SessionTarget } from "./sessionCreation";
import { createSessionRename, renamedSessionVisible, renameSelectionCurrent, sessionRenameMessage, type RenameTarget } from "./sessionRename";
import { createSessionDeletion, deletedSessionRecovery, deleteSelectionCurrent, sessionDeletionMessage, type DeletedTarget } from "./sessionDeletion";
import { batchDeleteCandidate, createSessionBatchDeletion } from "./sessionBatchDeletion";
import type { BatchDeleteControls } from "./SessionBatchDeletePanel";
import { createProjectRename, projectNameVisible, projectRenameMessage, projectRenameSelectionCurrent, type ProjectNameTarget } from "./projectRename";
import { sessionHierarchy } from "./sessionHierarchy";
import { limitSessionHierarchy } from "./recentSessions";
import { SessionTabMenu } from "./SessionTabMenu";
import { isSessionContextKey, restoreSessionMenuFocus, sessionActionAccess, type SessionAction, type SessionMenuTarget } from "./sessionRowActions";
import { projectRailProjection } from "./projectRail";
import { ProjectRailRows } from "./ProjectRailRows";
import { defaultIdeWidth, maximumIdeWidth, minimumIdeWidth, parseIdeWidth, persistIdeWidth, resizeIdeWidth } from "./ideWidth";
import { focusVisibleProject, projectRailVisible, restoreProjectRailFocus } from "./projectRailVisibility";
import { nextTheme, themeLabel, useWindowPreferences } from "./windowPreferences";
import { themeIcons } from "./GeneralSettings";
import { plainTitle } from "./sessionTitle";
import { GeneralSettings } from "./GeneralSettings";
import { createHostLiveness, hostPingInterval, hostPingTimeout } from "./hostLiveness";
import { installKeyboardClickGuard } from "./keyboardClickGuard";
import { closeApplicationWindow, logoUrl, useWindowTitleBar, WindowBrand, WindowControls } from "./windowChrome";
import { createPluginEventsRead } from "./pluginEvents";
import { ProjectContext } from "./ProjectContext";
import type { ComposerChromeValue } from "./composerChrome";
import { ShellLanguageContext, useLanguagePreference, useShellLanguage } from "./shellLanguage";
import { workflowNotice, type WorkflowNotice } from "./workflowNotice";
import { translate, type MessageKey } from "./localization";
import { ApplicationLogsPanel } from "./ApplicationLogsPanel";
import { AboutSettings } from "./AboutDialog";
import type { ProjectDetailsContext } from "./ProjectDetailsEntry";
import { createApplicationLogClearActions } from "./applicationLogClear";
import { SessionInfoDialog, type SessionInfoLifetime } from "./SessionInfoDialog";
import { selectedSessionInfoAvailable, selectedSessionInfoSelection, sessionInfoView } from "./sessionInfo";
import { CommandPalette } from "./CommandPalette";
import { CommandHelp } from "./CommandHelp";
import { resolveCommandKey, type CommandId } from "./commandRegistry";
import { createPaletteFocusRestoration } from "./paletteActions";
import "normalize.css";
import "@blueprintjs/core/lib/css/blueprint.css";
import "flexlayout-react/style/light.css";
import "./colorSchemes.gen.css";
import "./style.css";

type TimelineCommand = Readonly<{ sessionId: string; projectId: string | null; epoch: string | null;
  ready: () => boolean; navigate: (action: MessageNavigation) => void;
  latestReady: () => boolean; latest: () => void; cancelLatest: () => void }>;

const demoMode = import.meta.env.VITE_DEMO_MODE === "true";
type View = "workspace" | "appearance" | "providers" | "models" | "prompts" | "mcp" | "logs" | "skills" | "plugins" | "about" | "config";
type SettingsSection = Exclude<View, "workspace">;
const paneLayoutStorageKey = "codealta.desktop.panes.v1";

function App() {
  const language = useLanguagePreference();
  const t = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate(language.locale, key, parameters);
  const [batchDeletion] = useState(() => createSessionBatchDeletion(workspace.deleteSession));
  const batchDeletionState = useSyncExternalStore(batchDeletion.subscribe, batchDeletion.getSnapshot);
  useEffect(() => () => batchDeletion.invalidate(), [batchDeletion]);
  const [runtimeObservations] = useState(() => createRuntimeObservations(sessionRuntimeState.observe));
  useEffect(() => () => runtimeObservations.invalidate(), [runtimeObservations]);
  // Original create presentation authority only; never a backend receipt or retry grant.
  const creationGeneration = useRef(0);
  const commandGeneration = useRef(0);
  const [, publishModalGeneration] = useState(0);
  // Saved-browser/palette captures are invalidated synchronously at catalog,
  // host and navigation transitions, including ABA before React commits.
  const browserRevision = useRef(0);
  const [, renderInfoRevision] = useState(0);
  // Same-value/ABA state updates may otherwise skip a render and leave a canceled
  // Info read looking loading. Keep the existing synchronous capture revision visible.
  function advanceBrowserRevision() { batchDeletion.invalidate(); renderInfoRevision(++browserRevision.current); }
  const [browserCapture, setBrowserCapture] = useState<{ snapshot: WorkspaceSnapshot; projectId: string | null; revision: number; hostReady: boolean } | null>(null);
  const invalidateCreation = (paletteTransition = false) => { creationGeneration.current++; if (!paletteTransition) commandGeneration.current++; runtimeObservations.invalidate(); };
  const [logClearActions] = useState(() => createApplicationLogClearActions(applicationLogs.clear));
  const [status, writeStatus] = useState<BootStatus>();
  function setStatus(value: BootStatus) { advanceBrowserRevision(); invalidateCreation(); writeStatus(value); }
  const [error, setError] = useState<string>();
  const [hostLiveness] = useState(() => createHostLiveness(() => boot.status({}, { timeoutMilliseconds: hostPingTimeout })));
  const hostSilent = useSyncExternalStore(hostLiveness.subscribe, hostLiveness.getSnapshot);
  const windowSnapshot = useWindowTitleBar();
  const [workspaceState, setWorkspaceState] = useState<WorkspaceState>({ kind: "loading" });
  const currentSnapshot = useRef<WorkspaceSnapshot | undefined>(undefined);
  const projectInspection = useRef({ version: 0, ready: false });
  const [, setProjectInspectionVersion] = useState(0);
  function markProjectInspection(ready: boolean) {
    projectInspection.current = { version: projectInspection.current.version + 1, ready };
    setProjectInspectionVersion(projectInspection.current.version);
  }
  function publishWorkspaceState(value: WorkspaceState) {
    advanceBrowserRevision();
    invalidateCreation();
    currentSnapshot.current = value.kind === "ready" ? value.snapshot : undefined;
    markProjectInspection(value.kind === "ready");
    setWorkspaceState(value);
  }
  const [projectId, writeProjectId] = useState<string | null>(null);
  const [sessionId, writeSessionId] = useState<string | null>(null);
  const [restoredTabs] = useState(() => restoreSessionTabs(() => localStorage.getItem(sessionTabsKey)));
  const [tabs, setTabs] = useState(restoredTabs ?? emptySessionTabs);
  const [tabsReady, setTabsReady] = useState(false);
  // File editor tabs share the strip with the sessions. An active file is shown over the session selection,
  // which stays as it is; selecting a session, a project or the new-session tab leaves the file.
  const [restoredFileTabs] = useState(() => restoreFileTabs(() => localStorage.getItem(fileTabsKey)));
  const [fileTabs, setFileTabs] = useState(emptyFileTabs);
  const [fileEditors] = useState(createFileEditors);
  useSyncExternalStore(fileEditors.subscribe, fileEditors.snapshot);
  const [fileClosing, setFileClosing] = useState<{ tab: FileTab; busy: boolean } | null>(null);
  // The kinds of tab closed, oldest first: Reopen restores the most recent one.
  const closedTabKinds = useRef<TabKind[]>([]);
  // Scope-local text is App-owned even when storage is denied or workspace DOM is unmounted.
  const localDrafts = useRef(new Map<string, { text: string; revision: number }>());
  const localImageGeneration = useRef(0);
  const [, renderLocalDraft] = useState(0);
  const draftScope = `local-draft:${JSON.stringify(projectId)}`;
  const localDraftStorageKey = `codealta.desktop.localPrompt.${JSON.stringify(projectId)}`;
  if (!localDrafts.current.has(draftScope)) localDrafts.current.set(draftScope,
    { text: restoreDraft(() => localStorage.getItem(localDraftStorageKey), draftScope), revision: 0 });
  const localDraft = localDrafts.current.get(draftScope)!;  const [draftHandoffNotice, setDraftHandoffNotice] = useState("");
  // A prompt typed in the New session tab is sent as soon as its session exists and holds the text.
  const autoSend = useRef<{ sessionId: string; text: string } | null>(null);

  function editLocalDraft(text: string) {
    const current = localDrafts.current.get(draftScope)!;
    localDrafts.current.set(draftScope, { text, revision: current.revision + 1 });
    persistDraft((_key, value) => localStorage.setItem(localDraftStorageKey, value), () => localStorage.removeItem(localDraftStorageKey), draftScope, text);
    renderLocalDraft(value => value + 1);
  }
  const tabFocusPending = useRef(false);
  function setProjectId(value: string | null) { advanceBrowserRevision(); invalidateCreation(); writeProjectId(value); activateFile(null); }
  function setSessionId(value: string | null) { advanceBrowserRevision(); invalidateCreation(); writeSessionId(value); activateFile(null); }
  const [composerHeights, setComposerHeights] = useState<ReadonlyMap<string, number>>(() => new Map());
  const [view, setView] = useState<View>("workspace");
  const currentView = useRef<View>(view);
  currentView.current = view;
  const [settingsOpen, setSettingsOpen] = useState(false);
  const settingsVisible = useRef(false);
  const [settingsSection, setSettingsSection] = useState<SettingsSection>("appearance");
  const currentSettingsSection = useRef<SettingsSection>(settingsSection);
  currentSettingsSection.current = settingsSection;
  const settingsOrigin = useRef<HTMLElement | null>(null);
  const settingsOriginView = useRef<View>("workspace");
  const [focusRestoration] = useState(createPaletteFocusRestoration);
  useEffect(() => () => focusRestoration.cancel(), [focusRestoration]);
  function closeSettings() {
    invalidateCreation();
    settingsVisible.current = false;
    setSettingsOpen(false);
    const origin = settingsOrigin.current;
    focusRestoration.schedule(origin, () => currentView.current === settingsOriginView.current,
      () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'));
  }
  function navigate(next: View) {
    advanceBrowserRevision();
    invalidateCreation();
    focusRestoration.cancel();
    if (next === "workspace") {
      settingsVisible.current = false;
      setSettingsOpen(false);
      currentView.current = next;
      setView(next);
    } else {
      if (!settingsVisible.current) {
        settingsOrigin.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
        settingsOriginView.current = currentView.current;
      }
      currentSettingsSection.current = next;
      setSettingsSection(next);
      settingsVisible.current = true;
      setSettingsOpen(true);
    }
  }
  // A control outside the shell (a composer status item) asks for a Settings page by name.
  const openSettingsPage = useRef<(page: string) => void>(() => {});
  openSettingsPage.current = page => { if (page === "mcp" || page === "plugins" || page === "providers" || page === "skills") navigate(page); };
  useEffect(() => settingsNavigation.subscribe(page => openSettingsPage.current(page)), []);
  const [search, writeSearch] = useState("");
  function setSearch(value: string) { invalidateCreation(); writeSearch(value); }
  const [projectFilter, setProjectFilter] = useState("");
  const { projectSort, setProjectSort, theme, shownTheme, setTheme, colorScheme, setColorScheme, railState, setDesktopCollapsed, toggleRail, closeNarrowRail, notices: preferenceNotices, recentSessionCount, setRecentSessionCount } = useWindowPreferences();
  const [sessionExpansion, setSessionExpansion] = useState<{ projectId: string | null; search: string; extra: number } | null>(null);
  const [notesVisible, setNotesVisible] = useState(true);
  const [dialog, writeDialog] = useState<"project" | "help" | "sessions" | "archive" | "reminders" | "file" | null>(null);
  function setDialog(value: typeof dialog) { batchDeletion.invalidate(); invalidateCreation(); writeDialog(value); }
  const helpOrigin = useRef<{ element: HTMLElement | null; view: View; sessionId: string | null; scope: string | null } | null>(null);
  const [paletteOpen, writePaletteOpen] = useState(false);
  function setPaletteOpen(value: boolean) { invalidateCreation(true); writePaletteOpen(value); }
  const paletteOrigin = useRef<HTMLElement | null>(null);
  const palettePending = useRef<CommandId | null>(null);
  // True after Ctrl+G, until the second stroke of the chord.
  const commandChord = useRef(false);
  const [configurationState, setConfigurationState] = useState<{ snapshot?: ConfigurationSnapshot; error?: string }>({});
  const initialSelectionMade = useRef(false);
  useEffect(() => {
    if (demoMode) return;
    const lifetime = new AbortController();
    const unsubscribe = onDiagnostic(value => {
      if (value.level === "warning" || value.level === "error")
        console.warn("[CodeAlta RPC] transport diagnostic", { code: rpcFailureCode(value) });
    });
    void connect().then(connection => {
      if (lifetime.signal.aborted) return;
      const closed = () => console.warn("[CodeAlta RPC] connection closed; inspect receipts before any explicit retry. No automatic replay.");
      if (connection.closed.aborted) closed();
      else connection.closed.addEventListener("abort", closed, { once: true, signal: lifetime.signal });
    }).catch(error => { if (!lifetime.signal.aborted) console.warn("[CodeAlta RPC] connection unavailable", { code: rpcFailureCode(error) }); });
    return () => { lifetime.abort(); unsubscribe(); };
  }, []);
  const [submissions] = useState(() => createOwnedSubmissions(sessionOperations.send, sessionOperations.abort));
  const reminderCapability = useRef<ReturnType<typeof createMutationCapability> | undefined>(undefined);
  const [reminderActions] = useState(() => createReminderActions(reminder.create, reminder.delete, (target, reply) => {
    const capability = reminderCapability.current;
    if (capability?.canSubmit({ expectedEpoch: target.epoch })) capability.observe(reply);
  }, reminder.save));
  const [providerProbeHolds] = useState(() => new Set<string>());
  const [draftIndicators] = useState(createDraftIndicators);
  const [nextSendSelections] = useState(() => createNextSendSelectionStore(
    key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value)));
  useSyncExternalStore(draftIndicators.subscribe, draftIndicators.snapshot);
  const [steering] = useState(() => createSteeringSubmissions(sessionOperations.steer));
  const [compaction] = useState(() => createCompactionSubmissions(sessionOperations.compact));
  const [abortRuns] = useState(() => createAbortRunSubmissions(sessionOperations.abortRun));
  const [queue] = useState(() => createQueueSubmissions(sessionOperations.queue, sessionOperations.cancelQueue));
  const [askActions] = useState(() => createAskActions(
    request => sessionAsks.answer(askWireRequest(request), { timeoutMilliseconds: 8000 }),
    request => sessionAsks.cancel(askWireRequest(request), { timeoutMilliseconds: 8000 })));
  const [sessionPaneOwners] = useState(() => new Map<string, ReturnType<typeof createSessionPaneOwners>>());
  const [scrollMemory] = useState(createTimelineScrollMemory);
  const [projectOpening] = useState(() => createProjectOpening(workspace.openProject));
  const [projectArchive] = useState(() => createProjectArchive(workspace.archiveProject));
  const [projectRename] = useState(() => createProjectRename(workspace.readProjectName, workspace.renameProject));
  const [projectRenameTarget, setProjectRenameTarget] = useState<ProjectNameTarget | null>(null);
  const [projectRenameName, setProjectRenameName] = useState("");
  const [projectRenameBusy, setProjectRenameBusy] = useState(false);
  const [projectRenameConflict, setProjectRenameConflict] = useState(false);
  const [projectRenameNotice, setProjectRenameNotice] = useState<WorkflowNotice>("");
  const projectRenamePending = useRef(false);
  const projectRenameRefreshPending = useRef(false);
  const projectRenameGeneration = useRef(0);
  const uncertainProjectRename = useRef<{ target: ProjectNameTarget; name: string } | null>(null);
  const [projectRenameLocked, setProjectRenameLocked] = useState(false);
  const [createSession] = useState(() => createSessionCreation(workspace.createSession));
  const [renameSession] = useState(() => createSessionRename(workspace.renameSession));
  const [deleteSession] = useState(() => createSessionDeletion(workspace.deleteSession));
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [deletingConfirmation, setDeletingConfirmation] = useState("");
  const [deletingBusy, setDeletingBusy] = useState(false);
  const [deletingMessage, setDeletingMessage] = useState("");
  const deletingPending = useRef(false);
  const uncertainDelete = useRef<DeletedTarget | null>(null);
  const [deleteLocked, setDeleteLocked] = useState(false);
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renamingTitle, setRenamingTitle] = useState("");
  const [renamingBusy, setRenamingBusy] = useState(false);
  const [renamingMessage, setRenamingMessage] = useState<WorkflowNotice>("");
  const renamingPending = useRef(false);
  const uncertainRename = useRef<{ id: string; title: string; target: RenameTarget } | null>(null);
  const [renameLocked, setRenameLocked] = useState(false);
  const selectedSessionId = useRef<string | null>(null);
  const [menuTarget, setMenuTarget] = useState<SessionMenuTarget | null>(null);
  const menuSnapshot = useRef<WorkspaceSnapshot | undefined>(undefined);
  const menuSelection = useRef<string | null>(null);
  const menuOrigin = useRef<HTMLButtonElement>(null);
  const focusAction = useRef<"rename" | "delete" | null>(null);
  const [creatingVisible, writeCreatingVisible] = useState(false);
  const [sessionOptionsOpen, setSessionOptionsOpen] = useState(false);
  const [creatingTitle, writeCreatingTitle] = useState("");
  const [creatingProvider, writeCreatingProvider] = useState("");
  function setCreatingProvider(value: string) { invalidateCreation(); writeCreatingProvider(value); }
  function setCreatingVisible(value: boolean | ((previous: boolean) => boolean)) { invalidateCreation(); writeCreatingVisible(value); }
  function setCreatingTitle(value: string) { invalidateCreation(); writeCreatingTitle(value); }
  const [creatingBusy, setCreatingBusy] = useState(false);
  const [creationLocked, setCreationLocked] = useState(false);
  const creationHeld = useRef(false);
  const [creatingMessage, setCreatingMessage] = useState<WorkflowNotice>("");
  const creationPending = useRef(false);
  const creationAlive = useRef(true);
  const creationRefresh = useRef(new AbortController());
  const selectedScope = useRef<string | null>(null);
  function openHelp() {
    if (paletteOpen || dialog || settingsVisible.current || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    focusRestoration.cancel();
    helpOrigin.current = { element: document.activeElement instanceof HTMLElement ? document.activeElement : null,
      view: currentView.current, sessionId: selectedSessionId.current, scope: selectedScope.current };
    setDialog("help");
  }
  function closeHelp() {
    const origin = helpOrigin.current;
    setDialog(null);
    focusRestoration.schedule(origin?.element ?? null, () => currentView.current === origin?.view &&
      selectedSessionId.current === origin.sessionId && selectedScope.current === origin.scope,
      () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'));
  }
  useEffect(() => {
    creationAlive.current = true;
    creationRefresh.current = new AbortController();
    // Child-owned native dialogs (Info, Details, expanded editor) also end the original
    // presentation lifetime, even if opened and closed before the create reply arrives.
    const modalTransition = (event: Event) => {
      if (!(event.target instanceof HTMLDialogElement)) return;
      invalidateCreation(event.target.classList.contains("command-palette"));
      // Native child dialogs do not otherwise update App state. Publish the existing
      // synchronous fence now, not on the next unrelated (e.g. locale) render.
      // A normal state update also works during child layout effects; do not flushSync.
      publishModalGeneration(creationGeneration.current);
    };
    document.addEventListener("beforetoggle", modalTransition, true);
    return () => {
      invalidateCreation(); creationAlive.current = false; creationRefresh.current.abort();
      document.removeEventListener("beforetoggle", modalTransition, true);
    };
  }, []);
  const [mutation, setMutation] = useState<{ epoch: string; capability: ReturnType<typeof createMutationCapability> }>();
  reminderCapability.current = mutation?.capability;
  const subscribeReminderCapability = useCallback((listener: () => void) =>
    mutation?.capability.subscribe(listener) ?? (() => {}), [mutation?.capability]);
  useSyncExternalStore(subscribeReminderCapability, () => mutation?.capability.canMutate() ?? false);
  const readReminders = useCallback((request: ReminderListRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) =>
    reminder.list(request, options).then(value => { mutation?.capability.observe(value); return value; }), [mutation?.capability]);
  const readReminderDetail = useCallback((request: ReminderDetailRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) =>
    reminder.detail(request, options).then(value => { mutation?.capability.observe(value); return value; }), [mutation?.capability]);
  const workspaceShell = useRef<HTMLDivElement>(null);
  const projectRail = useRef<HTMLElement>(null);
  const projectRailToggle = useRef<HTMLButtonElement>(null);
  const focusProjectPending = useRef(false);
  const projectFilterInput = useRef<HTMLInputElement>(null);
  const sessionRail = useRef<HTMLElement>(null);
  const sessionInfoTrigger = useRef<HTMLButtonElement>(null);
  const remindersTrigger = useRef<HTMLButtonElement>(null);
  const remindersOrigin = useRef<{ element: HTMLButtonElement; lifetime: SessionInfoLifetime } | null>(null);
  const compactTrigger = useRef<HTMLButtonElement>(null);
  const searchInput = useRef<HTMLInputElement>(null);
  const timelineCommand = useRef<TimelineCommand | null>(null);
  const [paneLayout] = useState(() => restorePaneLayout(() => localStorage.getItem(paneLayoutStorageKey), window.innerWidth));
  const [workspaceWidth, setWorkspaceWidth] = useState(window.innerWidth);
  const [narrow, setNarrow] = useState(() => window.matchMedia("(max-width: 875px)").matches);
  const [ideWidth, setIdeWidth] = useState(() => {
    try { return parseIdeWidth(localStorage.getItem("codealta.desktop.ide-width.v1")); } catch { return parseIdeWidth(null); }
  });
  const [widthSaved, setWidthSaved] = useState(true);
  useEffect(() => { setWidthSaved(persistIdeWidth(value => localStorage.setItem("codealta.desktop.ide-width.v1", value), ideWidth)); }, [ideWidth]);
  const railVisible = projectRailVisible(railState, narrow);
  const detailsPaneVisible = !(narrow && railVisible);
  const currentDetailsPaneVisible = useRef(detailsPaneVisible);
  currentDetailsPaneVisible.current = detailsPaneVisible;
  const visiblePaneLayout = constrainPaneLayout(paneLayout, workspaceWidth);
  const visibleSessionWidth = !narrow && railState.desktopCollapsed
    ? collapsedSessionWidth(paneLayout, workspaceWidth) : visiblePaneLayout.sessions;
  const [clock, setClock] = useState(Date.now);

  useLayoutEffect(() => {
    document.documentElement.dataset.theme = shownTheme;
    document.documentElement.classList.toggle(Classes.DARK, shownTheme === "dark");
    const scheme = colorSchemeAttribute(colorScheme);
    if (scheme) document.documentElement.dataset.colorScheme = scheme;
    else delete document.documentElement.dataset.colorScheme;
  }, [shownTheme, colorScheme]);

  useEffect(() => {
    const timer = window.setInterval(() => setClock(Date.now()), 60_000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    persistPaneLayout(value => localStorage.setItem(paneLayoutStorageKey, value), paneLayout);
  }, [paneLayout]);


  useEffect(() => {
    const media = window.matchMedia("(max-width: 875px)");
    const changed = () => {
      const next = media.matches;
      if (next || railState.desktopCollapsed)
        restoreProjectRailFocus(projectRail.current, document.activeElement, projectRailToggle.current);
      focusProjectPending.current = false;
      closeNarrowRail();
      setNarrow(next);
    };
    media.addEventListener("change", changed);
    if (media.matches !== narrow) changed();
    return () => media.removeEventListener("change", changed);
  }, [railState.desktopCollapsed, narrow]);

  useLayoutEffect(() => {
    if (!railVisible || !focusProjectPending.current || view !== "workspace") return;
    if (focusVisibleProject(projectRail.current, projectFilterInput.current)) focusProjectPending.current = false;
  }, [railVisible, view, workspaceState.kind]);

  useEffect(() => {
    if (view !== "workspace" || !workspaceShell.current) return;
    const shell = workspaceShell.current;
    const measure = () => setWorkspaceWidth(shell.clientWidth);
    const observer = new ResizeObserver(measure);
    observer.observe(shell);
    measure();
    return () => observer.disconnect();
  }, [view]);

  useEffect(() => {
    const abort = new AbortController();
    void boot.status({}, { signal: abort.signal, timeoutMilliseconds: 8_000 })
      .then(value => {
        if (abort.signal.aborted) return;
        setStatus(value);
        setMutation(current => current?.epoch === value.hostEpoch ? current
          : value.hostEpoch ? { epoch: value.hostEpoch, capability: createMutationCapability(value.hostEpoch) } : undefined);
      })
      .catch(() => { if (!abort.signal.aborted) setError("The desktop bridge could not be initialized. Close the window and try again."); });
    void loadWorkspace(workspace.snapshot, abort.signal, publishWorkspaceState);
    void configuration.snapshot({}, { signal: abort.signal, timeoutMilliseconds: 8_000 })
      .then(value => { if (!abort.signal.aborted) setConfigurationState({ snapshot: value }); })
      .catch(() => { if (!abort.signal.aborted) setConfigurationState({ error: "Configuration inventory is unavailable." }); });
    return () => abort.abort();
  }, []);
  const hostAnswered = !!status;
  useEffect(() => {
    // Watch only a host that answered once; a window that never connected already says so.
    if (demoMode || !hostAnswered) return;
    const timer = setInterval(() => void hostLiveness.check(), hostPingInterval);
    return () => clearInterval(timer);
  }, [hostAnswered, hostLiveness]);
  // After a configuration save re-registered providers, re-read the inventory that pickers and settings show.
  function refreshConfiguration() {
    return configuration.snapshot({}, { timeoutMilliseconds: 8_000 })
      .then(value => { if (creationAlive.current) setConfigurationState({ snapshot: value }); })
      .catch(() => { /* The previous inventory stays visible. */ });
  }

  const snapshot = workspaceState.kind === "ready" ? workspaceState.snapshot : undefined;
  const projectListing = snapshot ? projectRailProjection(snapshot, projectFilter, projectSort) : null;
  useEffect(() => {
    if (!snapshot || initialSelectionMade.current) return;
    initialSelectionMade.current = true;
    setTabsReady(true);
    try {
      if (restoredTabs) {
        applyTabState(reconcileSessionTabs(restoredTabs, snapshot));
        return;
      }
      const firstSession = snapshot.sessions[0];
      if (!firstSession) return;
      const project = snapshot.projects.find(value => value.path === firstSession.workspacePath);
      selectedScope.current = project?.id ?? null;
      setProjectId(project?.id ?? null);
      setSessionId(firstSession.id);
      selectedSessionId.current = firstSession.id;
    } finally {
      // After the session selection above, which leaves any file: the restored file tab stays active.
      if (restoredFileTabs) setFileTabs(reconcileFileTabs(restoredFileTabs, snapshot));
    }
  }, [snapshot]);

  useEffect(() => {
    if (!snapshot || !tabsReady) return;
    const valid = reconcileSessionTabs(tabs, snapshot);
    // A previously verified active identity disappearing must not silently bind
    // the same ID to a different project/path or a duplicate catalog row.
    if (tabs.active && !resolveSessionTab(snapshot, tabs.active) && tabs.active.sessionId === sessionId) {
      const file = fileTabs.active;
      applyTabState(valid);
      if (file) activateFile(file);
      return;
    }
    const selection = selectedTab(snapshot, projectId, sessionId);
    const next = selection ? openSessionTab(valid, selection) : valid.active ? { ...valid, active: null } : valid;
    if (next !== tabs) setTabs(next);
  }, [snapshot, projectId, sessionId, tabsReady, tabs]);
  useEffect(() => {
    if (tabsReady && snapshot) persistSessionTabs(value => localStorage.setItem(sessionTabsKey, value), tabs);
  }, [tabs, tabsReady, snapshot]);
  useEffect(() => {
    if (tabsReady && snapshot) persistFileTabs(value => localStorage.setItem(fileTabsKey, value), fileTabs);
  }, [fileTabs, tabsReady, snapshot]);

  function applyTabState(next: SessionTabsState) {
    setTabs(next);
    const target = next.active;
    setMenuTarget(null); focusAction.current = null;
    if (target && selectedScope.current !== target.projectId) selectProject(target.projectId, target.sessionId);
    else { selectedSessionId.current = target?.sessionId ?? null; setSessionId(target?.sessionId ?? null); }
  }
  function selectSessionTab(tab: SessionTab) {
    if (!snapshot || snapshot !== currentSnapshot.current || !resolveSessionTab(snapshot, tab)) return;
    applyTabState(openSessionTab(reconcileSessionTabs(tabs, snapshot), tab));
  }
  function activateFile(tab: FileTab | null) { setFileTabs(state => activateFileTab(state, tab)); }
  function openFile(tab: FileTab) {
    // At the tab limit a file with unsaved edits is never the one that makes room.
    setFileTabs(state => openFileTab(state, tab, value => fileEditors.dirty(fileTabKey(value))));
  }
  function closeFile(tab: FileTab, discard = false) {
    if (!discard && fileEditors.dirty(fileTabKey(tab))) { activateFile(tab); setFileClosing({ tab, busy: false }); return; }
    setFileClosing(null);
    closedTabKinds.current = [...closedTabKinds.current, "file" as const].slice(-64);
    tabFocusPending.current = true;
    setFileTabs(state => closeFileTab(state, tab));
  }
  async function saveAndCloseFile(tab: FileTab) {
    setFileClosing({ tab, busy: true });
    const saved = await fileEditors.save(fileTabKey(tab));
    if (!creationAlive.current) return;
    // A refused save keeps the tab: its editor shows why.
    if (saved) closeFile(tab, true); else setFileClosing(null);
  }
  function openFilePicker() {
    if (dialog || paletteOpen || !owned || !selectedProject || selectedProject.archived || currentView.current !== "workspace"
      || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    setDialog("file");
  }
  function captureTabLifetime() {
    const revision = browserRevision.current;
    const epoch = currentHostEpoch.current;
    const scope = selectedScope.current;
    const selected = selectedSessionId.current;
    const generation = creationGeneration.current;
    return () => revision === browserRevision.current && epoch === currentHostEpoch.current
      && generation === creationGeneration.current && scope === selectedScope.current && selected === selectedSessionId.current
      && snapshot === currentSnapshot.current && currentView.current === "workspace" && !settingsVisible.current
      && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]');
  }
  function captureReferenceLifetime() {
    const catalog = currentSnapshot.current;
    return createReferencePopupLifetime(() => ({
      generation: creationGeneration.current, revision: browserRevision.current,
      key: JSON.stringify([currentHostEpoch.current, selectedScope.current, selectedSessionId.current]),
      available: creationAlive.current && !!catalog && catalog === currentSnapshot.current
        && currentView.current === "workspace" && !settingsVisible.current && !!mutation?.capability.canMutate(),
    }));
  }
  function openSessionBrowser() {
    if (!currentSnapshot.current || view !== "workspace" || settingsVisible.current || dialog || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    setBrowserCapture({ snapshot: currentSnapshot.current, projectId: selectedScope.current, revision: browserRevision.current,
      hostReady: mutation?.capability.canMutate() ?? false });
    setDialog("sessions");
  }

  function runtimeObservationControls() {
    const enabled = owned && !!currentHostEpoch.current && !!mutation?.capability.canMutate() && !settingsVisible.current && currentView.current === "workspace";
    return { store: runtimeObservations, enabled, canObserve: (tab: SessionTab) => !!runtimeTarget(currentSnapshot.current, tab, currentHostEpoch.current ?? undefined), refresh: (requested: readonly SessionTab[]) => {
      if (!enabled || settingsVisible.current || currentView.current !== "workspace") return;
      const targets = requested.slice(0, maximumRuntimeRows).map(tab => runtimeTarget(currentSnapshot.current, tab, currentHostEpoch.current ?? undefined)).filter(target => target !== null);
      return runtimeObservations.refresh(targets, requested.length - targets.length);
    } };
  }

  function batchDeleteControls(): BatchDeleteControls {
    const capability = mutation?.capability;
    const epoch = currentHostEpoch.current ?? "";
    const captured = browserCapture;
    const available = () => !!captured && captured.revision === browserRevision.current && currentSnapshot.current === captured.snapshot
      && currentHostEpoch.current === epoch && owned && !!epoch && !!mutation?.capability.canMutate()
      && currentView.current === "workspace" && !settingsVisible.current && !deletingPending.current && !uncertainDelete.current
      && !deleteLocked && !renamingPending.current && !uncertainRename.current && !renameLocked;
    return { owner: batchDeletion, epoch, canReview: available(), review: requests => {
      if (!available() || !captured) return false;
      const exact = requests.every(request => {
        const verified = batchDeleteCandidate(captured.snapshot, { sessionId: request.sessionId, projectId: request.projectId, path: request.projectPath ?? "" }, epoch);
        return verified && Object.keys(verified).every(key => verified[key as keyof typeof verified] === request[key as keyof typeof request]);
      });
      return exact && !!capability && batchDeletion.review(requests, available, capability);
    } };
  }
  function captureInfoLifetime(): SessionInfoLifetime {
    const revision = browserRevision.current;
    return { revision, current: () => revision === browserRevision.current && currentView.current === "workspace" && !settingsVisible.current };
  }
  function tabCommand(action: "nextTab" | "previousTab" | "closeTab" | "reopenTab") {
    if (!snapshot || view !== "workspace" || settingsVisible.current) return;
    const valid = reconcileSessionTabs(tabs, snapshot);
    if (action === "closeTab") {
      if (fileTabs.active) closeFile(fileTabs.active);
      else if (valid.active) {
        closedTabKinds.current = [...closedTabKinds.current, "session" as const].slice(-64);
        tabFocusPending.current = true; applyTabState(closeSessionTab(valid, valid.active));
      }
    } else if (action === "reopenTab") {
      const kind = reopenTabKind(closedTabKinds.current, valid.closed.length, fileTabs.closed.length);
      if (!kind) return;
      const at = closedTabKinds.current.lastIndexOf(kind);
      if (at >= 0) closedTabKinds.current = closedTabKinds.current.filter((_kind, index) => index !== at);
      tabFocusPending.current = true;
      if (kind === "file") openFile(fileTabs.closed.at(-1)!); else selectSessionTab(valid.closed.at(-1)!);
    } else if (valid.open.length + fileTabs.open.length) {
      tabFocusPending.current = true;
      const file = fileTabs.open.findIndex(tab => sameFileTab(tab, fileTabs.active));
      const session = valid.open.findIndex(tab => valid.active && tabKey(tab) === tabKey(valid.active));
      const current: TabPosition = file >= 0 ? { kind: "file", index: file } : session >= 0 ? { kind: "session", index: session } : { kind: "draft" };
      const next = cycleTab(valid.open.length, fileTabs.open.length, current, action === "nextTab" ? 1 : -1);
      if (next.kind === "file") activateFile(fileTabs.open[next.index]);
      else if (next.kind === "session") selectSessionTab(valid.open[next.index]);
      else applyTabState({ ...valid, active: null });
    }
  }
  useLayoutEffect(() => {
    if (!tabFocusPending.current) return;
    tabFocusPending.current = false;
    if (view === "workspace" && !settingsVisible.current)
      document.querySelector<HTMLButtonElement>('.session-tabs [role="tab"][aria-selected="true"], .session-tabs > button')?.focus();
  }, [tabs, fileTabs, view]);

  const sessions = snapshot ? sessionsForProject(snapshot, projectId) : [];
  const loadedSessionRows = snapshot ? sessionHierarchy(sessions, snapshot.sessions, search, projectId) : [];
  useEffect(() => { setSessionExpansion(null); }, [projectId, search]);
  const extraSessions = sessionExpansion?.projectId === projectId && sessionExpansion.search === search ? sessionExpansion.extra : 0;
  const visibleSessionRows = limitSessionHierarchy(loadedSessionRows, recentSessionCount + extraSessions, sessionId);
  const visibleSessions = visibleSessionRows.map(row => row.session);
  const autoStatusRefresh = useRef<() => Promise<void> | undefined>(() => undefined);
  autoStatusRefresh.current = () => {
    if (!snapshot || document.visibilityState === "hidden") return;
    const candidates = [...visibleSessions, ...snapshot.sessions.filter(row => tabs.open.some(tab => tab.sessionId === row.id)), ...snapshot.sessions];
    const seen = new Set<string>();
    const observed = candidates.flatMap(row => {
      if (seen.has(row.id)) return [];
      seen.add(row.id);
      const tab = selectedTab(snapshot, row.scopeKind === "project" ? row.projectId : null, row.id);
      return tab && runtimeTarget(snapshot, tab, status?.hostEpoch ?? undefined) ? [tab] : [];
    });
    return runtimeObservationControls().refresh(observed);
  };
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const refresh = async () => {
      try { await autoStatusRefresh.current(); }
      finally { if (!stopped) timer = setTimeout(refresh, 5000); }
    };
    void refresh();
    return () => { stopped = true; clearTimeout(timer); };
  }, [status?.hostAvailable, status?.hostEpoch]);
  const selectedSession = snapshot && tabs.active?.sessionId === sessionId && !resolveSessionTab(snapshot, tabs.active)
    ? undefined : snapshot?.sessions.find(value => value.id === sessionId);
  const selectedProject = snapshot?.projects.find(value => value.id === projectId);
  function currentProjectDetailsContext(): ProjectDetailsContext {
    return { snapshot: currentSnapshot.current, projectId: selectedScope.current, sessionId: selectedSessionId.current,
      hostEpoch: currentHostEpoch.current ?? null, hostAvailable: currentHostAvailable.current,
      refreshVersion: projectInspection.current.version, refreshReady: projectInspection.current.ready,
      active: currentView.current === "workspace" && currentDetailsPaneVisible.current };
  }
  const currentProjectWritable = () => projectId === null || !!selectedProject && !selectedProject.archived
    && !!savedProjectSelection(selectedProject, currentSnapshot.current);
  selectedScope.current = projectId;
  selectedSessionId.current = sessionId;
  const activeMenu = menuTarget && view === "workspace" && !settingsOpen && !dialog
    && snapshot === menuSnapshot.current && sessionId === menuSelection.current
    && menuTarget.projectId === projectId && menuTarget.hostEpoch === (status?.hostEpoch ?? null)
    && selectedSessionId.current === sessionId && selectedScope.current === projectId
    && visibleSessions.some(session => session.id === menuTarget.id)
    && snapshot?.sessions.filter(session => session.id === menuTarget.id).length === 1 ? menuTarget : null;
  useLayoutEffect(() => {
    if (menuTarget || !focusAction.current || narrow && railVisible) return;
    const field = sessionRail.current?.querySelector<HTMLInputElement>(focusAction.current === "rename" ? ".session-rename input" : ".session-delete input");
    if (field) { field.focus(); focusAction.current = null; }
  }, [menuTarget, renamingId, deletingId, narrow, railVisible]);
  useEffect(() => { if (menuTarget && !activeMenu) setMenuTarget(null); }, [menuTarget, activeMenu]);
  const notice = snapshot ? workspaceNotice(snapshot) : null;
  const owned = !!(status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch);
  const draftChrome = useComposerChrome(owned ? status?.hostEpoch ?? null : null, selectedProject);
  // Explorer markers for sessions with active reminders: read when the host is ready, after every reminder
  // change made here, and on a slow interval because reminders also fire and complete on their own.
  const [activeReminders, setActiveReminders] = useState<ActiveReminders | null>(null);
  const reminderEpoch = owned ? status!.hostEpoch! : null;
  useEffect(() => {
    if (!reminderEpoch) { setActiveReminders(null); return; }
    let alive = true;
    const read = () => void reminder.active({ expectedEpoch: reminderEpoch }, { timeoutMilliseconds: 15000 }).then(value => {
      const counts = alive ? activeReminderCounts(value, reminderEpoch) : null;
      if (counts) setActiveReminders(previous => previous && sameActiveReminders(previous, counts) ? previous : counts);
    }).catch(() => { /* Unknown keeps the last markers; the next read corrects them. */ });
    read();
    const timer = setInterval(read, 20000);
    const unsubscribe = reminderActions.subscribe(read);
    return () => { alive = false; clearInterval(timer); unsubscribe(); };
  }, [reminderEpoch, reminderActions]);
  const draftChoices = useNewSessionChoices(status?.hostEpoch, projectId, selectedProject?.path ?? null,
    creatingProvider, configurationState.snapshot, owned && sessionId === null && view === "workspace" && !settingsOpen
      && !!snapshot?.configured && (projectId === null || !!selectedProject && !selectedProject.archived), mutation?.capability);
  const currentHostEpoch = useRef(status?.hostEpoch);
  currentHostEpoch.current = status?.hostEpoch;
  const currentHostAvailable = useRef(!!status?.hostAvailable);
  currentHostAvailable.current = !!status?.hostAvailable;
  const localImageKey = JSON.stringify(["local-draft", status?.hostEpoch ?? null, projectId, selectedProject?.path ?? null]);
  const localImages = useLocalDraftImages(submissions.imageDrafts, localImageKey, () => {
    const generation = creationGeneration.current;
    const imageGeneration = localImageGeneration.current;
    const textRevision = localDraft.revision;
    const epoch = status?.hostEpoch;
    const scope = projectId;
    const current = () => creationAlive.current && owned && !!snapshot && snapshot.configured
      && mutation?.capability.canMutate() === true && currentHostAvailable.current && currentHostEpoch.current === epoch
      && selectedScope.current === scope && selectedSessionId.current === null && currentProjectWritable()
      && currentView.current === "workspace" && !settingsVisible.current && !creatingBusy
      && !document.querySelector('dialog[open]:not(.expanded-prompt-dialog), [role="dialog"][aria-modal="true"]')
      && localImageGeneration.current === imageGeneration && localDrafts.current.get(draftScope)?.revision === textRevision
      && generation === creationGeneration.current;
    return current() ? current : null;
  }, () => { localImageGeneration.current++; }, language.locale);

  function openPalette() {
    if (paletteOpen || dialog || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    focusRestoration.cancel();
    paletteOrigin.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setPaletteOpen(true);
  }

  function dismissPalette() {
    const origin = paletteOrigin.current;
    const originView = currentView.current;
    setPaletteOpen(false);
    focusRestoration.schedule(origin, () => currentView.current === originView,
      () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'));
  }

  // The chosen command runs once the palette's modal dialog has closed (see the layout effect below).
  function choosePalette(command: CommandId) {
    if (!commandAvailable(command)) return;
    focusRestoration.cancel();
    palettePending.current = command;
    setPaletteOpen(false);
  }

  function invokeComposerControl(button: HTMLButtonElement | null | undefined) {
    const shell = workspaceShell.current;
    if (!shell?.isConnected || !button?.isConnected || !shell.contains(button) || button.disabled ||
      button.closest('[inert], [hidden]') || currentView.current !== "workspace" || settingsVisible.current ||
      dialog || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    // Deliberate command invocation only, after the caller's target/lifetime checks.
    // Reveal this workspace's own overflow, not arbitrary ancestors or foreign modals.
    const menu = button.closest<HTMLDetailsElement>('details.composer-actions-menu');
    if (menu && shell.contains(menu)) menu.open = true;
    button.click();
  }

  useLayoutEffect(() => {
    if (paletteOpen || !palettePending.current) return;
    const command = palettePending.current;
    palettePending.current = null;
    // Give focus back to where the palette was opened from, so focus-relative commands act on it.
    if (paletteOrigin.current?.isConnected) paletteOrigin.current.focus();
    runCommand(command);
  });

  // Session-scoped commands need an open session that is not behind a file tab; everything else is always offered.
  function commandAvailable(command: CommandId): boolean {
    const session = view === "workspace" && !!selectedSession && !fileTabs.active;
    const ownedSession = session && owned;
    switch (command) {
      case "sessionInfo": case "messagePrevious": case "messageNext": case "messageFirst": case "messageLatest": case "toggleNotes": return session;
      case "usage": case "reminders": case "compact": case "abort": case "clearQueue": case "nextPrompt": case "modelSelector": case "send": case "steer": return ownedSession;
      case "expandPrompt": case "focusPrompt": return view === "workspace" && !fileTabs.active;
      case "closeTab": case "previousTab": case "nextTab": return tabs.open.length + fileTabs.open.length > 0;
      case "reopenTab": return tabs.closed.length + fileTabs.closed.length > 0;
      case "editFile": return owned && view === "workspace" && !!selectedProject && !selectedProject.archived;
      case "refreshStatuses": return owned && tabs.open.length > 0;
      case "newSession": return owned && !!snapshot && !selectedProject?.archived;
      case "renameProject": return owned && !!selectedProject && !selectedProject.archived;
      default: return true;
    }
  }

  // Commands the active session's composer carries out itself.
  function composerCommand(command: "send" | "abort" | "clearQueue" | "nextPrompt") {
    window.dispatchEvent(new CustomEvent("codealta:composer", { detail: command }));
  }
  const activeComposerControl = (selector: string) => Array.from(workspaceShell.current?.querySelectorAll<HTMLButtonElement>(selector) ?? [])
    .find(button => button.offsetParent !== null);

  function runCommand(command: CommandId) {
    if (!commandAvailable(command)) return;
    if (command === "exit") { closeApplicationWindow(); return; }
    // Settings is a modal window: only commands that move to another Settings page run while it is open.
    const pages: Partial<Record<CommandId, View>> = { settings: "appearance", about: "about", skills: "skills", plugins: "plugins", mcp: "mcp",
      config: "config", prompts: "prompts", providers: "providers", models: "models", logs: "logs" };
    if (pages[command]) { navigate(pages[command]!); return; }
    if (settingsVisible.current) return;
    switch (command) {
      case "help": openHelp(); break;
      case "palette": openPalette(); break;
      case "openProject": setDialog("project"); break;
      case "editFile": openFilePicker(); break;
      case "newSession": selectProject(projectId); requestAnimationFrame(() => document.querySelector<HTMLElement>("#session-prompt, #catalog-prompt")?.focus()); break;
      case "focusSidebar": runShortcut("focusProjects"); break;
      case "toggleNavigator": toggleProjects(); break;
      case "modelSelector": invokeComposerControl(activeComposerControl(".composer-selection")); break;
      case "usage": invokeComposerControl(activeComposerControl("#session-usage-trigger")); break;
      case "searchSessions": runShortcut("focusSearch"); break;
      case "refreshStatuses": runtimeObservationControls().refresh(tabs.open); break;
      case "send": case "abort": case "clearQueue": case "nextPrompt": composerCommand(command); break;
      case "steer": break;
      // The remaining commands share their implementation with the older shortcut actions of the same name.
      default: runShortcut(command as ShortcutAction);
    }
  }

  useEffect(() => {
    // Capture phase: the prompt editor (Monaco) must not see keys that belong to a command.
    function commandKey(event: globalThis.KeyboardEvent) {
      if (event.key === "Escape") { commandChord.current = false; return; }
      // Exit is global, as in the terminal UI: it also works while a window of the app is open.
      if (!commandChord.current && resolveCommandKey(event, false, "none").command === "exit") {
        event.preventDefault(); event.stopPropagation(); closeApplicationWindow(); return;
      }
      const target = event.target instanceof HTMLElement ? event.target : null;
      const settingsOnly = settingsVisible.current && !dialog && !paletteOpen
        && document.querySelectorAll('dialog[open], [role="dialog"][aria-modal="true"]').length === 1;
      const modal = paletteOpen || !!dialog || !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]');
      if (modal && !settingsOnly) { commandChord.current = false; return; }
      const focus = target?.closest("#session-prompt, #catalog-prompt") ? "prompt" : target?.closest(workspaceEditingSelector) ? "text" : "none";
      // "?" outside text opens help, as it does when typed into an empty prompt.
      const resolved = focus === "none" && !commandChord.current && event.key === "?" && !event.ctrlKey && !event.altKey && !event.metaKey && !event.repeat
        ? { command: "help" as CommandId, chord: false, handled: true }
        : resolveCommandKey(event, commandChord.current, focus);
      commandChord.current = resolved.chord;
      if (!resolved.handled) return;
      event.preventDefault(); event.stopPropagation();
      if (resolved.command) runCommand(resolved.command);
    }
    // Bubble phase: Escape reaches here only when nothing inside (a popover, the editor) used it.
    function escapeKey(event: globalThis.KeyboardEvent) {
      const target = event.target instanceof HTMLElement ? event.target : null;
      if (target && workspaceShell.current?.contains(target) && !target.closest(workspaceEditingSelector) &&
        !event.isComposing && event.keyCode !== 229 && !event.defaultPrevented &&
        ["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " "].includes(event.key))
        timelineCommand.current?.cancelLatest();
      if (event.key !== "Escape" || event.isComposing || event.keyCode === 229 || event.defaultPrevented || event.repeat
        // An open window closes itself on Escape (its own cancel handling).
        || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
      event.preventDefault(); runShortcut("escape");
    }
    window.addEventListener("keydown", commandKey, true);
    window.addEventListener("keydown", escapeKey);
    return () => { window.removeEventListener("keydown", commandKey, true); window.removeEventListener("keydown", escapeKey); };
  });

  function runShortcut(action: ShortcutAction) {
    if (settingsVisible.current) { if (action === "escape" && !dialog) closeSettings(); return; }
    if (action === "browseSessions") { openSessionBrowser(); return; }
    if (action === "nextTab" || action === "previousTab" || action === "closeTab" || action === "reopenTab") { tabCommand(action); return; }
    const projects = projectListing?.projects ?? [];
    if (action === "messagePrevious" || action === "messageNext" || action === "messageFirst" || action === "messageLatest") {
      const selection = selectedSessionInfoSelection(snapshot, selectedSession, projectId,
        selectedSessionId.current, selectedScope.current);
      const command = timelineCommand.current;
      if (view === "workspace" && selection && command?.sessionId === selection.sessionId
        && command.projectId === selection.projectId && command.epoch === (status?.hostEpoch ?? null)
        && (action === "messageLatest" ? command.latestReady() : command.ready())) {
        if (action === "messageLatest") command.latest();
        else command.navigate(action);
      }
    }
    else if (action === "sessionInfo") invokeComposerControl(sessionInfoTrigger.current);
    else if (action === "reminders") invokeComposerControl(remindersTrigger.current);
    else if (action === "compact") {
      const selection = selectedSessionInfoSelection(snapshot, selectedSession, projectId,
        selectedSessionId.current, selectedScope.current);
      const trigger = compactTrigger.current;
      if (view === "workspace" && owned && currentProjectWritable() && status?.hostEpoch && mutation?.capability.canMutate()
        && selection && trigger?.isConnected && !trigger.disabled
        && trigger.dataset.epoch === status.hostEpoch && trigger.dataset.sessionId === selection.sessionId
        && trigger.dataset.projectId === (selection.projectId ?? "")) invokeComposerControl(trigger);
    }
    else if (action === "expandPrompt") {
      if (!dialog) invokeComposerControl(workspaceShell.current?.querySelector<HTMLButtonElement>("#expand-session-prompt"));
    }
    else if (action === "renameProject") void beginProjectRename();
    else if (action === "escape") {
      if (dialog === "help") closeHelp();
      else if (railVisible && projectRail.current?.contains(document.activeElement)) toggleProjects();
      else { setDialog(null); (document.activeElement as HTMLElement | null)?.blur(); }
    }
    else if (action === "toggleNotes") setNotesVisible(value => !value);
    else if (action === "focusPrompt") document.querySelector<HTMLTextAreaElement>("#session-prompt, #catalog-prompt")?.focus();
    else if (action === "focusSearch") showSessionSearch();
    else if (action === "focusProjects") {
      if (!railVisible) toggleProjects();
      else focusVisibleProject(projectRail.current, projectFilterInput.current);
    }
    else if (action === "nextProject" || action === "previousProject") {
      if (!projects.length || !railVisible) return;
      const index = projects.findIndex(project => project.id === projectId);
      const nextIndex = index < 0 ? (action === "nextProject" ? 0 : projects.length - 1)
        : (index + (action === "nextProject" ? 1 : -1) + projects.length) % projects.length;
      selectProject(projects[nextIndex].id);
    } else if (action === "nextSession" || action === "previousSession") {
      if (!visibleSessions.length) return;
      setMenuTarget(null);
      focusAction.current = null;
      const index = Math.max(0, visibleSessions.findIndex(session => session.id === sessionId));
      const next = visibleSessions[(index + (action === "nextSession" ? 1 : -1) + visibleSessions.length) % visibleSessions.length].id;
      selectedSessionId.current = next;
      setSessionId(next);
    }
  }

  function openSelectedReminders(session: string, epoch: string, scope: string | null) {
    if (currentView.current !== "workspace" || dialog || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')
      || !status?.hostAvailable || currentHostEpoch.current !== epoch
      || !mutation?.capability.canMutate() || selectedSessionId.current !== session || selectedScope.current !== scope
      || selectedSession?.id !== session || projectId !== scope || !currentProjectWritable() ||
      !selectedSessionInfoAvailable(snapshot, selectedSession, scope)) return;
    const element = remindersTrigger.current;
    if (!element?.isConnected || element.disabled) return;
    focusRestoration.cancel();
    remindersOrigin.current = { element, lifetime: captureInfoLifetime() };
    setDialog("reminders");
  }

  function closeReminders() {
    const origin = remindersOrigin.current;
    setDialog(null);
    focusRestoration.schedule(origin?.element ?? null, () => !!origin && origin.lifetime.current()
      && remindersTrigger.current === origin.element && origin.element.isConnected && !origin.element.disabled
      && !origin.element.closest('details:not([open]), [hidden], [inert]'),
    () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'));
  }
  const remindersCurrent = !!remindersOrigin.current?.lifetime.current();
  useLayoutEffect(() => {
    if (dialog === "reminders" && !remindersCurrent) setDialog(null);
  }, [dialog, remindersCurrent]);
  const filePickerProject = owned && selectedProject && !selectedProject.archived ? selectedProject : null;
  useLayoutEffect(() => {
    if (dialog === "file" && !filePickerProject) setDialog(null);
  }, [dialog, filePickerProject]);

  // The inline session search field mounts focused; when it is already shown, focus it again.
  function showSessionSearch() { setSessionOptionsOpen(true); searchInput.current?.focus(); }
  function scopeCanCreateSession(id: string | null) {
    const project = id === null ? undefined : snapshot?.projects.find(value => value.id === id);
    return owned && !!snapshot && !creatingBusy && (id === null || !!project && !project.archived);
  }
  // One menu per scope row: the session actions first select that scope, then act on it.
  function scopeSessionAction(id: string | null, kind: "create" | "search" | "browse") {
    if (id !== selectedScope.current) selectProject(id);
    if (kind === "browse") openSessionBrowser();
    else if (kind === "search") showSessionSearch();
    else if (scopeCanCreateSession(id)) { setCreatingVisible(true); setCreatingMessage(""); }
  }

  function toggleProjects() {
    if (railVisible) {
      focusProjectPending.current = false;
      restoreProjectRailFocus(projectRail.current, document.activeElement, projectRailToggle.current);
    } else focusProjectPending.current = true;
    toggleRail(narrow);
  }

  function selectProject(nextProjectId: string | null, selectedId?: string | null) {
    setMenuTarget(null);
    focusAction.current = null;
    projectRenameGeneration.current++;
    setProjectRenameTarget(null);
    setProjectRenameConflict(false);
    setProjectRenameNotice(uncertainProjectRename.current ? { key: "A previous project rename is unconfirmed. Refresh and inspect; no retry will be sent." } : "");
    selectedScope.current = nextProjectId;
    setProjectId(nextProjectId);
    const nextSessionId = selectedId ?? null;
    setSessionId(nextSessionId);
    selectedSessionId.current = nextSessionId;
    setRenamingId(null);
    setRenamingMessage("");
    setDeletingId(null);
    setDeletingMessage("");
    navigate("workspace");
    setCreatingVisible(false);
    setCreatingMessage("");
  }

  function dismissSessionMenu(restoreFocus: boolean) {
    setMenuTarget(null);
    if (restoreFocus) restoreSessionMenuFocus(menuOrigin.current);
  }

  function openSessionMenu(id: string, origin: HTMLButtonElement | null) {
    if (!snapshot || !origin || snapshot.sessions.filter(session => session.id === id).length !== 1
      || !visibleSessions.some(session => session.id === id) || currentSnapshot.current !== snapshot
      || settingsVisible.current || dialog) return;
    menuOrigin.current = origin;
    if (menuTarget?.id === id && menuTarget.projectId === projectId) { dismissSessionMenu(true); return; }
    menuSnapshot.current = snapshot;
    menuSelection.current = selectedSessionId.current;
    setMenuTarget({ id, projectId, hostEpoch: status?.hostEpoch ?? null });
  }

  function beginSessionRename(row: WorkspaceSession) {
    setRenamingId(row.id);
    setRenamingTitle(row.title);
    setRenamingMessage(renameLocked ? { key: "Earlier rename is unconfirmed. Refresh and inspect; no retry will be sent." } : "");
  }

  function beginSessionDelete(row: WorkspaceSession) {
    setDeletingId(row.id);
    setDeletingConfirmation("");
    setDeletingMessage(deleteLocked ? "Earlier deletion is unconfirmed. Refresh and inspect; no retry will be sent." : "");
  }

  function runSessionMenuAction(action: SessionAction, row: WorkspaceSession, target: SessionMenuTarget) {
    if (!activeMenu || activeMenu !== target || currentSnapshot.current !== menuSnapshot.current
      || snapshot?.sessions.filter(session => session.id === row.id).length !== 1
      || snapshot.sessions.find(session => session.id === row.id) !== row
      || selectedSessionId.current !== menuSelection.current || selectedScope.current !== target.projectId) return;
    const access = sessionActionAccess(row, target,
      row.id,
      selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
      mutation?.capability.canMutate() ?? false, batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current);
    if (!access.open) return;
    if (action === "rename" && !access.rename || action === "delete" && !access.delete) return;
    const switching = selectedSessionId.current !== row.id;
    if (switching) { selectedSessionId.current = row.id; setSessionId(row.id); }
    if (action === "open") { dismissSessionMenu(false); menuOrigin.current?.closest<HTMLElement>(".session-row")?.querySelector<HTMLButtonElement>(":scope > button:first-child")?.focus(); return; }
    focusAction.current = action;
    dismissSessionMenu(false);
    if (switching) { setRenamingMessage(""); setDeletingMessage(""); }
    if (action === "rename") {
      beginSessionRename(row);
      setDeletingId(null);
    } else {
      beginSessionDelete(row);
      setRenamingId(null);
    }
  }

  async function refreshProjects(signal: AbortSignal) {
    markProjectInspection(false); // A failed or pending read cannot certify old details.
    try {
      const fresh = await workspace.snapshot({}, { signal, timeoutMilliseconds: 30_000 });
      if (signal.aborted) return undefined;
      publishWorkspaceState(fresh.configured ? { kind: "ready", snapshot: fresh } : { kind: "unconfigured" });
      return fresh;
    } catch { return undefined; }
  }

  async function beginProjectRename() {
    const project = selectedProject;
    const capability = mutation?.capability;
    if (!project || project.archived || !owned || !capability?.canMutate() || projectRenamePending.current) return;
    if (uncertainProjectRename.current) {
      setProjectRenameNotice({ key: "A previous project rename is unconfirmed. Refresh and inspect; no retry will be sent." });
      return;
    }
    const generation = ++projectRenameGeneration.current;
    projectRenamePending.current = true;
    setProjectRenameBusy(true);
    setProjectRenameNotice("");
    try {
      const result = await projectRename.preflight(status?.hostEpoch, project.id, project.path, capability);
      if (!creationAlive.current || generation !== projectRenameGeneration.current || selectedScope.current !== project.id
        || currentHostEpoch.current !== status?.hostEpoch || !capability.canMutate()) return;
      if (result.kind === "ready") {
        setProjectRenameTarget(result.target);
        setProjectRenameName(result.target.name);
        setProjectRenameConflict(false);
      } else setProjectRenameNotice(projectRenameMessage(result.code));
    } finally { projectRenamePending.current = false; if (creationAlive.current) setProjectRenameBusy(false); }
  }

  async function saveProjectRename() {
    const target = projectRenameTarget;
    const capability = mutation?.capability;
    if (!target || !capability?.canMutate() || projectRenamePending.current || projectRenameConflict || projectRenameLocked
      || selectedScope.current !== target.id || currentHostEpoch.current !== target.epoch) return;
    const name = projectRenameName;
    const generation = projectRenameGeneration.current;
    projectRenamePending.current = true;
    setProjectRenameBusy(true);
    setProjectRenameNotice("");
    try {
      const result = await projectRename.rename(target, name, capability);
      if (!creationAlive.current) return;
      if (result.kind === "renamed") {
        // The RPC can have committed even if a bridge refresh fails. Never retry this snapshot.
        let fresh: Awaited<ReturnType<typeof workspace.snapshot>> | undefined;
        if (currentHostEpoch.current === target.epoch && capability.canMutate()) {
          markProjectInspection(false);
          try {
            fresh = await workspace.snapshot({}, { signal: creationRefresh.current.signal, timeoutMilliseconds: 30_000 });
            if (creationAlive.current && currentHostEpoch.current === target.epoch && capability.canMutate() && fresh.configured)
              publishWorkspaceState({ kind: "ready", snapshot: fresh });
          } catch { /* Outcome is uncertain if refresh fails. */ }
        }
        if (!creationAlive.current) return;
        if (fresh && projectNameVisible(fresh, target, name) && capability.canMutate()
          && projectRenameSelectionCurrent(target, currentHostEpoch.current, selectedScope.current)
          && generation === projectRenameGeneration.current) {
          if (!projectRailProjection(fresh, projectFilter, projectSort).projects.some(project => project.id === target.id))
            projectFilterInput.current?.focus();
          setProjectRenameTarget(null);
          setProjectRenameNotice("");
        } else {
          uncertainProjectRename.current = { target, name };
          setProjectRenameLocked(true);
          setProjectRenameNotice({ key: "The rename may have completed, but its exact project/name was not confirmed. Refresh and inspect; no retry will be sent." });
        }
      } else {
        if (result.code === "rename_unconfirmed") {
          uncertainProjectRename.current = { target, name };
          setProjectRenameLocked(true);
          setProjectRenameNotice(projectRenameMessage(result.code));
        } else if (generation === projectRenameGeneration.current
          && projectRenameSelectionCurrent(target, currentHostEpoch.current, selectedScope.current)) {
          if (result.code === "conflict" || result.code === "unsupported") setProjectRenameConflict(true);
          setProjectRenameNotice(projectRenameMessage(result.code));
        }
      }
    } finally { projectRenamePending.current = false; if (creationAlive.current) setProjectRenameBusy(false); }
  }

  async function refreshProjectRename() {
    const uncertain = uncertainProjectRename.current;
    if (!uncertain || !creationAlive.current || projectRenameRefreshPending.current) return;
    const capability = mutation?.capability;
    if (!capability?.canMutate() || currentHostEpoch.current !== uncertain.target.epoch) {
      setProjectRenameNotice({ key: "The host changed. Reload before reconciling this rename; no retry will be sent." });
      return;
    }
    const generation = projectRenameGeneration.current;
    projectRenameRefreshPending.current = true;
    markProjectInspection(false);
    try {
      const fresh = await workspace.snapshot({}, { signal: creationRefresh.current.signal, timeoutMilliseconds: 30_000 });
      if (!creationAlive.current || uncertainProjectRename.current !== uncertain || !capability.canMutate()
        || currentHostEpoch.current !== uncertain.target.epoch || generation !== projectRenameGeneration.current) return;
      if (fresh.configured) publishWorkspaceState({ kind: "ready", snapshot: fresh });
      if (projectNameVisible(fresh, uncertain.target, uncertain.name)) {
        uncertainProjectRename.current = null;
        setProjectRenameLocked(false);
        if (selectedScope.current === uncertain.target.id) setProjectRenameTarget(null);
        setProjectRenameNotice("");
      } else setProjectRenameNotice({ key: "The exact project/name is not confirmed. Inspect or reload; no retry will be sent." });
    } catch {
      if (creationAlive.current && generation === projectRenameGeneration.current)
        setProjectRenameNotice({ key: "Refresh failed. Inspect or reload; no retry will be sent." });
    } finally { projectRenameRefreshPending.current = false; }
  }

  function creationProviderChoice(compact = false) {
    const inventory = configurationState.snapshot;
    const providers = inventory?.providerRuntimeAvailable ? inventory.providers.slice(0, 32).filter(provider => provider.enabled
      && provider.id.length > 0 && provider.id.length <= 256 && provider.id === provider.id.trim()
      && !/[\u0000-\u001f\u007f-\u009f\ud800-\udfff]/u.test(provider.id)
      && inventory.providers.filter(other => other.id === provider.id).length === 1) : [];
    // The provider in use is named even before the user picks one: the default provider, else the first enabled one.
    const usedProvider = creatingProvider || draftChoices.value.providerKey;
    const select = <HTMLSelect fill value={usedProvider} aria-label={t("Provider for new session")} disabled={creatingBusy || creationLocked || !owned} onChange={event => setCreatingProvider(event.target.value)}>
      {!usedProvider && <option value="">{t("No provider")}</option>}
      {usedProvider && !providers.some(provider => provider.id === usedProvider) && <option value={usedProvider} disabled>{usedProvider}</option>}
      {providers.map(provider => <option key={provider.id} value={provider.id}>{provider.id}</option>)}
    </HTMLSelect>;
    if (compact) {
      const locked = creatingBusy || creationLocked || !owned;
      const value = draftChoices.value;
      const efforts = draftChoices.models.find(model => model.id === value.modelId)?.efforts ?? [];
      const change = (field: "agentPromptId" | "modelId" | "reasoningEffort", next: string) => {
        invalidateCreation(); draftChoices.change(field, next);
      };
      return <ComposerSelectionFields sessionId="new" onOpenCatalog={navigate}
        summary={{ agent: draftChoices.prompts.find(prompt => prompt.id === value.agentPromptId)?.name ?? t(draftChoices.loadingPrompts ? "Loading…" : "Host default"),
          provider: usedProvider || t("No provider"),
          model: value.modelId ? draftChoices.models.find(model => model.id === value.modelId)?.name ?? value.modelId : t(draftChoices.loadingModels ? "Loading…" : "Provider default"),
          reasoning: value.reasoningEffort ?? t(draftChoices.loadingModels ? "Loading…" : "Model default") }}
        agent={<HTMLSelect fill id="composer-agent-new" aria-label={t("Agent prompt")} value={value.agentPromptId}
          disabled={locked || draftChoices.loadingPrompts || !draftChoices.prompts.length} onChange={event => change("agentPromptId", event.target.value)}>
          {!draftChoices.prompts.some(prompt => prompt.id === value.agentPromptId) && <option value={value.agentPromptId}>{t(draftChoices.loadingPrompts ? "Loading…" : "Host default")}</option>}
          {draftChoices.prompts.map(prompt => <option key={prompt.id} value={prompt.id}>{prompt.name}</option>)}
        </HTMLSelect>} provider={select}
        model={<HTMLSelect fill id="composer-model-new" data-model-selector aria-label={t("Model")} value={value.modelId ?? ""}
          disabled={locked || draftChoices.loadingModels} onChange={event => change("modelId", event.target.value)}>
          {!value.modelId && <option value="">{t(draftChoices.loadingModels ? "Loading…" : "Provider default")}</option>}
          {value.modelId && !draftChoices.models.some(model => model.id === value.modelId) && <option value={value.modelId}>{value.modelId} · {t("Unverified")}</option>}
          {draftChoices.models.map(model => <option key={model.id} value={model.id}>{model.name}</option>)}
        </HTMLSelect>}
        reasoning={<ReasoningSlider value={value.reasoningEffort ?? null} efforts={efforts} disabled={locked}
          onChange={next => change("reasoningEffort", next)} />} />;
    }
    return <div className="creation-provider">
      <label><span>{t("Provider for new session")}</span>
        {select}
      </label>
    </div>;
  }

  async function createSelectedSession(fromDraft = false) {
    if (creationPending.current || creationHeld.current || !owned || !snapshot || !mutation?.capability.canMutate() || selectedProject?.archived
      || projectId !== null && !selectedProject || settingsVisible.current || dialog || paletteOpen
      || currentView.current !== "workspace" || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    if (fromDraft && (!draftChoices.ready || sessionId !== null || (!localDraft.text.trim() && !localImages.images.length)
      || localImages.images.length > 0 && (localDraft.text.length > imageLimits.text || localDraft.text !== "" && !localDraft.text.trim()))) return;
    invalidateCreation(); // Deliberate capture fences any outstanding local paste.
    const handoff = fromDraft ? { scope: draftScope, ...localDraft, imageKey: localImageKey, images: localImages.images,
      imageGeneration: localImageGeneration.current, selection: { ...draftChoices.value } } : null;
    const recordHandoff = (outcome: string) => { if (handoff) setDraftHandoffNotice(outcome); };
    creationPending.current = true;
    creationHeld.current = true;
    setCreationLocked(true);
    const providerId = handoff?.selection.providerKey || creatingProvider || null;
    const target: SessionTarget = selectedProject
      ? { scope: "project", projectId: selectedProject.id, projectPath: selectedProject.path } : { scope: "global" };
    const generation = creationGeneration.current;
    const epoch = status?.hostEpoch;
    const sessionAtAdmission = sessionId;
    const capability = mutation.capability;
    const isCurrent = () => creationAlive.current && generation === creationGeneration.current
      && currentHostEpoch.current === epoch && currentHostAvailable.current && capability.canMutate()
      && selectedScope.current === (target.scope === "project" ? target.projectId : null)
      && selectedSessionId.current === sessionAtAdmission && currentView.current === "workspace"
      && !settingsVisible.current && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')
      && (!handoff || localDrafts.current.get(handoff.scope)?.revision === handoff.revision)
      && (!handoff || submissions.imageDrafts.get(handoff.imageKey) === handoff.images)
      && (!handoff || localImageGeneration.current === handoff.imageGeneration)
      && (target.scope === "global" || currentSnapshot.current?.projects.filter(project => project.id === target.projectId).length === 1
        && currentSnapshot.current.projects.some(project => project.id === target.projectId && project.path === target.projectPath && !project.archived));
    const completedElsewhere = "Creation may have completed, but its original view or input lifetime changed or the catalog did not confirm it. Inspect sessions; no retry was sent.";
    setCreatingBusy(true);
    setCreatingMessage("");
    if (handoff) setDraftHandoffNotice("Creation pending. Original draft retained; nothing has been sent.");
    try {
      const result = await createSession(epoch, target, handoff ? null : creatingTitle.trim() || null, capability, providerId);
      if (!creationAlive.current) return;
      if (result.kind === "created") {
        if (!isCurrent()) { setCreatingMessage(completedElsewhere); recordHandoff(completedElsewhere + " Original draft retained; nothing sent."); return; }
        // Acquire without publishing: a late create-specific read cannot overwrite a newer
        // host/catalog/selection. Other explicit refresh callers keep their existing behavior.
        let fresh: WorkspaceSnapshot | undefined;
        try { fresh = await workspace.snapshot({}, { signal: creationRefresh.current.signal, timeoutMilliseconds: 30_000 }); }
        catch { /* A failed read is not evidence that creation had no effects. */ }
        if (!creationAlive.current) return;
        const candidate = fresh && createdSessionSelection(fresh, result);
        const selection = candidate && fresh && selectedTab(fresh, candidate.projectId, candidate.sessionId) ? candidate : undefined;
        if (fresh && selection && isCurrent()) {
          if (handoff) {
            // Revalidate preferences against this newly verified session's choices, never
            // manufacture session authority from the pre-creation catalog DTOs.
            let choices: Awaited<ReturnType<typeof sessionOperations.choices>> | undefined;
            try {
              choices = await sessionOperations.choices({ expectedEpoch: epoch!, sessionId: selection.sessionId },
                { signal: creationRefresh.current.signal, timeoutMilliseconds: 15000 });
              capability.observe(choices);
            } catch { /* Original draft and choices stay available for manual review. */ }
            if (!isCurrent() || !choices || choices.status !== "ok" || choices.epoch !== epoch
              || choices.sessionId !== selection.sessionId || !choices.current || !validSelection(choices, handoff.selection)) {
              recordHandoff("Session created, but draft choices could not be verified. Original draft retained; nothing sent.");
              return;
            }
            // Never overwrite a pre-existing session editor. Storage failure cannot
            // certify delivery into the ordinary composer, so leave navigation alone.
            if (snapshot.sessions.some(row => row.id === selection.sessionId) || submissions.pending(selection.sessionId)
              || !(handoff.images.length ? submissions.imageDrafts.copyToEmpty(handoff.imageKey, handoff.images,
                JSON.stringify([epoch, selection.sessionId, selection.projectId, target.scope === "project" ? target.projectPath : null]), () => {
                  if (!isCurrent() || submissions.pending(selection.sessionId)) return false;
                  if (handoff.text === "") return !localStorage.getItem(draftStorageKey(selection.sessionId));
                  return transferPromptDraft(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value), selection.sessionId, handoff.text)
                    && isCurrent() && !submissions.pending(selection.sessionId);
                }) : transferPromptDraft(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value), selection.sessionId, handoff.text))) {
              recordHandoff("Session created, but draft transfer could not be confirmed. Original text and images retained. Destination text storage may be uncertain; inspect sessions. Nothing sent.");
              return;
            }
            if (!isCurrent() || !nextSendSelections.set(epoch!, selection.sessionId, choices, handoff.selection)) {
              recordHandoff("Session created, but draft choices could not be verified. Original draft retained; nothing sent.");
              return;
            }
            recordHandoff("");
            if (handoff.images.length === 0 && handoff.text.trim()) autoSend.current = { sessionId: selection.sessionId, text: handoff.text };
            // The session now owns the text; the New session tab starts empty again.
            if (handoff.scope === draftScope && localDrafts.current.get(draftScope)?.revision === handoff.revision) editLocalDraft("");
          }
          publishWorkspaceState({ kind: "ready", snapshot: fresh });
          creationHeld.current = false;
          setCreationLocked(false);
          selectedScope.current = selection.projectId;
          setProjectId(selection.projectId);
          setSessionId(selection.sessionId);
          setSearch("");
          setCreatingVisible(false);
          setCreatingTitle("");
          navigate("workspace");
        } else { setCreatingMessage(completedElsewhere); recordHandoff(completedElsewhere + " Original draft retained; nothing sent."); }
      } else {
        // Only a correlated definite refusal releases this App-owned original. Changes
        // to selection, inventory, settings or host never release an uncertain attempt.
        if (["invalid_scope", "unconfigured", "project_missing", "provider_unavailable", "busy", "closed"].includes(result.code)) {
          creationHeld.current = false;
          setCreationLocked(false);
        }
        const message = sessionCreationMessage(result.code) + (isCurrent() ? "" : ` ${completedElsewhere}`);
        setCreatingMessage(message); recordHandoff(message + " Original draft retained; nothing sent."); }
    } finally { creationPending.current = false; if (creationAlive.current) setCreatingBusy(false); }
  }

  async function renameSelectedSession() {
    if (batchDeletion.locked()) return;
    const session = selectedSession;
    if (!session || session.id !== renamingId || selectedSessionId.current !== session.id
      || selectedScope.current !== (selectedProject?.id ?? null) || renamingPending.current
      || uncertainRename.current !== null || !owned || !mutation?.capability.canMutate()
      || !session.workspacePath || selectedProject?.archived) return;
    if (!sessionActionAccess(session, { id: session.id, projectId: projectId, hostEpoch: status?.hostEpoch ?? null },
      selectedSessionId.current, selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
      mutation.capability.canMutate(), renamingBusy || deletingBusy || deletingPending.current,
      renameLocked || deleteLocked || !!uncertainDelete.current).rename) return;
    const target: RenameTarget = selectedProject
      ? { scope: "project", projectId: selectedProject.id, projectPath: selectedProject.path }
      : { scope: "global", projectPath: session.workspacePath };
    const capability = mutation.capability;
    const title = renamingTitle;
    renamingPending.current = true;
    setRenamingBusy(true);
    setRenamingMessage("");
    try {
      const result = await renameSession(status?.hostEpoch, target, session.id, title, capability);
      if (!creationAlive.current) return;
      if (result.kind === "renamed") {
        const fresh = await refreshProjects(creationRefresh.current.signal);
        if (!creationAlive.current) return;
        if (fresh && renamedSessionVisible(fresh, result) && capability.canMutate()
          && renameSelectionCurrent(result, selectedScope.current, selectedSessionId.current)) {
          setRenamingId(null);
          setRenamingTitle("");
        } else {
          uncertainRename.current = { id: session.id, title, target };
          setRenameLocked(true);
          if (selectedSessionId.current === session.id) setRenamingMessage({ key: "Rename may have completed, but the selection changed or the refreshed catalog did not show the title. Refresh and inspect; no retry will be sent." });
        }
      } else {
        if (result.code === "rename_unconfirmed") {
          uncertainRename.current = { id: session.id, title, target };
          setRenameLocked(true);
        }
        if (selectedSessionId.current === session.id) setRenamingMessage(sessionRenameMessage(result.code));
      }
    } finally { renamingPending.current = false; if (creationAlive.current) setRenamingBusy(false); }
  }

  async function refreshRenamedSession() {
    const original = uncertainRename.current;
    const fresh = await refreshProjects(creationRefresh.current.signal);
    if (!creationAlive.current || !original) return;
    const result = { kind: "renamed" as const, id: original.id, title: original.title, target: original.target };
    if (fresh && renamedSessionVisible(fresh, result)) {
      uncertainRename.current = null;
      setRenameLocked(false);
      if (renameSelectionCurrent(result, selectedScope.current, selectedSessionId.current)) setRenamingId(null);
      setRenamingMessage("");
    } else if (selectedSessionId.current === original.id) setRenamingMessage({ key: "Title not confirmed in the refreshed catalog. No retry will be sent; inspect the session or reload." });
  }

  async function deleteSelectedSession() {
    if (batchDeletion.locked()) return;
    const session = selectedSession;
    if (!session || session.id !== deletingId || selectedSessionId.current !== session.id
      || selectedScope.current !== (selectedProject?.id ?? null) || deletingPending.current || uncertainDelete.current
      || !owned || !mutation?.capability.canMutate() || !session.workspacePath || selectedProject?.archived
      || deletingConfirmation !== session.title) return;
    if (!sessionActionAccess(session, { id: session.id, projectId: projectId, hostEpoch: status?.hostEpoch ?? null },
      selectedSessionId.current, selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
      mutation.capability.canMutate(), deletingBusy || renamingBusy || renamingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current).delete) return;
    const target: RenameTarget = selectedProject
      ? { scope: "project", projectId: selectedProject.id, projectPath: selectedProject.path }
      : { scope: "global", projectPath: session.workspacePath };
    const captured: DeletedTarget = { target, id: session.id };
    const capability = mutation.capability;
    deletingPending.current = true;
    setDeletingBusy(true);
    setDeletingMessage("");
    try {
      const result = await deleteSession(status?.hostEpoch, target, session.id, session.title, deletingConfirmation, capability);
      if (!creationAlive.current) return;
      if (result.kind === "deleted") {
        const fresh = await refreshProjects(creationRefresh.current.signal);
        if (!creationAlive.current) return;
        const recovered = fresh && deletedSessionRecovery(fresh, result);
        if (recovered && capability.canMutate()) {
          if (deleteSelectionCurrent(result, selectedScope.current, selectedSessionId.current)) {
            selectedSessionId.current = recovered.sessionId;
            setSessionId(recovered.sessionId);
          }
          setDeletingId(null);
          setDeletingConfirmation("");
        } else {
          uncertainDelete.current = captured;
          setDeleteLocked(true);
          if (selectedSessionId.current === session.id) setDeletingMessage("Deletion may have completed, but the refreshed catalog did not confirm absence. No retry will be sent.");
        }
      } else {
        if (result.code === "delete_unconfirmed") {
          uncertainDelete.current = captured;
          setDeleteLocked(true);
        }
        if (selectedSessionId.current === session.id) setDeletingMessage(sessionDeletionMessage(result.code));
      }
    } finally { deletingPending.current = false; if (creationAlive.current) setDeletingBusy(false); }
  }

  async function refreshDeletedSession() {
    const captured = uncertainDelete.current;
    const fresh = await refreshProjects(creationRefresh.current.signal);
    if (!creationAlive.current || !captured) return;
    const recovered = fresh && deletedSessionRecovery(fresh, captured);
    if (recovered) {
      uncertainDelete.current = null;
      setDeleteLocked(false);
      if (deleteSelectionCurrent(captured, selectedScope.current, selectedSessionId.current)) {
        selectedSessionId.current = recovered.sessionId;
        setSessionId(recovered.sessionId);
      }
      setDeletingId(null);
      setDeletingConfirmation("");
      setDeletingMessage("");
    } else if (selectedSessionId.current === captured.id)
      setDeletingMessage("Absence is not confirmed in the refreshed catalog. No retry will be sent; inspect the session or reload.");
  }

  // Settings pages also edit the selected project's settings when it can be written.
  const settingsProject = selectedProject && !selectedProject.archived ? { id: selectedProject.id, name: selectedProject.name } : null;
  const newPromptDisabled = creatingBusy || creationLocked || !draftChoices.ready || !owned || !mutation?.capability.canMutate() || !snapshot
    || !!selectedProject?.archived || projectId !== null && !selectedProject || (!localDraft.text.trim() && !localImages.images.length)
    || localImages.images.length > 0 && (localDraft.text.length > imageLimits.text || localDraft.text !== "" && !localDraft.text.trim());
  return <ShellLanguageContext.Provider value={language}><div className="app-shell ide-shell">
    {(hostSilent || !widthSaved) && <div className="shell-notices" data-neoastra-no-drag>
      {hostSilent && <div className="shell-notice" role="alert">{t("CodeAlta is not responding.")}
        <Button size="small" intent="danger" onClick={() => window.location.reload()}>{t("Reload")}</Button></div>}
      {!widthSaved && <div className="shell-notice" role="status">{t("Width preference could not be saved; current layout stays available.")}</div>}
    </div>}

    {dialog === "reminders" && remindersCurrent && <RemindersDialog onClose={closeReminders}><ReminderScopeGate snapshot={snapshot} projectId={projectId}
          session={selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId ? selectedSession : undefined}
          epoch={owned && (currentProjectWritable() || !!snapshot && archivedProjectScope(snapshot, projectId)) ? status!.hostEpoch! : null}
          read={readReminders} readDetail={readReminderDetail} actions={reminderActions} mutationAllowed={!!mutation?.capability.canMutate()}
          canMutate={() => !!mutation?.capability.canMutate()} /></RemindersDialog>}
      <div className={`workspace-shell${railVisible ? " project-rail-open" : ""}`} ref={workspaceShell} style={{
          "--explorer-width": `${ideWidth.width}px`,
          "--project-pane-width": `${visiblePaneLayout.projects}px`,
          "--session-pane-width": `${visibleSessionWidth}px`,
        } as CSSProperties}>
        <WindowBrand>
          <nav className="activity-rail" aria-label={t("Workspace navigation")}>
            <Button ref={projectRailToggle} variant="minimal" size="small" active={railVisible} icon={<AppIcon name="folder" size={16} />} aria-label={t("Explorer")} title={t("Explorer")} aria-expanded={railVisible} aria-controls="project-rail" onClick={toggleProjects} />
            <Button variant="minimal" size="small" icon={<AppIcon name="search" size={16} />} aria-label={t("Open command palette")} aria-haspopup="dialog" title={`${t("Open command palette")} (Ctrl+P)`} onClick={openPalette} />
            <Button variant="minimal" size="small" icon={<AppIcon name="settings" size={16} />} className="activity-settings" aria-label={t("Settings & extensions")} title={t("Settings & extensions")} onClick={() => navigate("appearance")} />
          </nav>
        </WindowBrand>
        <div className="window-actions">
          <Button variant="minimal" size="small" className="theme-switch" icon={<AppIcon name={themeIcons[theme]} size={16} />}
            aria-label={t("Theme: {theme}", { theme: t(themeLabel(theme)) })} title={t("Theme: {theme}", { theme: t(themeLabel(theme)) })} onClick={() => setTheme(nextTheme(theme))} />
        </div>
        <WindowControls snapshot={windowSnapshot} />
        <SessionContentLayout sessionWidth={ideWidth.width} narrow={narrow} sessionsHidden={!railVisible}
          projects={sessions => <aside id="project-rail" className="project-rail" aria-label={t("Projects")} ref={projectRail} hidden={!railVisible}>
          <div className="panel-title"><span title={projectListing?.evidenceNotice ?? undefined}>{t("Projects")}<span className="count">{snapshot?.projects.length ?? 0}</span></span><span>
            {snapshot && <PopoverNext placement="bottom-end" content={<Menu aria-label={t("Project actions")}>
              <MenuDivider title={t("Sort projects")} />
              <MenuItem roleStructure="listoption" selected={projectSort === "name"} text={t("Name")} onClick={() => setProjectSort("name")} />
              <MenuItem roleStructure="listoption" selected={projectSort === "recent"} text={t("Recent visible updates")} onClick={() => setProjectSort("recent")} />
              <MenuDivider />
              <MenuItem icon={<AppIcon name="open" size={15} />} text={`${t("Open project")}…`} label="Ctrl+O" onClick={() => setDialog("project")} />
              <MenuItem icon={<AppIcon name="archive" size={15} />} text={t(selectedProject?.archived ? "Unarchive project…" : "Archive project…")}
                disabled={!selectedProject || !owned || !mutation?.capability.canMutate()} onClick={() => setDialog("archive")} />
            </Menu>}>
              <Button variant="minimal" size="small" className="rail-action" icon={<AppIcon name="ellipsis" size={18} />} aria-label={t("Project actions")} title={t("Project actions")} />
            </PopoverNext>}
            <Button variant="minimal" size="small" className="rail-action" icon={<AppIcon name="plus" size={18} />} aria-label={`${t("Open project")} (Ctrl+O)`} title={`${t("Open project")} (Ctrl+O)`} onClick={() => setDialog("project")} />
          </span></div>
          {workspaceState.kind === "loading" && <LoadingRows />}
          {workspaceState.kind === "unconfigured" && <div className="sidebar-empty">{t("No catalog configured. See the launch instructions below.")}</div>}
          {workspaceState.kind === "error" && <div role="alert" className="sidebar-empty error-text">{workspaceState.message}</div>}
          {snapshot && <InputGroup id="project-filter" inputRef={projectFilterInput} className="project-filter" size="small" type="search" value={projectFilter}
            leftIcon={<AppIcon name="search" size={14} className={Classes.ICON} />} onChange={event => setProjectFilter(event.target.value)}
            placeholder={t("Name or path")} aria-label={t("Filter projects by name or path")} aria-controls="project-list"
            rightElement={projectFilter ? <Button variant="minimal" size="small" icon={<AppIcon name="close" size={14} />} aria-label={t("Clear filter")} title={t("Clear filter")}
              onClick={() => { setProjectFilter(""); projectFilterInput.current?.focus(); }} /> : undefined} />}
          {snapshot && projectListing?.projects.length === 0 && <p className="sidebar-empty" role="status">
            {t(projectFilter.trim() ? "No matching projects. Clear the filter to show them again." : "No projects in this snapshot.")}
            {projectId !== null && ` ${t("The selected project and session remain open.")}`}
          </p>}
          {snapshot && <ProjectRailRows projects={projectListing?.projects ?? []} selectedId={projectId} onSelect={selectProject} children={sessions}
            activity={id => <><RunningSessionBadge controls={runtimeObservationControls()} projectId={id} />
              <ReminderBadge count={activeReminders ? scopeReminderCount(activeReminders, snapshot, id) : 0} /></>}
            canRename={owned} renameBusy={projectRenameBusy || !mutation?.capability.canMutate()} onRename={() => void beginProjectRename()}
            actions={{ current: () => ({ ...currentProjectDetailsContext(),
              active: creationAlive.current && currentView.current === "workspace" && !settingsVisible.current && !!projectRail.current && !projectRail.current.hidden,
              generation: browserRevision.current + projectRenameGeneration.current, modalGeneration: creationGeneration.current,
              canMutate: owned && !!mutation?.capability.canMutate(),
              locked: projectRenamePending.current || !!uncertainProjectRename.current || projectRenameLocked
                || !!projectRenameTarget || projectArchive.locked || !!projectOpening.getSnapshot() }),
              open: selectProject, rename: () => void beginProjectRename(), archive: () => setDialog("archive"),
              sessions: { canCreate: scopeCanCreateSession, create: id => scopeSessionAction(id, "create"),
                search: id => scopeSessionAction(id, "search"), browse: id => scopeSessionAction(id, "browse") } }} />}
          {projectArchive.records.length > 0 && <button type="button" className="quiet-button" onClick={() => setDialog("archive")}>{t("Archive operation evidence")}</button>}
          {projectRenameTarget && projectId === projectRenameTarget.id && currentHostEpoch.current === projectRenameTarget.epoch &&
            <div className="project-rename" role="group" aria-label={t("Rename project {name}", { name: projectRenameTarget.name })}>
              <label>{t("Project name")}
                <input autoFocus={railVisible} value={projectRenameName} maxLength={256} disabled={projectRenameBusy || projectRenameLocked || projectRenameConflict}
                  onChange={event => setProjectRenameName(event.target.value)}
                  onKeyDown={event => { if (event.key === "Escape" && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) {
                    event.preventDefault(); event.stopPropagation(); projectRenameGeneration.current++; setProjectRenameTarget(null);
                  } }} /></label>
              <button type="button" disabled={projectRenameBusy || projectRenameLocked || projectRenameConflict || !projectRenameName.trim()}
                onClick={() => void saveProjectRename()}>{t("Save project name")}</button>
              <button type="button" disabled={projectRenameBusy} onClick={() => { projectRenameGeneration.current++; setProjectRenameTarget(null); }}>{t("Cancel (Escape)")}</button>
            </div>}
          {projectRenameNotice && <p role="alert" className="notice error-text">{workflowNotice(language.locale, projectRenameNotice)}</p>}
          {projectRenameLocked && <button type="button" className="quiet-button" onClick={() => void refreshProjectRename()}>{t("Refresh project name (no retry)")}</button>}
        </aside>}
          splitter={<PaneSplitter className="session-splitter" label={t("Resize Explorer")} value={ideWidth.width} hidden={narrow || !railVisible}
            onResize={delta => setIdeWidth(value => resizeIdeWidth(value, delta))} onReset={() => setIdeWidth(defaultIdeWidth)} />}
          sessions={<aside className="session-rail" aria-label={t("Sessions")} ref={sessionRail} hidden={!railVisible}>
          {(sessionOptionsOpen || search !== "") && <InputGroup inputRef={searchInput} className="session-search" size="small" type="search" autoFocus
            leftIcon={<AppIcon name="search" size={14} className={Classes.ICON} />} value={search} title={notice || undefined}
            onChange={event => setSearch(event.target.value)} placeholder={t("Search sessions")} aria-label={t("Search sessions")}
            onKeyDown={event => { if (event.key === "Escape" && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) {
              event.preventDefault(); event.stopPropagation(); setSearch(""); setSessionOptionsOpen(false);
            } }}
            rightElement={<Button variant="minimal" size="small" icon={<AppIcon name="close" size={14} />} aria-label={t("Clear filter")} title={t("Clear filter")}
              onClick={() => { setSearch(""); setSessionOptionsOpen(false); }} />} />}
          {creatingVisible && <div className="session-create">
            <label>{selectedProject ? t("New session in {name}", { name: selectedProject.name }) : t("New global session")}
              <input value={creatingTitle} maxLength={256} disabled={creatingBusy} placeholder={t("Title (optional)")} onChange={event => setCreatingTitle(event.target.value)} /></label>
            {creationProviderChoice()}
            <button type="button" className="quiet-button" disabled={creatingBusy || creationLocked} onClick={() => void createSelectedSession()}>{t("Create and open")}</button>
          </div>}
          {creatingBusy && <p role="status" className="notice">{t("Creating session…")}</p>}
          {creationLocked && !creatingBusy && <p role="status" className="notice">{t("An earlier creation is unconfirmed. Inspect sessions; creation is blocked in this window.")}</p>}
          {creatingMessage && <p role="alert" className="notice error-text">{workflowNotice(language.locale, creatingMessage)}</p>}
          {creatingMessage && <button type="button" className="quiet-button" disabled={creatingBusy} onClick={() => {
            void refreshProjects(creationRefresh.current.signal).then(fresh => {
              if (creationAlive.current) setCreatingMessage({ key: fresh ? "Session list refreshed. Inspect the entries before creating another."
                : "Could not refresh the session list. Inspect before creating another." });
            });
          }}>{t("Refresh session list")}</button>}
          {renameLocked && <div role="alert" className="notice error-text">{t("A rename is unconfirmed. No further rename will be sent until the exact title is visible after refresh.")}
            <button type="button" className="quiet-button" onClick={() => void refreshRenamedSession()}>{t("Refresh title")}</button>
          </div>}
          {deleteLocked && <div role="alert" className="notice error-text">{t("A deletion is unconfirmed. No further deletion will be sent until a complete refresh confirms absence.")}
            <button type="button" className="quiet-button" onClick={() => void refreshDeletedSession()}>{t("Refresh session list")}</button>
          </div>}
          {!owned && <p className="muted-text">{t("Session creation requires an owned host.")}</p>}
          {batchDeletionState.phase !== "idle" && <p role="status">Batch deletion: {batchDeletionState.phase}. {batchDeletionState.items.filter(item => item.outcome === "deleted").length} confirmed deleted; {batchDeletionState.items.filter(item => item.outcome === "uncertain").length} uncertain. Single deletion is blocked. Reopen Browse saved sessions for the retained exact-target report.</p>}
          <div className="session-list">
            {visibleSessionRows.map(({ session, depth, diagnostic, tooltip }, index) => {
              const menu = activeMenu?.id === session.id ? activeMenu : null;
              const access = sessionActionAccess(session,
                menu ?? { id: session.id, projectId, hostEpoch: status?.hostEpoch ?? null },
                menu ? session.id : selectedSession === session && selectedSessionId.current === session.id ? sessionId : null,
                selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
                mutation?.capability.canMutate() ?? false, batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
                renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current);
              return <div className={`session-row${menu ? " menu-open" : ""}`} key={session.id}
                onContextMenu={event => {
                  const target = event.target as HTMLElement;
                  if (target.closest("input, textarea, select, [contenteditable='true'], .session-actions-menu")) return;
                  event.preventDefault();
                  openSessionMenu(session.id, event.currentTarget.querySelector<HTMLButtonElement>(".session-actions-trigger"));
                }}
                onKeyDown={event => {
                  const editing = !!(event.target as HTMLElement).closest("input, textarea, select, [contenteditable='true']");
                  if (!isSessionContextKey(event.key, event.shiftKey, event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229, editing)) return;
                  event.preventDefault(); event.stopPropagation();
                  openSessionMenu(session.id, event.currentTarget.querySelector<HTMLButtonElement>(".session-actions-trigger"));
                }}>
              <button type="button" aria-pressed={sessionId === session.id} aria-describedby={`session-tooltip-${index}`} title={tooltip}
                style={{ paddingLeft: 11 + Math.min(depth, 8) * 12 }}
                onClick={() => { setMenuTarget(null); focusAction.current = null; selectedSessionId.current = session.id; setSessionId(session.id); setRenamingId(null); setRenamingMessage(""); setDeletingId(null); setDeletingMessage(""); }}>
                <AppIcon name="assistant" size={13} /><span className="session-title">{depth > 0 && <span aria-hidden="true">↳ </span>}{diagnostic && <span aria-hidden="true">⚠ </span>}{plainTitle(session.title)}</span>
                <SessionDraftBadge active={draftIndicators.visible(session.id, sessionId)} />
                {snapshot && <RunningSessionBadge controls={runtimeObservationControls()} tab={{ projectId, sessionId: session.id, path: session.workspacePath }} />}
                <ReminderBadge count={activeReminders?.get(session.id) ?? 0} />
                <span className="session-meta"><span>{session.providerKey ?? t("No provider")}</span><SessionTime value={session.updatedAt} now={clock} /></span>
              </button>
              <span id={`session-tooltip-${index}`} role="tooltip" className="session-tooltip"
                tabIndex={tooltip.length > 256 ? 0 : undefined}>{tooltip}</span>
              <button type="button" className="icon-button session-actions-trigger" aria-label={t("Actions for {title} (ID: {id})", { title: session.title, id: session.id })}
                aria-haspopup="menu" aria-expanded={!!menu}
                onClick={event => openSessionMenu(session.id, event.currentTarget)}><AppIcon name="ellipsis" size={16} /></button>
              {menu && access.open && menuOrigin.current && <SessionTabMenu anchor={menuOrigin.current} container={document.body}
                title={t("Session actions for {title}", { title: session.title })} current={() => true}
                // The menu closes before it runs the chosen entry; clear the target afterwards so the entry still sees it.
                onClose={() => queueMicrotask(() => setMenuTarget(value => value === menu ? null : value))}
                items={[
                  { key: "open", label: t("Open session"), icon: "open", onSelect: () => runSessionMenuAction("open", session, menu) },
                  { key: "rename", label: t("Rename…"), icon: "edit", disabled: !access.rename, onSelect: () => runSessionMenuAction("rename", session, menu) },
                  { key: "delete", label: t("Delete… (confirmation required)"), icon: "trash", danger: true, disabled: !access.delete, onSelect: () => runSessionMenuAction("delete", session, menu) },
                ]} />}
              {renamingId === session.id && <div className="session-rename"><label>{t("New title for {title}", { title: session.title })}
                <input value={renamingTitle} maxLength={256} disabled={!owned || renamingBusy || renameLocked} onChange={event => setRenamingTitle(event.target.value)}
                  onKeyDown={event => { if (event.key === "Enter") void renameSelectedSession(); if (event.key === "Escape") setRenamingId(null); }} /></label>
                <button type="button" disabled={!owned || renamingBusy || renameLocked || !renamingTitle.trim()} onClick={() => void renameSelectedSession()}>{t("Save title")}</button>
                <button type="button" disabled={renamingBusy} onClick={() => setRenamingId(null)}>{t("Cancel")}</button>
                {renamingMessage && <p role="alert" className="notice error-text">{workflowNotice(language.locale, renamingMessage)}</p>}
              </div>}
              {deletingId === session.id && <div className="session-delete" role="group" aria-label={t("Confirm deletion of {title}", { title: session.title })}>
                <p>{t("Delete only this session's journal and history (ID: {id}). Project files are not deleted. This cannot be undone.", { id: session.id })}</p>
                <label>{t("Type the exact session title:")} <strong>{session.title}</strong>
                  <input value={deletingConfirmation} disabled={!owned || deletingBusy || deleteLocked} autoComplete="off"
                    onChange={event => setDeletingConfirmation(event.target.value)} /></label>
                <button type="button" disabled={batchDeletion.locked() || !owned || deletingBusy || deleteLocked || deletingConfirmation !== session.title}
                  onClick={() => void deleteSelectedSession()}>{t("Delete this session")}</button>
                <button type="button" disabled={deletingBusy} onClick={() => setDeletingId(null)}>{t("Cancel")}</button>
                {deletingMessage && <p role="alert" className="notice error-text">{deletingMessage}</p>}
              </div>}
            </div>;
            })}
            {snapshot && visibleSessions.length === 0 && <div className="sidebar-empty">{t(search ? "No matching sessions." : "No sessions in this project.")}</div>}
            <div className="session-list-disclosure">
              {visibleSessionRows.length < loadedSessionRows.length && <button type="button" className="quiet-button" onClick={() => setSessionExpansion({ projectId, search, extra: extraSessions + recentSessionCount })}>{t("Show more…")} <span className="muted-text">({loadedSessionRows.length - visibleSessionRows.length})</span></button>}
              {extraSessions > 0 && <button type="button" className="quiet-button" onClick={() => setSessionExpansion(null)}>{t("Show fewer")}</button>}
            </div>
          </div>
        </aside>}
          content={<ProjectReferenceContext.Provider value={owned && mutation?.capability.canMutate() && !settingsOpen && selectedProject && !selectedProject.archived
            && snapshot && (sessionId === null || !!selectedTab(snapshot, projectId, sessionId))
            ? { expectedEpoch: status!.hostEpoch!, projectId: selectedProject.id, projectPath: selectedProject.path, sessionId,
              lifetime: creationGeneration.current, capturePopup: captureReferenceLifetime,
              observe: value => mutation?.capability.observe(value) } : null}><main className="content">
          <SessionTabStrip state={sessionTabPresentation(tabs, snapshot, projectId, sessionId)} snapshot={snapshot}
            newSessionLabel={t("New session — {project}", { project: selectedProject?.name ?? t("Global") })}
            renderSession={(tab, visible) => {
              const row = snapshot && resolveSessionTab(snapshot, tab);
              if (!snapshot) return null;
              if (!row) return <NonIdealState className="session-unavailable" icon={<AppIcon name="error" size={32} />} title={t("Session unavailable")}
                description={t("This session is no longer in the catalog. Close the tab or refresh the projects.")} />;
              const ownerKey = JSON.stringify([status?.hostEpoch, tabKey(tab), row.createdAt]);
              let owners = sessionPaneOwners.get(ownerKey);
              if (!owners) { owners = createSessionPaneOwners(); sessionPaneOwners.set(ownerKey, owners); }
              return <ProjectReferenceContext.Provider key={ownerKey} value={owned && status?.hostEpoch && tab.projectId !== null
                && snapshot.projects.some(project => project.id === tab.projectId && project.path === tab.path && !project.archived)
                ? { expectedEpoch: status.hostEpoch, projectId: tab.projectId, projectPath: tab.path!, sessionId: row.id,
                  lifetime: creationGeneration.current, capturePopup: captureReferenceLifetime,
                  observe: value => mutation?.capability.observe(value) } : null}>
              <SessionWorkspace session={row} snapshot={snapshot} selectedProjectId={tab.projectId} onRunActivity={running => runtimeObservations.setLive(tab, running)} notesReader={owners.notesReader} observing={visible && view === "workspace" && !settingsOpen}
                active={tab.sessionId === sessionId} notesToggle={notesVisible} onActivate={() => { if (sessionId !== tab.sessionId || fileTabs.active) selectSessionTab(tab); }}
                infoTrigger={sessionInfoTrigger} remindersTrigger={remindersTrigger} compactTrigger={compactTrigger}
                infoLifetime={{ revision: 0, current: () => !!currentSnapshot.current && !!resolveSessionTab(currentSnapshot.current, tab)
                  && currentView.current === "workspace" && !settingsVisible.current && currentHostEpoch.current === status?.hostEpoch }}
                preferredComposerHeight={composerHeights.get(composerSizeKey(status?.hostEpoch ?? null, tab.projectId, row.id))}
                onComposerHeight={height => setComposerHeights(sizes => rememberComposerHeight(sizes, composerSizeKey(status?.hostEpoch ?? null, tab.projectId, row.id), height))}
                onOpenCatalog={navigate} onOpenReminders={openSelectedReminders} onOpenHelp={openHelp} onOpenPalette={openPalette}
                readReminders={readReminders} reminderActions={reminderActions} status={status} mutation={mutation}
                activeReminderCount={activeReminders ? activeReminders.get(row.id) ?? 0 : null}
                autoSend={autoSend.current?.sessionId === row.id ? { text: autoSend.current.text, consume: () => { autoSend.current = null; } } : null}
                submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} draftIndicators={draftIndicators}
                askActions={askActions} display={owners.display} scrollMemory={scrollMemory} runtimeReader={owners.runtimeReader}
                permissionReviewer={owners.permissionReviewer} inputReviewer={owners.inputReviewer} configuration={configurationState.snapshot}
                selections={nextSendSelections} timelineCommand={timelineCommand} /></ProjectReferenceContext.Provider>;
            }}
            capture={captureTabLifetime} dirty={id => draftIndicators.visible(id, sessionId)} observations={runtimeObservationControls()}
            select={selectSessionTab} close={tab => {
              if (!snapshot || snapshot !== currentSnapshot.current || !resolveSessionTab(snapshot, tab)) return;
              const next = closeSessionTab(tabs, tab);
              advanceBrowserRevision();
              closedTabKinds.current = [...closedTabKinds.current, "session" as const].slice(-64);
              tabFocusPending.current = true;
              // Closing the session behind an active file tab does not leave the file.
              const file = fileTabs.active;
              if (tabs.active && tabKey(tabs.active) === tabKey(tab)) { applyTabState(next); if (file) activateFile(file); } else setTabs(next);
            }} reopen={() => tabCommand("reopenTab")}
            files={fileTabs} fileDirty={tab => fileEditors.dirty(fileTabKey(tab))} selectFile={activateFile} closeFile={tab => closeFile(tab)}
            renderFile={(tab, visible) => <ProjectFileEditor key={fileTabKey(tab)} tab={tab} editors={fileEditors}
              epoch={!status ? undefined : owned ? status.hostEpoch : null}
              visible={visible} active={visible && sameFileTab(fileTabs.active, tab)} onActivate={() => activateFile(tab)} />}>
          <div id="active-session-content" className="active-session-content">
          {error && <div className="banner banner-error" role="alert">{error}</div>}
          {draftHandoffNotice && <p role="status" className="notice">{draftHandoffNotice}</p>}
          {!selectedSession
            ? <NewSessionWorkspace key={draftScope} project={selectedProject} chrome={draftChrome}
                preferredHeight={composerHeights.get(composerSizeKey(status?.hostEpoch ?? null, projectId, draftScope))}
                onHeight={height => setComposerHeights(sizes => rememberComposerHeight(sizes, composerSizeKey(status?.hostEpoch ?? null, projectId, draftScope), height))}>
                <ReadOnlyComposer key={draftScope} sessionId={draftScope} provider={null} draftIndicators={draftIndicators}
                  localImages={owned && snapshot?.configured && currentProjectWritable() ? localImages : undefined}
                  onOpenHelp={openHelp} onOpenPalette={openPalette}
                  reason={t("Draft kept locally. Start a session to send it.")}
                  localDraft={{ text: localDraft.text, edit: editLocalDraft, options: creationProviderChoice(true),
                    notice: draftChoices.failed && <p role="status" className="composer-notice">{t("Some draft choices are unavailable. Refresh choices to try again.")}</p>,
                    disabled: newPromptDisabled, busy: creatingBusy, submit: () => void createSelectedSession(true),
                    surface: owned && status?.hostEpoch ? { epoch: status.hostEpoch, onOpenProviders: () => navigate("providers"),
                      contextTokens: draftChoices.models.find(model => model.id === draftChoices.value.modelId)?.contextTokens ?? null } : undefined,
                    action: <>
                      {draftChoices.failed && <Button variant="minimal" icon={<AppIcon name="refresh" size={16} />} disabled={creatingBusy || creationLocked}
                        aria-label={t("Refresh composer choices")} title={t("Refresh composer choices")} onClick={draftChoices.refresh} />}
                     <SendSplitButton enqueue={false} onEnqueueChange={() => { /* A session that has not started has nothing to wait for. */ }} enqueueDisabled optionsDisabled={newPromptDisabled}>
                       <Button intent="primary" icon={<AppIcon name="send" size={16} />} disabled={newPromptDisabled}
                         aria-label={t("Start session")} title={t("Start the session and send (Enter)")}
                         onClick={() => void createSelectedSession(true)} />
                     </SendSplitButton>
                     {creatingBusy && <Button variant="minimal" icon={<AppIcon name="stop" size={16} />} aria-label={t("Cancel transfer")}
                       onClick={() => { invalidateCreation(); setDraftHandoffNotice("Transfer canceled locally. Creation may still complete; original text retained. Inspect sessions; nothing sent."); }} />}
                  </> }} />
               </NewSessionWorkspace>
            : null}
          </div></SessionTabStrip></main></ProjectReferenceContext.Provider>} />
      </div>
    {settingsOpen && <SettingsOverlay section={settingsSection} onSection={navigate} onClose={closeSettings}>
      {settingsSection === "appearance" ? <ConfigurationPanel preferences={{ theme, setTheme, shownTheme, colorScheme, setColorScheme, sort: projectSort, setSort: setProjectSort, desktopCollapsed: railState.desktopCollapsed, setDesktopCollapsed, notices: preferenceNotices, recentSessionCount, setRecentSessionCount: value => { batchDeletion.invalidate(); setRecentSessionCount(value); } }} />
      : settingsSection === "about" ? <AboutSettings status={status} bootError={!!error} demo={demoMode} logo={logoUrl} />
      : settingsSection === "plugins" ? <PluginSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} />
      : settingsSection === "skills" ? <SkillSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} />
      : settingsSection === "mcp" ? <McpServerSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} />
      : settingsSection === "prompts" ? <AgentPromptSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} />
      : settingsSection === "config" ? <ConfigEditorPanel epoch={owned ? status!.hostEpoch : null} onApplied={() => void refreshConfiguration()} />
      : settingsSection === "logs" ? <>
        <ApplicationLogsPanel clearActions={logClearActions} read={demoMode
          ? async () => ({ status: "unavailable", rows: [], captureOmitted: "0", readOmitted: 0, captureId: null, boundary: "0", grant: "" }) : applicationLogs.read} /></>
      : settingsSection === "providers" ? owned && status?.hostEpoch
        ? <ProviderSettings epoch={status.hostEpoch} readRuntime={modelCatalog.providers} probe={modelCatalog.probe}
          onOpenModels={() => navigate("models")} onOpenConfiguration={() => navigate("config")} onApplied={() => void refreshConfiguration()} />
        : <ProvidersPanel epoch={null} read={modelCatalog.providers} probe={modelCatalog.probe} catalogProviders={configurationState.snapshot?.providers}
          holds={providerProbeHolds} onOpenModels={() => navigate("models")} />
      : <ModelCatalogPanel epoch={owned ? status!.hostEpoch : null}
        readProviders={modelCatalog.providers} readModels={modelCatalog.models} readChoices={sessionOperations.choices}
        target={owned && currentProjectWritable() && selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId
          ? { sessionId: selectedSession.id, epoch: status!.hostEpoch! } : null}
        selections={nextSendSelections} pendingSend={!!(selectedSession && submissions.pending(selectedSession.id))}
        pendingSelection={selectedSession ? submissions.pending(selectedSession.id)?.request.selection ?? null : null}
        onApply={async (target, signal) => {
          const result = await applyCatalogNextSend(target, () => ({ epoch: currentHostEpoch.current ?? null,
            active: settingsVisible.current && currentSettingsSection.current === "models",
            sessionId: !signal.aborted && selectedScope.current === projectId ? selectedSessionId.current : null,
            canMutate: !!mutation?.capability.canMutate() && currentProjectWritable(), pending: !!submissions.pending(target.sessionId) }),
          async (epoch, sessionId) => {
            const value = await sessionOperations.choices({ expectedEpoch: epoch, sessionId }, { signal, timeoutMilliseconds: 15000 });
            mutation?.capability.observe(value);
            return value;
          }, nextSendSelections);
          if (result === "applied" && !signal.aborted) closeSettings();
          return result;
        }} />}
    </SettingsOverlay>}
    {dialog === "project" && <OpenProjectDialog snapshot={snapshot} getCurrentSnapshot={() => currentSnapshot.current}
      epoch={owned ? status?.hostEpoch : undefined}
      capability={owned ? mutation?.capability : undefined} opening={projectOpening}
      allowCompletion={!demoMode}
      getCurrentEpoch={() => owned ? currentHostEpoch.current ?? undefined : undefined}
      getCurrentScope={() => ({ projectId: selectedScope.current, sessionId: selectedSessionId.current })}
      completeDirectory={workspace.completeDirectory}
      onOpen={shown => {
        if (projectOpening.getSnapshot() || currentSnapshot.current !== snapshot || !savedProjectSelection(shown, currentSnapshot.current)) return false;
        selectProject(shown.id); setDialog(null); return true;
      }} onRefresh={refreshProjects}
      onImported={async (id, path, signal) => {
        const original = projectOpening.getSnapshot();
        const previousScope = selectedScope.current;
        const previousSession = selectedSessionId.current;
        if (original?.kind !== "imported" || original.epoch !== currentHostEpoch.current ||
          original.path !== path || original.projectId !== id) return false;
        const fresh = await refreshProjects(signal);
        if (!fresh?.configured || signal.aborted || !mutation?.capability.canMutate() ||
          currentHostEpoch.current !== original.epoch || selectedScope.current !== previousScope ||
          selectedSessionId.current !== previousSession || projectOpening.getSnapshot() !== original
          || !fresh.projects.some(project => project.id === id)) return false;
        selectedScope.current = id;
        setProjectId(id);
        const nextSession = sessionsForProject(fresh, id)[0]?.id ?? null;
        selectedSessionId.current = nextSession;
        setSessionId(nextSession);
        navigate("workspace");
        return true;
      }} onClose={() => setDialog(null)} />}
    {dialog === "help" && <CommandHelp onClose={closeHelp} />}
    {dialog === "file" && filePickerProject && <OpenFileDialog epoch={status!.hostEpoch!} project={filePickerProject}
      observe={value => mutation?.capability.observe(value)} onClose={() => setDialog(null)}
      onOpen={path => { setDialog(null); openFile({ projectId: filePickerProject.id, projectPath: filePickerProject.path, path }); }} />}
    {fileClosing && <UnsavedFileDialog name={fileTabName(fileClosing.tab)} mode="close" busy={fileClosing.busy}
      onSave={() => void saveAndCloseFile(fileClosing.tab)} onDiscard={() => closeFile(fileClosing.tab, true)}
      onCancel={() => { if (!fileClosing.busy) setFileClosing(null); }} />}
    {dialog === "sessions" && browserCapture && <SessionBrowser snapshot={browserCapture.snapshot} projectId={browserCapture.projectId} observations={runtimeObservationControls()} recentCount={recentSessionCount} activeSessionId={sessionId} batch={batchDeleteControls()}
      stale={browserCapture.revision !== browserRevision.current || browserCapture.hostReady !== (mutation?.capability.canMutate() ?? false) || view !== "workspace" || settingsOpen}
      close={() => setDialog(null)} open={tab => {
        if (view !== "workspace" || settingsVisible.current || browserCapture.hostReady !== (mutation?.capability.canMutate() ?? false)
          || !browserActivation(currentSnapshot.current, tab, browserCapture.revision, browserRevision.current)) return false;
        selectSessionTab(tab); setDialog(null); return true;
      }} />}
    <ProjectArchiveDialog owner={projectArchive} open={dialog === "archive" && !settingsOpen && view === "workspace"} close={() => setDialog(null)}
      current={() => {
        const matches = currentSnapshot.current?.projects.filter(project => project.id === selectedScope.current);
        const project = matches?.length === 1 ? matches[0] : undefined;
        if (!project || !owned || !mutation?.capability.canMutate() || !currentHostEpoch.current || settingsVisible.current || currentView.current !== "workspace") return null;
        return { epoch: currentHostEpoch.current, id: project.id, path: project.path, archived: project.archived,
          generation: browserRevision.current + creationGeneration.current };
      }} refresh={async epoch => {
        if (currentHostEpoch.current !== epoch || !mutation?.capability.canMutate()) return;
        const version = browserRevision.current;
        const fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 });
        if (creationAlive.current && currentHostEpoch.current === epoch && mutation.capability.canMutate() && version === browserRevision.current && fresh.configured)
          publishWorkspaceState({ kind: "ready", snapshot: fresh });
      }} />
    {paletteOpen && <CommandPalette available={commandAvailable} onChoose={choosePalette} onClose={dismissPalette} />}
  </div></ShellLanguageContext.Provider>;
}

// Native modal matches the other shell dialogs: showModal supplies inert background,
// browser-managed focus trapping and nested native About dialog top-layer ordering.
function SettingsOverlay({ section, onSection, onClose, children }: {
  section: SettingsSection; onSection: (section: SettingsSection) => void;
  onClose: () => void; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  const composingEscape = useRef(false);
  const destinations: readonly [MessageKey, readonly [SettingsSection, MessageKey, IconName][]][] = [
    ["Personalization", [["appearance", "Appearance", "palette"]]],
    ["Agent & models", [["providers", "Providers", "provider"], ["models", "Models", "model"], ["prompts", "Agent prompts", "assistant"], ["skills", "Skills", "skill"]]],
    ["Extensions", [["plugins", "Plugins", "plugin"], ["mcp", "MCP Servers", "server"]]],
    ["Advanced", [["config", "Configuration file", "config"]]],
    ["Diagnostics", [["logs", "Application Logs", "logs"], ["about", "About", "info"]]],
  ];
  return <AppWindow storageKey="codealta.desktop.window.settings.v1" className="settings-dialog" titleId="settings-title" title={t("Settings")}
    preferredSize={viewport => ({ width: viewport.width * 0.8, height: viewport.height * 0.8 })} minimumSize={{ width: 560, height: 360 }}
    onClose={onClose} closeLabel={t("Close settings")}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) composingEscape.current = true;
      else onClose();
    }} onKeyUp={() => { composingEscape.current = false; }} onCompositionEnd={() => { composingEscape.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composingEscape.current) onClose(); }}>
    <div className="settings-dialog-body">
      <nav className="settings-dialog-navigation" aria-label={t("Settings pages")}>
        {destinations.map(([group, pages]) => <div className="settings-dialog-group" key={group}>
          <h3>{t(group)}</h3>
          {pages.map(([value, label, icon]) => <button key={value} type="button" aria-current={section === value ? "page" : undefined}
            data-settings-section={value} onClick={() => onSection(value)}><AppIcon name={icon} size={16} /><span>{t(label)}</span></button>)}
        </div>)}
      </nav>
      <div className="settings-dialog-content" key={section}>{children}</div>
    </div>
  </AppWindow>;
}

function createSessionPaneOwners() {
  return {
    display: createSessionDisplayStore(sessionDisplay.observe),
    runtimeReader: createRuntimeStateReader(sessionRuntimeState.current),
    notesReader: createNotesReader(sessionNotes.current, sessionNotes.clear),
    permissionReviewer: createPermissionReviewer(sessionPermissions.list, sessionPermissions.resolve),
    inputReviewer: createUserInputReviewer(
      request => sessionUserInput.list(request, { timeoutMilliseconds: 8000 }),
      request => sessionUserInput.resolve({ ...request, answers: request.answers.map(answer => ({ ...answer })) }, { timeoutMilliseconds: 8000 }),
      request => sessionUserInput.cancel(request, { timeoutMilliseconds: 8000 })),
  };
}

// The working folder shown beside a composer (global sessions have none) and the plugin status items above it.
function useComposerChrome(epoch: string | null, project: WorkspaceSnapshot["projects"][number] | undefined): ComposerChromeValue {
  const id = project?.id, name = project?.name, path = project?.path;
  return useMemo(() => ({ context: id !== undefined && name !== undefined && path !== undefined
    ? <ProjectContext epoch={epoch} project={{ id, name, path }} read={projectGit.status} /> : undefined,
  status: epoch ? <ComposerStatus epoch={epoch} projectId={id ?? null} read={composerStatus.read} /> : undefined }), [epoch, id, name, path]);
}

function SessionWorkspace({ session, snapshot, selectedProjectId, preferredComposerHeight, onComposerHeight, infoTrigger: sharedInfoTrigger, infoLifetime, remindersTrigger: sharedRemindersTrigger, compactTrigger: sharedCompactTrigger, onOpenReminders, onOpenHelp, onOpenPalette, readReminders, reminderActions, status, mutation, submissions, steering, compaction, abortRuns, queue, draftIndicators, askActions, display, scrollMemory, runtimeReader, permissionReviewer, inputReviewer, configuration: configurationSnapshot, selections, timelineCommand, onOpenCatalog, active = true, observing = true, notesToggle, onActivate, notesReader, activeReminderCount = null, autoSend = null, onRunActivity }: {
  /** Reports whether the session is working while its panel watches it. */
  onRunActivity?: (running: boolean | null) => void;
  /** A draft prompt to send once this session's composer holds it. */
  autoSend?: { text: string; consume: () => void } | null;
  /** Active reminders of this session as last reported by the host; null while unknown. */
  activeReminderCount?: number | null;
  notesReader: ReturnType<typeof createNotesReader>;
  observing?: boolean;
  active?: boolean; notesToggle?: boolean; onActivate?: () => void;
  session: WorkspaceSession;
  snapshot: WorkspaceSnapshot;
  selectedProjectId: string | null;
  preferredComposerHeight: number | undefined;
  onComposerHeight: (height: number | undefined) => void;
  infoTrigger: RefObject<HTMLButtonElement | null>;
  infoLifetime: SessionInfoLifetime;
  remindersTrigger: RefObject<HTMLButtonElement | null>;
  onOpenReminders: (sessionId: string, epoch: string, projectId: string | null) => void;
  onOpenHelp: () => void;
  onOpenPalette: () => void;
  onOpenCatalog: (page: "models" | "prompts" | "providers") => void;
  readReminders: (request: ReminderListRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ReminderListResponse>;
  reminderActions: ReturnType<typeof createReminderActions>;
  compactTrigger: RefObject<HTMLButtonElement | null>;
  status: BootStatus | undefined;
  mutation: { epoch: string; capability: ReturnType<typeof createMutationCapability> } | undefined;
  submissions: ReturnType<typeof createOwnedSubmissions>;
  steering: ReturnType<typeof createSteeringSubmissions>;
  compaction: ReturnType<typeof createCompactionSubmissions>;
  abortRuns: ReturnType<typeof createAbortRunSubmissions>;
  queue: ReturnType<typeof createQueueSubmissions>;
  draftIndicators: ReturnType<typeof createDraftIndicators>;
  askActions: ReturnType<typeof createAskActions>;
  display: ReturnType<typeof createSessionDisplayStore>;
  scrollMemory: ReturnType<typeof createTimelineScrollMemory>;
  runtimeReader: ReturnType<typeof createRuntimeStateReader>;
  permissionReviewer: ReturnType<typeof createPermissionReviewer>;
  inputReviewer: ReturnType<typeof createUserInputReviewer>;
  configuration: ConfigurationSnapshot | undefined;
  selections: ReturnType<typeof createNextSendSelectionStore>;
  timelineCommand: RefObject<TimelineCommand | null>;
}) {
  const { t, locale: languageLocale } = useShellLanguage();
  const [persistedUsage, setPersistedUsage] = useState<string | null>(null);
  const localInfoTrigger = useRef<HTMLButtonElement>(null), localRemindersTrigger = useRef<HTMLButtonElement>(null), localCompactTrigger = useRef<HTMLButtonElement>(null);
  const infoTrigger = active ? sharedInfoTrigger : localInfoTrigger;
  const remindersTrigger = active ? sharedRemindersTrigger : localRemindersTrigger;
  const compactTrigger = active ? sharedCompactTrigger : localCompactTrigger;
  const [historyNotes, onNotesChange] = useState("");
  const [infoOpen, setInfoOpen] = useState(false);
  const infoActive = useRef(false);
  const askRefresh = useRef<(() => void) | null>(null);
  const [timelineNotices, setTimelineNotices] = useState<HTMLDivElement | null>(null);
  const [infoFocusRestoration] = useState(createPaletteFocusRestoration);
  useEffect(() => () => infoFocusRestoration.cancel(), [infoFocusRestoration]);
  function openInfo() {
    if (infoActive.current || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    infoFocusRestoration.cancel();
    infoActive.current = true;
    setInfoOpen(true);
  }
  function closeInfo() {
    const trigger = infoTrigger.current;
    infoActive.current = false;
    setInfoOpen(false);
    infoFocusRestoration.schedule(trigger, () => !infoActive.current && infoTrigger.current === trigger && !trigger?.disabled,
      () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'));
  }
  const timeline = useTimelinePosition(session.id, scrollMemory);
  const composer = useComposerLayout(preferredComposerHeight, onComposerHeight);
  const [messageNotice, setMessageNotice] = useState<TimelineNotice>(null);
  const [newerOmitted, setNewerOmitted] = useState(false);
  const newest = useExplicitNewestHistory(session.id, selectedProjectId, status?.hostEpoch ?? null, timeline, setMessageNotice, !newerOmitted);
  // Stable across History's auto-pages; do not cancel an admitted request on a parent render.
  const resetMessageNotice = useCallback((generation: number, explicitNewest: boolean) => {
    timeline.resetMessageNavigation();
    if (newest.onTarget(generation)) return;
    if (explicitNewest) timeline.pauseIfUnfollowed();
    setMessageNotice(null);
  }, [timeline.resetMessageNavigation, timeline.pauseIfUnfollowed, newest.onTarget]);
  useLayoutEffect(() => {
    if (demoMode || !active) return;
    const command: TimelineCommand = { sessionId: session.id, projectId: selectedProjectId, epoch: status?.hostEpoch ?? null,
      ready: timeline.messageReady,
      navigate: action => {
        newest.cancel();
        const result = timeline.navigateMessage(action);
        setMessageNotice(result.status === "unavailable" ? { key: "Retained history is not ready for message navigation." }
          : result.status === "boundary" ? action === "messageNext"
            ? { key: "Last retained message in this window. This may not be the newest persisted history; use Refresh newest history." }
            : { key: "First retained message in this window. Older journal history may be available via Load older history." }
          : { key: action === "messageFirst" ? "First retained message (not necessarily the first journal message): {label}" : "Retained message: {label}", parameters: result.label === undefined ? undefined : { label: result.label } });
      },
      latestReady: newest.available, latest: newest.latest, cancelLatest: newest.cancel };
    timelineCommand.current = command;
    return () => { if (timelineCommand.current === command) timelineCommand.current = null; };
  });
  const observedDisplay = useSyncExternalStore(display.subscribe, display.getSnapshot);
  useSyncExternalStore(submissions.subscribe, submissions.getSnapshot);
  const live = status?.hostEpoch && observedDisplay.hostEpoch === status.hostEpoch && observedDisplay.sessionId === session.id
    ? observedDisplay : null;
  const archivedScope = archivedProjectScope(snapshot, selectedProjectId);
  const ownedHost = !!(status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch);
  const ownedSession = ownedHost && !archivedScope;
  // A prompt sent from here is shown at once: the timeline goes to its end, wherever the reader was.
  const outgoingCount = ownedSession && status?.hostEpoch ? submissions.outgoing(status.hostEpoch, session.id).length : 0;
  const shownOutgoing = useRef(outgoingCount);
  useLayoutEffect(() => {
    if (outgoingCount > shownOutgoing.current && !newerOmitted) timeline.jump();
    shownOutgoing.current = outgoingCount;
  }, [outgoingCount]);
  const pluginEpoch = ownedHost ? status!.hostEpoch! : null;
  const sessionProject = snapshot.projects.find(project => project.id === selectedProjectId);
  const chrome = useComposerChrome(pluginEpoch, sessionProject);
  const readPluginEvents = useMemo(() => pluginEpoch === null ? undefined
    : createPluginEventsRead(sessionPluginEvents.read, { epoch: pluginEpoch, sessionId: session.id, projectId: selectedProjectId }),
  [pluginEpoch, session.id, selectedProjectId]);
  const infoControl = <Button ref={infoTrigger} variant="minimal" className="session-info-trigger" icon={<AppIcon name="info" size={16} />}
    aria-label={t("Session info")} title={`${t("Session info")} (Ctrl+G, Ctrl+T)`} aria-haspopup="dialog" aria-expanded={infoOpen}
    onClick={openInfo} />;
  return <div className="session-workspace" data-active={active} ref={composer.workspaceRef} onFocusCapture={onActivate} onPointerDownCapture={onActivate}>
    {infoOpen && <SessionInfoDialog info={sessionInfoView(snapshot, session, selectedProjectId)} demo={demoMode} onClose={closeInfo}
      lifetime={infoLifetime} canRead={() => !!mutation?.capability.canMutate()}
      target={ownedSession && !demoMode && mutation?.capability.canMutate() ? runtimeTarget(snapshot, { sessionId: session.id, projectId: selectedProjectId, path: session.workspacePath }, status?.hostEpoch ?? undefined) : null} />}
    {demoMode
      ? <DemoConversation session={session} />
      : <>
        <div className="session-timeline-area">
        <div className="timeline-scroll" ref={timeline.elementRef}
          onScroll={event => { newest.onScroll(); if (!newest.pending()) timeline.scroll(event.currentTarget); }}
          onWheel={event => { newest.cancel(); timeline.wheel(event); }} onKeyDown={timeline.keyDown}
          onPointerDown={event => { newest.cancel(); timeline.pointerDown(event); }}
          onPointerMove={timeline.pointerMove} onPointerUp={timeline.pointerEnd} onPointerCancel={timeline.pointerEnd}>
        <History observing={observing} sessionId={session.id} messageCount={session.messageCount} canInspect={() => infoLifetime.current()} onNotesChange={onNotesChange} onUsageChange={setPersistedUsage} onSettled={() => {
          timeline.settled(); if (!newest.pending()) timeline.pauseIfUnfollowed();
        }}
          onBeforeOlder={timeline.beforeOlderPage} onAfterOlder={timeline.afterOlderPage} onNewerOmitted={setNewerOmitted}
          onNavigationReset={resetMessageNotice} newestRequest={newest.requestRef} onNewestResult={newest.onResult}
          read={readTimeline} readPluginEvents={readPluginEvents}
          outgoing={ownedSession && status?.hostEpoch ? submissions.outgoing(status.hostEpoch, session.id) : []}
          onAcknowledgeOutgoing={submissions.acknowledgeOutgoing}
          live={ownedSession ? live?.snapshot?.session ?? null : null} />
        {ownedSession && status?.hostEpoch
        ? <>
          <LiveSessionPanel observing={observing} store={display} hostEpoch={status.hostEpoch} sessionId={session.id} capability={mutation!.capability} />
          <div className="timeline-notices" ref={setTimelineNotices} />
          {status.ownedAsksEnabled && <AskPanel observing={observing} epoch={status.hostEpoch} sessionId={session.id} actions={askActions} capability={mutation!.capability} refreshTrigger={askRefresh} />}
          {status.ownedUserInputEnabled && <UserInputPanel epoch={status.hostEpoch} sessionId={session.id} reviewer={inputReviewer} capability={mutation!.capability}
            canReview={() => infoLifetime.current()} />}
        </>
        : null}
        </div>
        <SessionNotesOverlay observing={observing} sessionId={session.id} epoch={ownedSession ? status?.hostEpoch : undefined} capability={mutation?.capability}
          fallbackMarkdown={historyNotes} toggle={active ? notesToggle : undefined} reader={notesReader} />
        </div>
        {!timeline.following && <button type="button" className="timeline-bottom-button" onClick={() => { newest.cancel(); timeline.jump(); }}><AppIcon name="arrowDown" size={14} />{t(newerOmitted ? "Bottom of retained window (not newest)" : "Jump to latest visible")}</button>}
        {messageNotice && <p role="status" className="detail timeline-navigation-notice">{timelineNotice(languageLocale, messageNotice)}</p>}
        <div className="composer-resize-bar" ref={composer.barRef}>
          <ComposerSplitter {...composer.splitter} />
        </div>
        <div ref={composer.regionRef} className={`composer-region${composer.height === undefined ? "" : " resized"}`}
          style={composer.height === undefined ? undefined : { height: composer.height }}>
        <SessionComposerGate snapshot={snapshot} projectId={selectedProjectId} session={session} chrome={chrome}
          epoch={ownedHost ? status!.hostEpoch! : null}
          owned={status?.hostEpoch && mutation ? <OwnedSessionPanel observing={observing} active={active} onRunActivity={onRunActivity} sessionId={session.id} epoch={status.hostEpoch} submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} capability={mutation.capability} runtimeReader={runtimeReader} permissionReviewer={status.commandReviewEnabled ? permissionReviewer : null} configuration={configurationSnapshot} draftIndicators={draftIndicators} selections={selections}
              persistedUsage={persistedUsage} usageTarget={ownedSession && verifiedReminderCountTarget(snapshot, session, selectedProjectId) ? {
                epoch: status.hostEpoch, sessionId: session.id, scope: selectedProjectId === null ? "global" : "project",
                projectId: selectedProjectId, expectedProjectPath: selectedProjectId === null ? null : session.workspacePath } : null}
              onOpenCatalog={onOpenCatalog} timelineNotices={timelineNotices} liveState={ownedSession ? live : null} inputLifetime={infoLifetime} remindersTrigger={remindersTrigger} compactTrigger={compactTrigger} infoControl={infoControl} projectId={selectedProjectId} onOpenReminders={() => onOpenReminders(session.id, status.hostEpoch!, selectedProjectId)} onOpenHelp={onOpenHelp} onOpenPalette={onOpenPalette}
              activeReminderCount={activeReminderCount} autoSend={autoSend} reminderActions={reminderActions} readReminderCount={ownedSession && verifiedReminderCountTarget(snapshot, session, selectedProjectId) ? readReminders : undefined} /> : null}
          readOnly={<ReadOnlyComposer active={active} sessionId={session.id} provider={session.providerKey} draftIndicators={draftIndicators} infoControl={infoControl} onOpenHelp={onOpenHelp} onOpenPalette={onOpenPalette}
              reason={archivedScope ? t("Archived project; this session is read-only. Sending is unavailable.") : undefined} />}
          recovery={ownedHost ? <ArchivedActionRecovery epoch={status!.hostEpoch!} sessionId={session.id} submissions={submissions}
            steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue}
            asks={askActions} inputs={inputReviewer} permissions={permissionReviewer} /> : null} />
        </div>
      </>}
  </div>;
}

function DemoConversation({ session }: { session: WorkspaceSession }) {
  const [text, setText] = useState("");
  const [messages, setMessages] = useState([
    { role: "user", text: "Create a usable desktop workspace I can run locally." },
    { role: "assistant", text: "The first interactive workspace is running. Project and session navigation, configuration surfaces, a transcript, and this composer are ready to try." },
  ]);
  function submit() {
    const value = text.trim();
    if (!value) return;
    setMessages(current => [...current, { role: "user", text: value }, { role: "assistant", text: "Demo response: the packaged app sends this through the shared session runtime. This browser preview keeps everything in memory." }]);
    setText("");
  }
  return <section className="conversation" aria-label={`Demo conversation for ${session.title}`}>
    <div className="demo-banner"><strong>Interactive browser demo</strong><span>Messages are local and disappear on refresh. Run the packaged desktop for real sessions.</span></div>
    <div className="messages">
      {messages.map((message, index) => <article key={index} className={`message message-${message.role}`}>
        <div className="avatar"><AppIcon name={message.role === "user" ? "user" : "assistant"} size={17} /></div><div><strong>{message.role === "user" ? "You" : "CodeAlta"}</strong><p>{message.text}</p></div>
      </article>)}
    </div>
    <div className="composer">
      <textarea aria-label="Message" value={text} onChange={event => setText(event.target.value)} onKeyDown={event => {
        if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); submit(); }
      }} placeholder="Ask CodeAlta to work on this project…" />
      <div className="composer-footer"><span>Enter to send · Shift+Enter for a new line</span><button type="button" onClick={submit} disabled={!text.trim()}>Send <AppIcon name="send" size={14} /></button></div>
    </div>
  </section>;
}

function ConfigurationPanel({ preferences }: { preferences: Parameters<typeof GeneralSettings>[0] }) {
  const { t } = useShellLanguage();
  return <div className="configuration-page settings-card-page">
    <header className="page-heading"><span className="eyebrow">{t("Personalization")}</span><h1>{t("Appearance")}</h1></header>
    <div className="settings-grid"><GeneralSettings {...preferences} /></div>
  </div>;
}

function LoadingRows() { return <div className="loading-rows"><span /><span /><span /></div>; }
function SessionTime({ value, now }: { value: string; now: number }) {
  const { locale } = useShellLanguage();
  const { label, title, dateTime } = sessionTime(value, locale, now);
  return <time dateTime={dateTime} title={title}>{label}</time>;
}

function PaneSplitter({ className, hidden, label, value, onResize, onReset }: {
  className: string;
  hidden?: boolean;
  label: string;
  value: number;
  onResize: (delta: number) => void;
  onReset: () => void;
}) {
  const lastX = useRef<number | undefined>(undefined);
  function pointerDown(event: PointerEvent<HTMLDivElement>) {
    lastX.current = event.clientX;
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function pointerMove(event: PointerEvent<HTMLDivElement>) {
    if (lastX.current === undefined || !event.currentTarget.hasPointerCapture(event.pointerId)) return;
    const delta = event.clientX - lastX.current;
    lastX.current = event.clientX;
    onResize(delta);
  }
  function pointerEnd(event: PointerEvent<HTMLDivElement>) {
    lastX.current = undefined;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  }
  function keyDown(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      onResize(event.key === "ArrowLeft" ? -16 : 16);
    } else if (event.key === "Home") {
      event.preventDefault();
      onReset();
    }
  }
  return <div className={`pane-splitter ${className}`} hidden={hidden} role="separator" aria-label={label} aria-orientation="vertical" aria-valuenow={value} aria-valuemin={minimumIdeWidth} aria-valuemax={maximumIdeWidth}
    tabIndex={0} onPointerDown={pointerDown} onPointerMove={pointerMove} onPointerUp={pointerEnd} onPointerCancel={pointerEnd}
    onDoubleClick={onReset} onKeyDown={keyDown}><span /></div>;
}

installKeyboardClickGuard(window);
createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);
