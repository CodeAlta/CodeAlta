import { StrictMode, useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent, type RefObject } from "react";
import { createRoot } from "react-dom/client";
import {
  boot, configuration, modelCatalog, promptCatalog, mcpInventory, reminder, workspace, sessionDisplay, sessionRuntimeState, sessionPermissions, sessionOperations,
  sessionAsks, sessionNotes, sessionUserInput, type BootStatus,
  type ReminderListRequest,
  type ConfigurationSnapshot, type WorkspaceProject, type WorkspaceSession, type WorkspaceSnapshot,
} from "#neoastra";
import { loadWorkspace, sessionsForProject, workspaceNotice, type WorkspaceState } from "./workspace";
import { History } from "./HistoryPanel";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { ModelCatalogPanel } from "./ModelCatalogPanel";
import { ProvidersPanel } from "./ProvidersPanel";
import { PromptCatalogPanel } from "./PromptCatalogPanel";
import { McpServersPanel } from "./McpServersPanel";
import { ReminderPanel } from "./ReminderPanel";
import { createReminderActions } from "./reminderActions";
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
import { createTimelineScrollMemory, useTimelinePosition } from "./timelineScroll";
import { resolveShortcut, sessionInfoChordContextAllowed, sessionInfoPrefixFromKey, type ShortcutAction } from "./shortcuts";
import { activateContextShortcut } from "./contextShortcut";
import { createDraftIndicators, persistDraft, restoreDraft } from "./promptDraft";
import { SessionDraftBadge } from "./SessionDraftBadge";
import { collapsedSessionWidth, constrainPaneLayout, defaultPaneLayout, persistPaneLayout, resizeCollapsedSessionPane, resizePane, restorePaneLayout, type PaneName } from "./paneLayout";
import { visibleConfigurationSections, type ConfigurationScope } from "./configurationSections";
import { AppIcon } from "./AppIcon";
import { sessionTime } from "./sessionTime";
import { canImportCheckedFolder, createProjectOpening, projectOpeningMessage } from "./projectOpening";
import { createSessionCreation, createdSessionSelection, sessionCreationMessage, type SessionTarget } from "./sessionCreation";
import { createSessionRename, renamedSessionVisible, renameSelectionCurrent, sessionRenameMessage, type RenameTarget } from "./sessionRename";
import { createSessionDeletion, deletedSessionRecovery, deleteSelectionCurrent, sessionDeletionMessage, type DeletedTarget } from "./sessionDeletion";
import { createProjectRename, projectNameVisible, projectRenameMessage, projectRenameSelectionCurrent, type ProjectNameTarget } from "./projectRename";
import { sessionHierarchy } from "./sessionHierarchy";
import { SessionActionMenu } from "./SessionActionMenu";
import { isSessionContextKey, restoreSessionMenuFocus, sessionActionAccess, type SessionAction, type SessionMenuTarget } from "./sessionRowActions";
import { persistProjectSort, projectRailProjection, projectSortStorageKey, restoreProjectSort, type ProjectSort } from "./projectRail";
import { ProjectRailRows } from "./ProjectRailRows";
import { ProjectRailToggle } from "./ProjectRailToggle";
import { focusVisibleProject, persistProjectRailCollapsed, projectRailVisibilityKey, projectRailVisible, resetNarrowRail, restoreProjectRailCollapsed, restoreProjectRailFocus, toggleProjectRail } from "./projectRailVisibility";
import { SessionInfoDialog } from "./SessionInfoDialog";
import { restoreSessionInfoFocus, selectedSessionInfoAvailable, sessionInfoView } from "./sessionInfo";
import "./style.css";

const demoMode = import.meta.env.VITE_DEMO_MODE === "true";
type View = "workspace" | "configuration" | "providers" | "models" | "prompts" | "reminders" | "mcp";
type Theme = "dark" | "light";
const paneLayoutStorageKey = "codealta.desktop.panes.v1";

