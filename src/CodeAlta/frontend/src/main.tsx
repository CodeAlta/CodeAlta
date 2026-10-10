import { Button, Classes, HTMLSelect, InputGroup, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext } from "@blueprintjs/core";
import { connect, onDiagnostic } from "@neoastra/client";
import { rpcFailureCode } from "./rpcDiagnostics";
import { StrictMode, createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type ContextType, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent, type RefObject, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import { ComposerStatus } from "./ComposerStatus";
import { settingsNavigation } from "./settingsNavigation";
import { dismissStartupScreen } from "./startupScreen";
import { ConfigRecoveryScreen } from "./ConfigRecoveryScreen";
import { createAppearancePreview } from "./appearancePreview";
import { useColorSchemeLibrary } from "./colorSchemeLibrary";
import { ShellAppearance } from "./ShellAppearance";
import { RunningExitDialog } from "./RunningExitDialog";
import { CloseWindowDialog } from "./CloseWindowDialog";
import { closeBehavior, entryAddedGuide, entryAddedNotice, type CloseBehavior } from "./desktopShell";
import { EntryAddedDialog } from "./EntryAddedDialog";
import { showToast } from "./appToaster";
import { availableUpdate, installedNotice, updateCheckInterval, updateToAnnounce, UpdateNotice } from "./UpdateNotice";
import {
  boot, configuration, applicationLogs, modelCatalog, reminder, workspace as workspaceApi, spaces as spacesApi, sessionDisplay, sessionRuntimeState, sessionPermissions, sessionOperations,
  sessionAsks, sessionNotes, sessionPluginEvents, projectFiles, projectGit, promptImages, toolCalls, composerStatus, pluginUi, sessionUserInput, type BootStatus, type CanvasItem, canvases as canvasesApi, plugins as pluginsApi,
  type ReminderListRequest,
  type ReminderListResponse,
  type ReminderDetailRequest,
  type ConfigurationSnapshot, type WorkspaceSession, type WorkspaceSnapshot,
  desktopShell, type DesktopShellPreferences, appUpdate, type AppUpdateResponse, terminals, type TerminalItem, automations, workItems as workItemsApi, issues as issuesApi, pullRequestPrompts, markdownLinks, settingsFiles,
} from "#neoastra";
import { loadWorkspace, sessionListSignature, sessionsForProject, workspaceNotice, type WorkspaceState } from "./workspace";
import { History } from "./HistoryPanel";
import { BackgroundCallsContext } from "./BackgroundTaskViews";
import { backgroundCalls, runningBackgroundTasks, type BackgroundCallState } from "./backgroundTasks";
import { MessageLinksContext } from "./TimelineMessage";
import { markdownHrefKind, openMarkdownLink, type MarkdownLinkScope } from "./markdownLinks";
import { MarkdownLinkScopeContext } from "./MarkdownContent";
import { readTimeline } from "./readTimeline";
import { createTimelineImageCache } from "./timelineImages";
import { createToolCallCache } from "./toolCallReader";
import { createToolOutputStore } from "./toolOutput";
import { SessionContentLayout } from "./SessionContentLayout";
import { SessionTabStrip } from "./SessionTabStrip";
import { createSessionTabModel, sessionTabPresentation } from "./sessionTabLayout";
import { createReferencePopupLifetime } from "./referencePopup";
import { SessionBrowser } from "./SavedSessionBrowser";
import { createRuntimeObservations, maximumRuntimeRows, runtimeTarget } from "./runtimeObservations";
import { archiveScopeCurrent, createProjectArchive, type ArchiveScope } from "./projectArchive";
import { browserActivation } from "./sessionBrowser";
import { closeSessionTab, emptySessionTabs, openSessionTab, persistSessionTabs, reconcileSessionTabs, resolveSessionTab, restoreSessionTabs, selectedTab, sessionTabsKey, tabKey, type SessionTab, type SessionTabs as SessionTabsState } from "./sessionTabs";
import { activateFileTab, automationsTab, canvasTab, canvasesTab, isCanvasTab, isCanvasesTab, refreshCanvasTab, isIssuesTab, issuesTab, isWorkItemsTab, workItemsTab, changesTab, closeFileTab, cycleTab, editorTab, emptyFileTabs, fileTabKey, isAutomationsTab, isChangesTab, isEditorTab, isFolderTab, isTerminalTab, fileTabsKey, openFileTab, persistFileTabs, pluginEditorTab, pluginFolderPrefix, diskEditorTab, diskFolderPrefix, reconcileFileTabs, reconcileTerminalTabs, reopenTabKind, resolveFileTab, restoreFileTabs, restoreLegacyFiles, sameFileTab, skillEditorTab, terminalTab, type FileTab, type FileTabs, type TabKind, type TabPosition } from "./fileTabs";
import { createFileEditors } from "./editor/fileEditors";
import { adoptLegacyFiles, editorStorageKey } from "./editor/editorWorkbench";
import { OpenFileDialog } from "./editor/OpenFileDialog";
import { ProjectEditor, type EditorRequest } from "./editor/ProjectEditor";
import { UnsavedExitDialog, UnsavedFileDialog } from "./editor/UnsavedDialogs";
import { ProjectChangesPanel } from "./changes/ProjectChangesPanel";
import { changeKeyKept } from "./changes/projectChanges";
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
import { SessionWaitingBadge, WaitingBadge } from "./WaitingBadge";
import { PermissionSettings } from "./PermissionSettings";
import { verifiedReminderCountTarget } from "./reminderListObservation";
import { applyCatalogNextSend, createNextSendSelectionStore } from "./nextSendSelection";
import { createMutationCapability, createOwnedSubmissions, noOutgoing } from "./sessionOperations";
import { createSessionDisplayStore } from "./sessionDisplay";
import { createRuntimeStateReader } from "./runtimeState";
import { createPermissionReviewer } from "./sessionPermissions";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { AskPanel, type AskMode } from "./AskPanel";
import { askWireRequest, createAskActions } from "./sessionAsks";
import { SessionNotesOverlay } from "./SessionNotesOverlay";
import { RunningSessionBadge } from "./RuntimeObservation";
import { createNotesReader } from "./sessionNotes";
import { createUserInputReviewer } from "./sessionUserInput";
import { UserInputPanel } from "./UserInputPanel";
import { LiveSessionPanel } from "./LiveSessionPanel";
import { createTimelineScrollMemory, useExplicitNewestHistory, useTimelinePosition, timelineNotice, type TimelineNotice, type MessageNavigation } from "./timelineScroll";
import { workspaceEditingSelector, type ShortcutAction } from "./shortcuts";
import { createDraftIndicators, createLocalDrafts, draftSendFacts, draftStorageKey, persistDraft, restoreDraft, transferPromptDraft } from "./promptDraft";
import { SessionDraftStatus } from "./SessionDraftBadge";
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
import { pickFolder, sameFolder } from "./folderPicker";
import { savedProjectSelection } from "./savedProjectSelection";
import { createSessionCreation, createdSessionSelection, sessionCreationMessage, worktreeCreationMessage, type SessionTarget } from "./sessionCreation";
import { createSessionRename, renamedSessionVisible, renameSelectionCurrent, sessionRenameMessage, type RenameTarget } from "./sessionRename";
import { createSessionDeletion, deletedSessionRecovery, deleteSelectionCurrent, sessionDeletionMessage, type DeletedTarget } from "./sessionDeletion";
import { batchDeleteCandidate, createSessionBatchDeletion } from "./sessionBatchDeletion";
import type { BatchDeleteControls } from "./SessionBatchDeletePanel";
import { ConfirmPopover } from "./ConfirmPopover";
import { RenamePopover } from "./RenamePopover";
import { createProjectRename, projectNameVisible, projectRenameMessage, projectRenameSelectionCurrent, type ProjectNameTarget } from "./projectRename";
import { sessionHierarchy } from "./sessionHierarchy";
import { SessionTabMenu, type SessionMenuEntry } from "./SessionTabMenu";
import { plainTitle } from "./sessionTitle";
import { isSessionContextKey, isSessionDeleteKey, restoreSessionMenuFocus, sessionActionAccess, type SessionAction, type SessionMenuTarget } from "./sessionRowActions";
import { projectRailProjection } from "./explorer/projectRail";
import { ProjectRailRows } from "./explorer/ProjectRailRows";
import { createTerminalWorkspace } from "./terminal/terminalWorkspace";
import { TerminalPanel } from "./terminal/TerminalPanel";
import { createAutomationsHub } from "./automations/automationsHub";
import { AutomationsPanel } from "./automations/AutomationsPanel";
import { SessionOrigin } from "./automations/SessionOrigin";
import { sessionOrigin } from "./automations/automations";
import { IssuesPanel } from "./issues/IssuesPanel";
import { PullRequestButton } from "./pullRequests/PullRequestButton";
import { PullRequestSettings } from "./pullRequests/PullRequestSettings";
import { createWorkItemsHub } from "./workItems/workItemsHub";
import { WorkItemsPanel, type WorkItemsFocus } from "./workItems/WorkItemsPanel";
import { WorkItemCards } from "./workItems/WorkItemCards";
import { WorkItemsBadge } from "./workItems/WorkItemsBadge";
import { WorkItemSettings } from "./workItems/WorkItemSettings";
import type { RunModelsLoader, RunProvider, RunsWith } from "./workItems/runsWith";
import { startWorkItem, type WorkStartSession } from "./workItems/startWorkItem";
import { carriedBy, readingOrder, sessionCards, workCounts, workItemKey, workItems, type WorkItem, type WorkStart } from "./workItems/workItems";
import { TerminalList } from "./terminal/TerminalList";
import { persistTerminalLook, restoreTerminalLook, terminalLookKey, type TerminalLook } from "./terminal/terminalLook";
import { applicationKey, terminalsOf } from "./terminal/terminals";
import { ExplorerSessions, SessionRowTitle, SubAgentDisclosure, sessionRowIndent, sessionTwist, sessionTwistKey } from "./explorer/ExplorerSessions";
import { listedSessions, sessionList, type SessionListEntry } from "./explorer/sessionTree";
import { ProviderBrandsContext, providerBrands } from "./ProviderIcon";
import { SessionLinksContext, type SessionLinks } from "./SessionReference";
import { SessionWidthContext, SessionWidthGrips, useSessionWidthStyle, type SessionWidthControl } from "./SessionWidthGrips";
import { applySessionWidth, defaultSessionWidth, sessionWidthsOf, validSessionWidth, withSessionWidth } from "./sessionWidth";
import { collapseAllScopes, emptyProjectTree, expandScope, globalScope, isCollapsed, isExpanded, isFavorite, persistProjectTree, projectTreeKey, restoreProjectTree, scopeKey, setCollapsed,
  setFavorite, toggleScope, type ProjectTree } from "./explorer/projectTree";
import { defaultIdeWidth, maximumIdeWidth, minimumIdeWidth, parseIdeWidth, persistIdeWidth, resizeIdeWidth } from "./ideWidth";
import { focusVisibleProject, projectRailVisible, restoreProjectRailFocus } from "./explorer/projectRailVisibility";
import { nextTheme, themeLabel, useWindowPreferences } from "./windowPreferences";
import { themeIcons } from "./GeneralSettings";
import { GeneralSettings } from "./GeneralSettings";
import { createHostLiveness, hostPingInterval, hostPingTimeout } from "./hostLiveness";
import { reloadsAfterClose, rpcSessionClosed, sessionRecoveryKey } from "./sessionRecovery";
import { installKeyboardClickGuard } from "./keyboardClickGuard";
import { closeApplicationWindow, logoUrl, useWindowTitleBar, WindowBrand, WindowControls } from "./windowChrome";
import { WindowZoom, zoomWindow } from "./WindowZoom";
import { createPluginEventsRead } from "./pluginEvents";
import { ProjectContext } from "./ProjectContext";
import { WorktreeManager } from "./worktrees/WorktreeManager";
import { WorktreeSettings } from "./worktrees/WorktreeSettings";
import { McpHostSettings } from "./mcpHost/McpHostSettings";
import { createSpacesHub } from "./spaces/spacesHub";
import { SpaceActivityBar, SpaceSwitch } from "./spaces/SpaceViews";
import { SpaceDialog } from "./spaces/SpaceDialog";
import { SpaceSettings } from "./spaces/SpaceSettings";
import { defaultSpaceId, findSpace, neighborSpace, persistShownSpace, placeProject, restoreShownSpace, sameMembers, scopeSnapshot, shownSpaceKey, spaceCalls, spaceMembers, spaceShows,
  spaceStorageKey } from "./spaces/spaces";
import { persistWorkPlaces, projectFolder as inProjectFolder, restoreWorkPlaces, sessionWorktree, withWorkPlace, workPlacesKey, type WorkPlace } from "./worktrees/worktrees";
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
import { GlobalSearch, type SearchChoice, type SearchStart } from "./search/GlobalSearch";
import { PluginUiHost } from "./PluginUiHost";
import { PluginRegionSlot } from "./PluginRegions";
import { askPluginComposer, noPluginContributions, pluginCommandAvailable, pluginContributions, pluginKeymap, PluginUiContext, resolvePluginKey,
  findPluginCommand, type PluginComposerRequest, type PluginContributionsView, type PluginPane, type PluginUiValue, pluginsChangedEvent } from "./pluginUi";
import { CommandHelp } from "./CommandHelp";
import { commandDefinitions, resolveCommandKey, type CommandId } from "./commandRegistry";
import { LandingShellContext } from "./landing/landingShell";
import { useLandingAtStartup, useLandingShell } from "./landing/landingWindow";
import { createPaletteFocusRestoration } from "./paletteActions";
import "normalize.css";
import "@blueprintjs/core/lib/css/blueprint.css";
import "flexlayout-react/style/light.css";
import "@xterm/xterm/css/xterm.css";
import "./style.css";
import "./editor/editor.css";
import "./explorer/explorer.css";
import "./terminal/terminal.css";
import "./automations/automations.css";
import "./workItems/workItems.css";
import "./charts/charts.css";
// The Statistics canvas is a module the application loads when a tab shows it, and a module brings no stylesheet with it: the sheet is part of the page.
import "./statistics/statistics.css";
import "./issues/issues.css";
import "./worktrees/worktrees.css";
import "./mcpHost/mcpHost.css";
import "./spaces/spaces.css";
import "./canvases/canvases.css";
import "./landing/landing.css";
import "./pluginButtons/pluginButtons.css";
import { CanvasPanel } from "./canvases/CanvasPanel";
import { PluginHostBridgeContext, type PluginHostBridge } from "./pluginScript/hostBridge";
import { createCanvasHub, type CanvasClosedInstance, type CanvasOpenRequest } from "./canvases/canvasHub";
import { createCanvasInstances } from "./canvases/canvasInstances";
import { CanvasesPanel } from "./canvases/CanvasesPanel";
import { addCanvasTabToSpace, bringCanvasTab, canvasMenuItems, canvasRequestSpace, canvasStatusKey, canvasTabOf, closedCanvasTab, defaultCanvasTarget, newCanvasPrompt, removeCanvasTabFromSpace, withCanvasStatus, type CanvasSelection, type CanvasTarget } from "./canvases/canvasPages";
import { createCanvasPluginControl, type CanvasPluginControl } from "./canvases/canvasPlugin";
import { PluginButtons } from "./pluginButtons/PluginButtons";
import { usePluginMenuEntries } from "./pluginButtons/pluginMenu";
import { PluginButtonsActiveContext, PluginButtonsContext, createHiddenButtons, type PluginButtonContext, type PluginButtonView, type PluginButtonsHost } from "./pluginButtons/pluginButtonModel";
import { dismissDialogsOnOutsidePress, modalDialogOpen } from "./modalDialogs";

type TimelineCommand = Readonly<{ sessionId: string; projectId: string | null; epoch: string | null;
  ready: () => boolean; navigate: (action: MessageNavigation) => void;
  latestReady: () => boolean; latest: () => void; cancelLatest: () => void }>;

const demoMode = import.meta.env.VITE_DEMO_MODE === "true";
type View = "workspace" | "appearance" | "spaces" | "providers" | "models" | "prompts" | "mcp" | "logs" | "skills" | "plugins" | "about" | "config" | "worktrees" | "workItems" | "pullRequests" | "mcpHost" | "permissions";
type SettingsSection = Exclude<View, "workspace">;

function App() {
  const language = useLanguagePreference();
  const t = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate(language.locale, key, parameters);
  // A press outside a window dismisses it, as Escape does.
  useEffect(() => dismissDialogsOnOutsidePress(document), []);
  // The spaces: the groups of projects the window shows one at a time. What is read of the catalog is kept
  // whole, and the window is given what the shown space has of it, so that everything it lists and opens
  // (the Explorer, the tabs, the search, the work items) is the space's.
  const [spacesHub] = useState(() => createSpacesHub(spacesApi));
  // The tabs that plugins provide: what they declare, what they ask of the window, and what they push to their tabs.
  const [canvasHub] = useState(() => createCanvasHub(canvasesApi));
  // The buttons of plugins that the user hid are a view state of this window, as its open tabs are.
  const [hiddenPluginButtons] = useState(() => createHiddenButtons(typeof localStorage === "undefined" ? null : localStorage));
  const spacesState = useSyncExternalStore(spacesHub.subscribe, spacesHub.getSnapshot);
  const [spaceId, writeSpaceId] = useState(() => restoreShownSpace(() => localStorage.getItem(shownSpaceKey)));
  const shownSpace = useRef(spaceId);
  const catalog = useRef<WorkspaceSnapshot | undefined>(undefined);
  const scopedMembers = useRef<ReadonlySet<string> | null>(null);
  const [workspace] = useState(() => ({ ...workspaceApi, snapshot: async (...request: Parameters<typeof workspaceApi.snapshot>) => {
    const fresh = await workspaceApi.snapshot(...request);
    // A window that comes back on a space waits for the spaces before it shows the projects of that one alone.
    if (shownSpace.current !== defaultSpaceId) await spacesHub.whenLoaded(6_000);
    catalog.current = fresh;
    scopedMembers.current = spaceMembers(spacesHub.getSnapshot().spaces, shownSpace.current);
    return scopeSnapshot(fresh, scopedMembers.current);
  } }));
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
  // Sessions also appear without the window asking: an agent creates sub-sessions through the alta tool.
  // While a run is live, and when it ends, the session list is read again and shown only if it differs.
  const liveRuns = useRef(new Set<string>());
  const sessionListRead = useRef(false);
  const readSessionList = useRef(() => {});
  readSessionList.current = () => {
    if (sessionListRead.current || !currentSnapshot.current || document.visibilityState !== "visible") return;
    sessionListRead.current = true;
    const revision = browserRevision.current;
    void workspace.snapshot({}, { timeoutMilliseconds: 30_000 }).then(fresh => {
      const shown = currentSnapshot.current;
      if (!creationAlive.current || !shown || !fresh.configured || revision !== browserRevision.current
        || sessionListSignature(shown) === sessionListSignature(fresh)) return;
      publishWorkspaceState({ kind: "ready", snapshot: fresh });
    }).catch(() => {}).finally(() => { sessionListRead.current = false; });
  };
  // A run that ends may have written a plan or proposed a task: the work items are read again too.
  const refreshWorkItems = useRef(() => {});
  const noteRunActivity = (id: string, running: boolean | null) => {
    const was = liveRuns.current.has(id);
    if (running) liveRuns.current.add(id); else liveRuns.current.delete(id);
    if (was && !running) { readSessionList.current(); refreshWorkItems.current(); }
  };
  useEffect(() => {
    const timer = setInterval(() => { if (liveRuns.current.size > 0) readSessionList.current(); }, 10_000);
    return () => clearInterval(timer);
  }, []);
  const [projectId, writeProjectId] = useState<string | null>(null);
  const [sessionId, writeSessionId] = useState<string | null>(null);
  const [restoredTabs] = useState(() => restoreSessionTabs(() => localStorage.getItem(spaceStorageKey(sessionTabsKey, spaceId))));
  const [tabs, setTabs] = useState(restoredTabs ?? emptySessionTabs);
  const [tabsReady, setTabsReady] = useState(false);
  // File editor tabs share the strip with the sessions. An active file is shown over the session selection,
  // which stays as it is; selecting a session, a project or the new-session tab leaves the file.
  const [restoredFileTabs] = useState(() => {
    // The files that were tabs of their own before the editor had tabs: each project's editor opens its files.
    adoptLegacyFiles(() => localStorage.getItem(editorStorageKey), value => localStorage.setItem(editorStorageKey, value),
      restoreLegacyFiles(() => localStorage.getItem(fileTabsKey)));
    return restoreFileTabs(() => localStorage.getItem(spaceStorageKey(fileTabsKey, spaceId)));
  });
  const [fileTabs, setFileTabs] = useState(emptyFileTabs);
  const [fileEditors] = useState(createFileEditors);
  useSyncExternalStore(fileEditors.subscribe, fileEditors.snapshot);
  const [fileClosing, setFileClosing] = useState<{ tab: FileTab; busy: boolean } | null>(null);
  // What was last asked of a changes tab, by project: the file an `alta diff show` asked it to select, the
  // checkout a session that works in a worktree asked it to show.
  const [changeRequests, setChangeRequests] = useState<ReadonlyMap<string, Readonly<{ path: string | null; worktree?: string | null }>>>(() => new Map());
  // Where the next session of each project works: the folder of the project unless a new worktree was chosen.
  const [workPlaces, setWorkPlaces] = useState(() => restoreWorkPlaces(() => localStorage.getItem(workPlacesKey)));
  const [setWorkPlace] = useState(() => (id: string, place: WorkPlace) => setWorkPlaces(current => {
    const next = withWorkPlace(current, id, place);
    persistWorkPlaces(value => localStorage.setItem(workPlacesKey, value), next);
    return next;
  }));
  // What was last asked of each project's code editor: a file, a place in it, its files.
  const [editorRequests, setEditorRequests] = useState<ReadonlyMap<string, EditorRequest>>(() => new Map());
  // The kinds of tab closed, oldest first: Reopen restores the most recent one.
  const closedTabKinds = useRef<TabKind[]>([]);
  // Scope-local text is App-owned even when storage is denied or workspace DOM is unmounted.
  const [localDrafts] = useState(createLocalDrafts);
  const localImageGeneration = useRef(0);
  const draftScope = `local-draft:${JSON.stringify(projectId)}`;
  const localDraftStorageKey = `codealta.desktop.localPrompt.${JSON.stringify(projectId)}`;
  // The draft as it is at this render. What runs later (a paste, Start session) reads it again: a keystroke
  // renders the prompt that shows the text, and App only when what decides the sending changes.
  const localDraft = localDrafts.get(draftScope, () => restoreDraft(() => localStorage.getItem(localDraftStorageKey), draftScope));
  useSyncExternalStore(localDrafts.subscribe, () => draftSendFacts(localDrafts.peek(draftScope)?.text ?? "", imageLimits.text));
  const [draftHandoffNotice, setDraftHandoffNotice] = useState("");
  // A prompt typed in the New session tab is sent as soon as its session exists and holds the text.
  const autoSend = useRef<{ sessionId: string; text: string } | null>(null);

  function editLocalDraft(text: string) {
    localDrafts.edit(draftScope, text);
    persistDraft((_key, value) => localStorage.setItem(localDraftStorageKey, value), () => localStorage.removeItem(localDraftStorageKey), draftScope, text);
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
    setProviderGuide(false);
    const origin = settingsOrigin.current;
    focusRestoration.schedule(origin, () => currentView.current === settingsOriginView.current,
      () => !!modalDialogOpen());
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
  openSettingsPage.current = page => { if (page === "mcp" || page === "plugins" || page === "providers" || page === "skills" || page === "spaces") navigate(page); };
  useEffect(() => settingsNavigation.subscribe(page => openSettingsPage.current(page)), []);
  const { projectSort, setProjectSort, theme, variant, appearance, setTheme, darker, setDarker, colorScheme, shownScheme, setColorScheme, customSchemes, setCustomSchemes, railState, setDesktopCollapsed, toggleRail, closeNarrowRail, notices: preferenceNotices, recentSessionCount, setRecentSessionCount, subAgentCount, setSubAgentCount, confirms, setConfirm } = useWindowPreferences();
  // The user's own color schemes, and what the editor of one shows while it edits.
  const schemeLibrary = useColorSchemeLibrary(setCustomSchemes);
  const [appearancePreview] = useState(createAppearancePreview);
  // What the Explorer remembers between starts: the scopes left open and the favorite projects.
  const [storedProjectTree] = useState(() => restoreProjectTree(() => localStorage.getItem(projectTreeKey)));
  const [projectTree, setProjectTree] = useState<ProjectTree>(storedProjectTree ?? emptyProjectTree);
  useEffect(() => { persistProjectTree(value => localStorage.setItem(projectTreeKey, value), projectTree); }, [projectTree]);
  // How many more sessions than at first each scope of the Explorer shows.
  const [sessionExtras, setSessionExtras] = useState<ReadonlyMap<string, number>>(() => new Map());
  function setSessionExtra(scope: string | null, extra: number) {
    setSessionExtras(current => {
      if ((current.get(scopeKey(scope)) ?? 0) === extra) return current;
      const next = new Map(current);
      if (extra > 0) next.set(scopeKey(scope), extra); else next.delete(scopeKey(scope));
      return next;
    });
  }
  // How many more sub-agents than at first each session of the Explorer lists.
  const [subAgentExtras, setSubAgentExtras] = useState<ReadonlyMap<string, number>>(() => new Map());
  function setSubAgentExtra(id: string, extra: number) {
    setSubAgentExtras(current => {
      if ((current.get(id) ?? 0) === extra) return current;
      const next = new Map(current);
      if (extra > 0) next.set(id, extra); else next.delete(id);
      return next;
    });
  }
  // The sub-agents of a session, in any scope: hidden or shown, and more or fewer of them listed.
  const sessionTree = {
    toggle: (session: WorkspaceSession, collapsed: boolean) => setProjectTree(current => setCollapsed(current, session.id, collapsed)),
    more: (id: string) => setSubAgentExtra(id, (subAgentExtras.get(id) ?? 0) + subAgentCount),
    fewer: (id: string) => setSubAgentExtra(id, 0),
  };
  const [notesVisible, setNotesVisible] = useState(true);
  const [dialog, writeDialog] = useState<"project" | "help" | "sessions" | "reminders" | "file" | "worktrees" | null>(null);
  // The project whose git worktrees the window of the worktrees shows; it lasts as long as that window.
  const [worktreesProject, setWorktreesProject] = useState<Readonly<{ id: string; name: string; path: string }> | null>(null);
  // The folder chosen with "+" that the Open project window opens on; it lasts as long as that window.
  const [projectFolder, setProjectFolder] = useState<string | null>(null);
  const addingFolder = useRef(false);
  function setDialog(value: typeof dialog) {
    batchDeletion.invalidate(); invalidateCreation(); writeDialog(value);
    if (value !== "project") setProjectFolder(null);
    if (value !== "worktrees") setWorktreesProject(null);
  }
  const helpOrigin = useRef<{ element: HTMLElement | null; view: View; sessionId: string | null; scope: string | null } | null>(null);
  // The search of the window: where it starts while it is open, and what was chosen in it, which runs once it has closed.
  const [searchStart, writeSearchStart] = useState<SearchStart | null>(null);
  const searchOpen = searchStart !== null;
  function setSearchStart(value: SearchStart | null) { invalidateCreation(true); writeSearchStart(value); }
  const searchOrigin = useRef<HTMLElement | null>(null);
  const searchPending = useRef<SearchChoice | null>(null);
  const [pluginContributed, setPluginContributed] = useState<PluginContributionsView>(noPluginContributions);
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
      const closed = () => {
        console.warn("[CodeAlta RPC] connection closed; inspect receipts before any explicit retry. No automatic replay.", { reason: connection.closeReason ?? null });
        // The host closed this document's session and nothing answers any more: only a reload opens a new one.
        if (connection.closeReason !== rpcSessionClosed) return;
        hostLiveness.lost();
        const read = () => { try { const value = sessionStorage.getItem(sessionRecoveryKey); return value === null ? null : Number(value); } catch { return null; } };
        if (!reloadsAfterClose(connection.closeReason, fileEditors.anyDirty(), Date.now(), read())) return;
        try { sessionStorage.setItem(sessionRecoveryKey, String(Date.now())); } catch { /* Without the mark a second loss reloads again. */ }
        window.location.reload();
      };
      if (connection.closed.aborted) closed();
      else connection.closed.addEventListener("abort", closed, { once: true, signal: lifetime.signal });
    }).catch(error => { if (!lifetime.signal.aborted) console.warn("[CodeAlta RPC] connection unavailable", { code: rpcFailureCode(error) }); });
    return () => { lifetime.abort(); unsubscribe(); };
  }, []);
  const [submissions] = useState(() => createOwnedSubmissions(sessionOperations.send, sessionOperations.abort));
  const [timelineImages] = useState(() => createTimelineImageCache(promptImages.read));
  const [toolRecords] = useState(() => ({ records: createToolCallCache(toolCalls.read), outputs: createToolOutputStore(toolCalls.observe) }));
  const reminderCapability = useRef<ReturnType<typeof createMutationCapability> | undefined>(undefined);
  const [reminderActions] = useState(() => createReminderActions(reminder.create, reminder.delete, (target, reply) => {
    const capability = reminderCapability.current;
    if (capability?.canSubmit({ expectedEpoch: target.epoch })) capability.observe(reply);
  }, reminder.save));
  const [providerProbeHolds] = useState(() => new Set<string>());
  const [draftIndicators] = useState(createDraftIndicators);
  // The marks of an edited prompt follow the indicators by themselves: a keystroke does not render App.
  const tabDrafts = useMemo(() => ({ indicators: draftIndicators, selectedId: sessionId }), [draftIndicators, sessionId]);
  const [nextSendSelections] = useState(() => createNextSendSelectionStore(
    key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value)));
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
  const [creatingVisible, writeCreatingVisible] = useState(false);
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
    if (searchOpen || dialog || settingsVisible.current || modalDialogOpen()) return;
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
      () => !!modalDialogOpen());
  }
  useEffect(() => {
    creationAlive.current = true;
    creationRefresh.current = new AbortController();
    // Child-owned native dialogs (Info, Details, expanded editor) also end the original
    // presentation lifetime, even if opened and closed before the create reply arrives.
    const modalTransition = (event: Event) => {
      if (!(event.target instanceof HTMLDialogElement)) return;
      invalidateCreation(event.target.classList.contains("global-search"));
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
  const sessionRail = useRef<HTMLElement>(null);
  const sessionInfoTrigger = useRef<HTMLButtonElement>(null);
  const remindersTrigger = useRef<HTMLButtonElement>(null);
  const remindersOrigin = useRef<{ element: HTMLButtonElement; lifetime: SessionInfoLifetime } | null>(null);
  const compactTrigger = useRef<HTMLButtonElement>(null);
  const timelineCommand = useRef<TimelineCommand | null>(null);
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
  const [clock, setClock] = useState(Date.now);

  // The start-up screen stays until the window has something to show in its place: the host's answer and the
  // workspace, or the reason there is none. It never stays longer than a few seconds.
  useEffect(() => {
    if (error || status && workspaceState.kind !== "loading") dismissStartupScreen();
  }, [status, workspaceState.kind, error]);
  useEffect(() => {
    const timer = window.setTimeout(dismissStartupScreen, 8_000);
    return () => window.clearTimeout(timer);
  }, []);

  useEffect(() => {
    const timer = window.setInterval(() => setClock(Date.now()), 60_000);
    return () => window.clearInterval(timer);
  }, []);

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
    if (focusVisibleProject(projectRail.current)) focusProjectPending.current = false;
  }, [railVisible, view, workspaceState.kind]);

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
  // The spaces are read once the host answered, and again each time it says they changed.
  const spacesEpoch = status?.hostAvailable ? status.hostEpoch ?? null : null;
  useEffect(() => {
    if (spacesEpoch) return spacesHub.connect(spacesEpoch);
    if (hostAnswered) spacesHub.unavailable();
    return undefined;
  }, [spacesHub, spacesEpoch, hostAnswered]);
  // The host is told which space the window shows: the commands of the sessions read it as their current space.
  useEffect(() => { if (spacesEpoch && spacesState.available) spacesHub.shown(spaceId); }, [spacesHub, spacesEpoch, spacesState.available, spaceId]);
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
  const projectListing = snapshot ? projectRailProjection(snapshot, projectSort, projectTree.favorites) : null;
  // The projects of the shown space changed without a read of the catalog (a project joined it, here or through
  // a command): the window is given the catalog again as that space now has it.
  useEffect(() => {
    const full = catalog.current;
    if (!full || workspaceState.kind !== "ready" || !spacesState.loaded) return;
    const members = spaceMembers(spacesState.spaces, shownSpace.current);
    if (sameMembers(members, scopedMembers.current)) return;
    scopedMembers.current = members;
    publishWorkspaceState({ kind: "ready", snapshot: scopeSnapshot(full, members) });
  }, [spacesState.spaces, spacesState.loaded, workspaceState.kind]);
  // A space that is gone (removed here, by a command or by another instance) gives its place to the default one,
  // and the tabs the window kept for it are forgotten.
  useEffect(() => {
    if (!spacesState.loaded || !spacesState.available) return;
    if (tabsReady && !spacesState.spaces.some(space => space.id === spaceId)) showSpace(defaultSpaceId, undefined, true);
    try {
      for (const base of [sessionTabsKey, fileTabsKey]) {
        for (const key of Object.keys(localStorage)) {
          if (key.startsWith(`${base}.`) && !spacesState.spaces.some(space => spaceStorageKey(base, space.id) === key)) localStorage.removeItem(key);
        }
      }
    } catch { /* Storage that cannot be read keeps what it has. */ }
    for (const id of [...spaceTabs.current.keys()]) if (!spacesState.spaces.some(space => space.id === id)) { spaceTabs.current.delete(id); spaceLayouts.current.delete(id); void canvasHub.closeSpace(id); }
  }, [spacesState.loaded, spacesState.available, spacesState.spaces, spaceId, tabsReady]);
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
    if (tabsReady && snapshot) persistSessionTabs(value => localStorage.setItem(spaceStorageKey(sessionTabsKey, spaceId), value), tabs);
  }, [tabs, tabsReady, snapshot, spaceId]);
  useEffect(() => {
    if (tabsReady && snapshot) persistFileTabs(value => localStorage.setItem(spaceStorageKey(fileTabsKey, spaceId), value), fileTabs);
  }, [fileTabs, tabsReady, snapshot, spaceId]);

  // A scope that becomes the selected one is opened. At the start what was remembered is kept as it is; with
  // nothing remembered the selected scope is the one open, as it was before the Explorer remembered anything.
  const treeScope = useRef<{ id: string | null } | null>(null);
  useEffect(() => {
    if (!tabsReady) return;
    const seen = treeScope.current;
    treeScope.current = { id: projectId };
    // The chats stay closed until the user opens them: a start with nothing selected does not open them.
    if (seen ? seen.id !== projectId : !storedProjectTree && projectId !== null) setProjectTree(current => expandScope(current, projectId));
  }, [projectId, tabsReady]);

  function applyTabState(next: SessionTabsState) {
    setTabs(next);
    const target = next.active;
    setMenuTarget(null);
    if (target && selectedScope.current !== target.projectId) selectProject(target.projectId, target.sessionId);
    else { selectedSessionId.current = target?.sessionId ?? null; setSessionId(target?.sessionId ?? null); }
  }
  function selectSessionTab(tab: SessionTab) {
    if (!snapshot || snapshot !== currentSnapshot.current || !resolveSessionTab(snapshot, tab)) return;
    applyTabState(openSessionTab(reconcileSessionTabs(tabs, snapshot), tab));
  }
  // What each space had open when the window left it: its tabs come back with it, and the dock of each space
  // keeps where its panes were. The panes of a space that is not shown are not in the page at all.
  // A target can carry what is then shown of its project in the space: its code editor, its changes.
  type SpaceTarget = Readonly<{ projectId: string | null; sessionId: string | null; then?: () => void }>;
  const spaceTabs = useRef(new Map<string, Readonly<{ tabs: SessionTabsState; files: FileTabs; projectId: string | null }>>());
  const spaceLayouts = useRef(new Map<string, ReturnType<typeof createSessionTabModel>>());
  function spaceLayout(id: string) {
    let layout = spaceLayouts.current.get(id);
    if (!layout) { layout = createSessionTabModel(); spaceLayouts.current.set(id, layout); }
    return layout;
  }
  // Leaving a space takes its code editors out of the page: while files hold unsaved edits, the window asks first.
  const [leavingSpace, setLeavingSpace] = useState<{ spaceId: string; target?: SpaceTarget; tabs: FileTab[]; busy: boolean } | null>(null);
  const [spaceMenuRequest, setSpaceMenuRequest] = useState(0);
  const [spaceDialog, setSpaceDialog] = useState(false);
  /**
   * Shows a space: its projects in the Explorer, and the tabs it had. With a target, that project or session is
   * then selected. False when the space is not shown (the catalog is not read yet, or files hold unsaved edits
   * and the window asks what to do with them).
   */
  function showSpace(id: string, target?: SpaceTarget, discard = false): boolean {
    const known = spacesHub.getSnapshot().spaces;
    const next = findSpace(known, id).id;
    if (next === shownSpace.current) {
      if (target) { selectProject(target.projectId, target.sessionId); target.then?.(); }
      return true;
    }
    const full = catalog.current;
    if (!full || !tabsReady) return false;
    const unsaved = fileTabs.open.filter(tab => fileEditors.dirty(fileTabKey(tab)));
    if (unsaved.length > 0 && !discard) { setLeavingSpace({ spaceId: next, target, tabs: unsaved, busy: false }); return false; }
    setLeavingSpace(null);
    spaceTabs.current.set(shownSpace.current, { tabs, files: fileTabs, projectId });
    shownSpace.current = next;
    writeSpaceId(next);
    persistShownSpace(value => localStorage.setItem(shownSpaceKey, value), next);
    const members = spaceMembers(known, next);
    scopedMembers.current = members;
    const scoped = scopeSnapshot(full, members);
    publishWorkspaceState({ kind: "ready", snapshot: scoped });
    const kept = spaceTabs.current.get(next);
    const entering = reconcileSessionTabs(kept?.tabs ?? restoreSessionTabs(() => localStorage.getItem(spaceStorageKey(sessionTabsKey, next))) ?? emptySessionTabs(), scoped);
    const files = reconcileTerminalTabs(reconcileFileTabs(kept?.files ?? restoreFileTabs(() => localStorage.getItem(spaceStorageKey(fileTabsKey, next))) ?? emptyFileTabs(), scoped),
      new Set(terminalList.map(terminal => terminal.id)));
    closedTabKinds.current = [];
    if (target) { setTabs(entering); selectProject(target.projectId, target.sessionId); }
    else if (entering.active) applyTabState(entering);
    else {
      setTabs(entering);
      // The project the space was left on, else its first one, else the chats.
      const project = scoped.projects.find(value => value.id === kept?.projectId && !value.archived) ?? scoped.projects.find(value => !value.archived);
      selectProject(project?.id ?? null);
    }
    // After the selection above, which leaves any file: the tab that was in front of the space stays in front.
    setFileTabs(target ? activateFileTab(files, null) : files);
    target?.then?.();
    return true;
  }
  const showSpaceLatest = useRef(showSpace); showSpaceLatest.current = showSpace;
  async function saveAndLeaveSpace(leaving: NonNullable<typeof leavingSpace>) {
    setLeavingSpace({ ...leaving, busy: true });
    for (const tab of leaving.tabs) {
      if (await fileEditors.save(fileTabKey(tab))) continue;
      if (creationAlive.current) { setLeavingSpace(null); activateFile(tab); }
      return;
    }
    if (creationAlive.current) showSpace(leaving.spaceId, leaving.target, true);
  }
  // A session of a project that the shown space does not have: the default space, which has every project, is shown first.
  function revealSession(session: WorkspaceSession) {
    const target = { projectId: session.scopeKind === "project" ? session.projectId : null, sessionId: session.id };
    if (currentSnapshot.current?.sessions.some(candidate => candidate.id === session.id)) selectProject(target.projectId, target.sessionId);
    else showSpace(defaultSpaceId, target);
  }
  // Shows a space on one of its sessions: the one that waits for the user there. A session that just started is read first.
  async function openSpaceSession(id: string, target: SpaceTarget) {
    if (target.sessionId && !catalog.current?.sessions.some(session => session.id === target.sessionId)) {
      try {
        const fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 });
        if (!creationAlive.current) return;
        if (fresh.configured) publishWorkspaceState({ kind: "ready", snapshot: fresh });
      } catch { /* The space is shown all the same: its Explorer has the session once it is listed. */ }
    }
    showSpace(id, catalog.current?.sessions.some(session => session.id === target.sessionId) ? target : undefined);
    focusPromptSoon();
  }
  // A project that another space has joins the one shown, and is selected there.
  async function joinShownSpace(id: string) {
    const outcome = await spacesHub.assign(id, [shownSpace.current]);
    if (!outcome.ok || !creationAlive.current) return;
    const fresh = await refreshProjects(creationRefresh.current.signal);
    if (fresh?.projects.some(project => project.id === id)) { selectProject(id); focusPromptSoon(); }
  }
  // What reaches the window from elsewhere: a command asks for a space, or changed the spaces or the projects.
  const spaceRequest = useRef<(kind: "show" | "changed", id: string | null) => void>(() => { });
  spaceRequest.current = (kind, id) => { if (kind === "show" && id) showSpace(id); else readSessionList.current(); };
  useEffect(() => { spacesHub.onRequest((kind, id) => spaceRequest.current(kind, id)); }, [spacesHub]);
  // The sessions that need the user in a space that is not shown: listed at the foot of the Explorer, and said
  // once, when a session starts to wait, in a message that leads to it.
  const calls = useMemo(() => spaceCalls(spacesState.spaces, spaceId, spacesState.sessions), [spacesState.spaces, spacesState.sessions, spaceId]);
  const saidCalls = useRef<{ spaceId: string; sessions: ReadonlySet<string> } | null>(null);
  useEffect(() => {
    const said = saidCalls.current;
    const waiting = calls.filter(call => call.waiting);
    saidCalls.current = { spaceId, sessions: new Set(waiting.map(call => call.sessionId)) };
    // What waited when the window started, or when it left a space, is not news.
    if (!said || said.spaceId !== spaceId) return;
    for (const call of waiting) {
      if (said.sessions.has(call.sessionId)) continue;
      showToast({ intent: "warning", icon: "help", timeout: 12_000,
        message: translate(shownLocale.current, "{title} waits for you in {space}", { title: call.title || translate(shownLocale.current, "A session"), space: call.space.name }),
        action: { text: translate(shownLocale.current, "Show"), onClick: () => void openSpaceSession(call.space.id, call) } });
    }
  }, [calls, spaceId]);
  // The sessions that wait for the user (a question, a command to allow, a form): marked in the Explorer and on their
  // tab. One of the space that is shown is also said once, when it starts to wait while another session is on screen.
  const waitingSessions = useMemo(() => new Set(spacesState.sessions.filter(session => session.waiting).map(session => session.sessionId)), [spacesState.sessions]);
  const saidWaiting = useRef<ReadonlySet<string> | null>(null);
  useEffect(() => {
    const said = saidWaiting.current;
    saidWaiting.current = waitingSessions;
    // What waited when the window started is not news.
    if (!said) return;
    for (const session of spacesState.sessions) {
      if (!session.waiting || said.has(session.sessionId) || session.sessionId === sessionId
        || !spaceShows(spacesState.spaces, spaceId, session.projectId)) continue;
      showToast({ intent: "warning", icon: "help", timeout: 12_000,
        message: translate(shownLocale.current, "{title} waits for you", { title: session.title || translate(shownLocale.current, "A session") }),
        action: { text: translate(shownLocale.current, "Show"), onClick: () => { selectProject(session.projectId, session.sessionId); focusPromptSoon(); } } });
    }
  }, [waitingSessions]);
  function activateFile(tab: FileTab | null) { setFileTabs(state => activateFileTab(state, tab)); }
  function openFile(tab: FileTab) {
    // At the tab limit a file with unsaved edits is never the one that makes room.
    setFileTabs(state => openFileTab(state, tab, value => fileEditors.dirty(fileTabKey(value))));
  }
  // Opens the changes of a project's repository in their tab, beside the tab that is shown: the ones of the
  // folder of the project, or of the worktree a session works in.
  function showChanges(project: Readonly<{ id: string; path: string }>, path: string | null = null, worktree?: string | null) {
    if (path !== null || worktree !== undefined) setChangeRequests(current => new Map(current).set(project.id, { path, worktree }));
    openFile(changesTab(project));
  }
  const showChangesLatest = useRef(showChanges); showChangesLatest.current = showChanges;
  // Opens the code editor of a project in its tab: on a file, at a place in it, or with the files of the project.
  function openEditor(project: Readonly<{ id: string; path: string }>, request: EditorRequest) {
    setEditorRequests(current => new Map(current).set(project.id, request));
    openFile(editorTab(project));
  }
  const openEditorLatest = useRef(openEditor); openEditorLatest.current = openEditor;
  // The code editor on the folder of a source plugin: from Settings, or asked by an agent with `alta plugin open`.
  function openPluginEditor(folder: Readonly<{ id: string; path: string; name: string }>, request: EditorRequest) {
    setEditorRequests(current => new Map(current).set(folder.id, request));
    openFile(pluginEditorTab(folder));
  }
  const openPluginEditorLatest = useRef(openPluginEditor); openPluginEditorLatest.current = openPluginEditor;
  // The code editor on a folder of the disk that is no project: the folder of a file that a link named.
  function openDiskEditor(folder: Readonly<{ id: string; path: string; name: string }>, request: EditorRequest) {
    setEditorRequests(current => new Map(current).set(folder.id, request));
    openFile(diskEditorTab(folder));
  }
  const openDiskEditorLatest = useRef(openDiskEditor); openDiskEditorLatest.current = openDiskEditor;
  // The code editor on the folder of a skill, from Settings: its files, with its SKILL.md shown.
  function openSkillEditor(folder: Readonly<{ id: string; path: string; name: string }>) {
    setEditorRequests(current => new Map(current).set(folder.id, { path: "SKILL.md", line: null, column: null, explorer: true }));
    openFile(skillEditorTab(folder));
  }
  // What an agent or a link asks to show of a project: at once when the shown space has the project. For a project
  // of another space the window stays where the user is, and a message offers that space, where it is then shown.
  function showInSpace(kind: "editor" | "changes", id: string, show: (project: Readonly<{ id: string; path: string }>) => void) {
    const place = () => placeProject(currentSnapshot.current?.projects ?? [], catalog.current?.projects ?? [], spacesHub.getSnapshot().spaces, shownSpace.current, id);
    const asked = place();
    if (!asked) return;
    if (!asked.space) { show(asked.project); return; }
    showToast({ intent: "primary", icon: "info-sign", timeout: 12_000,
      message: translate(shownLocale.current, kind === "editor" ? "The editor of {project} opens in {space}" : "The changes of {project} open in {space}",
        { project: asked.project.name, space: asked.space.name }),
      action: { text: translate(shownLocale.current, "Show"), onClick: () => {
        // Where the project is by now: the user may have shown its space, or taken the project out of it.
        const found = place();
        if (found?.space) showSpaceLatest.current(found.space.id, { projectId: id, sessionId: null, then: () => show(found.project) });
        else if (found) show(found.project);
      } } }, `space:${kind}:${id}`);
  }
  const showInSpaceLatest = useRef(showInSpace); showInSpaceLatest.current = showInSpace;
  // An agent asks for the editor of a project with `alta editor open`.
  useEffect(() => {
    const epoch = status?.hostEpoch;
    if (!epoch) return;
    const abort = new AbortController();
    void (async () => {
      try {
        for await (const request of await projectFiles.watch({ expectedEpoch: epoch }, { signal: abort.signal })) {
          if (abort.signal.aborted) return;
          const asked = { path: request.path, line: request.line, column: request.column, explorer: request.path === null ? true : null };
          if (request.projectId.startsWith(pluginFolderPrefix)) {
            if (request.name && request.root) openPluginEditorLatest.current({ id: request.projectId, path: request.root, name: request.name }, asked);
            continue;
          }
          // A link named a file that no project has: the code editor opens on its folder.
          if (request.projectId.startsWith(diskFolderPrefix)) {
            if (request.name && request.root) openDiskEditorLatest.current({ id: request.projectId, path: request.root, name: request.name }, asked);
            continue;
          }
          showInSpaceLatest.current("editor", request.projectId, project => openEditorLatest.current(project, asked));
        }
      } catch { /* The bridge is gone: the editor still opens from the window. */ }
    })();
    return () => abort.abort();
  }, [status?.hostEpoch]);
  const [showProjectChanges] = useState(() => (project: Readonly<{ id: string; path: string }>, worktree?: string | null) =>
    showChangesLatest.current(project, null, worktree));
  const [refreshSessionList] = useState(() => () => readSessionList.current());
  // An agent asks for the changes of a project with `alta diff show`.
  useEffect(() => {
    const epoch = status?.hostEpoch;
    if (!epoch) return;
    const abort = new AbortController();
    void (async () => {
      try {
        for await (const request of await projectGit.watch({ expectedEpoch: epoch }, { signal: abort.signal })) {
          if (abort.signal.aborted) return;
          showInSpaceLatest.current("changes", request.projectId, project => showChangesLatest.current(project, request.path, request.worktree ?? null));
        }
      } catch { /* The bridge is gone: the changes still open from the composer. */ }
    })();
    return () => abort.abort();
  }, [status?.hostEpoch]);
  // The canvases that plugins provide. The status each gave its tab is shown beside its title; the instance each tab shows is
  // kept so that closing the tab closes it (a tab that is only taken out of the page, with its space, stays open).
  const [canvasStatuses, setCanvasStatuses] = useState<ReadonlyMap<string, string>>(() => new Map());
  const canvasTabsOf = useRef<(space: string) => readonly FileTab[]>(() => []);
  canvasTabsOf.current = space => space === shownSpace.current ? fileTabs.open : spaceTabs.current.get(space)?.files.open ?? [];
  const [canvasInstances] = useState(() => createCanvasInstances(canvasHub, space => canvasTabsOf.current(space)));
  const changeCanvasLook = useCallback((tab: FileTab, space: string, look: Readonly<{ title?: string; status?: string | null; icon?: string; plugin?: string }>) => {
    if (look.title !== undefined || look.icon !== undefined || look.plugin !== undefined) setFileTabs(state => refreshCanvasTab(state, tab, { title: look.title, icon: look.icon, plugin: look.plugin }));
    // The same canvas in another space has an instance of its own: the status is the one of the tab of this space.
    if (look.status !== undefined) setCanvasStatuses(current => withCanvasStatus(current, tab, space, look.status ?? null));
  }, []);
  // What a tab that shows a plugin that is not running can do: its folder is known from the tab.
  const canvasControls = useRef(new Map<string, CanvasPluginControl | null>());
  const canvasControl = (tab: FileTab) => {
    const key = `${pluginEpoch}\n${tab.plugin ?? ""}`;
    if (!canvasControls.current.has(key)) canvasControls.current.set(key, createCanvasPluginControl(pluginsApi, pluginEpoch, tab.plugin));
    return canvasControls.current.get(key) ?? null;
  };
  // A plugin asks for a tab: it opens in the space the window shows, or joins the tabs of the space it names without moving the window.
  function openCanvasRequest(request: CanvasOpenRequest) {
    const full = catalog.current ?? currentSnapshot.current;
    if (!full) return;
    const session = request.sessionId ? full.sessions.find(candidate => candidate.id === request.sessionId) : undefined;
    const projectId = request.projectId ?? (session?.scopeKind === "project" ? session.projectId : null);
    const project = projectId ? full.projects.find(candidate => candidate.id === projectId && !candidate.archived) : undefined;
    if (projectId && !project) return;
    // The space the plugin names when the window has it and it shows the project; else the space that is shown, when it does.
    const space = canvasRequestSpace(spacesHub.getSnapshot().spaces, request.spaceId, shownSpace.current, projectId);
    if (!space) return;
    const tab = canvasTab({ pluginKey: request.pluginKey, canvasId: request.canvasId, project: project ? { id: project.id, path: project.path } : null, sessionId: request.sessionId, key: request.key },
      { title: request.title, icon: request.icon, plugin: request.plugin });
    const keep = (value: FileTab) => fileEditors.dirty(fileTabKey(value));
    if (space === shownSpace.current) { setFileTabs(state => bringCanvasTab(state, tab, request.focus, keep)); return; }
    // Another space: its tabs, in memory when the window left it in this run and in storage otherwise. The window stays where it is.
    const kept = spaceTabs.current.get(space);
    const next = addCanvasTabToSpace({ kept: kept?.files, read: () => localStorage.getItem(spaceStorageKey(fileTabsKey, space)),
      write: value => localStorage.setItem(spaceStorageKey(fileTabsKey, space), value) }, tab, request.focus, keep);
    if (kept) spaceTabs.current.set(space, { ...kept, files: next });
  }
  // A plugin closed an instance that no tab listened to: its tab goes with it, wherever it is. The tab of a space that is not shown is
  // taken out of the tabs of that space, in memory when the window left it in this run and in storage otherwise.
  function closeCanvasRequest(closed: CanvasClosedInstance) {
    const tab = closedCanvasTab(closed), space = closed.spaceId ?? shownSpace.current;
    if (space !== shownSpace.current) {
      const kept = spaceTabs.current.get(space);
      const next = removeCanvasTabFromSpace({ kept: kept?.files, read: () => localStorage.getItem(spaceStorageKey(fileTabsKey, space)),
        write: value => localStorage.setItem(spaceStorageKey(fileTabsKey, space), value) }, tab);
      if (kept && next) spaceTabs.current.set(space, { ...kept, files: next });
      return;
    }
    // In the space that is shown the tab asked for its instance and has no answer yet, or has not asked.
    const open = fileTabs.open.find(value => sameFileTab(value, tab));
    if (open) closeFile(open, true);
    // The tabs of a space that was just shown are not drawn yet: the tab leaves them all the same.
    else setFileTabs(state => { const late = state.open.find(value => sameFileTab(value, tab)); return late ? closeFileTab(state, late) : state; });
  }
  // The canvases the plugins declare now, read again whenever the plugins change.
  const canvasCatalog = useSyncExternalStore(canvasHub.subscribeCatalog, canvasHub.getCatalog);
  // Opens the tab of a canvas in the space that is shown: one tab for each canvas, project, session and key.
  function openCanvas(item: CanvasItem, target: CanvasTarget) {
    if (!owned) return;
    openFile(canvasTabOf(item, target));
  }
  // What the menu of a session row offers of the plugins: their canvases about a session, a few lines and then the page of the canvases.
  function sessionCanvasEntries(session: WorkspaceSession): SessionMenuEntry[] {
    const menu = owned ? canvasMenuItems(canvasCatalog, "Session") : null;
    if (!menu || menu.items.length === 0) return [];
    const project = selectedProject && !selectedProject.archived && session.scopeKind === "project" && session.projectId === selectedProject.id ? { id: selectedProject.id, path: selectedProject.path } : null;
    return [{ key: "canvases", divider: true },
      ...menu.items.map(item => ({ key: `canvas:${item.pluginKey}/${item.id}`, label: t("Open {title}", { title: item.title }), icon: "canvases" as const,
        onSelect: () => { dismissSessionMenu(false); openCanvas(item, { project, sessionId: session.id }); } })),
      ...(menu.more ? [{ key: "canvases-more", label: t("More…"), icon: "canvases" as const, onSelect: () => { dismissSessionMenu(false); openFile(canvasesTab); } }] : [])];
  }
  // Opens a canvas for what is selected: the search, the palette and the menus of rows do not ask which project.
  function openCanvasHere(item: CanvasItem) {
    const target = defaultCanvasTarget(item, canvasSelection);
    if (target) openCanvas(item, target);
  }
  // Asks an agent for a canvas: the new-session prompt of the project in front holds the request, which the user completes and sends.
  function newCanvas() {
    const project = canvasSelection.project;
    if (!owned || !project) return;
    const scope = `local-draft:${JSON.stringify(project.id)}`, key = `codealta.desktop.localPrompt.${JSON.stringify(project.id)}`;
    // What is already written there belongs to the user: it is shown, not replaced.
    if (!localDrafts.get(scope, () => restoreDraft(() => localStorage.getItem(key), scope)).text.trim()) {
      localDrafts.edit(scope, newCanvasPrompt);
      persistDraft((_key, value) => localStorage.setItem(key, value), () => localStorage.removeItem(key), scope, newCanvasPrompt);
    }
    selectProject(project.id);
    focusPromptSoon();
  }
  // The sessions a canvas of a session can be opened for: the ones of the space that is shown, the ones used last first.
  const canvasSessions = useMemo(() => [...(snapshot?.sessions ?? [])].sort((left, right) => Date.parse(right.updatedAt) - Date.parse(left.updatedAt)).slice(0, 200)
    .map(session => { const project = session.scopeKind === "project" ? snapshot?.projects.find(value => value.id === session.projectId && !value.archived) : undefined;
      return { id: session.id, title: plainTitle(session.title), project: project ? { id: project.id, path: project.path } : null, projectName: project?.name ?? null }; }), [snapshot]);
  const canvasProjects = useMemo(() => (snapshot?.projects ?? []).filter(value => !value.archived).map(value => ({ id: value.id, path: value.path, name: value.name })), [snapshot]);
  const openCanvasLatest = useRef(openCanvasRequest); openCanvasLatest.current = openCanvasRequest;
  const closeCanvasLatest = useRef(closeCanvasRequest); closeCanvasLatest.current = closeCanvasRequest;
  // What the script of a plugin asks of the shell (`alta.host`): another canvas, and the changes of a project.
  const pluginHostBridge = useMemo<PluginHostBridge>(() => ({
    openCanvas: request => {
      const declared = canvasHub.getCatalog().find(item => item.pluginKey === request.pluginKey && item.id === request.canvasId);
      if (!declared) return;
      openCanvasLatest.current({ ...request, spaceId: shownSpace.current, plugin: declared.package, focus: true, title: declared.title, icon: declared.icon });
    },
    showChanges: projectId => {
      const project = projectId ? (catalog.current ?? currentSnapshot.current)?.projects.find(candidate => candidate.id === projectId && !candidate.archived) : undefined;
      if (project) showChangesLatest.current({ id: project.id, path: project.path });
    },
  }), [canvasHub]);
  useEffect(() => {
    if (!tabsReady) return;
    canvasHub.onOpenRequest(request => openCanvasLatest.current(request), closed => closeCanvasLatest.current(closed));
    return () => canvasHub.onOpenRequest(null);
  }, [canvasHub, tabsReady]);
  function closeFile(tab: FileTab, discard = false) {
    if (!discard && fileEditors.dirty(fileTabKey(tab))) { activateFile(tab); setFileClosing({ tab, busy: false }); return; }
    setFileClosing(null);
    // What was asked of a closed editor is not asked again when it is reopened.
    if (isEditorTab(tab)) setEditorRequests(current => { const next = new Map(current); next.delete(tab.projectId); return next; });
    if (isCanvasTab(tab)) {
      // The tab is closed, not only taken out of the page: the instance closes and the plugin lets go of what it held for it.
      const space = shownSpace.current;
      canvasInstances.closed(tab, space);
      setCanvasStatuses(current => withCanvasStatus(current, tab, space, null));
    }
    closedTabKinds.current = [...closedTabKinds.current, "file" as const].slice(-64);
    tabFocusPending.current = true;
    setFileTabs(state => closeFileTab(state, tab));
  }
  // Exit asks first while files hold unsaved edits; saving stops at the first file that could not be saved.
  const [exiting, setExiting] = useState<{ tabs: FileTab[]; busy: boolean } | null>(null);
  function exitApplication() {
    // An exit asked for anywhere answers the question about the closed window.
    setCloseQuestion(false);
    const unsaved = fileTabs.open.filter(tab => fileEditors.dirty(fileTabKey(tab)));
    if (unsaved.length === 0) { quitApplication(); return; }
    setExiting(current => current?.busy ? current : { tabs: unsaved, busy: false });
  }
  const requestExit = useRef(exitApplication);
  requestExit.current = exitApplication;
  async function saveAllAndExit(unsaved: FileTab[]) {
    setExiting({ tabs: unsaved, busy: true });
    for (const tab of unsaved) {
      if (await fileEditors.save(fileTabKey(tab))) continue;
      if (creationAlive.current) { setExiting(null); activateFile(tab); }
      return;
    }
    setExiting(null);
    quitApplication();
  }
  // The host exits the application; while sessions run it first has this page ask (see the shell's notices).
  function quitApplication(confirmed = false) {
    void desktopShell.exit({ confirmed }, { timeoutMilliseconds: 15_000 })
      .then(reply => { if (reply.status !== "ok") closeApplicationWindow(); }, closeApplicationWindow);
  }
  // How the application lives beyond its window: whether closing it leaves CodeAlta running, and the host's
  // requests to exit (Exit in the tray) or to ask before an exit that stops running sessions.
  const [shellPreferences, setShellPreferences] = useState<DesktopShellPreferences | null>(null);
  // The width of the conversations: the user's setting, and the sessions an alta command gave a width of their own.
  const [sessionWidth, setSessionWidthValue] = useState(defaultSessionWidth);
  const [sessionWidths, setSessionWidths] = useState<ReadonlyMap<string, number>>(() => new Map());
  useLayoutEffect(() => { applySessionWidth(document.documentElement, sessionWidth); }, [sessionWidth]);
  const [exitQuestionFor, setExitQuestionFor] = useState<Readonly<{ sessions: number; terminals: number }> | null>(null);
  // Closing the window asks, until the answer is remembered, whether CodeAlta keeps running behind its icon.
  // A question about an exit already on screen is answered first.
  const [closeQuestion, setCloseQuestion] = useState(false);
  const exitPending = useRef(false);
  exitPending.current = exiting !== null || exitQuestionFor !== null;
  // A look for a newer version at the start, as the terminal application does, then again as time passes: the
  // application stays open, or in the notification area, for days. A newer version is announced once, with the
  // command that installs it; Settings > About keeps the result.
  const [appUpdateResult, setAppUpdateResult] = useState<AppUpdateResponse | null>(null);
  const announcedUpdate = useRef<string | null>(null);
  const updateChecked = useRef(false);
  // Update and restart: the host hands the update to a helper and exits as it does for Exit. An exit the
  // user cancels (unsaved files, running sessions) calls the update off.
  const updatePending = useRef(false);
  function installUpdate() {
    if (updatePending.current) return;
    updatePending.current = true;
    void appUpdate.install({}, { timeoutMilliseconds: 30_000 }).then(reply => reply.status === "started", () => false).then(started => {
      if (started) return;
      updatePending.current = false;
      showToast({ intent: "danger", icon: "error", timeout: 12_000, message: translate(shownLocale.current, "The update could not be started. Run the command in a terminal.") });
    });
  }
  function exitCanceled() {
    if (!updatePending.current) return;
    updatePending.current = false;
    void appUpdate.cancelInstallation({}, { timeoutMilliseconds: 15_000 }).catch(() => { /* The helper gives up by itself after its wait. */ });
  }
  function openReleaseNotes() { void appUpdate.openReleaseNotes({}, { timeoutMilliseconds: 15_000 }).catch(() => { /* The address is in the toast's command line. */ }); }
  // Asks the host, which looks at nuget.org again only when its last look is old enough; `refresh` is the
  // About page, where a look of a few minutes ago is made again.
  function checkForUpdate(refresh: boolean, signal: AbortSignal) {
    void appUpdate.check({ refresh }, { signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (signal.aborted) return;
      const first = !updateChecked.current;
      updateChecked.current = true;
      setAppUpdateResult(value);
      // What became of the update the previous run started, said once, then what is available now.
      const installed = first ? installedNotice(value) : null;
      if (installed) showToast({ intent: installed.intent, icon: installed.intent === "success" ? "tick" : "error", timeout: 12_000,
        message: translate(shownLocale.current, installed.key, installed.parameters) });
      const available = updateToAnnounce(announcedUpdate.current, availableUpdate(value));
      if (!available) return;
      announcedUpdate.current = available.version;
      showToast({ intent: "primary", icon: "automatic-updates", timeout: 20_000,
        message: <UpdateNotice update={available} locale={shownLocale.current} onOpenReleaseNotes={openReleaseNotes} onInstall={installUpdate} /> });
    }, () => {
      // A later question that gets no answer leaves what the page knows.
      if (!signal.aborted && !updateChecked.current) setAppUpdateResult({ status: "failed", packageId: "CodeAlta", currentVersion: "", latestVersion: null, command: null, releaseNotes: null, canInstall: false, installed: null });
    });
  }
  useEffect(() => {
    if (!status?.hostEpoch) return;
    const abort = new AbortController();
    checkForUpdate(false, abort.signal);
    // Again as time passes, and when the window comes back after a while: a hidden page may run no timer.
    let asked = Date.now();
    const again = () => { if (document.visibilityState === "visible" && Date.now() - asked >= updateCheckInterval) { asked = Date.now(); checkForUpdate(false, abort.signal); } };
    const timer = window.setInterval(again, updateCheckInterval);
    document.addEventListener("visibilitychange", again);
    window.addEventListener("focus", again);
    return () => { abort.abort(); window.clearInterval(timer); document.removeEventListener("visibilitychange", again); window.removeEventListener("focus", again); };
  }, [status?.hostEpoch]);
  // The About page shows the result: it is asked for again when the page opens.
  const aboutOpen = settingsOpen && settingsSection === "about";
  useEffect(() => {
    if (!aboutOpen || !status?.hostEpoch || !updateChecked.current) return;
    const abort = new AbortController();
    checkForUpdate(true, abort.signal);
    return () => abort.abort();
  }, [aboutOpen, status?.hostEpoch]);
  const shownLocale = useRef(language.locale); shownLocale.current = language.locale;
  // Said once, the first time the installed tool is added to the desktop's applications.
  // The notice may come before the platform is known: it then waits for it, as each platform is told differently.
  const entryAnnounced = useRef(false);
  const entryPending = useRef(false);
  const currentPlatform = useRef<string | null>(null);
  const [entryGuide, setEntryGuide] = useState(false);
  function announceEntry(platform: string) {
    if (entryAnnounced.current) return;
    entryAnnounced.current = true;
    if (entryAddedGuide(platform)) setEntryGuide(true);
    else showToast({ message: translate(shownLocale.current, entryAddedNotice(platform)), intent: "success", icon: "tick", timeout: 12_000 });
  }
  useEffect(() => {
    if (!status?.hostEpoch) return;
    const abort = new AbortController();
    void desktopShell.preferences({}, { signal: abort.signal, timeoutMilliseconds: 8_000 })
      .then(value => {
        if (abort.signal.aborted || value.status !== "ok") return;
        setShellPreferences(value);
        if (validSessionWidth(value.sessionWidth)) setSessionWidthValue(value.sessionWidth);
        setSessionWidths(sessionWidthsOf(value.sessionWidths));
        currentPlatform.current = value.platform;
        if (value.entryAdded || entryPending.current) announceEntry(value.platform);
      }, () => { /* No shell: the window is the application. */ });
    void (async () => {
      try {
        for await (const notice of await desktopShell.watch({}, { signal: abort.signal })) {
          if (abort.signal.aborted) return;
          if (notice.kind === "entry-added") { if (currentPlatform.current) announceEntry(currentPlatform.current); else entryPending.current = true; }
          else if (notice.kind === "exit-requested") requestExit.current();
          else if (notice.kind === "confirm-exit") setExitQuestionFor({ sessions: notice.runningSessions, terminals: notice.busyTerminals });
          else if (notice.kind === "confirm-close" && !exitPending.current) setCloseQuestion(true);
          // An application that drives CodeAlta through its MCP server creates sessions without the window asking.
          else if (notice.kind === "sessions-changed") readSessionList.current();
          // The width changed: the user's setting, or the width of one session (an alta command, or a drag that released it).
          else if (notice.kind === "session-width") {
            if (notice.sessionId) setSessionWidths(current => withSessionWidth(current, notice.sessionId!, notice.sessionWidth));
            else if (validSessionWidth(notice.sessionWidth)) setSessionWidthValue(notice.sessionWidth);
          }
          // A plugin was built again, started or stopped while the application runs.
          else if (notice.kind === "plugins-changed") { setPluginRevision(value => value + 1); window.dispatchEvent(new Event(pluginsChangedEvent)); }
        }
      } catch { /* The bridge is gone; the window's own close still works. */ }
    })();
    return () => abort.abort();
  }, [status?.hostEpoch]);
  // The user's setting; the session that was resized, if any, follows it again. A window without a shell keeps it for itself.
  const [setSessionWidth] = useState(() => (value: number, resizedSessionId?: string | null) => {
    if (!validSessionWidth(value)) return;
    setSessionWidthValue(value);
    if (resizedSessionId) setSessionWidths(current => withSessionWidth(current, resizedSessionId, 0));
    void desktopShell.setSessionWidth({ percent: value, sessionId: resizedSessionId ?? null }, { timeoutMilliseconds: 8_000 })
      .catch(() => { /* The window shows what was asked. */ });
  });
  const sessionWidthControl = useMemo<SessionWidthControl>(() => ({ width: sessionWidth, widths: sessionWidths, setWidth: setSessionWidth }), [sessionWidth, sessionWidths, setSessionWidth]);
  function setReviewPermissions(review: boolean) {
    // The requests the sessions show do not follow it: a send started with review on still asks until it ends.
    setShellPreferences(current => current && { ...current, reviewPermissions: review });
    void desktopShell.setReviewPermissions({ review }, { timeoutMilliseconds: 8_000 })
      .then(value => { if (value.status === "ok") setShellPreferences(value); }, () => { /* The setting shows what was asked. */ });
  }
  function setInheritPermissions(inherit: boolean) {
    setShellPreferences(current => current && { ...current, inheritPermissions: inherit });
    void desktopShell.setInheritPermissions({ inherit }, { timeoutMilliseconds: 8_000 })
      .then(value => { if (value.status === "ok") setShellPreferences(value); }, () => { /* The setting shows what was asked. */ });
  }
  function setOnClose(onClose: CloseBehavior) {
    setShellPreferences(current => current && { ...current, onClose });
    void desktopShell.setOnClose({ onClose }, { timeoutMilliseconds: 8_000 })
      .then(value => { if (value.status === "ok") setShellPreferences(value); }, () => { /* The setting shows what was asked. */ });
  }
  // The answers to the question about the closed window; a remembered one is what closing does from then on.
  function keepRunning(remember: boolean) {
    setCloseQuestion(false);
    if (remember) setOnClose("keep");
    void desktopShell.hide({}, { timeoutMilliseconds: 8_000 }).catch(() => { /* The window stays open. */ });
  }
  function exitOnClose(remember: boolean) {
    if (remember) setOnClose("exit");
    exitApplication();
  }
  async function saveAndCloseFile(tab: FileTab) {
    setFileClosing({ tab, busy: true });
    const saved = await fileEditors.save(fileTabKey(tab));
    if (!creationAlive.current) return;
    // A refused save keeps the tab: its editor shows why.
    if (saved) closeFile(tab, true); else setFileClosing(null);
  }
  // The project a file is picked in: the one of the editor or the changes in front, else the selected project.
  function editedProject() {
    const tab = fileTabs.active;
    const shown = tab && snapshot ? resolveFileTab(snapshot, tab) : undefined;
    return owned ? shown ?? (selectedProject && !selectedProject.archived ? selectedProject : null) : null;
  }
  // The files that the search of the window looks through: those of the project in front, searched by the host.
  function searchedFiles() {
    const project = editedProject(), epoch = status?.hostEpoch;
    return project && epoch ? { project, search: (query: string, signal: AbortSignal) => sessionOperations.searchReferences({ expectedEpoch: epoch,
      projectId: project.id, projectPath: project.path, query, sessionId: null }, { signal, timeoutMilliseconds: 8000 }) } : null;
  }
  // "/" in an empty prompt looks for a command.
  function openCommandSearch() { openSearch({ text: "/" }); }
  function openFilePicker() {
    if (dialog || searchOpen || !editedProject() || currentView.current !== "workspace"
      || modalDialogOpen()) return;
    setDialog("file");
  }
  // The code editor of a project with its files shown: `/editor`, the second Ctrl+E, the icon of a project.
  function openProjectEditor(project: Readonly<{ id: string; path: string }> | null = editedProject()) {
    if (!owned || !project || currentView.current !== "workspace") return;
    openEditor(project, { path: null, line: null, column: null, explorer: true });
  }
  // The window of the git worktrees of a project: `/worktree`, and the menu of a project.
  function openWorktrees(project: Readonly<{ id: string; name: string; path: string }> | null = editedProject()) {
    if (!owned || !project || dialog || searchOpen || currentView.current !== "workspace" || modalDialogOpen()) return;
    setDialog("worktrees");
    setWorktreesProject({ id: project.id, name: project.name, path: project.path });
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
      && !modalDialogOpen();
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
  // The reference pickers read their scope from a context. Its value is the same object while what it says is
  // the same: a new one on each render of App sent React through every pane in search of its readers.
  const latestReferenceCapture = useRef(captureReferenceLifetime);
  latestReferenceCapture.current = captureReferenceLifetime;
  const [captureReferencePopup] = useState(() => () => latestReferenceCapture.current());
  const observeReference = useCallback((value: { status: string; epoch: string | null }) => { mutation?.capability.observe(value); }, [mutation?.capability]);
  function openSessionBrowser() {
    if (!currentSnapshot.current || view !== "workspace" || settingsVisible.current || dialog || modalDialogOpen()) return;
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
      // In a code editor the tab that closes is the one of the file shown; the editor closes once it shows none.
      if (fileTabs.active) { if (!isEditorTab(fileTabs.active) || !fileEditors.closeFile(fileTabKey(fileTabs.active))) closeFile(fileTabs.active); }
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
  // After opening a project or creating, switching or closing a tab, typing goes to the prompt now shown.
  // Two frames let the newly active pane render its prompt; a file tab focuses its own editor.
  // The operating system's folder dialog exists where a host owns the window and folders can be added.
  const canPickFolder = () => !demoMode && owned && !!mutation?.capability.canMutate();
  const browseForFolder = (initialDirectory: string | null) => pickFolder(desktopShell.pickFolder, t("Add a project folder"), initialDirectory);
  // "+" adds a folder: it goes straight to the folder dialog. A folder that is already a project is opened;
  // any other is shown in the Open project window, checked and ready to be trusted. Where there is no folder
  // dialog, the window opens so that a path can be typed.
  async function addProjectFolder() {
    if (addingFolder.current) return;
    if (!canPickFolder()) { setDialog("project"); return; }
    addingFolder.current = true;
    const pick = await browseForFolder(null);
    addingFolder.current = false;
    if (pick.status === "canceled" || pick.status === "busy") return;
    // Another window was opened while the folder dialog was up: the pick is dropped.
    if (modalDialogOpen()) return;
    if (pick.status !== "ok") { setDialog("project"); return; }
    const saved = currentSnapshot.current?.projects.find(project => sameFolder(project.path, pick.path));
    if (saved && !projectOpening.getSnapshot()) { selectProject(saved.id); focusPromptSoon(); return; }
    // A folder that is a project of another space joins the one shown.
    const elsewhere = saved || shownSpace.current === defaultSpaceId ? undefined : catalog.current?.projects.find(project => !project.archived && sameFolder(project.path, pick.path));
    if (elsewhere && !projectOpening.getSnapshot()) { void joinShownSpace(elsewhere.id); return; }
    setDialog("project");
    setProjectFolder(pick.path);
  }

  function focusPromptSoon() {
    requestAnimationFrame(() => requestAnimationFrame(() => {
      if (currentView.current !== "workspace" || settingsVisible.current
        || modalDialogOpen() || document.querySelector(".project-editor[data-active='true'], .terminal-panel[data-active='true']")) return;
      const prompt = document.querySelector<HTMLElement>("#session-prompt, #catalog-prompt");
      // A request that waits for the user's permission is what its session shows first: its first choice takes the focus.
      const choice = prompt?.closest(".composer-region")?.querySelector<HTMLElement>(".command-permission-panel button[data-permission-choice]:not(:disabled)");
      if (choice) choice.focus();
      else if (prompt) prompt.focus();
      else document.querySelector<HTMLButtonElement>('.session-tabs [role="tab"][aria-selected="true"], .session-tabs > button')?.focus();
    }));
  }
  useLayoutEffect(() => {
    if (!tabFocusPending.current) return;
    tabFocusPending.current = false;
    if (view === "workspace" && !settingsVisible.current && !fileTabs.active) focusPromptSoon();
  }, [tabs, fileTabs, view]);

  const sessions = snapshot ? sessionsForProject(snapshot, projectId) : [];
  const loadedSessionRows = snapshot ? sessionHierarchy(sessions, snapshot.sessions, projectId) : [];
  const extraSessions = sessionExtras.get(scopeKey(projectId)) ?? 0;
  const listLimits = (count: number, active: string | null) => ({ count, subCount: subAgentCount, active,
    extra: (id: string) => subAgentExtras.get(id) ?? 0, collapsed: (id: string) => isCollapsed(projectTree, id) });
  const selectedList = sessionList(loadedSessionRows, listLimits(recentSessionCount + extraSessions, sessionId));
  const visibleSessions = listedSessions(selectedList.entries).map(row => row.session);
  // The sessions of the open scopes other than the selected one, which has the list above.
  const openScopes = useMemo(() => {
    const scopes = new Map<string, { entries: SessionListEntry[]; more: number }>();
    if (!snapshot) return scopes;
    for (const key of projectTree.expanded) {
      const id = key === globalScope ? null : key;
      if (id === projectId || id !== null && !snapshot.projects.some(project => project.id === id)) continue;
      const all = sessionHierarchy(sessionsForProject(snapshot, id), snapshot.sessions, id);
      const list = sessionList(all, listLimits(recentSessionCount + (sessionExtras.get(key) ?? 0), null));
      scopes.set(key, { entries: list.entries, more: list.hidden });
    }
    return scopes;
  }, [snapshot, projectTree.expanded, projectTree.collapsed, projectId, recentSessionCount, sessionExtras, subAgentCount, subAgentExtras]);
  const autoStatusRefresh = useRef<() => Promise<void> | undefined>(() => undefined);
  autoStatusRefresh.current = () => {
    if (!snapshot || document.visibilityState === "hidden") return;
    const candidates = [...visibleSessions, ...[...openScopes.values()].flatMap(scope => listedSessions(scope.entries).map(row => row.session)),
      ...snapshot.sessions.filter(row => tabs.open.some(tab => tab.sessionId === row.id)), ...snapshot.sessions];
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
  // What a canvas that is opened without a choice is about: the project in front, and the session in front with its project.
  const canvasSelection: CanvasSelection = {
    project: selectedProject && !selectedProject.archived ? { id: selectedProject.id, path: selectedProject.path } : null,
    session: selectedSession ? { id: selectedSession.id, project: selectedSession.scopeKind === "project" && selectedSession.projectId
      ? (catalog.current ?? snapshot)?.projects.filter(value => value.id === selectedSession.projectId && !value.archived).map(value => ({ id: value.id, path: value.path }))[0] ?? null : null } : null,
  };
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
  useEffect(() => { if (menuTarget && !activeMenu) setMenuTarget(null); }, [menuTarget, activeMenu]);
  // What plugins add to the menu of the session row that is open: read for that row.
  const sessionMenuPlugins = usePluginMenuEntries("SessionMenu", activeMenu ? { projectId: activeMenu.projectId, sessionId: activeMenu.id } : null);
  const notice = snapshot ? workspaceNotice(snapshot) : null;
  const owned = !!(status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch);
  // The terminals belong to the application: this window lists them, and shows in tabs those it was asked to.
  // A terminal runs with or without a tab, and its tab goes when it does.
  const [terminalWorkspace] = useState(() => createTerminalWorkspace(address => {
    const epoch = currentHostEpoch.current;
    if (epoch) void terminals.open({ expectedEpoch: epoch, address }).catch(() => { /* The link is not opened. */ });
  }));
  const terminalList = useSyncExternalStore(terminalWorkspace.hub.subscribe, terminalWorkspace.hub.list);
  const [terminalLook, setTerminalLook] = useState(() => restoreTerminalLook(() => localStorage.getItem(terminalLookKey)));
  useEffect(() => { terminalWorkspace.setLook(terminalLook); }, [terminalWorkspace, terminalLook]);
  function changeTerminalLook(look: TerminalLook) {
    setTerminalLook(look);
    persistTerminalLook(value => localStorage.setItem(terminalLookKey, value), look);
  }
  const terminalEpoch = owned ? status?.hostEpoch ?? null : null;
  useEffect(() => terminalEpoch ? terminalWorkspace.hub.connect(terminalEpoch) : undefined, [terminalWorkspace, terminalEpoch]);
  useEffect(() => { setFileTabs(state => reconcileTerminalTabs(state, new Set(terminalList.map(terminal => terminal.id)))); }, [terminalList]);
  // The automations belong to the application too: this window lists them and is told when they change.
  const [automationsHub] = useState(() => createAutomationsHub(automations));
  useEffect(() => terminalEpoch ? automationsHub.connect(terminalEpoch) : undefined, [automationsHub, terminalEpoch]);
  const automationState = useSyncExternalStore(automationsHub.subscribe, automationsHub.getSnapshot);
  // An automation starts a session without the window asking: the sessions are read again when its runs change.
  const automationRuns = automationState.runs.map(run => `${run.id}:${run.status}:${run.sessionId ?? ""}`).join("|");
  useEffect(() => { if (automationRuns) readSessionList.current(); }, [automationRuns]);
  const [automationFocus, setAutomationFocus] = useState<string | null>(null);
  function openAutomations(id: string | null = null) {
    if (id) setAutomationFocus(id);
    openFile(automationsTab);
  }
  // Opens the session of a run. One that was just started is not in the list of sessions yet: the list is read first.
  async function openAutomationSession(id: string) {
    let shown = currentSnapshot.current;
    if (!shown?.sessions.some(session => session.id === id)) {
      try {
        const fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 });
        if (!creationAlive.current || !fresh.configured) return;
        publishWorkspaceState({ kind: "ready", snapshot: fresh });
        shown = fresh;
      } catch { return; }
    }
    const session = shown.sessions.find(candidate => candidate.id === id) ?? catalog.current?.sessions.find(candidate => candidate.id === id);
    if (session) revealSession(session);
  }
  // The tasks agents propose and the plans of the projects: this window lists them and is told when they change.
  const [workHub] = useState(() => createWorkItemsHub(workItemsApi));
  useEffect(() => terminalEpoch ? workHub.connect(terminalEpoch) : undefined, [workHub, terminalEpoch]);
  const workState = useSyncExternalStore(workHub.subscribe, workHub.getSnapshot);
  refreshWorkItems.current = () => void workHub.refresh();
  // The Explorer does not wait for them: they are read a few projects at a time, the ones used last first.
  const workOrder = useMemo(() => snapshot ? readingOrder(snapshot.projects, snapshot.sessions, null) : null, [snapshot]);
  useEffect(() => { if (workOrder) workHub.setProjects(workOrder); }, [workHub, workOrder]);
  const workSessionIds = useMemo(() => new Set(snapshot?.sessions.map(session => session.id) ?? []), [snapshot]);
  // The work items of the projects the window shows: the ones of a project that is in another space are not listed here.
  const work = useMemo(() => {
    const shown = new Set(snapshot?.projects.map(project => project.id) ?? []);
    return workItems(workState.projects.filter(project => shown.has(project.projectId)), workSessionIds);
  }, [workState.projects, workSessionIds, snapshot]);
  // Each answer of a status refresh changes the observations; App follows only which sessions run.
  const observedRunning = useSyncExternalStore(runtimeObservations.subscribe, runtimeObservations.getRunning);
  const runningSessionIds = useMemo(() => new Set((snapshot?.sessions ?? []).filter(session => observedRunning.has(tabKey(
    { projectId: session.scopeKind === "project" ? session.projectId : null, sessionId: session.id, path: session.workspacePath }))).map(session => session.id)), [snapshot, observedRunning]);
  const workByProject = useMemo(() => workCounts(work, runningSessionIds), [work, runningSessionIds]);
  const [workFocus, setWorkFocus] = useState<WorkItemsFocus | null>(null);
  const [workBusy, setWorkBusy] = useState<ReadonlySet<string>>(new Set());
  function openWorkItems(focus: WorkItemsFocus | null = null) {
    if (focus) setWorkFocus(focus);
    openFile(workItemsTab);
  }
  // Starts the work of a task or a plan: in a new session, or in the session that shows it.
  // The providers a new session can run with, and the models of one of them: a work item is started with them.
  const runEpoch = owned ? status?.hostEpoch ?? null : null;
  const runInventory = configurationState.snapshot;
  // What every provider is shown with: the logo of a provider is drawn from its key anywhere in the window.
  const providerLogos = useMemo(() => providerBrands(configurationState.snapshot), [configurationState.snapshot]);
  const runProviders = useMemo<readonly RunProvider[]>(() => runEpoch && runInventory?.providerRuntimeAvailable
    ? runInventory.providers.filter(provider => provider.enabled) : [], [runEpoch, runInventory]);
  const loadRunModels = useMemo<RunModelsLoader | null>(() => !runEpoch ? null : (providerId, signal) =>
    modelCatalog.models({ expectedEpoch: runEpoch, providerId }, { signal, timeoutMilliseconds: 15000 }).then(reply => reply.status === "ok" ? reply.models : []), [runEpoch]);
  async function startWork(item: WorkItem, start: WorkStart, session: WorkStartSession | null, runsWith: RunsWith | null = null) {
    const key = workItemKey(item);
    setWorkBusy(value => new Set(value).add(key));
    try {
      return await startWorkItem({ hub: workHub, composer: (kind, id, text, agent) => askPluginComposer(kind, id, text ?? null, agent ?? null),
        openSession: id => void openAutomationSession(id),
        refuse: (message, detail) => showToast({ message: message ? t(message) : detail ?? t("The work did not start."), intent: "danger", icon: "error", timeout: 8000 }) }, item, start, session, runsWith);
    } finally { setWorkBusy(value => { const next = new Set(value); next.delete(key); return next; }); }
  }
  function showTerminal(terminal: TerminalItem) { openFile(terminalTab(terminal)); }
  const showTerminalLatest = useRef(showTerminal); showTerminalLatest.current = showTerminal;
  const navigateLatest = useRef(navigate); navigateLatest.current = navigate;
  const [openPullRequestSettings] = useState(() => () => navigateLatest.current("pullRequests"));
  // What a message between agents needs of the sessions: the title of the one it names, and a way to open it.
  const openLinkedSession = useRef<(id: string) => void>(() => { });
  // A message can name a session of a project that is in another space: every session of the catalog has its title here.
  const sessionTitles = useMemo(() => new Map((catalog.current?.sessions ?? snapshot?.sessions ?? []).map(session => [session.id.toLowerCase(), session.title])), [snapshot]);
  const sessionLinks = useMemo<SessionLinks>(() => ({ title: id => sessionTitles.get(id.toLowerCase()) ?? null, open: id => openLinkedSession.current(id) }), [sessionTitles]);
  openLinkedSession.current = id => void openAutomationSession(id);
  // A session can ask for the tab of a terminal to be shown.
  useEffect(() => terminalWorkspace.hub.onReveal(id => {
    const asked = terminalWorkspace.hub.list().find(terminal => terminal.id === id);
    if (asked) showTerminalLatest.current(asked);
  }), [terminalWorkspace]);
  // A new terminal, in the folder a session works in when it is opened from one, in the folder of the project otherwise.
  async function createTerminal(terminalProjectId: string | null, terminalSessionId: string | null = null) {
    if (!owned) return;
    const reply = await terminalWorkspace.hub.create(terminalProjectId, terminalSessionId, terminalLook.shellIntegration, terminalLook.shell).catch(() => null);
    if (reply?.status === "ok" && reply.terminal) { showTerminal(reply.terminal); return; }
    const message: MessageKey = reply?.status === "no_shell" ? "No command interpreter was found on this system."
      : reply?.status === "limit" ? "Too many terminals are open. Close one first."
      : reply?.status === "no_folder" || reply?.status === "project_unavailable" ? "The folder of the terminal no longer exists."
      : "The terminal could not be started.";
    showToast({ intent: "danger", icon: "error", timeout: 8000, message: t(message) });
  }
  const createTerminalLatest = useRef(createTerminal); createTerminalLatest.current = createTerminal;
  const [openSessionTerminal] = useState(() => (terminalProjectId: string | null, terminalSessionId: string | null) => void createTerminalLatest.current(terminalProjectId, terminalSessionId));
  // What a new terminal is opened from: the terminal in front, the session in front, or the selected project.
  function terminalOrigin(): Readonly<{ projectId: string | null; sessionId: string | null }> | null {
    if (!owned) return null;
    const tab = fileTabs.active;
    if (tab && isTerminalTab(tab)) {
      const shown = terminalList.find(terminal => terminal.id === tab.terminalId);
      return { projectId: shown?.projectId ?? (tab.projectId || null), sessionId: shown?.sessionId ?? null };
    }
    if (tab) return { projectId: tab.projectId || null, sessionId: null };
    if (selectedProject?.archived) return null;
    return { projectId, sessionId: selectedSession?.id ?? null };
  }
  // A start without any enabled provider opens their settings, as the terminal application does; there the
  // setup guide starts by itself the first time.
  const [providerGuide, setProviderGuide] = useState(false);
  const providerSetupOffered = useRef(false);
  useEffect(() => {
    if (!owned || !status?.providerSetup || providerSetupOffered.current) return;
    providerSetupOffered.current = true;
    setProviderGuide(true);
    navigate("providers");
  }, [owned, status]);
  // The next session of the selected project works where its chip says.
  const draftPlace = selectedProject ? workPlaces.get(selectedProject.id) ?? inProjectFolder : inProjectFolder;
  const draftProjectId = selectedProject && !selectedProject.archived ? selectedProject.id : null;
  const draftPlaceControl = useMemo(() => owned && draftProjectId !== null ? { value: draftPlace, onChange: (place: WorkPlace) => setWorkPlace(draftProjectId, place),
    onOpenSettings: () => navigate("worktrees") } : undefined, [owned, draftProjectId, draftPlace]);
  const draftChrome = useComposerChrome(owned ? status?.hostEpoch ?? null : null, selectedProject, null, owned ? showProjectChanges : null, null, draftPlaceControl);
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
  const projectRenaming = !!projectRenameTarget && projectId === projectRenameTarget.id && currentHostEpoch.current === projectRenameTarget.epoch;
  const currentHostAvailable = useRef(!!status?.hostAvailable);
  currentHostAvailable.current = !!status?.hostAvailable;
  const messageLinkEpoch = owned ? status!.hostEpoch! : null;
  // A link of the Markdown that the window shows: the host opens a page of the web in the system browser, and
  // has this window show a file in the code editor (see the requests of `projectFiles.watch`).
  const openMessageLink = useCallback((address: string, scope: MarkdownLinkScope | null) => {
    const current = () => currentHostAvailable.current && currentHostEpoch.current === messageLinkEpoch
      && mutation?.capability.canMutate() === true;
    const file = markdownHrefKind(address) === "file";
    void openMarkdownLink(markdownLinks.open, messageLinkEpoch, address, current, mutation?.capability.observe, scope).then(result => {
      if (result === "ok" || !current()) return;
      const message = result === "binary" ? "This file is not text and cannot be edited here."
        : result === "not_found" ? "The file could not be found." : file ? "The file could not be opened." : "The page could not be opened.";
      showToast({ message: translate(shownLocale.current, message), intent: "danger", icon: "error", timeout: 8000 });
    });
  }, [messageLinkEpoch, mutation?.capability]);
  const localImageKey = JSON.stringify(["local-draft", status?.hostEpoch ?? null, projectId, selectedProject?.path ?? null]);
  const localImages = useLocalDraftImages(submissions.imageDrafts, localImageKey, () => {
    const generation = creationGeneration.current;
    const imageGeneration = localImageGeneration.current;
    const textRevision = localDrafts.peek(draftScope)?.revision;
    const epoch = status?.hostEpoch;
    const scope = projectId;
    const current = () => creationAlive.current && owned && !!snapshot && snapshot.configured
      && mutation?.capability.canMutate() === true && currentHostAvailable.current && currentHostEpoch.current === epoch
      && selectedScope.current === scope && selectedSessionId.current === null && currentProjectWritable()
      && currentView.current === "workspace" && !settingsVisible.current && !creatingBusy
      && !document.querySelector('dialog[open]:not(.expanded-prompt-dialog), [role="dialog"][aria-modal="true"]')
      && localImageGeneration.current === imageGeneration && localDrafts.peek(draftScope)?.revision === textRevision
      && generation === creationGeneration.current;
    return current() ? current : null;
  }, () => { localImageGeneration.current++; }, language.locale);

  // Opens the search of the window: on everything, on a category, or on the sessions of one project.
  function openSearch(start: SearchStart = {}) {
    if (searchOpen || dialog || modalDialogOpen()) return;
    focusRestoration.cancel();
    searchOrigin.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setSearchStart(start);
  }

  function dismissSearch() {
    const origin = searchOrigin.current;
    const originView = currentView.current;
    setSearchStart(null);
    focusRestoration.schedule(origin, () => currentView.current === originView,
      () => !!modalDialogOpen());
  }

  // What was chosen in the search is opened once its modal dialog has closed (see the layout effect below).
  function chooseSearch(choice: SearchChoice) {
    if (choice.kind === "command" && !commandAvailable(choice.id)) return;
    focusRestoration.cancel();
    searchPending.current = choice;
    setSearchStart(null);
  }

  function invokeComposerControl(button: HTMLButtonElement | null | undefined) {
    const shell = workspaceShell.current;
    if (!shell?.isConnected || !button?.isConnected || !shell.contains(button) || button.disabled ||
      button.closest('[inert], [hidden]') || currentView.current !== "workspace" || settingsVisible.current ||
      dialog || modalDialogOpen()) return;
    // Deliberate command invocation only, after the caller's target/lifetime checks.
    // Reveal this workspace's own overflow, not arbitrary ancestors or foreign modals.
    const menu = button.closest<HTMLDetailsElement>('details.composer-actions-menu');
    if (menu && shell.contains(menu)) menu.open = true;
    button.click();
  }

  useLayoutEffect(() => {
    if (searchOpen || !searchPending.current) return;
    const choice = searchPending.current;
    searchPending.current = null;
    // Give focus back to where the search was opened from, so focus-relative commands act on it.
    if (searchOrigin.current?.isConnected) searchOrigin.current.focus();
    switch (choice.kind) {
      case "command": runCommand(choice.id); break;
      // After the search is gone and the focus is back where it was opened from.
      case "plugin": requestAnimationFrame(() => pluginShortcuts.current.run(choice.id)); break;
      // A project is shown in the Explorer with its sessions, and its prompt takes the keyboard.
      case "project": setProjectTree(current => expandScope(current, choice.id)); selectProject(choice.id); focusPromptSoon(); break;
      case "session": selectProject(choice.projectId, choice.id); focusPromptSoon(); break;
      case "file": openEditor(choice.project, { path: choice.path, line: null, column: null, explorer: null }); break;
      case "canvas": { const item = canvasCatalog.find(value => value.pluginKey === choice.pluginKey && value.id === choice.id); if (item) openCanvasHere(item); break; }
    }
  });

  // What plugins contribute for the selected project: their commands and shortcuts, and their prompt pickers.
  const pluginEpoch = owned ? status?.hostEpoch ?? null : null;
  // What plugins ask of the window for their tabs, and push to them, is read from the host that runs them.
  useEffect(() => pluginEpoch ? canvasHub.connect(pluginEpoch) : undefined, [canvasHub, pluginEpoch]);
  const pluginProjectId = projectId ?? null;
  // What the buttons of plugins in the title bar and the rail are asked about: the selected project and session.
  const pluginButtonContext = useMemo<PluginButtonContext>(() => ({ projectId: projectId ?? null, sessionId: sessionId ?? null }), [projectId, sessionId]);
  const [pluginRevision, setPluginRevision] = useState(0);
  useEffect(() => {
    if (!pluginEpoch) { setPluginContributed(noPluginContributions); return; }
    const abort = new AbortController();
    void pluginUi.contributions({ expectedEpoch: pluginEpoch, projectId: pluginProjectId, sessionId: null }, { signal: abort.signal, timeoutMilliseconds: 8000 })
      .then(reply => { if (!abort.signal.aborted) setPluginContributed(pluginContributions(reply, pluginProjectId) ?? noPluginContributions); },
        () => { if (!abort.signal.aborted) setPluginContributed(noPluginContributions); });
    return () => abort.abort();
  }, [pluginEpoch, pluginProjectId, pluginRevision]);
  // A plugin command runs for a pane: the one named, or the focused one. Its composer says what it holds.
  function runPluginCommand(commandId: string, pane?: Partial<PluginPane>) {
    const command = pluginContributed.commands.find(value => value.id === commandId);
    if (!command || !pluginEpoch) return;
    const composer = askPluginComposer("state", pane?.sessionId ?? null).state;
    const target: PluginPane = {
      projectId: pane?.projectId !== undefined ? pane.projectId : pluginProjectId,
      sessionId: pane?.sessionId ?? composer?.sessionId ?? null,
      busy: pane?.busy ?? composer?.busy ?? false, draftText: pane?.draftText ?? composer?.draftText ?? null,
    };
    const unavailable = () => showToast({ message: t("The command /{name} is not available here.", { name: command.name }), intent: "warning", icon: "warning-sign", timeout: 6000 });
    if (!pluginCommandAvailable(command, target)) { unavailable(); return; }
    void pluginUi.invokeCommand({ expectedEpoch: pluginEpoch, commandId, projectId: target.projectId, sessionId: target.sessionId,
      sessionBusy: target.busy, draftText: target.draftText, spaceId: shownSpace.current }, { timeoutMilliseconds: 8000 })
      .then(reply => { if (reply.status !== "started") unavailable(); }, unavailable);
  }
  const pluginKeys = useMemo(() => pluginKeymap(pluginContributed.commands), [pluginContributed]);
  const pluginShortcuts = useRef({ keys: pluginKeys, run: runPluginCommand });
  pluginShortcuts.current = { keys: pluginKeys, run: runPluginCommand };
  const pluginUiValue = useMemo<PluginUiValue>(() => ({
    epoch: pluginEpoch, projectId: pluginProjectId, contributions: pluginContributed,
    run: (commandId, pane) => pluginShortcuts.current.run(commandId, pane),
    runNamed: (name, pluginKey, pane) => {
      const command = findPluginCommand(pluginContributed.commands, name, pluginKey);
      if (command) pluginShortcuts.current.run(command.id, pane);
    },
  }), [pluginEpoch, pluginProjectId, pluginContributed]);

  // A button of a plugin runs its command or opens its canvas for the context of the button: the project of a row, not the selected one.
  function activatePluginButton(button: PluginButtonView, context: PluginButtonContext) {
    if (!pluginEpoch || button.disabled) return;
    const unavailable = () => showToast({ message: t("{name} is not available here.", { name: button.label }), intent: "warning", icon: "warning-sign", timeout: 6000 });
    if (button.canvas) {
      const item = canvasHub.getCatalog().find(candidate => candidate.pluginKey === button.pluginKey && candidate.id === button.canvas);
      const session = context.sessionId ? (catalog.current ?? currentSnapshot.current)?.sessions.find(candidate => candidate.id === context.sessionId) : undefined;
      const project = button.canvasScope === "Application" ? null : context.projectId ?? (session?.scopeKind === "project" ? session.projectId : null);
      // The tab of a canvas the window lists is made the way the Canvases page makes it.
      const known = project ? (catalog.current ?? currentSnapshot.current)?.projects.find(candidate => candidate.id === project && !candidate.archived) : undefined;
      if (item && (!project || known)) { openCanvas(item, { project: known ? { id: known.id, path: known.path } : null, sessionId: context.sessionId }); return; }
      openCanvasRequest({ pluginKey: button.pluginKey, canvasId: button.canvas, spaceId: shownSpace.current, projectId: project,
        sessionId: button.canvasScope === "Session" ? context.sessionId : null, key: null, plugin: item?.package ?? null, focus: true,
        title: item?.title ?? button.label, icon: item?.icon ?? button.icon });
      return;
    }
    const composer = context.sessionId ? askPluginComposer("state", context.sessionId).state : null;
    void pluginUi.invokeCommand({ expectedEpoch: pluginEpoch, commandId: button.commandId!, projectId: context.projectId, sessionId: context.sessionId,
      sessionBusy: composer?.busy ?? false, draftText: composer?.draftText ?? null, spaceId: shownSpace.current }, { timeoutMilliseconds: 8000 })
      .then(reply => { if (reply.status !== "started") unavailable(); }, unavailable);
  }
  // A button that opens a canvas is marked while that canvas is the tab in front, as the Issues button is.
  function pluginButtonActive(button: PluginButtonView, context: PluginButtonContext): boolean {
    const tab = fileTabs.active;
    if (!button.canvas || !tab || !isCanvasTab(tab) || tab.pluginKey !== button.pluginKey || tab.canvasId !== button.canvas) return false;
    return button.canvasScope === "Application" || (button.canvasScope === "Project" ? tab.projectId === context.projectId : tab.sessionId === context.sessionId);
  }
  const pluginButtonsLatest = useRef({ activate: activatePluginButton, active: pluginButtonActive }); pluginButtonsLatest.current = { activate: activatePluginButton, active: pluginButtonActive };
  const pluginButtonsHost = useMemo<PluginButtonsHost>(() => ({
    epoch: pluginEpoch, spaceId, api: pluginUi, hidden: hiddenPluginButtons,
    activate: (button, context) => pluginButtonsLatest.current.activate(button, context),
    isActive: (button, context) => pluginButtonsLatest.current.active(button, context),
  }), [pluginEpoch, spaceId, hiddenPluginButtons]);

  // Session-scoped commands need an open session that is not behind a file tab; everything else is always offered.
  function commandAvailable(command: CommandId): boolean {
    const session = view === "workspace" && !!selectedSession && !fileTabs.active;
    const ownedSession = session && owned;
    switch (command) {
      case "sessionInfo": case "messagePrevious": case "messageNext": case "messageFirst": case "messageLatest": case "toggleNotes": return session;
      case "usage": case "reminders": case "compact": case "abort": case "clearQueue": case "nextPrompt": case "modelSelector": case "send": case "steer": return ownedSession;
      case "expandPrompt": case "focusPrompt": return view === "workspace" && !fileTabs.active;
      case "focusAskFile": return session && !!visibleAsk(".ask-file-review");
      case "closeTab": case "previousTab": case "nextTab": return tabs.open.length + fileTabs.open.length > 0;
      case "reopenTab": return tabs.closed.length + fileTabs.closed.length > 0;
      case "editFile": case "projectEditor": case "worktrees": return view === "workspace" && !!editedProject();
      case "newTerminal": return view === "workspace" && !!terminalOrigin();
      case "automations": case "workItems": case "issues": case "canvases": return owned;
      case "spaces": case "newSpace": return owned && spacesState.available;
      case "goToSpace": return spacesState.available;
      case "previousSpace": case "nextSpace": return spacesState.available && spacesState.spaces.length > 1;
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
    if (command === "exit") { exitApplication(); return; }
    // The host zooms the window's view and keeps the zoom for the next starts; an open window does not stop it.
    if (command === "zoomIn" || command === "zoomOut" || command === "resetZoom") {
      // The answer holds the zoom the window now has: the title bar shows it.
      void zoomWindow(command, desktopShell.zoom, setShellPreferences).catch(error => console.error("[CodeAlta] zoom failed", error));
      return;
    }
    // Settings is a modal window: only commands that move to another Settings page run while it is open.
    const pages: Partial<Record<CommandId, View>> = { settings: "appearance", about: "about", skills: "skills", plugins: "plugins", mcp: "mcp",
      config: "config", prompts: "prompts", providers: "providers", models: "models", logs: "logs", spaces: "spaces" };
    if (pages[command]) { navigate(pages[command]!); return; }
    if (settingsVisible.current) return;
    switch (command) {
      case "help": openHelp(); break;
      case "palette": openSearch(); break;
      case "openProject": setDialog("project"); break;
      case "editFile": openFilePicker(); break;
      case "projectEditor": openProjectEditor(); break;
      case "worktrees": openWorktrees(); break;
      case "newTerminal": { const origin = terminalOrigin(); if (origin) void createTerminal(origin.projectId, origin.sessionId); break; }
      case "automations": openAutomations(); break;
      case "workItems": openWorkItems(); break;
      case "issues": openFile(issuesTab); break;
      case "canvases": openFile(canvasesTab); break;
      case "newSpace": setSpaceDialog(true); break;
      case "goToSpace": setSpaceMenuRequest(value => value + 1); break;
      case "previousSpace": case "nextSpace":
        if (showSpace(neighborSpace(spacesState.spaces, spaceId, command === "nextSpace" ? 1 : -1).id)) focusPromptSoon();
        break;
      case "newSession": selectProject(projectId); requestAnimationFrame(() => document.querySelector<HTMLElement>("#session-prompt, #catalog-prompt")?.focus()); break;
      case "focusSidebar": runShortcut("focusProjects"); break;
      case "focusAskFile": visibleAsk(".ask-file-review")?.dispatchEvent(new CustomEvent("codealta-ask-file-focus")); break;
      case "toggleNavigator": toggleProjects(); break;
      case "modelSelector": invokeComposerControl(activeComposerControl(".composer-selection")); break;
      case "usage": invokeComposerControl(activeComposerControl("#session-usage-trigger")); break;
      case "searchSessions": openSearch({ category: "sessions" }); break;
      case "refreshStatuses": runtimeObservationControls().refresh(tabs.open); break;
      case "send": case "abort": case "clearQueue": case "nextPrompt": composerCommand(command); break;
      case "steer": break;
      // The remaining commands share their implementation with the older shortcut actions of the same name.
      default: runShortcut(command as ShortcutAction);
    }
  }

  // What the landing page asks of the shell. A command is named as it is after a slash: one of the window first, then one of a plugin.
  function runNamedCommand(name: string): boolean {
    const builtIn = commandDefinitions.find(command => command.name === name);
    if (builtIn) { if (!commandAvailable(builtIn.id)) return false; runCommand(builtIn.id); return true; }
    const contributed = findPluginCommand(pluginContributed.commands, name, null);
    if (!contributed || !pluginEpoch) return false;
    runPluginCommand(contributed.id);
    return true;
  }
  const landingUnavailable = (label: string) => showToast({ message: t("{name} is not available here.", { name: label }), intent: "warning", icon: "warning-sign", timeout: 6000 });
  // The command of a card runs for the project of its plugin, whatever is selected. The host checks the project and the command, as for a button of a plugin.
  function runLandingCardCommand(commandId: string, cardProjectId: string | null, label: string) {
    if (!pluginEpoch) return;
    const unavailable = () => landingUnavailable(label);
    void pluginUi.invokeCommand({ expectedEpoch: pluginEpoch, commandId, projectId: cardProjectId, sessionId: null, sessionBusy: false, draftText: null, spaceId: shownSpace.current },
      { timeoutMilliseconds: 8000 }).then(reply => { if (reply.status !== "started") unavailable(); }, unavailable);
  }
  const landingShell = useLandingShell({ epoch: pluginEpoch, space: findSpace(spacesState.spaces, spaceId), snapshot, run: runNamedCommand, notifyUnavailable: landingUnavailable,
    openProject: id => { selectProject(id); focusPromptSoon(); }, openSession: id => void openAutomationSession(id), runCardCommand: runLandingCardCommand });
  useLandingAtStartup(owned && tabsReady, canvasCatalog, openCanvasHere);

  useEffect(() => {
    // Capture phase: the prompt editor (Monaco) must not see keys that belong to a command.
    function commandKey(event: globalThis.KeyboardEvent) {
      if (event.key === "Escape") { commandChord.current = false; return; }
      // A terminal has the keyboard: its program gets every key but the few the application keeps.
      if (!commandChord.current && event.target instanceof HTMLElement && event.target.closest("[data-terminal-keys] .terminal-host") && !applicationKey(event)) return;
      // Exit is global, as in the terminal UI: it also works while a window of the app is open. So is zoom: it
      // changes the whole window, whatever is open in it.
      const global = commandChord.current ? null : resolveCommandKey(event, false, "none").command;
      if (global === "exit") { event.preventDefault(); event.stopPropagation(); requestExit.current(); return; }
      if (global === "zoomIn" || global === "zoomOut" || global === "resetZoom") {
        event.preventDefault(); event.stopPropagation(); runCommand(global); return;
      }
      const target = event.target instanceof HTMLElement ? event.target : null;
      const settingsOnly = settingsVisible.current && !dialog && !searchOpen
        && document.querySelectorAll('dialog[open], [role="dialog"][aria-modal="true"]').length === 1;
      const modal = searchOpen || !!dialog || !!modalDialogOpen();
      if (modal && !settingsOnly) { commandChord.current = false; return; }
      // In an open ask Ctrl+N and Ctrl+P move between its questions and between the comments of its file.
      if (!commandChord.current && event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey && ["n", "p"].includes(event.key.toLowerCase())
        && target?.closest("[data-ask-keys]")) return;
      // In the text of the code editor Ctrl+G goes to a line, as in the terminal UI's editor.
      const editing = !!target?.closest("[data-editor-keys]");
      // A Changes tab goes through its changes, or its files, with Alt+Down and Alt+Up: from its list of files as
      // well as from a diff, they are not the session shortcuts there.
      if (!commandChord.current && changeKeyKept(event, target)) return;
      // A list that renames its own rows (the files of the code editor) keeps F2.
      if (!commandChord.current && event.key === "F2" && target?.closest("[data-rename-keys]")) return;
      if (editing && !commandChord.current && event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey && event.key.toLowerCase() === "g") return;
      const focus = target?.closest("#session-prompt, #catalog-prompt") ? "prompt" : target?.closest(workspaceEditingSelector) ? "text" : "none";
      // After Ctrl+G a digit shows the space at that place of the list.
      if (commandChord.current && /^[1-9]$/u.test(event.key) && !event.altKey && !event.shiftKey && !event.metaKey && !settingsVisible.current) {
        commandChord.current = false;
        event.preventDefault(); event.stopPropagation();
        const space = spacesHub.getSnapshot().spaces[Number(event.key) - 1];
        if (space && showSpace(space.id)) focusPromptSoon();
        return;
      }
      // "?" outside text opens help, as it does when typed into an empty prompt.
      const resolved = focus === "none" && !commandChord.current && event.key === "?" && !event.ctrlKey && !event.altKey && !event.metaKey && !event.repeat
        ? { command: "help" as CommandId, chord: false, handled: true }
        : resolveCommandKey(event, commandChord.current, focus);
      const chorded = commandChord.current;
      commandChord.current = resolved.chord;
      // A key that no command of the window takes can be the shortcut of a plugin command.
      const plugin = resolved.command || resolved.chord ? null : resolvePluginKey(event, chorded, focus, pluginShortcuts.current.keys);
      if (plugin) { event.preventDefault(); event.stopPropagation(); pluginShortcuts.current.run(plugin.id); return; }
      if (!resolved.handled) return;
      // A key whose command has nothing to act on now is the editor's: F3 goes to the next match of its search.
      if (editing && resolved.command && !chorded && !commandAvailable(resolved.command)) return;
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
        || modalDialogOpen()) return;
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
    // An open ask has the place of the prompt: its questions take the focus.
    else if (action === "focusPrompt") (visibleAsk(".ask-form")?.querySelector<HTMLElement>("[data-ask-question] input:checked, [data-ask-question] textarea, [data-ask-question] input")
      ?? document.querySelector<HTMLTextAreaElement>("#session-prompt, #catalog-prompt"))?.focus();
    else if (action === "focusSearch") openSearch({ category: "sessions" });
    else if (action === "focusProjects") {
      if (!railVisible) toggleProjects();
      else focusVisibleProject(projectRail.current);
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
      const index = Math.max(0, visibleSessions.findIndex(session => session.id === sessionId));
      const next = visibleSessions[(index + (action === "nextSession" ? 1 : -1) + visibleSessions.length) % visibleSessions.length].id;
      selectedSessionId.current = next;
      setSessionId(next);
    }
  }

  function openSelectedReminders(session: string, epoch: string, scope: string | null) {
    if (currentView.current !== "workspace" || dialog || modalDialogOpen()
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
    () => !!modalDialogOpen());
  }
  const remindersCurrent = !!remindersOrigin.current?.lifetime.current();
  useLayoutEffect(() => {
    if (dialog === "reminders" && !remindersCurrent) setDialog(null);
  }, [dialog, remindersCurrent]);
  const filePickerProject = editedProject();
  useLayoutEffect(() => {
    if (dialog === "file" && !filePickerProject) setDialog(null);
  }, [dialog, filePickerProject]);

  function scopeCanCreateSession(id: string | null) {
    const project = id === null ? undefined : snapshot?.projects.find(value => value.id === id);
    return owned && !!snapshot && !creatingBusy && (id === null || !!project && !project.archived);
  }
  // One menu per scope row: the session actions first select that scope, then act on it.
  function scopeSessionAction(id: string | null, kind: "create" | "search" | "browse") {
    // The sessions of a scope are searched in the search of the window, which then keeps to that scope.
    if (kind === "search") { openSearch({ category: "sessions", projectId: id }); return; }
    if (id !== selectedScope.current) selectProject(id);
    // The form of a new session is with the sessions of the scope: a closed scope hides it.
    if (kind === "create") setProjectTree(current => expandScope(current, id));
    if (kind === "browse") openSessionBrowser();
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
    if (!archiveBusy) { setArchiveAsk(null); setArchiveMessage(""); }
    navigate("workspace");
    setCreatingVisible(false);
    setCreatingMessage("");
  }

  // A session of an open scope that is not the selected one. Opening it makes its scope the selected one; renaming
  // and deleting act on the selected session, so they open it first and start once it is the one selected.
  const pendingSessionAction = useRef<{ projectId: string | null; sessionId: string; action: SessionAction } | null>(null);
  function openScopeSession(scope: string | null, session: WorkspaceSession, action: SessionAction) {
    pendingSessionAction.current = { projectId: scope, sessionId: session.id, action };
    selectProject(scope, session.id);
  }
  useLayoutEffect(() => {
    const pending = pendingSessionAction.current;
    pendingSessionAction.current = null;
    if (!pending || pending.projectId !== projectId || pending.sessionId !== sessionId) return;
    // The row that was used is gone with the list of its scope: the same session is now in the list of the selected one.
    const button = sessionRail.current?.querySelector<HTMLButtonElement>('.session-row > button[aria-pressed="true"]');
    if (!modalDialogOpen()) button?.focus({ preventScroll: true });
    button?.scrollIntoView({ block: "nearest" });
    if (pending.action === "open") return;
    const rows = snapshot?.sessions.filter(session => session.id === pending.sessionId) ?? [];
    if (rows.length !== 1) return;
    const access = sessionActionAccess(rows[0], { id: pending.sessionId, projectId, hostEpoch: status?.hostEpoch ?? null }, pending.sessionId,
      selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
      mutation?.capability.canMutate() ?? false, batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current);
    if (!access[pending.action]) return;
    if (pending.action === "rename") { beginSessionRename(rows[0]); setDeletingId(null); }
    else { beginSessionDelete(rows[0]); setRenamingId(null); }
  }, [projectId, sessionId]);
  function sessionMarks(session: WorkspaceSession, scope: string | null) {
    return <>
      <SessionDraftStatus indicators={draftIndicators} sessionId={session.id} selectedId={sessionId} />
      <SessionWaitingBadge waiting={waitingSessions.has(session.id)} />
      {snapshot && <RunningSessionBadge controls={runtimeObservationControls()} tab={{ projectId: scope, sessionId: session.id, path: session.workspacePath }} />}
      <ReminderBadge count={activeReminders?.get(session.id) ?? 0} />
      {carriedBy(work, session.id).length > 0 && <span className="session-work-mark" role="img" aria-label={t("Carries out a work item")}
        title={carriedBy(work, session.id).map(item => item.title).join("\n")}><AppIcon name="task" size={12} /></span>}
      <WorktreeBadge session={session} />
      <span className="session-meta"><span>{session.providerKey ?? t("No provider")}</span><SessionTime value={session.updatedAt} now={clock} /></span>
    </>;
  }
  function scopeSessions(scope: string | null) {
    const shown = openScopes.get(scopeKey(scope));
    if (!shown || !snapshot) return null;
    const project = scope === null ? undefined : snapshot.projects.find(value => value.id === scope);
    const extra = sessionExtras.get(scopeKey(scope)) ?? 0;
    return <ExplorerSessions entries={shown.entries} global={scope === null} more={shown.more} extended={extra > 0} tree={sessionTree} marks={session => sessionMarks(session, scope)}
      access={session => sessionActionAccess(session, { id: session.id, projectId: scope, hostEpoch: status?.hostEpoch ?? null }, session.id, scope, project,
        currentHostEpoch.current ?? null, owned, mutation?.capability.canMutate() ?? false,
        batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
        renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current)}
      onAction={(session, action) => openScopeSession(scope, session, action)} deleteAsks={confirms.sessionDelete}
      onMore={() => setSessionExtra(scope, extra + recentSessionCount)} onFewer={() => setSessionExtra(scope, 0)} />;
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

  // The session is the selected one. It is asked about beside its row, unless the user answered not to be asked again.
  function beginSessionDelete(row: WorkspaceSession) {
    setDeletingMessage("");
    if (confirms.sessionDelete) setDeletingId(row.id);
    else { setDeletingId(null); void deleteSessionRow(row, false); }
  }

  // Delete on the row of a session does what **Delete** of its menu does.
  function deleteSessionFromRow(row: WorkspaceSession) {
    if (snapshot?.sessions.filter(session => session.id === row.id).length !== 1 || currentSnapshot.current !== snapshot
      || settingsVisible.current || dialog || modalDialogOpen()) return;
    if (!sessionActionAccess(row, { id: row.id, projectId, hostEpoch: status?.hostEpoch ?? null }, row.id,
      selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
      mutation?.capability.canMutate() ?? false, batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current).delete) return;
    setMenuTarget(null);
    if (selectedSessionId.current !== row.id) { selectedSessionId.current = row.id; setSessionId(row.id); setRenamingMessage(""); }
    setRenamingId(null);
    beginSessionDelete(row);
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
      {providers.map(provider => <option key={provider.id} value={provider.id}>{provider.isDefault ? t("{name} (default)", { name: provider.id }) : provider.id}</option>)}
    </HTMLSelect>;
    if (compact) {
      const locked = creatingBusy || creationLocked || !owned;
      const value = draftChoices.value;
      const efforts = draftChoices.models.find(model => model.id === value.modelId)?.efforts ?? null;
      const change = (field: "agentPromptId" | "modelId" | "reasoningEffort", next: string) => {
        invalidateCreation(); draftChoices.change(field, next);
      };
      return <ComposerSelectionFields sessionId="new" onOpenCatalog={navigate}
        summary={{ agent: draftChoices.prompts.find(prompt => prompt.id === value.agentPromptId)?.name ?? t(draftChoices.loadingPrompts ? "Loading…" : "Host default"),
          provider: usedProvider || t("No provider"), providerKey: usedProvider || null, modelId: value.modelId || null,
          model: value.modelId ? draftChoices.models.find(model => model.id === value.modelId)?.name ?? value.modelId : t(draftChoices.loadingModels ? "Loading…" : "No model"),
          reasoning: value.reasoningEffort ?? (draftChoices.loadingModels ? t("Loading…") : null) }}
        agent={<HTMLSelect fill id="composer-agent-new" aria-label={t("Agent prompt")} value={value.agentPromptId}
          disabled={locked || draftChoices.loadingPrompts || !draftChoices.prompts.length} onChange={event => change("agentPromptId", event.target.value)}>
          {!draftChoices.prompts.some(prompt => prompt.id === value.agentPromptId) && <option value={value.agentPromptId}>{t(draftChoices.loadingPrompts ? "Loading…" : "Host default")}</option>}
          {draftChoices.prompts.map(prompt => <option key={prompt.id} value={prompt.id}>{prompt.name}</option>)}
        </HTMLSelect>} provider={select}
        model={<HTMLSelect fill id="composer-model-new" data-model-selector aria-label={t("Model")} value={value.modelId ?? ""}
          disabled={locked || draftChoices.loadingModels} onChange={event => change("modelId", event.target.value)}>
          {!value.modelId && <option value="">{t(draftChoices.loadingModels ? "Loading…" : "No model")}</option>}
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
      || projectId !== null && !selectedProject || settingsVisible.current || dialog || searchOpen
      || currentView.current !== "workspace" || modalDialogOpen()) return;
    // The text as it is now, not as it was when App was last rendered.
    const typed = localDrafts.peek(draftScope) ?? localDraft;
    if (fromDraft && (!draftChoices.ready || sessionId !== null || (!typed.text.trim() && !localImages.images.length)
      || localImages.images.length > 0 && (typed.text.length > imageLimits.text || typed.text !== "" && !typed.text.trim()))) return;
    invalidateCreation(); // Deliberate capture fences any outstanding local paste.
    const handoff = fromDraft ? { scope: draftScope, ...typed, imageKey: localImageKey, images: localImages.images,
      imageGeneration: localImageGeneration.current, selection: { ...draftChoices.value } } : null;
    const recordHandoff = (outcome: string) => { if (handoff) setDraftHandoffNotice(outcome); };
    creationPending.current = true;
    creationHeld.current = true;
    setCreationLocked(true);
    const providerId = handoff?.selection.providerKey || creatingProvider || null;
    const target: SessionTarget = selectedProject
      ? { scope: "project", projectId: selectedProject.id, projectPath: selectedProject.path } : { scope: "global" };
    // The place is the one the chip of the new session shows: a session created from elsewhere works in the folder of its project.
    const place = fromDraft && selectedProject ? workPlaces.get(selectedProject.id) ?? null : null;
    const generation = creationGeneration.current;
    const epoch = status?.hostEpoch;
    const sessionAtAdmission = sessionId;
    const capability = mutation.capability;
    const isCurrent = () => creationAlive.current && generation === creationGeneration.current
      && currentHostEpoch.current === epoch && currentHostAvailable.current && capability.canMutate()
      && selectedScope.current === (target.scope === "project" ? target.projectId : null)
      && selectedSessionId.current === sessionAtAdmission && currentView.current === "workspace"
      && !settingsVisible.current && !modalDialogOpen()
      && (!handoff || localDrafts.peek(handoff.scope)?.revision === handoff.revision)
      && (!handoff || submissions.imageDrafts.get(handoff.imageKey) === handoff.images)
      && (!handoff || localImageGeneration.current === handoff.imageGeneration)
      && (target.scope === "global" || currentSnapshot.current?.projects.filter(project => project.id === target.projectId).length === 1
        && currentSnapshot.current.projects.some(project => project.id === target.projectId && project.path === target.projectPath && !project.archived));
    const completedElsewhere = "Creation may have completed, but its original view or input lifetime changed or the catalog did not confirm it. Inspect sessions; no retry was sent.";
    setCreatingBusy(true);
    setCreatingMessage("");
    if (handoff) setDraftHandoffNotice("");
    try {
      const result = await createSession(epoch, target, handoff ? null : creatingTitle.trim() || null, capability, providerId, place);
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
            if (handoff.scope === draftScope && localDrafts.peek(draftScope)?.revision === handoff.revision) editLocalDraft("");
          }
          publishWorkspaceState({ kind: "ready", snapshot: fresh });
          creationHeld.current = false;
          setCreationLocked(false);
          selectedScope.current = selection.projectId;
          setProjectId(selection.projectId);
          setSessionId(selection.sessionId);
          setCreatingVisible(false);
          setCreatingTitle("");
          navigate("workspace");
        } else { setCreatingMessage(completedElsewhere); recordHandoff(completedElsewhere + " Original draft retained; nothing sent."); }
      } else {
        // Only a correlated definite refusal releases this App-owned original. Changes
        // to selection, inventory, settings or host never release an uncertain attempt.
        if (["invalid_scope", "unconfigured", "project_missing", "provider_unavailable", "busy", "closed", "worktree_failed"].includes(result.code)) {
          creationHeld.current = false;
          setCreationLocked(false);
        }
        // Where no worktree can ever be made, the next session works in the folder of the project again.
        if (result.code === "worktree_failed" && (result.reason === "not_repository" || result.reason === "git_unavailable") && target.scope === "project")
          setWorkPlace(target.projectId, inProjectFolder);
        const message = result.code === "worktree_failed" ? worktreeCreationMessage(result.reason, result.message)
          : sessionCreationMessage(result.code) + (isCurrent() ? "" : ` ${completedElsewhere}`);
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

  const sessionRows = () => Array.from(sessionRail.current?.querySelectorAll<HTMLButtonElement>(".session-row > button:first-child") ?? []);
  // The row that takes the place of a deleted session gets the focus its question or its row lost with it, so the
  // keyboard goes on from there. A focus that went elsewhere meanwhile stays where it is.
  const [deletedRow, setDeletedRow] = useState<{ index: number } | null>(null);
  useLayoutEffect(() => {
    if (!deletedRow) return;
    setDeletedRow(null);
    const focused = document.activeElement;
    if (modalDialogOpen() || focused instanceof HTMLElement && focused !== document.body && !sessionRail.current?.contains(focused)) return;
    const rows = sessionRows();
    rows[Math.min(deletedRow.index, rows.length - 1)]?.focus();
  }, [deletedRow]);

  // Deletes the selected session. What went wrong is told in the question that was answered, or in a notice when
  // nothing was asked.
  async function deleteSessionRow(session: WorkspaceSession, asked: boolean) {
    if (batchDeletion.locked()) return;
    if (selectedSessionId.current !== session.id
      || selectedScope.current !== (selectedProject?.id ?? null) || deletingPending.current || uncertainDelete.current
      || !owned || !mutation?.capability.canMutate() || !session.workspacePath || selectedProject?.archived) return;
    if (!sessionActionAccess(session, { id: session.id, projectId: projectId, hostEpoch: status?.hostEpoch ?? null },
      selectedSessionId.current, selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
      mutation.capability.canMutate(), deletingBusy || renamingBusy || renamingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current).delete) return;
    const target: RenameTarget = selectedProject
      ? { scope: "project", projectId: selectedProject.id, projectPath: selectedProject.path }
      : { scope: "global", projectPath: session.workspacePath };
    const captured: DeletedTarget = { target, id: session.id };
    const capability = mutation.capability;
    const index = sessionRows().findIndex(row => row.parentElement?.dataset.sessionId === session.id);
    const failed = (message: string) => {
      if (!asked) showToast({ message, intent: "danger", icon: "error", timeout: 8000 });
      else if (selectedSessionId.current === session.id) setDeletingMessage(message);
    };
    deletingPending.current = true;
    setDeletingBusy(true);
    setDeletingMessage("");
    try {
      const result = await deleteSession(status?.hostEpoch, target, session.id, session.title, capability);
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
          if (index >= 0) setDeletedRow({ index });
        } else {
          uncertainDelete.current = captured;
          setDeleteLocked(true);
          failed("Deletion may have completed, but the refreshed catalog did not confirm absence. No retry will be sent.");
        }
      } else {
        if (result.code === "delete_unconfirmed") {
          uncertainDelete.current = captured;
          setDeleteLocked(true);
        }
        failed(sessionDeletionMessage(result.code));
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
      setDeletingMessage("");
    } else if (selectedSessionId.current === captured.id)
      setDeletingMessage("Absence is not confirmed in the refreshed catalog. No retry will be sent; inspect the session or reload.");
  }

  // Archiving the selected project, or taking it out of the archive, is asked about beside its row, unless the
  // user answered not to be asked again. The project file is read and written in one go once the answer is yes.
  const [archiveAsk, setArchiveAsk] = useState<{ id: string; archived: boolean } | null>(null);
  const [archiveBusy, setArchiveBusy] = useState(false);
  const [archiveMessage, setArchiveMessage] = useState("");
  // A question is asked beside a row: when the Explorer goes, as in a window made narrow, it goes with its row.
  useEffect(() => {
    if (railVisible) return;
    if (!deletingPending.current) setDeletingId(null);
    if (!archiveBusy) setArchiveAsk(null);
  }, [railVisible]);
  function archiveScope(): ArchiveScope | null {
    const matches = currentSnapshot.current?.projects.filter(project => project.id === selectedScope.current);
    const project = matches?.length === 1 ? matches[0] : undefined;
    if (!project || !owned || !mutation?.capability.canMutate() || !currentHostEpoch.current || settingsVisible.current || currentView.current !== "workspace") return null;
    return { epoch: currentHostEpoch.current, id: project.id, path: project.path, archived: project.archived,
      generation: browserRevision.current + creationGeneration.current };
  }
  function beginProjectArchive() {
    const scope = archiveScope();
    if (!scope || archiveBusy || projectArchive.locked) return;
    setArchiveMessage("");
    if (confirms.projectArchive) setArchiveAsk({ id: scope.id, archived: scope.archived });
    else void archiveSelectedProject(false);
  }
  async function archiveSelectedProject(asked: boolean) {
    const failed = (message: string) => {
      if (asked) setArchiveMessage(message); else showToast({ message, intent: "danger", icon: "error", timeout: 8000 });
    };
    const changed = "The project changed. Nothing was written.";
    const scope = archiveScope();
    if (!scope) { failed(changed); return; }
    setArchiveBusy(true);
    setArchiveMessage("");
    try {
      const target = await projectArchive.prepare(scope);
      if (!creationAlive.current) return;
      if (typeof target === "string") { failed(target); return; }
      if (!archiveScopeCurrent(target, archiveScope())) { failed(changed); return; }
      const result = await projectArchive.confirm(target, archiveScope());
      if (!creationAlive.current) return;
      if (result?.state !== "confirmed") { failed(result?.status ?? changed); return; }
      setArchiveAsk(null);
      // The write is confirmed even when the projects cannot be read again.
      if (currentHostEpoch.current !== target.epoch || !mutation?.capability.canMutate()) return;
      const version = browserRevision.current;
      try {
        const fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 });
        if (creationAlive.current && currentHostEpoch.current === target.epoch && mutation.capability.canMutate() && version === browserRevision.current && fresh.configured)
          publishWorkspaceState({ kind: "ready", snapshot: fresh });
      } catch { /* The list is read again later. */ }
    } finally { if (creationAlive.current) setArchiveBusy(false); }
  }

  // Settings pages also edit the selected project's settings when it can be written.
  const settingsProject = selectedProject && !selectedProject.archived ? { id: selectedProject.id, name: selectedProject.name } : null;
  // The folder of the color schemes of the user, in the code editor: the host finds it, and the window leaves Settings.
  function openColorSchemeFolder() {
    const failed = () => showToast({ message: translate(shownLocale.current, "The file could not be opened."), intent: "danger", icon: "error", timeout: 8000 });
    void settingsFiles.open({ expectedEpoch: status?.hostEpoch ?? null, projectId: null, kind: "colorSchemes", scope: "Global", id: null, part: null }, { timeoutMilliseconds: 15000 })
      .then(result => { if (result.status === "ok") closeSettings(); else failed(); }, failed);
  }
  const newPromptDisabled = creatingBusy || creationLocked || !draftChoices.ready || !owned || !mutation?.capability.canMutate() || !snapshot
    || !!selectedProject?.archived || projectId !== null && !selectedProject || (!localDraft.text.trim() && !localImages.images.length)
    || localImages.images.length > 0 && (localDraft.text.length > imageLimits.text || localDraft.text !== "" && !localDraft.text.trim());
  const referenceAvailable = owned && !!mutation?.capability.canMutate() && !settingsOpen && !!selectedProject && !selectedProject.archived
    && !!snapshot && (sessionId === null || !!selectedTab(snapshot, projectId, sessionId));
  const referenceEpoch = status?.hostEpoch, referenceProject = selectedProject?.id, referencePath = selectedProject?.path, referenceLifetime = creationGeneration.current;
  const selectedReference = useMemo(() => referenceAvailable && referenceEpoch && referenceProject && referencePath !== undefined
    ? { expectedEpoch: referenceEpoch, projectId: referenceProject, projectPath: referencePath, sessionId, lifetime: referenceLifetime,
      capturePopup: captureReferencePopup, observe: observeReference } : null,
  [referenceAvailable, referenceEpoch, referenceProject, referencePath, sessionId, referenceLifetime, captureReferencePopup, observeReference]);
  return <ShellLanguageContext.Provider value={language}><ProviderBrandsContext.Provider value={providerLogos}><PluginUiContext.Provider value={pluginUiValue}><PluginHostBridgeContext.Provider value={pluginHostBridge}><PluginButtonsContext.Provider value={pluginButtonsHost}><PluginButtonsActiveContext.Provider value={fileTabs.active ?? null}><PullRequestSettingsContext.Provider value={owned ? openPullRequestSettings : null}><SessionLinksContext.Provider value={sessionLinks}><MessageLinksContext.Provider value={messageLinkEpoch ? openMessageLink : null}><SessionWidthContext.Provider value={sessionWidthControl}><ShowChangesContext.Provider value={owned ? showProjectChanges : null}><OpenTerminalContext.Provider value={owned ? openSessionTerminal : null}><SessionListRefreshContext.Provider value={owned ? refreshSessionList : null}><ShellAppearance appearance={appearance} preview={appearancePreview} /><div className="app-shell ide-shell">
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
        } as CSSProperties}>
        <WindowBrand developer={status?.developerMode ?? false}>
          <nav className="activity-rail" aria-label={t("Workspace navigation")}>
            <Button ref={projectRailToggle} variant="minimal" size="small" active={railVisible} icon={<AppIcon name="folder" size={16} />} aria-label={t("Explorer")} title={t("Explorer")} aria-expanded={railVisible} aria-controls="project-rail" onClick={toggleProjects} />
            <Button variant="minimal" size="small" icon={<AppIcon name="search" size={16} />} aria-label={t("Search")} aria-haspopup="dialog" title={`${t("Search")} (Ctrl+P)`} onClick={() => openSearch()} />
            <Button variant="minimal" size="small" icon={<AppIcon name="automation" size={16} />} className="activity-automations" disabled={!owned}
              active={!!fileTabs.active && isAutomationsTab(fileTabs.active)} aria-label={t("Automations")} title={`${t("Automations")} (Ctrl+G, Ctrl+M)`} onClick={() => openAutomations()} />
            <Button variant="minimal" size="small" icon={<AppIcon name="task" size={16} />} className="activity-work" disabled={!owned}
              active={!!fileTabs.active && isWorkItemsTab(fileTabs.active)} aria-label={t("Work items")} title={`${t("Work items")} (Ctrl+G, Ctrl+I)`} onClick={() => openWorkItems()}>
              {work.some(item => item.stage === "todo") && <span className="activity-work-dot" aria-hidden="true" />}</Button>
            <Button variant="minimal" size="small" icon={<AppIcon name="issueOpen" size={16} />} className="activity-issues" disabled={!owned}
              active={!!fileTabs.active && isIssuesTab(fileTabs.active)} aria-label={t("Issues")} title={`${t("Issues")} (Ctrl+G, Ctrl+B)`} onClick={() => openFile(issuesTab)} />
            <Button variant="minimal" size="small" icon={<AppIcon name="canvases" size={16} />} className="activity-canvases" disabled={!owned}
              active={!!fileTabs.active && isCanvasesTab(fileTabs.active)} aria-label={t("Canvases")} title={t("Canvases")} onClick={() => openFile(canvasesTab)} />
            <PluginButtons place="Rail" context={pluginButtonContext} />
            <Button variant="minimal" size="small" icon={<AppIcon name="settings" size={16} />} className="activity-settings" aria-label={t("Settings & extensions")} title={t("Settings & extensions")} onClick={() => navigate("appearance")} />
          </nav>
        </WindowBrand>
        <div className="window-actions">
          <PluginButtons place="TitleBar" context={pluginButtonContext} />
          {spacesState.available && <SpaceSwitch spaces={spacesState.spaces} shownId={spaceId} activity={spacesState.activity} canEdit={owned} request={spaceMenuRequest}
            onShow={id => { if (showSpace(id)) focusPromptSoon(); }} onCreate={() => setSpaceDialog(true)} onOrganize={() => navigate("spaces")} />}
          {shellPreferences && <WindowZoom zoom={shellPreferences.zoom} run={runCommand} />}
          <Button variant="minimal" size="small" className="theme-switch" icon={<AppIcon name={themeIcons[theme]} size={16} />}
            aria-label={t("Theme: {theme}", { theme: t(themeLabel(theme)) })} title={t("Theme: {theme}", { theme: t(themeLabel(theme)) })} onClick={() => setTheme(nextTheme(theme))} />
        </div>
        <WindowControls snapshot={windowSnapshot} />
        <SessionContentLayout sessionsHidden={!railVisible}
          projects={sessions => { const railHead = <>
          <div className="panel-title"><span title={projectListing?.evidenceNotice ?? undefined}>{t("Projects")}<span className="count">{snapshot?.projects.length ?? 0}</span></span><span>
            {snapshot && <Button variant="minimal" size="small" className="rail-action" icon={<AppIcon name="collapseAll" size={16} />} aria-label={t("Collapse all")} title={t("Collapse all")}
              disabled={!projectTree.expanded.length} onClick={() => setProjectTree(collapseAllScopes)} />}
            {snapshot && <PopoverNext placement="bottom-end" content={<Menu aria-label={t("Project actions")}>
              <MenuDivider title={t("Sort projects")} />
              <MenuItem roleStructure="listoption" selected={projectSort === "name"} text={t("Name")} onClick={() => setProjectSort("name")} />
              <MenuItem roleStructure="listoption" selected={projectSort === "recent"} text={t("Recent visible updates")} onClick={() => setProjectSort("recent")} />
              <MenuDivider />
              <MenuItem icon={<AppIcon name="open" size={15} />} text={`${t("Open project")}…`} label="Ctrl+O" onClick={() => setDialog("project")} />
              <MenuItem icon={<AppIcon name="archive" size={15} />} text={t(confirms.projectArchive ? selectedProject?.archived ? "Unarchive project…" : "Archive project…"
                : selectedProject?.archived ? "Unarchive project" : "Archive project")}
                disabled={!selectedProject || !owned || !mutation?.capability.canMutate() || archiveBusy || projectArchive.locked} onClick={beginProjectArchive} />
            </Menu>}>
              <Button variant="minimal" size="small" className="rail-action" icon={<AppIcon name="ellipsis" size={18} />} aria-label={t("Project actions")} title={t("Project actions")} />
            </PopoverNext>}
            <Button variant="minimal" size="small" className="rail-action" icon={<AppIcon name="plus" size={18} />} aria-label={t("Add a project folder")} title={t("Add a project folder")} onClick={() => void addProjectFolder()} />
          </span></div>
          {workspaceState.kind === "loading" && <LoadingRows />}
          {workspaceState.kind === "unconfigured" && <div className="sidebar-empty">{t("No catalog configured. See the launch instructions below.")}</div>}
          {workspaceState.kind === "error" && <div role="alert" className="sidebar-empty error-text">{workspaceState.message}</div>}
          {snapshot && projectListing?.projects.length === 0 && (spaceId === defaultSpaceId ? <p className="sidebar-empty" role="status">
            {t("No projects in this snapshot.")}
            {projectId !== null && ` ${t("The selected project and session remain open.")}`}
          </p> : <div className="sidebar-empty space-empty" role="status">
            <p>{t("No project in this space yet.")}</p>
            {owned && <Button size="small" icon={<AppIcon name="space" size={14} />} onClick={() => navigate("spaces")}>{t("Add projects")}…</Button>}
          </div>)}
          </>;
          return <aside id="project-rail" className="project-rail" aria-label={t("Projects")} ref={projectRail} hidden={!railVisible}>
          {!snapshot && railHead}
          {snapshot && <ProjectRailRows head={railHead} projects={projectListing?.projects ?? []} favorites={projectListing?.favorites ?? 0} selectedId={projectId} onSelect={selectProject} children={sessions}
            tree={{ expanded: id => isExpanded(projectTree, id), toggle: id => setProjectTree(current => toggleScope(current, id)),
              favorite: id => isFavorite(projectTree, id), setFavorite: (project, value) => setProjectTree(current => setFavorite(current, project.id, value)),
              sessions: scopeSessions,
              after: id => <TerminalList terminals={terminalsOf(terminalList, id)} rename={terminalWorkspace.hub.rename}
                active={fileTabs.active && isTerminalTab(fileTabs.active) ? fileTabs.active.terminalId ?? null : null}
                onOpen={showTerminal} onClose={terminal => terminalWorkspace.hub.close(terminal.id)} /> }}
            terminals={owned ? { count: id => terminalsOf(terminalList, id).length, create: project => void createTerminal(project.id) } : undefined}
            changes={owned ? { open: id => fileTabs.open.some(tab => isChangesTab(tab) && tab.projectId === id), show: project => showChanges(project) } : undefined}
            activity={id => <><WaitingBadge count={spacesState.sessions.reduce((count, session) => count + (session.waiting && session.projectId === id ? 1 : 0), 0)} />
              <RunningSessionBadge controls={runtimeObservationControls()} projectId={id} />
              <ReminderBadge count={activeReminders ? scopeReminderCount(activeReminders, snapshot, id) : 0} />
              {id !== null && <WorkItemsBadge counts={workByProject.get(id)} onOpen={owned ? () => openWorkItems({ projectId: id }) : undefined} />}</>}
            canRename={owned} renameBusy={projectRenameBusy || !mutation?.capability.canMutate()} onRename={() => void beginProjectRename()}
            renaming={projectRenaming && projectRenameTarget ? { id: projectRenameTarget.id, form: <RenamePopover label={t("Project name")}
              value={projectRenameName} onChange={setProjectRenameName} busy={projectRenameBusy} disabled={projectRenameLocked || projectRenameConflict}
              error={projectRenameNotice ? workflowNotice(language.locale, projectRenameNotice) : null}
              onSubmit={() => void saveProjectRename()} onCancel={() => { projectRenameGeneration.current++; setProjectRenameTarget(null); }} /> } : undefined}
            asking={archiveAsk && selectedProject && archiveAsk.id === selectedProject.id && archiveAsk.archived === selectedProject.archived ? { id: archiveAsk.id,
              form: <ConfirmPopover title={t(archiveAsk.archived ? "Unarchive this project?" : "Archive this project?")} subject={selectedProject.name}
                detail={t(archiveAsk.archived ? "It can be worked in again. Nothing is started."
                  : "It becomes read-only in CodeAlta until it is unarchived. Its files and its sessions are kept.")}
                confirmLabel={t(archiveAsk.archived ? "Unarchive" : "Archive")} busy={archiveBusy} error={archiveMessage || null}
                onConfirm={remember => { if (remember) setConfirm("projectArchive", false); void archiveSelectedProject(true); }}
                onCancel={() => { setArchiveAsk(null); setArchiveMessage(""); }} /> } : undefined}
            actions={{ current: () => ({ ...currentProjectDetailsContext(),
              active: creationAlive.current && currentView.current === "workspace" && !settingsVisible.current && !!projectRail.current && !projectRail.current.hidden,
              generation: browserRevision.current + projectRenameGeneration.current, modalGeneration: creationGeneration.current,
              canMutate: owned && !!mutation?.capability.canMutate(),
              locked: projectRenamePending.current || !!uncertainProjectRename.current || projectRenameLocked
                || !!projectRenameTarget || projectArchive.locked || !!projectOpening.getSnapshot() }),
              open: selectProject, rename: () => void beginProjectRename(), archive: beginProjectArchive, archiveAsks: confirms.projectArchive,
              worktrees: owned ? project => openWorktrees(project) : undefined,
              canvases: owned ? { list: () => canvasMenuItems(canvasCatalog, "Project"), open: (item, project) => openCanvas(item, { project: { id: project.id, path: project.path }, sessionId: null }),
                all: () => openFile(canvasesTab) } : undefined,
              sessions: { canCreate: scopeCanCreateSession, create: id => scopeSessionAction(id, "create"),
                search: id => scopeSessionAction(id, "search"), browse: id => scopeSessionAction(id, "browse") } }}
            editor={owned ? { open: id => fileTabs.open.some(tab => isEditorTab(tab) && tab.projectId === id),
              unsaved: project => fileEditors.dirty(fileTabKey(editorTab(project))), show: project => openProjectEditor(project) } : undefined} />}
          {projectArchive.uncertain && <p role="alert" className="notice error-text">{t("A change of a project archive could not be confirmed. Check the project, and reload the window before archiving again.")}</p>}
          {projectRenameNotice && !projectRenaming && <p role="alert" className="notice error-text">{workflowNotice(language.locale, projectRenameNotice)}</p>}
          {projectRenameLocked && <button type="button" className="quiet-button" onClick={() => void refreshProjectRename()}>{t("Refresh project name (no retry)")}</button>}
          <SpaceActivityBar spaces={spacesState.spaces} shownId={spaceId} activity={spacesState.activity} calls={calls}
            onShow={id => { if (showSpace(id)) focusPromptSoon(); }} onOpen={(id, session) => void openSpaceSession(id, session)} />
        </aside>; }}
          splitter={<PaneSplitter className="session-splitter" label={t("Resize Explorer")} value={ideWidth.width} hidden={narrow || !railVisible}
            onResize={delta => setIdeWidth(value => resizeIdeWidth(value, delta))} onReset={() => setIdeWidth(defaultIdeWidth)} />}
          sessions={<aside className="session-rail" aria-label={t("Sessions")} ref={sessionRail} hidden={!railVisible}>
          {creatingVisible && <div className="session-create">
            <label>{selectedProject ? t("New session in {name}", { name: selectedProject.name }) : t("New chat")}
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
            {selectedList.entries.map((entry, index) => {
              if (entry.kind === "more") return <SubAgentDisclosure key={`more:${entry.parentId}`} entry={entry}
                onMore={() => sessionTree.more(entry.parentId)} onFewer={() => sessionTree.fewer(entry.parentId)} />;
              const { session, depth, diagnostic, tooltip, subAgents } = entry.row;
              const twist = sessionTwist(entry, sessionTree.toggle);
              const menu = activeMenu?.id === session.id ? activeMenu : null;
              const access = sessionActionAccess(session,
                menu ?? { id: session.id, projectId, hostEpoch: status?.hostEpoch ?? null },
                menu ? session.id : selectedSession === session && selectedSessionId.current === session.id ? sessionId : null,
                selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
                mutation?.capability.canMutate() ?? false, batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
                renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current);
              return <div className={`session-row${menu ? " menu-open" : ""}`} key={session.id} data-session-id={session.id}
                onContextMenu={event => {
                  const target = event.target as HTMLElement;
                  if (target.closest("input, textarea, select, [contenteditable='true'], .session-actions-menu")) return;
                  event.preventDefault();
                  openSessionMenu(session.id, event.currentTarget.querySelector<HTMLButtonElement>(".session-actions-trigger"));
                }}
                onKeyDown={event => {
                  const editing = !!(event.target as HTMLElement).closest("input, textarea, select, [contenteditable='true']");
                  const composing = event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229;
                  // Delete on a row does what Delete of its menu does.
                  if (isSessionDeleteKey(event, composing) && event.target === event.currentTarget.firstElementChild) {
                    event.preventDefault(); event.stopPropagation();
                    deleteSessionFromRow(session);
                    return;
                  }
                  if (!isSessionContextKey(event.key, event.shiftKey, composing, editing)) return;
                  event.preventDefault(); event.stopPropagation();
                  openSessionMenu(session.id, event.currentTarget.querySelector<HTMLButtonElement>(".session-actions-trigger"));
                }}>
              <button type="button" aria-pressed={sessionId === session.id} aria-expanded={twist && !twist.collapsed} aria-describedby={`session-tooltip-${index}`} title={tooltip}
                style={{ paddingLeft: sessionRowIndent(depth) }} onKeyDown={event => sessionTwistKey(event, twist)}
                onClick={() => { setMenuTarget(null); selectedSessionId.current = session.id; setSessionId(session.id); setRenamingId(null); setRenamingMessage(""); setDeletingId(null); setDeletingMessage(""); }}>
                <SessionRowTitle session={session} depth={depth} diagnostic={diagnostic} subAgents={subAgents} twist={twist} />{sessionMarks(session, projectId)}
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
                  { key: "delete", label: confirms.sessionDelete ? `${t("Delete")}…` : t("Delete"), icon: "trash", danger: true, disabled: !access.delete, onSelect: () => runSessionMenuAction("delete", session, menu) },
                  ...sessionCanvasEntries(session),
                  ...sessionMenuPlugins,
                ]} />}
              {renamingId === session.id && <RenamePopover label={t("Session title")} value={renamingTitle} onChange={setRenamingTitle}
                busy={renamingBusy} disabled={!owned || renameLocked} error={renamingMessage ? workflowNotice(language.locale, renamingMessage) : null}
                onSubmit={() => void renameSelectedSession()} onCancel={() => setRenamingId(null)} />}
              {deletingId === session.id && <ConfirmPopover title={t("Delete this session?")} subject={plainTitle(session.title)}
                detail={t("Its history is deleted and cannot be restored. The files of the project are kept.")}
                confirmLabel={t("Delete")} intent="danger" busy={deletingBusy} error={deletingMessage || null}
                onConfirm={remember => { if (remember) setConfirm("sessionDelete", false); void deleteSessionRow(session, true); }}
                onCancel={() => { setDeletingId(null); setDeletingMessage(""); }} />}
            </div>;
            })}
            {snapshot && visibleSessions.length === 0 && <div className="sidebar-empty">{t(projectId === null ? "No chats." : "No sessions in this project.")}</div>}
            <div className="session-list-disclosure">
              {selectedList.hidden > 0 && <button type="button" className="quiet-button" onClick={() => setSessionExtra(projectId, extraSessions + recentSessionCount)}>{t("Show more…")} <span className="muted-text">({selectedList.hidden})</span></button>}
              {extraSessions > 0 && <button type="button" className="quiet-button" onClick={() => setSessionExtra(projectId, 0)}>{t("Show fewer")}</button>}
            </div>
          </div>
        </aside>}
          content={<ProjectReferenceContext.Provider value={selectedReference}><main className="content">
          <SessionTabStrip key={spaceId} layout={spaceLayout(spaceId)} state={sessionTabPresentation(tabs, snapshot, projectId, sessionId)} snapshot={snapshot}
            newSessionLabel={selectedProject ? t("New session — {project}", { project: selectedProject.name }) : t("New chat")}
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
                ? owners.reference({ expectedEpoch: status.hostEpoch, projectId: tab.projectId, projectPath: tab.path!, sessionId: row.id,
                  lifetime: creationGeneration.current, capturePopup: captureReferencePopup, observe: observeReference }) : null}>
              <SessionWorkspace session={row} snapshot={snapshot} selectedProjectId={tab.projectId}
                origin={row.automationId ? (() => {
                  const origin = sessionOrigin(row.id, row.automationId, automationState.items, automationState.runs, t);
                  return <SessionOrigin name={origin.name} summary={origin.summary} onOpen={owned ? () => openAutomations(origin.id) : undefined} />;
                })() : undefined}
                workCards={owned && workState.settings.notify ? <WorkItemCards hub={workHub} items={sessionCards(work, row.id)} preferredStart={workState.settings.start} busy={workBusy}
                  onStart={(item, start) => void startWork(item, start, { id: row.id, workingDirectory: row.workspacePath })}
                  onOpenList={item => openWorkItems({ projectId: item.projectId, key: workItemKey(item) })} /> : undefined}
                onRunActivity={(running, background) => { runtimeObservations.setLive(tab, running, background); noteRunActivity(tab.sessionId, running); }} notesReader={owners.notesReader} observing={visible && view === "workspace" && !settingsOpen}
                active={tab.sessionId === sessionId} notesToggle={notesVisible} onActivate={() => { if (sessionId !== tab.sessionId || fileTabs.active) selectSessionTab(tab); }}
                infoTrigger={sessionInfoTrigger} remindersTrigger={remindersTrigger} compactTrigger={compactTrigger}
                infoLifetime={{ revision: 0, current: () => !!currentSnapshot.current && !!resolveSessionTab(currentSnapshot.current, tab)
                  && currentView.current === "workspace" && !settingsVisible.current && currentHostEpoch.current === status?.hostEpoch }}
                preferredComposerHeight={composerHeights.get(composerSizeKey(status?.hostEpoch ?? null, tab.projectId, row.id))}
                onComposerHeight={height => setComposerHeights(sizes => rememberComposerHeight(sizes, composerSizeKey(status?.hostEpoch ?? null, tab.projectId, row.id), height))}
                onOpenCatalog={navigate} onOpenReminders={openSelectedReminders} onOpenHelp={openHelp} onOpenCommands={openCommandSearch}
                readReminders={readReminders} reminderActions={reminderActions} status={status} mutation={mutation}
                activeReminderCount={activeReminders ? activeReminders.get(row.id) ?? 0 : null}
                autoSend={autoSend.current?.sessionId === row.id ? { text: autoSend.current.text, consume: () => { autoSend.current = null; } } : null}
                submissions={submissions} timelineImages={timelineImages} toolRecords={toolRecords} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} draftIndicators={draftIndicators}
                askActions={askActions} display={owners.display} scrollMemory={scrollMemory} runtimeReader={owners.runtimeReader}
                permissionReviewer={owners.permissionReviewer} inputReviewer={owners.inputReviewer} configuration={configurationState.snapshot}
                selections={nextSendSelections} timelineCommand={timelineCommand} /></ProjectReferenceContext.Provider>;
            }}
            capture={captureTabLifetime} drafts={tabDrafts} observations={runtimeObservationControls()} waiting={waitingSessions}
            onSessionTabClick={focusPromptSoon}
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
            files={fileTabs} fileDirty={tab => fileEditors.dirty(fileTabKey(tab))} fileStatus={tab => canvasStatuses.get(canvasStatusKey(tab, spaceId))} selectFile={activateFile} closeFile={tab => closeFile(tab)}
            terminal={id => terminalList.find(terminal => terminal.id === id)}
            renderFile={(tab, visible) => isCanvasTab(tab)
              ? <LandingShellContext.Provider key={fileTabKey(tab)} value={landingShell}><CanvasPanel tab={tab} spaceId={spaceId} hub={canvasHub} visible={visible && view === "workspace" && !settingsOpen}
                active={visible && sameFileTab(fileTabs.active, tab)} onActivate={() => activateFile(tab)} onLook={look => changeCanvasLook(tab, spaceId, look)}
                onInstance={instance => instance ? canvasInstances.opened(tab, spaceId, instance) : canvasInstances.released(tab, spaceId)}
                onAbandoned={(instance, space) => canvasInstances.abandoned(tab, space ?? spaceId, instance)}
                onClose={() => closeFile(tab)} control={canvasControl(tab)}
                onOpenSource={folder => openPluginEditor(folder, { path: "plugin.cs", line: null, column: null, explorer: true })} /></LandingShellContext.Provider>
              : isCanvasesTab(tab)
              ? <CanvasesPanel key={fileTabKey(tab)} hub={canvasHub} tabs={fileTabs.open} projects={canvasProjects} sessions={canvasSessions} selection={canvasSelection}
                visible={visible && view === "workspace" && !settingsOpen} onActivate={() => activateFile(tab)} onOpen={openCanvas} onNew={owned && canvasSelection.project ? newCanvas : null} />
              : isIssuesTab(tab)
              ? <IssuesPanel key={fileTabKey(tab)} api={issuesApi} epoch={!status ? undefined : owned ? status.hostEpoch : null}
                projects={snapshot?.projects.filter(project => !project.archived) ?? []} projectId={selectedProject && !selectedProject.archived ? selectedProject.id : null}
                visible={visible && view === "workspace" && !settingsOpen} preferredStart={workState.settings.start} onActivate={() => activateFile(tab)}
                onOpenSession={id => void openAutomationSession(id)} onNotice={message => showToast({ message, intent: "danger", icon: "error", timeout: 8000 })} />
              : isWorkItemsTab(tab)
              ? <WorkItemsPanel key={fileTabKey(tab)} hub={workHub} projects={snapshot?.projects.filter(project => !project.archived) ?? []} sessions={snapshot?.sessions ?? []}
                runningSessions={runningSessionIds} projectId={selectedProject && !selectedProject.archived ? selectedProject.id : null}
                visible={visible && view === "workspace" && !settingsOpen} focus={workFocus} onFocused={() => setWorkFocus(null)}
                onActivate={() => activateFile(tab)} onStart={(item, start, runsWith) => startWork(item, start, null, runsWith)} providers={runProviders} loadModels={loadRunModels}
                onOpenSession={id => void openAutomationSession(id)}
                onOpenFile={(id, file) => { const project = snapshot?.projects.find(candidate => candidate.id === id); if (project) openEditor(project, { path: file, line: null, column: null, explorer: null }); }}
                onOpenSettings={() => navigate("workItems")} />
              : isAutomationsTab(tab)
              // The automations are the application's, whatever the space shown: each names its project among all of them.
              ? <AutomationsPanel key={fileTabKey(tab)} hub={automationsHub} projects={(catalog.current ?? snapshot)?.projects.filter(project => !project.archived) ?? []} sessions={(catalog.current ?? snapshot)?.sessions ?? []}
                projectId={selectedProject && !selectedProject.archived ? selectedProject.id : null} epoch={!status ? undefined : owned ? status.hostEpoch : null}
                providers={configurationState.snapshot?.providerRuntimeAvailable ? [...configurationState.snapshot.providers].filter(provider => provider.enabled).sort((a, b) => Number(b.isDefault) - Number(a.isDefault)) : []}
                visible={visible && view === "workspace" && !settingsOpen} focus={automationFocus} onFocused={() => setAutomationFocus(null)}
                onActivate={() => activateFile(tab)} onOpenSession={id => void openAutomationSession(id)} />
              : isTerminalTab(tab)
              ? <TerminalPanel key={fileTabKey(tab)} workspace={terminalWorkspace} id={tab.terminalId ?? ""} terminal={terminalList.find(terminal => terminal.id === tab.terminalId)}
                visible={visible && view === "workspace" && !settingsOpen} active={visible && sameFileTab(fileTabs.active, tab)} look={terminalLook} onLook={changeTerminalLook}
                onActivate={() => activateFile(tab)} rename={terminalWorkspace.hub.rename} onCloseTerminal={() => terminalWorkspace.hub.close(tab.terminalId ?? "")}
                onCreate={() => { const shown = terminalList.find(terminal => terminal.id === tab.terminalId); void createTerminal(shown?.projectId ?? (tab.projectId || null), shown?.sessionId ?? null); }} />
              : isChangesTab(tab)
              ? <ProjectChangesPanel key={fileTabKey(tab)} tab={tab} epoch={!status ? undefined : owned ? status.hostEpoch : null}
                projectName={snapshot?.projects.find(project => project.id === tab.projectId)?.name} request={changeRequests.get(tab.projectId)}
                sessions={snapshot?.sessions} onWorktreesChanged={refreshSessionList}
                visible={visible && view === "workspace" && !settingsOpen} active={visible && sameFileTab(fileTabs.active, tab)} onActivate={() => activateFile(tab)}
                onOpenFile={path => openEditor({ id: tab.projectId, path: tab.projectPath }, { path, line: null, column: null, explorer: null })} />
              : <ProjectEditor key={fileTabKey(tab)} tab={tab} editors={fileEditors} request={editorRequests.get(tab.projectId)}
              projectName={tab.name ?? snapshot?.projects.find(project => project.id === tab.projectId)?.name} platform={shellPreferences?.platform ?? "windows"}
              epoch={!status ? undefined : owned ? status.hostEpoch : null} onPickFile={isFolderTab(tab) ? undefined : openFilePicker}
              visible={visible && view === "workspace" && !settingsOpen} active={visible && sameFileTab(fileTabs.active, tab)} onActivate={() => activateFile(tab)} />}>
          <div id="active-session-content" className="active-session-content">
          {error && <div className="banner banner-error" role="alert">{error}</div>}
          {draftHandoffNotice && <p role="status" className="notice">{draftHandoffNotice}</p>}
          {!selectedSession
            ? <NewSessionWorkspace key={draftScope} project={selectedProject} chrome={draftChrome} worktree={!!draftPlaceControl && draftPlace.worktree}
                preferredHeight={composerHeights.get(composerSizeKey(status?.hostEpoch ?? null, projectId, draftScope))}
                onHeight={height => setComposerHeights(sizes => rememberComposerHeight(sizes, composerSizeKey(status?.hostEpoch ?? null, projectId, draftScope), height))}>
                <ReadOnlyComposer key={draftScope} sessionId={draftScope} provider={null} draftIndicators={draftIndicators}
                  localImages={owned && snapshot?.configured && currentProjectWritable() ? localImages : undefined}
                  onOpenHelp={openHelp} onOpenCommands={openCommandSearch}
                  reason={t("Draft kept locally. Start a session to send it.")}
                  localDraft={{ read: () => localDrafts.peek(draftScope)?.text ?? "", subscribe: localDrafts.subscribe, edit: editLocalDraft, options: creationProviderChoice(true),
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
            // A listed session whose recorded project or folder is not the one it is listed under has no tab.
            : snapshot && !selectedTab(snapshot, projectId, sessionId)
              ? <NonIdealState className="session-unavailable" icon={<AppIcon name="error" size={32} />} title={t("Session unavailable")}
                  description={t("This session was recorded for another project or folder than the one it is listed under.")} />
            : null}
          </div></SessionTabStrip></main></ProjectReferenceContext.Provider>} />
      </div>
    {settingsOpen && <SettingsOverlay section={settingsSection} onSection={navigate} onClose={closeSettings}>
      {settingsSection === "appearance" ? <ConfigurationPanel preferences={{ theme, setTheme, darker, setDarker,
        schemes: { colorScheme, setColorScheme, shownScheme, variant, customSchemes, library: schemeLibrary, preview: appearancePreview, platform: demoMode ? null : shellPreferences?.platform ?? null,
          onOpenFolder: owned ? openColorSchemeFolder : undefined }, sort: projectSort, setSort: setProjectSort, desktopCollapsed: railState.desktopCollapsed, setDesktopCollapsed, notices: preferenceNotices, recentSessionCount, setRecentSessionCount: value => { batchDeletion.invalidate(); setRecentSessionCount(value); }, subAgentCount, setSubAgentCount,
        sessionWidth, setSessionWidth, confirms: { ...confirms, set: setConfirm },
        closing: shellPreferences?.canKeepRunning ? { behavior: closeBehavior(shellPreferences.onClose), platform: shellPreferences.platform, trayIcon: shellPreferences.trayIcon, set: setOnClose } : null }} />
      : settingsSection === "permissions" ? <PermissionSettings
        permissions={owned && shellPreferences ? { review: shellPreferences.reviewPermissions, set: setReviewPermissions,
          inherit: shellPreferences.inheritPermissions, setInherit: setInheritPermissions } : null} />
      : settingsSection === "spaces" ? <SpaceSettings hub={spacesHub} spaces={spacesState.spaces} projects={catalog.current?.projects ?? []} shownId={spaceId}
        activity={spacesState.activity} canEdit={owned && !!mutation?.capability.canMutate()} onShow={id => { showSpace(id); }} onCreate={() => setSpaceDialog(true)} />
      : settingsSection === "about" ? <AboutSettings status={status} bootError={!!error} demo={demoMode} logo={logoUrl}
        update={owned ? appUpdateResult : undefined} onOpenReleaseNotes={openReleaseNotes} onInstallUpdate={installUpdate} />
      : settingsSection === "plugins" ? <PluginSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} revision={pluginRevision} canvases={canvasCatalog}
        onEdit={owned ? folder => { closeSettings(); openPluginEditor(folder, { path: "plugin.cs", line: null, column: null, explorer: true }); } : undefined}
        onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "worktrees" ? <WorktreeSettings epoch={owned ? status!.hostEpoch : null}
        pick={owned ? initial => pickFolder(desktopShell.pickFolder, t("Folder for worktrees"), initial) : undefined}
        onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "workItems" ? <WorkItemSettings hub={workHub} providers={runProviders} loadModels={loadRunModels} onOpenProviders={() => navigate("providers")}
        epoch={owned ? status!.hostEpoch : null} onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "pullRequests" ? <PullRequestSettings api={pullRequestPrompts} epoch={!status ? undefined : owned ? status.hostEpoch : null}
          project={selectedProject && !selectedProject.archived ? { id: selectedProject.id, name: selectedProject.name } : null}
          onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "mcpHost" ? <McpHostSettings epoch={owned ? status!.hostEpoch : null} developer={status?.developerMode ?? false} />
      : settingsSection === "skills" ? <SkillSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject}
        onEdit={owned ? folder => { closeSettings(); openSkillEditor(folder); } : undefined} onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "mcp" ? <McpServerSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "prompts" ? <AgentPromptSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "config" ? <ConfigEditorPanel epoch={owned ? status!.hostEpoch : null} project={settingsProject} onApplied={() => void refreshConfiguration()}
        onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "logs" ? <>
        <ApplicationLogsPanel clearActions={logClearActions} read={demoMode
          ? async () => ({ status: "unavailable", rows: [], captureOmitted: "0", readOmitted: 0, captureId: null, boundary: "0", grant: "" }) : applicationLogs.read} /></>
      : settingsSection === "providers" ? owned && status?.hostEpoch
        ? <ProviderSettings epoch={status.hostEpoch} readRuntime={modelCatalog.providers} probe={modelCatalog.probe}
          onOpenModels={() => navigate("models")} onOpenConfiguration={() => navigate("config")} onApplied={() => void refreshConfiguration()}
          onOpenFile={closeSettings} guide={providerGuide} onGuideClosed={() => setProviderGuide(false)} />
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
      initialFolder={projectFolder ?? undefined} pickFolder={canPickFolder() ? browseForFolder : undefined}
      epoch={owned ? status?.hostEpoch : undefined}
      capability={owned ? mutation?.capability : undefined} opening={projectOpening}
      allowCompletion={!demoMode}
      getCurrentEpoch={() => owned ? currentHostEpoch.current ?? undefined : undefined}
      getCurrentScope={() => ({ projectId: selectedScope.current, sessionId: selectedSessionId.current })}
      completeDirectory={workspace.completeDirectory}
      onOpen={shown => {
        if (projectOpening.getSnapshot() || currentSnapshot.current !== snapshot || !savedProjectSelection(shown, currentSnapshot.current)) return false;
        selectProject(shown.id); setDialog(null); focusPromptSoon(); return true;
      }} onRefresh={refreshProjects}
      onImported={async (id, path, signal) => {
        const original = projectOpening.getSnapshot();
        const previousScope = selectedScope.current;
        const previousSession = selectedSessionId.current;
        if (original?.kind !== "imported" || original.epoch !== currentHostEpoch.current ||
          original.path !== path || original.projectId !== id) return false;
        // A project opened while a space is shown joins that space.
        if (shownSpace.current !== defaultSpaceId) await spacesHub.assign(id, [shownSpace.current]);
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
        focusPromptSoon();
        return true;
      }} onClose={() => setDialog(null)} />}
    {spaceDialog && <SpaceDialog spaces={spacesState.spaces} projects={catalog.current?.projects ?? []} create={spacesHub.create}
      // A space made from Settings is organized there; one made from the title bar is shown at once.
      onCreated={space => { setSpaceDialog(false); if (!settingsVisible.current) showSpace(space.id); }} onClose={() => setSpaceDialog(false)} />}
    {leavingSpace && <UnsavedFileDialog name={leavingSpace.tabs.flatMap(tab => fileEditors.unsaved(fileTabKey(tab))).join(", ")} mode="close" busy={leavingSpace.busy}
      onSave={() => void saveAndLeaveSpace(leavingSpace)} onDiscard={() => showSpace(leavingSpace.spaceId, leavingSpace.target, true)}
      onCancel={() => { if (!leavingSpace.busy) setLeavingSpace(null); }} />}
    {dialog === "help" && <CommandHelp onClose={closeHelp} pluginCommands={pluginContributed.commands} />}
    {dialog === "worktrees" && worktreesProject && owned && status?.hostEpoch && <WorktreeManager epoch={status.hostEpoch} project={worktreesProject}
      onClose={() => setDialog(null)} onChanged={refreshSessionList}
      onShowChanges={row => { const project = worktreesProject; setDialog(null); showChanges(project, null, row.project ? null : row.folder); }} />}
    {dialog === "file" && filePickerProject && <OpenFileDialog epoch={status!.hostEpoch!} project={filePickerProject}
      observe={value => mutation?.capability.observe(value)} onClose={() => setDialog(null)}
      // A file picked for a project whose editor is not open opens it on that file alone, without the files of the project.
      onOpen={path => { setDialog(null); openEditor(filePickerProject, { path, line: null, column: null, explorer: false }); }}
      onOpenEditor={() => { setDialog(null); openProjectEditor(filePickerProject); }} />}
    {/* A first start also opens the settings of the providers, with their own guide: this one waits for them to close. */}
    {entryGuide && !settingsOpen && <EntryAddedDialog onClose={() => setEntryGuide(false)}
      onShowInFinder={() => { void desktopShell.revealEntry({}, { timeoutMilliseconds: 15_000 }).catch(() => { /* The folder is named in the dialog. */ }); }} />}
    {closeQuestion && <CloseWindowDialog platform={shellPreferences?.platform ?? "windows"} trayIcon={shellPreferences?.trayIcon} onKeepRunning={keepRunning} onExit={exitOnClose}
      onCancel={() => setCloseQuestion(false)} />}
    {exiting && <UnsavedExitDialog names={exiting.tabs.flatMap(tab => fileEditors.unsaved(fileTabKey(tab)))} busy={exiting.busy}
      onSave={() => void saveAllAndExit(exiting.tabs)} onDiscard={() => { setExiting(null); quitApplication(); }}
      onCancel={() => { if (!exiting.busy) { setExiting(null); exitCanceled(); } }} />}
    {exitQuestionFor !== null && <RunningExitDialog runningSessions={exitQuestionFor.sessions} busyTerminals={exitQuestionFor.terminals} onCancel={() => { setExitQuestionFor(null); exitCanceled(); }}
      onExit={() => { setExitQuestionFor(null); quitApplication(true); }} />}
    {fileClosing && <UnsavedFileDialog name={fileEditors.unsaved(fileTabKey(fileClosing.tab)).join(", ")} mode="close" busy={fileClosing.busy}
      onSave={() => void saveAndCloseFile(fileClosing.tab)} onDiscard={() => closeFile(fileClosing.tab, true)}
      onCancel={() => { if (!fileClosing.busy) setFileClosing(null); }} />}
    {dialog === "sessions" && browserCapture && <SessionBrowser snapshot={browserCapture.snapshot} projectId={browserCapture.projectId} observations={runtimeObservationControls()} recentCount={recentSessionCount} activeSessionId={sessionId} batch={batchDeleteControls()}
      stale={browserCapture.revision !== browserRevision.current || browserCapture.hostReady !== (mutation?.capability.canMutate() ?? false) || view !== "workspace" || settingsOpen}
      close={() => setDialog(null)} open={tab => {
        if (view !== "workspace" || settingsVisible.current || browserCapture.hostReady !== (mutation?.capability.canMutate() ?? false)
          || !browserActivation(currentSnapshot.current, tab, browserCapture.revision, browserRevision.current)) return false;
        selectSessionTab(tab); setDialog(null); return true;
      }} />}
    {searchStart && <GlobalSearch snapshot={snapshot ?? null} favorites={projectTree.favorites} start={searchStart} files={searchedFiles()} note={notice}
      available={commandAvailable} onCommand={id => chooseSearch({ kind: "command", id })}
      pluginCommands={pluginContributed.commands} onPluginCommand={id => chooseSearch({ kind: "plugin", id })}
      canvases={owned ? canvasCatalog : []} canvasAvailable={item => !!defaultCanvasTarget(item, canvasSelection)}
      onCanvas={item => chooseSearch({ kind: "canvas", pluginKey: item.pluginKey, id: item.id })}
      onProject={project => chooseSearch({ kind: "project", id: project.id })}
      onSession={session => chooseSearch({ kind: "session", projectId: session.projectId, id: session.id })}
      onFile={(project, path) => chooseSearch({ kind: "file", project, path })}
      onClose={dismissSearch} />}
    <PluginUiHost epoch={pluginEpoch}
      onPrompt={request => ["send", "enqueue", "steer", "compact"].includes(request.mode ?? "")
        && askPluginComposer(request.mode as PluginComposerRequest["kind"], request.sessionId ?? null, request.text ?? null).result}
      onDraft={request => { askPluginComposer("draft", request.sessionId ?? null, request.text ?? ""); }} />
  </div></SessionListRefreshContext.Provider></OpenTerminalContext.Provider></ShowChangesContext.Provider></SessionWidthContext.Provider></MessageLinksContext.Provider></SessionLinksContext.Provider></PullRequestSettingsContext.Provider></PluginButtonsActiveContext.Provider></PluginButtonsContext.Provider></PluginHostBridgeContext.Provider></PluginUiContext.Provider></ProviderBrandsContext.Provider></ShellLanguageContext.Provider>;
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
    ["Personalization", [["appearance", "Appearance", "palette"], ["spaces", "Spaces", "space"]]],
    ["Agent & models", [["providers", "Providers", "provider"], ["models", "Models", "model"], ["prompts", "Agent prompts", "assistant"], ["skills", "Skills", "skill"],
      ["permissions", "Permissions", "shield"],
      ["worktrees", "Worktrees", "worktree"], ["workItems", "Work items", "task"], ["pullRequests", "Pull requests", "pullRequest"]]],
    ["Extensions", [["plugins", "Plugins", "plugin"], ["mcp", "MCP Servers", "server"]]],
    ["Advanced", [["config", "Configuration file", "config"], ["mcpHost", "CodeAlta MCP", "remote"]]],
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

