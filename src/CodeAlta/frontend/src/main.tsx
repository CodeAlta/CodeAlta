import { StrictMode, useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent, type RefObject, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import {
  boot, configuration, applicationLogs, modelCatalog, promptCatalog, mcpInventory, reminder, workspace, sessionDisplay, sessionRuntimeState, sessionPermissions, sessionOperations,
  sessionAsks, sessionNotes, sessionUserInput, type BootStatus,
  type ReminderListRequest,
  type ReminderListResponse,
  type ReminderDetailRequest,
  type ConfigurationSnapshot, type WorkspaceSession, type WorkspaceSnapshot,
} from "#neoastra";
import { loadWorkspace, sessionsForProject, workspaceNotice, type WorkspaceState } from "./workspace";
import { History } from "./HistoryPanel";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { ReadOnlyComposer } from "./ReadOnlyComposer";
import { ModelCatalogPanel } from "./ModelCatalogPanel";
import { ProvidersPanel } from "./ProvidersPanel";
import { PromptCatalogPanel } from "./PromptCatalogPanel";
import { McpServersPanel } from "./McpServersPanel";
import { archivedProjectScope, ReminderScopeGate, SessionComposerGate } from "./ArchivedScopeGates";
import { ArchivedActionRecovery } from "./ArchivedActionRecovery";
import { createReminderActions } from "./reminderActions";
import { verifiedReminderCountTarget } from "./reminderListObservation";
import { applyCatalogNextSend, applyPromptNextSend, createNextSendSelectionStore } from "./nextSendSelection";
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
import { NotesPanel } from "./NotesPanel";
import { createNotesReader } from "./sessionNotes";
import { notesHeightKey, defaultNotesHeight, restoreNotesHeight, persistNotesHeight, resizeNotesHeight } from "./notesHeight";
import { createUserInputReviewer } from "./sessionUserInput";
import { UserInputPanel } from "./UserInputPanel";
import { LiveSessionPanel } from "./LiveSessionPanel";
import { createTimelineScrollMemory, useExplicitNewestHistory, useTimelinePosition, type MessageNavigation } from "./timelineScroll";
import type { ShortcutAction } from "./shortcuts";
import { dispatchWorkspaceShortcut, type WorkspaceShortcutState } from "./workspaceShortcutDispatch";
import { activateContextShortcut } from "./contextShortcut";
import { createDraftIndicators } from "./promptDraft";
import { SessionDraftBadge } from "./SessionDraftBadge";
import { collapsedSessionWidth, constrainPaneLayout, defaultPaneLayout, persistPaneLayout, resizeCollapsedSessionPane, resizePane, restorePaneLayout, type PaneName } from "./paneLayout";
import { composerBounds, composerSizeKey, rememberComposerHeight, resizeComposerHeight } from "./composerHeight";
import { AppIcon } from "./AppIcon";
import { sessionTime } from "./sessionTime";
import { createProjectOpening } from "./projectOpening";
import { OpenProjectDialog } from "./OpenProjectDialog";
import { savedProjectSelection } from "./savedProjectSelection";
import { createSessionCreation, createdSessionSelection, sessionCreationMessage, type SessionTarget } from "./sessionCreation";
import { createSessionRename, renamedSessionVisible, renameSelectionCurrent, sessionRenameMessage, type RenameTarget } from "./sessionRename";
import { createSessionDeletion, deletedSessionRecovery, deleteSelectionCurrent, sessionDeletionMessage, type DeletedTarget } from "./sessionDeletion";
import { createProjectRename, projectNameVisible, projectRenameMessage, projectRenameSelectionCurrent, type ProjectNameTarget } from "./projectRename";
import { sessionHierarchy } from "./sessionHierarchy";
import { SessionActionMenu } from "./SessionActionMenu";
import { isSessionContextKey, restoreSessionMenuFocus, sessionActionAccess, type SessionAction, type SessionMenuTarget } from "./sessionRowActions";
import { projectRailProjection, type ProjectSort } from "./projectRail";
import { ProjectRailRows } from "./ProjectRailRows";
import { ProjectRailToggle } from "./ProjectRailToggle";
import { focusVisibleProject, projectRailVisible, restoreProjectRailFocus } from "./projectRailVisibility";
import { useWindowPreferences } from "./windowPreferences";
import { GeneralSettings } from "./GeneralSettings";
import { ApplicationLogsPanel } from "./ApplicationLogsPanel";
import { AboutDialog, AboutSettingsEntry } from "./AboutDialog";
import { ProjectDetailsEntry, type ProjectDetailsContext } from "./ProjectDetailsEntry";
import { createApplicationLogClearActions } from "./applicationLogClear";
import { SessionInfoDialog } from "./SessionInfoDialog";
import { selectedSessionInfoAvailable, selectedSessionInfoSelection, sessionInfoView } from "./sessionInfo";
import { CommandPalette } from "./CommandPalette";
import { createPaletteFocusRestoration, paletteAvailable, paletteShortcut, type PaletteAction, type PaletteContext } from "./paletteActions";
import "./style.css";

type TimelineCommand = Readonly<{ sessionId: string; projectId: string | null; epoch: string | null;
  ready: () => boolean; navigate: (action: MessageNavigation) => void;
  latestReady: () => boolean; latest: () => void; cancelLatest: () => void }>;

const demoMode = import.meta.env.VITE_DEMO_MODE === "true";
type View = "workspace" | "appearance" | "providers" | "models" | "prompts" | "reminders" | "mcp" | "logs" | "skills" | "plugins" | "about";
type SettingsSection = Exclude<View, "workspace" | "reminders">;
type SettingsCardPage = Exclude<SettingsSection, "models" | "mcp">;
const paneLayoutStorageKey = "codealta.desktop.panes.v1";

function App() {
  // Original create presentation authority only; never a backend receipt or retry grant.
  const creationGeneration = useRef(0);
  const invalidateCreation = () => { creationGeneration.current++; };
  const [logClearActions] = useState(() => createApplicationLogClearActions(applicationLogs.clear));
  const [status, writeStatus] = useState<BootStatus>();
  function setStatus(value: BootStatus) { invalidateCreation(); writeStatus(value); }
  const [error, setError] = useState<string>();
  const [workspaceState, setWorkspaceState] = useState<WorkspaceState>({ kind: "loading" });
  const currentSnapshot = useRef<WorkspaceSnapshot | undefined>(undefined);
  const projectInspection = useRef({ version: 0, ready: false });
  const [, setProjectInspectionVersion] = useState(0);
  function markProjectInspection(ready: boolean) {
    projectInspection.current = { version: projectInspection.current.version + 1, ready };
    setProjectInspectionVersion(projectInspection.current.version);
  }
  function publishWorkspaceState(value: WorkspaceState) {
    invalidateCreation();
    currentSnapshot.current = value.kind === "ready" ? value.snapshot : undefined;
    markProjectInspection(value.kind === "ready");
    setWorkspaceState(value);
  }
  const [projectId, writeProjectId] = useState<string | null>(null);
  const [sessionId, writeSessionId] = useState<string | null>(null);
  function setProjectId(value: string | null) { invalidateCreation(); writeProjectId(value); }
  function setSessionId(value: string | null) { invalidateCreation(); writeSessionId(value); }
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
    invalidateCreation();
    focusRestoration.cancel();
    if (next === "workspace" || next === "reminders") {
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
  const [search, writeSearch] = useState("");
  function setSearch(value: string) { invalidateCreation(); writeSearch(value); }
  const [projectFilter, setProjectFilter] = useState("");
  const { projectSort, setProjectSort, theme, setTheme, railState, setDesktopCollapsed, toggleRail, closeNarrowRail, notices: preferenceNotices } = useWindowPreferences();
  const [notesVisible, setNotesVisible] = useState(true);
  const [notesHeight, setNotesHeight] = useState(() => restoreNotesHeight(() => localStorage.getItem(notesHeightKey)));
  const [historyNotes, setHistoryNotes] = useState<{ sessionId: string | null; markdown: string }>({ sessionId: null, markdown: "" });
  const updateHistoryNotes = useCallback((markdown: string) => setHistoryNotes({ sessionId, markdown }), [sessionId]);
  const [dialog, writeDialog] = useState<"project" | "help" | "about" | null>(null);
  function setDialog(value: typeof dialog) { invalidateCreation(); writeDialog(value); }
  const helpOrigin = useRef<{ element: HTMLElement | null; view: View; sessionId: string | null; scope: string | null } | null>(null);
  const aboutOrigin = useRef<{ element: HTMLElement | null; view: View } | null>(null);
  function openAbout(element: HTMLElement | null) {
    if (dialog || document.querySelector('dialog[open]:not(.settings-dialog), [role="dialog"][aria-modal="true"]:not(.settings-dialog)')) return;
    focusRestoration.cancel();
    aboutOrigin.current = { element, view: currentView.current };
    setDialog("about");
  }
  function closeAbout() {
    const origin = aboutOrigin.current;
    setDialog(null);
    focusRestoration.schedule(origin?.element ?? null, () => currentView.current === origin?.view,
      () => !!document.querySelector('dialog[open]:not(.settings-dialog), [role="dialog"][aria-modal="true"]:not(.settings-dialog)'));
  }
  const [paletteOpen, writePaletteOpen] = useState(false);
  function setPaletteOpen(value: boolean) { invalidateCreation(); writePaletteOpen(value); }
  const paletteCapture = useRef<PaletteContext | null>(null);
  const paletteOrigin = useRef<HTMLElement | null>(null);
  const palettePending = useRef<{ action: PaletteAction; captured: PaletteContext } | null>(null);
  const [configurationState, setConfigurationState] = useState<{ snapshot?: ConfigurationSnapshot; error?: string }>({});
  const initialSelectionMade = useRef(false);
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
  const [display] = useState(() => createSessionDisplayStore(sessionDisplay.observe));
  const [scrollMemory] = useState(createTimelineScrollMemory);
  const [runtimeReader] = useState(() => createRuntimeStateReader(sessionRuntimeState.current));
  const [notesReader] = useState(() => createNotesReader(sessionNotes.current, sessionNotes.clear));
  const [projectOpening] = useState(() => createProjectOpening(workspace.openProject));
  const [projectRename] = useState(() => createProjectRename(workspace.readProjectName, workspace.renameProject));
  const [projectRenameTarget, setProjectRenameTarget] = useState<ProjectNameTarget | null>(null);
  const [projectRenameName, setProjectRenameName] = useState("");
  const [projectRenameBusy, setProjectRenameBusy] = useState(false);
  const [projectRenameConflict, setProjectRenameConflict] = useState(false);
  const [projectRenameNotice, setProjectRenameNotice] = useState("");
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
  const [renamingMessage, setRenamingMessage] = useState("");
  const renamingPending = useRef(false);
  const uncertainRename = useRef<{ id: string; title: string; target: RenameTarget } | null>(null);
  const [renameLocked, setRenameLocked] = useState(false);
  const selectedSessionId = useRef<string | null>(null);
  const [menuTarget, setMenuTarget] = useState<SessionMenuTarget | null>(null);
  const menuSnapshot = useRef<WorkspaceSnapshot | undefined>(undefined);
  const menuSelection = useRef<string | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const menuOrigin = useRef<HTMLButtonElement>(null);
  const focusAction = useRef<"rename" | "delete" | null>(null);
  const [creatingVisible, writeCreatingVisible] = useState(false);
  const [creatingTitle, writeCreatingTitle] = useState("");
  function setCreatingVisible(value: boolean | ((previous: boolean) => boolean)) { invalidateCreation(); writeCreatingVisible(value); }
  function setCreatingTitle(value: string) { invalidateCreation(); writeCreatingTitle(value); }
  const [creatingBusy, setCreatingBusy] = useState(false);
  const [creatingMessage, setCreatingMessage] = useState("");
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
    const modalTransition = (event: Event) => { if (event.target instanceof HTMLDialogElement) invalidateCreation(); };
    document.addEventListener("beforetoggle", modalTransition, true);
    return () => {
      invalidateCreation(); creationAlive.current = false; creationRefresh.current.abort();
      document.removeEventListener("beforetoggle", modalTransition, true);
    };
  }, []);
  const [inputReviewer] = useState(() => createUserInputReviewer(
    request => sessionUserInput.list(request, { timeoutMilliseconds: 8000 }),
    request => sessionUserInput.resolve({ ...request, answers: request.answers.map(answer => ({ ...answer })) }, { timeoutMilliseconds: 8000 }),
    request => sessionUserInput.cancel(request, { timeoutMilliseconds: 8000 })));
  const [permissionReviewer] = useState(() => createPermissionReviewer(sessionPermissions.list, sessionPermissions.resolve));
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
  const compactTrigger = useRef<HTMLButtonElement>(null);
  const searchInput = useRef<HTMLInputElement>(null);
  const shortcutState = useRef<WorkspaceShortcutState>({ chordPending: false, sessionInfoPrefix: null, reminderPrefix: null });
  const timelineCommand = useRef<TimelineCommand | null>(null);
  const [paneLayout, setPaneLayout] = useState(() => restorePaneLayout(() => localStorage.getItem(paneLayoutStorageKey), window.innerWidth));
  const [workspaceWidth, setWorkspaceWidth] = useState(window.innerWidth);
  const [narrow, setNarrow] = useState(() => window.matchMedia("(max-width: 875px)").matches);
  const railVisible = projectRailVisible(railState, narrow);
  const detailsPaneVisible = !(narrow && railVisible);
  const currentDetailsPaneVisible = useRef(detailsPaneVisible);
  currentDetailsPaneVisible.current = detailsPaneVisible;
  const visiblePaneLayout = constrainPaneLayout(paneLayout, workspaceWidth);
  const visibleSessionWidth = !narrow && railState.desktopCollapsed
    ? collapsedSessionWidth(paneLayout, workspaceWidth) : visiblePaneLayout.sessions;
  const [clock, setClock] = useState(Date.now);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);

  useEffect(() => {
    const timer = window.setInterval(() => setClock(Date.now()), 60_000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    persistPaneLayout(value => localStorage.setItem(paneLayoutStorageKey, value), paneLayout);
  }, [paneLayout]);

  useEffect(() => { persistNotesHeight(value => localStorage.setItem(notesHeightKey, value), notesHeight); }, [notesHeight]);

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

  const snapshot = workspaceState.kind === "ready" ? workspaceState.snapshot : undefined;
  const projectListing = snapshot ? projectRailProjection(snapshot, projectFilter, projectSort) : null;
  useEffect(() => {
    if (!snapshot || initialSelectionMade.current) return;
    initialSelectionMade.current = true;
    const firstSession = snapshot.sessions[0];
    if (!firstSession) return;
    const project = snapshot.projects.find(value => value.path === firstSession.workspacePath);
    selectedScope.current = project?.id ?? null;
    setProjectId(project?.id ?? null);
    setSessionId(firstSession.id);
    selectedSessionId.current = firstSession.id;
  }, [snapshot]);

  const sessions = snapshot ? sessionsForProject(snapshot, projectId) : [];
  const visibleSessionRows = snapshot ? sessionHierarchy(sessions, snapshot.sessions, search, projectId) : [];
  const visibleSessions = visibleSessionRows.map(row => row.session);
  const selectedSession = snapshot?.sessions.find(value => value.id === sessionId);
  const selectedProject = snapshot?.projects.find(value => value.id === projectId);
  const projectDetailsContext: ProjectDetailsContext = { snapshot, projectId, sessionId, hostEpoch: status?.hostEpoch ?? null,
    hostAvailable: !!status?.hostAvailable, refreshVersion: projectInspection.current.version,
    refreshReady: projectInspection.current.ready, active: view === "workspace" && detailsPaneVisible };
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
    if (activeMenu) menuRef.current?.querySelector<HTMLButtonElement>('button[role="menuitem"]:not(:disabled)')?.focus();
  }, [activeMenu]);
  useLayoutEffect(() => {
    if (menuTarget || !focusAction.current || narrow && railVisible) return;
    const field = sessionRail.current?.querySelector<HTMLInputElement>(focusAction.current === "rename" ? ".session-rename input" : ".session-delete input");
    if (field) { field.focus(); focusAction.current = null; }
  }, [menuTarget, renamingId, deletingId, narrow, railVisible]);
  useEffect(() => { if (menuTarget && !activeMenu) setMenuTarget(null); }, [menuTarget, activeMenu]);
  useEffect(() => {
    if (!activeMenu) return;
    const dismiss = (event: globalThis.PointerEvent) => {
      if (!menuRef.current?.contains(event.target as Node) && !menuOrigin.current?.contains(event.target as Node)) setMenuTarget(null);
    };
    document.addEventListener("pointerdown", dismiss);
    return () => document.removeEventListener("pointerdown", dismiss);
  }, [activeMenu]);
  const notice = snapshot ? workspaceNotice(snapshot) : null;
  const connected = !!status?.hostAvailable;
  const owned = !!(status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch);
  const currentHostEpoch = useRef(status?.hostEpoch);
  currentHostEpoch.current = status?.hostEpoch;
  const currentHostAvailable = useRef(!!status?.hostAvailable);
  currentHostAvailable.current = !!status?.hostAvailable;

  function paletteContext(): PaletteContext {
    const selection = selectedSessionInfoSelection(snapshot, selectedSession, projectId,
      selectedSessionId.current, selectedScope.current);
    return { workspace: currentView.current === "workspace", selection,
      epoch: owned && mutation?.capability.canMutate() ? status?.hostEpoch ?? null : null,
      infoReady: !!sessionInfoTrigger.current?.isConnected && !sessionInfoTrigger.current.disabled &&
        sessionInfoTrigger.current.getAttribute("aria-expanded") === "false",
      promptReady: !!document.querySelector("#session-prompt, #catalog-prompt"),
      searchReady: !!searchInput.current?.isConnected };
  }

  function openPalette() {
    if (paletteOpen || dialog || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    focusRestoration.cancel();
    paletteOrigin.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    paletteCapture.current = paletteContext();
    setPaletteOpen(true);
  }

  function dismissPalette() {
    const origin = paletteOrigin.current;
    const originView = currentView.current;
    setPaletteOpen(false);
    focusRestoration.schedule(origin, () => currentView.current === originView,
      () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'));
  }

  function choosePalette(action: PaletteAction) {
    if (!paletteCapture.current || !paletteAvailable(action, paletteCapture.current, paletteContext())) return;
    focusRestoration.cancel();
    palettePending.current = { action, captured: paletteCapture.current };
    setPaletteOpen(false);
  }

  useLayoutEffect(() => {
    if (paletteOpen || !palettePending.current) return;
    const { action, captured } = palettePending.current;
    palettePending.current = null;
    if (dialog || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]') ||
      !paletteAvailable(action, captured, paletteContext())) return;
    if (action === "about") { navigate("about"); settingsOrigin.current = paletteOrigin.current; aboutOrigin.current = { element: null, view: currentView.current }; setDialog("about"); return; }
    if (action === "sessionInfo") sessionInfoTrigger.current?.click();
    else if (action === "reminders") navigate("reminders");
    else if (action === "focusPrompt") document.querySelector<HTMLTextAreaElement>("#session-prompt, #catalog-prompt")?.focus();
    else if (action === "focusSearch") searchInput.current?.focus();
    else { navigate(action === "settings" ? "appearance" : action); settingsOrigin.current = paletteOrigin.current; }
  });

  useEffect(() => {
    function keyDown(event: globalThis.KeyboardEvent) {
      if (paletteShortcut(event, paletteOpen || !!dialog || !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'))) {
        event.preventDefault(); openPalette(); return;
      }
      const target = event.target as HTMLElement | null;
      if (target && workspaceShell.current?.contains(target) && !target.closest("input, textarea, select, [contenteditable='true']") &&
        !event.isComposing && event.keyCode !== 229 && !event.defaultPrevented &&
        ["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " "].includes(event.key))
        timelineCommand.current?.cancelLatest();
      const infoSelection = selectedSessionInfoSelection(snapshot, selectedSession, projectId,
        selectedSessionId.current, selectedScope.current);
      const focusedProject = !!(view === "workspace" && owned && selectedProject && !selectedProject.archived
        && target?.closest('button[aria-pressed="true"]') === projectRail.current?.querySelector('button[aria-pressed="true"]'));
      dispatchWorkspaceShortcut(event, shortcutState.current, {
        workspaceActive: view === "workspace", workspaceShell: workspaceShell.current,
        modalOpen: !!dialog || !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'),
        selectedProjectFocused: focusedProject, infoTrigger: sessionInfoTrigger.current,
        reminderTrigger: remindersTrigger.current, compactTrigger: compactTrigger.current,
        infoSelection,
        selection: owned && currentProjectWritable() && status?.hostEpoch && infoSelection ? { epoch: status.hostEpoch, ...infoSelection } : null,
        messageAvailable: !!infoSelection && timelineCommand.current?.sessionId === infoSelection.sessionId
          && timelineCommand.current.projectId === infoSelection.projectId
          && timelineCommand.current.epoch === (status?.hostEpoch ?? null) && timelineCommand.current.ready(),
        latestAvailable: !!infoSelection && timelineCommand.current?.sessionId === infoSelection.sessionId
          && timelineCommand.current.projectId === infoSelection.projectId
          && timelineCommand.current.epoch === (status?.hostEpoch ?? null) && timelineCommand.current.latestReady(),
        run: runShortcut,
      });
    }
    window.addEventListener("keydown", keyDown);
    return () => window.removeEventListener("keydown", keyDown);
  });

  function runShortcut(action: ShortcutAction) {
    if (settingsVisible.current) { if (action === "escape" && !dialog) closeSettings(); return; }
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
    else if (action === "sessionInfo") sessionInfoTrigger.current?.click();
    else if (action === "reminders") remindersTrigger.current?.click();
    else if (action === "compact") {
      const selection = selectedSessionInfoSelection(snapshot, selectedSession, projectId,
        selectedSessionId.current, selectedScope.current);
      const trigger = compactTrigger.current;
      if (view === "workspace" && owned && currentProjectWritable() && status?.hostEpoch && mutation?.capability.canMutate()
        && selection && trigger?.isConnected && !trigger.disabled
        && trigger.dataset.epoch === status.hostEpoch && trigger.dataset.sessionId === selection.sessionId
        && trigger.dataset.projectId === (selection.projectId ?? "")) trigger.click();
    }
    else if (action === "expandPrompt") {
      if (!dialog) document.querySelector<HTMLButtonElement>("#expand-session-prompt")?.click();
    }
    else if (action === "openProject") setDialog("project");
    else if (action === "renameProject") void beginProjectRename();
    else if (action === "help") openHelp();
    else if (action === "escape") {
      if (dialog === "help") closeHelp();
      else if (railVisible && projectRail.current?.contains(document.activeElement)) toggleProjects();
      else { setDialog(null); (document.activeElement as HTMLElement | null)?.blur(); }
    }
    else if (action === "models") navigate("models");
    else if (action === "prompts") navigate("prompts");
    else if (action === "providers") navigate("providers");
    else if (action === "settings" || action === "plugins") navigate(action === "plugins" ? "mcp" : "appearance");
    else if (action === "toggleNotes") setNotesVisible(value => !value);
    else if (action === "focusPrompt") document.querySelector<HTMLTextAreaElement>("#session-prompt, #catalog-prompt")?.focus();
    else if (action === "focusSearch") searchInput.current?.focus();
    else if (action === "focusProjects") {
      if (!railVisible) toggleProjects();
      else focusVisibleProject(projectRail.current, projectFilterInput.current);
    }
    else if (action === "focusSessions" && !(narrow && railVisible))
      sessionRail.current?.querySelector<HTMLButtonElement>('button[aria-pressed="true"]')?.focus();
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
    } else if (action === "context") {
      if (workspaceShell.current?.querySelector(".owned-session #refresh-session-context")) activateContextShortcut(workspaceShell.current);
      else navigate("appearance");
    }
  }

  function openSelectedReminders(session: string, epoch: string, scope: string | null) {
    if (currentView.current !== "workspace" || dialog || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')
      || !status?.hostAvailable || currentHostEpoch.current !== epoch
      || !mutation?.capability.canMutate() || selectedSessionId.current !== session || selectedScope.current !== scope
      || selectedSession?.id !== session || projectId !== scope || !currentProjectWritable() ||
      !selectedSessionInfoAvailable(snapshot, selectedSession, scope)) return;
    navigate("reminders");
  }

  function toggleProjects() {
    if (railVisible) {
      focusProjectPending.current = false;
      restoreProjectRailFocus(projectRail.current, document.activeElement, projectRailToggle.current);
    } else focusProjectPending.current = true;
    toggleRail(narrow);
  }

  function selectProject(nextProjectId: string | null) {
    setMenuTarget(null);
    focusAction.current = null;
    projectRenameGeneration.current++;
    setProjectRenameTarget(null);
    setProjectRenameConflict(false);
    setProjectRenameNotice(uncertainProjectRename.current ? "A previous project rename is unconfirmed. Refresh and inspect; no retry will be sent." : "");
    selectedScope.current = nextProjectId;
    setProjectId(nextProjectId);
    const nextSessions = snapshot ? sessionsForProject(snapshot, nextProjectId) : [];
    setSessionId(nextSessions[0]?.id ?? null);
    selectedSessionId.current = nextSessions[0]?.id ?? null;
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
    setRenamingMessage(renameLocked ? "Earlier rename is unconfirmed. Refresh and inspect; no retry will be sent." : "");
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
      mutation?.capability.canMutate() ?? false, renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
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
      setProjectRenameNotice("A previous project rename is unconfirmed. Refresh and inspect; no retry will be sent.");
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
          setProjectRenameNotice("The rename may have completed, but its exact project/name was not confirmed. Refresh and inspect; no retry will be sent.");
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
      setProjectRenameNotice("The host changed. Reload before reconciling this rename; no retry will be sent.");
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
      } else setProjectRenameNotice("The exact project/name is not confirmed. Inspect or reload; no retry will be sent.");
    } catch {
      if (creationAlive.current && generation === projectRenameGeneration.current)
        setProjectRenameNotice("Refresh failed. Inspect or reload; no retry will be sent.");
    } finally { projectRenameRefreshPending.current = false; }
  }

  async function createSelectedSession() {
    if (creationPending.current || !owned || !snapshot || !mutation?.capability.canMutate() || selectedProject?.archived
      || projectId !== null && !selectedProject || settingsVisible.current || dialog || paletteOpen
      || currentView.current !== "workspace" || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    creationPending.current = true;
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
      && (target.scope === "global" || currentSnapshot.current?.projects.filter(project => project.id === target.projectId).length === 1
        && currentSnapshot.current.projects.some(project => project.id === target.projectId && project.path === target.projectPath && !project.archived));
    const completedElsewhere = "Creation may have completed, but its original view or input lifetime changed or the catalog did not confirm it. Inspect sessions; no retry was sent.";
    setCreatingBusy(true);
    setCreatingMessage("");
    try {
      const result = await createSession(epoch, target, creatingTitle.trim() || null, capability);
      if (!creationAlive.current) return;
      if (result.kind === "created") {
        if (!isCurrent()) { setCreatingMessage(completedElsewhere); return; }
        // Acquire without publishing: a late create-specific read cannot overwrite a newer
        // host/catalog/selection. Other explicit refresh callers keep their existing behavior.
        let fresh: WorkspaceSnapshot | undefined;
        try { fresh = await workspace.snapshot({}, { signal: creationRefresh.current.signal, timeoutMilliseconds: 30_000 }); }
        catch { /* A failed read is not evidence that creation had no effects. */ }
        if (!creationAlive.current) return;
        const selection = fresh && createdSessionSelection(fresh, result);
        if (fresh && selection && isCurrent()) {
          publishWorkspaceState({ kind: "ready", snapshot: fresh });
          selectedScope.current = selection.projectId;
          setProjectId(selection.projectId);
          setSessionId(selection.sessionId);
          setSearch("");
          setCreatingVisible(false);
          setCreatingTitle("");
          navigate("workspace");
        } else setCreatingMessage(completedElsewhere);
      } else setCreatingMessage(sessionCreationMessage(result.code) + (isCurrent() ? "" : ` ${completedElsewhere}`));
    } finally { creationPending.current = false; if (creationAlive.current) setCreatingBusy(false); }
  }

  async function renameSelectedSession() {
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
          if (selectedSessionId.current === session.id) setRenamingMessage("Rename may have completed, but the selection changed or the refreshed catalog did not show the title. Refresh and inspect; no retry will be sent.");
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
    } else if (selectedSessionId.current === original.id) setRenamingMessage("Title not confirmed in the refreshed catalog. No retry will be sent; inspect the session or reload.");
  }

  async function deleteSelectedSession() {
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

  function changePane(pane: PaneName, delta: number) {
    setPaneLayout(current => {
      const width = workspaceShell.current?.clientWidth ?? workspaceWidth;
      if (pane === "sessions" && !narrow && railState.desktopCollapsed) {
        const next = resizeCollapsedSessionPane(current, delta, width);
        return next.sessions === current.sessions ? current : next;
      }
      const visible = constrainPaneLayout(current, width);
      const next = resizePane(visible, pane, delta, width);
      return next[pane] === visible[pane] ? current : next;
    });
  }

  function resetPane(pane: PaneName) {
    setPaneLayout(current => ({ ...current, [pane]: defaultPaneLayout[pane] }));
  }

  const settingsCard = (page: SettingsCardPage) => <ConfigurationPanel page={page} status={status}
    selectedSession={selectedSession} configurationState={configurationState}
    preferences={{ theme, setTheme, sort: projectSort, setSort: setProjectSort, desktopCollapsed: railState.desktopCollapsed, setDesktopCollapsed, notices: preferenceNotices }}
    onOpenAbout={openAbout} />;
  return <div className="app-shell">
    <header className="topbar">
      <div className="brand"><span className="brand-mark">A</span><span>CodeAlta</span><small>{demoMode ? "interactive preview" : "desktop"}</small>
        {view === "workspace" && <ProjectRailToggle expanded={railVisible} onToggle={toggleProjects} buttonRef={projectRailToggle} />}
        <button type="button" className="project-rail-toggle" aria-label="Open command palette" aria-haspopup="dialog" onClick={openPalette}>Commands <kbd>Ctrl+P</kbd></button>
      </div>
      <div className={`connection ${error ? "connection-error" : connected ? "connection-live" : "connection-readonly"}`}>
        <span className="connection-dot" />
        {error ? "Bridge unavailable" : demoMode ? "Local demo" : connected ? "Runtime connected" : "Catalog only"}
      </div>
    </header>

    {view === "reminders" ? <div className="reminders-destination"><button type="button" className="quiet-button" onClick={() => navigate("workspace")}>Back to session</button><ReminderScopeGate snapshot={snapshot} projectId={projectId}
          session={selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId ? selectedSession : undefined}
          epoch={owned && (currentProjectWritable() || !!snapshot && archivedProjectScope(snapshot, projectId)) ? status!.hostEpoch! : null}
          read={readReminders} readDetail={readReminderDetail} actions={reminderActions} mutationAllowed={!!mutation?.capability.canMutate()}
          canMutate={() => !!mutation?.capability.canMutate()} /></div>
      : <div className={`workspace-shell${railVisible ? " project-rail-open" : ""}`} ref={workspaceShell} style={{
          "--project-pane-width": `${visiblePaneLayout.projects}px`,
          "--session-pane-width": `${visibleSessionWidth}px`,
        } as CSSProperties}>
        <aside id="project-rail" className="project-rail" aria-label="Projects" ref={projectRail} hidden={!railVisible}>
          <div className="panel-title"><span>Projects</span><span><button type="button" className="rail-action" aria-label="Open project (Ctrl+O)" title="Open project (Ctrl+O)" onClick={() => setDialog("project")}>＋</button><span className="count">{snapshot?.projects.length ?? 0}</span></span></div>
          {workspaceState.kind === "loading" && <LoadingRows />}
          {workspaceState.kind === "unconfigured" && <div className="sidebar-empty">No catalog configured. See the launch instructions below.</div>}
          {workspaceState.kind === "error" && <div role="alert" className="sidebar-empty error-text">{workspaceState.message}</div>}
          {snapshot && <div className="project-controls">
            <input id="project-filter" ref={projectFilterInput} type="search" value={projectFilter} onChange={event => setProjectFilter(event.target.value)}
              placeholder="Name or path" aria-label="Filter projects by name or path" aria-controls="project-list" />
            <div className="project-sort-controls">
              <select id="project-sort" aria-label="Sort projects" value={projectSort} onChange={event => setProjectSort(event.target.value as ProjectSort)}>
                <option value="name">Name</option><option value="recent">Recent visible updates</option>
              </select>
              <button type="button" className="quiet-button" disabled={!projectFilter} onClick={() => { setProjectFilter(""); projectFilterInput.current?.focus(); }}>Clear filter</button>
            </div>
          </div>}
          {snapshot && projectListing?.evidenceNotice && <p className="project-evidence" role="status">{projectListing.evidenceNotice}</p>}
          {snapshot && projectListing?.projects.length === 0 && <p className="sidebar-empty" role="status">
            {projectFilter.trim() ? "No matching projects. Clear the filter to show them again." : "No projects in this snapshot."}
            {projectId !== null && " The selected project and session remain open."}
          </p>}
          {snapshot && <ProjectRailRows projects={projectListing?.projects ?? []} selectedId={projectId} onSelect={selectProject}
            canRename={owned} renameBusy={projectRenameBusy || !mutation?.capability.canMutate()} onRename={() => void beginProjectRename()} />}
          {projectRenameTarget && projectId === projectRenameTarget.id && currentHostEpoch.current === projectRenameTarget.epoch &&
            <div className="project-rename" role="group" aria-label={`Rename project ${projectRenameTarget.name}`}>
              <label>Project name
                <input autoFocus={railVisible} value={projectRenameName} maxLength={256} disabled={projectRenameBusy || projectRenameLocked || projectRenameConflict}
                  onChange={event => setProjectRenameName(event.target.value)}
                  onKeyDown={event => { if (event.key === "Escape" && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) {
                    event.preventDefault(); event.stopPropagation(); projectRenameGeneration.current++; setProjectRenameTarget(null);
                  } }} /></label>
              <button type="button" disabled={projectRenameBusy || projectRenameLocked || projectRenameConflict || !projectRenameName.trim()}
                onClick={() => void saveProjectRename()}>Save project name</button>
              <button type="button" disabled={projectRenameBusy} onClick={() => { projectRenameGeneration.current++; setProjectRenameTarget(null); }}>Cancel (Escape)</button>
            </div>}
          {projectRenameNotice && <p role="alert" className="notice error-text">{projectRenameNotice}</p>}
          {projectRenameLocked && <button type="button" className="quiet-button" onClick={() => void refreshProjectRename()}>Refresh project name (no retry)</button>}
          <div className="rail-footer">
            <button type="button" className="quiet-button icon-label-button" onClick={() => navigate("appearance")}><AppIcon name="settings" size={14} />Settings &amp; extensions</button>
          </div>
        </aside>

        <PaneSplitter className="project-splitter" label="Resize projects" value={visiblePaneLayout.projects} hidden={!railVisible}
          onResize={delta => changePane("projects", delta)} onReset={() => resetPane("projects")} />

        <aside className="session-rail" aria-label="Sessions" ref={sessionRail} hidden={narrow && railVisible}>
          <div className="session-rail-header">
            <div><h2>{selectedProject?.name ?? "Other sessions"}</h2></div>
            <div className="session-rail-actions"><ProjectDetailsEntry context={projectDetailsContext} getCurrent={currentProjectDetailsContext} />
              <button type="button" className="icon-button" aria-label="Create session" title="Create session in selected scope"
                disabled={!owned || !snapshot || !!selectedProject?.archived || projectId !== null && !selectedProject || creatingBusy}
                onClick={() => { setCreatingVisible(value => !value); setCreatingMessage(""); }}>＋</button></div>
          </div>
          {creatingVisible && <div className="session-create">
            <label>New {selectedProject ? `session in ${selectedProject.name}` : "global session"}
              <input value={creatingTitle} maxLength={256} disabled={creatingBusy} placeholder="Title (optional)" onChange={event => setCreatingTitle(event.target.value)} /></label>
            <button type="button" className="quiet-button" disabled={creatingBusy} onClick={() => void createSelectedSession()}>Create and open</button>
          </div>}
          {creatingBusy && <p role="status" className="notice">Creating session…</p>}
          {creatingMessage && <p role="alert" className="notice error-text">{creatingMessage}</p>}
          {creatingMessage && <button type="button" className="quiet-button" disabled={creatingBusy} onClick={() => {
            void refreshProjects(creationRefresh.current.signal).then(fresh => {
              if (creationAlive.current) setCreatingMessage(fresh ? "Session list refreshed. Inspect the entries before creating another."
                : "Could not refresh the session list. Inspect before creating another.");
            });
          }}>Refresh session list</button>}
          {renameLocked && <div role="alert" className="notice error-text">A rename is unconfirmed. No further rename will be sent until the exact title is visible after refresh.
            <button type="button" className="quiet-button" onClick={() => void refreshRenamedSession()}>Refresh title</button>
          </div>}
          {deleteLocked && <div role="alert" className="notice error-text">A deletion is unconfirmed. No further deletion will be sent until a complete refresh confirms absence.
            <button type="button" className="quiet-button" onClick={() => void refreshDeletedSession()}>Refresh session list</button>
          </div>}
          {!owned && <p className="muted-text">Session creation requires an owned host.</p>}
          <label className="search"><AppIcon name="search" size={14} /><input ref={searchInput} value={search} onChange={event => setSearch(event.target.value)} placeholder="Search sessions" aria-label="Search sessions" /></label>
          {notice && <p role="status" className="notice">{notice}</p>}
          <div className="session-list">
            {visibleSessionRows.map(({ session, depth, diagnostic, tooltip }, index) => {
              const menu = activeMenu?.id === session.id ? activeMenu : null;
              const access = sessionActionAccess(session,
                menu ?? { id: session.id, projectId, hostEpoch: status?.hostEpoch ?? null },
                menu ? session.id : selectedSession === session && selectedSessionId.current === session.id ? sessionId : null,
                selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
                mutation?.capability.canMutate() ?? false, renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
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
                <span className="session-title">{depth > 0 && <span aria-hidden="true">↳ </span>}{diagnostic && <span aria-hidden="true">⚠ </span>}{session.title}</span>
                <SessionDraftBadge active={draftIndicators.visible(session.id, sessionId)} />
                <span className="session-meta"><span>{session.providerKey ?? "No provider"}</span><SessionTime value={session.updatedAt} now={clock} /></span>
              </button>
              <span id={`session-tooltip-${index}`} role="tooltip" className="session-tooltip"
                tabIndex={tooltip.length > 256 ? 0 : undefined}>{tooltip}</span>
              <button type="button" className="icon-button session-actions-trigger" aria-label={`Actions for ${session.title} (ID: ${session.id})`}
                aria-haspopup="menu" aria-expanded={!!menu} aria-controls={menu ? `session-actions-${index}` : undefined}
                onClick={event => openSessionMenu(session.id, event.currentTarget)}><AppIcon name="ellipsis" size={16} /></button>
              {menu && access.open && <SessionActionMenu id={`session-actions-${index}`} label={session.title}
                rename={access.rename} deleteAllowed={access.delete} menuRef={menuRef}
                onAction={action => runSessionMenuAction(action, session, menu)} onDismiss={dismissSessionMenu} />}
              {renamingId === session.id && <div className="session-rename"><label>New title for {session.title}
                <input value={renamingTitle} maxLength={256} disabled={!owned || renamingBusy || renameLocked} onChange={event => setRenamingTitle(event.target.value)}
                  onKeyDown={event => { if (event.key === "Enter") void renameSelectedSession(); if (event.key === "Escape") setRenamingId(null); }} /></label>
                <button type="button" disabled={!owned || renamingBusy || renameLocked || !renamingTitle.trim()} onClick={() => void renameSelectedSession()}>Save title</button>
                <button type="button" disabled={renamingBusy} onClick={() => setRenamingId(null)}>Cancel</button>
                {renamingMessage && <p role="alert" className="notice error-text">{renamingMessage}</p>}
              </div>}
              {deletingId === session.id && <div className="session-delete" role="group" aria-label={`Confirm deletion of ${session.title}`}>
                <p>Delete only this session's journal and history (ID: <code>{session.id}</code>). Project files are not deleted. This cannot be undone.</p>
                <label>Type the exact session title: <strong>{session.title}</strong>
                  <input value={deletingConfirmation} disabled={!owned || deletingBusy || deleteLocked} autoComplete="off"
                    onChange={event => setDeletingConfirmation(event.target.value)} /></label>
                <button type="button" disabled={!owned || deletingBusy || deleteLocked || deletingConfirmation !== session.title}
                  onClick={() => void deleteSelectedSession()}>Delete this session</button>
                <button type="button" disabled={deletingBusy} onClick={() => setDeletingId(null)}>Cancel</button>
                {deletingMessage && <p role="alert" className="notice error-text">{deletingMessage}</p>}
              </div>}
            </div>;
            })}
            {snapshot && visibleSessions.length === 0 && <div className="sidebar-empty">{search ? "No matching sessions." : "No sessions in this project."}</div>}
          </div>
          {notesVisible && <NotesPanel epoch={owned ? status?.hostEpoch : undefined} sessionId={sessionId}
            reader={owned ? notesReader : undefined} capability={owned ? mutation?.capability : undefined}
            fallbackMarkdown={historyNotes.sessionId === sessionId ? historyNotes.markdown : ""} onClose={() => setNotesVisible(false)}
            preferredHeight={notesHeight} onResize={delta => setNotesHeight(height => resizeNotesHeight(height, -delta))}
            onReset={() => setNotesHeight(defaultNotesHeight)} onCleared={target => { if (target === sessionId) setHistoryNotes({ sessionId: target, markdown: "" }); }} />}
          {!notesVisible && <button type="button" className="quiet-button icon-label-button show-notes" onClick={() => setNotesVisible(true)}><AppIcon name="notes" size={14} />Show Alta notes</button>}
        </aside>

        <PaneSplitter className="session-splitter" label="Resize sessions" value={visibleSessionWidth}
          onResize={delta => changePane("sessions", delta)} onReset={() => resetPane("sessions")} />

        <main className="content">
          {error && <div className="banner banner-error" role="alert">{error}</div>}
          {!selectedSession
            ? <EmptyWorkspace workspaceState={workspaceState} />
            : <SessionWorkspace key={JSON.stringify([projectId, selectedSession.id])} session={selectedSession} snapshot={snapshot!} selectedProjectId={projectId} infoTrigger={sessionInfoTrigger} remindersTrigger={remindersTrigger}
                preferredComposerHeight={composerHeights.get(composerSizeKey(status?.hostEpoch ?? null, projectId, selectedSession.id))}
                onComposerHeight={height => { if (selectedScope.current !== projectId || selectedSessionId.current !== selectedSession.id) return;
                  setComposerHeights(sizes => rememberComposerHeight(sizes, composerSizeKey(status?.hostEpoch ?? null, projectId, selectedSession.id), height)); }}
                onOpenReminders={openSelectedReminders} onOpenHelp={openHelp} onOpenPalette={openPalette} readReminders={readReminders} reminderActions={reminderActions} compactTrigger={compactTrigger} status={status} mutation={mutation}
                submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} draftIndicators={draftIndicators}
                 askActions={askActions} display={display} scrollMemory={scrollMemory} runtimeReader={runtimeReader}
                 permissionReviewer={permissionReviewer} inputReviewer={inputReviewer} configuration={configurationState.snapshot}
                  onNotesChange={updateHistoryNotes} selections={nextSendSelections}
                timelineCommand={timelineCommand} />}
        </main>
      </div>}
    {settingsOpen && <SettingsOverlay section={settingsSection} onSection={navigate} onClose={closeSettings}>
      {settingsSection === "appearance" || settingsSection === "skills" || settingsSection === "plugins" || settingsSection === "about"
        ? settingsCard(settingsSection)
      : settingsSection === "logs" ? <>{settingsCard("logs")}
        <ApplicationLogsPanel clearActions={logClearActions} read={demoMode
          ? async () => ({ status: "unavailable", rows: [], captureOmitted: "0", readOmitted: 0, captureId: null, boundary: "0", grant: "" }) : applicationLogs.read} /></>
      : settingsSection === "providers" ? <>{settingsCard("providers")}
        <ProvidersPanel epoch={owned ? status!.hostEpoch : null}
        read={modelCatalog.providers} probe={modelCatalog.probe} catalogProviders={configurationState.snapshot?.providers} holds={providerProbeHolds}
        onOpenModels={() => navigate("models")} /></>
      : settingsSection === "mcp" ? <McpServersPanel target={owned && selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId
        ? { sessionId: selectedSession.id, epoch: status!.hostEpoch!, projectId: selectedSession.projectId ?? null } : null}
        read={mcpInventory.list} />
      : settingsSection === "prompts" ? <>{settingsCard("prompts")}
        <PromptCatalogPanel epoch={owned ? status!.hostEpoch : null} readPrompts={promptCatalog.list}
        readChoices={sessionOperations.choices} target={owned && currentProjectWritable() && selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId
          ? { sessionId: selectedSession.id, epoch: status!.hostEpoch! } : null}
        selections={nextSendSelections} pendingSend={!!(selectedSession && submissions.pending(selectedSession.id))}
        pendingSelection={selectedSession ? submissions.pending(selectedSession.id)?.request.selection ?? null : null}
        onApply={async (target, signal) => {
          const result = await applyPromptNextSend(target, () => ({ epoch: currentHostEpoch.current ?? null,
            active: settingsVisible.current && currentSettingsSection.current === "prompts" && !signal.aborted,
            sessionId: selectedScope.current === projectId ? selectedSessionId.current : null,
            canMutate: !!mutation?.capability.canMutate() && currentProjectWritable(), pending: !!submissions.pending(target.sessionId) }),
          async (epoch, sessionId) => {
            const value = await sessionOperations.choices({ expectedEpoch: epoch, sessionId }, { signal, timeoutMilliseconds: 15000 });
            mutation?.capability.observe(value);
            return value;
          }, nextSendSelections);
          if (result === "applied" && !signal.aborted) closeSettings();
          return result;
        }} /></>
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
          || !fresh.projects.some(project => project.id === id && project.path === path)) return false;
        selectedScope.current = id;
        setProjectId(id);
        const nextSession = sessionsForProject(fresh, id)[0]?.id ?? null;
        selectedSessionId.current = nextSession;
        setSessionId(nextSession);
        navigate("workspace");
        return true;
      }} onClose={() => setDialog(null)} />}
    {dialog === "help" && <ShortcutHelp onClose={closeHelp} />}
    {dialog === "about" && <AboutDialog status={status} bootError={!!error} demo={demoMode} onClose={closeAbout} />}
    {paletteOpen && paletteCapture.current && <CommandPalette context={paletteContext()} captured={paletteCapture.current}
      onChoose={choosePalette} onClose={dismissPalette} />}
  </div>;
}