function App() {
  const [status, setStatus] = useState<BootStatus>();
  const [error, setError] = useState<string>();
  const [workspaceState, setWorkspaceState] = useState<WorkspaceState>({ kind: "loading" });
  const [projectId, setProjectId] = useState<string | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [view, setView] = useState<View>("workspace");
  const currentView = useRef<View>(view);
  currentView.current = view;
  function navigate(next: View) { currentView.current = next; setView(next); }
  const [search, setSearch] = useState("");
  const [projectFilter, setProjectFilter] = useState("");
  const [projectSort, setProjectSort] = useState<ProjectSort>(() => restoreProjectSort(() => localStorage.getItem(projectSortStorageKey)));
  const [theme, setTheme] = useState<Theme>("dark");
  const [notesVisible, setNotesVisible] = useState(true);
  const [notesHeight, setNotesHeight] = useState(() => restoreNotesHeight(() => localStorage.getItem(notesHeightKey)));
  const [historyNotes, setHistoryNotes] = useState<{ sessionId: string | null; markdown: string }>({ sessionId: null, markdown: "" });
  const updateHistoryNotes = useCallback((markdown: string) => setHistoryNotes({ sessionId, markdown }), [sessionId]);
  const [dialog, setDialog] = useState<"project" | "help" | null>(null);
  const [configurationState, setConfigurationState] = useState<{ snapshot?: ConfigurationSnapshot; error?: string }>({});
  const initialSelectionMade = useRef(false);
  const [submissions] = useState(() => createOwnedSubmissions(sessionOperations.send, sessionOperations.abort));
  const reminderCapability = useRef<ReturnType<typeof createMutationCapability> | undefined>(undefined);
  const [reminderActions] = useState(() => createReminderActions(reminder.create, reminder.delete, (target, reply) => {
    const capability = reminderCapability.current;
    if (capability?.canSubmit({ expectedEpoch: target.epoch })) capability.observe(reply);
  }));
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
  const menuRef = useRef<HTMLDivElement>(null);
  const menuOrigin = useRef<HTMLButtonElement>(null);
  const focusAction = useRef<"rename" | "delete" | null>(null);
  const [creatingVisible, setCreatingVisible] = useState(false);
  const [creatingTitle, setCreatingTitle] = useState("");
  const [creatingBusy, setCreatingBusy] = useState(false);
  const [creatingMessage, setCreatingMessage] = useState("");
  const creationPending = useRef(false);
  const creationAlive = useRef(true);
  const creationRefresh = useRef(new AbortController());
  const selectedScope = useRef<string | null>(null);
  useEffect(() => {
    creationAlive.current = true;
    creationRefresh.current = new AbortController();
    return () => { creationAlive.current = false; creationRefresh.current.abort(); };
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
  const workspaceShell = useRef<HTMLDivElement>(null);
  const projectRail = useRef<HTMLElement>(null);
  const projectRailToggle = useRef<HTMLButtonElement>(null);
  const focusProjectPending = useRef(false);
  const projectFilterInput = useRef<HTMLInputElement>(null);
  const sessionRail = useRef<HTMLElement>(null);
  const sessionInfoTrigger = useRef<HTMLButtonElement>(null);
  const searchInput = useRef<HTMLInputElement>(null);
  const chordPending = useRef(false);
  const sessionInfoPrefix = useRef(false);
  const [paneLayout, setPaneLayout] = useState(() => restorePaneLayout(() => localStorage.getItem(paneLayoutStorageKey), window.innerWidth));
  const [workspaceWidth, setWorkspaceWidth] = useState(window.innerWidth);
  const [narrow, setNarrow] = useState(() => window.matchMedia("(max-width: 875px)").matches);
  const [railState, setRailState] = useState(() => ({
    desktopCollapsed: restoreProjectRailCollapsed(() => localStorage.getItem(projectRailVisibilityKey)), narrowOpen: false,
  }));
  const railVisible = projectRailVisible(railState, narrow);
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
  useEffect(() => { persistProjectSort(value => localStorage.setItem(projectSortStorageKey, value), projectSort); }, [projectSort]);
  useEffect(() => { persistProjectRailCollapsed(value => localStorage.setItem(projectRailVisibilityKey, value), railState.desktopCollapsed); }, [railState.desktopCollapsed]);

  useEffect(() => {
    const media = window.matchMedia("(max-width: 875px)");
    const changed = () => {
      const next = media.matches;
      if (next || railState.desktopCollapsed)
        restoreProjectRailFocus(projectRail.current, document.activeElement, projectRailToggle.current);
      focusProjectPending.current = false;
      setRailState(resetNarrowRail);
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
    void loadWorkspace(workspace.snapshot, abort.signal, setWorkspaceState);
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
  selectedScope.current = projectId;
  selectedSessionId.current = sessionId;
  const activeMenu = menuTarget && view === "workspace" && menuTarget.id === sessionId
    && menuTarget.projectId === projectId && menuTarget.hostEpoch === (status?.hostEpoch ?? null)
    && selectedSession?.id === menuTarget.id && selectedSessionId.current === menuTarget.id && selectedScope.current === projectId
    && visibleSessions.some(session => session.id === menuTarget.id)
    && snapshot?.sessions.filter(session => session.id === menuTarget.id).length === 1 ? menuTarget : null;
  useLayoutEffect(() => {
    if (activeMenu && !(narrow && railVisible)) menuRef.current?.querySelector<HTMLButtonElement>('button[role="menuitem"]:not(:disabled)')?.focus();
  }, [activeMenu, narrow, railVisible]);
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

  useEffect(() => {
    function keyDown(event: globalThis.KeyboardEvent) {
      const target = event.target as HTMLElement | null;
      if (target?.closest("dialog[open]")) { chordPending.current = false; sessionInfoPrefix.current = false; return; }
      const editing = target?.matches("input, textarea, select, [contenteditable='true']") === true;
      const focusedProject = !!(view === "workspace" && owned && selectedProject && !selectedProject.archived
        && target?.closest('button[aria-pressed="true"]') === projectRail.current?.querySelector('button[aria-pressed="true"]'));
      const trigger = sessionInfoTrigger.current;
      const sessionInfoAvailable = sessionInfoPrefix.current && event.key.toLowerCase() === "t" && sessionInfoChordContextAllowed({
        workspaceActive: view === "workspace",
        modalOpen: !!dialog || !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'),
        inWorkspace: target instanceof Node && workspaceShell.current?.contains(target) === true,
        editing, promptFocused: target?.matches("#session-prompt, #catalog-prompt") === true,
        triggerReady: !!trigger?.isConnected && !trigger.disabled && trigger.getAttribute("aria-expanded") === "false"
          && workspaceShell.current?.contains(trigger) === true,
      })
        && selectedSessionId.current === selectedSession?.id && selectedScope.current === projectId
        && selectedSessionInfoAvailable(snapshot, selectedSession, projectId);
      const resolved = resolveShortcut(event, chordPending.current, editing, focusedProject, sessionInfoAvailable);
      chordPending.current = resolved.chordPending;
      sessionInfoPrefix.current = sessionInfoPrefixFromKey(event, resolved);
      if (!resolved.handled) return;
      event.preventDefault();
      if (resolved.action) runShortcut(resolved.action);
    }
    window.addEventListener("keydown", keyDown);
    return () => window.removeEventListener("keydown", keyDown);
  });

  function runShortcut(action: ShortcutAction) {
    const projects = projectListing?.projects ?? [];
    if (action === "sessionInfo") sessionInfoTrigger.current?.click();
    else if (action === "expandPrompt") {
      if (!dialog) document.querySelector<HTMLButtonElement>("#expand-session-prompt")?.click();
    }
    else if (action === "openProject") setDialog("project");
    else if (action === "renameProject") void beginProjectRename();
    else if (action === "help") setDialog("help");
    else if (action === "escape") {
      if (railVisible && projectRail.current?.contains(document.activeElement)) toggleProjects();
      else { setDialog(null); (document.activeElement as HTMLElement | null)?.blur(); }
    }
    else if (action === "models") navigate("models");
    else if (action === "prompts") navigate("prompts");
    else if (action === "providers") navigate("providers");
    else if (action === "settings" || action === "plugins") navigate("configuration");
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
    } else if (action === "context") activateContextShortcut(workspaceShell.current);
  }

  function toggleProjects() {
    if (railVisible) {
      focusProjectPending.current = false;
      restoreProjectRailFocus(projectRail.current, document.activeElement, projectRailToggle.current);
    } else focusProjectPending.current = true;
    setRailState(current => toggleProjectRail(current, narrow));
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
      || !sessions.some(session => session.id === id)) return;
    menuOrigin.current = origin;
    if (menuTarget?.id === id && menuTarget.projectId === projectId) { dismissSessionMenu(true); return; }
    const switching = selectedSessionId.current !== id || selectedScope.current !== projectId;
    selectedSessionId.current = id;
    selectedScope.current = projectId;
    setSessionId(id);
    if (switching) {
      setRenamingId(null);
      setRenamingMessage("");
      setDeletingId(null);
      setDeletingMessage("");
    }
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
    const access = sessionActionAccess(row, target,
      selectedSession === row && selectedSessionId.current === row.id ? sessionId : null,
      selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
      mutation?.capability.canMutate() ?? false, renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current);
    if (!activeMenu || activeMenu !== target || !access.open) return;
    if (action === "open") { dismissSessionMenu(false); sessionRail.current?.querySelector<HTMLButtonElement>('button[aria-pressed="true"]')?.focus(); return; }
    if (action === "rename" && !access.rename || action === "delete" && !access.delete) return;
    focusAction.current = action;
    dismissSessionMenu(false);
    if (action === "rename") {
      beginSessionRename(row);
      setDeletingId(null);
    } else {
      beginSessionDelete(row);
      setRenamingId(null);
    }
  }

  async function refreshProjects(signal: AbortSignal) {
    try {
      const fresh = await workspace.snapshot({}, { signal, timeoutMilliseconds: 30_000 });
      if (signal.aborted) return undefined;
      setWorkspaceState(fresh.configured ? { kind: "ready", snapshot: fresh } : { kind: "unconfigured" });
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
          try {
            fresh = await workspace.snapshot({}, { signal: creationRefresh.current.signal, timeoutMilliseconds: 30_000 });
            if (creationAlive.current && currentHostEpoch.current === target.epoch && capability.canMutate() && fresh.configured)
              setWorkspaceState({ kind: "ready", snapshot: fresh });
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
    try {
      const fresh = await workspace.snapshot({}, { signal: creationRefresh.current.signal, timeoutMilliseconds: 30_000 });
      if (!creationAlive.current || uncertainProjectRename.current !== uncertain || !capability.canMutate()
        || currentHostEpoch.current !== uncertain.target.epoch || generation !== projectRenameGeneration.current) return;
      if (fresh.configured) setWorkspaceState({ kind: "ready", snapshot: fresh });
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
      || projectId !== null && !selectedProject) return;
    creationPending.current = true;
    const target: SessionTarget = selectedProject
      ? { scope: "project", projectId: selectedProject.id, projectPath: selectedProject.path } : { scope: "global" };
    const scopeAtAdmission = projectId;
    const capability = mutation.capability;
    setCreatingBusy(true);
    setCreatingMessage("");
    try {
      const result = await createSession(status?.hostEpoch, target, creatingTitle.trim() || null, capability);
      if (!creationAlive.current) return;
      if (result.kind === "created") {
        const fresh = await refreshProjects(creationRefresh.current.signal);
        if (!creationAlive.current) return;
        const selection = fresh && createdSessionSelection(fresh, result);
        if (selection && selectedScope.current === scopeAtAdmission && capability.canMutate()) {
          selectedScope.current = selection.projectId;
          setProjectId(selection.projectId);
          setSessionId(selection.sessionId);
          setSearch("");
          setCreatingVisible(false);
          setCreatingTitle("");
          navigate("workspace");
        } else setCreatingMessage("Creation may have completed, but the selected scope changed or the refreshed catalog did not show the session. Inspect and refresh sessions before creating another.");
      } else setCreatingMessage(sessionCreationMessage(result.code));
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

  return <div className="app-shell">
    <header className="topbar">
      <div className="brand"><span className="brand-mark">A</span><span>CodeAlta</span><small>{demoMode ? "interactive preview" : "desktop"}</small>
        {view === "workspace" && <ProjectRailToggle expanded={railVisible} onToggle={toggleProjects} buttonRef={projectRailToggle} />}
      </div>
      <nav className="topnav" aria-label="Primary navigation">
        <button type="button" aria-current={view === "workspace" ? "page" : undefined} onClick={() => navigate("workspace")}>Sessions</button>
        <button type="button" aria-current={view === "configuration" ? "page" : undefined} onClick={() => navigate("configuration")}>Configuration</button>
        <button type="button" aria-current={view === "providers" ? "page" : undefined} onClick={() => navigate("providers")}>Providers</button>
        <button type="button" aria-current={view === "models" ? "page" : undefined} onClick={() => navigate("models")}>Models</button>
        <button type="button" aria-current={view === "prompts" ? "page" : undefined} onClick={() => navigate("prompts")}>Agent prompts</button>
        <button type="button" aria-current={view === "mcp" ? "page" : undefined} onClick={() => navigate("mcp")}>MCP Servers</button>
        <button type="button" aria-current={view === "reminders" ? "page" : undefined} onClick={() => navigate("reminders")}>Reminders</button>
      </nav>
      <div className={`connection ${error ? "connection-error" : connected ? "connection-live" : "connection-readonly"}`}>
        <span className="connection-dot" />
        {error ? "Bridge unavailable" : demoMode ? "Local demo" : connected ? "Runtime connected" : "Catalog only"}
      </div>
    </header>

    {view === "configuration"
      ? <ConfigurationPanel status={status} selectedSession={selectedSession} configurationState={configurationState} theme={theme} setTheme={setTheme} onOpenProviders={() => navigate("providers")} onOpenModels={() => navigate("models")} onOpenPrompts={() => navigate("prompts")} />
      : view === "providers" ? <ProvidersPanel epoch={owned ? status!.hostEpoch : null}
          read={modelCatalog.providers} probe={modelCatalog.probe} catalogProviders={configurationState.snapshot?.providers} holds={providerProbeHolds}
          onOpenModels={() => navigate("models")} />
      : view === "mcp" ? <McpServersPanel target={owned && selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId
          ? { sessionId: selectedSession.id, epoch: status!.hostEpoch!, projectId: selectedSession.projectId ?? null } : null}
          read={mcpInventory.list} />
      : view === "reminders" ? <ReminderPanel key={owned && selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId
          ? JSON.stringify([status!.hostEpoch, selectedSession.id]) : "none"}
          target={owned && selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId
          ? { sessionId: selectedSession.id, epoch: status!.hostEpoch! } : null}
          read={readReminders} actions={reminderActions} mutationAllowed={!!mutation?.capability.canMutate()}
          canMutate={() => !!mutation?.capability.canMutate()} />
      : view === "prompts" ? <PromptCatalogPanel epoch={owned ? status!.hostEpoch : null} readPrompts={promptCatalog.list}
          readChoices={sessionOperations.choices} target={owned && selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId
            ? { sessionId: selectedSession.id, epoch: status!.hostEpoch! } : null}
          selections={nextSendSelections} pendingSend={!!(selectedSession && submissions.pending(selectedSession.id))}
          pendingSelection={selectedSession ? submissions.pending(selectedSession.id)?.request.selection ?? null : null}
          onApply={async (target, signal) => {
            const result = await applyPromptNextSend(target, () => ({ epoch: currentHostEpoch.current ?? null,
              active: currentView.current === "prompts" && !signal.aborted,
              sessionId: selectedScope.current === projectId ? selectedSessionId.current : null,
              canMutate: !!mutation?.capability.canMutate(), pending: !!submissions.pending(target.sessionId) }),
            async (epoch, sessionId) => {
              const value = await sessionOperations.choices({ expectedEpoch: epoch, sessionId }, { signal, timeoutMilliseconds: 15000 });
              mutation?.capability.observe(value);
              return value;
            }, nextSendSelections);
            if (result === "applied" && !signal.aborted) navigate("workspace");
            return result;
          }} />
      : view === "models" ? <ModelCatalogPanel epoch={owned ? status!.hostEpoch : null}
          readProviders={modelCatalog.providers} readModels={modelCatalog.models} readChoices={sessionOperations.choices}
          target={owned && selectedSession?.id === selectedSessionId.current && selectedScope.current === projectId
            ? { sessionId: selectedSession.id, epoch: status!.hostEpoch! } : null}
          selections={nextSendSelections} pendingSend={!!(selectedSession && submissions.pending(selectedSession.id))}
          pendingSelection={selectedSession ? submissions.pending(selectedSession.id)?.request.selection ?? null : null}
          onApply={async (target, signal) => {
            const result = await applyCatalogNextSend(target, () => ({ epoch: currentHostEpoch.current ?? null,
              active: currentView.current === "models",
              sessionId: !signal.aborted && selectedScope.current === projectId ? selectedSessionId.current : null,
              canMutate: !!mutation?.capability.canMutate(), pending: !!submissions.pending(target.sessionId) }),
            async (epoch, sessionId) => {
              const value = await sessionOperations.choices({ expectedEpoch: epoch, sessionId }, { signal, timeoutMilliseconds: 15000 });
              mutation?.capability.observe(value);
              return value;
            }, nextSendSelections);
            if (result === "applied" && !signal.aborted) navigate("workspace");
            return result;
          }} />
      : <div className={`workspace-shell${railVisible ? " project-rail-open" : ""}`} ref={workspaceShell} style={{
          "--project-pane-width": `${visiblePaneLayout.projects}px`,
          "--session-pane-width": `${visibleSessionWidth}px`,
        } as CSSProperties}>
        <aside id="project-rail" className="project-rail" aria-label="Projects" ref={projectRail} hidden={!railVisible}>
          <div className="panel-title"><span>Projects</span><span><button type="button" className="rail-action" title="Open project (Ctrl+O)" onClick={() => setDialog("project")}>＋</button><span className="count">{snapshot?.projects.length ?? 0}</span></span></div>
          {workspaceState.kind === "loading" && <LoadingRows />}
          {workspaceState.kind === "unconfigured" && <div className="sidebar-empty">No catalog configured. See the launch instructions below.</div>}
          {workspaceState.kind === "error" && <div role="alert" className="sidebar-empty error-text">{workspaceState.message}</div>}
          {snapshot && <div className="project-controls">
            <label htmlFor="project-filter">Filter projects by name or path</label>
            <input id="project-filter" ref={projectFilterInput} type="search" value={projectFilter} onChange={event => setProjectFilter(event.target.value)}
              placeholder="Name or path" aria-controls="project-list" />
            <div className="project-sort-controls"><label htmlFor="project-sort">Sort projects</label>
              <select id="project-sort" value={projectSort} onChange={event => setProjectSort(event.target.value as ProjectSort)}>
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
            <button type="button" className="quiet-button icon-label-button" onClick={() => navigate("configuration")}><AppIcon name="settings" size={14} />Settings &amp; extensions</button>
          </div>
        </aside>

        <PaneSplitter className="project-splitter" label="Resize projects" value={visiblePaneLayout.projects} hidden={!railVisible}
          onResize={delta => changePane("projects", delta)} onReset={() => resetPane("projects")} />

        <aside className="session-rail" aria-label="Sessions" ref={sessionRail} hidden={narrow && railVisible}>
          <div className="session-rail-header">
            <div><span className="eyebrow">Sessions</span><h2>{selectedProject?.name ?? "Other sessions"}</h2></div>
            <button type="button" className="icon-button" aria-label="Create session" title="Create session in selected scope"
              disabled={!owned || !snapshot || !!selectedProject?.archived || projectId !== null && !selectedProject || creatingBusy}
              onClick={() => { setCreatingVisible(value => !value); setCreatingMessage(""); }}>＋</button>
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
          <label className="search"><AppIcon name="search" size={14} /><input ref={searchInput} value={search} onChange={event => setSearch(event.target.value)} placeholder="Search sessions" /></label>
          {notice && <p role="status" className="notice">{notice}</p>}
          <div className="session-list">
            {visibleSessionRows.map(({ session, depth, diagnostic, tooltip }, index) => {
              const menu = activeMenu?.id === session.id ? activeMenu : null;
              const access = sessionActionAccess(session,
                menu ?? { id: session.id, projectId, hostEpoch: status?.hostEpoch ?? null },
                selectedSession === session && selectedSessionId.current === session.id ? sessionId : null,
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
              <button type="button" className="quiet-button session-actions-trigger" aria-label={`Actions for ${session.title}`}
                aria-haspopup="menu" aria-expanded={!!menu} aria-controls={menu ? `session-actions-${index}` : undefined}
                onClick={event => openSessionMenu(session.id, event.currentTarget)}>Actions</button>
              {menu && access.open && <SessionActionMenu id={`session-actions-${index}`} label={session.title}
                rename={access.rename} deleteAllowed={access.delete} menuRef={menuRef}
                onAction={action => runSessionMenuAction(action, session, menu)} onDismiss={dismissSessionMenu} />}
              {owned && sessionId === session.id && session.workspacePath && <button type="button" className="quiet-button" aria-label={`Rename ${session.title}`}
                disabled={!access.rename} onClick={() => beginSessionRename(session)}>Rename</button>}
              {owned && sessionId === session.id && session.workspacePath && <button type="button" className="quiet-button" aria-label={`Delete ${session.title}`}
                disabled={!access.delete} onClick={() => beginSessionDelete(session)}>Delete</button>}
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
            : <SessionWorkspace key={JSON.stringify([projectId, selectedSession.id])} session={selectedSession} snapshot={snapshot!} selectedProjectId={projectId} infoTrigger={sessionInfoTrigger} status={status} mutation={mutation}
                submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} draftIndicators={draftIndicators}
                 askActions={askActions} display={display} scrollMemory={scrollMemory} runtimeReader={runtimeReader}
                 permissionReviewer={permissionReviewer} inputReviewer={inputReviewer} configuration={configurationState.snapshot}
                 onNotesChange={updateHistoryNotes} onOpenConfiguration={() => navigate("configuration")} selections={nextSendSelections} />}
        </main>
      </div>}
    {dialog === "project" && <OpenProjectDialog projects={snapshot?.projects ?? []} epoch={owned ? status?.hostEpoch : undefined}
      capability={owned ? mutation?.capability : undefined} opening={projectOpening}
      onOpen={id => { selectProject(id); setDialog(null); }} onRefresh={refreshProjects}
      onImported={async (id, path, signal) => {
        const fresh = await refreshProjects(signal);
        if (!fresh?.configured || signal.aborted || !mutation?.capability.canMutate()
          || !fresh.projects.some(project => project.id === id && project.path === path)) return false;
        selectedScope.current = id;
        setProjectId(id);
        setSessionId(sessionsForProject(fresh, id)[0]?.id ?? null);
        navigate("workspace");
        return true;
      }} onClose={() => setDialog(null)} />}
    {dialog === "help" && <ShortcutHelp onClose={() => setDialog(null)} />}
  </div>;
}

function SessionWorkspace({ session, snapshot, selectedProjectId, infoTrigger, status, mutation, submissions, steering, compaction, abortRuns, queue, draftIndicators, askActions, display, scrollMemory, runtimeReader, permissionReviewer, inputReviewer, configuration: configurationSnapshot, onNotesChange, onOpenConfiguration, selections }: {
  session: WorkspaceSession;
  snapshot: WorkspaceSnapshot;
  selectedProjectId: string | null;
  infoTrigger: RefObject<HTMLButtonElement | null>;
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
  onOpenConfiguration: () => void;
  selections: ReturnType<typeof createNextSendSelectionStore>;
}) {
  const [infoOpen, setInfoOpen] = useState(false);
  function closeInfo() {
    const trigger = infoTrigger.current;
    setInfoOpen(false);
    requestAnimationFrame(() => restoreSessionInfoFocus(trigger));
  }
  const timeline = useTimelinePosition(session.id, scrollMemory);
  const observedDisplay = useSyncExternalStore(display.subscribe, display.getSnapshot);
  const live = status?.hostEpoch && observedDisplay.hostEpoch === status.hostEpoch && observedDisplay.sessionId === session.id
    ? observedDisplay : null;
  const ownedSession = !!(status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch);
  return <div className="session-workspace">
    <header className="session-header">
      <div><span className="eyebrow">Session</span><h1 title={session.title}>{session.title}</h1></div>
      <div className="session-chips"><span>{session.providerKey ?? "Provider not recorded"}</span>
        <span>{demoMode ? "Demo" : status?.hostAvailable ? "Host available" : "Catalog only"}</span>
        <button ref={infoTrigger} type="button" className="quiet-button session-info-trigger" aria-haspopup="dialog" aria-expanded={infoOpen}
          onClick={() => setInfoOpen(true)}>Session info</button></div>
    </header>
    {infoOpen && <SessionInfoDialog info={sessionInfoView(snapshot, session, selectedProjectId)} demo={demoMode} onClose={closeInfo} />}
    {demoMode
      ? <DemoConversation session={session} />
      : <>
        <div className="timeline-scroll" ref={timeline.elementRef} onScroll={event => timeline.scroll(event.currentTarget)}>
        <History sessionId={session.id} onNotesChange={onNotesChange} onSettled={timeline.settled}
          onBeforeOlder={timeline.beforeOlderPage} onAfterOlder={timeline.afterOlderPage} read={workspace.historyTail}
          live={ownedSession ? live?.snapshot?.session ?? null : null} />
        {ownedSession && status?.hostEpoch
        ? <>
          <LiveSessionPanel store={display} hostEpoch={status.hostEpoch} sessionId={session.id} capability={mutation!.capability} />
          {status.ownedAsksEnabled && <AskPanel epoch={status.hostEpoch} sessionId={session.id} actions={askActions} capability={mutation!.capability} />}
          {status.ownedUserInputEnabled && <UserInputPanel epoch={status.hostEpoch} sessionId={session.id} reviewer={inputReviewer} capability={mutation!.capability} />}
        </>
        : null}
        </div>
        {!timeline.following && <button type="button" className="timeline-bottom-button" onClick={timeline.jump}><AppIcon name="arrowDown" size={14} />Jump to latest</button>}
        {ownedSession && status?.hostEpoch
          ? <OwnedSessionPanel sessionId={session.id} epoch={status.hostEpoch} submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} capability={mutation!.capability} runtimeReader={runtimeReader} permissionReviewer={status.commandReviewEnabled ? permissionReviewer : null} configuration={configurationSnapshot} draftIndicators={draftIndicators} selections={selections} />
          : <ReadOnlyComposer sessionId={session.id} provider={session.providerKey} configuration={configurationSnapshot} onOpenConfiguration={onOpenConfiguration} draftIndicators={draftIndicators} />}
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

function ConfigurationPanel({ status, selectedSession, configurationState, theme, setTheme, onOpenProviders, onOpenModels, onOpenPrompts }: {
  status: BootStatus | undefined;
  selectedSession: WorkspaceSession | undefined;
  configurationState: { snapshot?: ConfigurationSnapshot; error?: string };
  theme: Theme;
  setTheme: (theme: Theme) => void;
  onOpenModels: () => void;
  onOpenProviders: () => void;
  onOpenPrompts: () => void;
}) {
  const inventory = configurationState.snapshot;
  const [scope, setScope] = useState<ConfigurationScope>("all");
  const [query, setQuery] = useState("");
  const visible = new Set(visibleConfigurationSections(scope, query));
  const mcp = inventory?.plugins.find(plugin => `${plugin.id} ${plugin.name}`.toLowerCase().includes("mcp"));
  return <main className="configuration-page">
    <header className="page-heading"><span className="eyebrow">Desktop</span><h1>Configuration</h1><p>Inspect the active desktop environment and personalize this window.</p></header>
    <div className="settings-layout">
      <aside className="settings-navigation" aria-label="Configuration sections">
        <label className="settings-search"><AppIcon name="search" size={14} /><input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder="Search settings" /></label>
        <nav>{([ ["all", "All settings"], ["general", "General"], ["agent", "Agent"], ["extensions", "Extensions"] ] as const).map(([value, label]) =>
          <button type="button" key={value} aria-pressed={scope === value} onClick={() => setScope(value)}>{label}</button>)}</nav>
        <button type="button" onClick={onOpenModels}>Open model catalog</button>
        <p>Configuration is read-only unless a card explicitly offers an editable control.</p>
      </aside>
      <div className="settings-grid">
      {visible.has("appearance") && <section className="settings-card"><div className="settings-icon">◐</div><div><h2>Appearance</h2><p>Applied immediately to this window.</p><div className="segmented">
        <button type="button" aria-pressed={theme === "dark"} onClick={() => setTheme("dark")}>Dark</button>
        <button type="button" aria-pressed={theme === "light"} onClick={() => setTheme("light")}>Light</button>
      </div></div></section>}
      {visible.has("providers") && <section className="settings-card"><div className="settings-icon"><AppIcon name="model" size={19} /></div><div><h2>Providers</h2><p>Current session provider: <strong>{selectedSession?.providerKey ?? "not recorded"}</strong>.</p>
        <button type="button" className="quiet-button" onClick={onOpenProviders}>Open provider management</button>
        {configurationState.error && <p className="error-text">{configurationState.error}</p>}
        {!inventory && !configurationState.error && <p>Loading configured providers…</p>}
        {inventory && inventory.providers.length === 0 && <p>No provider inventory is exposed in this launch mode.</p>}
        {inventory?.providers.map(provider => <div className="inventory-row" key={provider.id}><span><strong>{provider.name}</strong><small>{provider.type} · {provider.defaultModel ?? "No default model"}</small></span><StatusPill label={provider.enabled ? "Enabled" : "Disabled"} /></div>)}
        {inventory?.providersTruncated && <p className="muted-text">Showing the first 32 configured providers.</p>}
      </div></section>}
      {visible.has("prompts") && <section className="settings-card"><div className="settings-icon"><AppIcon name="prompt" size={19} /></div><div><h2>Agent prompts</h2><p>Inspect effective host prompts for the selected session and choose its next Send prompt.</p><button type="button" className="quiet-button" onClick={onOpenPrompts}>Browse agent prompts</button><StatusPill label={status?.hostAvailable ? "Session state available" : "Catalog history available"} /></div></section>}
      {visible.has("skills") && <section className="settings-card"><div className="settings-icon"><AppIcon name="tool" size={19} /></div><div><h2>Skills</h2><p>Skills remain project/global filesystem resources and are available to shared agent sessions.</p><StatusPill label="Managed by CodeAlta runtime" /></div></section>}
      {visible.has("plugins") && <section className="settings-card"><div className="settings-icon">⬡</div><div><h2>Plugins &amp; MCP</h2><p>Configured plugin policy is visible in catalog mode. Active state is shown only when the owned runtime has started that plugin.</p>
        <div className="inventory-row"><span><strong>MCP servers</strong><small>Model Context Protocol runtime state</small></span><StatusPill label={mcp ? mcp.state : inventory?.pluginRuntimeAvailable ? "Not configured" : "Runtime not started"} /></div>
        {inventory?.plugins.map(plugin => <div className="inventory-row" key={plugin.id}><span><strong>{plugin.name}</strong><small>{plugin.version ?? "No version"} · {plugin.contributionCount} contributions</small></span><StatusPill label={plugin.state} /></div>)}
        {inventory && inventory.plugins.length === 0 && <StatusPill label={inventory.pluginRuntimeAvailable ? "No active plugins" : "Requires packaged host"} />}
        {inventory?.pluginsTruncated && <p className="muted-text">Showing the first 32 active plugins.</p>}
      </div></section>}
      {visible.has("about") && <section className="settings-card"><div className="settings-icon">i</div><div><h2>About</h2><p>{status?.productName ?? "CodeAlta Desktop"} · {status?.version ?? "initializing"}</p><p className="muted-text">Use <code>altatui</code> for provider/account/plugin mutation until those commands are exposed by the desktop bridge.</p></div></section>}
      {visible.size === 0 && <div className="empty-settings"><h2>No matching settings</h2><p>Try a different search or configuration section.</p></div>}
      </div>
    </div>
  </main>;
}

function StatusPill({ label }: { label: string }) { return <span className="status-pill">{label}</span>; }

function OpenProjectDialog({ projects, epoch, capability, opening, onOpen, onRefresh, onImported, onClose }: {
  projects: ReadonlyArray<WorkspaceProject>; epoch: string | undefined;
  capability: ReturnType<typeof createMutationCapability> | undefined;
  opening: ReturnType<typeof createProjectOpening>;
  onOpen: (id: string) => void; onRefresh: (signal: AbortSignal) => Promise<{ configured: boolean } | undefined>;
  onImported: (id: string, path: string, signal: AbortSignal) => Promise<boolean>;
  onClose: () => void;
}) {
  const [query, setQuery] = useState("");
  const [preview, setPreview] = useState<{ requestedPath: string; path: string }>();
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [notice, setNotice] = useState("");
  const [, notifyCapability] = useState(0);
  const canImport = !!epoch && !!capability?.canMutate();
  const alive = useRef(true);
  const followUp = useRef(new AbortController());
  useEffect(() => {
    alive.current = true;
    followUp.current = new AbortController();
    return () => { alive.current = false; followUp.current.abort(); };
  }, []);
  useEffect(() => {
    return capability?.subscribe(() => notifyCapability(value => value + 1));
  }, [capability]);
  const close = () => { alive.current = false; followUp.current.abort(); onClose(); };
  async function checkPath() {
    if (busy || !canImport) return;
    const requested = query.trim();
    setBusy(true);
    setPreview(undefined);
    setConfirmed(false);
    setMessage("");
    setNotice("");
    const result = await opening.preview(epoch, requested, capability);
    if (!alive.current) return;
    setBusy(false);
    if (result.kind === "ready") setPreview(result);
    else setMessage(projectOpeningMessage(result.kind === "error" ? result.code : "invalid_response"));
  }
  async function importPath() {
    if (!canImportCheckedFolder(preview, confirmed, busy, canImport) || !preview) return;
    setBusy(true);
    setMessage("");
    setNotice("");
    const result = await opening.import(epoch, preview, capability);
    if (!alive.current) return;
    if (result.kind === "imported") {
      if (await onImported(result.id, result.path, followUp.current.signal)) { if (alive.current) close(); return; }
      if (!alive.current) return;
      setMessage("The project may have been imported, but the refreshed catalog did not show it. Inspect the project list before retrying.");
    } else setMessage(projectOpeningMessage(result.kind === "error" ? result.code : "import_unconfirmed"));
    setPreview(undefined);
    setConfirmed(false);
    setBusy(false);
  }
  async function refreshList() {
    if (busy) return;
    setBusy(true);
    const fresh = await onRefresh(followUp.current.signal);
    if (!alive.current) return;
    setBusy(false);
    if (fresh) { setMessage(""); setNotice("Project list refreshed. Check the entries before requesting another import."); }
    else setMessage("Could not refresh the project list. No import was requested.");
  }
  const normalized = query.trim().toLowerCase();
  const matches = projects.filter(project => !normalized || `${project.name} ${project.path}`.toLowerCase().includes(normalized));
  return <div className="dialog-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close(); }}>
    <section className="app-dialog" role="dialog" aria-modal="true" aria-labelledby="open-project-title"
      onKeyDown={event => { if (event.key === "Escape") { event.stopPropagation(); close(); } }}>
      <header><div><span className="eyebrow">Workspace</span><h2 id="open-project-title">Open project</h2></div><button type="button" className="icon-button" aria-label="Close" title="Close" onClick={close}><AppIcon name="close" size={16} /></button></header>
      <label className="settings-search"><AppIcon name="search" size={14} /><input autoFocus aria-label="Project name or absolute folder path" value={query} disabled={busy}
        onChange={event => { setQuery(event.target.value); setPreview(undefined); setConfirmed(false); setMessage(""); setNotice(""); }}
        onKeyDown={event => { if (event.key === "Escape") { event.stopPropagation(); close(); }
          else if (event.key === "Enter" && !event.nativeEvent.isComposing) { event.preventDefault(); void checkPath(); } }}
        placeholder="Project name or absolute folder path" /></label>
      <div className="dialog-list">{matches.map(project => <button type="button" key={project.id} onClick={() => onOpen(project.id)}>
        <span className="project-icon">{project.name.slice(0, 1).toUpperCase()}</span><span><strong>{project.name}</strong><small>{project.path}</small></span>
      </button>)}</div>
      {matches.length === 0 && <p className="muted-text">No known project matches. Enter an absolute path to check another existing folder.</p>}
      {!canImport && <p className="muted-text">Adding a folder requires an owned host. Catalog-only browsing never changes the project list.</p>}
      {canImport && <div className="project-import">
        <button type="button" className="quiet-button" disabled={busy || !query.trim()} onClick={() => void checkPath()}>Check folder</button>
        {preview && <><p>Existing folder: <code>{preview.path}</code></p>
          <label><input type="checkbox" checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} /> I trust this folder and want to add it to the active project catalog.</label>
          <button type="button" className="quiet-button" disabled={!canImportCheckedFolder(preview, confirmed, busy, canImport)} onClick={() => void importPath()}>Import and open folder</button></>}
      </div>}
      {busy && <p role="status">Checking or importing the folder…</p>}
      {notice && <p role="status">{notice}</p>}
      {message && <p role="alert" className="error-text">{message}</p>}
      <footer><span><kbd>Ctrl</kbd>+<kbd>O</kbd> · <kbd>Esc</kbd></span><span>
        <button type="button" className="quiet-button" disabled={busy} onClick={() => void refreshList()}>Refresh projects</button>{" "}
        <button type="button" className="quiet-button" onClick={close}>Cancel</button></span></footer>
    </section>
  </div>;
}

function ShortcutHelp({ onClose }: { onClose: () => void }) {
  const shortcuts = [
    ["Ctrl+O", "Open project"], ["Ctrl+F", "Search sessions"], ["Alt+↑ / Alt+↓", "Previous / next session"],
    ["Alt+← / Alt+→", "Previous / next project"], ["Ctrl+,", "Configuration"], ["Ctrl+Shift+N", "Toggle Alta notes"],
    ["Ctrl+G, Ctrl+P", "Focus prompt"], ["Ctrl+G, Ctrl+S", "Focus projects"], ["Ctrl+G, Ctrl+R", "Providers"],
    ["Ctrl+G, Ctrl+O", "Models"], ["Ctrl+G, Ctrl+H", "Agent prompts"], ["Ctrl+G, Ctrl+U", "Context state"],
    ["Ctrl+G, Ctrl+T", "Session info (selected workspace session only)"],
    ["F1 or ?", "Keyboard shortcuts"], ["Escape", "Close / cancel"], ["Enter / Shift+Enter", "Send / new line in prompt"],
    ["F6", "Expand prompt (owned session)"], ["Ctrl+Enter", "Steer in regular prompt; close in expanded editor"],
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

function ReadOnlyComposer({ sessionId, provider, configuration, onOpenConfiguration, draftIndicators }: {
  sessionId: string; provider: string | null; configuration?: ConfigurationSnapshot; onOpenConfiguration: () => void;
  draftIndicators: ReturnType<typeof createDraftIndicators>;
}) {
  const [draft, setDraft] = useState(() => ({ text: restoreDraft(key => localStorage.getItem(key), sessionId), editGeneration: null as number | null }));
  const text = draft.text;
  const restoredText = useRef(text);
  useLayoutEffect(() => { draftIndicators.clear(sessionId); }, [draftIndicators, sessionId]);
  const [message, setMessage] = useState("Draft locally; sending requires an explicitly owned desktop host.");
  useEffect(() => { draftIndicators.persisted(sessionId, draft.editGeneration,
    persistDraft((key, value) => localStorage.setItem(key, value), key => localStorage.removeItem(key), sessionId, draft.text));
  }, [sessionId, draft, draftIndicators]);
  return <section className="composer catalog-composer" aria-label="Message composer">
    <div className="prompt-options" aria-label="Session configuration">
      <label><span>Agent prompt</span><select aria-label="Agent prompt" value="recorded" disabled><option value="recorded">Recorded by session</option></select></label>
      <label><span>Model</span><select aria-label="Model" value="recorded" disabled><option value="recorded">Recorded by session</option></select></label>
      <label><span>Reasoning</span><select aria-label="Reasoning" value="recorded" disabled><option value="recorded">Recorded by session</option></select></label>
      <button type="button" className="prompt-state" onClick={onOpenConfiguration} aria-label="Open provider configuration" title={provider ?? "Provider not recorded"}><AppIcon name="settings" size={13} /><strong>{configuration?.providers.length ?? 0} providers</strong></button>
      <span className="prompt-state"><span>Context / MCP</span><strong>Requires runtime</strong></span>
    </div>
    <textarea id="catalog-prompt" aria-label="Message" maxLength={32768} value={text} onChange={event => {
      const value = event.target.value;
      setDraft({ text: value, editGeneration: draftIndicators.edit(sessionId, value, restoredText.current) });
    }}
      onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); setMessage("This explicit catalog-only launch is read-only; your draft remains saved."); } }}
      placeholder="Draft a prompt for this session…" />
    <div className="composer-footer"><span role="status">{message}</span><button type="button" disabled={!text.trim()} onClick={() => setMessage("This explicit catalog-only launch is read-only; your draft remains saved.")}>Send <AppIcon name="send" size={14} /></button></div>
  </section>;
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