type ReferenceScope = NonNullable<ContextType<typeof ProjectReferenceContext>>;
function createSessionPaneOwners() {
  let reference: ReferenceScope | undefined;
  return {
    /** The scope the reference pickers of the pane read: the same object while its fields are the same. */
    reference(next: ReferenceScope): ReferenceScope {
      const same = reference && (Object.keys(next) as (keyof ReferenceScope)[]).every(key => reference![key] === next[key])
        && Object.keys(reference).length === Object.keys(next).length;
      return same ? reference! : reference = next;
    },
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

// The working folder shown beside a composer (global sessions have none) and the plugin status items above
// it, which are those of the composer's session once it has one.
// The part of an open ask (its questions, its file) in the session tab that is shown.
function visibleAsk(selector: string): HTMLElement | null {
  return Array.from(document.querySelectorAll<HTMLElement>(selector)).find(element => element.offsetParent !== null) ?? null;
}

/**
 * Opens the changes tab of a project, on the folder of the project or on the worktree a session works in;
 * null where the window has no host to read them from.
 */
const ShowChangesContext = createContext<((project: Readonly<{ id: string; path: string }>, worktree?: string | null) => void) | null>(null);

/** Reads the list of sessions again: what a session records, such as its worktree, changed outside the window. */
const SessionListRefreshContext = createContext<(() => void) | null>(null);

/** The mark of a session that works in a git worktree, in the lists of sessions. */
function WorktreeBadge({ session }: { session: WorkspaceSession }) {
  const { t } = useShellLanguage();
  const worktree = sessionWorktree(session);
  if (!worktree) return null;
  return <span className="session-worktree" data-missing={worktree.missing || undefined} role="img"
    aria-label={worktree.missing ? t("Worktree {name} (removed)", { name: worktree.name }) : t("Worktree {name}", { name: worktree.name })}
    title={`${worktree.missing ? t("Worktree {name} (removed)", { name: worktree.name }) : t("Worktree {name}", { name: worktree.name })}\n${worktree.path}`}>
    <AppIcon name="worktree" size={12} /></span>;
}

/** Opens a terminal in the folder of a session, or of a project without one; null where the window has no host. */
const OpenTerminalContext = createContext<((projectId: string | null, sessionId: string | null) => void) | null>(null);
/** Opens the settings of the instructions for a pull request; null where the window has none. */
const PullRequestSettingsContext = createContext<(() => void) | null>(null);

function useComposerChrome(epoch: string | null, project: WorkspaceSnapshot["projects"][number] | undefined, sessionId: string | null = null,
  /** The way to the changes tab for a composer the application builds itself, above the context. */
  show: ((project: Readonly<{ id: string; path: string }>, worktree?: string | null) => void) | null = null,
  /** The git worktree the session works in, as its row records it. */
  session: WorkspaceSession | null = null,
  /** Before a session exists: where it will work. */
  place?: Parameters<typeof ProjectContext>[0]["place"]): ComposerChromeValue {
  const id = project?.id, name = project?.name, path = project?.path, archived = project?.archived;
  // The regions are read only when a plugin has content for them.
  const regions = useContext(PluginUiContext).contributions.regions;
  const showChanges = useContext(ShowChangesContext) ?? show;
  const openTerminal = useContext(OpenTerminalContext);
  const refreshSessions = useContext(SessionListRefreshContext);
  const openPullRequestSettings = useContext(PullRequestSettingsContext);
  const worktreePath = session?.worktreePath ?? null, worktreeName = session?.worktreeName ?? null, worktreeMissing = session?.worktreeMissing === true;
  return useMemo(() => {
    const worktree = sessionWorktree({ worktreePath, worktreeName, worktreeRoot: null, worktreeMissing });
    // The changes of where the session works: its worktree while the folder is there.
    const shown = worktree && !worktree.missing ? worktree.path : null;
    return ({ context: id !== undefined && name !== undefined && path !== undefined
    ? <ProjectContext epoch={epoch} project={{ id, name, path }} read={projectGit.status} worktree={worktree} place={place}
      onWorktreeGone={refreshSessions ?? undefined} branches={!!showChanges && !archived}
      onShowChanges={showChanges && !archived ? () => showChanges({ id, path }, shown) : undefined}
      onOpenTerminal={openTerminal && !archived ? () => openTerminal(id, sessionId) : undefined}
      // A session that exists can be asked for a pull request of its work; a draft has no work yet.
      pullRequest={epoch && sessionId && !place && !archived ? <PullRequestButton api={pullRequestPrompts} epoch={epoch} projectId={id} sessionId={sessionId}
        onOpenSettings={openPullRequestSettings ?? undefined} onNotice={message => showToast({ message, intent: "warning", icon: "warning-sign", timeout: 6000 })} /> : undefined} /> : undefined,
  status: epoch ? <>{regions && <PluginRegionSlot epoch={epoch} projectId={id ?? null} sessionId={sessionId} region="inline" read={pluginUi.regions} />}
    <ComposerStatus epoch={epoch} projectId={id ?? null} sessionId={sessionId} read={composerStatus.read} /></> : undefined,
  footer: epoch && regions ? <PluginRegionSlot epoch={epoch} projectId={id ?? null} sessionId={sessionId} region="footer" read={pluginUi.regions} /> : undefined });
  }, [epoch, id, name, path, archived, sessionId, regions, showChanges, openTerminal, refreshSessions, openPullRequestSettings, worktreePath, worktreeName, worktreeMissing, place]);
}

function SessionWorkspace({ session, snapshot, selectedProjectId, preferredComposerHeight, onComposerHeight, infoTrigger: sharedInfoTrigger, infoLifetime, remindersTrigger: sharedRemindersTrigger, compactTrigger: sharedCompactTrigger, onOpenReminders, onOpenHelp, onOpenCommands, readReminders, reminderActions, status, mutation, submissions, timelineImages, toolRecords, steering, compaction, abortRuns, queue, draftIndicators, askActions, display, scrollMemory, runtimeReader, permissionReviewer, inputReviewer, configuration: configurationSnapshot, selections, timelineCommand, onOpenCatalog, active = true, observing = true, notesToggle, onActivate, notesReader, activeReminderCount = null, autoSend = null, onRunActivity, origin, workCards }: {
  /** Reports whether the session is working while its panel watches it, and how many tasks go on in its background. */
  onRunActivity?: (running: boolean | null, background?: number) => void;
  /** A draft prompt to send once this session's composer holds it. */
  autoSend?: { text: string; consume: () => void } | null;
  /** What started the session when it was not the user, shown above its timeline. */
  origin?: ReactNode;
  /** The work items the session proposes, shown over the top right corner of its timeline. */
  workCards?: ReactNode;
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
  onOpenCommands: () => void;
  onOpenCatalog: (page: "models" | "prompts" | "providers") => void;
  readReminders: (request: ReminderListRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ReminderListResponse>;
  reminderActions: ReturnType<typeof createReminderActions>;
  compactTrigger: RefObject<HTMLButtonElement | null>;
  status: BootStatus | undefined;
  mutation: { epoch: string; capability: ReturnType<typeof createMutationCapability> } | undefined;
  submissions: ReturnType<typeof createOwnedSubmissions>;
  timelineImages: ReturnType<typeof createTimelineImageCache>;
  toolRecords: { records: ReturnType<typeof createToolCallCache>; outputs: ReturnType<typeof createToolOutputStore> };
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
  // An open ask takes the place of the prompt with its questions and, with a file to review, of the timeline.
  const [askMode, setAskMode] = useState<AskMode>("none");
  const [askFormSlot, setAskFormSlot] = useState<HTMLDivElement | null>(null);
  const [askFileSlot, setAskFileSlot] = useState<HTMLDivElement | null>(null);
  const [running, setRunning] = useState<boolean | null>(null);
  // The tool calls whose task goes on in the background, or ended there: their tiles say so.
  const [backgroundCallStates, setBackgroundCallStates] = useState<ReadonlyMap<string, BackgroundCallState>>(() => new Map());
  const [timelineNotices, setTimelineNotices] = useState<HTMLDivElement | null>(null);
  const [infoFocusRestoration] = useState(createPaletteFocusRestoration);
  useEffect(() => () => infoFocusRestoration.cancel(), [infoFocusRestoration]);
  function openInfo() {
    if (infoActive.current || modalDialogOpen()) return;
    infoFocusRestoration.cancel();
    infoActive.current = true;
    setInfoOpen(true);
  }
  function closeInfo() {
    const trigger = infoTrigger.current;
    infoActive.current = false;
    setInfoOpen(false);
    infoFocusRestoration.schedule(trigger, () => !infoActive.current && infoTrigger.current === trigger && !trigger?.disabled,
      () => !!modalDialogOpen());
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
  // One function across renders: the history runs it when its window settles, not on each render of this panel
  // (a reader who left the end of the timeline had its position read from the layout every time).
  const settleHistory = useRef(() => { });
  settleHistory.current = () => { timeline.settled(); if (!newest.pending()) timeline.pauseIfUnfollowed(); };
  const onHistorySettled = useCallback(() => settleHistory.current(), []);
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
  const chrome = useComposerChrome(pluginEpoch, sessionProject, session.id, null, session);
  const readPluginEvents = useMemo(() => pluginEpoch === null ? undefined
    : createPluginEventsRead(sessionPluginEvents.read, { epoch: pluginEpoch, sessionId: session.id, projectId: selectedProjectId }),
  [pluginEpoch, session.id, selectedProjectId]);
  // Reading is not sending: the images of an archived project's session are shown too.
  const readImages = useMemo(() => pluginEpoch === null ? undefined : timelineImages.reader(pluginEpoch, session.id),
    [timelineImages, pluginEpoch, session.id]);
  const readTool = useMemo(() => pluginEpoch === null ? undefined : toolRecords.records.reader(pluginEpoch, session.id),
    [toolRecords, pluginEpoch, session.id]);
  const toolOutputs = useMemo(() => pluginEpoch === null ? undefined : toolRecords.outputs.session(pluginEpoch, session.id),
    [toolRecords, pluginEpoch, session.id]);
  const ownWidth = useSessionWidthStyle(session.id);
  const linkScope = useMemo<MarkdownLinkScope>(() => ({ sessionId: session.id }), [session.id]);
  const infoControl =<Button ref={infoTrigger} variant="minimal" className="session-info-trigger" icon={<AppIcon name="info" size={16} />}
    aria-label={t("Session info")} title={`${t("Session info")} (Ctrl+G, Ctrl+T)`} aria-haspopup="dialog" aria-expanded={infoOpen}
    onClick={openInfo} />;
  // A relative link of a message, of the notes or of a tool window of this session starts from the folder it works in.
  return <MarkdownLinkScopeContext.Provider value={linkScope}><BackgroundCallsContext.Provider value={backgroundCallStates}><div className="session-workspace" data-active={active} style={ownWidth} ref={composer.workspaceRef} onFocusCapture={onActivate} onPointerDownCapture={onActivate}>
    {infoOpen && <SessionInfoDialog info={sessionInfoView(snapshot, session, selectedProjectId)} demo={demoMode} onClose={closeInfo}
      lifetime={infoLifetime} canRead={() => !!mutation?.capability.canMutate()}
      target={ownedSession && !demoMode && mutation?.capability.canMutate() ? runtimeTarget(snapshot, { sessionId: session.id, projectId: selectedProjectId, path: session.workspacePath }, status?.hostEpoch ?? undefined) : null} />}
    {demoMode
      ? <DemoConversation session={session} />
      : <>
        {origin}
        <div className="session-timeline-area" data-ask={askMode}>
        <div className="ask-file-slot" ref={setAskFileSlot} hidden={askMode !== "file"} />
        <div className="timeline-scroll" ref={timeline.elementRef}
          onScroll={event => { newest.onScroll(); if (!newest.pending()) timeline.scroll(event.currentTarget); }}
          onWheel={event => { newest.cancel(); timeline.wheel(event); }} onKeyDown={timeline.keyDown}
          onPointerDown={event => { newest.cancel(); timeline.pointerDown(event); }}
          onPointerMove={timeline.pointerMove} onPointerUp={timeline.pointerEnd} onPointerCancel={timeline.pointerEnd}>
        <History observing={observing} sessionId={session.id} canInspect={() => infoLifetime.current()} onNotesChange={onNotesChange} onUsageChange={setPersistedUsage} onSettled={onHistorySettled}
          onBeforeOlder={timeline.beforeOlderPage} onAfterOlder={timeline.afterOlderPage} onNewerOmitted={setNewerOmitted}
          onNavigationReset={resetMessageNotice} newestRequest={newest.requestRef} onNewestResult={newest.onResult}
          read={readTimeline} readPluginEvents={readPluginEvents} readImages={readImages} readTool={readTool} toolOutputs={toolOutputs}
          outgoing={ownedSession && status?.hostEpoch ? submissions.outgoing(status.hostEpoch, session.id) : noOutgoing}
          onAcknowledgeOutgoing={submissions.acknowledgeOutgoing}
          live={ownedSession ? live?.snapshot?.session ?? null : null} />
        {ownedSession && status?.hostEpoch
        ? <>
          <LiveSessionPanel observing={observing} store={display} hostEpoch={status.hostEpoch} sessionId={session.id} capability={mutation!.capability} />
          <div className="timeline-notices" ref={setTimelineNotices} />
          {status.ownedAsksEnabled && <AskPanel observing={observing} epoch={status.hostEpoch} sessionId={session.id} actions={askActions} capability={mutation!.capability} refreshTrigger={askRefresh}
            projectId={selectedProjectId} idle={running !== true} formSlot={askFormSlot} fileSlot={askFileSlot} onMode={setAskMode} />}
          {status.ownedUserInputEnabled && <UserInputPanel epoch={status.hostEpoch} sessionId={session.id} reviewer={inputReviewer} capability={mutation!.capability}
            canReview={() => infoLifetime.current()} />}
        </>
        : null}
        </div>
        <SessionNotesOverlay observing={observing} sessionId={session.id} epoch={ownedSession ? status?.hostEpoch : undefined} capability={mutation?.capability}
          fallbackMarkdown={historyNotes} toggle={active ? notesToggle : undefined} reader={notesReader} />
        {workCards}
        </div>
        {!timeline.following && <button type="button" className="timeline-bottom-button" onClick={() => { newest.cancel(); timeline.jump(); }}><AppIcon name="arrowDown" size={14} />{t(newerOmitted ? "Bottom of retained window (not newest)" : "Jump to latest visible")}</button>}
        {/* Message navigation is announced to assistive technology only: nothing is written above the prompt. */}
        {messageNotice && <p role="status" className="sr-only">{timelineNotice(languageLocale, messageNotice)}</p>}
        <div className="composer-resize-bar" ref={composer.barRef} hidden={askMode !== "none"}>
          <ComposerSplitter {...composer.splitter} />
        </div>
        <div ref={composer.regionRef} className={`composer-region${composer.height === undefined || askMode !== "none" ? "" : " resized"}`} data-ask={askMode}
          style={composer.height === undefined || askMode !== "none" ? undefined : { height: composer.height }}>
        <SessionWidthGrips sessionId={session.id} />
        <div className="ask-form-slot" ref={setAskFormSlot} hidden={askMode === "none"} />
        <SessionComposerGate snapshot={snapshot} projectId={selectedProjectId} session={session} chrome={chrome}
          epoch={ownedHost ? status!.hostEpoch! : null}
          owned={status?.hostEpoch && mutation ? <OwnedSessionPanel observing={observing} active={active} onRunActivity={(value, background) => {
            setRunning(value);
            const calls = backgroundCalls(background ?? []);
            setBackgroundCallStates(current => current.size === calls.size && [...calls].every(([call, state]) => current.get(call) === state) ? current : calls);
            onRunActivity?.(value, runningBackgroundTasks(background ?? []).length);
          }} toolOutputs={toolOutputs} sessionId={session.id} epoch={status.hostEpoch} submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} capability={mutation.capability} runtimeReader={runtimeReader} permissionReviewer={status.commandReviewEnabled ? permissionReviewer : null} configuration={configurationSnapshot} draftIndicators={draftIndicators} selections={selections}
              persistedUsage={persistedUsage} usageTarget={ownedSession && verifiedReminderCountTarget(snapshot, session, selectedProjectId) ? {
                epoch: status.hostEpoch, sessionId: session.id, scope: selectedProjectId === null ? "global" : "project",
                projectId: selectedProjectId, expectedProjectPath: selectedProjectId === null ? null : session.workspacePath } : null}
              onOpenCatalog={onOpenCatalog} timelineNotices={timelineNotices} liveState={ownedSession ? live : null} inputLifetime={infoLifetime} remindersTrigger={remindersTrigger} compactTrigger={compactTrigger} infoControl={infoControl} projectId={selectedProjectId} onOpenReminders={() => onOpenReminders(session.id, status.hostEpoch!, selectedProjectId)} onOpenHelp={onOpenHelp} onOpenCommands={onOpenCommands}
              activeReminderCount={activeReminderCount} autoSend={autoSend} reminderActions={reminderActions} readReminderCount={ownedSession && verifiedReminderCountTarget(snapshot, session, selectedProjectId) ? readReminders : undefined} /> : null}
          readOnly={<ReadOnlyComposer active={active} sessionId={session.id} provider={session.providerKey} draftIndicators={draftIndicators} infoControl={infoControl} onOpenHelp={onOpenHelp} onOpenCommands={onOpenCommands}
              reason={archivedScope ? t("Archived project; this session is read-only. Sending is unavailable.") : undefined} />}
          recovery={ownedHost ? <ArchivedActionRecovery epoch={status!.hostEpoch!} sessionId={session.id} submissions={submissions}
            steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue}
            asks={askActions} inputs={inputReviewer} permissions={permissionReviewer} /> : null} />
        </div>
      </>}
  </div></BackgroundCallsContext.Provider></MarkdownLinkScopeContext.Provider>;
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
  // The list's clock ticks slowly: activity recorded since its last tick is "now", not in the future.
  const { label, title, dateTime } = sessionTime(value, locale, Math.max(now, Date.parse(value) || 0));
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
// The host says first what this window is for: the workspace, or the repair of a configuration file that
// cannot be loaded. The start-up screen stays until one of them has something to show.
function Root() {
  const [purpose, setPurpose] = useState<{ recovery: boolean; developer: boolean }>();
  useEffect(() => {
    const abort = new AbortController();
    void boot.status({}, { signal: abort.signal, timeoutMilliseconds: 8_000 }).then(
      value => { if (!abort.signal.aborted) setPurpose({ recovery: value.configRecovery, developer: value.developerMode }); },
      () => { if (!abort.signal.aborted) setPurpose({ recovery: false, developer: false }); }); // The workspace reports a bridge that does not answer.
    return () => abort.abort();
  }, []);
  return !purpose ? null : purpose.recovery ? <ConfigRecoveryScreen developer={purpose.developer} /> : <App />;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Root /></StrictMode>);