// Native modal matches the other shell dialogs: showModal supplies inert background,
// browser-managed focus trapping and nested native About dialog top-layer ordering.
function SettingsOverlay({ section, onSection, onClose, children }: {
  section: SettingsSection; onSection: (section: SettingsSection) => void;
  onClose: () => void; children: ReactNode;
}) {
  const modal = useRef<HTMLDialogElement>(null);
  const composingEscape = useRef(false);
  useLayoutEffect(() => {
    const element = modal.current;
    element?.showModal();
    return () => { if (element?.open) element.close(); };
  }, []);
  const destinations: readonly [string, readonly [SettingsSection, string][]][] = [
    ["Personalization", [["appearance", "Appearance"]]],
    ["Agent & models", [["providers", "Providers"], ["models", "Models"], ["prompts", "Agent prompts"], ["skills", "Skills"]]],
    ["Extensions", [["plugins", "Plugins & MCP"], ["mcp", "MCP Servers"]]],
    ["Diagnostics", [["logs", "Application Logs"], ["about", "About"]]],
  ];
  return <dialog ref={modal} className="settings-dialog" aria-modal="true" aria-labelledby="settings-title"
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) composingEscape.current = true;
      else onClose();
    }} onKeyUp={() => { composingEscape.current = false; }} onCompositionEnd={() => { composingEscape.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composingEscape.current) onClose(); }}>
    <header className="settings-dialog-header"><h2 id="settings-title">Settings</h2>
      <button type="button" className="icon-button" aria-label="Close settings" onClick={onClose}><AppIcon name="close" size={16} /></button></header>
    <div className="settings-dialog-body">
      <nav className="settings-dialog-navigation" aria-label="Settings pages">
        {destinations.map(([group, pages]) => <div className="settings-dialog-group" key={group}>
          <h3>{group}</h3>
          {pages.map(([value, label]) => <button key={value} type="button" aria-current={section === value ? "page" : undefined}
            onClick={() => onSection(value)}>{label}</button>)}
        </div>)}
      </nav>
      <div className="settings-dialog-content" key={section}>{children}</div>
    </div>
  </dialog>;
}

