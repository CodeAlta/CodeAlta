import { Button, Checkbox, Classes, FormGroup, HTMLSelect, InputGroup, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext, Radio, RadioGroup, type ToastProps } from "@blueprintjs/core";
import { connect, onDiagnostic } from "@neoastra/client";
import { rpcFailureCode } from "./rpcDiagnostics";
import { Fragment, StrictMode, createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type ContextType, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent, type RefObject, type ReactNode } from "react";
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
import { NewSessionPopover } from "./NewSessionPopover";
import { SessionStartProgress, type SessionStartStep } from "./SessionStartProgress";
import { CloseWindowDialog } from "./CloseWindowDialog";
import { closeBehavior, entryAddedGuide, entryAddedNotice, type CloseBehavior } from "./desktopShell";
import { EntryAddedDialog } from "./EntryAddedDialog";
import { showToast } from "./appToaster";
import { availableUpdate, installedNotice, runtimeNotification, updateCheckInterval, updateToAnnounce, updateNotification, UpdateToaster, type AvailableUpdate } from "./UpdateNotice";
import {
  boot, configuration, applicationLogs, modelCatalog, reminder, workspace as workspaceApi, spaces as spacesApi, labels as labelsApi, sessionDisplay, sessionRuntimeState, sessionPermissions, sessionOperations,
  sessionAsks, sessionNotes, sessionPluginEvents, projectFiles, projectGit, promptImages, toolCalls, composerStatus, pluginUi, sessionUserInput, conversationHeld, type BootStatus, type CanvasItem, canvases as canvasesApi, documentation as documentationApi, plugins as pluginsApi,
  type ReminderListRequest,
  type ReminderListResponse,
  type ReminderDetailRequest,
  type ConfigurationSnapshot, type WorkspaceClaudeCodeConversation, type WorkspaceMovedSession, type WorkspaceProject, type WorkspaceSession, type WorkspaceSnapshot,
  desktopShell, type DesktopShellPreferences, appUpdate, type AppUpdateResponse, terminals, type TerminalItem, automations, workflows as workflowsApi, workItems as workItemsApi, reviews as reviewsApi, issues as issuesApi, pullRequestPrompts, markdownLinks, settingsFiles,
  skills as skillsApi,
} from "#neoastra";
import { skillPrompt, usableSkills, type SkillChoice } from "./search/skillResults";
import { archivedSessions, loadWorkspace, sessionListSignature, sessionsForProject, withoutArchived, workspaceNotice, type WorkspaceState } from "./workspace";
import { History } from "./HistoryPanel";
import { BackgroundCallsContext } from "./BackgroundTaskViews";
import { backgroundCalls, runningBackgroundTasks, type BackgroundCallState } from "./backgroundTasks";
import { MessageLinksContext } from "./TimelineMessage";
import { markdownHrefKind, openMarkdownLink, type MarkdownLinkScope } from "./markdownLinks";
import { createMarkdownImageCache, markdownImageRequest, type MarkdownImages } from "./markdownImages";
import { MarkdownImagesContext, MarkdownLinkScopeContext } from "./MarkdownContent";
import { readTimeline } from "./readTimeline";
import { createTimelineImageCache } from "./timelineImages";
import { createToolCallCache } from "./toolCallReader";
import { createSystemPromptCache } from "./systemPromptReader";
import { SystemPromptDiffContext } from "./SystemPromptDialog";
import { SystemPromptDiff } from "./SystemPromptDiff";
import { createToolOutputStore } from "./toolOutput";
import { SessionContentLayout } from "./SessionContentLayout";
import { SessionTabStrip } from "./SessionTabStrip";
import { ActivityRail } from "./ActivityRail";
import { activeDockNodeId, createSessionTabModel, cycleDockTab, draftNodeId, sessionDraftNodeId, sessionNodeId, sessionTabPresentation } from "./sessionTabLayout";
import { createReferencePopupLifetime } from "./referencePopup";
import { SessionBrowser, type ArchivedSessionControls } from "./SavedSessionBrowser";
import { createRuntimeObservations, maximumRuntimeRows, runtimeTarget } from "./runtimeObservations";
import { archiveScopeCurrent, createProjectArchive, type ArchiveScope } from "./projectArchive";
import { createProjectDeletion, projectDeleteCurrent, type ProjectDeleteTarget } from "./projectDeletion";
import { browserActivation } from "./sessionBrowser";
import { backgroundSessionTabs, closeDraftTab, closePreviewTab, closeSessionTab, draftKey, draftTabOf, emptySessionTabs, hasDraftTab, openDraftTab, openSessionTab, persistSessionTabs, pinSessionTab, pruneSessionPaneOwners, reconcileSessionTabs, resolveSessionTab, restoreSessionTabs, selectedTab, sessionPaneKey, sessionTabsKey, singleSessionTab, tabKey,
  type DraftTab, type SessionTab, type SessionTabMode, type SessionTabs as SessionTabsState } from "./sessionTabs";
import { activateFileTab, claudeCodeTabProvider, automationsTab, isWorkflowsTab, workflowsTab, isWorkflowRunTab, workflowRunTab,bookmarksTab, canvasTab, canvasesTab, documentationTab, isBookmarksTab, isCanvasTab, isCanvasesTab, isDocumentationTab, refreshCanvasTab, isIssuesTab, issuesTab, isWorkItemsTab, workItemsTab, changesTab, closeFileTab, editorTab, emptyFileTabs, fileTabKey, isAutomationsTab, isChangesTab, isEditorTab, isFolderTab, isTerminalTab, fileTabsKey, openFileTab, persistFileTabs, pluginEditorTab, pluginFolderPrefix, diskEditorTab, diskFolderPrefix, reconcileFileTabs, reconcileTerminalTabs, reopenTabKind, resolveFileTab, restoreFileTabs, restoreLegacyFiles, sameFileTab, skillEditorTab, terminalTab, fileNodeId, type FileTab, type FileTabs, type TabKind, claudeCodeTab, dropClaudeCodeTab, isClaudeCodeTab, openClaudeCodeTab } from "./fileTabs";
import { closeSessionTabs, shownAfterClosing, type DockTab, type DockTabs } from "./sessionTabClosing";
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
import { PermissionModeSelect } from "./PermissionModeSelect";
import { permissionModeName } from "./permissionModes";
import { SessionSettings } from "./SessionSettings";
import { CodeModeSettings } from "./CodeModeSettings";
import { verifiedReminderCountTarget } from "./reminderListObservation";
import { applyCatalogNextSend, createNextSendSelectionStore } from "./nextSendSelection";
import { createMutationCapability, createOwnedSubmissions, noOutgoing } from "./sessionOperations";
import { createSessionDisplayStore } from "./sessionDisplay";
import { createRuntimeStateReader } from "./runtimeState";
import { createPermissionReviewer } from "./sessionPermissions";
import { ownPermissionChoice } from "./CommandPermissionPanel";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createComposerQueue } from "./composerQueue";
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
import { createDraftIndicators, createLocalDrafts, draftSendFacts, draftStorageKey, givePromptBack, persistDraft, restoreDraft, transferPromptDraft,
  type PromptBackComposer } from "./promptDraft";
import type { UnsentPrompt } from "./timeline";
import { SessionDraftStatus } from "./SessionDraftBadge";
import { composerSizeKey, rememberComposerHeight } from "./composerHeight";
import { ComposerSplitter, useComposerLayout } from "./ComposerLayout";
import { NewSessionWorkspace } from "./NewSessionWorkspace";
import { useNewSessionChoices } from "./newSessionChoices";
import { providersInSpace } from "./newSessionPreferences";
import { resourceSpacesOf } from "./spaces/SpaceChips";
import { ComposerModelPicker } from "./ComposerModelPicker";
import { ComposerSelectionFields, ReasoningSlider, SendSplitButton } from "./ComposerSurface";
import { ContextSizeSelect } from "./ContextSizeSelect";
import { ReviewBeforeCommitSelect } from "./ReviewBeforeCommitSelect";
import { validSelection } from "./sessionSelection";
import { AppIcon, type IconName } from "./AppIcon";
import { AppWindow } from "./AppWindow";
import { ConfigEditorPanel } from "./ConfigEditorPanel";
import { ProviderSettings } from "./ProviderSettings";
import { ClaudeCliFirstUse } from "./claudeCli/ClaudeCliFirstUse";
import { ClaudeCliEpochContext } from "./claudeCli/timelineCard";
import { sessionTime } from "./sessionTime";
import { createProjectOpening } from "./projectOpening";
import { spaceShowingProject, whenProjectKnown } from "./hostProjects";
import { OpenProjectDialog } from "./OpenProjectDialog";
import { pickFolder, sameFolder } from "./folderPicker";
import { attachEvent } from "./PromptImageAttachments";
import { savedProjectSelection } from "./savedProjectSelection";
import { createSessionCreation, createdSessionSelection, sessionCreationMessage, worktreeCreationMessage, type SessionTarget } from "./sessionCreation";
import { createSessionRename, renamedSessionVisible, renameSelectionCurrent, sessionRenameMessage, type RenameTarget } from "./sessionRename";
import { createSessionMove, movedSessionsVisible, retargetSessionTabs, sessionMoveMessage, splitPendingTabMoves } from "./sessionMove";
import { createCoalescedRead } from "./coalescedRead";
import { useSessionRowDrag, type ClaudeCodeConversationDrag, type SessionMoveDrag, type SessionRowDrag } from "./explorer/sessionDrag";
import type { ProjectRowDrop } from "./explorer/ProjectRowActions";
import { MoveSessionDialog } from "./MoveSessionDialog";
import { createSessionDeletion, deletedSessionRecovery, deleteSelectionCurrent, sessionDeletionMessage, type DeletedTarget } from "./sessionDeletion";
import { batchDeleteCandidate, createSessionBatchDeletion } from "./sessionBatchDeletion";
import type { BatchDeleteControls } from "./SessionBatchDeletePanel";
import { ConfirmPopover } from "./ConfirmPopover";
import { OverflowTip } from "./OverflowTip";
import { RenamePopover } from "./RenamePopover";
import { createProjectRename, projectNameVisible, projectRenameMessage, projectRenameSelectionCurrent, type ProjectNameTarget } from "./projectRename";
import { sessionHierarchy } from "./sessionHierarchy";
import type { SessionMenuEntry } from "./SessionTabMenu";
import { plainTitle } from "./sessionTitle";
import { KeyGesture } from "./KeyGesture";
import { isSessionContextKey, isSessionDeleteKey, restoreSessionMenuFocus, sessionRowShortcut, sessionActionAccess, sessionMoveAccess, type SessionAction, type SessionMenuTarget } from "./sessionRowActions";
import { providerHasRemoteControl, type RemoteControlOpenRequest } from "./remoteControl";
import { copyResumeCommand, isOpenElsewhereAction, openElsewhereCommandAction, openSessionInVsCode, opensElsewhere, runSessionInTerminal,
  readClaudeCodeConversation, type OpenElsewhereAction } from "./claudeCodeOpen/claudeCodeOpen";
import { claudeCodeOpenHostApi } from "./claudeCodeOpen/hostApi";
import { ConversationElsewhereBanner, ConversationElsewhereLine, promptFocusTarget, useConversationElsewhere } from "./claudeCodeOpen/ConversationElsewhereNotice";
import { openElsewhereEntries } from "./claudeCodeOpen/openElsewhereEntries";
import { projectRailGroups, projectRailProjection } from "./explorer/projectRail";
import { createLabelsHub } from "./labels/labelsHub";
import { canMoveLabel, labelsOfSpace, labelSubtreeProjects, projectLabels } from "./labels/labels";
import { LabelRailRow, type LabelRowEditing } from "./labels/LabelRailRow";
import { LabelDragGhost, LabelFormPopover } from "./labels/LabelViews";
import { LabelSettings } from "./labels/LabelSettings";
import { ProjectLabelBadges, ProjectLabelsPopover } from "./labels/ProjectLabels";
import { labelMoveTarget, useRailDrag, type RailDrag, type RailDrop } from "./labels/labelDrag";
import { ProjectRailRows } from "./explorer/ProjectRailRows";
import { createTerminalWorkspace } from "./terminal/terminalWorkspace";
import { TerminalPanel } from "./terminal/TerminalPanel";
import { createAutomationsHub } from "./automations/automationsHub";
import { AutomationsPanel } from "./automations/AutomationsPanel";
import { SessionOrigin } from "./automations/SessionOrigin";
import { sessionOrigin } from "./automations/automations";
import { WorkflowsPanel } from "./workflows/WorkflowsPanel";
import { RunTab } from "./workflows/RunTab";
import { OpenRunContext } from "./workflows/openRun";
import { createMessageReader, MessageReaderContext } from "./wholeMessage";
import { createWorkflowsHub } from "./workflows/workflowsHub";
import { ScriptRunActionsContext } from "./scriptCard/SaveAsScript";
import { SessionResumeContext } from "./resumeCard/TimelineResumeCard";
import { createResumeHub } from "./resumeCard/resumeHub";
import { IssuesPanel } from "./issues/IssuesPanel";
import { PullRequestButton } from "./pullRequests/PullRequestButton";
import { PullRequestSettings } from "./pullRequests/PullRequestSettings";
import { createWorkItemsHub } from "./workItems/workItemsHub";
import { createReviewsHub } from "./review/reviewsHub";
import { ReviewOpenerContext, ReviewsHubContext, ReviewsListOpenerContext } from "./review/useReviews";
import { ReviewComposerStatus } from "./review/ReviewComposerStatus";
import type { ReviewScope, ReviewTarget } from "./review/reviewsHub";
import { reviewSessions, sessionCheckout } from "./review/reviewSessions";
import { WorkItemsPanel, type WorkItemsFocus } from "./workItems/WorkItemsPanel";
import { WorkItemCards } from "./workItems/WorkItemCards";
import { WorkItemsBadge } from "./workItems/WorkItemsBadge";
import { WorkItemSettings } from "./workItems/WorkItemSettings";
import type { RunModelsLoader, RunProvider } from "./workItems/runsWith";
import type { SessionStartRequest } from "./sessionSettings/sessionSettings";
import { sessionSettingsApi } from "./sessionSettings/sessionSettingsApi";
import { startWorkItem, type WorkStartSession } from "./workItems/startWorkItem";
import { carriedBy, readingOrder, sessionCards, workCounts, workItemKey, workItems, type WorkItem, type WorkStart } from "./workItems/workItems";
import { TerminalList } from "./terminal/TerminalList";
import { persistTerminalLook, restoreTerminalLook, terminalLookKey, type TerminalLook } from "./terminal/terminalLook";
import { applicationKey, terminalsOf } from "./terminal/terminals";
import { ExplorerSessions, SessionRowTitle, SubAgentDisclosure, sessionRowIndent, sessionTwist, sessionTwistKey } from "./explorer/ExplorerSessions";
import { listedSessions, sessionList, withClaudeCodeSessions, type SessionListEntry } from "./explorer/sessionTree";
import { ClaudeCodeSessionsGroup } from "./claudeCodeImport/ClaudeCodeSessionsGroup";
import { hasClaudeCodeProvider, importClaudeCodeSession, importErrorMessage, openEntryInVsCode, readClaudeCodeConversations, type ClaudeCodeConversations } from "./claudeCodeImport/claudeCodeSessions";
import { ProviderBrandsContext, providerBrands, providerName } from "./ProviderIcon";
import { ClaudeCodePreviewPane } from "./claudeCodeImport/ClaudeCodePreviewPane";
import { clearPreviewDraft, continuePreview, handOffPreviewPrompt, type HandOffRefusal, type PreviewPrompt } from "./claudeCodeImport/previewContinuation";
import { previewTarget } from "./claudeCodeImport/claudeCodePreview";
import { filterOpens, filterSessionRows, sidebarFilterView, toggleFilterFold, unplacedKey } from "./explorer/sidebarFilter";
import { SidebarFilterBox } from "./explorer/SidebarFilterBox";
import { readQuery } from "./search/searchResults";
import { SessionLinksContext, type SessionLinks } from "./SessionReference";
import { SessionWidthContext, SessionWidthGrips, useSessionWidthStyle, type SessionWidthControl } from "./SessionWidthGrips";
import { applySessionWidth, defaultSessionWidth, sessionWidthsOf, validSessionWidth, withSessionWidth } from "./sessionWidth";
import { startSplitterDrag } from "./splitterPress";
import { collapseAllScopes, emptyProjectTree, expandScope, globalScope, isCollapsed, isExpanded, isFavorite, persistProjectTree, projectTreeKey, restoreProjectTree, scopeKey, setCollapsed,
  setFavorite, toggleScope, isClaudeCodeOpen, unplacedClaudeCodeKey, setClaudeCodeOpen, isLabelFolded, setLabelFolded, type ProjectTree } from "./explorer/projectTree";
import { defaultIdeWidth, maximumIdeWidth, minimumIdeWidth, parseIdeWidth, persistIdeWidth, resizeIdeWidth } from "./ideWidth";
import { focusVisibleProject, projectRailVisible, restoreProjectRailFocus } from "./explorer/projectRailVisibility";
import { nextTheme, themeLabel, useWindowPreferences } from "./windowPreferences";
import { AppearanceSettings, themeIcons } from "./AppearanceSettings";
import { TimelineEmphasisProvider } from "./timelineEmphasis/useTimelineEmphasis";
import { StorageSettings } from "./storage/StorageSettings";
import { useStorageMaintenance } from "./storage/useStorageMaintenance";
import { storage } from "#neoastra";
import { speech as speechApi } from "#neoastra";
import { mcpApps as mcpAppsApi } from "#neoastra";
import { createMcpAppsHost, McpAppsContext } from "./mcpApps/mcpAppHost";
import type { SessionDisplayView, SessionOptionsChoice, SpaceSessionResourcesResponse } from "#neoastra";
import { globalConfig } from "#neoastra";
import { windows as windowsApi, type WindowsCurrentResponse } from "#neoastra";
import { createWindowsHub, readWindowPage, writeWindowPage, type WindowsRequest } from "./windows/windowsHub";
import { fileTabRef, readTransfer, sessionTabRef, spaceBound, transferOf } from "./windows/tabRefs";
import { WindowBadge, WindowIcon, windowName } from "./windows/WindowBadge";
import { WindowSettings } from "./windows/WindowSettings";
import { SpeechContext } from "./speech/speech";
import { SpeechSettings } from "./speech/SpeechSettings";
import { speechEnabled } from "./speech/speechStore";
import { useSpeechInstall } from "./speech/useSpeechInstall";
import { GeneralSettings } from "./GeneralSettings";
import { createHostLiveness, hostCheckInterval, hostPingInterval } from "./hostLiveness";
import { createSlowCallWatch, installLocalCallPolicy } from "./localCalls";
import { closingPingInterval, readHostClosing, type HostClosing } from "./hostClosing";
import { reportToHost } from "./hostReport";
import { collectSessionRetention, createSessionRetention } from "./sessionRetention";
import { publishSessionRetention, watchSessionRetention } from "./sessionRetentionRpc";
import { SessionRetentionGate } from "./SessionRetentionGate";
import { reloadsAfterClose, rpcSessionClosed, sessionRecoveryKey } from "./sessionRecovery";
import { installKeyboardClickGuard } from "./keyboardClickGuard";
import { installContextClickGuard } from "./contextClickGuard";
import { installParagraphSelection } from "./paragraphSelection";
import { installFontPreferences } from "./fonts/fontPreferences";
import { closeApplicationWindow, logoUrl, useWindowTitleBar, WindowBrand, WindowControls } from "./windowChrome";
import { WindowZoom, zoomWindow } from "./WindowZoom";
import { createPluginEventsRead } from "./pluginEvents";
import { ProjectContext } from "./ProjectContext";
import { WorkspaceChip } from "./promptSurface/WorkspaceChip";
import { SubAgentsChip } from "./promptSurface/SubAgentsChip";
import { reasoningShown } from "./promptSurface/promptSurface";
import { WorktreeManager } from "./worktrees/WorktreeManager";
import { WorktreeSettings } from "./worktrees/WorktreeSettings";
import { McpHostSettings } from "./mcpHost/McpHostSettings";
import { createSpacesHub, type SpacesRequestKind } from "./spaces/spacesHub";
import { ElsewhereBadge } from "./sessionLeases/ElsewhereBadge";
import { elsewhereFollowView, elsewhereKind, elsewhereLabel, openSessionIds, type ElsewhereKind } from "./sessionLeases/sessionLeases";
import { SpaceActivityBar, SpaceIcon, SpaceSwitch } from "./spaces/SpaceViews";
import { readSpacePageLink, type SpacePageTarget } from "./spaces/spaceSummary";
import { SpaceDialog } from "./spaces/SpaceDialog";
import { SpaceSettings } from "./spaces/SpaceSettings";
import { MoveToSpaceDialog } from "./spaces/MoveToSpaceDialog";
import { SessionChildrenDialog } from "./sessionChildren/SessionChildrenDialog";
import { deleteSessionTree, readSessionChildren, type SessionChildren, type SessionTreeDeletion } from "./sessionChildren/sessionChildren";
import { moveSessionToSpace, spaceMoveMessage } from "./spaces/sessionSpaceMove";
import { archivableDescendants, newRestoredNotices, sessionArchiveMessage, setSessionArchived } from "./sessionArchive/sessionArchive";
import { archivedInView, defaultSpaceId, findSpace, neighborSpace, persistShownSpace, placeProject, resourceSpaces, restoreShownSpace, scopeSnapshot, sessionHome, showOthersKey, spaceChatCounts, shownSpaceKey, spaceCalls,
  spaceStorageKey, spaceView, spaceViewKey, toldDefaultKey, viewLists, waitingSubSessions, subSessionsAtWork, type Space, type SubSessionAtWork, type WaitingSubSession } from "./spaces/spaces";
import { newFallbackNotices } from "./spaces/fallbackNotices";
import { composerTierEntries, tierStore } from "./modelTiers";
import { deletedSessionWorktree, persistWorkPlaces, projectFolder as inProjectFolder, restoreWorkPlaces, sessionWorktree, withWorkPlace, workPlacesKey, type WorkPlace } from "./worktrees/worktrees";
import { createPreparedWorktrees } from "./worktrees/preparedWorktrees";
import { WorktreePreparation } from "./worktrees/WorktreePreparation";
import { editorFailure, removalReason, removalReply } from "./worktrees/worktreeInventory";
import type { ComposerChromeValue } from "./composerChrome";
import { ShellLanguageContext, useLanguagePreference, useShellLanguage } from "./shellLanguage";
import { workflowNotice, type WorkflowNotice } from "./workflowNotice";
import { translate, type MessageKey } from "./localization";
import { ApplicationLogsPanel } from "./ApplicationLogsPanel";
import { aboutFacts, AboutSettings } from "./AboutDialog";
import type { ProjectDetailsContext } from "./ProjectDetailsEntry";
import { createApplicationLogClearActions } from "./applicationLogClear";
import { SessionInfoDialog, type SessionInfoLifetime } from "./SessionInfoDialog";
import { selectedSessionInfoAvailable, selectedSessionInfoSelection, sessionInfoView } from "./sessionInfo";
import { GlobalSearch, type ContentSearch, type SearchChoice, type SearchStart } from "./search/GlobalSearch";
import { PluginUiHost } from "./PluginUiHost";
import { PluginRegionSlot, usePluginRegions } from "./PluginRegions";
import { createHostReads } from "./hostReads";
import { HostReadsContext, PaneObservingContext, type HostReads } from "./hostReadsContext";
import { askPluginComposer, noPluginContributions, pluginCommandAvailable, pluginContributions, pluginKeymap, PluginPaneContext, PluginUiContext, resolvePluginKey,
  findPluginCommand, type PluginComposerRequest, type PluginContributionsView, type PluginPane, type PluginUiValue, pluginsChangedEvent } from "./pluginUi";
import { CommandHelp } from "./CommandHelp";
import { commandDefinitions, commandKeys, resolveCommandKey, type CommandId } from "./commandRegistry";
import { composerCommandBlock, composerControlState, isComposerControlCommand, type ComposerControlCommand } from "./composerCommands";
import { landingCanvasId, landingPluginKey, LandingShellContext } from "./landing/landingShell";
import { useLandingAtStartup, useLandingShell } from "./landing/landingWindow";
import { createPaletteFocusRestoration } from "./paletteActions";
import "normalize.css";
import "@blueprintjs/core/lib/css/blueprint.css";
import "flexlayout-react/style/light.css";
import "@xterm/xterm/css/xterm.css";
import "@xyflow/react/dist/style.css";
import "./style.css";
import "./timelineEmphasis/timelineEmphasis.css";
import "./timelineActivity/timelineActivity.css";
import "./scriptCard/scriptCard.css";
import "./resumeCard/resumeCard.css";
import "./editor/editor.css";
import "./pluginProviders/pluginProviders.css";
import "./review/review.css";
import "./windows/windows.css";
import "./explorer/explorer.css";
import "./labels/labels.css";
import "./terminal/terminal.css";
import "./automations/automations.css";
import "./workflows/workflows.css";
import "./workItems/workItems.css";
import "./charts/charts.css";
// The Statistics canvas is a module the application loads when a tab shows it, and a module brings no stylesheet with it: the sheet is part of the page.
import "./statistics/statistics.css";
import "./search/canvas/canvas.css";
import "./issues/issues.css";
import "./worktrees/worktrees.css";
import "./mcpHost/mcpHost.css";
import "./spaces/spaces.css";
import "./canvases/canvases.css";
import "./landing/landing.css";
import "./mobile/canvas/canvas.css";
import "./mobile/settings/settings.css";
import "./documentation/documentation.css";
import "./promptInheritance/promptInheritance.css";
import { DocumentationPanel } from "./documentation/DocumentationPanel";
import { findKeyKept } from "./findInPage/findInPage";
import { createDocumentationHub } from "./documentation/documentationHub";
import "./pluginButtons/pluginButtons.css";
import "./sessionBranch/sessionBranch.css";
import "./sessionChildren/sessionChildren.css";
import "./conversationHeld/conversationHeld.css";
import "./claudeCodeImport/claudeCodeSessions.css";
import { createConversationHeldReviewer } from "./conversationHeld/conversationHeld";
import { SessionBranchDialog, type BranchDialogHost } from "./sessionBranch/SessionBranchDialog";
import { SessionLineageBanner, SessionLineageMark } from "./sessionBranch/SessionLineage";
import { composerPrompt, type BranchMode } from "./sessionBranch/branchPoints";
import { createBranchRequests, readKeepRemovedTurns, saveKeepRemovedTurns, type BranchRequests } from "./sessionBranch/branchRequests";
import { lineageTitle, sessionLineage } from "./sessionBranch/lineage";
import { branchTarget, createSessionBranch, readSessionTurns, type ForkResult, type RewindResult } from "./sessionBranch/sessionBranchRpc";
import { CanvasPanel } from "./canvases/CanvasPanel";
import { CanvasIcon } from "./canvases/CanvasIcon";
import { PluginCanvasSettingsPage } from "./canvases/PluginCanvasSettingsPage";
import { PluginHostBridgeContext, type PluginHostBridge } from "./pluginScript/hostBridge";
import { createCanvasHub, type CanvasClosedInstance, type CanvasOpenRequest } from "./canvases/canvasHub";
import { createCanvasInstances } from "./canvases/canvasInstances";
import { CanvasesPanel } from "./canvases/CanvasesPanel";
import { addCanvasTabToSpace, bringCanvasTab, canvasMenuItems, canvasPlacement, canvasRequestSpace, canvasStatusKey, canvasTabOf, closedCanvasTab, defaultCanvasTarget, newCanvasPrompt, pluginSettingsPage, pluginSettingsPages, pluginSettingsTab, removeCanvasTabFromSpace, samePluginSettingsPage, sessionCanvasMenuEntries, tabCanvases, withCanvasStatus, type CanvasScopeProject, type CanvasSelection, type CanvasTarget, type PluginSettingsPage } from "./canvases/canvasPages";
import { createCanvasPluginControl, type CanvasPluginControl } from "./canvases/canvasPlugin";
import { PluginButtons } from "./pluginButtons/PluginButtons";
import { SessionRowMenu } from "./pluginButtons/pluginMenu";
import { PluginButtonsActiveContext, PluginButtonsContext, createHiddenButtons, type PluginButtonContext, type PluginButtonView, type PluginButtonsHost } from "./pluginButtons/pluginButtonModel";
import { dismissDialogsOnOutsidePress, modalDialogOpen } from "./modalDialogs";
import { bookmarks as bookmarksApi } from "#neoastra";
import { localSearch as localSearchApi, type LocalSearchHit } from "#neoastra";
import { createLocalSearchHub, isExcluded, searchEnabled } from "./search/localSearchHub";
import { LocalSearchContext } from "./search/localSearchContext";
import { SearchSettings, meaningStateText } from "./search/SearchSettings";
import { MeaningDialogHost } from "./search/MeaningDialog";
import { contentTarget } from "./search/contentResults";
import { worktrees as worktreesApi } from "#neoastra";
import { externalEditors as externalEditorsApi } from "#neoastra";
import { sandboxSettings, type SandboxScopeItem, type SandboxSessionResponse } from "#neoastra";
import { notSandboxed, sessionSandboxSummary } from "./sandbox/sandboxChoices";
import { SessionOptionsFields } from "./sessionSettings/SessionOptionsFields";
import { changedSessionOptions, sessionOptionsOf } from "./sessionSettings/sessionOptions";
import { SandboxSettings } from "./sandbox/SandboxSettings";
import { SessionSandboxSelect } from "./sandbox/SessionSandboxSelect";
import { createExternalOpener, ExternalOpenerContext } from "./externalEditors/externalEditors";
import { sessionBookmarks, type Bookmark } from "./bookmarks/bookmarks";
import { createBookmarksHub, type BookmarksHub } from "./bookmarks/bookmarksHub";
import { createRevealRequests, type RevealOutcome, type RevealRequest, type RevealRequests } from "./bookmarks/revealRequests";
import { createTimelineBookmarks, TimelineBookmarksContext } from "./bookmarks/timelineBookmarks";
import { BookmarkRail } from "./bookmarks/BookmarkRail";
import { BookmarksPanel } from "./bookmarks/BookmarksPanel";
import { SessionBookmarkMark } from "./bookmarks/SessionBookmarkMark";
import "./bookmarks/bookmarks.css";
import "./speech/speech.css";

type TimelineCommand = Readonly<{ sessionId: string; projectId: string | null; epoch: string | null;
  ready: () => boolean; navigate: (action: MessageNavigation) => void;
  latestReady: () => boolean; latest: () => void; cancelLatest: () => void;
  /** Scrolls to a user message, by the journal offset that keys its row; false when the loaded window does not hold it. */
  showRow: (key: string) => boolean;
  /** Goes to the bookmarked message before or after the rows in view; false when there is none that way. */
  stepBookmark: (direction: -1 | 1) => boolean }>;

/**
 * What a pane of a session asks about bookmarks: the list the host keeps, the requests that show a message of a
 * timeline, and the two things a pane does with a bookmark, which the window of the application carries through.
 */
type SessionBookmarkHost = Readonly<{ hub: BookmarksHub; reveals: RevealRequests;
  /** Bookmarks the message of a row of a session, or removes the bookmark it has. */
  toggleMessage: (sessionId: string, rowKey: string, bookmarkId: string | null) => void;
  /** Goes to what a bookmark marks. */
  go: (bookmark: Bookmark) => void;
  /** Says that the message of a request could not be shown. */
  unshown: (outcome: RevealOutcome) => void }>;

/**
 * What a pane of a session asks for a fork or a rewind of its session: the requests that open the window, and the
 * two changes, which the window of the application carries through (the new session, its tab, the prompt).
 */
type SessionBranchHost = Readonly<{ requests: BranchRequests;
  fork: (session: WorkspaceSession, projectId: string | null, beforeRunId: string | null) => Promise<ForkResult>;
  rewind: (session: WorkspaceSession, projectId: string | null, beforeRunId: string, keepRemovedTurns: boolean) => Promise<RewindResult> }>;

const demoMode = import.meta.env.VITE_DEMO_MODE === "true";
// Every call of the page to its host waits for its answer: none is given up on a timer, and a slow one is logged.
if (!demoMode) installLocalCallPolicy(createSlowCallWatch(reportToHost));
const noSubscription = () => () => { };
const noBookmarkProjects: readonly { id: string; name: string }[] = [];
type View = "workspace" | "appearance" | "general" | "windows" | "spaces" | "labels" | "providers" | "models" | "prompts" | "mcp" | "logs" | "skills" | "plugins" | "about" | "config" | "worktrees" | "workItems" | "pullRequests" | "mcpHost" | "permissions" | "sandbox" | "codeMode" | "sessions" | "storage" | "speech" | "search" | "plugin";
type SettingsSection = Exclude<View, "workspace">;

function App({ initialWindow = null }: { initialWindow?: WindowsCurrentResponse | null }) {
  const language = useLanguagePreference();
  const t = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate(language.locale, key, parameters);
  // The window of this page: the main one, the control center, or a secondary one that holds tabs moved out of it. A
  // secondary window has no rail, no Explorer and no title-bar actions; it keeps the space it was created in, and its
  // tabs are kept by its window, not in the storage that the pages share.
  const [windowsHub] = useState(() => createWindowsHub(windowsApi, demoMode ? null : initialWindow));
  const windowsState = useSyncExternalStore(windowsHub.subscribe, windowsHub.getSnapshot);
  const secondary = windowsHub.secondary;
  const [windowPage] = useState(() => readWindowPage(windowsHub.pageState));
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
  // The labels of the projects: the Explorer lists the projects of the shown space under them.
  const [labelsHub] = useState(() => createLabelsHub(labelsApi));
  const labelsState = useSyncExternalStore(labelsHub.subscribe, labelsHub.getSnapshot);
  // The spaces of the resources, which the pages of Settings show and change beside each row.
  const resourceScope = useMemo(() => resourceSpacesOf(spacesState, spacesHub), [spacesState, spacesHub]);
  // The spaces of each provider as the hub reads them again after every change: what a new session is offered.
  const providerSpaces = useMemo(() => spacesState.available ? (key: string) => resourceSpaces(spacesState.assignments, "provider", key) : null,
    [spacesState.available, spacesState.assignments]);
  // The same, with the names of the spaces, for the model tiers of Settings > Models.
  const tierProviderSpaces = useMemo(() => providerSpaces
    ? { of: providerSpaces, name: (id: string) => spacesState.spaces.find(space => space.id === id)?.name ?? id } : null,
    [providerSpaces, spacesState.spaces]);
  // The space of a session and what it can use there, for the panel of the session.
  const sessionSpaces = useMemo(() => spacesState.available ? { spaces: spacesState.spaces, read: spacesHub.sessionResources } : null,
    [spacesState.available, spacesState.spaces, spacesHub]);
  const [spaceId, writeSpaceId] = useState(() => secondary ? windowsHub.getSnapshot().current?.spaceId ?? defaultSpaceId
    : restoreShownSpace(() => localStorage.getItem(shownSpaceKey)));
  const shownSpace = useRef(spaceId);
  // Whether the default space shows what belongs to the other spaces too: a choice of this window, kept as the shown space is.
  const [showOthers, writeShowOthers] = useState(() => { try { return localStorage.getItem(showOthersKey) === "true"; } catch { return false; } });
  const showingOthers = useRef(showOthers);
  const catalog = useRef<WorkspaceSnapshot | undefined>(undefined);
  // What the window was last given of the catalog: it is given the catalog again when that changes.
  const scopedView = useRef<string | null>(null);
  const [workspace] = useState(() => ({ ...workspaceApi, snapshot: async (...request: Parameters<typeof workspaceApi.snapshot>) => {
    const fresh = await workspaceApi.snapshot(...request);
    // A window that comes back on a space waits for the spaces before it shows the projects of that one alone.
    if (shownSpace.current !== defaultSpaceId) await spacesHub.whenLoaded(6_000);
    catalog.current = fresh;
    const view = spaceView(spacesHub.getSnapshot().spaces, shownSpace.current, showingOthers.current);
    scopedView.current = spaceViewKey(view);
    // The archived sessions stay in the catalog, for the saved sessions and the search; the window lists the others.
    return withoutArchived(scopeSnapshot(fresh, view));
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
  // A host that closes keeps its window until its close ends: the ping says so, and what the close waits for.
  const [closing, setClosing] = useState<HostClosing | null>(null);
  const closingRef = useRef(false);
  closingRef.current = !!closing;
  // The host is pinged without a timer to give it up: a ping that waits says the host is busy, for as long as it waits.
  const [hostLiveness] = useState(() => createHostLiveness(async () => {
    const value = await boot.status({});
    setClosing(readHostClosing(value));
    return value;
  }, { pingInterval: () => closingRef.current ? closingPingInterval : hostPingInterval, report: reportToHost }));
  const hostState = useSyncExternalStore(hostLiveness.subscribe, hostLiveness.getSnapshot);
  // Only a lost connection stops what needs the host: a busy host is waited for, and the window says so.
  const hostLost = hostState.lost;
  // The page lost its retention lifetime with the host while the window stays: the panes are held, and only a reload
  // of the window opens a new one. The window says so and offers the reload, also once the host answers again.
  const [linkLost, setLinkLost] = useState(false);
  const windowSnapshot = useWindowTitleBar();
  const [workspaceState, setWorkspaceState] = useState<WorkspaceState>({ kind: "loading" });
  const currentSnapshot = useRef<WorkspaceSnapshot | undefined>(undefined);
  const projectInspection = useRef({ version: 0, ready: false });
  const [, setProjectInspectionVersion] = useState(0);
  function markProjectInspection(ready: boolean) {
    projectInspection.current = { version: projectInspection.current.version + 1, ready };
    setProjectInspectionVersion(projectInspection.current.version);
  }
  // The moves whose tabs have not followed them yet: each is retargeted with the first list that shows it moved.
  const pendingTabMoves = useRef<readonly WorkspaceMovedSession[]>([]);
  function publishWorkspaceState(value: WorkspaceState) {
    if (value.kind === "ready" && pendingTabMoves.current.length > 0) {
      const { shown, waiting } = splitPendingTabMoves(value.snapshot, pendingTabMoves.current);
      pendingTabMoves.current = waiting;
      // In the same update as the list: published alone, the list drops the tab of a moved session.
      retargetMovedTabs(shown);
    }
    advanceBrowserRevision();
    invalidateCreation();
    currentSnapshot.current = value.kind === "ready" ? value.snapshot : undefined;
    markProjectInspection(value.kind === "ready");
    setWorkspaceState(value);
  }
  // Sessions also appear without the window asking: an agent creates sub-sessions through the alta tool.
  // While a run is live, and when it ends, the session list is read again and shown only if it differs.
  const liveRuns = useRef(new Set<string>());
  const sessionListBody = useRef(async () => {});
  sessionListBody.current = async () => {
    if (!creationAlive.current || !currentSnapshot.current || document.visibilityState !== "visible") return;
    const revision = browserRevision.current;
    const fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 });
    const shown = currentSnapshot.current;
    if (movingPending.current) { sessionLists.defer(); return; }
    if (!creationAlive.current || !shown || !fresh.configured || revision !== browserRevision.current
      || sessionListSignature(shown) === sessionListSignature(fresh)) return;
    publishWorkspaceState({ kind: "ready", snapshot: fresh });
  };
  // A move publishes its own list, with the tabs of the moved sessions pointed at their new project: read during it,
  // a list would drop those tabs. What is asked for then is read once the move ends.
  const [sessionLists] = useState(() => createCoalescedRead(() => movingPending.current, () => sessionListBody.current()));
  const readSessionList = useRef(() => {});
  readSessionList.current = () => sessionLists.run();
  // A run that ends may have written a plan or proposed a task: the work items are read again too.
  const refreshWorkItems = useRef(() => {});
  const noteRunActivity = (id: string, running: boolean | null) => {
    const was = liveRuns.current.has(id);
    if (running) liveRuns.current.add(id); else liveRuns.current.delete(id);
    if (was && !running) { readSessionList.current(); refreshWorkItems.current(); }
    // The first prompt of a session titles it when its run starts: the list is read once that is written.
    if (!was && running) setTimeout(() => readSessionList.current(), 1_500);
  };
  // A session also runs with no tab on it (a sub-agent, a prompt sent by the alta tool): the activity of the host
  // says when one starts and when it ends, and its title is read then as the one of a session of a tab is.
  const hostRuns = useMemo(() => spacesState.sessions.filter(session => session.running).map(session => session.sessionId).sort().join("\n"), [spacesState.sessions]);
  const hostRunsNow = useRef(hostRuns);
  useEffect(() => {
    if (hostRunsNow.current === hostRuns) return;
    hostRunsNow.current = hostRuns;
    readSessionList.current();
    const timer = setTimeout(() => readSessionList.current(), 1_500);
    return () => clearTimeout(timer);
  }, [hostRuns]);
  useEffect(() => {
    const timer = setInterval(() => { if (liveRuns.current.size > 0 || hostRunsNow.current) readSessionList.current(); }, 10_000);
    return () => clearInterval(timer);
  }, []);
  const [projectId, writeProjectId] = useState<string | null>(null);
  const [sessionId, writeSessionId] = useState<string | null>(null);
  const [submissions] = useState(() => createOwnedSubmissions(sessionOperations.send, sessionOperations.abort));
  // Read ALL persisted spaces before presentation restoration can reconcile or overwrite any of them.
  const [retention] = useState(() => createSessionRetention(publishSessionRetention,
    () => collectSessionRetention(() => localStorage, submissions.imageDrafts.retentionKeys())));
  useSyncExternalStore(retention.subscribe, retention.getSnapshot);
  const [retentionLifetime, setRetentionLifetime] = useState<string | null>(null);
  const [restoredTabs] = useState(() => restoreSessionTabs(() => secondary ? windowPage.sessionTabs : localStorage.getItem(spaceStorageKey(sessionTabsKey, spaceId))));
  const [tabs, writeTabs] = useState(emptySessionTabs);
  // A session that is selected without a tab is looked at: it takes the transient tab. The one named here is opened
  // to be kept instead (a session that was just created, a fork), until its tab is there.
  const keptSession = useRef<string | null>(null);
  const openingMode = (id: string | null): SessionTabMode => id !== null && keptSession.current === id ? "permanent" : "preview";
  const retentionNavigation = useRef(0);
  // All shell routes, including recovery callbacks, use these admission boundaries. A newer navigation
  // invalidates an older held acknowledgement; a catalog/host/lifetime change cannot revive it either.
  function protectNavigation(ids: readonly string[], expose: () => void): boolean {
    const generation = ++retentionNavigation.current;
    const epoch = currentHostEpoch.current;
    if (!currentHostAvailable.current) { if (status && !status.hostAvailable) { expose(); return true; } return false; }
    if (!epoch || hostLost || !creationAlive.current) return false;
    if (ids.every(id => retention.hasClaim(epoch, id))) { expose(); return true; }
    const revision = browserRevision.current, catalog = currentSnapshot.current;
    void retention.admit(epoch, ids, () => generation === retentionNavigation.current && creationAlive.current
      && currentHostEpoch.current === epoch && browserRevision.current === revision && currentSnapshot.current === catalog, expose);
    return false;
  }
  function setTabs(next: SessionTabsState) { protectNavigation(next.open.map(tab => tab.sessionId), () => writeTabs(next)); }
  const [tabsReady, setTabsReady] = useState(false);
  // File editor tabs share the strip with the sessions. An active file is shown over the session selection,
  // which stays as it is; selecting a session, a project or the new-session tab leaves the file.
  const [restoredFileTabs] = useState(() => {
    if (secondary) return restoreFileTabs(() => windowPage.fileTabs);
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
  const [changeRequests, setChangeRequests] = useState<ReadonlyMap<string, Readonly<{ path: string | null; worktree?: string | null; review?: "changes" | "branch" | null }>>>(() => new Map());
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
  // A first message typed under the preview of a Claude Code conversation is sent the same way once its session holds it,
  // with the images pasted with it.
  const autoSend = useRef<{ sessionId: string; text: string; images?: number } | null>(null);

  function editLocalDraft(text: string) {
    localDrafts.edit(draftScope, text);
    persistDraft((_key, value) => localStorage.setItem(localDraftStorageKey, value), () => localStorage.removeItem(localDraftStorageKey), draftScope, text);
  }
  const tabFocusPending = useRef(false);
  // At a start the host wins: a tab that another window holds is that window's, and this one does not restore it too.
  function unheldSessionTabs(state: SessionTabsState): SessionTabsState {
    const held = (tab: SessionTab | null | undefined) => !!tab && windowsHub.heldElsewhere(sessionTabRef(tab.sessionId));
    if (!state.open.some(held)) return state;
    const open = state.open.filter(tab => !held(tab));
    return { ...state, open, active: held(state.active) ? open[0] ?? null : state.active, ...(state.preview !== undefined ? { preview: held(state.preview) ? null : state.preview } : {}) };
  }
  function unheldFileTabs(state: FileTabs): FileTabs {
    const held = (tab: FileTab | null) => !!tab && windowsHub.heldElsewhere(fileTabRef(tab));
    if (!state.open.some(held)) return state;
    return { ...state, open: state.open.filter(tab => !held(tab)), active: held(state.active) ? null : state.active };
  }
  // What the window showed in front: a session whose tab another window holds leaves it there (see the selection below).
  const shownFront = useRef<Readonly<{ file: FileTab }> | Readonly<{ session: SessionTab }> | null>(null);
  function setProjectId(value: string | null) { advanceBrowserRevision(); invalidateCreation(); writeProjectId(value); activateFile(null); }
  function setSessionId(value: string | null) {
    protectNavigation(value ? [value] : [], () => {
      advanceBrowserRevision(); invalidateCreation(); selectedSessionId.current = value; writeSessionId(value); activateFile(null);
    });
  }
  const [composerHeights, setComposerHeights] = useState<ReadonlyMap<string, number>>(() => new Map());
  const [view, setView] = useState<View>("workspace");
  const currentView = useRef<View>(view);
  currentView.current = view;
  const [settingsOpen, setSettingsOpen] = useState(false);
  const settingsVisible = useRef(false);
  const [settingsSection, setSettingsSection] = useState<SettingsSection>("appearance");
  // The space whose page Settings > Spaces shows, at which section; null for the cards of every space.
  const [spacePage, setSpacePage] = useState<SpacePageTarget | null>(null);
  // The settings page of a plugin that Settings > Extensions shows, when the section is `plugin`.
  const [pluginPage, setPluginPage] = useState<PluginSettingsPage | null>(null);
  // The provider Settings > Providers selects when it opens from a link of Settings > Sandbox.
  const [providerFocus, setProviderFocus] = useState<string | null>(null);
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
    setProviderFocus(null);
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
      // Settings > Spaces opens on the cards of every space when it is reached from elsewhere.
      if (next === "spaces" && (!settingsVisible.current || currentSettingsSection.current !== "spaces")) setSpacePage(null);
      currentSettingsSection.current = next;
      setSettingsSection(next);
      settingsVisible.current = true;
      setSettingsOpen(true);
    }
  }
  // Settings on the settings page of a plugin: from its entry in Extensions, the gear of its row, or a request of the plugin or an agent.
  function openPluginSettings(page: PluginSettingsPage) {
    setPluginPage(page);
    navigate("plugin");
  }
  // A control outside the shell (a composer status item) asks for a Settings page by name.
  const openSettingsPage = useRef<(page: string) => void>(() => {});
  openSettingsPage.current = page => {
    if (page === "mcp" || page === "plugins" || page === "providers" || page === "sandbox" || page === "skills" || page === "spaces" || page === "speech") { navigate(page); return; }
    // `spaces/<id>/<section>` opens the page of a space at one of its sections.
    const target = readSpacePageLink(page);
    if (target) { navigate("spaces"); setSpacePage(target); }
  };
  useEffect(() => settingsNavigation.subscribe(page => openSettingsPage.current(page)), []);
  // A place that chooses a sandbox, from Settings > Sandbox: the editor of its provider, the page of its space, or the
  // row of its project in the Explorer, where its "Sandbox…" action is.
  function openSandboxScope(scope: SandboxScopeItem) {
    if (scope.kind === "provider" || scope.kind === "model") { navigate("providers"); setProviderFocus(scope.id); }
    else if (scope.kind === "space") { navigate("spaces"); setSpacePage({ spaceId: scope.id, section: "newSessions" }); }
    else if (scope.kind === "project") { closeSettings(); selectProject(scope.id); }
  }
  const { projectSort, setProjectSort, theme, variant, appearance, setTheme, darker, setDarker, colorScheme, shownScheme, setColorScheme, customSchemes, setCustomSchemes, railState, setDesktopCollapsed, toggleRail, closeNarrowRail, notices: preferenceNotices, recentSessionCount, setRecentSessionCount, subAgentCount, setSubAgentCount, confirms, setConfirm,
    singleSessionTab: singleTab, setSingleSessionTab: setSingleTab, projectGrouping, setProjectGrouping } = useWindowPreferences();
  // With a single tab for the sessions, whatever opens a session shows it in that tab, in the place of the one that
  // was there, and the tabs the window keeps are the one of the session shown.
  const tabMode = (mode: SessionTabMode): SessionTabMode => singleTab ? "single" : mode;
  const shapeTabs = (state: SessionTabsState) => singleTab ? singleSessionTab(state) : state;
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
  // The skills the search offers: those of the project in front, read each time the search opens, by host and project.
  const [searchSkills, setSearchSkills] = useState<{ key: string; skills: readonly SkillChoice[] } | null>(null);
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
        retention.invalidate();
        console.warn("[CodeAlta RPC] connection closed; inspect receipts before any explicit retry. No automatic replay.", { reason: connection.closeReason ?? null });
        // The connection ended for another reason: the panes are held, so the window offers the reload instead of
        // staying without a way out.
        if (connection.closeReason !== rpcSessionClosed) { setLinkLost(true); return; }
        // The host closed this document's session and nothing answers any more: only a reload opens a new one.
        hostLiveness.lost();
        const read = () => { try { const value = sessionStorage.getItem(sessionRecoveryKey); return value === null ? null : Number(value); } catch { return null; } };
        if (!reloadsAfterClose(connection.closeReason, fileEditors.anyDirty(), Date.now(), read())) return;
        try { sessionStorage.setItem(sessionRecoveryKey, String(Date.now())); } catch { /* Without the mark a second loss reloads again. */ }
        window.location.reload();
      };
      if (connection.closed.aborted) closed();
      else connection.closed.addEventListener("abort", closed, { once: true, signal: lifetime.signal });
    }).catch(error => {
      if (lifetime.signal.aborted) return;
      retention.invalidate();
      console.warn("[CodeAlta RPC] connection unavailable", { code: rpcFailureCode(error) });
      reportToHost("warning", `The window could not connect to its host (${rpcFailureCode(error)}): it offers a reload.`);
      // Without a connection no call of this page can answer: the window says so and offers the reload, the way out.
      setLinkLost(true);
    });
    return () => { lifetime.abort(); unsubscribe(); };
  }, []);
  const [timelineImages] = useState(() => createTimelineImageCache(promptImages.read));
  const [toolRecords] = useState(() => ({ records: createToolCallCache(toolCalls.read), outputs: createToolOutputStore(toolCalls.observe),
    prompts: createSystemPromptCache(toolCalls.readPrompt) }));
  const reminderCapability = useRef<ReturnType<typeof createMutationCapability> | undefined>(undefined);
  const [reminderActions] = useState(() => createReminderActions(reminder.create, reminder.delete, (target, reply) => {
    const capability = reminderCapability.current;
    if (capability?.canSubmit({ expectedEpoch: target.epoch })) capability.observe(reply);
  }, reminder.save));
  const [providerProbeHolds] = useState(() => new Set<string>());
  // The reads of the host that the panes share: one read for all the panes that ask for the same thing at once.
  const [hostReads] = useState(() => createHostReads());
  const [draftIndicators] = useState(createDraftIndicators);
  // The marks of an edited prompt follow the indicators by themselves: a keystroke does not render App.
  const tabDrafts = useMemo(() => ({ indicators: draftIndicators, selectedId: sessionId }), [draftIndicators, sessionId]);
  const [nextSendSelections] = useState(() => createNextSendSelectionStore(
    key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value)));
  const [steering] = useState(() => createSteeringSubmissions(sessionOperations.steer));
  const [compaction] = useState(() => createCompactionSubmissions(sessionOperations.compact));
  const [abortRuns] = useState(() => createAbortRunSubmissions(sessionOperations.abortRun));
  const [queue] = useState(() => createComposerQueue());
  // A session whose tab closes, or is taken by another session, while prompts wait above its composer keeps its pane,
  // out of sight: the pane is what sends them once the session is idle, as the pane of a tab that is not in front does.
  // The window follows the waiting prompts for that only.
  useSyncExternalStore(queue.subscribe, queue.getSnapshot);
  const [askActions] = useState(() => createAskActions(
    request => sessionAsks.answer(askWireRequest(request), { timeoutMilliseconds: 8000 }),
    request => sessionAsks.cancel(askWireRequest(request), { timeoutMilliseconds: 8000 })));
  const [sessionPaneOwners] = useState(() => new Map<string, ReturnType<typeof createSessionPaneOwners>>());
  // The fork and the rewind of sessions: one change at a time, and which pane shows the window that asks for it.
  const [sessionBranch] = useState(() => createSessionBranch(workspaceApi.forkSession, workspaceApi.rewindSession));
  const branchActions = useRef<Pick<SessionBranchHost, "fork" | "rewind">>({ fork: async () => ({ kind: "error", code: "unconfigured" }), rewind: async () => ({ kind: "error", code: "unconfigured" }) });
  const [branchHost] = useState<SessionBranchHost>(() => ({ requests: createBranchRequests(),
    fork: (...request) => branchActions.current.fork(...request), rewind: (...request) => branchActions.current.rewind(...request) }));
  // The sessions rewound from this window, with the count the host answered: their pane is read again at once,
  // before the list of sessions says the same.
  const [rewoundHere, setRewoundHere] = useState<ReadonlyMap<string, number>>(() => new Map());
  const [scrollMemory] = useState(createTimelineScrollMemory);
  const [projectOpening] = useState(() => createProjectOpening(workspace.openProject));
  const [projectArchive] = useState(() => createProjectArchive(workspace.archiveProject));
  const projectDeleteRpc = useRef(workspace.deleteProject);
  projectDeleteRpc.current = (request, options) => workspace.deleteProject(request, options)
    .then(reply => { mutation?.capability.observe({ status: reply.status, epoch: reply.hostEpoch }); return reply; });
  const [projectDeletion] = useState(() => createProjectDeletion((request, options) => projectDeleteRpc.current(request, options)));
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
  const [createSession] = useState(() => createSessionCreation(workspace.createSession, workspace.createSessionOutcome));
  // The worktree of the next session of a project, made while its prompt is typed.
  const [preparedWorktrees] = useState(() => createPreparedWorktrees({ prepare: workspace.prepareWorktree, read: workspace.preparedWorktreeState,
    discard: workspace.discardWorktree }));
  const preparedWorktreeList = useSyncExternalStore(preparedWorktrees.subscribe, preparedWorktrees.snapshot);
  const [renameSession] = useState(() => createSessionRename(workspace.renameSession));
  const [deleteSession] = useState(() => createSessionDeletion(workspace.deleteSession));
  const [moveSession] = useState(() => createSessionMove(workspace.moveSession));
  const [claudeCodeSessions] = useState(() => ({ list: workspace.claudeCodeSessions, import: workspace.importClaudeCodeSession,
    conversation: workspace.claudeCodeSessionConversation, openInVsCode: workspace.openClaudeCodeSessionInVsCode, preview: workspace.claudeCodeSessionPreview,
    conversations: workspace.claudeCodeConversations }));
  // The session whose project is being chosen, with the project it is in when its menu was used.
  const [movingSession, setMovingSession] = useState<SessionMoveDrag | null>(null);
  // The session the window that gives it another space is open for, with the space it is in now.
  const [movingToSpace, setMovingToSpace] = useState<Readonly<{ id: string; title: string; projectId: string | null; spaceId: string }> | null>(null);
  // The window of the sessions below a session; `deleting` when Delete led to it.
  const [childrenView, setChildrenView] = useState<Readonly<{ id: string; title: string; deleting: boolean }> | null>(null);
  // The row of a session dragged onto the row of another project, and that project; a Claude Code conversation dragged
  // onto a project is imported there.
  const rowDrag = useSessionRowDrag(id => currentSnapshot.current?.projects.find(project => project.id === id), (drag, project) => {
    if (drag.kind === "claude-code") { void importClaudeCodeInto(drag, project); return; }
    if (!movingPending.current) { setMenuTarget(null); void moveSessionTo(drag, project); }
  });
  const [movingBusy, setMovingBusy] = useState(false);
  const movingPending = useRef(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [deletingBusy, setDeletingBusy] = useState(false);
  const [deletingMessage, setDeletingMessage] = useState("");
  // The question of a deletion also asks to remove the worktree of the session; off each time it is asked.
  const [deletingWorktree, setDeletingWorktree] = useState(false);
  const deletingPending = useRef(false);
  const uncertainDelete = useRef<DeletedTarget | null>(null);
  const [deleteLocked, setDeleteLocked] = useState(false);
  // The session asked about beside its row before it is archived: one with child sessions, which go with it by default.
  // The archive of a session with child sessions that asks, and where it was asked: the Explorer or the composer.
  const [sessionArchiving, setSessionArchiving] = useState<Readonly<{ id: string; count: number; withChildren: boolean; from: "explorer" | "composer" }> | null>(null);
  const [sessionArchiveBusy, setSessionArchiveBusy] = useState(false);
  const [sessionArchiveNotice, setSessionArchiveText] = useState("");
  const sessionArchivePending = useRef(false);
  // The archived session whose info the saved sessions show, by its id: the catalog is read again meanwhile.
  const [archivedInfo, setArchivedInfo] = useState<string | null>(null);
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
  function setCreatingVisible(value: boolean | ((previous: boolean) => boolean)) { invalidateCreation(); writeCreatingVisible(value); }
  function setCreatingTitle(value: string) { invalidateCreation(); writeCreatingTitle(value); }
  const [creatingBusy, setCreatingBusy] = useState(false);
  // The scope a session is being created in, and whether its prompt is handed to it: its New session tab says that
  // the session starts.
  const [creatingScope, setCreatingScope] = useState<Readonly<{ projectId: string | null; handoff: boolean }> | null>(null);
  // The steps of the session that starts in the New session tab of a scope, as the host says them; a creation that
  // failed keeps them, with why, until Retry or Close.
  const [startSteps, setStartSteps] = useState<Readonly<{ requestId: string; projectId: string | null; fromDraft: boolean;
    steps: readonly SessionStartStep[]; error: string | null }> | null>(null);
  function newSessionTabLabel(project: { name: string } | null | undefined, starting: boolean) {
    if (starting) return project ? t("Starting… — {project}", { project: project.name }) : t("Starting chat…");
    return project ? t("New session — {project}", { project: project.name }) : t("New chat");
  }
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
  // A secondary window holds tabs only: the Explorer is the main window's.
  const railVisible = !secondary && projectRailVisible(railState, narrow);
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
    retention.invalidate();
    setRetentionLifetime(null);
    // A host that is slow to answer the pings keeps its lifetime: ending it here would retire this page for good on
    // the host, and leave every pane held after the host answers again. The stream itself says when it really ends.
    if (!spacesEpoch) { retention.invalidate(); return; }
    const lifetime = new AbortController();
    void (async () => {
      try {
        for await (const reply of await watchSessionRetention({ expectedEpoch: spacesEpoch, pageId: retention.pageId }, lifetime.signal)) {
          if (lifetime.signal.aborted || !reply.accepted) break;
          // Keep reading the lifetime while publication is held: stream closure must revoke the
          // owner immediately, not wait for that unrelated RPC to settle and expose stale work.
          void retention.connect(spacesEpoch).then(accepted => {
            if (accepted && !lifetime.signal.aborted) setRetentionLifetime(spacesEpoch);
          });
        }
      } catch (error) {
        // An unknown transport lifetime cannot authorize restoration or further edits.
        console.warn("[CodeAlta RPC] session retention lifetime failed", { code: rpcFailureCode(error) });
      }
      finally {
        if (!lifetime.signal.aborted) { lifetime.abort(); retention.invalidate(); setRetentionLifetime(null); setLinkLost(true); }
      }
    })();
    const refresh = () => { void retention.publish(spacesEpoch); };
    // Native window registration owns disconnect safety; an unload RPC is never a release.
    const leaving = () => { lifetime.abort(); retention.invalidate(); setRetentionLifetime(null); };
    window.addEventListener("storage", refresh);
    window.addEventListener("pagehide", leaving);
    const unsubscribeImages = submissions.imageDrafts.subscribe(refresh);
    const unsubscribeDrafts = draftIndicators.subscribe(refresh);
    return () => {
      window.removeEventListener("storage", refresh); window.removeEventListener("pagehide", leaving);
      unsubscribeImages(); unsubscribeDrafts(); lifetime.abort(); retention.invalidate();
    };
  }, [retention, spacesEpoch, submissions, draftIndicators]);
  useEffect(() => {
    if (spacesEpoch) return spacesHub.connect(spacesEpoch);
    if (hostAnswered) spacesHub.unavailable();
    return undefined;
  }, [spacesHub, spacesEpoch, hostAnswered]);
  useEffect(() => {
    if (spacesEpoch) return labelsHub.connect(spacesEpoch);
    if (hostAnswered) labelsHub.unavailable();
    return undefined;
  }, [labelsHub, spacesEpoch, hostAnswered]);
  // The host is told which space the window shows: the commands of the sessions read it as their current space.
  useEffect(() => { if (spacesEpoch && spacesState.available) spacesHub.shown(spaceId); }, [spacesHub, spacesEpoch, spacesState.available, spaceId]);
  useEffect(() => {
    // Watch only a host that answered once; a window that never connected already says so.
    if (demoMode || !hostAnswered) return;
    const timer = setInterval(() => void hostLiveness.check(), hostCheckInterval);
    return () => clearInterval(timer);
  }, [hostAnswered, hostLiveness]);
  // After a configuration save re-registered providers, re-read the inventory that pickers and settings show.
  function refreshConfiguration() {
    // A provider enabled, disabled or given to other spaces changes what the tiers give.
    if (spacesEpoch) void tierStore.refreshAll(spacesEpoch);
    return configuration.snapshot({}, { timeoutMilliseconds: 8_000 })
      .then(value => { if (creationAlive.current) setConfigurationState({ snapshot: value }); })
      .catch(() => { /* The previous inventory stays visible. */ });
  }

  const snapshot = workspaceState.kind === "ready" ? workspaceState.snapshot : undefined;
  // The filter at the top of the sidebar: while something is typed, the sidebar shows only the sessions, the projects
  // and the Claude Code sessions started elsewhere whose title has every word, open. Nothing of the tree is changed.
  const [sidebarText, setSidebarText] = useState("");
  const sidebarWords = useMemo(() => readQuery(sidebarText).words, [sidebarText]);
  // The Claude Code sessions started elsewhere the search and the filter look through, read when one of them is used.
  const [searchedClaudeCode, setSearchedClaudeCode] = useState<ClaudeCodeConversations | null>(null);
  // Whether that read answered (with nothing when it failed): until then, the filter does not say that nothing matches.
  const [claudeCodeSearched, setClaudeCodeSearched] = useState(false);
  const filterView = useMemo(() => sidebarWords.length && snapshot ? sidebarFilterView(snapshot, sidebarWords, searchedClaudeCode?.conversations) : null,
    [snapshot, sidebarWords, searchedClaudeCode]);
  // The scopes the filter opens that are folded while it is used; the tree of the sidebar is left as it is.
  const [filterFolded, setFilterFolded] = useState<ReadonlySet<string | null>>(() => new Set());
  const filtering = sidebarWords.length > 0;
  useEffect(() => { if (!filtering) setFilterFolded(new Set()); }, [filtering]);
  const fullListing = snapshot ? projectRailProjection(snapshot, projectSort, projectTree.favorites) : null;
  const projectListing = fullListing && filterView ? { ...fullListing, projects: fullListing.projects.filter(project => filterView.projects.has(project.id)),
    favorites: fullListing.projects.slice(0, fullListing.favorites).filter(project => filterView.projects.has(project.id)).length } : fullListing;
  // The labels of the shown space, and the parts of the Explorer when it lists the projects under them: while the list
  // is filtered, only the labels with a project that matches, unfolded.
  const spaceLabels = useMemo(() => labelsOfSpace(labelsState.labels, spaceId), [labelsState.labels, spaceId]);
  const railGroups = projectListing && labelsState.available && projectGrouping === "labels"
    ? projectRailGroups(projectListing.projects, projectListing.favorites, spaceLabels, !filterView) : [];
  // A label made from the Explorer at the top is in the shown space, or in every space from the default one.
  const labelSpace = spaceId === defaultSpaceId ? null : spaceId;
  const [newLabel, setNewLabel] = useState<{ busy: boolean; error: string | null } | null>(null);
  async function createTopLabel(name: string, icon: string, color: string) {
    setNewLabel({ busy: true, error: null });
    const outcome = await labelsHub.create(name, { icon: icon || null, color: color || null, space: labelSpace });
    setNewLabel(outcome.ok ? null : { busy: false, error: outcome.message ?? t("The label could not be created.") });
  }
  const labelEditing: LabelRowEditing = useMemo(() => ({ catalog: labelsState.labels, update: labelsHub.update, move: labelsHub.move, remove: labelsHub.remove,
    create: (name, look) => labelsHub.create(name, { ...look, space: look.parent ? null : labelSpace }) }), [labelsState.labels, labelsHub, labelSpace]);
  // A label dragged before, after or under another one; a project dragged onto a label gets it, and leaves the label
  // it was dragged out of unless Ctrl or Alt is held; dropped on the projects of no label, it leaves that label.
  function railDropAccepts(drag: RailDrag, drop: RailDrop): boolean {
    const all = labelsState.labels;
    if (drag.kind === "project") {
      if (!drop.labelId) return drag.from !== null;
      return drop.labelId !== drag.from && !all.find(label => label.id === drop.labelId)?.projectIds.includes(drag.projectId);
    }
    const target = labelMoveTarget(all, drag.labelId, drop);
    return !!target && canMoveLabel(drag.labelId, target.parent, all);
  }
  async function railDropped(drag: RailDrag, drop: RailDrop, copy: boolean) {
    const outcome = drag.kind === "project"
      ? await labelsHub.assign(drag.projectId, drop.labelId ? [drop.labelId] : [], drag.from && (!copy || !drop.labelId) ? [drag.from] : [])
      : await (async () => { const target = labelMoveTarget(labelsState.labels, drag.labelId, drop); return target ? labelsHub.move(drag.labelId, target.parent, target.index) : null; })();
    if (outcome && !outcome.ok) showToast({ message: outcome.message ?? t("The labels could not be changed."), intent: "danger" });
  }
  const railDrag = useRailDrag(railDropAccepts, (drag, drop, copy) => void railDropped(drag, drop, copy));
  // The projects of the shown space changed without a read of the catalog (a project joined it, here or through
  // a command): the window is given the catalog again as that space now has it.
  useEffect(() => {
    const full = catalog.current;
    if (!full || workspaceState.kind !== "ready" || !spacesState.loaded) return;
    const view = spaceView(spacesState.spaces, shownSpace.current, showOthers);
    const key = spaceViewKey(view);
    if (key === scopedView.current) return;
    scopedView.current = key;
    publishWorkspaceState({ kind: "ready", snapshot: withoutArchived(scopeSnapshot(full, view)) });
  }, [spacesState.spaces, spacesState.loaded, workspaceState.kind, showOthers]);
  function setShowOthers(value: boolean) {
    showingOthers.current = value;
    writeShowOthers(value);
    try { localStorage.setItem(showOthersKey, value ? "true" : "false"); } catch { /* The choice holds for this run. */ }
  }
  // Once, when the window first finds spaces that hold something: the default space shows what belongs to no other
  // space, and a click shows everything there again.
  useEffect(() => {
    if (!spacesState.loaded || !spacesState.available || workspaceState.kind !== "ready" || secondary) return;
    try { if (localStorage.getItem(toldDefaultKey)) return; } catch { return; }
    const full = catalog.current;
    const others = spacesState.spaces.filter(space => !space.isDefault);
    const hides = others.some(space => space.projectIds.length > 0)
      || !!full?.sessions.some(session => session.spaceId !== null && others.some(space => space.id === session.spaceId));
    // Told once, the first time the Default space hides something: spaces that are still empty hide nothing yet.
    if (!hides) return;
    try { localStorage.setItem(toldDefaultKey, "1"); } catch { return; }
    if (showingOthers.current) return;
    showToast({ intent: "primary", icon: "info-sign", timeout: 20_000,
      message: translate(shownLocale.current, "The Default space now shows what belongs to no other space."),
      action: { text: translate(shownLocale.current, "Show all"), onClick: () => { setShowOthers(true); showSpaceLatest.current(defaultSpaceId); } } }, "space:default");
  }, [spacesState.loaded, spacesState.available, spacesState.spaces, workspaceState.kind]);
  // A space that is gone (removed here, by a command or by another instance) gives its place to the default one,
  // and the tabs the window kept for it are forgotten.
  useEffect(() => {
    // The storage of the pages is the main window's: a secondary window keeps its space and its tabs in its window.
    if (!spacesState.loaded || !spacesState.available || secondary) return;
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
    if (!snapshot || !status || hostLost || status.hostAvailable && (!spacesEpoch || retentionLifetime !== spacesEpoch)) return;
    let current = true;
    const initial = !initialSelectionMade.current;
    // Initial restoration is the mount-time intent, not a new intent when its prerequisites finally
    // answer. A row/space selection made even before this effect first runs must win over saved state.
    const navigation = initial ? 0 : retentionNavigation.current, revision = browserRevision.current;
    const first = snapshot.sessions[0];
    // A new secondary window has the tabs that are moved into it, none of its own.
    const next = shapeTabs(initial ? restoredTabs ? reconcileSessionTabs(unheldSessionTabs(restoredTabs), snapshot)
      : first && !secondary ? openSessionTab(emptySessionTabs(), { sessionId: first.id,
        projectId: snapshot.projects.find(project => project.path === first.workspacePath)?.id ?? null, path: first.workspacePath })
        : emptySessionTabs() : tabs);
    const expose = () => {
      if (!initial) return;
      initialSelectionMade.current = true;
      setTabsReady(true);
      // Restoration is allowed to finish protection after the user navigates, but never to undo that
      // newer selection/settings action merely because its original acknowledgement arrived late.
      if (navigation !== retentionNavigation.current || revision !== browserRevision.current) return;
      applyTabState(next);
      if (restoredFileTabs) setFileTabs(reconcileFileTabs(unheldFileTabs(restoredFileTabs), snapshot));
    };
    if (spacesEpoch) void retention.restore(spacesEpoch, next.open.map(tab => tab.sessionId),
      () => current && creationAlive.current && snapshot === currentSnapshot.current, expose);
    else expose();
    return () => { current = false; };
  }, [snapshot, spacesEpoch, hostLost, retentionLifetime]);

  useEffect(() => {
    if (!snapshot || !tabsReady) return;
    // With a single tab, the tabs that were open when the setting was turned on leave here, but the one of the session shown.
    const valid = shapeTabs(reconcileSessionTabs(tabs, snapshot));
    // A previously verified active identity disappearing must not silently bind
    // the same ID to a different project/path or a duplicate catalog row.
    if (tabs.active && !resolveSessionTab(snapshot, tabs.active) && tabs.active.sessionId === sessionId) {
      const file = fileTabs.active;
      applyTabState(valid);
      if (file) activateFile(file);
      return;
    }
    const selection = selectedTab(snapshot, projectId, sessionId);
    // A session whose tab another window holds is shown there: that window comes to the front, and this one shows
    // what it showed.
    if (selection && !tabs.open.some(tab => tab.sessionId === selection.sessionId) && windowsHub.heldElsewhere(sessionTabRef(selection.sessionId))) {
      windowsHub.focusTab(sessionTabRef(selection.sessionId));
      const front = shownFront.current;
      if (front && "file" in front && fileTabs.open.some(tab => sameFileTab(tab, front.file))) {
        if (valid.active) applyTabState(valid); else selectProject(projectId);
        activateFile(front.file);
      } else if (valid.active) applyTabState(valid); else selectProject(projectId);
      return;
    }
    // The prompt of a new session that is shown takes the single tab: the session that was in it leaves.
    const next = selection ? openSessionTab(valid, selection, tabMode(openingMode(selection.sessionId))) : valid.active ? shapeTabs({ ...valid, active: null }) : valid;
    if (next !== tabs) setTabs(next);
    else if (selection && keptSession.current === selection.sessionId) keptSession.current = null;
  }, [snapshot, projectId, sessionId, tabsReady, tabs, singleTab]);
  // After the selection above, which reads what was in front before this change.
  useEffect(() => {
    if (fileTabs.active) shownFront.current = { file: fileTabs.active };
    else if (tabs.active) shownFront.current = { session: tabs.active };
  });
  // What is typed in the prompt of the session that is looked at keeps its tab, as an edit keeps the preview tab of
  // a code editor. The window does not follow the prompts: it is told of an edit, and only this one changes it.
  const keepEdited = useRef(() => { });
  keepEdited.current = () => {
    const preview = tabs.preview;
    if (preview && draftIndicators.visible(preview.sessionId, preview.sessionId)) setTabs(pinSessionTab(tabs, preview));
  };
  useEffect(() => draftIndicators.subscribe(() => keepEdited.current()), [draftIndicators]);
  // A secondary window keeps its tabs in its window, which the host writes: the storage of the pages is the main window's.
  useEffect(() => {
    if (tabsReady && snapshot) persistSessionTabs(value => { if (secondary) windowPage.sessionTabs = value; else localStorage.setItem(spaceStorageKey(sessionTabsKey, spaceId), value); }, tabs);
  }, [tabs, tabsReady, snapshot, spaceId]);
  useEffect(() => {
    if (tabsReady && snapshot) persistFileTabs(value => { if (secondary) windowPage.fileTabs = value; else localStorage.setItem(spaceStorageKey(fileTabsKey, spaceId), value); }, fileTabs);
  }, [fileTabs, tabsReady, snapshot, spaceId]);
  // The host is told which tabs this window holds: a tab is in one window, and a link to it brings that window to the front.
  useEffect(() => {
    if (!tabsReady || !snapshot) return;
    const refs = [...tabs.open.map(tab => sessionTabRef(tab.sessionId)), ...fileTabs.open.map(fileTabRef)];
    const active = fileTabs.active ? fileTabRef(fileTabs.active) : tabs.active ? sessionTabRef(tabs.active.sessionId) : null;
    windowsHub.report(refs, active, secondary ? writeWindowPage(windowPage) : null);
  }, [tabs, fileTabs, tabsReady, snapshot, windowsHub]);
  // The host holds the sessions this window has a tab of, so that the command line does not run one open here: the main
  // window says those of every space, a secondary window its own.
  useEffect(() => {
    if (!tabsReady || !snapshot) return;
    let storage: Storage | null = null;
    if (!secondary) { try { storage = localStorage; } catch { storage = null; } }
    spacesHub.openSessions(openSessionIds(tabs.open, storage, secondary ? null : spaceStorageKey(sessionTabsKey, spaceId)));
  }, [tabs, tabsReady, snapshot, spaceId, spacesHub, spacesState.available]);

  // A scope that becomes the selected one with a session is opened. At the start what was remembered is kept as it is; with
  // nothing remembered the selected scope is the one open, as it was before the Explorer remembered anything.
  const treeScope = useRef<{ id: string | null } | null>(null);
  useEffect(() => {
    if (!tabsReady) return;
    const seen = treeScope.current;
    treeScope.current = { id: projectId };
    // The chats stay closed until the user opens them: a start with nothing selected does not open them.
    // The row of a scope selects it without opening it: only a session that is shown opens the scope it is in.
    if (seen ? seen.id !== projectId && sessionId !== null : !storedProjectTree && projectId !== null) setProjectTree(current => expandScope(current, projectId));
  }, [projectId, tabsReady]);

  function applyTabState(next: SessionTabsState) {
    protectNavigation(next.open.map(tab => tab.sessionId), () => applyAdmittedTabState(next));
  }
  function applyAdmittedTabState(next: SessionTabsState) {
    writeTabs(next);
    const target = next.active;
    setMenuTarget(null);
    if (target && selectedScope.current !== target.projectId) selectProject(target.projectId, target.sessionId);
    else setSessionId(target?.sessionId ?? null);
  }
  // Shows the tab of a session. One that has none is given its own, unless it is only looked at (`preview`); a tab
  // that is shown again stays what it is.
  function selectSessionTab(tab: SessionTab, mode: SessionTabMode = "permanent") {
    if (!snapshot || snapshot !== currentSnapshot.current || !resolveSessionTab(snapshot, tab)) return;
    applyTabState(openSessionTab(reconcileSessionTabs(tabs, snapshot), tab, tabMode(mode)));
  }
  // A double click on the row of a session: its tab is its own, and stays when another session is looked at.
  function keepSession(scope: string | null, session: WorkspaceSession) {
    const tab = snapshot ? selectedTab(snapshot, scope, session.id) : null;
    if (tab) selectSessionTab(tab);
  }
  // The row of a scope in the Explorer shows the prompt of a new session of it. The prompt takes the transient tab:
  // a session that was only looked at leaves it. Kept (a double click), it has a tab of its own, as many scopes can.
  // With a single tab the prompt takes that tab once its scope is selected, and no prompt has a tab of its own.
  function showScopeDraft(id: string | null, keep = false) {
    const draft = snapshot && snapshot === currentSnapshot.current ? draftTabOf(snapshot, id) : null;
    if (draft && !singleTab) {
      const valid = reconcileSessionTabs(tabs, snapshot!);
      const next = keep ? openDraftTab(valid, draft) : hasDraftTab(valid, draft) ? valid : closePreviewTab(valid);
      if (next !== tabs) setTabs(next);
    }
    selectProject(id);
  }
  // The prompt of a new session that has a tab of its own is closed like any tab: the tab goes, what was typed stays
  // with its scope. When it was the one shown, a session is shown in its place if one is open.
  function closeScopeDraft(draft: DraftTab) {
    if (!snapshot || snapshot !== currentSnapshot.current) return;
    const valid = reconcileSessionTabs(tabs, snapshot);
    const next = closeDraftTab(valid, draft);
    if (next === valid) return;
    // The worktree made for the session of that tab is no longer wanted.
    if (draft.projectId !== null) preparedWorktrees.discard(draft.projectId);
    advanceBrowserRevision();
    tabFocusPending.current = true;
    // A prompt that is closed stays closed, whatever is typed in it.
    leftPrompt.current = null;
    const shown = !valid.active && !fileTabs.active && draftKey(draft) === draftKey({ projectId, path: draft.path });
    if (shown && next.open.length) applyTabState({ ...next, active: next.open[0] }); else setTabs(next);
  }
  // Several tabs closed at once, from the menu of a tab or of a pane: one change of the tabs. When what is in front
  // closes, the tab the command was asked from comes in front. An editor with unsaved changes stays open, and the
  // first of them asks what to do with its changes, as its close button does.
  function closeTabs(closing: DockTabs, from: DockTab | null) {
    if (!snapshot || snapshot !== currentSnapshot.current) return;
    const valid = reconcileSessionTabs(tabs, snapshot);
    const unsaved = closing.files.filter(file => fileEditors.dirty(fileTabKey(file)));
    const closed = { ...closing, files: closing.files.filter(file => !unsaved.includes(file)) };
    const front = fileTabs.active;
    const show = unsaved.length ? null : shownAfterClosing(closed, { file: front, session: valid.active, scope: draftTabOf(snapshot, projectId) }, from);
    const next = closeSessionTabs(valid, closed.sessions, closed.drafts, show && "session" in show ? show.session : null);
    if (next === valid && !closing.files.length) return;
    advanceBrowserRevision();
    closedTabKinds.current = [...closedTabKinds.current, ...valid.open.filter(tab => !next.open.includes(tab)).map(() => "session" as const)].slice(-64);
    tabFocusPending.current = true;
    leftPrompt.current = null;
    for (const file of closed.files) closeFile(file);
    const key = (tab: SessionTab | null) => tab ? tabKey(tab) : null;
    if (show && "draft" in show) {
      // The prompt of a new session is shown by selecting its scope with no session.
      applyTabState({ ...next, active: null });
      if (show.draft.projectId !== projectId) selectProject(show.draft.projectId);
    } else if (key(next.active) !== key(valid.active)) {
      // Closing the session behind a tab of a project does not leave that tab.
      const kept = front && !closed.files.some(file => sameFileTab(file, front)) ? front : null;
      applyTabState(next);
      if (kept) activateFile(kept);
    } else if (next !== tabs) setTabs(next);
    if (show && "file" in show) activateFile(show.file);
    if (unsaved.length) closeFile(unsaved[0]);
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
    const next = findSpace(spacesHub.getSnapshot().spaces, id).id;
    // A secondary window keeps the space it was created in: what is of another space is shown by the main window.
    if (secondary && next !== shownSpace.current) { windowsHub.focus("main"); return false; }
    const entering = spaceTabs.current.get(next)?.tabs ?? restoreSessionTabs(() => localStorage.getItem(spaceStorageKey(sessionTabsKey, next)));
    const ids = entering?.open.map(tab => tab.sessionId) ?? [];
    if (target?.sessionId) ids.push(target.sessionId);
    let shown = false;
    protectNavigation(ids, () => { shown = showAdmittedSpace(id, target, discard); });
    return shown;
  }
  function showAdmittedSpace(id: string, target?: SpaceTarget, discard = false): boolean {
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
    const view = spaceView(known, next, showingOthers.current);
    scopedView.current = spaceViewKey(view);
    const scoped = withoutArchived(scopeSnapshot(full, view));
    publishWorkspaceState({ kind: "ready", snapshot: scoped });
    const kept = spaceTabs.current.get(next);
    const entering = shapeTabs(reconcileSessionTabs(kept?.tabs ?? restoreSessionTabs(() => localStorage.getItem(spaceStorageKey(sessionTabsKey, next))) ?? emptySessionTabs(), scoped));
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
  // A dialog that saves before it acts (leave a space, exit, close a file) can always be canceled, also while the host
  // saves: the save goes on, and what was to follow it does not happen. Each of them takes a token; a cancel moves it on.
  const saveFollowUp = useRef(0);
  function cancelSaveFollowUp() { saveFollowUp.current++; }
  async function saveAndLeaveSpace(leaving: NonNullable<typeof leavingSpace>) {
    const intent = ++saveFollowUp.current;
    setLeavingSpace({ ...leaving, busy: true });
    for (const tab of leaving.tabs) {
      if (await fileEditors.save(fileTabKey(tab))) continue;
      if (creationAlive.current && intent === saveFollowUp.current) { setLeavingSpace(null); activateFile(tab); }
      return;
    }
    if (creationAlive.current && intent === saveFollowUp.current) showSpace(leaving.spaceId, leaving.target, true);
  }
  // A session the shown space does not list: its own space is shown first.
  function revealSession(session: WorkspaceSession) {
    const target = { projectId: session.scopeKind === "project" ? session.projectId : null, sessionId: session.id };
    if (currentSnapshot.current?.sessions.some(candidate => candidate.id === session.id)) selectProject(target.projectId, target.sessionId);
    else showSpace(sessionHome(spacesHub.getSnapshot().spaces, { spaceId: session.spaceId, projectId: target.projectId }).id, target);
  }
  const revealSessionLatest = useRef(revealSession); revealSessionLatest.current = revealSession;
  // Shows a space on one of its sessions: the one that waits for the user there. A session that just started is read first.
  async function openSpaceSession(id: string, target: SpaceTarget) {
    if (target.sessionId && !catalog.current?.sessions.some(session => session.id === target.sessionId)) {
      try {
        const fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 });
        if (!creationAlive.current) return;
        if (fresh.configured) publishWorkspaceState({ kind: "ready", snapshot: fresh });
      } catch { /* The space is shown all the same: its Explorer has the session once it is listed. */ }
    }
    showSpaceLatest.current(id, catalog.current?.sessions.some(session => session.id === target.sessionId) ? target : undefined);
    focusPromptSoon();
  }
  const openSpaceSessionLatest = useRef(openSpaceSession); openSpaceSessionLatest.current = openSpaceSession;
  // A project that another space has joins the one shown, and is selected there.
  async function joinShownSpace(id: string) {
    const outcome = await spacesHub.assign(id, [shownSpace.current]);
    if (!outcome.ok || !creationAlive.current) return;
    const fresh = await refreshProjects(creationRefresh.current.signal);
    if (fresh?.projects.some(project => project.id === id)) { selectProject(id); focusPromptSoon(); }
  }
  // What reaches the window from elsewhere: a command asks for a space, or changed the spaces or the projects.
  // A change of the spaces, or of a session another process writes (listed, grown, released): the list is read again.
  const spaceRequest = useRef<(kind: SpacesRequestKind, id: string | null) => void>(() => { });
  spaceRequest.current = (kind, id) => {
    if (kind === "show" && id) showSpace(id);
    else readSessionList.current();
    // A window or a command that changes the labels says the spaces changed.
    if (kind === "changed") void labelsHub.refresh();
  };
  useEffect(() => { spacesHub.onRequest((kind, id) => spaceRequest.current(kind, id)); }, [spacesHub]);
  // The sessions that need the user in a space that is not shown: listed at the foot of the Explorer, and said
  // once, when a session starts to wait, in a message that leads to it.
  const calls = useMemo(() => spaceCalls(spacesState.spaces, spaceId, spacesState.sessions, showOthers), [spacesState.spaces, spacesState.sessions, spaceId, showOthers]);
  const saidCalls = useRef<{ spaceId: string; sessions: ReadonlySet<string> } | null>(null);
  useEffect(() => {
    const said = saidCalls.current;
    const waiting = calls.filter(call => call.waiting);
    saidCalls.current = { spaceId, sessions: new Set(waiting.map(call => call.sessionId)) };
    // What waited when the window started, or when it left a space, is not news. The main window says it, not each window.
    if (!said || said.spaceId !== spaceId || secondary) return;
    for (const call of waiting) {
      if (said.sessions.has(call.sessionId)) continue;
      showToast({ intent: "warning", icon: "help", timeout: 12_000,
        message: translate(shownLocale.current, "{title} waits for you in {space}", { title: call.title || translate(shownLocale.current, "A session"), space: call.space.name }),
        action: { text: translate(shownLocale.current, "Show"), onClick: () => void openSpaceSessionLatest.current(call.space.id, call) } });
    }
  }, [calls, spaceId]);
  // The sessions that wait for the user (a question, a command to allow, a form): marked in the Explorer and on their
  // tab. One of the space that is shown is also said once, when it starts to wait while another session is on screen.
  const waitingSessions = useMemo(() => new Set(spacesState.sessions.filter(session => session.waiting).map(session => session.sessionId)), [spacesState.sessions]);
  // The sessions another process holds (the command line, another instance): marked, and read-only here meanwhile.
  const elsewhereSessions = useMemo(() => new Map(spacesState.sessions.flatMap(session => {
    const kind = elsewhereKind(session.elsewhere);
    return kind ? [[session.sessionId, { kind, journalLength: session.journalLength }] as const] : [];
  })), [spacesState.sessions]);
  // The sessions under each session that wait for the user: shown above its composer.
  const waitingBelow = useMemo(() => waitingSubSessions(spacesState.sessions), [spacesState.sessions]);
  const workingBelow = useMemo(() => subSessionsAtWork(spacesState.sessions), [spacesState.sessions]);
  const saidWaiting = useRef<ReadonlySet<string> | null>(null);
  useEffect(() => {
    const said = saidWaiting.current;
    saidWaiting.current = waitingSessions;
    // What waited when the window started is not news. The main window says it, not each window.
    if (!said || secondary) return;
    for (const session of spacesState.sessions) {
      // The session on screen, or one under it, whose pane already shows it waits, is not said again.
      if (!session.waiting || said.has(session.sessionId) || session.sessionId === sessionId || (sessionId !== null && session.ancestors.includes(sessionId))
        || !viewLists(spaceView(spacesState.spaces, spaceId, showOthers), session)) continue;
      showToast({ intent: "warning", icon: "help", timeout: 12_000,
        message: translate(shownLocale.current, "{title} waits for you", { title: session.title || translate(shownLocale.current, "A session") }),
        action: { text: translate(shownLocale.current, "Show"), onClick: () => { selectProject(session.projectId, session.sessionId); focusPromptSoon(); } } });
    }
  }, [waitingSessions]);
  // A session that went on with another model after a failure of its provider is said once, with what happened.
  const saidFallbacks = useRef<ReadonlySet<string> | null>(null);
  useEffect(() => {
    const { notices, said } = newFallbackNotices(saidFallbacks.current, spacesState.sessions);
    saidFallbacks.current = said;
    if (secondary) return;
    for (const notice of notices) {
      showToast({ intent: "primary", icon: "swap-horizontal", timeout: 12_000,
        message: translate(shownLocale.current, "{title}: {message}", { title: notice.title || translate(shownLocale.current, "A session"), message: notice.message }),
        action: { text: translate(shownLocale.current, "Show"), onClick: () => { selectProject(notice.projectId, notice.sessionId); focusPromptSoon(); } } });
    }
  }, [spacesState.sessions]);
  // A prompt for an archived session (an agent, a reminder, an automation) takes it out of the archive before it
  // runs: the window says so once, and lists the session again.
  const saidRestores = useRef<ReadonlySet<string> | null>(null);
  useEffect(() => {
    const { notices, said } = newRestoredNotices(saidRestores.current, spacesState.sessions);
    saidRestores.current = said;
    if (notices.length) readSessionList.current();
    if (secondary) return;
    for (const notice of notices) {
      showToast({ intent: "primary", icon: "unarchive", timeout: 10_000,
        message: translate(shownLocale.current, "Restored to run {title}", { title: notice.title || translate(shownLocale.current, "A session") }),
        action: { text: translate(shownLocale.current, "Show"), onClick: () => { selectProject(notice.projectId, notice.sessionId); focusPromptSoon(); } } });
    }
  }, [spacesState.sessions]);
  function activateFile(tab: FileTab | null) { setFileTabs(state => activateFileTab(state, tab)); }
  // A tab is brought to the front by what happens in it while it is shown. A focus that lands in a tab that is not
  // shown, as the one a closing popover gives back to its button once another tab was pressed, leaves it behind.
  function activateShown(tab: FileTab, visible: boolean) { return () => { if (visible) activateFile(tab); }; }
  function openFile(tab: FileTab) {
    // A tab that another window holds is shown there: that window comes to the front, on it.
    const ref = fileTabRef(tab);
    if (!fileTabs.open.some(value => sameFileTab(value, tab)) && windowsHub.heldElsewhere(ref)) { windowsHub.focusTab(ref); return; }
    openFileHere(tab);
  }
  // Open run on the card of a run, or a run of the Workflows tab: the tab of that run.
  const [openRun] = useState(() => (run: Readonly<{ runId: string; title: string | null }>) => openFileLatest.current(workflowRunTab(run)));
  const openFileLatest = useRef(openFile); openFileLatest.current = openFile;
  function openFileHere(tab: FileTab) {
    // At the tab limit a file with unsaved edits is never the one that makes room.
    setFileTabs(state => openFileTab(state, tab, value => fileEditors.dirty(fileTabKey(value))));
  }
  // Opens the changes of a project's repository in their tab, beside the tab that is shown: the ones of the
  // folder of the project, or of the worktree a session works in.
  function showChanges(project: Readonly<{ id: string; path: string }>, path: string | null = null, worktree?: string | null, review: "changes" | "branch" | null = null) {
    if (path !== null || worktree !== undefined || review) setChangeRequests(current => new Map(current).set(project.id, { path, worktree, review }));
    openFile(changesTab(project));
  }
  const showChangesLatest = useRef(showChanges); showChangesLatest.current = showChanges;
  // Opens the code editor of a project in its tab: on a file, at a place in it, or with the files of the project.
  function openEditor(project: Readonly<{ id: string; path: string }>, request: EditorRequest) {
    setEditorRequests(current => new Map(current).set(project.id, request));
    openFile(editorTab(project));
  }
  const openEditorLatest = useRef(openEditor); openEditorLatest.current = openEditor;
  // A review is done where its target is shown: a file of the project folder in the editor, rendered when it is a
  // document; the changes, or a file of a worktree, in the Changes tab of the checkout.
  function openReview(scope: ReviewScope, target: ReviewTarget) {
    const project = snapshot?.projects.find(candidate => candidate.id === scope.projectId);
    if (!project) return;
    if (target.kind === "file" && scope.checkout === null) openEditor(project, { path: target.path, line: null, column: null, explorer: null, view: "preview" });
    else showChanges(project, target.kind === "file" ? target.path : null, scope.checkout, target.kind === "file" ? null : target.kind);
  }
  const openReviewLatest = useRef(openReview); openReviewLatest.current = openReview;
  const [reviewOpener] = useState(() => (scope: ReviewScope, target: ReviewTarget) => openReviewLatest.current(scope, target));
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
  const [showProjectChanges] = useState(() => (project: Readonly<{ id: string; path: string }>, worktree?: string | null, review: "changes" | "branch" | null = null) =>
    showChangesLatest.current(project, null, worktree, review));
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
  // The settings page of a plugin that Settings shows now, as the tab its canvas is drawn as: what is not shown has its instance closed.
  const shownPluginPage = useRef<FileTab | null>(null);
  shownPluginPage.current = settingsOpen && settingsSection === "plugin" && pluginPage ? pluginSettingsTab(pluginPage) : null;
  const [settingsCanvasInstances] = useState(() => createCanvasInstances(canvasHub, () => shownPluginPage.current ? [shownPluginPage.current] : []));
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
    // The settings page of a plugin opens in Settings, in the main window, whatever space the window shows.
    const declared = canvasHub.getCatalog().find(item => item.pluginKey === request.pluginKey && item.id === request.canvasId);
    if (request.placement === "settings" || declared && canvasPlacement(declared) === "Settings") {
      if (!secondary && owned) openPluginSettings(declared ? pluginSettingsPage(declared)
        : { pluginKey: request.pluginKey, canvasId: request.canvasId, title: request.title ?? request.canvasId, description: null, icon: request.icon, plugin: request.plugin });
      return;
    }
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
    // Every window hears it: the one that holds the tab shows it, else the main one; another window brought to the front shows it.
    const ref = fileTabRef(tab);
    if (!windowsHub.handles(ref)) return;
    if (windowsHub.heldElsewhere(ref)) { if (request.focus) windowsHub.focusTab(ref); return; }
    if (space === shownSpace.current) { setFileTabs(state => bringCanvasTab(state, tab, request.focus, keep)); return; }
    // A secondary window keeps its space: the tabs of the other spaces are the main window's.
    if (secondary) return;
    // Another space: its tabs, in memory when the window left it in this run and in storage otherwise. The window stays where it is.
    const kept = spaceTabs.current.get(space);
    const next = addCanvasTabToSpace({ kept: kept?.files, read: () => localStorage.getItem(spaceStorageKey(fileTabsKey, space)),
      write: value => localStorage.setItem(spaceStorageKey(fileTabsKey, space), value), closed: removed => canvasInstances.closed(removed, space) }, tab, request.focus, keep);
    if (kept) spaceTabs.current.set(space, { ...kept, files: next });
  }
  // A plugin closed an instance that no tab listened to: its tab goes with it, wherever it is. The tab of a space that is not shown is
  // taken out of the tabs of that space, in memory when the window left it in this run and in storage otherwise.
  function closeCanvasRequest(closed: CanvasClosedInstance) {
    const tab = closedCanvasTab(closed), space = closed.spaceId ?? shownSpace.current;
    if (space !== shownSpace.current) {
      if (secondary) return;
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
  // The canvases the plugins declare now, read again whenever the plugins change: the tabs, which the page of the canvases, the search and
  // the menus list, and the settings pages of the plugins, which Settings > Extensions lists.
  const declaredCanvases = useSyncExternalStore(canvasHub.subscribeCatalog, canvasHub.getCatalog);
  const canvasCatalog = useMemo(() => tabCanvases(declaredCanvases), [declaredCanvases]);
  const pluginPages = useMemo(() => pluginSettingsPages(declaredCanvases), [declaredCanvases]);
  // Opens the tab of a canvas in the space that is shown: one tab for each canvas, project, session and key. A settings page opens in Settings.
  function openCanvas(item: CanvasItem, target: CanvasTarget) {
    if (!owned) return;
    if (canvasPlacement(item) === "Settings") { if (!secondary) openPluginSettings(pluginSettingsPage(item)); return; }
    openFile(canvasTabOf(item, target));
  }
  // What the menu of a session row offers of the plugins: their canvases about a session, a few lines and then the page of the canvases.
  // `scopeProject` is the project of the scope the row is listed under: the selected one, or the one of an open scope that is not
  // selected, whose sessions never borrow the project in front.
  function sessionCanvasEntries(session: WorkspaceSession, scopeProject: CanvasScopeProject | null | undefined): SessionMenuEntry[] {
    return sessionCanvasMenuEntries(owned ? canvasCatalog : [], session, scopeProject, {
      label: title => t("Open {title}", { title }), more: t("More…"),
      open: (item, target) => { dismissSessionMenu(false); openCanvas(item, target); },
      openPage: () => { dismissSessionMenu(false); openFile(canvasesTab); },
    });
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
    openDocumentation: (page, anchor) => openDocumentationLatest.current(page, anchor),
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
  // The windows: a tab moved to another window, and what the host asks of this one. A move closes the tab here and opens
  // it there; the sessions, the terminals and the canvases are the host's, so the window that receives a tab attaches to
  // them again, and the drafts of the prompts are in the storage the pages share.
  type MovedTab = Readonly<{ session: SessionTab }> | Readonly<{ file: FileTab }>;
  const movedRef = (tab: MovedTab) => "session" in tab ? sessionTabRef(tab.session.sessionId) : fileTabRef(tab.file);
  // Why a tab cannot move now; null when it can.
  function unmovable(tab: MovedTab): MessageKey | null {
    if ("session" in tab) {
      const id = tab.session.sessionId, epoch = status?.hostEpoch;
      return submissions.pending(id) || (epoch && queue.sessions(epoch).includes(id)) ? "A prompt is being sent in this session: the tab moves once it is sent." : null;
    }
    return fileEditors.dirty(fileTabKey(tab.file)) ? "Save the files of this editor before moving it." : null;
  }
  // Takes a tab out of this window without listing it among the closed tabs: it went to another window.
  function releaseTab(tab: MovedTab) {
    if ("session" in tab) {
      if (!snapshot || snapshot !== currentSnapshot.current) return;
      const closed = closeSessionTab(reconcileSessionTabs(tabs, snapshot), tab.session);
      const next = { ...closed, closed: closed.closed.filter(value => value.sessionId !== tab.session.sessionId) };
      const file = fileTabs.active;
      advanceBrowserRevision();
      if (tabs.active?.sessionId === tab.session.sessionId) { applyTabState(next); if (file) activateFile(file); }
      else setTabs(next);
      return;
    }
    const file = tab.file;
    if (isEditorTab(file)) setEditorRequests(current => { const next = new Map(current); next.delete(file.projectId); return next; });
    setFileTabs(state => { const next = closeFileTab(state, file); return { ...next, closed: next.closed.filter(value => !sameFileTab(value, file)) }; });
  }
  /** Moves a tab of this window to another window, or to a new one (no target). */
  async function moveTabToWindow(tab: MovedTab, target: string | null, requestId: string | null = null): Promise<string> {
    const why = unmovable(tab);
    if (why) {
      if (requestId) windowsHub.refuseMove(requestId, "session" in tab ? "not_movable" : "unsaved");
      showToast({ message: t(why), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return "not_movable";
    }
    const moved = await windowsHub.moveTab(movedRef(tab), transferOf(tab), spaceBound(tab), target, requestId);
    if (moved === "ok") releaseTab(tab);
    else showToast({ message: t(moved === "other_space" ? "This window shows another space." : moved === "unavailable" ? "No window can be opened now." : "The tab could not be moved."),
      intent: "warning", icon: "warning-sign", timeout: 6000 });
    return moved;
  }
  const moveTabLatest = useRef(moveTabToWindow); moveTabLatest.current = moveTabToWindow;
  // The tab of a reference, among the tabs of this window.
  function tabOfRef(ref: string): MovedTab | null {
    const session = tabs.open.find(tab => sessionTabRef(tab.sessionId) === ref);
    if (session) return { session };
    const file = fileTabs.open.find(tab => fileTabRef(tab) === ref);
    return file ? { file } : null;
  }
  // A tab moved into this window: opened as its own tab, once the tabs of the window are there.
  const arrivals = useRef<WindowsRequest[]>([]);
  function receiveTab(request: WindowsRequest) {
    const moved = readTransfer(request.payload);
    if (!moved) return;
    if (!tabsReady || !snapshot || snapshot !== currentSnapshot.current) { arrivals.current = [...arrivals.current, request].slice(-32); return; }
    windowsHub.claim(movedRef(moved));
    setExternalDrag(null);
    if (request.x !== null && request.y !== null)
      setArrival({ nodeId: "session" in moved ? sessionNodeId(moved.session) : fileNodeId(moved.file), x: request.x, y: request.y });
    if ("session" in moved) {
      keptSession.current = moved.session.sessionId;
      selectSessionTab(moved.session, "permanent");
    } else openFileHere(moved.file);
    tabFocusPending.current = true;
  }
  useEffect(() => {
    if (!tabsReady || !snapshot || !arrivals.current.length) return;
    const waiting = arrivals.current;
    arrivals.current = [];
    for (const request of waiting) receiveTab(request);
  });
  // Brings a tab of this window to the front: another window asked for it.
  function focusTabRef(ref: string) {
    const tab = tabOfRef(ref);
    if (!tab) return;
    if ("session" in tab) selectSessionTab(tab.session, "preview"); else activateFile(tab.file);
    tabFocusPending.current = true;
  }
  // The window is asked to close (its close button, Close window, `alta window close`): its tabs close as each would,
  // the editors with unsaved edits asking first; the last tab that leaves closes the window.
  function closeWindowTabs() {
    if (!secondary) return;
    if (!tabs.open.length && !(tabs.drafts?.length) && !fileTabs.open.length) { windowsHub.closeSelf(); return; }
    closeTabs({ sessions: tabs.open, drafts: tabs.drafts ?? [], files: fileTabs.open }, null);
  }
  const windowRequest = useRef<(request: WindowsRequest) => void>(() => { });
  windowRequest.current = request => {
    if (request.kind === "open") receiveTab(request);
    else if (request.kind === "focus" && request.tab) focusTabRef(request.tab);
    else if (request.kind === "move" && request.tab && request.requestId) {
      const tab = tabOfRef(request.tab);
      if (tab) void moveTabLatest.current(tab, request.target === "new" ? null : request.target, request.requestId);
      else windowsHub.refuseMove(request.requestId, "not_found");
    } else if (request.kind === "close") closeWindowTabs();
    else if (request.kind === "drag-over") tabDragOver.current(request);
  };
  // What another window shows of a tab dragged over this one: the drop indicator, drawn by the dock.
  const [externalDrag, setExternalDrag] = useState<Readonly<{ x: number; y: number; refused: string | null }> | null>(null);
  const tabDragOver = useRef<(request: WindowsRequest) => void>(() => { });
  tabDragOver.current = request => setExternalDrag(request.x !== null && request.y !== null
    ? { x: request.x, y: request.y, refused: request.refused ? t("This window shows another space.") : null } : null);
  // A tab dropped on this window from another one: it goes to the pane it was dropped on.
  const [arrival, setArrival] = useState<Readonly<{ nodeId: string; x: number; y: number }> | null>(null);
  // A tab dragged out of this window: the window under the pointer draws where it would go, and a release moves it there,
  // or to a new window where no window is.
  async function dropTabOutside(tab: MovedTab, point: Readonly<{ x: number; y: number }>) {
    const why = unmovable(tab);
    if (why) { showToast({ message: t(why), intent: "warning", icon: "warning-sign", timeout: 6000 }); return; }
    const moved = await windowsHub.drop(movedRef(tab), transferOf(tab), spaceBound(tab), point);
    if (moved === "ok") releaseTab(tab);
    else if (moved !== "here") showToast({ message: t(moved === "other_space" ? "This window shows another space." : moved === "unavailable" ? "No window can be opened now." : "The tab could not be moved."),
      intent: "warning", icon: "warning-sign", timeout: 6000 });
  }
  const movedOf = (dock: DockTab): MovedTab | null => "session" in dock ? { session: dock.session } : "file" in dock ? { file: dock.file } : null;
  const tabDragOut = windowsState.current && windowsState.canDrag ? {
    over: (dock: DockTab, point: Readonly<{ x: number; y: number }> | null) => { const tab = movedOf(dock); if (tab) void windowsHub.dragOver(movedRef(tab), spaceBound(tab), point); },
    drop: (dock: DockTab, point: Readonly<{ x: number; y: number }>) => { const tab = movedOf(dock); if (tab) void dropTabOutside(tab, point); },
  } : undefined;
  useEffect(() => {
    windowsHub.onRequest(request => windowRequest.current(request));
    return windowsHub.connect();
  }, [windowsHub]);
  // What the menu of a tab offers of the windows: the other open windows and the saved ones, and a new window.
  function windowMenuEntries(dock: DockTab): SessionMenuEntry[] {
    if (!windowsState.current || "draft" in dock) return [];
    const tab: MovedTab = "session" in dock ? { session: dock.session } : { file: dock.file };
    const why = unmovable(tab);
    const others = windowsState.windows.filter(other => other.id !== windowsState.current?.id && (other.open || other.saved));
    return [
      { key: "d-windows", divider: true, label: t("Move to window") },
      ...others.map(other => ({ key: `window:${other.id}`, label: other.open ? windowName(t, other) : t("{name} (closed)", { name: windowName(t, other) }),
        iconNode: <WindowIcon window={other} size={15} />, disabled: !!why, onSelect: () => void moveTabToWindow(tab, other.id) })),
      { key: "window:new", label: t("New window"), icon: "windowMove" as const, disabled: !!why, onSelect: () => void moveTabToWindow(tab, null) },
    ];
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
  // Exit now: the sessions at work stop, as after the question of an exit.
  const requestImmediateExit = useRef(() => quitApplication(true));
  // A project that a start of alta added (`alta .`): the window reads the catalog again and selects it.
  const showHostProject = useRef(async (_projectId: string, _spaceId: string | null) => {});
  const nativeCloseTab = useRef<() => void>(() => {});
  requestExit.current = exitApplication;
  async function saveAllAndExit(unsaved: FileTab[]) {
    const intent = ++saveFollowUp.current;
    setExiting({ tabs: unsaved, busy: true });
    for (const tab of unsaved) {
      if (await fileEditors.save(fileTabKey(tab))) continue;
      if (creationAlive.current && intent === saveFollowUp.current) { setExiting(null); activateFile(tab); }
      return;
    }
    if (intent !== saveFollowUp.current) return;
    setExiting(null);
    quitApplication();
  }
  // The host exits the application; while sessions run it first has this page ask (see the shell's notices).
  function quitApplication(confirmed = false) {
    void desktopShell.exit({ confirmed }, { timeoutMilliseconds: 15_000 })
      .then(reply => { if (reply.status !== "ok") closeApplicationWindow(); }, closeApplicationWindow);
  }
  // A close that lasts: the host ends its process when it allows it. An answer means it did not, and the notice stays.
  function forceExit() {
    void desktopShell.forceExit({}, { timeoutMilliseconds: 5_000 })
      .catch(error => console.warn("[CodeAlta RPC] force exit failed", { code: rpcFailureCode(error) }));
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
  const [updateNotice, setUpdateNotice] = useState<ToastProps | null>(null);
  const announcedUpdates = useRef(new Set<string>());
  const pendingUpdate = useRef<AvailableUpdate | null>(null);
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
  function announceUpdate() {
    const available = updateToAnnounce(announcedUpdates.current, pendingUpdate.current, document.visibilityState === "visible");
    if (!available) return;
    announcedUpdates.current.add(available.version);
    setUpdateNotice(updateNotification(available, shownLocale.current, openReleaseNotes, installUpdate));
  }
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
      pendingUpdate.current = availableUpdate(value);
      announceUpdate();
    }, () => {
      // A later question that gets no answer leaves what the page knows.
      if (!signal.aborted && !updateChecked.current) setAppUpdateResult({ status: "failed", packageId: "CodeAlta", currentVersion: "", latestVersion: null, command: null, releaseNotes: null, canInstall: false, installed: null });
    });
  }
  // A .NET runtime that can crash the application is said once a window, with the way to the download page.
  const runtimeChecked = useRef(false);
  useEffect(() => {
    if (!status?.hostEpoch || runtimeChecked.current) return;
    runtimeChecked.current = true;
    void appUpdate.runtimeStatus({}, { timeoutMilliseconds: 15_000 }).then(runtime => {
      const toast = runtimeNotification(runtime, shownLocale.current,
        () => void appUpdate.openRuntimeDownload({}, { timeoutMilliseconds: 15_000 }).catch(() => { /* The page names the version to install. */ }));
      if (toast) showToast(toast, "runtime-update");
    }, () => { /* A host that does not answer says nothing of its runtime. */ });
  }, [status?.hostEpoch]);
  useEffect(() => {
    if (!status?.hostEpoch) return;
    const abort = new AbortController();
    checkForUpdate(false, abort.signal);
    // The host checks even while hidden. On return read its result without waiting for the next page tick,
    // and show any notice deferred while hidden. Frequent focus events reuse the host's cached check.
    const again = () => { if (document.visibilityState === "visible") { announceUpdate(); checkForUpdate(false, abort.signal); } };
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
          else if (notice.kind === "close-tab") nativeCloseTab.current();
          else if (notice.kind === "confirm-exit") setExitQuestionFor({ sessions: notice.runningSessions, terminals: notice.busyTerminals });
          else if (notice.kind === "confirm-close" && !exitPending.current) setCloseQuestion(true);
          // The program that started CodeAlta asked it to exit: it does once the sessions at work end.
          else if (notice.kind === "exit-pending") showToast({ intent: "primary", icon: "time", timeout: 0,
            message: translate(shownLocale.current, "CodeAlta exits once the sessions at work end."),
            action: { text: translate(shownLocale.current, "Exit now"), onClick: () => requestImmediateExit.current() } }, "exit-pending");
          // An application that drives CodeAlta through its MCP server creates sessions without the window asking.
          else if (notice.kind === "sessions-changed") readSessionList.current();
          else if (notice.kind === "show-project" && notice.projectId) void showHostProject.current(notice.projectId, notice.spaceId ?? null);
          // The user chose another external editor, in this window or another one.
          else if (notice.kind === "external-editor") void externalOpener.reload();
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
  function setReconnectRemoteControl(reconnect: boolean) {
    setShellPreferences(current => current && { ...current, reconnectRemoteControl: reconnect });
    void desktopShell.setReconnectRemoteControl({ reconnect }, { timeoutMilliseconds: 8_000 })
      .then(value => { if (value.status === "ok") setShellPreferences(value); }, () => { /* The setting shows what was asked. */ });
  }
  function setKeepAwake(keepAwake: boolean) {
    setShellPreferences(current => current && { ...current, keepAwake });
    void desktopShell.setKeepAwake({ keepAwake }, { timeoutMilliseconds: 8_000 })
      .then(value => { if (value.status === "ok") setShellPreferences(value); }, () => { /* The setting shows what was asked. */ });
  }
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
    const intent = ++saveFollowUp.current;
    setFileClosing({ tab, busy: true });
    const saved = await fileEditors.save(fileTabKey(tab));
    if (!creationAlive.current || intent !== saveFollowUp.current) return;
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
  // "/" in an empty prompt looks for a command or a skill.
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
      } else {
        // The prompt of a new session closes when it has a tab of its own; the transient one stays, as the last tab does.
        const scope = draftTabOf(snapshot, projectId);
        if (scope && hasDraftTab(valid, scope)) closeScopeDraft(scope);
      }
    } else if (action === "reopenTab") {
      const kind = reopenTabKind(closedTabKinds.current, valid.closed.length, fileTabs.closed.length);
      if (!kind) return;
      const at = closedTabKinds.current.lastIndexOf(kind);
      if (at >= 0) closedTabKinds.current = closedTabKinds.current.filter((_kind, index) => index !== at);
      tabFocusPending.current = true;
      if (kind === "file") openFile(fileTabs.closed.at(-1)!); else selectSessionTab(valid.closed.at(-1)!);
    } else {
      // The tabs are gone through as the strip shows them, the tools, the sessions and the editors alike: the transient
      // tab of a new session is one of them only while it is there.
      const shown = sessionTabPresentation(tabs, snapshot, projectId, sessionId, tabMode(openingMode(sessionId)));
      const next = cycleDockTab(spaceLayout(spaceId), activeDockNodeId(shown, fileTabs), action === "nextTab" ? 1 : -1);
      if (next === null) return;
      const file = fileTabs.open.find(tab => fileNodeId(tab) === next);
      const session = valid.open.find(tab => sessionNodeId(tab) === next);
      const prompt = valid.drafts?.find(draft => draftNodeId(draft) === next);
      tabFocusPending.current = true;
      if (file) activateFile(file);
      // A tab that is shown again stays what it is: the transient one is not kept by going through the tabs.
      else if (session) selectSessionTab(session, "preview");
      else if (prompt) selectProject(prompt.projectId);
      else if (next === sessionDraftNodeId) { if (fileTabs.active) activateFile(null); else applyTabState({ ...valid, active: null }); }
    }
  }
  // The macOS menu consumes Cmd+W before the webview. Use the current tab state and the same close path
  // as the keyboard, including dirty-editor handling and the unclosable prompt tab. Never close behind a dialog.
  nativeCloseTab.current = () => { if (!searchOpen && !dialog && !modalDialogOpen()) tabCommand("closeTab"); };
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

  showHostProject.current = async (id, joined) => {
    readSessionList.current();
    if (joined) spacesHub.refresh();
    if (!await whenProjectKnown(id, () => catalog.current?.projects)) return;
    setProjectTree(current => expandScope(current, id));
    showSpace(spaceShowingProject(id, currentSnapshot.current?.projects ?? [], shownSpace.current, joined, defaultSpaceId),
      { projectId: id, sessionId: null, then: focusPromptSoon });
  };

  function focusPromptSoon() {
    requestAnimationFrame(() => requestAnimationFrame(() => {
      if (currentView.current !== "workspace" || settingsVisible.current
        || modalDialogOpen() || document.querySelector(".project-editor[data-active='true'], .terminal-panel[data-active='true']")) return;
      const prompt = document.querySelector<HTMLElement>("#session-prompt, #catalog-prompt");
      // A request that waits for the user's permission is what its session shows first: its first choice takes the focus.
      // A request of a session under it does not: the prompt keeps the keyboard.
      const choice = ownPermissionChoice(prompt?.closest(".composer-region"));
      if (choice) choice.focus();
      else if (prompt) promptFocusTarget(prompt)?.focus();
      else document.querySelector<HTMLButtonElement>('.session-tabs [role="tab"][aria-selected="true"], .session-tabs > button')?.focus();
    }));
  }
  useLayoutEffect(() => {
    if (!tabFocusPending.current) return;
    tabFocusPending.current = false;
    if (view === "workspace" && !settingsVisible.current && !fileTabs.active) focusPromptSoon();
  }, [tabs, fileTabs, view]);

  const sessions = snapshot ? sessionsForProject(snapshot, projectId) : [];
  // The lineage knows the archived sessions too: a child of an archived session is a child, at the top of its list.
  const lineageSessions = snapshot ? catalog.current ? [...snapshot.sessions, ...catalog.current.sessions.filter(session => session.archived)] : snapshot.sessions : [];
  const loadedSessionRows = snapshot ? sessionHierarchy(sessions, lineageSessions, projectId) : [];
  const extraSessions = sessionExtras.get(scopeKey(projectId)) ?? 0;
  const listLimits = (count: number, active: string | null) => ({ count, subCount: subAgentCount, active,
    extra: (id: string) => subAgentExtras.get(id) ?? 0, collapsed: (id: string) => isCollapsed(projectTree, id) });
  // Filtered, every session found is listed, with its sub-agents found, whatever the counts and what is collapsed.
  const filteredLimits = (active: string | null) => ({ ...listLimits(Number.MAX_SAFE_INTEGER, active), subCount: Number.MAX_SAFE_INTEGER, collapsed: () => false });
  const selectedList = filterView ? sessionList(filterSessionRows(loadedSessionRows, sidebarWords), filteredLimits(sessionId))
    : sessionList(loadedSessionRows, listLimits(recentSessionCount + extraSessions, sessionId));
  const visibleSessions = listedSessions(selectedList.entries).map(row => row.session);
  // The sessions of the open scopes other than the selected one, which has the list above.
  const openScopes = useMemo(() => {
    const scopes = new Map<string, { entries: SessionListEntry[]; more: number }>();
    if (!snapshot) return scopes;
    // Filtered, the scopes with something found are open too.
    const keys = new Set(projectTree.expanded);
    if (filterView) {
      for (const scope of filterView.scopes) keys.add(scope === null ? globalScope : scope);
      for (const id of filterView.projects) keys.add(id);
    }
    for (const key of keys) {
      const id = key === globalScope ? null : key;
      if (id === projectId || id !== null && !snapshot.projects.some(project => project.id === id)) continue;
      const all = sessionHierarchy(sessionsForProject(snapshot, id), lineageSessions, id);
      const list = filterView ? sessionList(filterSessionRows(all, sidebarWords), filteredLimits(null))
        : sessionList(all, listLimits(recentSessionCount + (sessionExtras.get(key) ?? 0), null));
      scopes.set(key, { entries: list.entries, more: list.hidden });
    }
    return scopes;
  }, [snapshot, projectTree.expanded, projectTree.collapsed, projectId, recentSessionCount, sessionExtras, subAgentCount, subAgentExtras, filterView, sidebarWords]);
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
  const notice = snapshot ? workspaceNotice(snapshot) : null;
  const owned = !!(status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch);
  const storageMaintenance = useStorageMaintenance(storage, owned ? status!.hostEpoch : null, language.locale, !hostLost, !secondary);
  // Speech to prompt: the microphones of the composers and the download of a model follow it by themselves.
  const speech = useSpeechInstall(speechApi, owned && !hostLost ? status!.hostEpoch : null, language.locale);
  // Settings covers the workspace: the pane of a session stops being observed, and the prompt of a new session is told here.
  useEffect(() => { if (settingsOpen) speech.abandon(`draft:${draftScope}`); }, [speech, settingsOpen, draftScope]);
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
  // The user guide that ships with the application: its tab keeps its page while it is closed, and opens at the page
  // a command, a link of a message or `alta documentation open` names.
  const [documentationHub] = useState(() => createDocumentationHub(documentationApi));
  useEffect(() => terminalEpoch ? documentationHub.connect(terminalEpoch) : undefined, [documentationHub, terminalEpoch]);
  function openDocumentation(page: string | null = null, anchor: string | null = null) {
    documentationHub.show(page, anchor);
    openFile(documentationTab);
  }
  const openDocumentationLatest = useRef(openDocumentation); openDocumentationLatest.current = openDocumentation;
  useEffect(() => {
    if (!terminalEpoch) return;
    const abort = new AbortController();
    void (async () => {
      try {
        for await (const request of await documentationApi.watch({ expectedEpoch: terminalEpoch }, { signal: abort.signal })) {
          if (abort.signal.aborted) return;
          openDocumentationLatest.current(request.page, request.anchor);
        }
      } catch { /* The bridge is gone: the guide still opens from its icon. */ }
    })();
    return () => abort.abort();
  }, [terminalEpoch]);
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
  // The bookmarks: the sessions and the messages the user marked to find them again. This window lists them and is told
  // when they change. It does not follow them itself: the tab, the rail of a timeline and the mark of a row do.
  const bookmarkActions = useRef<Pick<SessionBookmarkHost, "toggleMessage" | "go" | "unshown">>({ toggleMessage: () => { }, go: () => { }, unshown: () => { } });
  const [bookmarkHost] = useState<SessionBookmarkHost>(() => ({ hub: createBookmarksHub(bookmarksApi), reveals: createRevealRequests(),
    toggleMessage: (...request) => bookmarkActions.current.toggleMessage(...request), go: bookmark => bookmarkActions.current.go(bookmark),
    unshown: outcome => bookmarkActions.current.unshown(outcome) }));
  useEffect(() => terminalEpoch ? bookmarkHost.hub.connect(terminalEpoch) : undefined, [bookmarkHost, terminalEpoch]);
  const bookmarkNotice = (message: MessageKey) => showToast({ message: t(message), intent: "warning", icon: "warning-sign", timeout: 6000 });
  const bookmarkRefused = (status: string) => bookmarkNotice(status === "not_bookmarkable" ? "This message cannot be bookmarked."
    : status === "limit" ? "There are too many bookmarks. Remove some first." : "The bookmark could not be added.");
  // Goes to what a bookmark marks: its session, and for a message its place in the timeline. The host says where the
  // message is now; the pane of the session reads its history up to it.
  async function goToBookmark(bookmark: Bookmark) {
    const target = await bookmarkHost.hub.resolve(bookmark.id);
    if (!creationAlive.current) return;
    if (target.status === "session_missing") { bookmarkNotice("The session of this bookmark no longer exists."); return; }
    if (target.status !== "ok" && target.status !== "gone" || !target.sessionId) { bookmarkNotice("The bookmark could not be opened."); return; }
    if (target.status === "gone") bookmarkNotice("The bookmarked message is no longer in its session.");
    else if (target.offset) bookmarkHost.reveals.open(target.sessionId, target.offset);
    await openAutomationSession(target.sessionId);
  }
  // The bookmark of the whole session: added, or removed when it has one.
  async function toggleSessionBookmark(id: string) {
    const held = sessionBookmarks(bookmarkHost.hub.getSnapshot().bookmarks, id).session;
    const outcome = held ? await bookmarkHost.hub.remove(held.id) : await bookmarkHost.hub.add(id);
    if (!outcome.ok && creationAlive.current) { if (held) bookmarkNotice("The bookmark could not be changed."); else bookmarkRefused(outcome.status); }
  }
  // The local search of the host: the index that the search window, Settings › Search and the menus of the projects
  // and the spaces read. The commands of the window are given to it, so that agents and the Search canvas find them.
  const [localSearchHub] = useState(() => createLocalSearchHub(localSearchApi));
  useEffect(() => terminalEpoch ? localSearchHub.connect(terminalEpoch) : undefined, [localSearchHub, terminalEpoch]);
  const localSearchState = useSyncExternalStore(localSearchHub.subscribe, localSearchHub.getSnapshot);
  const searchOn = searchEnabled(localSearchState);
  const contentSearch = useMemo<ContentSearch | null>(() => !terminalEpoch || !searchOn ? null : {
    search: (query, kinds, project, signal) => localSearchApi.query({ expectedEpoch: terminalEpoch, query, kinds, spaceId: shownSpace.current, projectId: project,
      limit: 30, typing: true, mode: null }, { signal, timeoutMilliseconds: 15_000 }),
  }, [terminalEpoch, searchOn]);
  useEffect(() => {
    if (!terminalEpoch) return;
    const commands = commandDefinitions.filter(command => !command.hidden).map(command => ({ id: command.id, title: t(command.label), category: t(command.category),
      description: t(command.description), keywords: command.search ?? null, shortcut: commandKeys(command)[0] ?? null }));
    void localSearchApi.registerCommands({ expectedEpoch: terminalEpoch, commands }, { timeoutMilliseconds: 15_000 }).catch(() => { /* The index finds them at the next start. */ });
  }, [terminalEpoch, language.locale]);
  // Search by meaning: the dialog an offer opens, and the toast that follows the work once it was turned on here.
  const [meaningDialog, setMeaningDialog] = useState(false);
  // The host asks for the dialog of search by meaning (the landing card, `/search_meaning`): it opens, or Settings › Search once it is on.
  const openMeaning = useRef(() => { });
  useEffect(() => localSearchHub.onOpen(() => openMeaning.current()), [localSearchHub]);
  const [followMeaning, setFollowMeaning] = useState(false);
  const meaningNow = localSearchState.status?.semantic ?? null;
  openMeaning.current = () => { if (meaningNow?.enabled) navigate("search"); else if (meaningNow?.supported) setMeaningDialog(true); };
  const meaningProgress = meaningNow ? `${meaningNow.state}|${meaningNow.embedded}|${meaningNow.total}|${meaningNow.download.receivedKilobytes}|${meaningNow.error ?? ""}` : "";
  useEffect(() => {
    if (!followMeaning || !meaningNow) return;
    if (meaningNow.state === "ready") {
      showToast({ message: t("Search by meaning is ready"), intent: "success", icon: "tick", timeout: 5000 }, "search-meaning");
      setFollowMeaning(false);
      return;
    }
    if (meaningNow.state === "failed") {
      showToast({ message: meaningStateText(meaningNow, t, language.locale), intent: "warning", icon: "warning-sign", timeout: 8000 }, "search-meaning");
      setFollowMeaning(false);
      return;
    }
    if (meaningNow.state === "off" && meaningNow.download.state !== "downloading") return;
    const action = meaningNow.state === "downloading" ? { text: t("Cancel"), onClick: () => void localSearchHub.cancelDownload() }
      : meaningNow.state === "indexing" ? { text: t("Pause"), onClick: () => void localSearchHub.pauseMeaning(true) }
      : meaningNow.state === "paused" ? { text: t("Resume"), onClick: () => void localSearchHub.pauseMeaning(false) } : undefined;
    // The toast can be closed: the progress stays in Settings › Search and in alta search status.
    showToast({ message: meaningStateText(meaningNow, t, language.locale), icon: "search", timeout: 0, action,
      onDismiss: expired => { if (!expired) setFollowMeaning(false); } }, "search-meaning");
  }, [followMeaning, meaningProgress, language.locale]);
  // Opens what the index found: a message where it is in its session, the page of the guide, a plan in the code editor.
  async function openContent(hit: LocalSearchHit) {
    const target = contentTarget(hit);
    switch (target.kind) {
      case "message": {
        try {
          const located = await localSearchApi.locate({ expectedEpoch: terminalEpoch, ref: target.ref }, { timeoutMilliseconds: 30_000 });
          if (!creationAlive.current) return;
          if (located.status === "session_missing") { bookmarkNotice("The session of this message no longer exists."); return; }
          if (located.status === "gone") bookmarkNotice("The message is no longer in its session.");
          else if (located.status === "ok" && located.offset && located.sessionId) bookmarkHost.reveals.open(located.sessionId, located.offset);
          else if (located.status !== "ok") { bookmarkNotice("The message could not be opened."); return; }
        } catch { bookmarkNotice("The message could not be opened."); return; }
        await openAutomationSession(target.sessionId);
        break;
      }
      case "session": await openAutomationSession(target.sessionId); break;
      case "doc": openDocumentation(target.page, target.anchor); break;
      case "file": {
        const project = currentSnapshot.current?.projects.find(candidate => candidate.id === target.projectId) ?? catalog.current?.projects.find(candidate => candidate.id === target.projectId);
        if (project) openEditor(project, { path: target.path, line: null, column: null, explorer: null });
        break;
      }
      case "settings": navigate(target.page); break;
      case "details": showToast({ message: `${target.title} — ${target.text}`, intent: "none", icon: "info-sign", timeout: 8000 }); break;
    }
  }
  bookmarkActions.current = {
    go: bookmark => void goToBookmark(bookmark),
    toggleMessage: (id, rowKey, bookmarkId) => void (bookmarkId ? bookmarkHost.hub.remove(bookmarkId) : bookmarkHost.hub.add(id, rowKey)).then(outcome => {
      if (!outcome.ok && creationAlive.current) { if (bookmarkId) bookmarkNotice("The bookmark could not be changed."); else bookmarkRefused(outcome.status); }
    }),
    unshown: outcome => bookmarkNotice(outcome === "missing" ? "The bookmarked message is no longer in its session." : "The history of the session could not be read up to the bookmarked message."),
  };
  // The line of the menu of a session row: read when the menu opens, which is when it is drawn.
  const sessionBookmarkEntries = (session: WorkspaceSession): SessionMenuEntry[] => {
    if (!owned || !bookmarkHost.hub.getSnapshot().available) return [];
    const held = sessionBookmarks(bookmarkHost.hub.getSnapshot().bookmarks, session.id).session;
    return [{ key: "bookmark", label: t(held ? "Remove session bookmark" : "Bookmark session"), icon: held ? "bookmarkRemove" : "bookmarkAdd", onSelect: () => void toggleSessionBookmark(session.id) }];
  };
  // The tasks agents propose and the plans of the projects: this window lists them and is told when they change.
  const [workHub] = useState(() => createWorkItemsHub(workItemsApi));
  useEffect(() => terminalEpoch ? workHub.connect(terminalEpoch) : undefined, [workHub, terminalEpoch]);
  // The reviews of the page: one watch of the host, the threads of each review a surface shows.
  const [reviewsHub] = useState(() => createReviewsHub(reviewsApi));
  useEffect(() => terminalEpoch && owned ? reviewsHub.connect(terminalEpoch) : undefined, [reviewsHub, terminalEpoch, owned]);
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
  const openReviewsList = useCallback((projectId: string | null) => openWorkItems({ projectId, kind: "review" }), []);
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
  // The catalogs of the form of the Agent chip of a session a task or an issue starts: in the space the window shows, as the host starts it.
  const runSpace = spacesState.available ? spaceId : null;
  const runProjects = snapshot?.projects;
  const sessionSettingsFor = useMemo(() => !runEpoch ? null : (projectId: string) => {
    const project = runProjects?.find(candidate => candidate.id === projectId);
    return project ? sessionSettingsApi(runEpoch, projectId, project.path, runSpace) : null;
  }, [runEpoch, runProjects, runSpace]);
  // A session started in the background (an issue, a work item) while the user looks at something else: a toast offers it.
  function offerStartedSession(id: string, title: string) {
    showToast({ message: t("The session “{title}” was created.", { title: plainTitle(title) }), intent: "success", icon: "tick", timeout: 10_000,
      action: { text: t("Show"), onClick: () => void openAutomationSession(id) } });
  }
  // `shown` says whether the user still looks at what started the work when its session exists: the session is then
  // opened, and offered by a toast otherwise.
  async function startWork(item: WorkItem, start: WorkStart, session: WorkStartSession | null, request: SessionStartRequest | null = null, shown?: () => boolean) {
    const key = workItemKey(item);
    setWorkBusy(value => new Set(value).add(key));
    try {
      if (session) {
        const epoch = currentHostEpoch.current, revision = browserRevision.current;
        if (!epoch || !await retention.admit(epoch, [session.id], () => currentHostEpoch.current === epoch
          && currentHostAvailable.current && revision === browserRevision.current, () => {})) return false;
      }
      return await startWorkItem({ hub: workHub, composer: (kind, id, text, agent) => askPluginComposer(kind, id, text ?? null, agent ?? null),
        openSession: id => { if (!shown || shown()) void openAutomationSession(id); else offerStartedSession(id, item.title); },
        refuse: (message, detail) => showToast({ message: message ? t(message) : detail ?? t("The work did not start."), intent: "danger", icon: "error", timeout: 8000 }) }, item, start, session, request);
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
    // Every window hears it: the one that holds the tab of the terminal shows it, else the main one.
    if (!windowsHub.handles(`terminal:${id}`)) return;
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
  // The code editor on the folder a session works in: the worktree, as git lists it, while its folder is there; the editor of the project otherwise.
  async function openWorkEditor(project: Readonly<{ id: string; path: string }>, worktreeRoot: string | null) {
    const epoch = currentHostEpoch.current;
    if (!owned || !epoch) return;
    if (!worktreeRoot) { openProjectEditor(project); return; }
    const status = await worktreesApi.openEditor({ expectedEpoch: epoch, projectId: project.id, path: worktreeRoot }, { timeoutMilliseconds: 30_000 })
      .then(reply => reply.status, () => "failed");
    if (status !== "ok") showToast({ intent: "warning", icon: "warning-sign", timeout: 6000, message: editorFailure(status, t) });
  }
  const openWorkEditorLatest = useRef(openWorkEditor); openWorkEditorLatest.current = openWorkEditor;
  const [openSessionEditor] = useState(() => (project: Readonly<{ id: string; path: string }>, worktreeRoot: string | null) => void openWorkEditorLatest.current(project, worktreeRoot));
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
    const abort = new AbortController();
    // One reading at a time: the timer and each reminder action ask for it, and a slow host gets one more, not a pile.
    const reading = createCoalescedRead(() => !alive, () => reminder.active({ expectedEpoch: reminderEpoch }, { signal: abort.signal }).then(value => {
      const counts = alive ? activeReminderCounts(value, reminderEpoch) : null;
      if (counts) setActiveReminders(previous => previous && sameActiveReminders(previous, counts) ? previous : counts);
    }).catch(() => { /* Unknown keeps the last markers; the next read corrects them. */ }));
    reading.run();
    const timer = setInterval(reading.run, 20000);
    const unsubscribe = reminderActions.subscribe(reading.run);
    return () => { alive = false; abort.abort(); clearInterval(timer); unsubscribe(); };
  }, [reminderEpoch, reminderActions]);
  // Whether the user reviews the changes of the new session before an agent commits them: the setting, unless the
  // draft composer gives the session a choice of its own, which is applied once it is created, before its first prompt.
  const [draftReview, setDraftReview] = useState<boolean | null>(null);
  const [reviewSetting, setReviewSetting] = useState<boolean | null>(null);
  useEffect(() => {
    if (!status?.hostEpoch) return;
    const controller = new AbortController();
    void globalConfig.reviewSettings({ expectedEpoch: status.hostEpoch }, { signal: controller.signal, timeoutMilliseconds: 8000 })
      .then(result => { if (!controller.signal.aborted && result.status === "ok") setReviewSetting(result.beforeCommit); }).catch(() => { /* No row then. */ });
    return () => controller.abort();
  }, [status?.hostEpoch, creatingVisible, settingsSection]);
  const draftChoices = useNewSessionChoices(status?.hostEpoch, projectId, selectedProject?.path ?? null,
    configurationState.snapshot, owned && (sessionId === null || creatingVisible) && view === "workspace" && !settingsOpen
      && !!snapshot?.configured && (projectId === null || !!selectedProject && !selectedProject.archived), mutation?.capability,
    spacesState.available ? spaceId : null, providerSpaces);
  // The sandbox of the new session: what its settings give (its model, its provider, its project, its space, every
  // session), unless the draft composer gives it a choice of its own, applied once it is created, before its first prompt.
  const [draftSandbox, setDraftSandbox] = useState<string | null>(null);
  const [draftSandboxScopes, setDraftSandboxScopes] = useState<SandboxSessionResponse | null>(null);
  // The options of the new session (sub-sessions), sent with its creation; null for the defaults.
  const [draftOptions, setDraftOptions] = useState<SessionOptionsChoice | null>(null);
  const draftSandboxProvider = draftChoices.value.providerKey || null, draftSandboxModel = draftChoices.value.modelId || null;
  useEffect(() => {
    if (!status?.hostEpoch || !owned) { setDraftSandboxScopes(null); return; }
    const controller = new AbortController();
    void sandboxSettings.draft({ expectedEpoch: status.hostEpoch, providerKey: draftSandboxProvider, modelId: draftSandboxModel, projectId,
      spaceId: spacesState.available ? spaceId : null, own: null }, { signal: controller.signal, timeoutMilliseconds: 8000 })
      .then(reply => { if (!controller.signal.aborted) setDraftSandboxScopes(reply.status === "ok" ? reply : null); }).catch(() => { /* No row then. */ });
    return () => controller.abort();
  }, [status?.hostEpoch, owned, draftSandboxProvider, draftSandboxModel, projectId, spacesState.available, spaceId, creatingVisible, settingsSection, settingsOpen]);
  const currentHostEpoch = useRef(status?.hostEpoch);
  currentHostEpoch.current = status?.hostEpoch;
  // The external editor and the file manager of the system: the host is asked for the editor when an action first names it.
  const [externalOpener] = useState(() => createExternalOpener({
    list: refresh => externalEditorsApi.list({ refresh }, { timeoutMilliseconds: 15_000 }),
    openFolder: async (target, projectId, sessionId) => (await externalEditorsApi.openFolder({ expectedEpoch: currentHostEpoch.current ?? null, projectId, sessionId, target },
      { timeoutMilliseconds: 15_000 })).status,
    openEntry: async (target, projectId, path, line) => (await projectFiles.openExternal({ expectedEpoch: currentHostEpoch.current ?? null, projectId, path, target, line, column: null },
      { timeoutMilliseconds: 15_000 })).status,
    openWorktree: async (target, projectId, path) => (await worktreesApi.openExternal({ expectedEpoch: currentHostEpoch.current ?? null, projectId, path, target },
      { timeoutMilliseconds: 15_000 })).status,
    failed: message => showToast({ message: translate(shownLocale.current, message), intent: "warning", icon: "warning-sign", timeout: 6000 }),
  }));
  const [externalEditorSettings] = useState(() => ({
    list: (refresh: boolean) => externalEditorsApi.list({ refresh }, { timeoutMilliseconds: 15_000 }),
    set: (request: Parameters<typeof externalEditorsApi.set>[0]) => externalEditorsApi.set(request, { timeoutMilliseconds: 15_000 }),
  }));
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
  // A picture of the disk that the Markdown names is drawn where its link is, read by the host as the link names it;
  // its window shows its file in the file manager the same way, never by a path the page sends.
  const [markdownImageCache] = useState(() => createMarkdownImageCache(markdownLinks.image));
  const shellPlatform = shellPreferences?.platform ?? "windows";
  const markdownImages = useMemo<MarkdownImages | null>(() => {
    if (!messageLinkEpoch) return null;
    const current = () => currentHostAvailable.current && currentHostEpoch.current === messageLinkEpoch;
    return {
      peek: (address, scope) => markdownImageCache.peek(markdownImageRequest(messageLinkEpoch, address, scope)),
      load: async (address, scope) => current() ? markdownImageCache.load(markdownImageRequest(messageLinkEpoch, address, scope)) : null,
      reveal: (address, scope) => {
        if (!current() || mutation?.capability.canMutate() !== true) return;
        void markdownLinks.reveal(markdownImageRequest(messageLinkEpoch, address, scope)).then(reply => reply.status, () => "failed").then(status => {
          if (status === "ok" || status === "stale_epoch" || !current()) return;
          const message = status === "not_found" ? "The file could not be found." : "The file could not be opened.";
          showToast({ message: translate(shownLocale.current, message), intent: "danger", icon: "error", timeout: 8000 });
        });
      },
      platform: shellPlatform,
    };
  }, [messageLinkEpoch, markdownImageCache, mutation?.capability, shellPlatform]);
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
  // The next session of a project works in a new worktree: git makes it as soon as its prompt is typed, on the host,
  // and the prompt stays free meanwhile. A worktree that is no longer wanted (the chip changed, the project went) is
  // discarded; one made from another branch is replaced.
  const draftTyped = !!localDraft.text.trim() || localImages.images.length > 0;
  const preparedProject = draftProjectId !== null ? selectedProject : undefined;
  useEffect(() => {
    if (!owned || !status?.hostEpoch || !mutation?.capability.canMutate() || !preparedProject || sessionId !== null || creatingBusy) return;
    if (!draftPlace.worktree) preparedWorktrees.discard(preparedProject.id);
    else if (draftTyped) preparedWorktrees.ensure(status.hostEpoch, preparedProject, draftPlace.base);
    // The worktrees follow too: one a session took, or that the host forgot, is made again while a prompt is typed.
  }, [owned, status?.hostEpoch, mutation?.capability, preparedProject?.id, preparedProject?.path, draftPlace, draftTyped, sessionId, creatingBusy, preparedWorktreeList]);
  useEffect(() => {
    if (snapshot?.configured) preparedWorktrees.keepOnly(new Set(snapshot.projects.filter(project => !project.archived).map(project => project.id)));
  }, [snapshot]);
  // Where no worktree can ever be made, the next session works in the folder of the project again, and the user is told.
  useEffect(() => {
    for (const worktree of preparedWorktreeList.values()) {
      if (worktree.state !== "failed" || (worktree.reason !== "not_repository" && worktree.reason !== "git_unavailable")) continue;
      preparedWorktrees.discard(worktree.projectId);
      setWorkPlace(worktree.projectId, inProjectFolder);
      showToast({ message: worktreeCreationMessage(worktree.reason, worktree.message), intent: "warning", icon: "warning-sign", timeout: 10_000 });
    }
  }, [preparedWorktreeList]);
  const draftWorktree = preparedProject && draftPlace.worktree ? preparedWorktreeList.get(preparedProject.id) : undefined;
  const draftWorktreeWaits = !!draftWorktree && draftWorktree.state === "preparing" && draftWorktree.base === draftPlace.base;
  // What is typed in the prompt of a new session keeps its tab, as it does for a session that is looked at. When the
  // prompt of another scope, or a session, takes the place of a prompt that has a text or pictures, that prompt gets
  // a tab of its own, as a double click gives it. An empty prompt leaves its place, and so does the one a session
  // was just started from. This runs after the effect that opens the tab of the selected session, and adds to it.
  // With a single tab the prompt that is left gets no tab: what was typed stays with its scope, and is there when the
  // prompt of that scope is shown again.
  const promptTyped = !!(localDrafts.peek(draftScope)?.text ?? "").trim() || localImages.images.length > 0;
  const leftPrompt = useRef<{ projectId: string | null; sessionId: string | null; typed: boolean } | null>(null);
  useEffect(() => {
    const before = leftPrompt.current;
    leftPrompt.current = { projectId, sessionId, typed: promptTyped };
    if (!before?.typed || before.sessionId !== null || before.projectId === projectId && sessionId === null) return;
    if (singleTab || !tabsReady || !snapshot || snapshot !== currentSnapshot.current) return;
    const left = draftTabOf(snapshot, before.projectId);
    if (left) writeTabs(current => openDraftTab(current, left));
  });

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
  const skillEpoch = owned ? status?.hostEpoch ?? null : null;
  const skillsKey = `${skillEpoch}:${projectId ?? ""}`;
  useEffect(() => {
    if (!searchOpen || !skillEpoch) return;
    const controller = new AbortController();
    void skillsApi.list({ expectedEpoch: skillEpoch, projectId: projectId ?? null }, { signal: controller.signal, timeoutMilliseconds: 8000 })
      .then(page => { if (!controller.signal.aborted && page.status === "ok") setSearchSkills({ key: skillsKey, skills: usableSkills(page.skills) }); },
        () => { /* The search goes on without the skills. */ });
    return () => controller.abort();
  }, [searchOpen, skillEpoch, projectId]);

  // A skill chosen in the search: the session in front is asked to use it, as a prompt it receives like any other.
  function askToUseSkill(name: string, args: string) {
    const said = (key: "Open a session to use a skill." | "The session could not be asked to use {name}.") =>
      showToast({ intent: "warning", icon: "info-sign", timeout: 6000, message: translate(shownLocale.current, key, { name }) });
    // Only the session whose tab is in front: a session behind a file, a canvas or the welcome page is not asked.
    const front = !fileTabs.active && view === "workspace" ? sessionId : null;
    if (!front) { said("Open a session to use a skill."); return; }
    const request = askPluginComposer("send", front, skillPrompt(name, args));
    if (!request.handled) { said("Open a session to use a skill."); return; }
    if (!request.result) { said("The session could not be asked to use {name}."); return; }
    void request.completion?.then(sent => { if (sent === false) said("The session could not be asked to use {name}."); });
  }

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
      case "skill": askToUseSkill(choice.name, choice.args); break;
      case "content": void openContent(choice.hit); break;
      case "claude-code": showClaudeCodeConversation(choice.conversation, choice.providerId); break;
    }
  });
  // A Claude Code session chosen in the search: its preview opens, and its row is shown in its group, the one of its
  // project (opened) or the one of no project.
  function showClaudeCodeConversation(conversation: WorkspaceClaudeCodeConversation, providerId: string | null) {
    const project = conversation.projectId ? snapshot?.projects.find(value => value.id === conversation.projectId) ?? null : null;
    setFileTabs(state => openClaudeCodeTab(state, claudeCodeTab(project, conversation, providerId)));
    setProjectTree(current => setClaudeCodeOpen(project ? expandScope(current, project.id) : current, project?.id ?? unplacedClaudeCodeKey, true));
  }

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
      case "bookmarkPrevious": case "bookmarkNext": case "bookmarkSession": return ownedSession && bookmarkHost.hub.getSnapshot().available;
      case "usage": case "reminders": case "nextPrompt": case "modelSelector": case "steer": return ownedSession;
      // The control of the composer in front decides: the command runs exactly when pressing it would.
      case "send": case "abort": case "clearQueue": case "compact":
        return ownedSession && composerCommandBlock(command, composerControlState(composerControl(command)), t) === null;
      case "fork": case "rewind": return ownedSession && currentProjectWritable();
      case "reviewChanges": return ownedSession && !!selectedSession?.projectId;
      case "expandPrompt": case "focusPrompt": return view === "workspace" && !fileTabs.active;
      // The composer in front attaches, when it can (an editable session or a new-session draft).
      case "attachFiles": case "attachFolder": case "attachIssues":
        return view === "workspace" && !fileTabs.active && !!activeComposerControl("section.prompt-surface")?.querySelector("input[data-attach-input]:not(:disabled)");
      case "dictate": return view === "workspace" && !fileTabs.active && speechEnabled(speech.getState());
      case "focusAskFile": return session && !!visibleAsk(".ask-file-review");
      case "closeTab": case "previousTab": case "nextTab": return tabs.open.length + fileTabs.open.length > 0;
      case "reopenTab": return tabs.closed.length + fileTabs.closed.length > 0;
      case "moveTabToNewWindow": return !!windowsState.current && (!!fileTabs.active || !!tabs.active);
      case "editFile": case "projectEditor": case "worktrees": return view === "workspace" && !!editedProject();
      case "newTerminal": return view === "workspace" && !!terminalOrigin();
      case "externalEditor": case "fileExplorer": { const origin = view === "workspace" ? terminalOrigin() : null; return !!origin && !!(origin.projectId || origin.sessionId); }
      case "automations": case "workflows": case "workItems": case "reviews": case "issues": case "canvases": case "documentation": case "bookmarks": return owned;
      case "spaces": case "newSpace": return owned && spacesState.available;
      case "goToSpace": return spacesState.available;
      case "previousSpace": case "nextSpace": return spacesState.available && spacesState.spaces.length > 1;
      case "refreshStatuses": return owned && tabs.open.length > 0;
      case "newSession": return owned && !!snapshot && !selectedProject?.archived;
      case "renameProject": return owned && !!selectedProject && !selectedProject.archived;
      // The session of a project, as its menu moves it, under the same locks.
      case "moveSession": return ownedSession && !!selectedSession && selectedSessionAccess(selectedSession).move;
      // A Claude Code session that has a conversation of the CLI, as its menu opens it elsewhere.
      case "openInVsCode": case "copyResumeCommand": case "runInTerminal": return ownedSession && !!selectedSession
        && opensElsewhere(selectedSession, providerLogos.get(selectedSession.providerKey?.toLowerCase() ?? "")?.type)
        && selectedSessionAccess(selectedSession)[openElsewhereCommandAction[command]];
      default: return true;
    }
  }

  // What the menu of the selected session allows, under the locks of its changes.
  function selectedSessionAccess(session: WorkspaceSession) {
    return sessionActionAccess(session,
      { id: session.id, projectId, hostEpoch: status?.hostEpoch ?? null }, selectedSessionId.current, selectedScope.current, selectedProject,
      currentHostEpoch.current ?? null, owned, mutation?.capability.canMutate() ?? false,
      batchDeletion.locked() || renamingBusy || deletingBusy || movingBusy || renamingPending.current || deletingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current);
  }

  // Why a command that is not offered now cannot run, when the composer in front knows it: the picker shows it on the
  // entry, a shortcut in a notice.
  function commandUnavailableReason(command: CommandId): string | null {
    if (!isComposerControlCommand(command) || view !== "workspace" || !selectedSession || fileTabs.active || !owned) return null;
    return composerCommandBlock(command, composerControlState(composerControl(command)), t);
  }
  function notifyCommandUnavailable(command: CommandId) {
    const reason = commandUnavailableReason(command);
    const definition = commandDefinitions.find(item => item.id === command);
    if (reason && definition) showToast({ message: t("{command}: {reason}", { command: t(definition.label), reason }), intent: "warning",
      icon: "warning-sign", timeout: 6000 }, `command-unavailable:${command}`);
  }
  // The Compact control of the session in front, under the identity it was rendered for.
  function compactControl() {
    const selection = selectedSessionInfoSelection(snapshot, selectedSession, projectId, selectedSessionId.current, selectedScope.current);
    const trigger = compactTrigger.current;
    return view === "workspace" && owned && currentProjectWritable() && status?.hostEpoch && mutation?.capability.canMutate()
      && selection && trigger?.isConnected && trigger.dataset.epoch === status.hostEpoch && trigger.dataset.sessionId === selection.sessionId
      && trigger.dataset.projectId === (selection.projectId ?? "") ? trigger : null;
  }
  // The control of the composer in front that carries out a command; only the active composer marks its controls.
  function composerControl(command: ComposerControlCommand) {
    return command === "compact" ? compactControl()
      : workspaceShell.current?.querySelector<HTMLButtonElement>(`[data-composer-command="${command}"]`) ?? null;
  }

  // Commands the active session's composer carries out itself.
  function composerCommand(command: "nextPrompt") {
    window.dispatchEvent(new CustomEvent("codealta:composer", { detail: command }));
  }
  const activeComposerControl = (selector: string) => Array.from(workspaceShell.current?.querySelectorAll<HTMLButtonElement>(selector) ?? [])
    .find(button => button.offsetParent !== null);

  function runCommand(command: CommandId) {
    // Only a shortcut gets here with a command that is not offered: it is told why, as the picker shows it.
    if (!commandAvailable(command)) { notifyCommandUnavailable(command); return; }
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
      // The folder a terminal would start in: the one the session works in, else the folder of the project.
      case "externalEditor": case "fileExplorer": {
        const origin = terminalOrigin();
        if (!origin) break;
        if (command === "fileExplorer") { void externalOpener.openFolder("explorer", origin.projectId, origin.sessionId); break; }
        void (async () => {
          // The editor is asked for the first time the command needs it; with none found, the setting is where one is chosen.
          if (externalOpener.state().editor === undefined) await externalOpener.reload();
          if (externalOpener.state().editor === null) navigate("general");
          else void externalOpener.openFolder("editor", origin.projectId, origin.sessionId);
        })();
        break;
      }
      case "automations": openAutomations(); break;
      case "workflows": openFile(workflowsTab); break;
      case "workItems": openWorkItems(); break;
      case "issues": openFile(issuesTab); break;
      // The tab in front, a file over the session behind it, goes to a window of its own.
      case "moveTabToNewWindow": {
        const front: MovedTab | null = fileTabs.active ? { file: fileTabs.active } : tabs.active ? { session: tabs.active } : null;
        if (front) void moveTabToWindow(front, null);
        break;
      }
      case "canvases": openFile(canvasesTab); break;
      case "documentation": openDocumentation(); break;
      case "bookmarks": openFile(bookmarksTab); break;
      case "reviews": openWorkItems({ projectId: null, kind: "review" }); break;
      case "bookmarkSession": if (selectedSession) void toggleSessionBookmark(selectedSession.id); break;
      case "bookmarkPrevious": case "bookmarkNext": {
        const pane = timelineCommand.current;
        if (pane?.sessionId === selectedSession?.id && !pane?.stepBookmark(command === "bookmarkNext" ? 1 : -1)) bookmarkNotice("There is no other bookmark that way.");
        break;
      }
      case "newSpace": setSpaceDialog(true); break;
      case "goToSpace": setSpaceMenuRequest(value => value + 1); break;
      case "previousSpace": case "nextSpace":
        if (showSpace(neighborSpace(spacesState.spaces, spaceId, command === "nextSpace" ? 1 : -1).id)) focusPromptSoon();
        break;
      case "newSession": selectProject(projectId); requestAnimationFrame(() => promptFocusTarget(document.querySelector<HTMLElement>("#session-prompt, #catalog-prompt"))?.focus()); break;
      case "moveSession": if (selectedSession) beginSessionMove(selectedSession); break;
      case "openInVsCode": case "copyResumeCommand": case "runInTerminal":
        if (selectedSession) void openSessionElsewhere(openElsewhereCommandAction[command], selectedSession);
        break;
      case "focusSidebar": runShortcut("focusProjects"); break;
      case "focusAskFile": visibleAsk(".ask-file-review")?.dispatchEvent(new CustomEvent("codealta-ask-file-focus")); break;
      case "toggleNavigator": toggleProjects(); break;
      case "modelSelector": invokeComposerControl(activeComposerControl(".composer-selection")); break;
      case "attachFiles": case "attachFolder": case "attachIssues": {
        // To the menu of the composer in front, through its file input: a keyboard gesture may open the file dialog.
        const input = activeComposerControl("section.prompt-surface")?.querySelector<HTMLInputElement>("input[data-attach-input]");
        if (input && !input.disabled && !dialog && !modalDialogOpen())
          input.dispatchEvent(new CustomEvent(attachEvent, { detail: command === "attachFiles" ? "files" : command === "attachFolder" ? "folder" : "issues" }));
        break;
      }
      case "usage": invokeComposerControl(activeComposerControl("#session-usage-trigger")); break;
      // The microphone of the composer in front starts, or stops what it records.
      case "dictate": invokeComposerControl(workspaceShell.current?.querySelector<HTMLButtonElement>("#dictate-prompt")); break;
      case "searchSessions": openSearch({ category: "sessions" }); break;
      case "refreshStatuses": runtimeObservationControls().refresh(tabs.open); break;
      case "send": case "abort": case "clearQueue": case "compact": invokeComposerControl(composerControl(command)); break;
      case "nextPrompt": composerCommand(command); break;
      case "steer": break;
      case "fork": case "rewind": openSessionBranch(command); break;
      case "reviewChanges": if (selectedSession) reviewSessionChanges(selectedSession); break;
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
  const landingCommand = pluginContributed.commands.find(command => command.pluginKey === landingPluginKey && command.name === "landing");
  const landingShell = useLandingShell({ epoch: pluginEpoch, version: aboutFacts(status, !!error, demoMode).version,
    space: findSpace(spacesState.spaces, spaceId), snapshot, run: runNamedCommand, notifyUnavailable: landingUnavailable,
    openProject: id => { selectProject(id); focusPromptSoon(); }, openSession: id => void openAutomationSession(id), runCardCommand: runLandingCardCommand,
    // A message found by search, as a bookmark of a message is shown: the pane of the session reads its history up to it.
    openMessage: (id, offset) => { bookmarkHost.reveals.open(id, offset); void openAutomationSession(id); } });
  // The Welcome page is the main window's: a secondary window opens with the tabs it had.
  // A Welcome page that another window holds stays there: a start does not bring that window to the front.
  useLandingAtStartup(owned && tabsReady && !secondary, canvasCatalog, item => {
    const target = defaultCanvasTarget(item, canvasSelection);
    if (target && windowsHub.heldElsewhere(fileTabRef(canvasTabOf(item, target)))) return;
    openCanvasHere(item);
  });

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
      // The code editor goes through its own files with Ctrl+Tab and Ctrl+Shift+Tab; Ctrl+PageDown and Ctrl+PageUp
      // still go through the tabs of the window from there.
      if (!commandChord.current && event.key === "Tab" && target?.closest("[data-file-tab-keys]")) return;
      // A list that renames its own rows (the files of the code editor) keeps F2.
      if (!commandChord.current && event.key === "F2" && target?.closest("[data-rename-keys]")) return;
      // A rendered page (the Documentation tab, a Markdown preview, a plan under review) finds in itself with Ctrl+F,
      // which searches the sessions everywhere else.
      if (!commandChord.current && findKeyKept(event, target)) return;
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
    else if (action === "compact") invokeComposerControl(compactControl());
    else if (action === "expandPrompt") {
      if (!dialog) invokeComposerControl(workspaceShell.current?.querySelector<HTMLButtonElement>("#expand-session-prompt"));
    }
    else if (action === "renameProject") void beginProjectRename();
    else if (action === "escape") {
      if (dialog === "help") closeHelp();
      else if (railVisible && projectRail.current?.contains(document.activeElement)) toggleProjects();
      // With no window open there is nothing to close: a session that is being created keeps its view, and its
      // result is still the one that was asked for.
      else { if (dialog) setDialog(null); (document.activeElement as HTMLElement | null)?.blur(); }
    }
    else if (action === "toggleNotes") setNotesVisible(value => !value);
    // An open ask has the place of the prompt: its questions take the focus.
    else if (action === "focusPrompt") (visibleAsk(".ask-form")?.querySelector<HTMLElement>("[data-ask-question] input:checked, [data-ask-question] textarea, [data-ask-question] input")
      ?? promptFocusTarget(document.querySelector<HTMLElement>("#session-prompt, #catalog-prompt")))?.focus();
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
    return owned && !!snapshot && !creatingBusy && !!mutation?.capability.canMutate()
      && (id === null || !!project && !project.archived);
  }
  // One menu per scope row: the session actions first select that scope, then act on it.
  function scopeSessionAction(id: string | null, kind: "create" | "search" | "browse") {
    if (kind === "create" && (modalDialogOpen() || !scopeCanCreateSession(id))) return;
    // The sessions of a scope are searched in the search of the window, which then keeps to that scope.
    if (kind === "search") { openSearch({ category: "sessions", projectId: id }); return; }
    if (id !== selectedScope.current) selectProject(id);
    // The session that is created is listed under its scope: a closed scope would hide it.
    if (kind === "create") setProjectTree(current => expandScope(current, id));
    if (kind === "browse") openSessionBrowser();
    // The form is beside the row of the scope it creates in, which is the selected one by now.
    else if (selectedScope.current === id && scopeCanCreateSession(id)) { setCreatingTitle(""); setCreatingVisible(true); setCreatingMessage(""); }
  }

  function toggleProjects() {
    if (railVisible) {
      focusProjectPending.current = false;
      restoreProjectRailFocus(projectRail.current, document.activeElement, projectRailToggle.current);
    } else focusProjectPending.current = true;
    toggleRail(narrow);
  }

  function selectProject(nextProjectId: string | null, selectedId?: string | null) {
    protectNavigation(selectedId ? [selectedId] : [], () => selectAdmittedProject(nextProjectId, selectedId));
  }
  function selectAdmittedProject(nextProjectId: string | null, selectedId?: string | null) {
    setMenuTarget(null);
    projectRenameGeneration.current++;
    setProjectRenameTarget(null);
    setProjectRenameConflict(false);
    setProjectRenameNotice(uncertainProjectRename.current ? { key: "A previous project rename is unconfirmed. Refresh and inspect; no retry will be sent." } : "");
    selectedScope.current = nextProjectId;
    setProjectId(nextProjectId);
    const nextSessionId = selectedId ?? null;
    setSessionId(nextSessionId);
    setRenamingId(null);
    setRenamingMessage("");
    setDeletingId(null);
    setDeletingMessage("");
    if (!archiveBusy) { setArchiveAsk(null); setArchiveMessage(""); }
    if (!projectDeleteBusy) { setProjectDeleteTarget(null); setProjectDeleteMessage(""); }
    navigate("workspace");
    setCreatingVisible(false);
    setCreatingMessage("");
  }

  // A session of an open scope that is not the selected one. Opening it makes its scope the selected one; renaming
  // and deleting act on the selected session, so they open it first and start once it is the one selected.
  const pendingSessionAction = useRef<{ projectId: string | null; sessionId: string; action: SessionAction } | null>(null);
  // The Actions menu of a session asks its composer to show its Remote Control: the session is selected, and its
  // panel opens it when it sees the request.
  const [remoteControlAsk, setRemoteControlAsk] = useState<{ sessionId: string; request: RemoteControlOpenRequest } | null>(null);
  function askRemoteControl(sessionId: string) {
    const ask = { sessionId, request: { done: () => setRemoteControlAsk(current => current === ask ? null : current) } };
    setRemoteControlAsk(ask);
  }
  function openScopeSession(scope: string | null, session: WorkspaceSession, action: SessionAction) {
    protectNavigation([session.id], () => {
      pendingSessionAction.current = { projectId: scope, sessionId: session.id, action };
      selectProject(scope, session.id);
    });
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
    if (pending.action === "remote-control") { askRemoteControl(pending.sessionId); return; }
    const rows = snapshot?.sessions.filter(session => session.id === pending.sessionId) ?? [];
    if (rows.length !== 1) return;
    const access = sessionActionAccess(rows[0], { id: pending.sessionId, projectId, hostEpoch: status?.hostEpoch ?? null }, pending.sessionId,
      selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
      mutation?.capability.canMutate() ?? false, batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current);
    if (!access[pending.action]) return;
    if (isOpenElsewhereAction(pending.action)) { void openSessionElsewhere(pending.action, rows[0]); return; }
    if (pending.action === "fork") { branchHost.requests.open(pending.sessionId, "fork"); return; }
    if (pending.action === "move") { beginSessionMove(rows[0]); return; }
    if (pending.action === "space") { beginSpaceMove(rows[0]); return; }
    if (pending.action === "archive") { beginSessionArchive(rows[0]); return; }
    if (pending.action === "rename") { beginSessionRename(rows[0]); setDeletingId(null); }
    else { beginSessionDelete(rows[0]); setRenamingId(null); }
  }, [projectId, sessionId]);
  function sessionMarks(session: WorkspaceSession, scope: string | null) {
    return <>
      <SessionDraftStatus indicators={draftIndicators} sessionId={session.id} selectedId={sessionId} />
      <SessionWaitingBadge waiting={waitingSessions.has(session.id)} below={waitingBelow.get(session.id)?.length ?? 0} />
      <ElsewhereBadge kind={elsewhereSessions.get(session.id)?.kind ?? null} />
      {snapshot && <RunningSessionBadge controls={runtimeObservationControls()} tab={{ projectId: scope, sessionId: session.id, path: session.workspacePath }} />}
      <ReminderBadge count={activeReminders?.get(session.id) ?? 0} />
      {carriedBy(work, session.id).length > 0 && <span className="session-work-mark" role="img" aria-label={t("Carries out a work item")}
        title={carriedBy(work, session.id).map(item => item.title).join("\n")}><AppIcon name="task" size={12} /></span>}
      <SessionBookmarkMark hub={bookmarkHost.hub} sessionId={session.id} />
      <WorktreeBadge session={session} />
      {session.forkedFromSessionId && <SessionLineageMark lineage={sessionLineage(session)!} />}
      <span className="session-meta"><span>{session.providerKey ?? t("No provider")}</span><SessionTime value={session.updatedAt} now={clock} /></span>
    </>;
  }
  // A closed project shows its chevron only when opening it shows something: its sessions, its terminals, or the
  // Claude Code sessions of its folder. A truncated snapshot does not tell, so every project keeps it then.
  const projectsWithSessions = new Set(snapshot?.sessions.flatMap(session => session.projectId ? [session.projectId] : []) ?? []);
  const projectUnfoldable = (id: string) => !snapshot || snapshot.sessionsTruncated || claudeCodeConfigured || projectsWithSessions.has(id)
    || terminalsOf(terminalList, id).length > 0;
  // What a label row tells of the projects it holds, with those of the labels under it: the sessions that wait for
  // the user, and how many run, so that a folded label says it without being unfolded.
  function labelActivity(projects: ReadonlySet<string>) {
    if (!projects.size) return null;
    return <><WaitingBadge count={spacesState.sessions.reduce((count, session) => count + (session.waiting && session.projectId && projects.has(session.projectId) ? 1 : 0), 0)} />
      <RunningSessionBadge controls={runtimeObservationControls()} projectIds={projects} /></>;
  }
  function scopeSessions(scope: string | null) {
    const shown = openScopes.get(scopeKey(scope));
    if (!shown || !snapshot) return null;
    const project = scope === null ? undefined : snapshot.projects.find(value => value.id === scope);
    const extra = sessionExtras.get(scopeKey(scope)) ?? 0;
    return <ExplorerSessions entries={withClaudeCodeSessions(shown.entries, scope, claudeCodeConfigured)} global={scope === null} projectId={scope} more={shown.more} filtered={!!filterView}
      extended={extra > 0} tree={sessionTree} marks={session => sessionMarks(session, scope)} claudeCode={claudeCodeGroup}
      access={session => sessionActionAccess(session, { id: session.id, projectId: scope, hostEpoch: status?.hostEpoch ?? null }, session.id, scope, project,
        currentHostEpoch.current ?? null, owned, mutation?.capability.canMutate() ?? false,
        batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
        renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current)}
      onAction={(session, action) => openScopeSession(scope, session, action)} onKeep={session => keepSession(scope, session)} menuEntries={session => sessionCanvasEntries(session, project)} actionEntries={session => [...sessionBookmarkEntries(session), ...sessionLineageEntries(session), ...sessionReviewEntries(session)]} deleteAsks={confirms.sessionDelete}
      archiveAsks={session => archivableDescendants(catalog.current?.sessions ?? [], session.id).length > 0}
      spaces={spacesState.available && spacesState.spaces.length > 1}
      drag={session => sessionDragSource(session, scope)}
      onMore={() => setSessionExtra(scope, extra + recentSessionCount)} onFewer={() => setSessionExtra(scope, 0)} />;
  }

  // The Claude Code sessions started in VS Code or a terminal in the folder of a project, at the end of its sessions: a
  // click opens the preview of one, which writes nothing; its menu also continues it in CodeAlta (the import, which opens
  // the session that continues it), or opens it in VS Code or the built-in terminal, in the folder of the project. The
  // group is closed until it is opened, and stays as it was left, project by project.
  const claudeCodeConfigured = owned && hasClaudeCodeProvider(providerLogos.values());
  // The space a Claude Code session is listed, shown and imported in: the one the window shows, as for a new session.
  const claudeCodeSpace = spacesState.available ? spaceId : null;
  // The group of a project, or the one of the conversations of no project (scope null), which follows the projects:
  // its rows are dragged onto a project to continue them there, or a project is chosen in their menu.
  function claudeCodeGroup(scope: string | null) {
    const project = scope === null ? null : snapshot?.projects.find(value => value.id === scope);
    if (project === undefined || project?.archived || !snapshot) return null;
    const listed = project ? { id: project.id, path: project.path, spaceId: claudeCodeSpace } : null;
    const previewing = fileTabs.active && isClaudeCodeTab(fileTabs.active) && fileTabs.active.projectId === (project?.id ?? "") ? fileTabs.active.key ?? null : null;
    const conversation = (session: Readonly<{ cliSessionId: string; title: string }>, providerId: string | null): ClaudeCodeConversationDrag =>
      ({ kind: "claude-code", id: session.cliSessionId, title: plainTitle(session.title), from: project ? { projectId: project.id, projectPath: project.path } : null,
        providerId });
    const treeKey = project?.id ?? unplacedClaudeCodeKey;
    return <ClaudeCodeSessionsGroup project={listed} spaceId={claudeCodeSpace} epoch={status?.hostEpoch ?? null} api={claudeCodeSessions}
      refresh={snapshot.sessions.length} indent={sessionRowIndent(0)} onOpen={(id, _existing, title, cliSessionId) => void showImportedSession(id, title, cliSessionId)}
      onPreview={(session, providerId) => setFileTabs(state => openClaudeCodeTab(state, claudeCodeTab(listed, session, providerId)))} previewing={previewing}
      expanded={isClaudeCodeOpen(projectTree, treeKey)} onExpandedChange={open => setProjectTree(current => setClaudeCodeOpen(current, treeKey, open))}
      write={navigator.clipboard ? text => navigator.clipboard.writeText(text) : undefined}
      drag={owned ? (session, providerId) => rowDrag.source(conversation(session, providerId)) : undefined}
      matches={filterView ? filterView.claudeCode.get(project?.id ?? unplacedKey) ?? [] : null}
      matchesProviderId={searchedClaudeCode?.providerId ?? null} importingElsewhere={claudeCodeImporting}
      onChooseProject={owned ? (session, providerId) => setContinuingClaudeCode(conversation(session, providerId)) : undefined}
      terminal={project ? {
        create: async () => {
          const reply = await terminalWorkspace.hub.create(project.id, null, terminalLook.shellIntegration, terminalLook.shell).catch(() => null);
          if (reply?.status !== "ok" || !reply.terminal) return null;
          showTerminal(reply.terminal);
          return { id: reply.terminal.id, profile: reply.terminal.profile };
        },
        input: (id, data) => terminalWorkspace.hub.input(id, data),
      } : undefined}
      onOutcome={outcome => showToast({ message: translate(shownLocale.current, outcome.message), intent: outcome.ok ? "success" : "warning",
        icon: outcome.ok ? "tick" : "warning-sign", timeout: outcome.ok ? 6000 : 10_000 })} />;
  }
  // Read when the search opens or the filter of the sidebar starts to be used, and again when the sessions change. Those
  // of another space are not shown while the space shown now is read.
  const lookingThrough = !!searchStart || sidebarWords.length > 0;
  const searchedSpace = useRef(claudeCodeSpace);
  useEffect(() => {
    if (!lookingThrough || !claudeCodeConfigured) { setSearchedClaudeCode(null); setClaudeCodeSearched(false); return; }
    if (searchedSpace.current !== claudeCodeSpace) { searchedSpace.current = claudeCodeSpace; setSearchedClaudeCode(null); setClaudeCodeSearched(false); }
    let current = true;
    void readClaudeCodeConversations(claudeCodeSessions, status?.hostEpoch, claudeCodeSpace).then(found => { if (current) { setSearchedClaudeCode(found); setClaudeCodeSearched(true); } });
    return () => { current = false; };
  }, [lookingThrough, claudeCodeConfigured, status?.hostEpoch, snapshot?.sessions.length, claudeCodeSpace]);
  // A Claude Code conversation dropped on a project, or chosen for one in its menu, is imported there, and the session
  // that continues it is opened. One import at a time.
  const [continuingClaudeCode, setContinuingClaudeCode] = useState<ClaudeCodeConversationDrag | null>(null);
  // The conversation being imported that way, whose row shows it.
  const [claudeCodeImporting, setClaudeCodeImporting] = useState<string | null>(null);
  const claudeCodeImportPending = useRef(false);
  async function importClaudeCodeInto(conversation: ClaudeCodeConversationDrag, target: WorkspaceProject) {
    if (!owned || claudeCodeImportPending.current || target.archived) return;
    claudeCodeImportPending.current = true;
    setClaudeCodeImporting(conversation.id);
    try {
      const result = await importClaudeCodeSession(claudeCodeSessions, currentHostEpoch.current, { id: target.id, path: target.path, spaceId: claudeCodeSpace,
        providerId: conversation.providerId ?? null }, conversation.id);
      if (!creationAlive.current) return;
      setContinuingClaudeCode(null);
      if (result.kind === "error") {
        showToast({ message: translate(shownLocale.current, importErrorMessage(result.code)), intent: "warning", icon: "warning-sign", timeout: 10_000 });
        return;
      }
      await showImportedSession(result.sessionId, conversation.title, conversation.id);
    } finally {
      claudeCodeImportPending.current = false;
      if (creationAlive.current) setClaudeCodeImporting(null);
    }
  }
  // The project the first message of a preview continues its conversation in: the one chosen in its banner, else the one
  // that lists it, else the project the window shows.
  const [previewTargets, setPreviewTargets] = useState<ReadonlyMap<string, string>>(() => new Map());
  const claudeCodePreviewTarget = (tab: FileTab) =>
    previewTarget(snapshot?.projects ?? [], previewTargets.get(fileTabKey(tab)) ?? null, tab.projectId || null, projectId);
  // The session that continues a Claude Code conversation, just imported or continuing it already: the list is read again,
  // the preview of the conversation leaves (the session takes the place of its tab), and the session is selected in its
  // tab. Null when the session is not in the list read again: the window says so.
  async function findImportedSession(id: string, title: string): Promise<WorkspaceSession | null> {
    const epoch = currentHostEpoch.current;
    let fresh: WorkspaceSnapshot | undefined;
    try { fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 }); } catch { /* The session exists whatever the list says. */ }
    if (!creationAlive.current) return null;
    const session = fresh?.configured && currentHostEpoch.current === epoch ? (catalog.current ?? fresh).sessions.find(value => value.id === id) : undefined;
    if (!fresh || !session) {
      showToast({ message: translate(shownLocale.current, "The session “{title}” was imported, but it could not be opened from here. Look for it in the list of sessions.", { title: plainTitle(title) }),
        intent: "warning", icon: "warning-sign", timeout: 12_000 });
      return null;
    }
    publishWorkspaceState({ kind: "ready", snapshot: fresh });
    return session;
  }
  function openImportedSession(session: WorkspaceSession, cliSessionId: string | undefined, send: Readonly<{ text: string; images: number }> | null) {
    if (send) autoSend.current = { sessionId: session.id, text: send.text, images: send.images };
    if (cliSessionId) setFileTabs(state => state.open.filter(tab => isClaudeCodeTab(tab) && tab.key === cliSessionId).reduce(dropClaudeCodeTab, state));
    keptSession.current = session.id;
    revealSessionLatest.current(session);
    focusPromptSoon();
  }
  async function showImportedSession(id: string, title: string, cliSessionId?: string) {
    const session = await findImportedSession(id, title);
    if (session) openImportedSession(session, cliSessionId, null);
  }
  // Continues the conversation of a preview in CodeAlta: it is imported (or the session that continues it already is
  // found), then that session takes the place of the preview. With the first message typed under it, the message is given
  // to the session and sent there. When the import fails, or the session does not take the message (a draft of its own, a
  // prompt it sends, another host), the preview stays with the message and its images, and the window says why. One
  // conversation is imported at a time: the other previews wait meanwhile.
  const [previewImporting, setPreviewImporting] = useState<string | null>(null);
  async function continueClaudeCodePreview(tab: FileTab, prompt: PreviewPrompt | null) {
    if (!owned || previewImporting || !tab.key) return;
    const key = fileTabKey(tab), cliSessionId = tab.key, epoch = currentHostEpoch.current;
    const say = (message: MessageKey) => translate(shownLocale.current, message);
    let found: WorkspaceSession | null = null;
    setPreviewImporting(key);
    try {
      const target = claudeCodePreviewTarget(tab);
      if (!target) return;
      const outcome = await continuePreview({
        importConversation: () => importClaudeCodeSession(claudeCodeSessions, epoch, { id: target.id, path: target.path, spaceId: claudeCodeSpace,
          providerId: claudeCodeTabProvider(tab) }, cliSessionId),
        findSession: async id => found = await findImportedSession(id, tab.name ?? ""),
        handOff: (session, message): HandOffRefusal | null => {
          if (!epoch || currentHostEpoch.current !== epoch) return "stale";
          if (submissions.pending(session.id)) return "sending";
          if (!handOffPreviewPrompt({ read: name => localStorage.getItem(name), write: (name, value) => localStorage.setItem(name, value) },
            submissions.imageDrafts, epoch, session, message, false)) return "draft";
          // The session holds the message now: the prompt of the preview is empty again.
          clearPreviewDraft(localDrafts, message.scope, name => localStorage.removeItem(name));
          submissions.imageDrafts.replace(message.imageKey, message.images, []);
          return null;
        },
        open: (session, send) => { if (creationAlive.current) openImportedSession(session, cliSessionId, send); },
      }, prompt);
      if (!creationAlive.current) return;
      if (outcome.kind === "import_failed") showToast({ message: say(importErrorMessage(outcome.code)), intent: "warning", icon: "warning-sign", timeout: 10_000 });
      else if (outcome.kind === "kept") {
        // The session is opened beside the preview on demand: the preview keeps the message.
        const session = found;
        showToast({ intent: "warning", icon: "warning-sign", timeout: 30_000, message: say(outcome.reason === "draft"
          ? "The session that continues this conversation holds a draft: your message stayed under the preview."
          : outcome.reason === "sending" ? "The session that continues this conversation is sending a prompt: your message stayed under the preview. Send it again once it is done."
          : "The window changed while the conversation was imported: your message stayed under the preview. Send it again."),
          action: session ? { text: say("Open the session"), onClick: () => { if (creationAlive.current) openImportedSession(session, undefined, null); } } : undefined });
      }
    } finally { if (creationAlive.current) setPreviewImporting(current => current === key ? null : current); }
  }
  async function openPreviewInVsCode(tab: FileTab) {
    if (!owned || !tab.key) return;
    const outcome = await openEntryInVsCode(claudeCodeSessions, currentHostEpoch.current, tab.projectId ? { id: tab.projectId, path: tab.projectPath, spaceId: claudeCodeSpace, providerId: claudeCodeTabProvider(tab) }
      : { spaceId: claudeCodeSpace, providerId: claudeCodeTabProvider(tab) }, tab.key);
    if (!creationAlive.current) return;
    showToast({ message: translate(shownLocale.current, outcome.message), intent: outcome.ok ? "success" : "warning",
      icon: outcome.ok ? "tick" : "warning-sign", timeout: outcome.ok ? 6000 : 10_000 });
  }

  // A Claude Code session opened in VS Code or the built-in terminal, or its resume command copied. The host gives the
  // command and builds the address of VS Code; before the conversation is opened elsewhere, or its command copied, it
  // closes the process it keeps for the session, or says why it cannot.
  async function openSessionElsewhere(action: OpenElsewhereAction, session: WorkspaceSession) {
    if (!owned) return;
    const epoch = currentHostEpoch.current ?? null;
    const outcome = action === "open-vs-code" ? await openSessionInVsCode(claudeCodeOpenHostApi, epoch, session.id)
      : action === "copy-resume-command" ? await copyResumeCommand(claudeCodeOpenHostApi, epoch, session.id,
        navigator.clipboard ? text => navigator.clipboard.writeText(text) : undefined)
      : await runSessionInTerminal(claudeCodeOpenHostApi, epoch, session.id, {
        create: async id => {
          const reply = await terminalWorkspace.hub.create(session.scopeKind === "project" ? session.projectId : null, id,
            terminalLook.shellIntegration, terminalLook.shell).catch(() => null);
          if (reply?.status !== "ok" || !reply.terminal) return null;
          showTerminal(reply.terminal);
          return { id: reply.terminal.id, profile: reply.terminal.profile };
        },
        input: (id, data) => terminalWorkspace.hub.input(id, data),
      });
    // Done with something left to do (a copied command whose conversation CodeAlta still has open) is a warning.
    const done = outcome.ok && !outcome.detail;
    const message = translate(shownLocale.current, outcome.message);
    showToast({ message: outcome.detail ? `${message} ${translate(shownLocale.current, outcome.detail)}` : message, intent: done ? "success" : "warning",
      icon: done ? "tick" : "warning-sign", timeout: done ? 6000 : 10_000 });
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
    setDeletingWorktree(false);
    // A session that others come from is not deleted alone: what is below it is shown, with the choice to delete it all.
    if (knownChildren(row.id)) { showSessionChildren(row, true); return; }
    if (confirms.sessionDelete) setDeletingId(row.id);
    else { setDeletingId(null); void deleteSessionRow(row, false); }
  }

  // Delete on the row of a session does what **Delete** of its menu does.
  function deleteSessionFromRow(row: WorkspaceSession) {
    protectNavigation([row.id], () => deleteAdmittedSessionFromRow(row));
  }
  function deleteAdmittedSessionFromRow(row: WorkspaceSession) {
    if (snapshot?.sessions.filter(session => session.id === row.id).length !== 1 || currentSnapshot.current !== snapshot
      || settingsVisible.current || dialog || modalDialogOpen()) return;
    if (!sessionActionAccess(row, { id: row.id, projectId, hostEpoch: status?.hostEpoch ?? null }, row.id,
      selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
      mutation?.capability.canMutate() ?? false, batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current).delete) return;
    setMenuTarget(null);
    if (selectedSessionId.current !== row.id) { setSessionId(row.id); setRenamingMessage(""); }
    setRenamingId(null);
    beginSessionDelete(row);
  }

  // F2, F and A on the row of a session do what **Rename**, **Fork** and **Archive** of its menu do.
  function runSessionRowShortcut(action: "rename" | "fork" | "archive", row: WorkspaceSession) {
    protectNavigation([row.id], () => {
      if (snapshot?.sessions.filter(session => session.id === row.id).length !== 1 || currentSnapshot.current !== snapshot
        || settingsVisible.current || dialog || modalDialogOpen()) return;
      const access = sessionActionAccess(row, { id: row.id, projectId, hostEpoch: status?.hostEpoch ?? null }, row.id,
        selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
        mutation?.capability.canMutate() ?? false, batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
        renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current);
      if (!access[action] || action === "archive" && sessionArchiveBusy) return;
      setMenuTarget(null);
      if (selectedSessionId.current !== row.id) { setSessionId(row.id); setRenamingMessage(""); setDeletingMessage(""); }
      if (action === "fork") { branchHost.requests.open(row.id, "fork"); return; }
      if (action === "archive") { beginSessionArchive(row); return; }
      setDeletingId(null);
      beginSessionRename(row);
    });
  }

  function runSessionMenuAction(action: SessionAction, row: WorkspaceSession, target: SessionMenuTarget) {
    protectNavigation([row.id], () => runAdmittedSessionMenuAction(action, row, target));
  }
  function runAdmittedSessionMenuAction(action: SessionAction, row: WorkspaceSession, target: SessionMenuTarget) {
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
    if (action === "rename" && !access.rename || action === "delete" && !access.delete || action === "fork" && !access.fork
      || action === "move" && !access.move || action === "space" && !access.space || action === "archive" && !access.archive) return;
    const switching = selectedSessionId.current !== row.id;
    if (switching) setSessionId(row.id);
    if (action === "open") { dismissSessionMenu(false); menuOrigin.current?.closest<HTMLElement>(".session-row")?.querySelector<HTMLButtonElement>(":scope > button:first-child")?.focus(); return; }
    if (action === "remote-control") { dismissSessionMenu(false); if (access["remote-control"]) askRemoteControl(row.id); return; }
    if (isOpenElsewhereAction(action)) { dismissSessionMenu(false); if (access[action]) void openSessionElsewhere(action, row); return; }
    // The window of the fork is the one of the session's pane: the session is selected, and its pane shows it.
    if (action === "fork") { dismissSessionMenu(false); branchHost.requests.open(row.id, "fork"); return; }
    if (action === "move") { dismissSessionMenu(false); beginSessionMove(row); return; }
    if (action === "space") { dismissSessionMenu(false); beginSpaceMove(row); return; }
    if (action === "archive") { dismissSessionMenu(false); beginSessionArchive(row); return; }
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

  // The sessions below a session, wherever they are: the whole catalog knows of them, not only the shown space.
  function knownChildren(id: string) {
    return catalog.current?.sessions.some(session => session.parentSessionId === id) ?? false;
  }
  function showSessionChildren(row: WorkspaceSession, deleting: boolean) {
    setRenamingId(null);
    setDeletingId(null);
    setChildrenView({ id: row.id, title: plainTitle(row.title), deleting });
  }
  // The entries of a session's menu about its family: its child sessions, and its parent, which can be in another scope.
  // Opens the Changes tab on the checkout a session works in, in review mode: its uncommitted changes.
  function reviewSessionChanges(session: WorkspaceSession) {
    const project = session.projectId ? snapshot?.projects.find(value => value.id === session.projectId && !value.archived) : undefined;
    if (project) showChanges(project, null, sessionCheckout(session), "changes");
  }
  const sessionReviewEntries = (session: WorkspaceSession): SessionMenuEntry[] => owned && session.projectId && !session.archived
    ? [{ key: "review-changes", label: t("Review changes"), icon: "chat" as const, onSelect: () => reviewSessionChanges(session) }] : [];
  const sessionLineageEntries = (session: WorkspaceSession): SessionMenuEntry[] => {
    const parent = session.parentSessionId ? catalog.current?.sessions.find(value => value.id === session.parentSessionId && !value.archived) : undefined;
    return [
      ...(knownChildren(session.id) ? [{ key: "children", label: t("Child sessions…"), icon: "tree" as const, onSelect: () => showSessionChildren(session, false) }] : []),
      ...(parent ? [{ key: "parent", label: t("Open parent session"), icon: "arrowUp" as const, onSelect: () => revealSession(parent) }] : []),
    ];
  };
  // Deletes a session with the sessions below it, as the window of its child sessions showed them.
  async function deleteChildrenTree(tree: SessionChildren): Promise<SessionTreeDeletion> {
    const capability = mutation?.capability;
    const root = tree.root;
    const project = root.scopeKind === "project" ? catalog.current?.projects.find(value => value.id === root.projectId) : undefined;
    if (batchDeletion.locked() || deletingPending.current || !owned || !capability?.canMutate() || !root.workspacePath
      || root.scopeKind === "project" && (!project || project.archived)) return { kind: "error", code: "busy", deleted: [], blocking: null };
    const target: RenameTarget = project ? { scope: "project", projectId: project.id, projectPath: project.path }
      : { scope: "global", projectPath: root.workspacePath };
    deletingPending.current = true;
    setDeletingBusy(true);
    try {
      const result = await deleteSessionTree(workspace.deleteSessionTree, status?.hostEpoch, target, tree);
      if (!creationAlive.current) return result;
      if (result.deleted.length > 0 || result.kind === "error" && result.code === "delete_unconfirmed") {
        const fresh = await refreshProjects(creationRefresh.current.signal);
        // The selected session is gone: its scope shows the next one.
        if (fresh?.configured && selectedSessionId.current && result.deleted.includes(selectedSessionId.current))
          setSessionId(sessionsForProject(fresh, selectedScope.current)[0]?.id ?? null);
      }
      if (result.kind === "deleted") showToast({ message: t("Deleted {count} sessions.", { count: result.deleted.length }), intent: "success", icon: "tick", timeout: 4000 });
      return result;
    } finally { deletingPending.current = false; if (creationAlive.current) setDeletingBusy(false); }
  }

  // The session is the selected one, in the selected project: the window asks which project it goes to.
  // A session chosen in its menu goes to another space: the space it is in now is not offered.
  function beginSpaceMove(row: WorkspaceSession) {
    if (movingPending.current) return;
    setRenamingId(null);
    setDeletingId(null);
    const home = sessionHome(spacesHub.getSnapshot().spaces, { spaceId: row.spaceId, projectId: row.scopeKind === "project" ? row.projectId : null });
    setMovingToSpace({ id: row.id, title: plainTitle(row.title), projectId: row.scopeKind === "project" ? row.projectId : null, spaceId: home.id });
  }
  async function moveSessionToSpaceNow(moving: NonNullable<typeof movingToSpace>, target: Space) {
    const capability = mutation?.capability;
    if (movingPending.current || !owned || !capability?.canMutate() || target.id === moving.spaceId) return;
    movingPending.current = true;
    setMovingBusy(true);
    let readAgain = false;
    try {
      const result = await moveSessionToSpace(workspace.moveSessionToSpace, status?.hostEpoch, moving.id, target.id);
      if (!creationAlive.current) return;
      let fresh: WorkspaceSnapshot | undefined;
      if (result.kind === "moved" || result.code === "move_unconfirmed" || result.code === "move_partial") {
        try { fresh = await workspace.snapshot({}, { signal: creationRefresh.current.signal, timeoutMilliseconds: 30_000 }); }
        catch { fresh = undefined; }
        if (!creationAlive.current) return;
      }
      readAgain = result.kind === "moved" && !fresh?.configured;
      if (fresh?.configured) publishWorkspaceState({ kind: "ready", snapshot: fresh });
      setMovingToSpace(null);
      if (result.kind === "moved") {
        // The session is in the other space now: a click there shows it.
        showToast({ message: t("Moved to {space}.", { space: target.name }), intent: "success", icon: "tick", timeout: 6000,
          action: target.id === shownSpace.current ? undefined : { text: t("Show"), onClick: () => void openSpaceSessionLatest.current(target.id, { projectId: moving.projectId, sessionId: moving.id }) } });
        if (result.warnings.length > 0)
          showToast({ message: `${t("The session moved, but:")} ${result.warnings[0]}`, intent: "warning", icon: "warning-sign", timeout: 12000 });
      } else showToast({ message: t(spaceMoveMessage(result.code)), intent: "danger", icon: "error", timeout: 8000 });
    } finally {
      movingPending.current = false;
      if (creationAlive.current) {
        setMovingBusy(false);
        sessionLists.resume(readAgain);
      }
    }
  }

  // A session is archived at once, with a message that brings it back; one with child sessions asks first whether they
  // go with it, where the user asked: beside its row in the Explorer, or above the `⋯` menu of its composer.
  function beginSessionArchive(row: WorkspaceSession, from: "explorer" | "composer" = "explorer") {
    if (sessionArchivePending.current) return;
    setRenamingId(null);
    setDeletingId(null);
    setSessionArchiveText("");
    const below = archivableDescendants(catalog.current?.sessions ?? [], row.id).length;
    if (below > 0) setSessionArchiving({ id: row.id, count: below, withChildren: true, from });
    else void archiveSessionNow(row, false, false);
  }
  // The question of the archive of a session with child sessions: with them, by default, or alone.
  function sessionArchiveQuestion(session: WorkspaceSession, placement: "right-start" | "top-end") {
    if (sessionArchiving?.id !== session.id) return null;
    return <ConfirmPopover always title={t("Archive this session?")} subject={plainTitle(session.title)} placement={placement}
      detail={t("It leaves the Explorer and keeps its history. Saved sessions and the search find it, to restore it.")}
      confirmLabel={t("Archive")} busy={sessionArchiveBusy} error={sessionArchiveNotice || null}
      onConfirm={() => void archiveSessionNow(session, sessionArchiving.withChildren, true)}
      onCancel={() => { setSessionArchiving(null); setSessionArchiveText(""); }}>
      <RadioGroup className="confirm-choices" selectedValue={sessionArchiving.withChildren ? "tree" : "alone"} disabled={sessionArchiveBusy}
        onChange={event => { const withChildren = event.currentTarget.value === "tree"; setSessionArchiving(value => value && { ...value, withChildren }); }}>
        <Radio value="tree" label={sessionArchiving.count === 1 ? t("Archive with its child session") : t("Archive with its {count} child sessions", { count: sessionArchiving.count })} />
        <Radio value="alone" label={t("Archive this session only")} />
      </RadioGroup>
    </ConfirmPopover>;
  }
  // Archives a session, with its child sessions when asked. What went wrong is said in the question that was asked,
  // or in a message when nothing was asked.
  async function archiveSessionNow(row: WorkspaceSession, withChildren: boolean, asked: boolean) {
    if (sessionArchivePending.current || !owned || !mutation?.capability.canMutate()) return;
    sessionArchivePending.current = true;
    setSessionArchiveBusy(true);
    setSessionArchiveText("");
    try {
      const result = await setSessionArchived(workspace.archiveSession, status?.hostEpoch, row.id, withChildren);
      if (!creationAlive.current) return;
      if (result.changed.length > 0 || result.kind === "error" && result.code === "archive_unconfirmed") {
        const fresh = await refreshProjects(creationRefresh.current.signal);
        // The selected session left the list: its scope shows the next one.
        if (fresh?.configured && selectedSessionId.current && result.changed.includes(selectedSessionId.current))
          setSessionId(sessionsForProject(fresh, selectedScope.current)[0]?.id ?? null);
      }
      if (result.kind === "done") {
        setSessionArchiving(null);
        showToast({ message: result.changed.length > 1 ? t("Archived {count} sessions.", { count: result.changed.length }) : t("Archived."),
          intent: "success", icon: "tick", timeout: 8000, action: { text: t("Restore"), onClick: () => void restoreSessionNow(row, false) } });
        return;
      }
      const message = t(sessionArchiveMessage(result.code));
      if (asked) setSessionArchiveText(message);
      else showToast({ message, intent: "danger", icon: "error", timeout: 8000 });
    } finally {
      sessionArchivePending.current = false;
      if (creationAlive.current) setSessionArchiveBusy(false);
    }
  }
  // Takes a session out of the archive, with the sessions archived below it, and shows it when `open` says so.
  async function restoreSessionNow(row: WorkspaceSession, open: boolean): Promise<boolean> {
    if (sessionArchivePending.current || !owned || !mutation?.capability.canMutate()) return false;
    sessionArchivePending.current = true;
    setSessionArchiveBusy(true);
    try {
      const result = await setSessionArchived(workspace.restoreSession, status?.hostEpoch, row.id, true);
      if (!creationAlive.current) return false;
      const fresh = result.changed.length > 0 || result.kind === "error" ? await refreshProjects(creationRefresh.current.signal) : undefined;
      if (!creationAlive.current) return false;
      if (result.kind === "error") {
        showToast({ message: t(sessionArchiveMessage(result.code)), intent: "danger", icon: "error", timeout: 8000 });
        return false;
      }
      showToast({ message: t("Restored."), intent: "success", icon: "tick", timeout: 4000 });
      const restored = fresh?.sessions.find(session => session.id === row.id);
      if (open && restored) revealSession(restored);
      return true;
    } finally {
      sessionArchivePending.current = false;
      if (creationAlive.current) setSessionArchiveBusy(false);
    }
  }
  // Deletes an archived session from the saved sessions. One with child sessions is shown with them, to delete them
  // together. The answer is why it was not deleted, or null.
  async function deleteArchivedSession(row: WorkspaceSession, remember: boolean): Promise<string | null> {
    const capability = mutation?.capability;
    if (sessionArchivePending.current || deletingPending.current || !owned || !capability?.canMutate() || !row.workspacePath) return t(sessionDeletionMessage("busy") as MessageKey);
    if (remember) setConfirm("sessionDelete", false);
    if (knownChildren(row.id)) { setDialog(null); showSessionChildren(row, true); return null; }
    const project = row.scopeKind === "project" ? catalog.current?.projects.find(value => value.id === row.projectId && value.path === row.workspacePath) : undefined;
    if (row.scopeKind === "project" && (!project || project.archived)) return t(sessionDeletionMessage("scope_missing") as MessageKey);
    const target: RenameTarget = project ? { scope: "project", projectId: project.id, projectPath: project.path } : { scope: "global", projectPath: row.workspacePath };
    sessionArchivePending.current = true;
    setSessionArchiveBusy(true);
    try {
      const result = await deleteSession(status?.hostEpoch, target, row.id, row.title, capability);
      if (!creationAlive.current) return null;
      if (result.kind === "deleted" || result.code === "delete_unconfirmed") {
        await refreshProjects(creationRefresh.current.signal);
        // The browser goes on with the list as it is now.
        if (creationAlive.current && currentSnapshot.current)
          setBrowserCapture(value => value && { ...value, snapshot: currentSnapshot.current!, revision: browserRevision.current });
      }
      return result.kind === "deleted" ? null : t(sessionDeletionMessage(result.code) as MessageKey);
    } finally {
      sessionArchivePending.current = false;
      if (creationAlive.current) setSessionArchiveBusy(false);
    }
  }
  function archivedSessionControls(): ArchivedSessionControls | undefined {
    if (!owned || !mutation?.capability.canMutate()) return undefined;
    return { sessions: shownArchived, busy: sessionArchiveBusy, deleteAsks: confirms.sessionDelete,
      restore: session => { setDialog(null); void restoreSessionNow(session, true); },
      info: session => setArchivedInfo(session.id),
      remove: deleteArchivedSession };
  }
  // The archived sessions the shown space lists: the saved sessions and the search find them there. The catalog is
  // read again with each list the window is given.
  const shownArchived = useMemo(() => {
    const full = catalog.current;
    return full ? archivedSessions({ ...full, sessions: archivedInView(full, spaceView(spacesState.spaces, spaceId, showOthers)) }) : [];
  }, [snapshot, spacesState.spaces, spaceId, showOthers]);

  function beginSessionMove(row: WorkspaceSession) {
    const project = selectedProject;
    if (!project || project.archived || row.projectId !== project.id || row.workspacePath !== project.path || movingPending.current) return;
    setRenamingId(null);
    setDeletingId(null);
    setMovingSession({ id: row.id, title: plainTitle(row.title), from: { projectId: project.id, projectPath: project.path } });
  }

  // The row of a session of a project can be dragged onto the row of another project: the drop moves it there.
  function sessionDragSource(session: WorkspaceSession, scope: string | null): SessionRowDrag {
    const project = scope === null ? undefined : snapshot?.projects.find(value => value.id === scope);
    if (!project || !sessionMoveAccess(session, project, currentHostEpoch.current ?? null, owned, mutation?.capability.canMutate() ?? false,
      batchDeletion.locked() || renamingBusy || deletingBusy || movingBusy || renamingPending.current || deletingPending.current,
      renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current)) return {};
    return rowDrag.source({ id: session.id, title: plainTitle(session.title), from: { projectId: project.id, projectPath: project.path } });
  }
  function projectDropTarget(project: WorkspaceProject): ProjectRowDrop | undefined {
    return rowDrag.view?.over === project.id ? { "data-over": "true" } : undefined;
  }

  // The tabs of moved sessions follow them, in every space: the one shown, those kept in memory, and those only stored.
  function retargetMovedTabs(moved: readonly WorkspaceMovedSession[]) {
    if (moved.length === 0) return;
    writeTabs(current => retargetSessionTabs(current, moved));
    for (const [id, kept] of spaceTabs.current) spaceTabs.current.set(id, { ...kept, tabs: retargetSessionTabs(kept.tabs, moved) });
    try {
      for (const space of spacesHub.getSnapshot().spaces) {
        if (space.id === shownSpace.current || spaceTabs.current.has(space.id)) continue;
        const key = spaceStorageKey(sessionTabsKey, space.id);
        const stored = restoreSessionTabs(() => localStorage.getItem(key));
        if (stored) persistSessionTabs(value => localStorage.setItem(key, value), retargetSessionTabs(stored, moved));
      }
    } catch { /* Storage that cannot be read keeps what it has: those tabs are dropped when their space is shown. */ }
  }

  // A session chosen in its menu, or dropped on the row of a project, goes to that project.
  async function moveSessionTo(moving: SessionMoveDrag, target: WorkspaceProject) {
    const capability = mutation?.capability;
    if (movingPending.current || !owned || !capability?.canMutate() || target.archived || target.id === moving.from.projectId) return;
    // The locks of the menu, again: a rename or a delete may have become pending or unconfirmed while the window was open.
    if (batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current
      || renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current) {
      setMovingSession(null);
      showToast({ message: t(sessionMoveMessage("busy")), intent: "warning", icon: "warning-sign", timeout: 6000 });
      return;
    }
    movingPending.current = true;
    setMovingBusy(true);
    // Sessions moved, but the list after the move could not be read: it is read again once the move ends.
    let readAgain = false;
    try {
      const result = await moveSession(status?.hostEpoch, moving.from, { projectId: target.id, projectPath: target.path }, moving.id, capability);
      if (!creationAlive.current) return;
      // Their tabs follow them with the list published next, this fresh one or, if it cannot be read, a later one.
      pendingTabMoves.current = [...pendingTabMoves.current, ...result.moved];
      let fresh: WorkspaceSnapshot | undefined;
      if (result.moved.length > 0 || result.kind === "error" && result.code === "move_unconfirmed") {
        markProjectInspection(false);
        try { fresh = await workspace.snapshot({}, { signal: creationRefresh.current.signal, timeoutMilliseconds: 30_000 }); }
        catch { fresh = undefined; }
        if (!creationAlive.current) return;
      }
      readAgain = result.moved.length > 0 && !fresh?.configured;
      if (fresh?.configured) {
        publishWorkspaceState({ kind: "ready", snapshot: fresh });
        const selected = selectedSessionId.current;
        if (selected && result.moved.some(value => value.sessionId === selected)) selectProject(target.id, selected);
      }
      setMovingSession(null);
      if (result.kind === "moved") {
        if (fresh && !movedSessionsVisible(fresh, result.moved))
          showToast({ message: t("The session moved, but the refreshed list does not show it in {project} yet. Refresh to check.", { project: target.name }),
            intent: "warning", icon: "warning-sign", timeout: 8000 });
        else showToast({ message: t("Moved to {project}.", { project: target.name }), intent: "success", icon: "tick", timeout: 4000 });
        // What went wrong without stopping the move, as the host says it (the log has all of it).
        if (result.warnings.length > 0)
          showToast({ message: `${t("The session moved, but:")} ${result.warnings[0]}`, intent: "warning", icon: "warning-sign", timeout: 12000 });
      } else showToast({ message: t(sessionMoveMessage(result.code)), intent: "danger", icon: "error", timeout: 8000 });
    } finally {
      movingPending.current = false;
      if (creationAlive.current) {
        setMovingBusy(false);
        // What happened meanwhile (a run that ended, a session an agent created) is not lost with the reads skipped.
        sessionLists.resume(readAgain);
      }
    }
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

  function creationProviderChoice(inPopover = false) {
    const inventory = configurationState.snapshot;
    // A new session is in the space shown: only the providers of that space are offered.
    const providers = inventory?.providerRuntimeAvailable ? providersInSpace(inventory.providers.slice(0, 32).filter(provider => provider.enabled
      && provider.id.length > 0 && provider.id.length <= 256 && provider.id === provider.id.trim()
      && !/[\u0000-\u001f\u007f-\u009f\ud800-\udfff]/u.test(provider.id)
      && inventory.providers.filter(other => other.id === provider.id).length === 1), spacesState.available ? spaceId : null, providerSpaces) : [];
    // The provider in use is named even before the user picks one: the one chosen earlier, else the default provider, else the first enabled one.
    const usedProvider = draftChoices.value.providerKey;
    const change = (field: "providerKey" | "agentPromptId" | "modelId" | "reasoningEffort" | "permissionMode", next: string) => {
      invalidateCreation(); draftChoices.change(field, next);
    };
    const select = <HTMLSelect fill value={usedProvider} aria-label={t("Provider for new session")} disabled={creatingBusy || !owned} onChange={event => change("providerKey", event.target.value)}>
      {!usedProvider && <option value="">{t("No provider")}</option>}
      {usedProvider && !providers.some(provider => provider.id === usedProvider) && <option value={usedProvider} disabled>{usedProvider}</option>}
      {providers.map(provider => <option key={provider.id} value={provider.id}>{provider.isDefault ? t("{name} (default)", { name: provider.id }) : provider.id}</option>)}
    </HTMLSelect>;
    const locked = creatingBusy || !owned;
    const value = draftChoices.value;
    const draftModel = draftChoices.models.find(model => model.id === value.modelId);
    const efforts = draftModel?.efforts ?? null;
    const reasoning = reasoningShown(value.reasoningEffort ?? null, draftModel);
    const choiceId = inPopover ? "create" : "new";
    // The permission mode the session starts in, shown as an existing session shows its own, where the provider offers modes.
    const permissionModes = draftChoices.permissionModes;
    const shownPermission = permissionModes.length > 0 ? value.permissionMode ?? draftChoices.defaultPermissionMode : undefined;
    return <ComposerSelectionFields sessionId={choiceId} onOpenCatalog={page => { if (inPopover) setCreatingVisible(false); navigate(page); }}
      summary={{ agent: draftChoices.prompts.find(prompt => prompt.id === value.agentPromptId)?.name ?? t(draftChoices.loadingPrompts ? "Loading…" : "Host default"),
        provider: usedProvider ? providerName(providerLogos, usedProvider) ?? usedProvider : t("No provider"), providerKey: usedProvider || null, modelId: value.modelId || null,
        model: value.modelId ? draftChoices.models.find(model => model.id === value.modelId)?.name ?? value.modelId : t(draftChoices.loadingModels ? "Loading…" : "No model"),
        reasoning: reasoning.effort ?? (draftChoices.loadingModels ? t("Loading…") : null),
        reasoningNote: reasoning.note === "none" ? t("{model} has no reasoning effort", { model: draftModel!.name })
          : reasoning.note === "unused" ? `${t("Reasoning")}: ${value.reasoningEffort} · ${t("Not used")}` : null,
        permission: shownPermission === undefined ? null : { mode: shownPermission,
          name: shownPermission === null ? t("The setting of the CLI") : permissionModeName(shownPermission) ? t(permissionModeName(shownPermission)!) : shownPermission },
        review: reviewSetting === null ? null : draftReview ?? reviewSetting,
        sandbox: !draftSandboxScopes ? null : draftSandbox === null ? sessionSandboxSummary(draftSandboxScopes)
          : { profile: draftSandbox.toLowerCase() === notSandboxed ? null : draftSandbox, source: "session", problem: null },
        subSessions: sessionOptionsOf(draftOptions).subSessions }}
      review={reviewSetting !== null && <ReviewBeforeCommitSelect id={`composer-review-${choiceId}`} own={draftReview} setting={reviewSetting} disabled={locked}
        onChange={next => setDraftReview(next)} />}
      sandbox={draftSandboxScopes && <SessionSandboxSelect id={`composer-sandbox-${choiceId}`} own={draftSandbox} inherited={draftSandboxScopes.profile}
        choices={draftSandboxScopes.choices} disabled={locked} onChange={next => setDraftSandbox(next)} />}
      options={{ changed: changedSessionOptions(draftOptions) + (value.context?.tokens != null ? 1 : 0),
        content: <>
          <FormGroup label={t("Context size")} labelFor={`composer-context-${choiceId}`}>
            <ContextSizeSelect id={`composer-context-${choiceId}`} value={value.context?.tokens ?? null} model={draftModel}
              loading={draftChoices.loadingModels} disabled={locked} onChange={tokens => { invalidateCreation(); draftChoices.changeContext(tokens); }} />
          </FormGroup>
          <SessionOptionsFields id={choiceId} value={draftOptions} disabled={locked} onChange={next => setDraftOptions(next)} />
        </> }}
      permission={permissionModes.length > 0 ? <PermissionModeSelect id={`composer-permission-${choiceId}`} value={value.permissionMode} modes={permissionModes}
        defaultMode={draftChoices.defaultPermissionMode} disabled={locked} onChange={next => change("permissionMode", next)} /> : undefined}
      agent={<HTMLSelect fill id={`composer-agent-${choiceId}`} aria-label={t("Agent prompt")} value={value.agentPromptId}
        disabled={locked || draftChoices.loadingPrompts || draftChoices.loadingModels || !draftChoices.prompts.length} onChange={event => change("agentPromptId", event.target.value)}>
        {!draftChoices.prompts.some(prompt => prompt.id === value.agentPromptId) && <option value={value.agentPromptId}>{t(draftChoices.loadingPrompts ? "Loading…" : "Host default")}</option>}
        {draftChoices.prompts.map(prompt => <option key={prompt.id} value={prompt.id}>{prompt.name}</option>)}
      </HTMLSelect>} provider={select}
      model={<ComposerModelPicker id={`composer-model-${choiceId}`} value={value.modelId || null} models={draftChoices.models} providerKey={usedProvider || null}
        disabled={locked} loading={draftChoices.loadingModels} onChange={modelId => change("modelId", modelId)}
        tiers={composerTierEntries(draftChoices.tiers, t)} currentTier={draftChoices.tier}
        onChooseTier={tier => { invalidateCreation(); draftChoices.chooseTier(tier); }} />}
      reasoning={<ReasoningSlider value={value.reasoningEffort ?? null} efforts={efforts} complete={!!draftModel?.effortsComplete} model={draftModel?.name} disabled={locked}
        onChange={next => change("reasoningEffort", next)} />} />;
  }

  const draftSendWaits = useRef(false);
  const createLatest = useRef<(fromDraft?: boolean) => Promise<void>>(async () => { });
  createLatest.current = createSelectedSession;
  async function createSelectedSession(fromDraft = false) {
    if (creationPending.current || !owned || !snapshot || !mutation?.capability.canMutate() || selectedProject?.archived
      || projectId !== null && !selectedProject || settingsVisible.current || dialog || searchOpen
      || currentView.current !== "workspace" || modalDialogOpen() || !draftChoices.ready) return;
    // A send asked while the prompt is dictated waits for the last words: the dictation is stopped, they are written,
    // and the session is then created with what the prompt holds.
    if (fromDraft) {
      const written = speech.finishForSend(`draft:${draftScope}`);
      if (written) {
        if (!draftSendWaits.current) {
          draftSendWaits.current = true;
          void written.then(() => { draftSendWaits.current = false; void createLatest.current(true); });
        }
        return;
      }
    }
    // The text as it is now, not as it was when App was last rendered.
    const typed = localDrafts.peek(draftScope) ?? localDraft;
    if (fromDraft && (!draftChoices.ready || sessionId !== null || (!typed.text.trim() && !localImages.images.length)
      || localImages.images.length > 0 && (typed.text.length > imageLimits.text || typed.text !== "" && !typed.text.trim()))) return;
    // A prompt waits while git makes the worktree its session will work in.
    const chosenPlace = selectedProject ? workPlaces.get(selectedProject.id) : undefined;
    if (fromDraft && selectedProject && chosenPlace?.worktree && preparedWorktrees.preparing(selectedProject.id, chosenPlace.base)) return;
    invalidateCreation(); // Deliberate capture fences any outstanding local paste.
    const handoff = fromDraft ? { scope: draftScope, ...typed, imageKey: localImageKey, images: localImages.images,
      imageGeneration: localImageGeneration.current } : null;
    // The session is created in the permission mode chosen: its first Send keeps the mode the session has.
    const creationPermissionMode = draftChoices.value.permissionMode;
    const creationSelection = { ...draftChoices.value, permissionMode: null };
    const creationTier = draftChoices.tier;
    const recordHandoff = (outcome: string) => { if (handoff) setDraftHandoffNotice(outcome); };
    creationPending.current = true;
    const providerId = creationSelection.providerKey || null;
    const target: SessionTarget = selectedProject
      ? { scope: "project", projectId: selectedProject.id, projectPath: selectedProject.path } : { scope: "global" };
    const scope = target.scope === "project" ? target.projectId : null;
    // The place is the one the chip shows, in the form beside the row as in the draft.
    const place = selectedProject ? workPlaces.get(selectedProject.id) ?? null : null;
    // The worktree made while the prompt was typed: a prompt waits until git has made it, and its session works there.
    const worktreeProject = target.scope === "project" && place?.worktree ? target.projectId : null;
    const preparedWorktreeId = worktreeProject !== null ? preparedWorktrees.ready(worktreeProject, place!.base) : null;
    const title = handoff ? null : creatingTitle.trim() || null;
    // The form closes at once: the New session tab of its scope is shown, and says that the session starts. The
    // window is not held meanwhile.
    if (!handoff) showScopeDraft(scope);
    const epoch = status?.hostEpoch;
    const capability = mutation.capability;
    // A prompt is handed to its session only if nothing changed in the window meanwhile (Cancel transfer is one change).
    const generation = creationGeneration.current;
    // Whether the user still looks at the tab the session starts in: then the session is opened there. It is created
    // whatever the user does meanwhile, and only said otherwise.
    const isCurrent = () => creationAlive.current && (!handoff || generation === creationGeneration.current)
      && currentHostEpoch.current === epoch && currentHostAvailable.current && capability.canMutate()
      && selectedScope.current === scope && selectedSessionId.current === null && currentView.current === "workspace"
      && !settingsVisible.current && !modalDialogOpen()
      && (!handoff || localDrafts.peek(handoff.scope)?.revision === handoff.revision)
      && (!handoff || submissions.imageDrafts.get(handoff.imageKey) === handoff.images)
      && (!handoff || localImageGeneration.current === handoff.imageGeneration)
      && (target.scope === "global" || currentSnapshot.current?.projects.filter(project => project.id === target.projectId).length === 1
        && currentSnapshot.current.projects.some(project => project.id === target.projectId && project.path === target.projectPath && !project.archived));
    const say = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate(shownLocale.current, key, parameters);
    const draftRetained = "The session was created while its prompt was not shown. The prompt keeps its text; nothing was sent.";
    let failedHere = false;
    setCreatingBusy(true);
    setCreatingScope(Object.freeze({ projectId: scope, handoff: !!handoff }));
    setCreatingMessage("");
    if (handoff) setDraftHandoffNotice("");
    // The tab of the scope shows the steps of the creation, read from the host while it runs.
    const requestId = crypto.randomUUID();
    const showSteps = (change: (current: NonNullable<typeof startSteps>) => NonNullable<typeof startSteps> | null) =>
      setStartSteps(current => current?.requestId === requestId ? change(current) : current);
    setStartSteps({ requestId, projectId: scope, fromDraft: !!handoff, steps: [], error: null });
    const readSteps = async () => {
      try {
        const progress = await workspace.createSessionProgress({ expectedHostEpoch: epoch!, requestId }, { timeoutMilliseconds: 5000 });
        if (creationAlive.current && progress.status === "ok") showSteps(current => ({ ...current, steps: progress.steps }));
      } catch { /* The steps are read again; the creation does not depend on them. */ }
    };
    let creating = true;
    void (async () => {
      while (creating && creationAlive.current) {
        await readSteps();
        await new Promise(resolve => setTimeout(resolve, 300));
      }
    })();
    try {
      const result = await createSession(epoch, target, title, capability, providerId, place, spacesState.available ? spaceId : null, requestId,
        creationPermissionMode, preparedWorktreeId, draftOptions).finally(() => { creating = false; });
      // The options were for this session: the next one starts with the defaults.
      if (result.kind === "created") setDraftOptions(null);
      // The session took the worktree, or it is still free for the next one.
      if (worktreeProject !== null && preparedWorktreeId !== null) void preparedWorktrees.settle(worktreeProject, preparedWorktreeId);
      // A session that made its own worktree after a failure: the failure no longer stands under the next prompt.
      else if (worktreeProject !== null && result.kind === "created" && ["failed", "dismissed"].includes(preparedWorktrees.get(worktreeProject)?.state ?? ""))
        preparedWorktrees.discard(worktreeProject);
      if (!creationAlive.current) return;
      // The agent prompt chosen in the draft was for this session: the next one starts with the one of its space.
      if (result.kind === "created")
        draftChoices.forgetDraftPrompt(target.scope === "project" ? target.projectId : null, target.scope === "project" ? target.projectPath : null);
      // The choice of review the draft gave the session, before anything is sent to it.
      if (result.kind === "created" && draftReview !== null) {
        await sessionOperations.setReviewBeforeCommit({ expectedEpoch: epoch, sessionId: result.id, reviewBeforeCommit: draftReview }, { timeoutMilliseconds: 30_000 })
          .catch(() => null);
        setDraftReview(null);
        if (!creationAlive.current) return;
      }
      // The sandbox the draft chose for the session, before anything is sent to it.
      if (result.kind === "created" && draftSandbox !== null) {
        const reply = await sandboxSettings.setSession({ expectedEpoch: epoch, sessionId: result.id, value: draftSandbox }, { timeoutMilliseconds: 30_000 }).catch(() => null);
        if (reply?.status !== "ok") showToast({ message: say("The sandbox was not changed ({status}).", { status: reply?.status ?? "no_answer" }),
          intent: "warning", icon: "warning-sign", timeout: 8000 });
        setDraftSandbox(null);
        if (!creationAlive.current) return;
      }
      if (result.kind === "created") {
        // The worktree lacks some of its submodules: the session works there all the same, and the user is told.
        if (result.warning) showToast({ message: say("The submodules of the worktree could not be checked out: {message}", { message: result.warning }),
          intent: "warning", icon: "warning-sign", timeout: 15_000 });
        // Acquire without publishing: a late create-specific read cannot overwrite a newer
        // host/catalog/selection. Other explicit refresh callers keep their existing behavior.
        let fresh: WorkspaceSnapshot | undefined;
        try { fresh = await workspace.snapshot({}, { signal: creationRefresh.current.signal, timeoutMilliseconds: 30_000 }); }
        catch { /* A failed read is not evidence that creation had no effects. */ }
        if (!creationAlive.current) return;
        const candidate = fresh && createdSessionSelection(fresh, result);
        const selection = candidate && fresh && selectedTab(fresh, candidate.projectId, candidate.sessionId) ? candidate : undefined;
        const row = selection && fresh?.sessions.find(session => session.id === selection.sessionId);
        const sameHost = currentHostEpoch.current === epoch && capability.canMutate();
        if (!fresh || !selection || !row || !sameHost) {
          showToast({ message: say("The session was created, but it could not be opened from here. Look for it in the list of sessions."), intent: "warning", icon: "warning-sign", timeout: 12_000 });
          recordHandoff("Session created, but it could not be opened from here. Original draft retained; nothing sent.");
          return;
        }
        // Revalidate preferences against this newly verified session's choices, never
        // manufacture session authority from the pre-creation catalog DTOs.
        let choices: Awaited<ReturnType<typeof sessionOperations.choices>> | undefined;
        try {
          choices = await sessionOperations.choices({ expectedEpoch: epoch!, sessionId: selection.sessionId },
            { signal: creationRefresh.current.signal, timeoutMilliseconds: 15000 });
          capability.observe(choices);
        } catch { /* Original draft and choices stay available for manual review. */ }
        if (!creationAlive.current) return;
        const offered = choices && choices.status === "ok" && choices.epoch === epoch && choices.sessionId === selection.sessionId
          && choices.current && validSelection(choices, creationSelection) ? choices : undefined;
        // A session that starts with the model of the standard tier runs on that tier, and falls back along it.
        if (offered && creationTier) void sessionOperations.setTier({ expectedEpoch: epoch!, sessionId: selection.sessionId, tier: creationTier },
          { timeoutMilliseconds: 15000 }).catch(() => { /* The session keeps its model; only its fallback is not set. */ });
        // The user went elsewhere meanwhile: the session is listed, and a toast offers it. Nothing is sent.
        if (!isCurrent() || !await retention.admit(epoch!, [selection.sessionId], isCurrent, () => {}) || !isCurrent()) {
          if (currentHostEpoch.current === epoch) publishWorkspaceState({ kind: "ready", snapshot: fresh });
          if (offered) nextSendSelections.set(epoch!, selection.sessionId, offered, creationSelection);
          if (!handoff) setCreatingTitle("");
          recordHandoff(draftRetained);
          showToast({ message: say("The session “{title}” was created.", { title: plainTitle(row.title) }), intent: "success", icon: "tick", timeout: 8000,
            action: { text: say("Show"), onClick: () => revealSessionLatest.current(row) } });
          return;
        }
        if (handoff) {
          if (!offered) {
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
          if (!isCurrent() || !nextSendSelections.set(epoch!, selection.sessionId, offered, creationSelection)) {
            recordHandoff("Session created, but draft choices could not be verified. Original draft retained; nothing sent.");
            return;
          }
          recordHandoff("");
          // The composer of the session sends what it was given, its attachments too: it refuses the images a model cannot read, and keeps them.
          autoSend.current = { sessionId: selection.sessionId, text: handoff.text, images: handoff.images.length };
          // The session now owns the text and the attachments; the New session tab starts empty again.
          if (handoff.scope === draftScope && localDrafts.peek(draftScope)?.revision === handoff.revision) editLocalDraft("");
          if (handoff.images.length) submissions.imageDrafts.replace(handoff.imageKey, handoff.images, []);
        } else {
          // The session exists. When what was chosen cannot be kept for its first Send, it opens as it is, and says so.
          if (!offered || !nextSendSelections.set(epoch!, selection.sessionId, offered, creationSelection))
            showToast({ message: translate(shownLocale.current, "The session uses its default agent and model."), intent: "warning", icon: "warning-sign", timeout: 8000 });
        }
        publishWorkspaceState({ kind: "ready", snapshot: fresh });
        // The session takes the place of the prompt it was started from: that prompt keeps no tab of its own.
        leftPrompt.current = null;
        selectedScope.current = selection.projectId;
        setProjectId(selection.projectId);
        // A session that was just created is one to work in: its tab is its own.
        keptSession.current = selection.sessionId;
        setSessionId(selection.sessionId);
        setCreatingVisible(false);
        setCreatingTitle("");
        navigate("workspace");
      } else {
        // Where no worktree can ever be made, the next session works in the folder of the project again.
        if (result.code === "worktree_failed" && (result.reason === "not_repository" || result.reason === "git_unavailable") && target.scope === "project")
          setWorkPlace(target.projectId, inProjectFolder);
        const message = result.code === "worktree_failed" ? worktreeCreationMessage(result.reason, result.message) : sessionCreationMessage(result.code);
        // A creation that may have happened is looked for in the list of sessions, read again.
        if (result.code === "create_unconfirmed") void refreshProjects(creationRefresh.current.signal);
        // Still at the tab of the creation: its steps say which one failed and why, with Retry and Close.
        if (result.code !== "create_unconfirmed" && isCurrent()) {
          await readSteps();
          showSteps(current => ({ ...current, error: message }));
          failedHere = true;
        } else {
          if (handoff) recordHandoff(message + " Original draft retained; nothing sent.");
          else showToast({ message, intent: result.code === "create_unconfirmed" ? "warning" : "danger", icon: "warning-sign", timeout: 12_000 });
        }
      }
    } finally {
      // Whatever the outcome, another session can be created: the host never makes the same request twice.
      creationPending.current = false;
      if (creationAlive.current) { setCreatingBusy(false); setCreatingScope(null); if (!failedHere) showSteps(() => null); }
    }
  }

  // After a creation that was refused or is unconfirmed: the list of sessions is read again, to be looked at.
  function refreshSessionsAfterCreation() {
    void refreshProjects(creationRefresh.current.signal).then(fresh => {
      if (creationAlive.current) setCreatingMessage({ key: fresh ? "Session list refreshed. Inspect the entries before creating another."
        : "Could not refresh the session list. Inspect before creating another." });
    });
  }

  // The window of a fork or a rewind is the one of the selected session's pane: a command only asks for it.
  function openSessionBranch(mode: BranchMode) {
    const selection = selectedSessionInfoSelection(snapshot, selectedSession, projectId, selectedSessionId.current, selectedScope.current);
    if (view === "workspace" && owned && currentProjectWritable() && selection) branchHost.requests.open(selection.sessionId, mode);
  }
  // A fork: the new session is listed, given the prompt of the turn it stops before as its draft, and opened in a tab.
  async function forkSessionOfPane(row: WorkspaceSession, paneProjectId: string | null, beforeRunId: string | null): Promise<ForkResult> {
    const epoch = status?.hostEpoch ?? undefined;
    const target = branchTarget(currentSnapshot.current, row, paneProjectId);
    if (!target) return { kind: "error", code: "scope_missing" };
    const result = await sessionBranch.fork(epoch, target, row.id, beforeRunId, mutation?.capability);
    if (result.kind === "forked") branchHost.requests.done(row.id);
    if (result.kind === "forked" && creationAlive.current) void showFork(result, epoch!, row.title);
    return result;
  }
  async function showFork(result: Extract<ForkResult, { kind: "forked" }>, epoch: string, sourceTitle: string) {
    const say = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate(shownLocale.current, key, parameters);
    // The composer of the fork does not exist yet: the prompt waits for it as its draft. A draft that is there is never replaced.
    const prompt = composerPrompt(result.prompt);
    if (prompt) transferPromptDraft(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value), result.id, prompt);
    let fresh: WorkspaceSnapshot | undefined;
    try { fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 }); } catch { /* The fork exists whatever the list says. */ }
    if (!creationAlive.current) return;
    const title = plainTitle(result.title || sourceTitle);
    const fork = fresh?.configured && currentHostEpoch.current === epoch ? (catalog.current ?? fresh).sessions.find(session => session.id === result.id) : undefined;
    if (!fresh || !fork) {
      showToast({ message: say("The fork “{title}” was created, but it could not be opened from here. Look for it in the list of sessions.", { title }), intent: "warning", icon: "warning-sign", timeout: 12_000 });
      return;
    }
    publishWorkspaceState({ kind: "ready", snapshot: fresh });
    keptSession.current = fork.id;
    revealSessionLatest.current(fork);
    focusPromptSoon();
    showToast({ intent: "success", icon: "tick", timeout: 8000, message: result.turnCount === 0 ? say("The fork “{title}” was created empty.", { title })
      : say("The fork “{title}” was created with {turns}.", { title, turns: result.turnCount === 1 ? say("1 turn") : say("{count} turns", { count: result.turnCount }) }) });
  }
  // A rewind: the pane of the session is read again, its composer gets the prompt of the first removed turn, and the user is told what was removed.
  async function rewindSessionOfPane(row: WorkspaceSession, paneProjectId: string | null, beforeRunId: string, keepRemovedTurns: boolean): Promise<RewindResult> {
    const epoch = status?.hostEpoch ?? undefined;
    const target = branchTarget(currentSnapshot.current, row, paneProjectId);
    if (!target) return { kind: "error", code: "scope_missing" };
    const result = await sessionBranch.rewind(epoch, target, row.id, beforeRunId, keepRemovedTurns, mutation?.capability);
    if (result.kind === "rewound") branchHost.requests.done(row.id);
    if (result.kind === "rewound" && creationAlive.current) void showRewind(result, epoch!, row);
    return result;
  }
  async function showRewind(result: Extract<RewindResult, { kind: "rewound" }>, epoch: string, row: WorkspaceSession) {
    const say = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate(shownLocale.current, key, parameters);
    // The prompt goes back to the composer when it is empty; a draft that is there is kept, and the prompt offered beside it.
    // The pane is mounted again below: its composer starts from what is stored.
    const prompt = composerPrompt(result.prompt);
    const offered = prompt && givePromptBack(prompt, promptBackComposer(result.id), true) === "offered" ? prompt : null;
    setRewoundHere(current => new Map(current).set(result.id, Math.max(result.rewindCount, row.rewindCount + 1, (current.get(result.id) ?? 0) + 1)));
    focusPromptSoon();
    const title = plainTitle(row.title), kept = result.removedTurnsSessionId;
    const turns = result.removedTurnCount === 1 ? say("1 turn") : say("{count} turns", { count: result.removedTurnCount });
    showToast({ intent: "success", icon: "tick", timeout: 12_000,
      message: kept ? say("Rewound “{title}”: {turns} removed and kept in “{kept}”. Changed files were left as they are.", { title, turns, kept: plainTitle(result.removedTurnsTitle ?? kept) })
        : say("Rewound “{title}”: {turns} removed and deleted. Changed files were left as they are.", { title, turns }),
      action: kept ? { text: say("Open the kept session"), onClick: () => openLinkedSession.current(kept) } : undefined });
    if (offered) showToast({ intent: "primary", icon: "info-sign", timeout: 30_000, message: say("The composer holds a draft: the prompt of the removed turn was not put in it."),
      action: { text: say("Copy the prompt"), onClick: () => void navigator.clipboard.writeText(offered).catch(() => { }) } });
    let fresh: WorkspaceSnapshot | undefined;
    try { fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 }); } catch { /* The session is rewound whatever the list says. */ }
    if (!creationAlive.current) return;
    if (fresh?.configured && currentHostEpoch.current === epoch) publishWorkspaceState({ kind: "ready", snapshot: fresh });
    else showToast({ message: say("The session was rewound, but the list of sessions could not be read. Refresh the sessions to see it as it is now."), intent: "warning", icon: "warning-sign", timeout: 12_000 });
  }
  branchActions.current = { fork: forkSessionOfPane, rewind: rewindSessionOfPane };

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
  // The worktree of a deleted session goes when its question asked it: one that holds changes that are not committed
  // stays, and its branch goes only when its commits are all in another branch. What became of it is told in a toast.
  async function removeDeletedWorktree(epoch: string, projectId: string, worktree: Readonly<{ name: string; path: string }>) {
    const reply = await worktreesApi.removeMany({ expectedEpoch: epoch, projectId, paths: [worktree.path], discard: [], deleteMergedBranches: true },
      { timeoutMilliseconds: 600_000 }).then(value => removalReply(value, [worktree.path]), () => "unconfirmed");
    if (!creationAlive.current) return;
    const result = typeof reply === "string" ? { status: reply, message: null, branchKept: null, branchDeleted: null } : reply[0];
    if (result.status === "ok") {
      const branch = result.branchDeleted ? t("The branch {branch} was deleted.", { branch: result.branchDeleted })
        : result.branchKept ? t("A branch that holds commits of its own is kept.") : "";
      showToast({ message: `${t("The worktree was removed.")} ${branch}`.trim(), intent: "success", icon: "tick", timeout: 6000 });
    } else showToast({ message: t("The worktree {name} stays: {reason}", { name: worktree.name, reason: removalReason(result.status, result.message, t) }),
      intent: "warning", icon: "warning-sign", timeout: 8000 });
    refreshSessionList();
  }

  async function deleteSessionRow(session: WorkspaceSession, asked: boolean, worktree: Readonly<{ name: string; path: string }> | null = null) {
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
            setSessionId(recovered.sessionId);
          }
          setDeletingId(null);
          if (index >= 0) setDeletedRow({ index });
          if (worktree && selectedProject && status?.hostEpoch) void removeDeletedWorktree(status.hostEpoch, selectedProject.id, worktree);
        } else {
          uncertainDelete.current = captured;
          setDeleteLocked(true);
          failed("Deletion may have completed, but the refreshed catalog did not confirm absence. No retry will be sent.");
        }
      } else if (result.code === "has_children") {
        // Children the window did not know of: they are shown, with the choice to delete them with it.
        setDeletingId(null);
        showSessionChildren(session, true);
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
  const [projectDeleteTarget, setProjectDeleteTarget] = useState<ProjectDeleteTarget | null>(null);
  const [projectDeleteBusy, setProjectDeleteBusy] = useState(false);
  const [projectDeleteMessage, setProjectDeleteMessage] = useState("");
  // A question is asked beside a row: when the Explorer goes, as in a window made narrow, it goes with its row.
  useEffect(() => {
    if (railVisible) return;
    if (!deletingPending.current) setDeletingId(null);
    if (!archiveBusy) setArchiveAsk(null);
    if (!projectDeleteBusy) setProjectDeleteTarget(null);
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
    if (!scope || archiveBusy || projectArchive.locked || projectDeleteBusy) return;
    setArchiveMessage("");
    setProjectDeleteTarget(null);
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

  // Deleting the selected project is asked about beside its row, every time: its sessions go with it.
  function projectDeleteScope(): ProjectDeleteTarget | null {
    const matches = currentSnapshot.current?.projects.filter(project => project.id === selectedScope.current);
    const project = matches?.length === 1 ? matches[0] : null;
    if (!project || !owned || !mutation?.capability.canMutate() || !currentHostEpoch.current
      || settingsVisible.current || currentView.current !== "workspace") return null;
    return { epoch: currentHostEpoch.current, id: project.id, path: project.path, name: project.name, archived: project.archived,
      generation: browserRevision.current + projectRenameGeneration.current };
  }
  function beginProjectDelete() {
    const target = projectDeleteScope();
    if (!target || projectDeletion.locked || projectArchive.locked || archiveBusy || projectRenamePending.current || !!projectRenameTarget
      || deletingPending.current || renamingPending.current || batchDeletion.locked()) return;
    setProjectDeleteMessage("");
    setArchiveAsk(null);
    setArchiveMessage("");
    setProjectDeleteTarget(Object.freeze(target));
  }
  async function deleteConfirmedProject() {
    const target = projectDeleteTarget;
    if (!target || projectDeletion.locked) return;
    setProjectDeleteBusy(true);
    setProjectDeleteMessage("");
    try {
      const outcome = await projectDeletion.confirm(target, projectDeleteScope());
      if (!creationAlive.current) return;
      if (outcome === "deleted") {
        setProjectDeleteTarget(null);
        // Do not redirect a newer selection when this original finishes.
        if (projectDeleteCurrent(target, projectDeleteScope())) selectProject(null);
      } else {
        setProjectDeleteMessage(t(outcome === "inUse" ? "A session of this project is running or waits for an answer. Stop it or let it end first."
          : outcome === "children" ? "A session of another project was started from this project. Delete that session first."
            : outcome === "failed" ? "The project could not be deleted completely. Some of its sessions may be gone. Try again."
              : "The project changed. Nothing was deleted."));
        if (outcome !== "failed") return;
      }
      // What is left is shown: the project is gone, or some of its sessions are.
      if (currentHostEpoch.current !== target.epoch || !mutation?.capability.canMutate()) return;
      const version = browserRevision.current;
      try {
        const fresh = await workspace.snapshot({}, { timeoutMilliseconds: 30_000 });
        if (creationAlive.current && currentHostEpoch.current === target.epoch && mutation.capability.canMutate()
          && version === browserRevision.current && fresh.configured) publishWorkspaceState({ kind: "ready", snapshot: fresh });
      } catch { /* The list is read again later. */ }
      // The question stays for another try, about the project as it is now.
      const left = outcome === "failed" ? projectDeleteScope() : null;
      if (left?.id === target.id) setProjectDeleteTarget(Object.freeze(left));
    } finally { if (creationAlive.current) setProjectDeleteBusy(false); }
  }

  // Settings pages also edit the selected project's settings when it can be written.
  const settingsProject = selectedProject && !selectedProject.archived ? { id: selectedProject.id, name: selectedProject.name } : null;
  // The folder of the color schemes of the user, in the code editor: the host finds it, and the window leaves Settings.
  function openColorSchemeFolder() {
    const failed = () => showToast({ message: translate(shownLocale.current, "The file could not be opened."), intent: "danger", icon: "error", timeout: 8000 });
    void settingsFiles.open({ expectedEpoch: status?.hostEpoch ?? null, projectId: null, kind: "colorSchemes", scope: "Global", id: null, part: null }, { timeoutMilliseconds: 15000 })
      .then(result => { if (result.status === "ok") closeSettings(); else failed(); }, failed);
  }
  // The form of a new session is beside the row of its scope. What is said about its creation is said there, not also in the list.
  const creatingShown = creatingVisible && view === "workspace" && !settingsOpen && (projectId === null || !!selectedProject);
  const newPromptDisabled = creatingBusy || !draftChoices.ready || !owned || !mutation?.capability.canMutate() || !snapshot
    || !!selectedProject?.archived || projectId !== null && !selectedProject || (!localDraft.text.trim() && !localImages.images.length)
    || localImages.images.length > 0 && (localDraft.text.length > imageLimits.text || localDraft.text !== "" && !localDraft.text.trim())
    || draftWorktreeWaits;
  const referenceAvailable = owned && !!mutation?.capability.canMutate() && !settingsOpen && !!selectedProject && !selectedProject.archived
    && !!snapshot && (sessionId === null || !!selectedTab(snapshot, projectId, sessionId));
  const referenceEpoch = status?.hostEpoch, referenceProject = selectedProject?.id, referencePath = selectedProject?.path, referenceLifetime = creationGeneration.current;
  const selectedReference = useMemo(() => referenceAvailable && referenceEpoch && referenceProject && referencePath !== undefined
    ? { expectedEpoch: referenceEpoch, projectId: referenceProject, projectPath: referencePath, sessionId, lifetime: referenceLifetime,
      capturePopup: captureReferencePopup, observe: observeReference } : null,
  [referenceAvailable, referenceEpoch, referenceProject, referencePath, sessionId, referenceLifetime, captureReferencePopup, observeReference]);
  const presented = sessionTabPresentation(tabs, snapshot, projectId, sessionId, tabMode(openingMode(sessionId)));
  // A secondary window starts no session: it has no tab for the prompt of a new one, but while it holds nothing else.
  const tabView = secondary && presented.open.length + fileTabs.open.length > 0 ? { ...presented, noPrompt: true } : presented;
  const backgroundTabs = snapshot && status?.hostEpoch ? backgroundSessionTabs(snapshot, queue.sessions(status.hostEpoch), tabView.open) : undefined;
  // A rewind made here is known before the list of sessions says it; one made elsewhere (the alta tool) comes with the list.
  const paneRewinds = (id: string, rewindCount: number) => Math.max(rewindCount, rewoundHere.get(id) ?? 0);
  // The owners of the panes that are no longer mounted go once the dock has let them go.
  useEffect(() => {
    if (snapshot) pruneSessionPaneOwners(sessionPaneOwners, snapshot, status?.hostEpoch, [...tabView.open, ...backgroundTabs ?? []], paneRewinds);
  });
  return <ShellLanguageContext.Provider value={language}><HostReadsContext.Provider value={hostReads}><SpeechContext.Provider value={speech}><ProviderBrandsContext.Provider value={providerLogos}><PluginUiContext.Provider value={pluginUiValue}><PluginHostBridgeContext.Provider value={pluginHostBridge}><PluginButtonsContext.Provider value={pluginButtonsHost}><PluginButtonsActiveContext.Provider value={fileTabs.active ?? null}><PullRequestSettingsContext.Provider value={owned ? openPullRequestSettings : null}><SessionLinksContext.Provider value={sessionLinks}><MessageLinksContext.Provider value={messageLinkEpoch ? openMessageLink : null}><MarkdownImagesContext.Provider value={markdownImages}><SessionWidthContext.Provider value={sessionWidthControl}><ShowChangesContext.Provider value={owned ? showProjectChanges : null}><OpenTerminalContext.Provider value={owned ? openSessionTerminal : null}><OpenEditorContext.Provider value={owned ? openSessionEditor : null}><OpenRunContext.Provider value={owned ? openRun : null}><ExternalOpenerContext.Provider value={owned ? externalOpener : null}><SessionListRefreshContext.Provider value={owned ? refreshSessionList : null}><LocalSearchContext.Provider value={owned ? localSearchHub : null}><ReviewsHubContext.Provider value={owned ? reviewsHub : null}><ReviewOpenerContext.Provider value={owned ? reviewOpener : null}><ReviewsListOpenerContext.Provider value={owned ? openReviewsList : null}><ShellAppearance appearance={appearance} preview={appearancePreview} /><div className={`app-shell ide-shell${secondary ? " secondary-window" : ""}`}
      style={secondary && windowsState.current?.color ? { "--window-color": windowsState.current.color } as CSSProperties : undefined}>
    {(closing || hostState.busy || linkLost || !widthSaved) && <div className="shell-notices" data-neoastra-no-drag>
      {closing ? <div className="shell-notice" role="alert">
        {closing.step ? t("CodeAlta is closing: waiting for {step} ({seconds} s).", { step: closing.step, seconds: closing.seconds }) : t("CodeAlta is closing.")}
        {closing.canForceExit && <Button size="small" intent="danger" onClick={forceExit}>{t("Force exit")}</Button>}</div>
      : hostState.lost ? <div className="shell-notice" role="alert">{t("This window lost its link to CodeAlta: reload it to go on.")}
        <Button size="small" intent="danger" onClick={() => window.location.reload()}>{t("Reload")}</Button></div>
      : hostState.busy ? <div className="shell-notice" role="status">
        {t("CodeAlta is busy and has not answered for {seconds} s. Nothing is lost: your work goes on when it answers.", { seconds: hostState.seconds })}</div>
        : linkLost && <div className="shell-notice" role="alert">{t("This window lost its link to CodeAlta: reload it to go on.")}
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
          {/* A secondary window says which one it is, and what changes its name, its icon and its color. */}
          {secondary && windowsState.current ? <WindowBadge hub={windowsHub} window={windowsState.current} onClose={closeWindowTabs}
            spaceName={spacesState.available && spacesState.spaces.length > 1 ? spacesState.spaces.find(space => space.id === spaceId)?.name ?? null : null} />
          // What the title bar has no room for folds into a menu: the Explorer and Settings always show.
          : <ActivityRail label={t("Workspace navigation")} moreLabel={t("More")}
            first={<Button ref={projectRailToggle} variant="minimal" size="small" active={railVisible} icon={<AppIcon name="folder" size={16} />} aria-label={t("Explorer")} title={t("Explorer")} aria-expanded={railVisible} aria-controls="project-rail" onClick={toggleProjects} />}
            items={[
              { key: "search", icon: "search", className: "activity-search", label: t("Search"), title: `${t("Search")} (Ctrl+P)`, onClick: () => openSearch() },
              { key: "automations", icon: "automation", className: "activity-automations", disabled: !owned, active: !!fileTabs.active && isAutomationsTab(fileTabs.active),
                label: t("Automations"), title: `${t("Automations")} (Ctrl+G, Ctrl+M)`, onClick: () => openAutomations() },
              { key: "workflows", icon: "workflow", className: "activity-workflows", disabled: !owned, active: !!fileTabs.active && isWorkflowsTab(fileTabs.active),
                label: t("Workflows"), title: t("Workflows"), onClick: () => openFile(workflowsTab) },
              { key: "work", icon: "task", className: "activity-work", disabled: !owned, active: !!fileTabs.active && isWorkItemsTab(fileTabs.active), dot: work.some(item => item.stage === "todo"),
                label: t("Work items"), title: `${t("Work items")} (Ctrl+G, Ctrl+I)`, onClick: () => openWorkItems() },
              { key: "issues", icon: "issueOpen", className: "activity-issues", disabled: !owned, active: !!fileTabs.active && isIssuesTab(fileTabs.active),
                label: t("Issues"), title: `${t("Issues")} (Ctrl+G, Ctrl+B)`, onClick: () => openFile(issuesTab) },
              { key: "canvases", icon: "canvases", className: "activity-canvases", disabled: !owned, active: !!fileTabs.active && isCanvasesTab(fileTabs.active),
                label: t("Canvases"), title: t("Canvases"), onClick: () => openFile(canvasesTab) },
              { key: "bookmarks", icon: "bookmark", className: "activity-bookmarks", disabled: !owned, active: !!fileTabs.active && isBookmarksTab(fileTabs.active),
                label: t("Bookmarks"), title: `${t("Bookmarks")} (Ctrl+G, Ctrl+F)`, onClick: () => openFile(bookmarksTab) },
            ]}
            last={<><PluginButtons place="Rail" context={pluginButtonContext} />
              <Button variant="minimal" size="small" icon={<AppIcon name="settings" size={16} />} className="activity-settings" aria-label={t("Settings & extensions")} title={`${t("Settings & extensions")} (Ctrl+,)`} onClick={() => navigate("appearance")} /></>} />}
        </WindowBrand>
        {!secondary && <div className="window-actions">
          <PluginButtons place="TitleBar" context={pluginButtonContext} />
          {spacesState.available && <SpaceSwitch spaces={spacesState.spaces} shownId={spaceId} activity={spacesState.activity} canEdit={owned} request={spaceMenuRequest}
            onShow={id => { if (showSpace(id)) focusPromptSoon(); }} onCreate={() => setSpaceDialog(true)} onOrganize={() => navigate("spaces")} />}
          <Button variant="minimal" size="small" className="home-open" icon={<AppIcon name="home" size={16} />} disabled={!owned || !landingCommand}
            active={!!fileTabs.active && isCanvasTab(fileTabs.active) && fileTabs.active.pluginKey === landingPluginKey && fileTabs.active.canvasId === landingCanvasId}
            aria-label={t("Home")} title={t("Home")} onClick={() => { if (landingCommand) runPluginCommand(landingCommand.id); }} />
          <Button variant="minimal" size="small" className="documentation-open" icon={<AppIcon name="documentation" size={16} />} disabled={!owned}
            active={!!fileTabs.active && isDocumentationTab(fileTabs.active)} aria-label={t("Documentation")} title={t("Documentation")} onClick={() => openDocumentation()} />
          {shellPreferences && <WindowZoom zoom={shellPreferences.zoom} run={runCommand} />}
          <Button variant="minimal" size="small" className="theme-switch" icon={<AppIcon name={themeIcons[theme]} size={16} />}
            aria-label={t("Theme: {theme}", { theme: t(themeLabel(theme)) })} title={t("Theme: {theme}", { theme: t(themeLabel(theme)) })} onClick={() => setTheme(nextTheme(theme))} />
        </div>}
        <WindowControls snapshot={windowSnapshot} />
        <SessionContentLayout sessionsHidden={!railVisible}
          projects={sessions => { const railHead = <>
          <div className="panel-title"><span title={projectListing?.evidenceNotice ?? undefined}>{t("Projects")}<span className="count">{snapshot?.projects.length ?? 0}</span>
            {newLabel && <span className="label-new-anchor"><LabelFormPopover heading={t("New label")} submitLabel={t("Create")} labels={labelsState.labels} parent={null} ownId={null}
              initial={{ name: "", icon: "", color: "" }} busy={newLabel.busy} error={newLabel.error}
              onSubmit={look => void createTopLabel(look.name, look.icon, look.color)} onCancel={() => setNewLabel(null)} /></span>}</span><span>
            {snapshot && <Button variant="minimal" size="small" className="rail-action" icon={<AppIcon name="collapseAll" size={16} />} aria-label={t("Collapse all")} title={t("Collapse all")}
              disabled={!projectTree.expanded.length} onClick={() => setProjectTree(collapseAllScopes)} />}
            {snapshot && <PopoverNext placement="bottom-end" content={<Menu aria-label={t("Project actions")}>
              <MenuDivider title={t("Sort projects")} />
              <MenuItem roleStructure="listoption" selected={projectSort === "name"} text={t("Name")} onClick={() => setProjectSort("name")} />
              <MenuItem roleStructure="listoption" selected={projectSort === "recent"} text={t("Recent visible updates")} onClick={() => setProjectSort("recent")} />
              {labelsState.available && <><MenuDivider title={t("Labels")} />
                <MenuItem roleStructure="listoption" selected={projectGrouping === "labels"} text={t("Group by labels")} onClick={() => setProjectGrouping("labels")} />
                <MenuItem roleStructure="listoption" selected={projectGrouping === "flat"} text={t("One list")} onClick={() => setProjectGrouping("flat")} />
                {owned && <MenuItem icon={<AppIcon name="tag" size={15} />} text={`${t("New label")}…`} onClick={() => setNewLabel({ busy: false, error: null })} />}
                <MenuItem icon={<AppIcon name="settings" size={15} />} text={`${t("Organize labels")}…`} onClick={() => navigate("labels")} /></>}
              <MenuDivider />
              <MenuItem icon={<AppIcon name="open" size={15} />} text={`${t("Open project")}…`} label="Ctrl+O" onClick={() => setDialog("project")} />
              <MenuItem icon={<AppIcon name="archive" size={15} />} text={t(confirms.projectArchive ? selectedProject?.archived ? "Unarchive project…" : "Archive project…"
                : selectedProject?.archived ? "Unarchive project" : "Archive project")}
                disabled={!selectedProject || !owned || !mutation?.capability.canMutate() || archiveBusy || projectArchive.locked || projectDeletion.locked} onClick={beginProjectArchive} />
              <MenuItem intent="danger" icon={<AppIcon name="trash" size={15} />} text={t("Delete project…")}
                disabled={!projectDeleteScope() || projectDeletion.locked || projectArchive.locked || archiveBusy} onClick={beginProjectDelete} />
            </Menu>}>
              <Button variant="minimal" size="small" className="rail-action" icon={<AppIcon name="ellipsis" size={18} />} aria-label={t("Project actions")} title={t("Project actions")} />
            </PopoverNext>}
            <Button variant="minimal" size="small" className="rail-action" icon={<AppIcon name="plus" size={18} />} aria-label={t("Add a project folder")} title={t("Add a project folder")} onClick={() => void addProjectFolder()} />
          </span></div>
          {workspaceState.kind === "loading" && <LoadingRows />}
          {workspaceState.kind === "unconfigured" && <div className="sidebar-empty">{t("No catalog configured. See the launch instructions below.")}</div>}
          {workspaceState.kind === "error" && <div role="alert" className="sidebar-empty error-text">{workspaceState.message}</div>}
          {snapshot && projectListing?.projects.length === 0 && (spaceId === defaultSpaceId
            // The Default space shows what belongs to no other space: projects that are all in other spaces are offered here.
            ? !showOthers && catalog.current?.projects.some(project => !project.archived) ? <div className="sidebar-empty space-empty" role="status">
              <p>{t("Your projects are in other spaces.")}</p>
              <Button size="small" icon={<AppIcon name="eye" size={14} />} onClick={() => setShowOthers(true)}>{t("Show them here")}</Button>
            </div> : <p className="sidebar-empty" role="status">
            {t("No projects in this snapshot.")}
            {projectId !== null && ` ${t("The selected project and session remain open.")}`}
          </p> : <div className="sidebar-empty space-empty" role="status">
            <p>{t("No project in this space yet.")}</p>
            {owned && <Button size="small" icon={<AppIcon name="plus" size={14} />} onClick={() => void addProjectFolder()}>{t("Add projects")}…</Button>}
          </div>)}
          </>;
          return <aside id="project-rail" className="project-rail" aria-label={t("Projects")} ref={projectRail} hidden={!railVisible}>
          <LabelDragGhost view={railDrag.view} />
          {!snapshot && railHead}
          {snapshot && <ProjectRailRows head={railHead} projects={projectListing?.projects ?? []} favorites={projectListing?.favorites ?? 0} selectedId={projectId} onSelect={id => showScopeDraft(id)} onKeep={id => showScopeDraft(id, true)} children={sessions}
            drop={owned ? projectDropTarget : undefined} footer={claudeCodeConfigured ? claudeCodeGroup(null) : undefined}
            labels={labelsState.available ? {
              groups: railGroups,
              folded: id => !filterView && isLabelFolded(projectTree, id),
              row: group => <LabelRailRow group={group} labels={spaceLabels} folded={!filterView && isLabelFolded(projectTree, group.label.id)}
                onFold={value => setProjectTree(current => setLabelFolded(current, group.label.id, value))} editing={owned ? labelEditing : undefined}
                drop={railDrag.view?.over?.labelId === group.label.id ? railDrag.view.over.place : undefined}
                drag={owned ? railDrag.source({ kind: "label", labelId: group.label.id }, group.label.name) : undefined}
                activity={labelActivity(labelSubtreeProjects(group.label.id, spaceLabels))} />,
              badges: project => <ProjectLabelBadges labels={projectLabels(project.id, spaceLabels)} all={spaceLabels} />,
              drag: owned && spaceLabels.length ? (project, from) => railDrag.source({ kind: "project", projectId: project.id, from }, project.name) : undefined,
              otherDrop: railDrag.view?.over?.labelId === "" ? "into" : undefined,
            } : undefined}
            filter={<SidebarFilterBox value={sidebarText} onChange={setSidebarText}
              empty={!!filterView && !filterView.scopes.size && !filterView.projects.size && !filterView.claudeCode.size && (!claudeCodeConfigured || claudeCodeSearched)} />}
            tree={{ expanded: id => filterView && filterOpens(filterView, id) ? !filterFolded.has(id) : isExpanded(projectTree, id),
              toggle: id => filterView && filterOpens(filterView, id) ? setFilterFolded(current => toggleFilterFold(current, id))
                : setProjectTree(current => toggleScope(current, id)),
              favorite: id => isFavorite(projectTree, id), setFavorite: (project, value) => setProjectTree(current => setFavorite(current, project.id, value)),
              sessions: scopeSessions,
              after: id => <TerminalList terminals={terminalsOf(terminalList, id)} rename={terminalWorkspace.hub.rename}
                active={fileTabs.active && isTerminalTab(fileTabs.active) ? fileTabs.active.terminalId ?? null : null}
                onOpen={showTerminal} onClose={terminal => terminalWorkspace.hub.close(terminal.id)} /> }}
            terminals={owned ? { count: id => terminalsOf(terminalList, id).length, create: project => void createTerminal(project.id) } : undefined}
            changes={owned ? { open: id => fileTabs.open.some(tab => isChangesTab(tab) && tab.projectId === id), show: project => showChanges(project) } : undefined}
            unfoldable={projectUnfoldable}
            activity={id => <><WaitingBadge count={spacesState.sessions.reduce((count, session) => count + (session.waiting && session.projectId === id ? 1 : 0), 0)} />
              <RunningSessionBadge controls={runtimeObservationControls()} projectId={id} />
              <ReminderBadge count={activeReminders ? scopeReminderCount(activeReminders, snapshot, id) : 0} />
              {id !== null && <WorkItemsBadge counts={workByProject.get(id)} onOpen={owned ? () => openWorkItems({ projectId: id }) : undefined} />}</>}
            canRename={owned} renameBusy={projectRenameBusy || !mutation?.capability.canMutate()} onRename={() => void beginProjectRename()}
            renaming={projectRenaming && projectRenameTarget ? { id: projectRenameTarget.id, form: <RenamePopover label={t("Project name")}
              value={projectRenameName} onChange={setProjectRenameName} busy={projectRenameBusy} disabled={projectRenameLocked || projectRenameConflict}
              error={projectRenameNotice ? workflowNotice(language.locale, projectRenameNotice) : null}
              onSubmit={() => void saveProjectRename()} onCancel={() => { projectRenameGeneration.current++; setProjectRenameTarget(null); }} /> } : undefined}
            asking={projectDeleteTarget && selectedProject && projectDeleteTarget.id === selectedProject.id ? { id: projectDeleteTarget.id,
              form: <ConfirmPopover always title={t("Delete this project?")} subject={selectedProject.name}
                detail={t("It is removed from CodeAlta and its sessions are deleted: they cannot be restored. Its folder and its files are kept.")}
                confirmLabel={t("Delete")} intent="danger" busy={projectDeleteBusy} error={projectDeleteMessage || null}
                onConfirm={() => void deleteConfirmedProject()}
                onCancel={() => { setProjectDeleteTarget(null); setProjectDeleteMessage(""); }} /> }
              : archiveAsk && selectedProject && archiveAsk.id === selectedProject.id && archiveAsk.archived === selectedProject.archived ? { id: archiveAsk.id,
              form: <ConfirmPopover title={t(archiveAsk.archived ? "Unarchive this project?" : "Archive this project?")} subject={selectedProject.name}
                detail={t(archiveAsk.archived ? "It can be worked in again. Nothing is started."
                  : "It becomes read-only in CodeAlta until it is unarchived. Its files and its sessions are kept.")}
                confirmLabel={t(archiveAsk.archived ? "Unarchive" : "Archive")} busy={archiveBusy} error={archiveMessage || null}
                onConfirm={remember => { if (remember) setConfirm("projectArchive", false); void archiveSelectedProject(true); }}
                onCancel={() => { setArchiveAsk(null); setArchiveMessage(""); }} /> } : undefined}
            creating={creatingShown ? { id: projectId, form: <NewSessionPopover heading={selectedProject ? t("New session in {name}", { name: selectedProject.name }) : t("New chat")}
              title={creatingTitle} onTitle={setCreatingTitle} choices={creationProviderChoice(true)}
              place={selectedProject && <ProjectContext epoch={owned ? status?.hostEpoch ?? null : null} project={selectedProject} read={projectGit.status} branches
                place={draftPlaceControl && { ...draftPlaceControl, onOpenSettings: () => { setCreatingVisible(false); navigate("worktrees"); } }} />}
              disabled={creatingBusy || !draftChoices.ready || !owned || !mutation?.capability.canMutate() || !!selectedProject?.archived}
              error={creatingMessage ? workflowNotice(language.locale, creatingMessage) : null}
              refresh={draftChoices.failed ? { label: t("Refresh composer choices"), run: draftChoices.refresh } : undefined}
              // A refusal that was read goes with the form, which closes on Create.
              onCreate={() => void createSelectedSession()} onCancel={() => { setCreatingVisible(false); setCreatingMessage(""); }} /> } : undefined}
            actions={{ current: () => ({ ...currentProjectDetailsContext(),
              active: creationAlive.current && currentView.current === "workspace" && !settingsVisible.current && !!projectRail.current && !projectRail.current.hidden,
              generation: browserRevision.current + projectRenameGeneration.current, modalGeneration: creationGeneration.current,
              canMutate: owned && !!mutation?.capability.canMutate(),
              locked: projectRenamePending.current || !!uncertainProjectRename.current || projectRenameLocked
                || !!projectRenameTarget || projectArchive.locked || projectDeletion.locked || !!projectDeleteTarget || !!projectOpening.getSnapshot() }),
              open: selectProject, rename: () => void beginProjectRename(), archive: beginProjectArchive, archiveAsks: confirms.projectArchive, delete: beginProjectDelete,
              worktrees: owned ? project => openWorktrees(project) : undefined,
              sandbox: owned && status?.hostEpoch ? { epoch: status.hostEpoch, api: sandboxSettings } : undefined,
              labels: owned && labelsState.available ? (project, close) => <ProjectLabelsPopover projectId={project.id} projectName={project.name} labels={spaceLabels}
                assign={labelsHub.assign} create={(name, projectIds) => labelsHub.create(name, { space: labelSpace }, projectIds)} onClose={close} /> : undefined,
              search: owned && searchOn ? { excluded: project => isExcluded(localSearchHub.getSnapshot(), "project", project.id),
                set: (project, excluded) => void localSearchHub.setExcluded("project", project.id, excluded).then(status => {
                  if (!creationAlive.current) return;
                  if (status !== "ok") showToast({ message: t("The search settings could not be saved."), intent: "warning", icon: "warning-sign", timeout: 6000 });
                  else showToast({ message: t(excluded ? "{name} is kept out of search." : "{name} is searched again.", { name: project.name }), intent: "none", icon: "search", timeout: 4000 });
                }) } : undefined,
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
          <OverflowTip within={projectRail} target=".session-row > button:first-child" label=".session-title" />
        </aside>; }}
          splitter={<PaneSplitter className="session-splitter" label={t("Resize Explorer")} value={ideWidth.width} hidden={narrow || !railVisible}
            onResize={delta => setIdeWidth(value => resizeIdeWidth(value, delta))} onReset={() => setIdeWidth(defaultIdeWidth)} />}
          sessions={<aside className="session-rail" aria-label={t("Sessions")} ref={sessionRail} hidden={!railVisible}>
          {creatingBusy && !creatingShown && <p role="status" className="notice">{t("Creating session…")}</p>}
          {creatingMessage && !creatingShown && <p role="alert" className="notice error-text">{workflowNotice(language.locale, creatingMessage)}</p>}
          {creatingMessage && !creatingShown && <button type="button" className="quiet-button" disabled={creatingBusy} onClick={refreshSessionsAfterCreation}>{t("Refresh session list")}</button>}
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
              const { session, depth, diagnostic, tooltip, subAgents, parentElsewhere } = entry.row;
              const twist = sessionTwist(entry, sessionTree.toggle);
              const menu = activeMenu?.id === session.id ? activeMenu : null;
              const access = sessionActionAccess(session,
                menu ?? { id: session.id, projectId, hostEpoch: status?.hostEpoch ?? null },
                menu ? session.id : selectedSession === session && selectedSessionId.current === session.id ? sessionId : null,
                selectedScope.current, selectedProject, currentHostEpoch.current ?? null, owned,
                mutation?.capability.canMutate() ?? false, batchDeletion.locked() || renamingBusy || deletingBusy || renamingPending.current || deletingPending.current,
                renameLocked || deleteLocked || !!uncertainRename.current || !!uncertainDelete.current);
              return <div className={`session-row${menu ? " menu-open" : ""}`} key={session.id} data-session-id={session.id} data-rename-keys {...sessionDragSource(session, projectId)}
                onContextMenu={event => {
                  const target = event.target as HTMLElement;
                  if (target.closest("input, textarea, select, [contenteditable='true'], .session-actions-menu")) return;
                  event.preventDefault();
                  openSessionMenu(session.id, event.currentTarget.querySelector<HTMLButtonElement>(".session-actions-trigger"));
                }}
                onKeyDown={event => {
                  const editing = !!(event.target as HTMLElement).closest("input, textarea, select, [contenteditable='true']");
                  const composing = event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229;
                  // Delete, Backspace, F2, F and A on a row do what the same entries of its menu do; the row keeps F2 from
                  // Rename Project (data-rename-keys), as the lists of files and terminals do.
                  if (isSessionDeleteKey(event, composing) && event.target === event.currentTarget.firstElementChild) {
                    event.preventDefault(); event.stopPropagation();
                    deleteSessionFromRow(session);
                    return;
                  }
                  const shortcut = sessionRowShortcut(event, composing);
                  if (shortcut && event.target === event.currentTarget.firstElementChild) {
                    event.preventDefault(); event.stopPropagation();
                    runSessionRowShortcut(shortcut, session);
                    return;
                  }
                  if (!isSessionContextKey(event.key, event.shiftKey, composing, editing)) return;
                  event.preventDefault(); event.stopPropagation();
                  openSessionMenu(session.id, event.currentTarget.querySelector<HTMLButtonElement>(".session-actions-trigger"));
                }}>
              <button type="button" aria-pressed={sessionId === session.id} aria-expanded={twist && !twist.collapsed} aria-describedby={`session-tooltip-${index}`} title={tooltip}
                data-full-text={plainTitle(session.fullTitle || session.title)} style={{ paddingLeft: sessionRowIndent(depth) }} onKeyDown={event => sessionTwistKey(event, twist)}
                onClick={() => protectNavigation([session.id], () => { setMenuTarget(null); setSessionId(session.id); setRenamingId(null); setRenamingMessage(""); setDeletingId(null); setDeletingMessage(""); })}
                onDoubleClick={() => keepSession(projectId, session)}>
                <SessionRowTitle session={session} depth={depth} diagnostic={diagnostic} subAgents={subAgents} twist={twist} parentElsewhere={parentElsewhere} />{sessionMarks(session, projectId)}
              </button>
              <span id={`session-tooltip-${index}`} role="tooltip" className="session-tooltip"
                tabIndex={tooltip.length > 256 ? 0 : undefined}>{tooltip}</span>
              <button type="button" className="icon-button session-actions-trigger" aria-label={t("Actions for {title} (ID: {id})", { title: session.title, id: session.id })}
                aria-haspopup="menu" aria-expanded={!!menu}
                onClick={event => openSessionMenu(session.id, event.currentTarget)}><AppIcon name="ellipsis" size={16} /></button>
              {menu && access.open && menuOrigin.current && <SessionRowMenu anchor={menuOrigin.current} container={document.body}
                context={{ projectId: menu.projectId, sessionId: menu.id }}
                title={t("Session actions for {title}", { title: session.title })} current={() => true}
                // The menu closes before it runs the chosen entry; clear the target afterwards so the entry still sees it.
                onClose={() => queueMicrotask(() => setMenuTarget(value => value === menu ? null : value))}
                items={[
                  { key: "open", label: t("Open session"), icon: "open", onSelect: () => runSessionMenuAction("open", session, menu) },
                  { key: "rename", label: t("Rename…"), icon: "edit", endNode: <KeyGesture gesture="F2" />, disabled: !access.rename, onSelect: () => runSessionMenuAction("rename", session, menu) },
                  { key: "fork", label: t("Fork…"), icon: "branch", endNode: <KeyGesture gesture="F" />, disabled: !access.fork, onSelect: () => runSessionMenuAction("fork", session, menu) },
                  ...(providerHasRemoteControl(providerLogos.get(session.providerKey?.toLowerCase() ?? "")?.type)
                    ? [{ key: "remote-control", label: t("Remote Control…"), icon: "remote" as const, disabled: !access["remote-control"], onSelect: () => runSessionMenuAction("remote-control", session, menu) }] : []),
                  ...openElsewhereEntries(t, session, providerLogos.get(session.providerKey?.toLowerCase() ?? "")?.type, access, action => runSessionMenuAction(action, session, menu)),
                  ...sessionBookmarkEntries(session),
                  ...sessionLineageEntries(session),
                  ...sessionReviewEntries(session),
                  ...(projectId === null ? [] : [{ key: "move", label: t("Move to project…"), icon: "folderInput" as const, disabled: !access.move, onSelect: () => runSessionMenuAction("move", session, menu) }]),
                  ...(spacesState.available && spacesState.spaces.length > 1
                    ? [{ key: "space", label: t("Move to space…"), icon: "space" as const, disabled: !access.space, onSelect: () => runSessionMenuAction("space", session, menu) }] : []),
                  { key: "archive", label: archivableDescendants(catalog.current?.sessions ?? [], session.id).length > 0 ? t("Archive…") : t("Archive"), icon: "archive", endNode: <KeyGesture gesture="A" />,
                    disabled: !access.archive || sessionArchiveBusy, onSelect: () => runSessionMenuAction("archive", session, menu) },
                  { key: "delete", label: confirms.sessionDelete ? `${t("Delete")}…` : t("Delete"), icon: "trash", endNode: <KeyGesture gesture="Delete" />, danger: true, disabled: !access.delete, onSelect: () => runSessionMenuAction("delete", session, menu) },
                  ...sessionCanvasEntries(session, selectedProject),
                ]} />}
              {renamingId === session.id && <RenamePopover label={t("Session title")} value={renamingTitle} onChange={setRenamingTitle}
                busy={renamingBusy} disabled={!owned || renameLocked} error={renamingMessage ? workflowNotice(language.locale, renamingMessage) : null}
                onSubmit={() => void renameSelectedSession()} onCancel={() => setRenamingId(null)} />}
              {deletingId === session.id && (() => {
                // A session that works in a worktree leaves it, with its branch: the question says where, and can remove it.
                const worktree = selectedProject && !selectedProject.archived ? deletedSessionWorktree(catalog.current?.sessions ?? [], session) : null;
                return <ConfirmPopover title={t("Delete this session?")} subject={plainTitle(session.title)}
                  detail={t("Its history is deleted and cannot be restored. The files of the project are kept.")}
                  confirmLabel={t("Delete")} intent="danger" busy={deletingBusy} error={deletingMessage || null}
                  onConfirm={remember => { if (remember) setConfirm("sessionDelete", false);
                    void deleteSessionRow(session, true, worktree && !worktree.shared && deletingWorktree ? worktree : null); }}
                  onCancel={() => { setDeletingId(null); setDeletingMessage(""); }}>
                  {worktree && <div className="confirm-worktree">
                    <p>{t("Its worktree {name} is kept in {path}, with its branch.", { name: worktree.name, path: worktree.path })}</p>
                    {worktree.shared ? <p>{t("Other sessions work in this worktree: it stays.")}</p>
                      : <Checkbox checked={deletingWorktree} disabled={deletingBusy} label={t("Also remove the worktree")}
                        onChange={event => setDeletingWorktree(event.currentTarget.checked)} />}
                    {deletingWorktree && !worktree.shared && <p className="muted-text">
                      {t("A worktree that holds changes that are not committed is kept, and its branch is deleted only when its commits are all in another branch.")}</p>}
                  </div>}
                </ConfirmPopover>;
              })()}
              {sessionArchiving?.id === session.id && sessionArchiving.from === "explorer" && sessionArchiveQuestion(session, "right-start")}
            </div>;
            })}
            {snapshot && visibleSessions.length === 0 && !filterView && <div className="sidebar-empty">{t(projectId === null ? "No chats." : "No sessions in this project.")}</div>}
            <div className="session-list-disclosure">
              {selectedList.hidden > 0 && <button type="button" className="quiet-button" onClick={() => setSessionExtra(projectId, extraSessions + recentSessionCount)}>{t("Show more…")} <span className="muted-text">({selectedList.hidden})</span></button>}
              {extraSessions > 0 && <button type="button" className="quiet-button" onClick={() => setSessionExtra(projectId, 0)}>{t("Show fewer")}</button>}
            </div>
            {withClaudeCodeSessions(selectedList.entries, projectId, claudeCodeConfigured).map(entry => entry.kind === "claude-code"
              && <Fragment key={`claude-code:${entry.projectId}`}>{claudeCodeGroup(entry.projectId)}</Fragment>)}
          </div>
        </aside>}
          content={<ProjectReferenceContext.Provider value={selectedReference}><main className="content">
          <SessionTabStrip key={spaceId} layout={spaceLayout(spaceId)} state={tabView} snapshot={snapshot}
            background={backgroundTabs}
            draftLabel={draft => { const project = draft.projectId === null ? null : snapshot?.projects.find(value => value.id === draft.projectId);
              return newSessionTabLabel(project, creatingScope?.projectId === draft.projectId); }}
            keep={tab => { if (snapshot && snapshot === currentSnapshot.current) setTabs(pinSessionTab(reconcileSessionTabs(tabs, snapshot), tab)); }}
            keepDraft={() => { const draft = snapshot && snapshot === currentSnapshot.current ? draftTabOf(snapshot, projectId) : null;
              if (draft && !singleTab) setTabs(openDraftTab(reconcileSessionTabs(tabs, snapshot!), draft)); }}
            selectDraft={draft => selectProject(draft.projectId)} closeDraft={closeScopeDraft} closePrompt={() => { leftPrompt.current = null; }} closeTabs={closeTabs}
            windowEntries={windowMenuEntries} dragOut={tabDragOut} externalDrag={externalDrag} arrival={arrival} onArrived={() => setArrival(null)}
            newSessionLabel={secondary ? t("Move a tab here") : newSessionTabLabel(selectedProject, creatingScope?.projectId === projectId)}
            renderSession={(tab, visible) => {
              const row = snapshot && resolveSessionTab(snapshot, tab);
              if (!snapshot) return null;
              if (!row) return <NonIdealState className="session-unavailable" icon={<AppIcon name="error" size={32} />} title={t("Session unavailable")}
                description={t("This session is no longer in the catalog. Close the tab or refresh the projects.")} />;
              const rewinds = paneRewinds(row.id, row.rewindCount);
              const ownerKey = sessionPaneKey(status?.hostEpoch, tab, row.createdAt, rewinds);
              const lineage = sessionLineage(row);
              const source = lineage && lineageTitle(lineage, (catalog.current ?? snapshot).sessions);
              const admitted = () => !!status && (currentHostAvailable.current
                ? !!status.hostEpoch && currentHostEpoch.current === status.hostEpoch && retention.hasClaim(status.hostEpoch, row.id)
                : !status.hostAvailable);
              let owners = sessionPaneOwners.get(ownerKey);
              if (!owners) { owners = createSessionPaneOwners(hostReads); sessionPaneOwners.set(ownerKey, owners); }
              return <SessionRetentionGate key={ownerKey} available={admitted()}
                fallback={<NonIdealState className="session-unavailable" icon={<AppIcon name="error" size={32} />} title={t("Session unavailable")} />}>
                <ProjectReferenceContext.Provider value={admitted() && owned && status?.hostEpoch && tab.projectId !== null
                && snapshot.projects.some(project => project.id === tab.projectId && project.path === tab.path && !project.archived)
                ? owners.reference({ expectedEpoch: status.hostEpoch, projectId: tab.projectId, projectPath: tab.path!, sessionId: row.id,
                  lifetime: creationGeneration.current, capturePopup: captureReferencePopup, observe: observeReference }) : null}>
              <SessionWorkspace sessionSpaces={sessionSpaces} session={row} canAdmit={admitted} rewinds={rewinds} waitingSubSessions={waitingBelow.get(row.id)} subSessions={workingBelow.get(row.id)}
                heldElsewhere={elsewhereSessions.get(row.id) ?? null}
                archive={owned && !row.archived ? { asks: archivableDescendants(catalog.current?.sessions ?? [], row.id).length > 0,
                  run: () => { protectNavigation([row.id], () => beginSessionArchive(row, "composer")); },
                  question: sessionArchiving?.from === "composer" ? sessionArchiveQuestion(row, "top-end") : null } : null} branch={owned ? branchHost : null} bookmarks={owned ? bookmarkHost : null}
                lineage={lineage && source ? <SessionLineageBanner lineage={lineage} title={source.title}
                  onOpen={source.exists ? () => void openAutomationSession(lineage.sourceId) : undefined} /> : undefined}
                remoteControlRequest={remoteControlAsk?.sessionId === row.id ? remoteControlAsk.request : null} snapshot={snapshot} selectedProjectId={tab.projectId}
                origin={row.automationId ? (() => {
                  const origin = sessionOrigin(row.id, row.automationId, automationState.items, automationState.runs, t);
                  return <SessionOrigin name={origin.name} summary={origin.summary} onOpen={owned ? () => openAutomations(origin.id) : undefined} />;
                })() : undefined}
                workCards={owned && workState.settings.notify ? <WorkItemCards hub={workHub} items={sessionCards(work, row.id)} preferredStart={workState.settings.start} busy={workBusy}
                  onStart={(item, start) => void startWork(item, start, { id: row.id, workingDirectory: row.workspacePath })}
                  onOpenList={item => openWorkItems({ projectId: item.projectId, key: workItemKey(item) })} /> : undefined}
                onRunActivity={(running, background) => { runtimeObservations.setLive(tab, running, background); noteRunActivity(tab.sessionId, running); }} notesReader={owners.notesReader} observing={visible && view === "workspace" && !settingsOpen}
                active={tab.sessionId === sessionId} notesHidden={!notesVisible} onActivate={() => { if (visible && (sessionId !== tab.sessionId || fileTabs.active)) selectSessionTab(tab, "preview"); }}
                infoTrigger={sessionInfoTrigger} remindersTrigger={remindersTrigger} compactTrigger={compactTrigger}
                infoLifetime={{ revision: 0, current: () => !!currentSnapshot.current && !!resolveSessionTab(currentSnapshot.current, tab)
                  && admitted() && currentView.current === "workspace" && !settingsVisible.current && currentHostEpoch.current === status?.hostEpoch }}
                preferredComposerHeight={composerHeights.get(composerSizeKey(status?.hostEpoch ?? null, tab.projectId, row.id))}
                onComposerHeight={height => setComposerHeights(sizes => rememberComposerHeight(sizes, composerSizeKey(status?.hostEpoch ?? null, tab.projectId, row.id), height))}
                onOpenCatalog={navigate} onOpenDocumentation={openDocumentation} onOpenReminders={openSelectedReminders} onOpenHelp={openHelp} onOpenCommands={openCommandSearch}
                readReminders={readReminders} reminderActions={reminderActions} status={status} mutation={mutation}
                activeReminderCount={activeReminders ? activeReminders.get(row.id) ?? 0 : null}
                autoSend={autoSend.current?.sessionId === row.id ? { text: autoSend.current.text, images: autoSend.current.images, consume: () => { autoSend.current = null; } } : null}
                submissions={submissions} timelineImages={timelineImages} toolRecords={toolRecords} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} draftIndicators={draftIndicators}
                askActions={askActions} display={owners.display} scrollMemory={scrollMemory} runtimeReader={owners.runtimeReader}
                permissionReviewer={owners.permissionReviewer} heldReviewer={owners.heldReviewer} inputReviewer={owners.inputReviewer} configuration={configurationState.snapshot}
                selections={nextSendSelections} timelineCommand={timelineCommand} /></ProjectReferenceContext.Provider></SessionRetentionGate>;
            }}
            capture={captureTabLifetime} drafts={tabDrafts} observations={runtimeObservationControls()} waiting={waitingSessions} waitingBelow={waitingBelow}
            elsewhere={elsewhereSessions}
            onSessionTabClick={focusPromptSoon}
            select={tab => selectSessionTab(tab, "preview")} close={tab => {
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
            renderFile={(tab, visible) => isClaudeCodeTab(tab)
              // The preview of a Claude Code conversation started elsewhere: its first message continues it here.
              ? <ClaudeCodePreviewPane key={fileTabKey(tab)} tab={tab} epoch={owned ? status?.hostEpoch ?? null : null} spaceId={claudeCodeSpace} api={claudeCodeSessions} previewApi={claudeCodeSessions.preview}
                drafts={localDrafts} imageDrafts={submissions.imageDrafts} draftIndicators={draftIndicators} scrollMemory={scrollMemory}
                visible={visible && view === "workspace" && !settingsOpen} busy={previewImporting === fileTabKey(tab)}
                importingElsewhere={!!previewImporting && previewImporting !== fileTabKey(tab)}
                onSend={prompt => void continueClaudeCodePreview(tab, prompt)}
                onOpenInVsCode={() => void openPreviewInVsCode(tab)} onExisting={(id, keep) => void showImportedSession(id, tab.name ?? "", keep ? undefined : tab.key)}
                projects={snapshot?.projects ?? []} target={claudeCodePreviewTarget(tab)}
                onTargetChange={project => setPreviewTargets(current => new Map(current).set(fileTabKey(tab), project.id))}
                onOpenHelp={openHelp} onOpenCommands={openCommandSearch} />
              : isCanvasTab(tab)
              ? <LandingShellContext.Provider key={fileTabKey(tab)} value={landingShell}><CanvasPanel tab={tab} spaceId={spaceId} hub={canvasHub} visible={visible && view === "workspace" && !settingsOpen}
                active={visible && sameFileTab(fileTabs.active, tab)} onActivate={activateShown(tab, visible)} onLook={look => changeCanvasLook(tab, spaceId, look)}
                onInstance={instance => instance ? canvasInstances.opened(tab, spaceId, instance) : canvasInstances.released(tab, spaceId)}
                onAbandoned={(instance, space) => canvasInstances.abandoned(tab, space ?? spaceId, instance)}
                onClose={() => closeFile(tab)} control={canvasControl(tab)}
                onOpenSource={folder => openPluginEditor(folder, { path: "plugin.cs", line: null, column: null, explorer: true })} /></LandingShellContext.Provider>
              : isCanvasesTab(tab)
              ? <CanvasesPanel key={fileTabKey(tab)} hub={canvasHub} tabs={fileTabs.open} projects={canvasProjects} sessions={canvasSessions} selection={canvasSelection}
                visible={visible && view === "workspace" && !settingsOpen} onActivate={activateShown(tab, visible)} onOpen={openCanvas} onNew={owned && canvasSelection.project ? newCanvas : null} />
              : isDocumentationTab(tab)
              ? <DocumentationPanel key={fileTabKey(tab)} hub={documentationHub} visible={visible && view === "workspace" && !settingsOpen} onActivate={activateShown(tab, visible)}
                onOpenSession={id => void openAutomationSession(id)} onProviders={() => navigate("providers")}
                onNotice={message => showToast({ message, intent: "warning", icon: "info-sign", timeout: 5000 })} />
              : isBookmarksTab(tab)
              // The bookmarks are the application's, whatever the space shown: each names its project among all of them.
              ? <BookmarksPanel key={fileTabKey(tab)} hub={bookmarkHost.hub} projects={(catalog.current ?? snapshot)?.projects ?? noBookmarkProjects}
                onActivate={activateShown(tab, visible)} onGo={bookmarkHost.go} onOpenSession={id => void openAutomationSession(id)}
                onNotice={message => showToast({ message, intent: "warning", icon: "warning-sign", timeout: 6000 })} />
              : isIssuesTab(tab)
              ? <IssuesPanel key={fileTabKey(tab)} api={issuesApi} epoch={!status ? undefined : owned ? status.hostEpoch : null}
                projects={snapshot?.projects.filter(project => !project.archived) ?? []} projectId={selectedProject && !selectedProject.archived ? selectedProject.id : null}
                visible={visible && view === "workspace" && !settingsOpen} preferredStart={workState.settings.start} onActivate={activateShown(tab, visible)}
                onOpenSession={id => void openAutomationSession(id)} onStarted={(id, _project, title) => offerStartedSession(id, title)}
                onNotice={message => showToast({ message, intent: "danger", icon: "error", timeout: 8000 })} providers={runProviders} sessionSettings={sessionSettingsFor} />
              : isWorkItemsTab(tab)
              ? <WorkItemsPanel key={fileTabKey(tab)} hub={workHub} epoch={owned ? status?.hostEpoch ?? null : null} projects={snapshot?.projects.filter(project => !project.archived) ?? []} sessions={snapshot?.sessions ?? []}
                runningSessions={runningSessionIds} projectId={selectedProject && !selectedProject.archived ? selectedProject.id : null}
                visible={visible && view === "workspace" && !settingsOpen} focus={workFocus} onFocused={() => setWorkFocus(null)}
                onActivate={activateShown(tab, visible)} onStart={(item, start, request, shown) => startWork(item, start, null, request, shown)} providers={runProviders} sessionSettings={sessionSettingsFor}
                onOpenSession={id => void openAutomationSession(id)}
                onOpenFile={(id, file) => { const project = snapshot?.projects.find(candidate => candidate.id === id); if (project) openEditor(project, { path: file, line: null, column: null, explorer: null }); }}
                onOpenSettings={() => navigate("workItems")} />
              : isAutomationsTab(tab)
              // The automations are the application's, whatever the space shown: each names its project among all of them.
              ? <AutomationsPanel key={fileTabKey(tab)} hub={automationsHub} projects={(catalog.current ?? snapshot)?.projects.filter(project => !project.archived) ?? []} sessions={(catalog.current ?? snapshot)?.sessions ?? []}
                projectId={selectedProject && !selectedProject.archived ? selectedProject.id : null} epoch={!status ? undefined : owned ? status.hostEpoch : null}
                providers={configurationState.snapshot?.providerRuntimeAvailable ? [...configurationState.snapshot.providers].filter(provider => provider.enabled).sort((a, b) => Number(b.isDefault) - Number(a.isDefault)) : []}
                visible={visible && view === "workspace" && !settingsOpen} focus={automationFocus} onFocused={() => setAutomationFocus(null)}
                onActivate={activateShown(tab, visible)} onOpenSession={id => void openAutomationSession(id)} />
              : isWorkflowsTab(tab)
              ? <WorkflowsPanel key={fileTabKey(tab)} api={workflowsApi} epoch={!status ? undefined : owned ? status.hostEpoch : null}
                projects={snapshot?.projects.filter(project => !project.archived) ?? []} sessions={(catalog.current ?? snapshot)?.sessions ?? []}
                projectId={selectedProject && !selectedProject.archived ? selectedProject.id : null} visible={visible && view === "workspace" && !settingsOpen}
                onActivate={activateShown(tab, visible)} onOpenSession={id => void openAutomationSession(id)} onOpenRun={owned ? openRun : undefined}
                onEditScript={owned ? (project, path) => openEditor(project, { path, line: null, column: null, explorer: null }) : undefined}
                providers={runProviders} sessionSettings={sessionSettingsFor} />
              : isWorkflowRunTab(tab)
              ? <RunTab key={fileTabKey(tab)} api={workflowsApi} epoch={!status ? undefined : owned ? status.hostEpoch : null} runId={tab.key ?? ""}
                visible={visible && view === "workspace" && !settingsOpen} onActivate={activateShown(tab, visible)} onGone={() => closeFile(tab)} />
              : isTerminalTab(tab)
              ? <TerminalPanel key={fileTabKey(tab)} workspace={terminalWorkspace} id={tab.terminalId ?? ""} terminal={terminalList.find(terminal => terminal.id === tab.terminalId)}
                visible={visible && view === "workspace" && !settingsOpen} active={visible && sameFileTab(fileTabs.active, tab)} look={terminalLook} onLook={changeTerminalLook}
                onActivate={activateShown(tab, visible)} rename={terminalWorkspace.hub.rename} onCloseTerminal={() => terminalWorkspace.hub.close(tab.terminalId ?? "")}
                onCreate={() => { const shown = terminalList.find(terminal => terminal.id === tab.terminalId); void createTerminal(shown?.projectId ?? (tab.projectId || null), shown?.sessionId ?? null); }} />
              : isChangesTab(tab)
              ? <ProjectChangesPanel key={fileTabKey(tab)} tab={tab} epoch={!status ? undefined : owned ? status.hostEpoch : null}
                projectName={snapshot?.projects.find(project => project.id === tab.projectId)?.name} request={changeRequests.get(tab.projectId)}
                sessions={snapshot?.sessions} onWorktreesChanged={refreshSessionList} onOpenSession={id => void openAutomationSession(id)}
                visible={visible && view === "workspace" && !settingsOpen} active={visible && sameFileTab(fileTabs.active, tab)} onActivate={activateShown(tab, visible)}
                onOpenFile={path => openEditor({ id: tab.projectId, path: tab.projectPath }, { path, line: null, column: null, explorer: null })} />
              : <ProjectEditor key={fileTabKey(tab)} tab={tab} editors={fileEditors} request={editorRequests.get(tab.projectId)}
              projectName={tab.name ?? snapshot?.projects.find(project => project.id === tab.projectId)?.name} platform={shellPreferences?.platform ?? "windows"}
              epoch={!status ? undefined : owned ? status.hostEpoch : null} onPickFile={isFolderTab(tab) ? undefined : openFilePicker}
              visible={visible && view === "workspace" && !settingsOpen} active={visible && sameFileTab(fileTabs.active, tab)} onActivate={activateShown(tab, visible)}
              // The files of a project take comments, sent to one of its sessions; a folder that is not a project's, none.
              review={owned && !isFolderTab(tab) && snapshot?.projects.some(project => project.id === tab.projectId)
                ? { sessions: reviewSessions(snapshot.sessions, tab.projectId, null, selectedSession?.id ?? null), current: selectedSession?.projectId === tab.projectId ? selectedSession.id : null,
                  openSession: id => void openAutomationSession(id) } : null} />}>
          <div id="active-session-content" className="active-session-content">
          {error && <div className="banner banner-error" role="alert">{error}</div>}
          {draftHandoffNotice && <p role="status" className="notice">{draftHandoffNotice}</p>}
          {!selectedSession && secondary
            // A secondary window starts no session: it holds the tabs moved into it.
            ? <NonIdealState className="window-empty" icon={<AppIcon name="window" size={32} />} title={t("Move a tab here")}
              description={t("Drag a tab to this window, or choose Move to window in the menu of a tab.")} />
            : !selectedSession
            ? <NewSessionWorkspace key={draftScope} project={selectedProject} chrome={draftChrome} worktree={!!draftPlaceControl && draftPlace.worktree}
                starting={startSteps && startSteps.projectId === projectId ? <SessionStartProgress steps={startSteps.steps} error={startSteps.error}
                  onRetry={startSteps.error && !creatingBusy ? () => { const fromDraft = startSteps.fromDraft; setStartSteps(null); void createSelectedSession(fromDraft); } : undefined}
                  onClose={startSteps.error ? () => setStartSteps(null) : undefined} /> : undefined}
                worktreeStatus={draftWorktree && preparedProject && status?.hostEpoch ? <WorktreePreparation worktree={draftWorktree}
                  onRetry={() => preparedWorktrees.retry(status.hostEpoch!, preparedProject)} onClose={() => preparedWorktrees.dismiss(preparedProject.id)} /> : undefined}
                preferredHeight={composerHeights.get(composerSizeKey(status?.hostEpoch ?? null, projectId, draftScope))}
                onHeight={height => setComposerHeights(sizes => rememberComposerHeight(sizes, composerSizeKey(status?.hostEpoch ?? null, projectId, draftScope), height))}>
                <ReadOnlyComposer key={draftScope} sessionId={draftScope} provider={null} draftIndicators={draftIndicators} folder={selectedProject?.path ?? null}
                  localImages={owned && snapshot?.configured && currentProjectWritable() ? localImages : undefined}
                  onOpenHelp={openHelp} onOpenCommands={openCommandSearch}
                  reason={t("Draft kept locally. Start a session to send it.")}
                  localDraft={{ read: () => localDrafts.peek(draftScope)?.text ?? "", subscribe: localDrafts.subscribe, edit: editLocalDraft, options: creationProviderChoice(),
                    disabled: newPromptDisabled, busy: creatingScope?.projectId === projectId, submit: () => void createSelectedSession(true),
                    surface: owned && status?.hostEpoch ? { epoch: status.hostEpoch, onOpenProviders: () => navigate("providers"),
                      contextTokens: draftChoices.models.find(model => model.id === draftChoices.value.modelId)?.contextTokens ?? null } : undefined,
                    action: <>
                      {draftChoices.failed && <Button variant="minimal" icon={<AppIcon name="refresh" size={16} />} disabled={creatingBusy}
                        aria-label={t("Refresh composer choices")} title={t("Refresh composer choices")} onClick={draftChoices.refresh} />}
                     <SendSplitButton enqueue={false} onEnqueueChange={() => { /* A session that has not started has nothing to wait for. */ }} enqueueDisabled optionsDisabled={newPromptDisabled}>
                       <Button intent="primary" icon={<AppIcon name="send" size={16} />} disabled={newPromptDisabled}
                         aria-label={t("Start session")} title={draftWorktreeWaits ? t("The worktree is not ready yet") : t("Start the session and send (Enter)")}
                         onClick={() => void createSelectedSession(true)} />
                     </SendSplitButton>
                     {creatingScope?.handoff && creatingScope.projectId === projectId && <Button variant="minimal" icon={<AppIcon name="stop" size={16} />} aria-label={t("Cancel transfer")}
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
    {settingsOpen && <SettingsOverlay section={settingsSection} onSection={navigate} onClose={closeSettings}
      spaces={spacesState.available ? spacesState.spaces : []} spacePage={spacePage} onSpacePage={setSpacePage}
      pluginPages={owned ? pluginPages : []} pluginPage={pluginPage} onPluginPage={openPluginSettings}>
      {settingsSection === "appearance" ? <ConfigurationPanel preferences={{ theme, setTheme, darker, setDarker,
        schemes: { colorScheme, setColorScheme, shownScheme, variant, customSchemes, library: schemeLibrary, preview: appearancePreview, platform: demoMode ? null : shellPreferences?.platform ?? null,
          onOpenFolder: owned ? openColorSchemeFolder : undefined }, sort: projectSort, setSort: setProjectSort, desktopCollapsed: railState.desktopCollapsed, setDesktopCollapsed, notices: preferenceNotices, recentSessionCount, setRecentSessionCount: value => { batchDeletion.invalidate(); setRecentSessionCount(value); }, subAgentCount, setSubAgentCount,
        sessionWidth, setSessionWidth }} />
      : settingsSection === "windows" ? <WindowSettings hub={windowsHub} state={windowsState} />
      : settingsSection === "general" ? <GeneralSettings
        remoteControl={owned && shellPreferences ? { reconnect: shellPreferences.reconnectRemoteControl === true, set: setReconnectRemoteControl } : null}
        closing={shellPreferences?.canKeepRunning ? { behavior: closeBehavior(shellPreferences.onClose), platform: shellPreferences.platform, trayIcon: shellPreferences.trayIcon, set: setOnClose } : null}
        keepAwake={owned && typeof shellPreferences?.keepAwake === "boolean" ? { on: shellPreferences.keepAwake, set: setKeepAwake } : null}
        externalEditor={owned ? { api: externalEditorSettings, onChanged: externalOpener.accept } : null} />
      : settingsSection === "permissions" ? <PermissionSettings confirms={{ ...confirms, set: setConfirm }} tools={owned && status?.hostEpoch ? { epoch: status.hostEpoch, api: globalConfig } : null}
        permissions={owned && shellPreferences ? { review: shellPreferences.reviewPermissions, set: setReviewPermissions,
          inherit: shellPreferences.inheritPermissions, setInherit: setInheritPermissions } : null} />
      : settingsSection === "sandbox" ? <SandboxSettings epoch={owned ? status?.hostEpoch ?? null : null} onOpenScope={openSandboxScope} />
      : settingsSection === "codeMode" ? <CodeModeSettings epoch={owned ? status!.hostEpoch : null} onOpenFile={owned ? closeSettings : undefined}
        onOpenScope={scope => { navigate("providers"); setProviderFocus(scope.providerKey); }} />
      : settingsSection === "spaces" ? <SpaceSettings hub={spacesHub} spaces={spacesState.spaces} projects={catalog.current?.projects ?? []} shownId={spaceId}
        chats={spaceChatCounts(catalog.current?.sessions ?? [])} showOthers={showOthers} onShowOthers={setShowOthers}
        activity={spacesState.activity} canEdit={owned && !!mutation?.capability.canMutate()} onShow={id => { showSpace(id); }} onCreate={() => setSpaceDialog(true)}
        page={spacePage} onPage={setSpacePage} onOpenPage={owned ? navigate : undefined} />
      : settingsSection === "labels" ? <LabelSettings hub={labelsHub} spaces={spacesState.spaces} projects={catalog.current?.projects ?? []} shownId={spaceId}
        canEdit={owned && !!mutation?.capability.canMutate()} />
      : settingsSection === "about" ? <AboutSettings status={status} bootError={!!error} demo={demoMode} logo={logoUrl}
        update={owned ? appUpdateResult : undefined} onOpenReleaseNotes={openReleaseNotes} onInstallUpdate={installUpdate} />
      : settingsSection === "plugin" && pluginPage ? <PluginCanvasSettingsPage key={`${pluginPage.pluginKey}/${pluginPage.canvasId}`} page={pluginPage} hub={canvasHub}
        control={canvasControl(pluginSettingsTab(pluginPage))} onLeave={() => navigate("plugins")}
        onInstance={instance => instance ? settingsCanvasInstances.opened(pluginSettingsTab(pluginPage), "settings", instance) : settingsCanvasInstances.released(pluginSettingsTab(pluginPage), "settings")}
        onAbandoned={instance => settingsCanvasInstances.abandoned(pluginSettingsTab(pluginPage), "settings", instance)}
        onOpenSource={folder => { closeSettings(); openPluginEditor(folder, { path: "plugin.cs", line: null, column: null, explorer: true }); }} />
      : settingsSection === "plugins" ? <PluginSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} revision={pluginRevision} canvases={declaredCanvases} spaceScope={resourceScope}
        askBeforeStop={confirms.pluginStop} onStopQuestionDismissed={() => setConfirm("pluginStop", false)}
        onOpenSettings={owned ? openPluginSettings : undefined}
        onEdit={owned ? folder => { closeSettings(); openPluginEditor(folder, { path: "plugin.cs", line: null, column: null, explorer: true }); } : undefined}
        onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "sessions" ? <SessionSettings epoch={owned ? status!.hostEpoch : null} onOpenFile={owned ? closeSettings : undefined}
        tabs={{ single: singleTab, setSingle: setSingleTab }} />
      : settingsSection === "worktrees" ? <WorktreeSettings epoch={owned ? status!.hostEpoch : null}
        pick={owned ? initial => pickFolder(desktopShell.pickFolder, t("Folder for worktrees"), initial) : undefined}
        onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "workItems" ? <WorkItemSettings hub={workHub} providers={runProviders} loadModels={loadRunModels} onOpenProviders={() => navigate("providers")}
        epoch={owned ? status!.hostEpoch : null} onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "pullRequests" ? <PullRequestSettings api={pullRequestPrompts} epoch={!status ? undefined : owned ? status.hostEpoch : null}
          project={selectedProject && !selectedProject.archived ? { id: selectedProject.id, name: selectedProject.name } : null}
          onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "mcpHost" ? <McpHostSettings epoch={owned ? status!.hostEpoch : null} developer={status?.developerMode ?? false} />
      : settingsSection === "search" ? <SearchSettings projects={catalog.current?.projects ?? snapshot?.projects ?? []} spaces={spacesState.spaces}
          onMeaningTurnedOn={() => setFollowMeaning(true)}
          onOpenCanvas={(() => { const item = canvasCatalog.find(value => value.pluginKey === "builtin:search" && value.id === "search");
            return item ? () => { closeSettings(); openCanvasHere(item); } : undefined; })()} />
      : settingsSection === "speech" ? <SpeechSettings api={speechApi} speech={speech} epoch={owned ? status!.hostEpoch : null} />
      : settingsSection === "storage" ? <StorageSettings api={storage} epoch={owned ? status!.hostEpoch : null} onOpenFile={owned ? closeSettings : undefined}
          maintenance={storageMaintenance.maintenance} retention={storageMaintenance.retention} />
      : settingsSection === "skills" ? <SkillSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} spaceScope={resourceScope}
        onEdit={owned ? folder => { closeSettings(); openSkillEditor(folder); } : undefined} onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "mcp" ? <McpServerSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} onOpenFile={owned ? closeSettings : undefined} spaceScope={resourceScope} />
      : settingsSection === "prompts" ? <AgentPromptSettings epoch={owned ? status!.hostEpoch : null} project={settingsProject} onOpenFile={owned ? closeSettings : undefined} spaceScope={resourceScope} />
      : settingsSection === "config" ? <ConfigEditorPanel epoch={owned ? status!.hostEpoch : null} project={settingsProject} onApplied={() => void refreshConfiguration()}
        onOpenFile={owned ? closeSettings : undefined} />
      : settingsSection === "logs" ? <>
        <ApplicationLogsPanel clearActions={logClearActions} read={demoMode
          ? async () => ({ status: "unavailable", rows: [], captureOmitted: "0", readOmitted: 0, captureId: null, boundary: "0", grant: "" }) : applicationLogs.read}
          levelsApi={demoMode ? undefined : applicationLogs} epoch={owned ? status!.hostEpoch : null} /></>
      : settingsSection === "providers" ? owned && status?.hostEpoch
        ? <ProviderSettings key={providerFocus ?? ""} epoch={status.hostEpoch} readRuntime={modelCatalog.providers} probe={modelCatalog.probe} initialProvider={providerFocus}
          onOpenModels={() => navigate("models")} onOpenConfiguration={() => navigate("config")}
          // A renamed provider keeps its spaces under its new key: the chips read them again.
          onApplied={() => { void refreshConfiguration(); void spacesHub.refresh(); }} spaceScope={resourceScope}
          onOpenFile={closeSettings} guide={providerGuide} onGuideClosed={() => setProviderGuide(false)}
          providerSessions={key => snapshot?.sessions.filter(session => session.providerKey?.toLowerCase() === key.toLowerCase()).length ?? 0}
          // The sign-in of an agent that signs in in a terminal: a terminal of the window, with the command typed in it.
          runInTerminal={command => void (async () => {
            const reply = await terminalWorkspace.hub.create(null, null, terminalLook.shellIntegration, terminalLook.shell).catch(() => null);
            if (reply?.status !== "ok" || !reply.terminal) { showToast({ intent: "danger", icon: "error", timeout: 8000, message: t("The terminal could not be started.") }); return; }
            closeSettings();
            showTerminal(reply.terminal);
            terminalWorkspace.hub.input(reply.terminal.id, command + "\r");
          })()} />
        : <ProvidersPanel epoch={null} read={modelCatalog.providers} probe={modelCatalog.probe} catalogProviders={configurationState.snapshot?.providers}
          holds={providerProbeHolds} onOpenModels={() => navigate("models")} />
      : <ModelCatalogPanel epoch={owned ? status!.hostEpoch : null}
        readProviders={modelCatalog.providers} readModels={modelCatalog.models} readChoices={sessionOperations.choices}
        providerSpaces={tierProviderSpaces}
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
        setSessionId(nextSession);
        navigate("workspace");
        focusPromptSoon();
        return true;
      }} onClose={() => setDialog(null)} />}
    {rowDrag.view && <div className="session-drag-ghost" role="presentation" data-over={rowDrag.view.over ? "true" : undefined}
      style={{ left: rowDrag.view.x + 14, top: rowDrag.view.y + 10 }}><AppIcon name="folderInput" size={13} /><span>{rowDrag.view.title}</span></div>}
    {movingToSpace && <MoveToSpaceDialog title={movingToSpace.title} spaces={spacesState.spaces} currentId={movingToSpace.spaceId} busy={movingBusy}
      onMove={space => void moveSessionToSpaceNow(movingToSpace, space)} onClose={() => setMovingToSpace(null)} />}
    {childrenView && <SessionChildrenDialog key={childrenView.id} title={childrenView.title} deleting={childrenView.deleting}
      read={() => readSessionChildren(workspace.sessionChildren, status?.hostEpoch, childrenView.id)}
      projects={catalog.current?.projects ?? []} spaces={spacesState.spaces} canDelete={owned && !!mutation?.capability.canMutate()}
      onOpen={session => { setChildrenView(null); revealSession(session); }} onDelete={deleteChildrenTree} onClose={() => setChildrenView(null)} />}
    {movingSession && snapshot && <MoveSessionDialog title={movingSession.title} projectId={movingSession.from.projectId}
      projects={snapshot.projects} busy={movingBusy} onMove={project => void moveSessionTo(movingSession, project)}
      onClose={() => setMovingSession(null)} />}
    {continuingClaudeCode && snapshot && <MoveSessionDialog title={continuingClaudeCode.title} projectId={null} continuing
      projects={snapshot.projects} busy={claudeCodeImporting !== null} onMove={project => void importClaudeCodeInto(continuingClaudeCode, project)}
      onClose={() => { if (!claudeCodeImportPending.current) setContinuingClaudeCode(null); }} />}
    {spaceDialog && <SpaceDialog spaces={spacesState.spaces} projects={catalog.current?.projects ?? []} create={spacesHub.create}
      // A space made from Settings is organized there; one made from the title bar is shown at once.
      onCreated={space => { setSpaceDialog(false); if (!settingsVisible.current) showSpace(space.id); }} onClose={() => setSpaceDialog(false)} />}
    {leavingSpace && <UnsavedFileDialog name={leavingSpace.tabs.flatMap(tab => fileEditors.unsaved(fileTabKey(tab))).join(", ")} mode="close" busy={leavingSpace.busy}
      onSave={() => void saveAndLeaveSpace(leavingSpace)} onDiscard={() => showSpace(leavingSpace.spaceId, leavingSpace.target, true)}
      onCancel={() => { cancelSaveFollowUp(); setLeavingSpace(null); }} />}
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
    <UpdateToaster notification={updateNotice} />
    {closeQuestion && <CloseWindowDialog platform={shellPreferences?.platform ?? "windows"} trayIcon={shellPreferences?.trayIcon} onKeepRunning={keepRunning} onExit={exitOnClose}
      onCancel={() => setCloseQuestion(false)} />}
    {exiting && <UnsavedExitDialog names={exiting.tabs.flatMap(tab => fileEditors.unsaved(fileTabKey(tab)))} busy={exiting.busy}
      onSave={() => void saveAllAndExit(exiting.tabs)} onDiscard={() => { setExiting(null); quitApplication(); }}
      onCancel={() => { cancelSaveFollowUp(); setExiting(null); exitCanceled(); }} />}
    {exitQuestionFor !== null && <RunningExitDialog runningSessions={exitQuestionFor.sessions} busyTerminals={exitQuestionFor.terminals} onCancel={() => { setExitQuestionFor(null); exitCanceled(); }}
      onExit={() => { setExitQuestionFor(null); quitApplication(true); }} />}
    {fileClosing && <UnsavedFileDialog name={fileEditors.unsaved(fileTabKey(fileClosing.tab)).join(", ")} mode="close" busy={fileClosing.busy}
      onSave={() => void saveAndCloseFile(fileClosing.tab)} onDiscard={() => closeFile(fileClosing.tab, true)}
      onCancel={() => { cancelSaveFollowUp(); setFileClosing(null); }} />}
    {dialog === "sessions" && browserCapture && <SessionBrowser snapshot={browserCapture.snapshot} projectId={browserCapture.projectId} observations={runtimeObservationControls()} recentCount={recentSessionCount} activeSessionId={sessionId} batch={batchDeleteControls()}
      archived={archivedSessionControls()}
      stale={browserCapture.revision !== browserRevision.current || browserCapture.hostReady !== (mutation?.capability.canMutate() ?? false) || view !== "workspace" || settingsOpen}
      close={() => setDialog(null)} open={tab => {
        if (view !== "workspace" || settingsVisible.current || browserCapture.hostReady !== (mutation?.capability.canMutate() ?? false)
          || !browserActivation(currentSnapshot.current, tab, browserCapture.revision, browserRevision.current)) return false;
        selectSessionTab(tab); setDialog(null); return true;
      }} />}
    {archivedInfo && catalog.current && (row => row && <SessionInfoDialog info={sessionInfoView(catalog.current!, row, row.projectId)}
      demo={demoMode} onClose={() => setArchivedInfo(null)} />)(catalog.current.sessions.find(session => session.id === archivedInfo))}
    {searchStart && <GlobalSearch snapshot={snapshot ?? null} favorites={projectTree.favorites} start={searchStart} files={searchedFiles()} note={notice}
      available={commandAvailable} unavailableReason={commandUnavailableReason} onCommand={id => chooseSearch({ kind: "command", id })}
      pluginCommands={pluginContributed.commands} onPluginCommand={id => chooseSearch({ kind: "plugin", id })}
      canvases={owned ? canvasCatalog : []} canvasAvailable={item => !!defaultCanvasTarget(item, canvasSelection)}
      onCanvas={item => chooseSearch({ kind: "canvas", pluginKey: item.pluginKey, id: item.id })}
      onProject={project => chooseSearch({ kind: "project", id: project.id })}
      onSession={session => chooseSearch({ kind: "session", projectId: session.projectId, id: session.id })}
      archived={owned && mutation?.capability.canMutate() ? shownArchived : undefined} archivedProjects={catalog.current?.projects}
      onRestore={owned ? session => { dismissSearch(); void restoreSessionNow(session, true); } : undefined}
      onFile={(project, path) => chooseSearch({ kind: "file", project, path })}
      skills={searchSkills?.key === skillsKey ? searchSkills.skills : undefined} onSkill={(name, args) => chooseSearch({ kind: "skill", name, args })}
      content={contentSearch} onContent={hit => chooseSearch({ kind: "content", hit })}
      onOfferMeaning={owned && meaningNow?.offer ? () => { dismissSearch(); setMeaningDialog(true); } : undefined}
      claudeCode={searchedClaudeCode} onClaudeCode={(conversation, providerId) => chooseSearch({ kind: "claude-code", conversation, providerId })}
      onClose={dismissSearch} />}
    {meaningDialog && <MeaningDialogHost offered={meaningNow?.offer === true} onClose={() => setMeaningDialog(false)} onTurnedOn={() => setFollowMeaning(true)}
      onKeepOut={() => { setMeaningDialog(false); navigate("search"); }} />}
    <PluginUiHost epoch={pluginEpoch}
      onPrompt={(request, signal) => {
        if (!["send", "enqueue", "steer", "compact"].includes(request.mode ?? "") || signal.aborted) return false;
        const command = askPluginComposer(request.mode as PluginComposerRequest["kind"], request.sessionId ?? null, request.text ?? null, null, signal);
        return command.completion ?? command.result;
      }}
      onDraft={request => { askPluginComposer("draft", request.sessionId ?? null, request.text ?? ""); }} />
  </div></ReviewsListOpenerContext.Provider></ReviewOpenerContext.Provider></ReviewsHubContext.Provider></LocalSearchContext.Provider></SessionListRefreshContext.Provider></ExternalOpenerContext.Provider></OpenRunContext.Provider></OpenEditorContext.Provider></OpenTerminalContext.Provider></ShowChangesContext.Provider></SessionWidthContext.Provider></MarkdownImagesContext.Provider></MessageLinksContext.Provider></SessionLinksContext.Provider></PullRequestSettingsContext.Provider></PluginButtonsActiveContext.Provider></PluginButtonsContext.Provider></PluginHostBridgeContext.Provider></PluginUiContext.Provider></ProviderBrandsContext.Provider></SpeechContext.Provider></HostReadsContext.Provider></ShellLanguageContext.Provider>;
}

// Native modal matches the other shell dialogs: showModal supplies inert background,
// browser-managed focus trapping and nested native About dialog top-layer ordering.
function SettingsOverlay({ section, onSection, onClose, spaces, spacePage, onSpacePage, pluginPages, pluginPage, onPluginPage, children }: {
  section: SettingsSection; onSection: (section: SettingsSection) => void;
  onClose: () => void;
  /** The spaces, each a page of the Spaces group; empty in a window whose host keeps none. */
  spaces: readonly Space[];
  spacePage: SpacePageTarget | null; onSpacePage: (page: SpacePageTarget | null) => void;
  /** The settings pages of the plugins that run in the space the window shows, each a page of the Extensions group. */
  pluginPages: readonly PluginSettingsPage[];
  pluginPage: PluginSettingsPage | null; onPluginPage: (page: PluginSettingsPage) => void;
  children: ReactNode;
}) {
  const { t } = useShellLanguage();
  const composingEscape = useRef(false);
  const navigation = useRef<HTMLElement>(null);
  const destinations: readonly [MessageKey, readonly [SettingsSection, MessageKey, IconName][]][] = [
    ["Personalization", [["appearance", "Appearance", "palette"], ["general", "General", "settings"], ["windows", "Windows", "window"], ["speech", "Speech", "mic"], ["search", "Search", "search"]]],
    ["Agent & models", [["providers", "Providers", "provider"], ["models", "Models", "model"], ["prompts", "Agent prompts", "assistant"], ["skills", "Skills", "skill"],
      ["permissions", "Permissions", "shield"], ["sandbox", "Sandbox", "sandbox"], ["codeMode", "Code Mode", "code"], ["sessions", "Sessions", "chat"],
      ["worktrees", "Worktrees", "worktree"], ["workItems", "Work items", "task"], ["pullRequests", "Pull requests", "pullRequest"]]],
    ["Extensions", [["plugins", "Plugins", "plugin"], ["mcp", "MCP Servers", "server"]]],
    ["Advanced", [["config", "Configuration file", "config"], ["storage", "Storage", "database"], ["mcpHost", "CodeAlta MCP", "remote"]]],
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
      <nav className="settings-dialog-navigation" aria-label={t("Settings pages")} ref={navigation}>
        {destinations.map(([group, pages], index) => <Fragment key={group}>
          <div className="settings-dialog-group">
            <h3>{t(group)}</h3>
            {pages.map(([value, label, icon]) => <button key={value} type="button" aria-current={section === value ? "page" : undefined}
              data-settings-section={value} onClick={() => onSection(value)}><AppIcon name={icon} size={16} /><span>{t(label)}</span></button>)}
            {/* Each plugin that has a settings page has its own, after the pages of the extensions, named and iconed as its canvas. */}
            {group === "Extensions" && pluginPages.map(page => <button key={`${page.pluginKey}/${page.canvasId}`} type="button"
              aria-current={section === "plugin" && samePluginSettingsPage(pluginPage, page) ? "page" : undefined} data-settings-plugin={`${page.pluginKey}/${page.canvasId}`}
              onClick={() => onPluginPage(page)}><CanvasIcon name={page.icon} pluginKey={page.pluginKey} size={16} /><span>{page.title}</span></button>)}
          </div>
          {/* Each space is a setup of its own: the group lists them after the overview, right after Personalization. */}
          {index === 0 && <div className="settings-dialog-group settings-dialog-spaces">
            <h3>{t("Spaces")}</h3>
            <button type="button" aria-current={section === "spaces" && !spacePage ? "page" : undefined} data-settings-section="spaces"
              onClick={() => { onSection("spaces"); onSpacePage(null); }}><AppIcon name="canvases" size={16} /><span>{t("Overview")}</span></button>
            <button type="button" aria-current={section === "labels" ? "page" : undefined} data-settings-section="labels"
              onClick={() => onSection("labels")}><AppIcon name="tag" size={16} /><span>{t("Labels")}</span></button>
            {spaces.map(space => <button key={space.id} type="button" aria-current={section === "spaces" && spacePage?.spaceId === space.id ? "page" : undefined}
              data-settings-space={space.id} onClick={() => { onSection("spaces"); onSpacePage({ spaceId: space.id, section: spacePage?.section ?? "general" }); }}>
              <SpaceIcon space={space} size={16} /><span>{space.name}</span></button>)}
          </div>}
        </Fragment>)}
        <OverflowTip within={navigation} target=".settings-dialog-navigation button" label=":scope > span:last-child" />
      </nav>
      <div className="settings-dialog-content" key={section}>{children}</div>
    </div>
  </AppWindow>;
}

type ReferenceScope = NonNullable<ContextType<typeof ProjectReferenceContext>>;
function createSessionPaneOwners(reads: HostReads) {
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
    permissionReviewer: createPermissionReviewer(reads.permissions, reads.resolvePermission),
    heldReviewer: createConversationHeldReviewer(conversationHeld.list, conversationHeld.resolve),
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
const ShowChangesContext = createContext<((project: Readonly<{ id: string; path: string }>, worktree?: string | null, review?: "changes" | "branch" | null) => void) | null>(null);

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
/** Opens the code editor on the folder a session works in: its worktree (the root git lists) or the project; null where the window has no host. */
const OpenEditorContext = createContext<((project: Readonly<{ id: string; path: string }>, worktreeRoot: string | null) => void) | null>(null);
/** Opens the settings of the instructions for a pull request; null where the window has none. */
const PullRequestSettingsContext = createContext<(() => void) | null>(null);

function useComposerChrome(epoch: string | null, project: WorkspaceSnapshot["projects"][number] | undefined, sessionId: string | null = null,
  /** The way to the changes tab for a composer the application builds itself, above the context. */
  show: ((project: Readonly<{ id: string; path: string }>, worktree?: string | null) => void) | null = null,
  /** The git worktree the session works in, as its row records it. */
  session: WorkspaceSession | null = null,
  /** Before a session exists: where it will work. */
  place?: Parameters<typeof ProjectContext>[0]["place"],
  /** Whether the pane of the composer is on screen: a hidden one reads nothing. */
  observing = true,
  /** A run of the session is active: the changes of its checkout are not waiting for a review yet. */
  running = false): ComposerChromeValue {
  const id = project?.id, name = project?.name, path = project?.path, archived = project?.archived;
  // The regions are read only when a plugin has content for them.
  const regions = useContext(PluginUiContext).contributions.regions;
  // One read for both places of the composer.
  const regionItems = usePluginRegions(regions ? epoch : null, project?.id ?? null, sessionId, pluginUi.regions, observing);
  const showChanges = useContext(ShowChangesContext) ?? show;
  const openTerminal = useContext(OpenTerminalContext);
  const openEditor = useContext(OpenEditorContext);
  const refreshSessions = useContext(SessionListRefreshContext);
  const openPullRequestSettings = useContext(PullRequestSettingsContext);
  const worktreePath = session?.worktreePath ?? null, worktreeName = session?.worktreeName ?? null, worktreeMissing = session?.worktreeMissing === true;
  const worktreeRoot = session?.worktreeRoot ?? null;
  return useMemo(() => {
    const worktree = sessionWorktree({ worktreePath, worktreeName, worktreeRoot, worktreeMissing });
    // The changes of where the session works: its worktree while the folder is there.
    const shown = worktree && !worktree.missing ? worktree.path : null;
    return ({ context: id !== undefined && name !== undefined && path !== undefined
    ? <WorkspaceChip epoch={epoch} project={{ id, name, path }} sessionId={sessionId} read={projectGit.status} worktree={worktree} place={place}
      onWorktreeGone={refreshSessions ?? undefined} branches={!!showChanges && !archived}
      onShowChanges={showChanges && !archived ? () => showChanges({ id, path }, shown) : undefined}
      onOpenTerminal={openTerminal && !archived ? () => openTerminal(id, sessionId) : undefined}
      // The code editor where the session works: the worktree while its folder is there.
      onOpenEditor={openEditor && !archived ? () => openEditor({ id, path }, worktreeRoot && !worktreeMissing && sessionId ? worktreeRoot : null) : undefined}
      // A session that exists can be asked for a pull request of its work; a draft has no work yet.
      pullRequest={epoch && sessionId && !place && !archived ? <PullRequestButton labelled api={pullRequestPrompts} epoch={epoch} projectId={id} sessionId={sessionId}
        onOpenSettings={openPullRequestSettings ?? undefined} onNotice={message => showToast({ message, intent: "warning", icon: "warning-sign", timeout: 6000 })} /> : undefined} /> : undefined,
  status: epoch ? <>{regions && <PluginRegionSlot items={regionItems} projectId={id ?? null} sessionId={sessionId} region="inline" />}
    {sessionId && !archived && <ReviewComposerStatus epoch={epoch} projectId={id ?? null} checkout={shown} sessionId={sessionId} running={running} />}
    <ComposerStatus epoch={epoch} projectId={id ?? null} sessionId={sessionId} read={composerStatus.read} /></> : undefined,
  footer: epoch && regions ? <PluginRegionSlot items={regionItems} projectId={id ?? null} sessionId={sessionId} region="footer" /> : undefined });
  }, [epoch, id, name, path, archived, sessionId, regions, regionItems, showChanges, openTerminal, openEditor, refreshSessions, openPullRequestSettings, worktreePath, worktreeName, worktreeRoot, worktreeMissing, place, running]);
}

// The composer of a session, as a prompt given back to it sees it: the one shown, else the draft stored for the session.
function promptBackComposer(sessionId: string): PromptBackComposer {
  const shown = askPluginComposer("state", sessionId).state;
  return {
    draftText: shown?.draftText ?? restoreDraft(key => localStorage.getItem(key), sessionId),
    draft: shown ? text => askPluginComposer("draft", sessionId, text).result : null,
    persist: text => persistDraft((key, value) => localStorage.setItem(key, value), key => localStorage.removeItem(key), sessionId, text),
  };
}

// The run of a session whose prompt was last given back to its composer: a prompt is given back once, whatever reads the window again.
const unsentPromptKey = (sessionId: string) => `codealta.desktop.unsentPrompt.${sessionId}`;

function SessionWorkspace({ canAdmit, remoteControlRequest = null, session, snapshot, selectedProjectId, preferredComposerHeight, onComposerHeight, infoTrigger: sharedInfoTrigger, infoLifetime, remindersTrigger: sharedRemindersTrigger, compactTrigger: sharedCompactTrigger, onOpenReminders, onOpenHelp, onOpenCommands, onOpenDocumentation, readReminders, reminderActions, status, mutation, submissions, timelineImages, toolRecords, steering, compaction, abortRuns, queue, draftIndicators, askActions, display, scrollMemory, runtimeReader, permissionReviewer, heldReviewer, inputReviewer, configuration: configurationSnapshot, selections, timelineCommand, onOpenCatalog, active = true, observing = true, notesHidden = false, onActivate, notesReader, activeReminderCount = null, autoSend = null, onRunActivity, origin, workCards, rewinds = 0, branch = null, bookmarks = null, lineage, waitingSubSessions, subSessions, archive = null, sessionSpaces = null, heldElsewhere = null }: {
  canAdmit: () => boolean;
  /**
   * The process that holds the session when another one does (the command line, another instance), with the length of
   * its journal: the prompt is read-only and says why, and the timeline is read again as the journal grows.
   */
  heldElsewhere?: Readonly<{ kind: ElsewhereKind; journalLength: string | null }> | null;
  /** The spaces, and what reads the space of a session and what it can use there; null where the host keeps no spaces. */
  sessionSpaces?: Readonly<{ spaces: readonly Space[]; read: (sessionId: string) => Promise<SpaceSessionResourcesResponse | null> }> | null;
  /** The number of times the session was rewound: what was read of its journal before a rewind is not what it holds now. */
  rewinds?: number;
  /** The fork and the rewind of this session; null where it cannot be changed. */
  branch?: SessionBranchHost | null;
  /** The bookmarks of the messages of this session; null where the host keeps none. */
  bookmarks?: SessionBookmarkHost | null;
  /** Where the session comes from when it is a fork, shown above its timeline. */
  lineage?: ReactNode;
  /** The sessions under this one that wait for the user. */
  waitingSubSessions?: readonly WaitingSubSession[];
  /** The sessions under this one at work: shown below its prompt. */
  subSessions?: readonly SubSessionAtWork[];
  /**
   * Archives the session, as its menu in the Explorer does; `asks` when it has child sessions, and `question` is that
   * question while it is asked from the composer. Null where it cannot be archived.
   */
  archive?: Readonly<{ asks: boolean; run: () => void; question?: ReactNode }> | null;
  /** Reports whether the session is working while its panel watches it, and how many tasks go on in its background. */
  onRunActivity?: (running: boolean | null, background?: number) => void;
  /** The request of the Actions menu of the session to show its Remote Control, until it is shown. */
  remoteControlRequest?: RemoteControlOpenRequest | null;
  /** A draft prompt to send once this session's composer holds it. */
  autoSend?: { text: string; images?: number; consume: () => void } | null;
  /** What started the session when it was not the user, shown above its timeline. */
  origin?: ReactNode;
  /** The work items the session proposes, shown over the top right corner of its timeline. */
  workCards?: ReactNode;
  /** Active reminders of this session as last reported by the host; null while unknown. */
  activeReminderCount?: number | null;
  notesReader: ReturnType<typeof createNotesReader>;
  observing?: boolean;
  active?: boolean; notesHidden?: boolean; onActivate?: () => void;
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
  onOpenDocumentation?: (page: string, anchor: string | null) => void;
  readReminders: (request: ReminderListRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ReminderListResponse>;
  reminderActions: ReturnType<typeof createReminderActions>;
  compactTrigger: RefObject<HTMLButtonElement | null>;
  status: BootStatus | undefined;
  mutation: { epoch: string; capability: ReturnType<typeof createMutationCapability> } | undefined;
  submissions: ReturnType<typeof createOwnedSubmissions>;
  timelineImages: ReturnType<typeof createTimelineImageCache>;
  toolRecords: { records: ReturnType<typeof createToolCallCache>; outputs: ReturnType<typeof createToolOutputStore>;
    prompts: ReturnType<typeof createSystemPromptCache> };
  steering: ReturnType<typeof createSteeringSubmissions>;
  compaction: ReturnType<typeof createCompactionSubmissions>;
  abortRuns: ReturnType<typeof createAbortRunSubmissions>;
  queue: ReturnType<typeof createComposerQueue>;
  draftIndicators: ReturnType<typeof createDraftIndicators>;
  askActions: ReturnType<typeof createAskActions>;
  display: ReturnType<typeof createSessionDisplayStore>;
  scrollMemory: ReturnType<typeof createTimelineScrollMemory>;
  runtimeReader: ReturnType<typeof createRuntimeStateReader>;
  permissionReviewer: ReturnType<typeof createPermissionReviewer>;
  heldReviewer: ReturnType<typeof createConversationHeldReviewer>;
  inputReviewer: ReturnType<typeof createUserInputReviewer>;
  configuration: ConfigurationSnapshot | undefined;
  selections: ReturnType<typeof createNextSendSelectionStore>;
  timelineCommand: RefObject<TimelineCommand | null>;
}) {
  const { t, locale: languageLocale } = useShellLanguage();
  // A review of changes an agent asks for is done in the Changes tab of its checkout.
  const showReviewChanges = useContext(ShowChangesContext);
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
  // How many tasks still run in the background of the session: a rewind waits for them.
  const [backgroundRunning, setBackgroundRunning] = useState(0);
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
  const timeline = useTimelinePosition(session.id, scrollMemory, rewinds);
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
            : { key: result.older ? "Loading previous messages…" : "First retained message in this window." }
          : { key: action === "messageFirst" ? "First retained message (not necessarily the first journal message): {label}" : "Retained message: {label}", parameters: result.label === undefined ? undefined : { label: result.label } });
      },
      latestReady: newest.available, latest: newest.latest, cancelLatest: newest.cancel,
      showRow: key => { newest.cancel(); return timeline.showRow(key); }, stepBookmark: direction => bookmarkStep.current?.(direction) ?? false };
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
  // A session another process holds is read again as its journal grows, as a session of this window is with its run.
  // Once more when it is released: the last records of the other process come with the release.
  const elsewhereJournal = heldElsewhere ? heldElsewhere.journalLength ?? "" : null;
  const [elsewhereShown, setElsewhereShown] = useState<SessionDisplayView | null>(null);
  const elsewhereView = elsewhereFollowView(session.id, elsewhereJournal, elsewhereShown);
  if (elsewhereView !== elsewhereShown) setElsewhereShown(elsewhereView);
  // The prompt of a run that waited for the conversation to be released elsewhere, and found turns added there meanwhile,
  // was never sent: it goes back to the composer when it is empty, as after a rewind; a draft that is there is kept, and
  // the prompt is offered beside it to copy. Once per run.
  const giveBackUnsent = useRef((_: UnsentPrompt | null) => { });
  giveBackUnsent.current = unsent => {
    if (!unsent || !ownedSession) return;
    try {
      if (localStorage.getItem(unsentPromptKey(session.id)) === unsent.runId) return;
      localStorage.setItem(unsentPromptKey(session.id), unsent.runId);
    } catch { return; }
    const prompt = composerPrompt(unsent.text);
    if (!prompt) return;
    if (givePromptBack(prompt, promptBackComposer(session.id), false) === "drafted")
      showToast({ intent: "primary", icon: "info-sign", timeout: 8000, message: t("The prompt that was not sent is back in the composer.") });
    else showToast({ intent: "primary", icon: "info-sign", timeout: 30_000, message: t("The composer holds a draft: the prompt that was not sent was not put in it."),
      action: { text: t("Copy the prompt"), onClick: () => void navigator.clipboard.writeText(prompt).catch(() => { }) } });
  };
  const onUnsentPrompt = useCallback((unsent: UnsentPrompt | null) => giveBackUnsent.current(unsent), []);
  // A prompt sent from here is shown at once: the timeline goes to its end, wherever the reader was.
  const outgoingCount = ownedSession && status?.hostEpoch ? submissions.outgoing(status.hostEpoch, session.id).length : 0;
  const shownOutgoing = useRef(outgoingCount);
  useLayoutEffect(() => {
    if (outgoingCount > shownOutgoing.current && !newerOmitted) timeline.jump();
    shownOutgoing.current = outgoingCount;
  }, [outgoingCount]);
  const pluginEpoch = ownedHost ? status!.hostEpoch! : null;
  const sessionProject = snapshot.projects.find(project => project.id === selectedProjectId);
  const sharedChrome = useComposerChrome(pluginEpoch, sessionProject, session.id, null, session, undefined, observing, running === true);
  // A session under the one of the pane, waiting for a permission, has its own reviewer there: the pane's reviews its own.
  const hostReads = useContext(HostReadsContext);
  const createSubSessionReviewer = useMemo(() => () => hostReads ? createPermissionReviewer(hostReads.permissions, hostReads.resolvePermission)
    : createPermissionReviewer(sessionPermissions.list, sessionPermissions.resolve), [hostReads]);
  const chrome = useMemo(() => subSessions?.length ? { ...sharedChrome, subAgents: <SubAgentsChip sessions={subSessions} /> } : sharedChrome, [sharedChrome, subSessions]);
  const readPluginEvents = useMemo(() => pluginEpoch === null ? undefined
    : createPluginEventsRead(sessionPluginEvents.read, { epoch: pluginEpoch, sessionId: session.id, projectId: selectedProjectId }),
  [pluginEpoch, session.id, selectedProjectId]);
  // A command of a card of this timeline runs for this session, whatever pane has the keyboard.
  const timelinePane = useMemo(() => ({ sessionId: session.id, projectId: selectedProjectId }), [session.id, selectedProjectId]);
  // Reading is not sending: the images of an archived project's session are shown too.
  const readImages = useMemo(() => pluginEpoch === null ? undefined : timelineImages.reader(pluginEpoch, session.id, rewinds),
    [timelineImages, pluginEpoch, session.id, rewinds]);
  const readTool = useMemo(() => pluginEpoch === null ? undefined : toolRecords.records.reader(pluginEpoch, session.id, rewinds),
    [toolRecords, pluginEpoch, session.id, rewinds]);
  const readPrompt = useMemo(() => pluginEpoch === null ? undefined : toolRecords.prompts.reader(pluginEpoch, session.id, rewinds),
    [toolRecords, pluginEpoch, session.id, rewinds]);
  // The whole text of a message the history page shows the start of, read when its card asks for it.
  const readMessage = useMemo(() => pluginEpoch === null ? null : createMessageReader(toolCalls.readMessage, pluginEpoch, session.id),
    [pluginEpoch, session.id]);
  // The window of a fork or a rewind of this session: a command, a prompt of the timeline or the menu of its row
  // asks for it, and the pane that has the session shows it.
  const branchRequest = useSyncExternalStore(branch ? branch.requests.subscribe : noSubscription, () => branch?.requests.forSession(session.id) ?? null);
  const branchEpoch = branch && ownedSession && !demoMode ? status!.hostEpoch! : null;
  const branchDialogHost = useMemo<BranchDialogHost | null>(() => branch && branchEpoch ? {
    readTurns: (beforeTurn, signal) => readSessionTurns(workspaceApi.sessionTurns, branchEpoch, session.id, beforeTurn, signal),
    fork: beforeRunId => branch.fork(session, selectedProjectId, beforeRunId),
    rewind: (beforeRunId, keepRemovedTurns) => branch.rewind(session, selectedProjectId, beforeRunId, keepRemovedTurns),
  } : null, [branch, branchEpoch, session, selectedProjectId]);
  // A request for a session that cannot be changed here is dropped.
  useEffect(() => { if (branchRequest && !branchDialogHost) branch?.requests.close(branchRequest); }, [branch, branchRequest, branchDialogHost]);
  const openBranch = useCallback((key: string, mode: BranchMode) => branch?.requests.open(session.id, mode, key), [branch, session.id]);
  // The window scrolls the conversation to the turn it has selected; closed without a change, it leaves the reader where they were.
  const showBranchPoint = (key: string | null) => { if (key) { newest.cancel(); timeline.showRow(key); } };
  // Whether the composer holds a draft when the window opens: a rewind keeps it, and the window says so.
  const [branchDraft, setBranchDraft] = useState(false);
  useLayoutEffect(() => {
    if (!branchRequest) return;
    setBranchDraft(!!askPluginComposer("state", session.id).state?.draftText.trim());
    const before = timeline.position();
    return () => timeline.restorePosition(before);
  }, [branchRequest]);
  // The bookmarks of the messages of this timeline: its rows follow their own mark, and its rail lists them.
  // A host that keeps no bookmarks offers none: the rows have no button for them.
  const bookmarksKept = useSyncExternalStore(bookmarks ? bookmarks.hub.subscribe : noSubscription, () => bookmarks?.hub.getSnapshot().available ?? false);
  const bookmarkMarks = useMemo(() => bookmarks && bookmarksKept ? createTimelineBookmarks((rowKey, bookmarkId) => bookmarks.toggleMessage(session.id, rowKey, bookmarkId)) : null,
    [bookmarks, bookmarksKept, session.id]);
  const bookmarkStep = useRef<((direction: -1 | 1) => boolean) | null>(null);
  // A message of this session to show: the history reads up to it, then the timeline goes there and points it out.
  const revealRequest = useSyncExternalStore(bookmarks ? bookmarks.reveals.subscribe : noSubscription, () => bookmarks?.reveals.forSession(session.id) ?? null);
  const onReveal = useCallback((request: RevealRequest, outcome: RevealOutcome) => {
    bookmarks?.reveals.done(request);
    if (outcome === "shown") { newest.cancel(); if (timeline.showRow(request.offset, true)) return; outcome = "missing"; }
    bookmarks?.unshown(outcome);
  }, [bookmarks, newest.cancel, timeline.showRow]);
  const toolOutputs = useMemo(() => pluginEpoch === null ? undefined : toolRecords.outputs.session(pluginEpoch, session.id),
    [toolRecords, pluginEpoch, session.id]);
  const ownWidth = useSessionWidthStyle(session.id);
  const linkScope = useMemo<MarkdownLinkScope>(() => ({ sessionId: session.id }), [session.id]);
  // The views of the MCP tool calls of the session (MCP Apps): their requests go to its server through the host, and a
  // message of a view is sent as the composer of the session sends a prompt, queued while a run is busy.
  const openMessageLink = useContext(MessageLinksContext);
  const appsEpoch = ownedSession && !demoMode ? status?.hostEpoch ?? null : null;
  const appsVersion = aboutFacts(status, false, demoMode).version ?? "desktop";
  const mcpAppsHost = useMemo(() => appsEpoch ? createMcpAppsHost(mcpAppsApi, appsEpoch, session.id,
    async text => (await askPluginComposer("send", session.id, text).completion) === true,
    url => { if (!openMessageLink) return false; openMessageLink(url, { sessionId: session.id }); return true; }, appsVersion) : null,
  [appsEpoch, session.id, openMessageLink, appsVersion]);
  // Who else holds the Claude Code conversation of the session (VS Code, a terminal): said by a banner in the place of
  // the text of the prompt until the user collapses it, then on a line above the prompt.
  const holderEpoch = ownedSession && !demoMode && session.providerConversation === true ? status?.hostEpoch ?? null : null;
  const readHolder = useMemo(() => holderEpoch
    ? async () => (await readClaudeCodeConversation(claudeCodeOpenHostApi, holderEpoch, session.id))?.held ?? null : null, [holderEpoch, session.id]);
  const elsewhere = useConversationElsewhere(readHolder, active && observing, running === true || askMode !== "none");
  const sessionChrome = useMemo(() => elsewhere.covers ? { ...chrome, promptCover: <ConversationElsewhereBanner elsewhere={elsewhere} /> } : chrome,
    // The banner reads its holder: a new holder makes a new one.
    [chrome, elsewhere.covers, elsewhere.held]);
  const infoControl = <Button ref={infoTrigger} variant="minimal" className="session-info-trigger" icon={<AppIcon name="info" size={16} />}
    aria-label={t("Session info")} title={`${t("Session info")} (Ctrl+G, Ctrl+T)`} aria-haspopup="dialog" aria-expanded={infoOpen}
    onClick={openInfo}>{t("Session info")}</Button>;
  // A relative link of a message, of the notes or of a tool window of this session starts from the folder it works in.
  // The error of a Claude Code provider without its CLI proposes the download in an owned host only.
  const cliEpoch = ownedSession && !demoMode ? status?.hostEpoch ?? null : null;
  const cliHost = useMemo(() => cliEpoch ? { epoch: cliEpoch, composerProvider: session.providerKey ?? null, Card: ClaudeCliFirstUse } : null, [cliEpoch, session.providerKey]);
  // Save as script…, Resume and the reruns on the card of a run that ended ask this session, in an owned host only.
  const scriptActions = useMemo(() => cliEpoch ? {
    saveAsScript: async (runId: string, name: string) => {
      const outcome = await createWorkflowsHub(workflowsApi, cliEpoch).saveAsScript(session.id, runId, name);
      return outcome.ok ? null : outcome.message ?? "";
    },
    resume: async (runId: string, how: Readonly<{ from?: string; all?: boolean }>) => {
      const outcome = await createWorkflowsHub(workflowsApi, cliEpoch).resume(session.id, runId, how);
      return outcome.ok ? null : outcome.message ?? "";
    },
    stop: async (runId: string) => {
      const outcome = await createWorkflowsHub(workflowsApi, cliEpoch).stop(runId);
      return outcome.ok ? null : outcome.message ?? "";
    },
  } : null, [cliEpoch, session.id]);
  // The resume after a limit the session waits for: its warning card offers Resume now and Cancel, in an owned host only.
  // It is read when a card shows it, and again when the run of the session starts or ends.
  const resumeHub = useMemo(() => cliEpoch ? createResumeHub(sessionOperations, cliEpoch, session.id) : null, [cliEpoch, session.id]);
  useEffect(() => { resumeHub?.noteRunning(running === true); }, [resumeHub, running]);
  return <MarkdownLinkScopeContext.Provider value={linkScope}><SessionResumeContext.Provider value={resumeHub}><ScriptRunActionsContext.Provider value={scriptActions}><ClaudeCliEpochContext.Provider value={cliHost}><BackgroundCallsContext.Provider value={backgroundCallStates}><PaneObservingContext.Provider value={observing}><MessageReaderContext.Provider value={readMessage}><div className="session-workspace" inert={!canAdmit()} data-active={active} style={ownWidth} ref={composer.workspaceRef} onFocusCapture={onActivate} onPointerDownCapture={onActivate}>
    {infoOpen && <SessionInfoDialog info={sessionInfoView(snapshot, session, selectedProjectId)} demo={demoMode} onClose={closeInfo}
      // The conversation of the Claude Code CLI the session continues: read when the window opens; another provider has none.
      conversation={ownedSession && !demoMode && session.providerConversation === true
        ? () => readClaudeCodeConversation(claudeCodeOpenHostApi, status?.hostEpoch, session.id) : null}
      lifetime={infoLifetime} canRead={() => !!mutation?.capability.canMutate()}
      spaceResources={sessionSpaces && !demoMode ? { spaces: sessionSpaces.spaces, read: () => sessionSpaces.read(session.id) } : null}
      target={ownedSession && !demoMode && mutation?.capability.canMutate() ? runtimeTarget(snapshot, { sessionId: session.id, projectId: selectedProjectId, path: session.workspacePath }, status?.hostEpoch ?? undefined) : null} />}
    {branchRequest && branchDialogHost && <SessionBranchDialog key={`${branchRequest.mode}:${branchRequest.promptOffset ?? ""}`} title={plainTitle(session.title)} request={branchRequest} host={branchDialogHost}
      facts={{ canMutate: !!mutation?.capability.canMutate(), running: running === true, question: askMode !== "none", background: backgroundRunning, draft: branchDraft }}
      keepRemovedTurns={readKeepRemovedTurns(key => localStorage.getItem(key))} onKeepRemovedTurns={keep => saveKeepRemovedTurns((key, value) => localStorage.setItem(key, value), keep)}
      onPoint={showBranchPoint} onClose={() => branch?.requests.close(branchRequest)} />}
    {demoMode
      ? <DemoConversation session={session} />
      : <>
        {origin}
        {lineage}
        <div className="session-timeline-area" data-ask={askMode}>
        <div className="ask-file-slot" ref={setAskFileSlot} hidden={askMode !== "file"} />
        {/* A press in the timeline gives it the keyboard, so that the keys scroll it: the panel of the tab around
            it would take the press otherwise, and the keys scroll from what has the keyboard upwards. */}
        <div className="timeline-scroll" tabIndex={-1} ref={timeline.elementRef}
          onScroll={event => { newest.onScroll(); if (!newest.pending()) timeline.scroll(event.currentTarget); }}
          onWheel={event => { newest.cancel(); timeline.wheel(event); }} onKeyDown={timeline.keyDown}
          onPointerDown={event => { newest.cancel(); timeline.pointerDown(event); }}
          onPointerMove={timeline.pointerMove} onPointerUp={timeline.pointerEnd} onPointerCancel={timeline.pointerEnd}>
        <PluginPaneContext.Provider value={timelinePane}><TimelineBookmarksContext.Provider value={bookmarkMarks}><McpAppsContext.Provider value={mcpAppsHost}><History observing={observing} reveal={revealRequest} onReveal={onReveal} sessionId={session.id} canInspect={() => infoLifetime.current()} onNotesChange={onNotesChange} onUsageChange={setPersistedUsage} onUnsentPrompt={onUnsentPrompt} onSettled={onHistorySettled}
          onBeforeOlder={timeline.beforeOlderPage} onAfterOlder={timeline.afterOlderPage} onNewerOmitted={setNewerOmitted}
          onNavigationReset={resetMessageNotice} newestRequest={newest.requestRef} olderRequest={timeline.olderRequestRef} onNewestResult={newest.onResult}
          read={readTimeline} readPluginEvents={readPluginEvents} readImages={readImages} readTool={readTool} readPrompt={readPrompt} toolOutputs={toolOutputs}
          outgoing={ownedSession && status?.hostEpoch ? submissions.outgoing(status.hostEpoch, session.id) : noOutgoing}
          onAcknowledgeOutgoing={submissions.acknowledgeOutgoing} onBranch={branchDialogHost ? openBranch : undefined}
          live={(heldElsewhere ? elsewhereView : null) ?? (ownedSession ? live?.snapshot?.session ?? null : null) ?? elsewhereView} running={running ?? undefined} /></McpAppsContext.Provider></TimelineBookmarksContext.Provider></PluginPaneContext.Provider>
        {ownedSession && status?.hostEpoch
        ? <>
          <LiveSessionPanel observing={observing} store={display} hostEpoch={status.hostEpoch} sessionId={session.id} capability={mutation!.capability} />
          <div className="timeline-notices" ref={setTimelineNotices} />
          {status.ownedAsksEnabled && <AskPanel observing={observing} epoch={status.hostEpoch} sessionId={session.id} actions={askActions} capability={mutation!.capability} refreshTrigger={askRefresh}
            projectId={selectedProjectId} idle={running !== true} formSlot={askFormSlot} fileSlot={askFileSlot} onMode={setAskMode}
            onOpenChanges={showReviewChanges && ((id, checkout, kind) => {
              const project = snapshot?.projects.find(candidate => candidate.id === id);
              if (project) showReviewChanges({ id: project.id, path: project.path }, checkout, kind);
            })} />}
          {status.ownedUserInputEnabled && <UserInputPanel epoch={status.hostEpoch} sessionId={session.id} reviewer={inputReviewer} capability={mutation!.capability}
            canReview={() => infoLifetime.current()} />}
        </>
        : null}
        </div>
        <SessionNotesOverlay observing={observing} sessionId={session.id} epoch={ownedSession ? status?.hostEpoch : undefined} capability={mutation?.capability}
          fallbackMarkdown={historyNotes} hidden={notesHidden} reader={notesReader} />
        {workCards}
        {bookmarks && bookmarkMarks && <BookmarkRail sessionId={session.id} hub={bookmarks.hub} marks={bookmarkMarks} scroll={timeline.elementRef} onGo={bookmarks.go} step={bookmarkStep} />}
        {!timeline.following && <button type="button" className="timeline-bottom-button" onClick={() => { newest.cancel(); timeline.jump(); }}><AppIcon name="arrowDown" size={14} />{t(newerOmitted ? "Bottom of retained window (not newest)" : "Jump to latest visible")}</button>}
        </div>
        {/* Message navigation is announced to assistive technology only: nothing is written above the prompt. */}
        {messageNotice && <p role="status" className="sr-only">{timelineNotice(languageLocale, messageNotice)}</p>}
        {/* Another Claude process that holds the conversation of the session, once the banner over the prompt is collapsed. */}
        <ConversationElsewhereLine elsewhere={elsewhere} shown={!elsewhere.covers} />
        <div className="composer-resize-bar" ref={composer.barRef} hidden={askMode !== "none"}>
          <ComposerSplitter {...composer.splitter} />
        </div>
        <div ref={composer.regionRef} className={`composer-region${composer.height === undefined || askMode !== "none" ? "" : " resized"}`} data-ask={askMode}
          style={askMode !== "none" ? undefined : composer.regionStyle}>
        <SessionWidthGrips sessionId={session.id} />
        <div className="ask-form-slot" ref={setAskFormSlot} hidden={askMode === "none"} />
        <SessionComposerGate snapshot={snapshot} projectId={selectedProjectId} session={session} chrome={sessionChrome}
          epoch={ownedHost && !heldElsewhere ? status!.hostEpoch! : null}
          owned={status?.hostEpoch && mutation ? <OwnedSessionPanel canAdmit={canAdmit} observing={observing} active={active} onRunActivity={(value, background) => {
            setRunning(value);
            const calls = backgroundCalls(background ?? []);
            setBackgroundCallStates(current => current.size === calls.size && [...calls].every(([call, state]) => current.get(call) === state) ? current : calls);
            const stillRunning = runningBackgroundTasks(background ?? []).length;
            setBackgroundRunning(stillRunning);
            onRunActivity?.(value, stillRunning);
          }} toolOutputs={toolOutputs} sessionId={session.id} sessionFolder={session.workspacePath ?? null} remoteControlRequest={remoteControlRequest} epoch={status.hostEpoch} submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} capability={mutation.capability} runtimeReader={runtimeReader} permissionReviewer={status.commandReviewEnabled ? permissionReviewer : null} heldReviewer={heldReviewer}
              waitingSubSessions={waitingSubSessions} createSubSessionReviewer={status.commandReviewEnabled ? createSubSessionReviewer : null} configuration={configurationSnapshot} draftIndicators={draftIndicators} selections={selections}
              persistedUsage={persistedUsage} usageTarget={ownedSession && verifiedReminderCountTarget(snapshot, session, selectedProjectId) ? {
                epoch: status.hostEpoch, sessionId: session.id, createdAt: session.createdAt, scope: selectedProjectId === null ? "global" : "project",
                projectId: selectedProjectId, expectedProjectPath: selectedProjectId === null ? null : session.workspacePath } : null}
              onOpenCatalog={onOpenCatalog} onOpenDocumentation={onOpenDocumentation} timelineNotices={timelineNotices} liveState={ownedSession ? live : null} inputLifetime={infoLifetime} remindersTrigger={remindersTrigger} compactTrigger={compactTrigger} infoControl={infoControl} projectId={selectedProjectId} onOpenReminders={() => onOpenReminders(session.id, status.hostEpoch!, selectedProjectId)} onOpenHelp={onOpenHelp} onOpenCommands={onOpenCommands}
              activeReminderCount={activeReminderCount} autoSend={autoSend} archive={ownedSession ? archive : null} reminderActions={reminderActions} readReminderCount={ownedSession && verifiedReminderCountTarget(snapshot, session, selectedProjectId) ? readReminders : undefined} /> : null}
          readOnly={<ReadOnlyComposer active={active} sessionId={session.id} provider={session.providerKey} draftIndicators={draftIndicators} infoControl={infoControl} onOpenHelp={onOpenHelp} onOpenCommands={onOpenCommands}
              reason={archivedScope ? t("Archived project; this session is read-only. Sending is unavailable.")
                : heldElsewhere ? t(elsewhereLabel(heldElsewhere.kind)) : undefined} />}
          recovery={ownedHost ? <ArchivedActionRecovery epoch={status!.hostEpoch!} sessionId={session.id} submissions={submissions}
            steering={steering} compaction={compaction} abortRuns={abortRuns}
            asks={askActions} inputs={inputReviewer} permissions={permissionReviewer} /> : null} />
        </div>
      </>}
  </div></MessageReaderContext.Provider></PaneObservingContext.Provider></BackgroundCallsContext.Provider></ClaudeCliEpochContext.Provider></ScriptRunActionsContext.Provider></SessionResumeContext.Provider></MarkdownLinkScopeContext.Provider>;
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

function ConfigurationPanel({ preferences }: { preferences: Parameters<typeof AppearanceSettings>[0] }) {
  const { t } = useShellLanguage();
  return <div className="configuration-page settings-card-page">
    <header className="page-heading"><span className="eyebrow">{t("Personalization")}</span><h1>{t("Appearance")}</h1></header>
    <div className="settings-grid"><AppearanceSettings {...preferences} /></div>
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
    if (startSplitterDrag(event)) lastX.current = event.clientX;
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
installContextClickGuard(window);
installParagraphSelection(document);
installFontPreferences(window);
// The host says first what this window is for: the workspace, or the repair of a configuration file that
// cannot be loaded. The start-up screen stays until one of them has something to show.
function Root() {
  const [purpose, setPurpose] = useState<{ recovery: boolean; developer: boolean; window: WindowsCurrentResponse | null }>();
  useEffect(() => {
    const abort = new AbortController();
    // And which window the page is: the main one, or a secondary one with the tabs it kept.
    const windowInfo = demoMode ? Promise.resolve(null)
      : windowsApi.current({}, { signal: abort.signal, timeoutMilliseconds: 8_000 }).catch(() => null);
    void boot.status({}, { signal: abort.signal, timeoutMilliseconds: 8_000 }).then(
      async value => { const shown = await windowInfo; if (!abort.signal.aborted) setPurpose({ recovery: value.configRecovery, developer: value.developerMode, window: shown }); },
      async () => { const shown = await windowInfo; if (!abort.signal.aborted) setPurpose({ recovery: false, developer: false, window: shown }); }); // The workspace reports a bridge that does not answer.
    return () => abort.abort();
  }, []);
  return !purpose ? null : purpose.recovery ? <ConfigRecoveryScreen developer={purpose.developer} /> : <App initialWindow={purpose.window} />;
}

// The window of a system prompt compares two prompts with the diff editor, which the application brings.
createRoot(document.getElementById("root")!).render(<StrictMode><SystemPromptDiffContext.Provider value={SystemPromptDiff}><TimelineEmphasisProvider><Root /></TimelineEmphasisProvider></SystemPromptDiffContext.Provider></StrictMode>);