function SessionWorkspace({ session, snapshot, selectedProjectId, preferredComposerHeight, onComposerHeight, infoTrigger, remindersTrigger, compactTrigger, onOpenReminders, onOpenHelp, onOpenPalette, readReminders, reminderActions, status, mutation, submissions, steering, compaction, abortRuns, queue, draftIndicators, askActions, display, scrollMemory, runtimeReader, permissionReviewer, inputReviewer, configuration: configurationSnapshot, onNotesChange, selections, timelineCommand }: {
  session: WorkspaceSession;
  snapshot: WorkspaceSnapshot;
  selectedProjectId: string | null;
  preferredComposerHeight: number | undefined;
  onComposerHeight: (height: number | undefined) => void;
  infoTrigger: RefObject<HTMLButtonElement | null>;
  remindersTrigger: RefObject<HTMLButtonElement | null>;
  onOpenReminders: (sessionId: string, epoch: string, projectId: string | null) => void;
  onOpenHelp: () => void;
  onOpenPalette: () => void;
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
  onNotesChange: (markdown: string) => void;
  selections: ReturnType<typeof createNextSendSelectionStore>;
  timelineCommand: RefObject<TimelineCommand | null>;
}) {
  const [infoOpen, setInfoOpen] = useState(false);
  const infoActive = useRef(false);
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
  const workspaceElement = useRef<HTMLDivElement>(null);
  const resizeBar = useRef<HTMLDivElement>(null);
  const composerRegion = useRef<HTMLDivElement>(null);
  const [layout, setLayout] = useState({ available: 0, rendered: 0 });
  useLayoutEffect(() => {
    const workspace = workspaceElement.current;
    const scroller = timeline.elementRef.current;
    const bar = resizeBar.current;
    const region = composerRegion.current;
    if (!workspace || !scroller || !bar || !region) return;
    const measure = () => {
      const available = Math.max(0, Math.floor(workspace.getBoundingClientRect().bottom - scroller.getBoundingClientRect().top
        - (bar.getBoundingClientRect().top - scroller.getBoundingClientRect().bottom) - bar.getBoundingClientRect().height
        - parseFloat(getComputedStyle(workspace).paddingBottom)));
      const rendered = Math.round(region.getBoundingClientRect().height);
      setLayout(old => old.available === available && old.rendered === rendered ? old : { available, rendered });
    };
    const observer = new ResizeObserver(measure);
    for (const element of [workspace, scroller, bar, region]) observer.observe(element);
    measure();
    return () => observer.disconnect();
  }, [timeline.elementRef]);
  const bounds = composerBounds(layout.available);
  const visibleComposerHeight = preferredComposerHeight === undefined ? undefined : resizeComposerHeight(preferredComposerHeight, 0, bounds);
  const pendingComposerHeight = useRef<number | null>(null);
  useLayoutEffect(() => { pendingComposerHeight.current = null; }, [preferredComposerHeight]);
  const resizeComposer = (delta: number) => {
    const base = pendingComposerHeight.current ?? (preferredComposerHeight === undefined
      ? composerRegion.current?.getBoundingClientRect().height ?? layout.rendered : visibleComposerHeight!);
    const next = resizeComposerHeight(base, delta, bounds);
    pendingComposerHeight.current = next;
    onComposerHeight(next);
  };
  const [messageNotice, setMessageNotice] = useState("");
  const [newerOmitted, setNewerOmitted] = useState(false);
  const newest = useExplicitNewestHistory(session.id, selectedProjectId, status?.hostEpoch ?? null, timeline, setMessageNotice);
  // Stable across History's auto-pages; do not cancel an admitted request on a parent render.
  const resetMessageNotice = useCallback((generation: number, explicitNewest: boolean) => {
    timeline.resetMessageNavigation();
    if (newest.onTarget(generation)) return;
    if (explicitNewest) timeline.pauseIfUnfollowed();
    setMessageNotice("");
  }, [timeline.resetMessageNavigation, timeline.pauseIfUnfollowed, newest.onTarget]);
  useLayoutEffect(() => {
    if (demoMode) return;
    const command: TimelineCommand = { sessionId: session.id, projectId: selectedProjectId, epoch: status?.hostEpoch ?? null,
      ready: timeline.messageReady,
      navigate: action => {
        newest.cancel();
        const result = timeline.navigateMessage(action);
        setMessageNotice(result.status === "unavailable" ? "Retained history is not ready for message navigation."
          : result.status === "boundary" ? action === "messageNext"
            ? "Last retained message in this window. This may not be the newest persisted history; use Refresh newest history."
            : "First retained message in this window. Older journal history may be available via Load older history."
          : `${action === "messageFirst" ? "First retained message (not necessarily the first journal message): " : "Retained message: "}${result.label ?? "User or assistant message"}`);
      },
      latestReady: newest.available, latest: newest.latest, cancelLatest: newest.cancel };
    timelineCommand.current = command;
    return () => { if (timelineCommand.current === command) timelineCommand.current = null; };
  });
  const observedDisplay = useSyncExternalStore(display.subscribe, display.getSnapshot);
  const live = status?.hostEpoch && observedDisplay.hostEpoch === status.hostEpoch && observedDisplay.sessionId === session.id
    ? observedDisplay : null;
  const archivedScope = archivedProjectScope(snapshot, selectedProjectId);
  const ownedHost = !!(status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch);
  const ownedSession = ownedHost && !archivedScope;
  const infoControl = <button ref={infoTrigger} type="button" className="composer-icon-button session-info-trigger"
    aria-label="Session info" title="Session info (Ctrl+G, Ctrl+T)" aria-haspopup="dialog" aria-expanded={infoOpen}
    onClick={openInfo}><AppIcon name="info" size={16} /></button>;
  return <div className="session-workspace" ref={workspaceElement}>
    <header className="session-header">
      <div><span className="eyebrow">Session</span><h1 title={session.title}>{session.title}</h1></div>
      <div className="session-chips"><span>{session.providerKey ?? "Provider not recorded"}</span>
        <span>{demoMode ? "Demo" : archivedScope ? "Archived (read-only)" : status?.hostAvailable ? "Host available" : "Catalog only"}</span>
        {demoMode && infoControl}</div>
    </header>
    {infoOpen && <SessionInfoDialog info={sessionInfoView(snapshot, session, selectedProjectId)} demo={demoMode} onClose={closeInfo} />}
    {demoMode
      ? <DemoConversation session={session} />
      : <>
        <div className="timeline-scroll" ref={timeline.elementRef}
          onScroll={event => { newest.onScroll(); if (!newest.pending()) timeline.scroll(event.currentTarget); }}
          onWheel={event => { newest.cancel(); timeline.wheel(event); }} onKeyDown={timeline.keyDown}
          onPointerDown={event => { newest.cancel(); timeline.pointerDown(event); }}
          onPointerMove={timeline.pointerMove} onPointerUp={timeline.pointerEnd} onPointerCancel={timeline.pointerEnd}>
        <History sessionId={session.id} onNotesChange={onNotesChange} onSettled={() => {
          timeline.settled(); if (!newest.pending()) timeline.pauseIfUnfollowed();
        }}
          onBeforeOlder={timeline.beforeOlderPage} onAfterOlder={timeline.afterOlderPage} onNewerOmitted={setNewerOmitted}
          onNavigationReset={resetMessageNotice} newestRequest={newest.requestRef} onNewestResult={newest.onResult}
          read={workspace.historyTail}
          live={ownedSession ? live?.snapshot?.session ?? null : null} />
        {ownedSession && status?.hostEpoch
        ? <>
          <LiveSessionPanel store={display} hostEpoch={status.hostEpoch} sessionId={session.id} capability={mutation!.capability} />
          {status.ownedAsksEnabled && <AskPanel epoch={status.hostEpoch} sessionId={session.id} actions={askActions} capability={mutation!.capability} />}
          {status.ownedUserInputEnabled && <UserInputPanel epoch={status.hostEpoch} sessionId={session.id} reviewer={inputReviewer} capability={mutation!.capability} />}
        </>
        : null}
        </div>
        {!timeline.following && <button type="button" className="timeline-bottom-button" onClick={() => { newest.cancel(); timeline.jump(); }}><AppIcon name="arrowDown" size={14} />{newerOmitted ? "Bottom of retained window (not newest)" : "Jump to latest visible"}</button>}
        {messageNotice && <p role="status" className="detail timeline-navigation-notice">{messageNotice}</p>}
        <div className="composer-resize-bar" ref={resizeBar}>
          <ComposerSplitter value={layout.rendered} min={bounds.min} max={bounds.max} automatic={preferredComposerHeight === undefined}
            onResize={resizeComposer} onReset={() => onComposerHeight(undefined)} />
          {preferredComposerHeight !== undefined && <button type="button" className="quiet-button" onClick={() => {
            resizeBar.current?.querySelector<HTMLElement>(".composer-splitter")?.focus(); onComposerHeight(undefined);
          }}
            aria-label="Reset composer size to automatic">Auto size</button>}
        </div>
        <div ref={composerRegion} className={`composer-region${preferredComposerHeight === undefined ? "" : " resized"}`}
          style={visibleComposerHeight === undefined ? undefined : { height: visibleComposerHeight }}>
        <SessionComposerGate snapshot={snapshot} projectId={selectedProjectId} session={session}
          epoch={ownedHost ? status!.hostEpoch! : null}
          owned={status?.hostEpoch && mutation ? <OwnedSessionPanel sessionId={session.id} epoch={status.hostEpoch} submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} capability={mutation.capability} runtimeReader={runtimeReader} permissionReviewer={status.commandReviewEnabled ? permissionReviewer : null} configuration={configurationSnapshot} draftIndicators={draftIndicators} selections={selections}
              usageTarget={ownedSession && verifiedReminderCountTarget(snapshot, session, selectedProjectId) ? {
                epoch: status.hostEpoch, sessionId: session.id, scope: selectedProjectId === null ? "global" : "project",
                projectId: selectedProjectId, expectedProjectPath: selectedProjectId === null ? null : session.workspacePath } : null}
              remindersTrigger={remindersTrigger} compactTrigger={compactTrigger} infoControl={infoControl} projectId={selectedProjectId} onOpenReminders={() => onOpenReminders(session.id, status.hostEpoch!, selectedProjectId)} onOpenHelp={onOpenHelp} onOpenPalette={onOpenPalette}
              reminderActions={reminderActions} readReminderCount={ownedSession && verifiedReminderCountTarget(snapshot, session, selectedProjectId) ? readReminders : undefined} /> : null}
          readOnly={<ReadOnlyComposer sessionId={session.id} provider={session.providerKey} draftIndicators={draftIndicators} infoControl={infoControl} onOpenHelp={onOpenHelp} onOpenPalette={onOpenPalette}
              reason={archivedScope ? "Archived project; this session is read-only. Sending is unavailable." : undefined} />}
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

function ConfigurationPanel({ page, status, selectedSession, configurationState, preferences, onOpenAbout }: {
  page: SettingsCardPage;
  status: BootStatus | undefined;
  selectedSession: WorkspaceSession | undefined;
  configurationState: { snapshot?: ConfigurationSnapshot; error?: string };
  preferences: Parameters<typeof GeneralSettings>[0];
  onOpenAbout: (origin: HTMLElement | null) => void;
}) {
  const inventory = configurationState.snapshot;
  const mcp = inventory?.plugins.find(plugin => `${plugin.id} ${plugin.name}`.toLowerCase().includes("mcp"));
  return <div className="configuration-page settings-card-page">
    {page === "appearance" && <header className="page-heading"><span className="eyebrow">Desktop</span><h1>Appearance</h1><p>Personalize this window and project navigator.</p></header>}
    <div className="settings-grid">
      {page === "appearance" && <GeneralSettings {...preferences} />}
      {page === "logs" && <section className="settings-card"><div className="settings-icon"><AppIcon name="history" size={19} /></div><div><h2>Application Logs</h2><p>Read a bounded snapshot of this process's in-memory desktop logs. No log files are opened.</p></div></section>}
      {page === "providers" && <section className="settings-card"><div className="settings-icon"><AppIcon name="model" size={19} /></div><div><h2>Providers</h2><p>Current session provider: <strong>{selectedSession?.providerKey ?? "not recorded"}</strong>.</p>
        {configurationState.error && <p className="error-text">{configurationState.error}</p>}
        {!inventory && !configurationState.error && <p>Loading configured providers…</p>}
        {inventory && inventory.providers.length === 0 && <p>No provider inventory is exposed in this launch mode.</p>}
        {inventory?.providers.map(provider => <div className="inventory-row" key={provider.id}><span><strong>{provider.name}</strong><small>{provider.type} · {provider.defaultModel ?? "No default model"}</small></span><StatusPill label={provider.enabled ? "Enabled" : "Disabled"} /></div>)}
        {inventory?.providersTruncated && <p className="muted-text">Showing the first 32 configured providers.</p>}
      </div></section>}
      {page === "prompts" && <section className="settings-card"><div className="settings-icon"><AppIcon name="prompt" size={19} /></div><div><h2>Agent prompts</h2><p>Inspect effective host prompts for the selected session and choose its next Send prompt.</p><StatusPill label={status?.hostAvailable ? "Session state available" : "Catalog history available"} /></div></section>}
      {page === "skills" && <section className="settings-card"><div className="settings-icon"><AppIcon name="tool" size={19} /></div><div><h2>Skills</h2><p>Skills remain project/global filesystem resources and are available to shared agent sessions.</p><StatusPill label="Managed by CodeAlta runtime" /></div></section>}
      {page === "plugins" && <section className="settings-card"><div className="settings-icon">⬡</div><div><h2>Plugins &amp; MCP</h2><p>Configured plugin policy is visible in catalog mode. Active state is shown only when the owned runtime has started that plugin.</p>
        <div className="inventory-row"><span><strong>MCP servers</strong><small>Model Context Protocol runtime state</small></span><StatusPill label={mcp ? mcp.state : inventory?.pluginRuntimeAvailable ? "Not configured" : "Runtime not started"} /></div>
        {inventory?.plugins.map(plugin => <div className="inventory-row" key={plugin.id}><span><strong>{plugin.name}</strong><small>{plugin.version ?? "No version"} · {plugin.contributionCount} contributions</small></span><StatusPill label={plugin.state} /></div>)}
        {inventory && inventory.plugins.length === 0 && <StatusPill label={inventory.pluginRuntimeAvailable ? "No active plugins" : "Requires packaged host"} />}
        {inventory?.pluginsTruncated && <p className="muted-text">Showing the first 32 active plugins.</p>}
      </div></section>}
      {page === "about" && <AboutSettingsEntry onOpen={onOpenAbout} />}
    </div>
  </div>;
}

function StatusPill({ label }: { label: string }) { return <span className="status-pill">{label}</span>; }

function ShortcutHelp({ onClose }: { onClose: () => void }) {
  const shortcuts = [
    ["Ctrl+O", "Open project"], ["Ctrl+F", "Search sessions"], ["Alt+↑ / Alt+↓", "Previous / next session"],
    ["Alt+← / Alt+→", "Previous / next project"], ["Ctrl+,", "Configuration"], ["Ctrl+P", "Implemented actions palette"],
    ["Ctrl+Shift+N", "Toggle Alta notes"],
    ["Ctrl+G, Ctrl+P", "Focus prompt"], ["Ctrl+G, Ctrl+S", "Focus projects"], ["Ctrl+G, Ctrl+R", "Providers"],
    ["Ctrl+G, Ctrl+O", "Models"], ["Ctrl+G, Ctrl+H", "Agent prompts"], ["Ctrl+G, Ctrl+U", "Context state"],
    ["Ctrl+G, Ctrl+T", "Selected session info (also in composer controls; saved catalog only)"],
    ["Ctrl+G, Ctrl+D", "Reminders (selected owned workspace session only)"],
    ["F1 / ? outside text", "Keyboard shortcuts"],
    ["? in empty regular prompt", "Keyboard shortcuts"],
    ["/ in empty regular prompt", "Implemented actions palette (not slash-command execution)"],
    ["Escape", "Close / cancel"], ["Enter / Shift+Enter", "Send / new line in prompt"],
    ["F3 / F4", "Previous / next retained user or assistant message"],
    ["Ctrl+F3", "First retained message (not journal first)"],
    ["Ctrl+F4", "Refresh newest persisted history, then follow on success"],
    ["Ctrl+F11", "Attempt compaction of the observed idle attachment (selected owned session only)"],
    ["F6", "Expand prompt (owned session)"], ["Ctrl+Enter", "Steer in regular prompt; close in expanded editor; Create only inside the Reminders Create form"],
  ];
  return <div className="dialog-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <section className="app-dialog shortcut-dialog" role="dialog" aria-modal="true" aria-labelledby="shortcut-title">
      <header><div><span className="eyebrow">Keyboard first</span><h2 id="shortcut-title">Shortcuts</h2></div><button autoFocus type="button" className="icon-button" aria-label="Close" title="Close" onClick={onClose}><AppIcon name="close" size={16} /></button></header>
      <dl>{shortcuts.map(([keys, label]) => <div key={keys}><dt>{keys}</dt><dd>{label}</dd></div>)}</dl>
    </section>
  </div>;
}

function LoadingRows() { return <div className="loading-rows"><span /><span /><span /></div>; }
function EmptyWorkspace({ workspaceState }: { workspaceState: WorkspaceState }) {
  return <div className="empty-workspace"><div className="empty-logo">A</div><h1>{workspaceState.kind === "loading" ? "Loading your sessions…" : "Select a session"}</h1><p>Choose a project and session from the sidebar to inspect its transcript and runtime.</p></div>;
}
function SessionTime({ value, now }: { value: string; now: number }) {
  const { label, title, dateTime } = sessionTime(value, now);
  return <time dateTime={dateTime} title={title}>{label}</time>;
}

function ComposerSplitter({ value, min, max, automatic, onResize, onReset }: {
  value: number; min: number; max: number; automatic: boolean; onResize: (delta: number) => void; onReset: () => void;
}) {
  const handle = useRef<HTMLDivElement>(null);
  const pointer = useRef<{ id: number; y: number } | null>(null);
  useEffect(() => {
    const blur = () => {
      const id = pointer.current?.id;
      pointer.current = null;
      if (id !== undefined && handle.current?.hasPointerCapture(id)) handle.current.releasePointerCapture(id);
    };
    window.addEventListener("blur", blur);
    return () => { window.removeEventListener("blur", blur); blur(); };
  }, []);
  function end(event: PointerEvent<HTMLDivElement>) {
    if (pointer.current?.id !== event.pointerId) return;
    pointer.current = null;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  }
  return <div ref={handle} className="composer-splitter" role="separator" aria-label="Resize timeline and composer" aria-orientation="horizontal"
    aria-valuemin={Math.min(min, value)} aria-valuemax={Math.max(max, value)} aria-valuenow={value}
    aria-valuetext={automatic ? `Automatic, ${value} pixels` : `${value} pixels`}
    title="Arrow Up enlarges composer; Arrow Down shrinks; Home resets to automatic" tabIndex={0}
    onKeyDown={event => {
      if (event.key !== "ArrowUp" && event.key !== "ArrowDown" && event.key !== "Home") return;
      event.stopPropagation();
      if (event.defaultPrevented || event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229
        || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
      event.preventDefault();
      if (event.key === "Home") onReset(); else onResize(event.key === "ArrowUp" ? 16 : -16);
    }}
    onDoubleClick={onReset}
    onPointerDown={event => {
      if (!event.isPrimary || event.button !== 0 || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
      pointer.current = { id: event.pointerId, y: event.clientY };
      event.currentTarget.focus(); event.currentTarget.setPointerCapture(event.pointerId); event.preventDefault();
    }}
    onPointerMove={event => {
      if (pointer.current?.id !== event.pointerId || !event.currentTarget.hasPointerCapture(event.pointerId)) return;
      const delta = pointer.current.y - event.clientY;
      pointer.current.y = event.clientY;
      onResize(delta);
    }} onPointerUp={end} onPointerCancel={end}
    onLostPointerCapture={() => { pointer.current = null; }}><span /></div>;
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
  return <div className={`pane-splitter ${className}`} hidden={hidden} role="separator" aria-label={label} aria-orientation="vertical" aria-valuenow={value}
    tabIndex={0} onPointerDown={pointerDown} onPointerMove={pointerMove} onPointerUp={pointerEnd} onPointerCancel={pointerEnd}
    onDoubleClick={onReset} onKeyDown={keyDown}><span /></div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);
