using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.Catalog;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Editing;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Tabs;
using CodeAlta.Presentation.Threads;
using CodeAlta.Presentation.Usage;
using CodeAlta.Presentation.Workspace;
using CodeAlta.ViewModels;
using CodeAlta.Search;
using XenoAtom.Logging;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Threading;

namespace CodeAlta.Views;

internal sealed class CodeAltaApp : IAsyncDisposable
{
    internal static readonly Logger UiLogger = LogManager.GetLogger("CodeAlta.UI");
    internal const string DraftTabId = "__draft__";
    private const bool DefaultAutoApproveEnabled = true;
    private readonly ChatBackendPreferenceCoordinator _backendPreferences;
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly AgentHub _agentHub;
    private readonly KnownProjectImporter _knownProjectImporter;
    private readonly CodeAltaOwnedServices? _ownedServices;
    private readonly CodeAltaShellController _shellController;
    private readonly RuntimeEventPump _runtimeEventPump;
    private readonly TerminalLoopCoordinator _terminalLoopCoordinator;
    private readonly ChatBackendInitializationCoordinator _chatBackendInitializationCoordinator;
    private readonly ShellThreadStateCoordinator _threadStateCoordinator;
    private readonly ShellWorkspaceCoordinator _workspaceCoordinator;
    private readonly ThreadHistoryCoordinator _threadHistoryCoordinator;
    private readonly ThreadRuntimeEventCoordinator _threadRuntimeEventCoordinator;
    private readonly ThreadPromptQueueCoordinator _threadPromptQueueCoordinator;
    private readonly ThreadCommandCoordinator _threadCommandCoordinator;
    private readonly ShellCommandSurfaceCoordinator _shellCommandSurfaceCoordinator;
    private readonly ThreadCreationCoordinator _threadCreationCoordinator;
    private readonly PromptDraftUiCoordinator _promptDraftUiCoordinator;
    private readonly CodeAltaShellViewModel _shellViewModel;
    private readonly SidebarViewModel _sidebarViewModel;
    private readonly ThreadWorkspaceViewModel _threadWorkspaceViewModel;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly SessionUsageViewModel _sessionUsageViewModel;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly SidebarCoordinator _sidebarCoordinator;
    private readonly NavigatorActionCoordinator _navigatorActionCoordinator;
    private readonly ChatSelectorCoordinator _chatSelectorCoordinator;
    private readonly ThreadTabStripCoordinator _threadTabStripCoordinator;
    private readonly ChatPreferenceContext _chatPreferenceContext;
    private readonly ShellAnimationRuntime _shellAnimationRuntime = new();
    private readonly DeferredUiActionQueue _deferredUiActionQueue = new();
    private readonly ShellWorkspaceContext _shellWorkspaceContext;
    private readonly ThreadSelectionContext _threadSelectionContext;
    private readonly ThreadTabContext _threadTabContext;
    private readonly WorkspaceRefreshContext _workspaceRefreshContext;
    private readonly AcpManagementCoordinator? _acpManagementCoordinator;
    private readonly AcpFrontendCoordinator _acpUi;
    private readonly ProviderFrontendCoordinator _providerUi;
    private readonly ProviderDialogCoordinator _providerDialogCoordinator;
    private readonly FileEditorWorkspaceCoordinator _fileEditorWorkspaceCoordinator;
    private readonly InitialCatalogStateCoordinator _initialCatalogStateCoordinator;
    private CodeAltaShellView? _shellView;
    private ThreadWorkspaceView? _threadWorkspaceView;
    private SessionUsagePresenter? _sessionUsagePresenter;
    private ThreadInfoPresenter? _threadInfoPresenter;
    private IUiDispatcher? _uiDispatcher;
    private bool _disableTerminalLoopCallback;
    private bool _commandBarMultiLine;
    private bool _startupProviderDialogHandled;
    private WorkThreadViewState _viewState
    {
        get => _threadStateCoordinator.ViewState;
        set => _threadStateCoordinator.ViewState = value;
    }
    private Visual? ThreadPaneLayout => _threadWorkspaceView?.ThreadPaneLayout;
    private VSplitter? ThreadBodySplitter => _threadWorkspaceView?.ThreadBodySplitter;
    private ChatPromptEditor? ThreadInput => _threadWorkspaceView?.ThreadInput;
    private Visual GetDialogAnchor() => ThreadInput is Visual input ? input : _sidebarCoordinator.View.Tree;
    private CommandBar? ThreadCommandBar => _threadWorkspaceView?.ThreadCommandBar;
    private TabControl? ThreadTabControl => _threadWorkspaceView?.ThreadTabControl;

    public CodeAltaApp(
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub)
        : this(
            CodeAltaOwnedServices.CreateBuiltInBackendDescriptors(),
            projectCatalog,
            threadCatalog,
            runtimeService,
            catalogOptions,
            agentHub,
            NullProjectFileSearchService.Instance,
            knownProjectImporter: null,
            ownedServices: null)
    {
    }

    public static async Task<CodeAltaApp> CreateAsync(CancellationToken cancellationToken)
        => Create(await CodeAltaOwnedServices.CreateAsync(cancellationToken));
    internal static CodeAltaApp Create(CodeAltaOwnedServices ownedServices)
    {
        ArgumentNullException.ThrowIfNull(ownedServices);
        return new(
            ownedServices.BackendDescriptors,
            ownedServices.ProjectCatalog,
            ownedServices.ThreadCatalog,
            ownedServices.RuntimeService,
            ownedServices.CatalogOptions,
            ownedServices.AgentHub,
            ownedServices.ProjectFileSearchService,
            new KnownProjectImporter(ownedServices.AgentHub, ownedServices.BackendDescriptors, ownedServices.ProjectCatalog, ownedServices.CatalogOptions),
            ownedServices,
            ownedServices.CodexInstallProgress);
    }

    private CodeAltaApp(
        IReadOnlyList<AgentBackendDescriptor> backendDescriptors,
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub,
        IProjectFileSearchService projectFileSearchService,
        KnownProjectImporter? knownProjectImporter,
        CodeAltaOwnedServices? ownedServices,
        CodexInstallProgressReporter? codexInstallProgress = null)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        _backendPreferences = new ChatBackendPreferenceCoordinator(new CodeAltaConfigStore(catalogOptions), UiLogger);
        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _agentHub = agentHub;
        _knownProjectImporter = knownProjectImporter ?? new KnownProjectImporter(agentHub, backendDescriptors, projectCatalog, catalogOptions);
        _ownedServices = ownedServices;
        ShellThreadStateCoordinator? tsc = null;
        var composition = CodeAltaFrontendComposition.Create(
            backendDescriptors,
            projectCatalog,
            threadCatalog,
            runtimeService,
            catalogOptions,
            agentHub,
            projectFileSearchService,
            new CodeAltaShellBridge(this),
            _knownProjectImporter,
            _shellAnimationRuntime.WelcomePhase01,
            new CodeAltaFrontendCallbacks
            {
                AssignUiDispatcher = dispatcher => _uiDispatcher = dispatcher,
                ApplyPendingSidebarSelection = ApplyPendingSidebarSelection,
                GetUiDispatcher = GetUiDispatcher,
                GetThreadPaneBounds = () => ThreadPaneLayout?.GetAbsoluteBounds(),
                IsChatBackendReady = IsChatBackendReady,
                LoadPromptDraft = threadId => _promptDraftUiCoordinator!.LoadPromptDraft(threadId),
                DeletePromptDraft = threadId => _promptDraftUiCoordinator!.DeletePersistedPromptDraft(threadId),
                ApplyThreadPreference = ApplyThreadPreference,
                RememberThreadPreference = RememberThreadPreference,
                EnsureThreadHistoryLoadedAsync = EnsureThreadHistoryLoadedAsync,
                RefreshSelectionAndThreadWorkspace = RefreshSelectionAndThreadWorkspace,
                RefreshCatalogAndThreadWorkspace = RefreshCatalogAndThreadWorkspace,
                ResetPendingThreadTabSelection = ResetPendingThreadTabSelection,
                RemoveThreadTabPage = threadId => _threadWorkspaceView?.RemoveTabPage(threadId),
                SetStatus = SetStatus,
                SetProviderSessionLoadStatus = SetProviderSessionLoadStatus,
                IsSelectedThread = IsSelectedThread,
                ApplyDraftBackendPreference = ApplyDraftBackendPreference,
                RememberGlobalBackendPreference = RememberGlobalBackendPreference,
                InvalidateSelectedSessionUsage = InvalidateSelectedSessionUsage,
                RefreshHeaderAndThreadWorkspace = RefreshHeaderAndThreadWorkspace,
                RekeyThreadIdentity = (oldId, thread) => tsc!.RekeyThreadIdentity(oldId, thread),
                HasWorkspaceSurface = () => _threadWorkspaceView is not null,
                SetThreadPaneContent = content =>
                {
                    if (ThreadBodySplitter is not null)
                    {
                        ThreadBodySplitter.First = content;
                    }
                },
                EnsureSelectionDefaults = EnsureSelectionDefaults,
                RefreshSidebarProjection = RefreshSidebarProjection,
                SyncSidebarSelectionToCurrentState = SyncSidebarSelectionToCurrentState,
                RefreshChatSelectorsForDraftScope = () => RefreshChatSelectorsForDraftScope(),
                RefreshChatSelectorsForThread = RefreshChatSelectorsForThread,
                SyncChatSelectorItems = SyncChatSelectorItems,
                SyncPromptText = session => _promptDraftUiCoordinator!.SyncPromptText(session),
                UpdatePromptAvailabilityUi = UpdatePromptAvailabilityUi,
                UpdatePromptImageAttachmentsUi = UpdatePromptImageAttachmentsUi,
                SyncThreadTabControl = SyncThreadTabControl,
                DispatchToUi = DispatchToUi,
                DispatchToUiDeferred = DispatchToUiDeferred,
                FocusPromptTarget = FocusPromptTarget,
                VerifyBindableAccess = VerifyBindableAccess,
                GetAutoApproveEnabled = GetAutoApproveEnabled,
                RefreshShellChrome = RefreshShellChrome,
                SetThreadStatus = (tab, message, showSpinner, tone) => SetThreadStatus(tab, message, showSpinner, tone),
                ClearThreadStatus = ClearThreadStatus,
                TrySetPromptUnavailableStatus = TrySetPromptUnavailableStatus,
                SetReadyStatusForCurrentSelection = SetReadyStatusForCurrentSelection,
                ClearDraftPromptText = () => _promptDraftUiCoordinator!.ClearDraftPromptText(),
                ClearPromptText = () => _promptDraftUiCoordinator!.ClearPrompt(),
                IsPromptTextEmpty = () => ReadBindableState(() => string.IsNullOrWhiteSpace(_promptDraftUiCoordinator!.PromptText) && !_promptDraftUiCoordinator.HasCurrentPromptImages),
                RestorePromptText = prompt => DispatchToUi(() => _promptDraftUiCoordinator!.PromptText = prompt),
                SnapshotPromptImages = () => ReadBindableState(() => _promptDraftUiCoordinator!.SnapshotPromptImages()),
                RestorePromptImages = images => DispatchToUi(() => _promptDraftUiCoordinator!.RestorePromptImages(images)),
                PersistViewStateAsync = PersistViewStateAsync,
                RegisterCreatedThreadAsync = RegisterCreatedThreadAsync,
                GetPromptFocusTarget = () => ThreadInput,
            },
            codexInstallProgress,
            ownedServices?.PluginHostBridge);
        _backendPreferences = composition.BackendPreferences;
        _shellController = composition.ShellController;
        _runtimeEventPump = composition.RuntimeEventPump;
        _terminalLoopCoordinator = composition.TerminalLoopCoordinator;
        _chatBackendInitializationCoordinator = composition.ChatBackendInitializationCoordinator;
        _threadStateCoordinator = composition.ThreadStateCoordinator;
        tsc = _threadStateCoordinator;
        _workspaceCoordinator = composition.WorkspaceCoordinator;
        _threadRuntimeEventCoordinator = composition.ThreadRuntimeEventCoordinator;
        _threadPromptQueueCoordinator = composition.ThreadPromptQueueCoordinator;
        _threadCommandCoordinator = composition.ThreadCommandCoordinator;
        _threadCreationCoordinator = composition.ThreadCreationCoordinator;
        _promptDraftUiCoordinator = composition.PromptDraftUiCoordinator;
        _shellViewModel = composition.ShellViewModel;
        _sidebarViewModel = composition.SidebarViewModel;
        _threadWorkspaceViewModel = composition.ThreadWorkspaceViewModel;
        _promptComposerViewModel = composition.PromptComposerViewModel;
        _sessionUsageViewModel = composition.SessionUsageViewModel;
        _chatBackendStates = composition.ChatBackendStates;
        _sidebarCoordinator = composition.SidebarCoordinator;
        _navigatorActionCoordinator = composition.NavigatorActionCoordinator;
        _chatSelectorCoordinator = composition.ChatSelectorCoordinator;
        _chatPreferenceContext = composition.ChatPreferenceContext;
        _shellWorkspaceContext = composition.ShellWorkspaceContext;
        _threadSelectionContext = composition.ThreadSelectionContext;
        _workspaceRefreshContext = composition.WorkspaceRefreshContext;
        _initialCatalogStateCoordinator = new InitialCatalogStateCoordinator(
            cancellationToken => _threadStateCoordinator.LoadInitialCatalogStateAsync(cancellationToken),
            _threadStateCoordinator.ApplyInitialCatalogState,
            RefreshCatalogAndThreadWorkspace,
            FocusPromptEditor,
            SetStatus);
        _acpUi = new AcpFrontendCoordinator(
            _ownedServices,
            _chatBackendInitializationCoordinator,
            _chatBackendStates,
            DispatchToUi,
            RefreshSelectionAndThreadWorkspace,
            SetStatus);
        _providerUi = new ProviderFrontendCoordinator(_ownedServices, _catalogOptions, _chatBackendInitializationCoordinator, _chatBackendStates, DispatchToUi, RefreshSelectionAndThreadWorkspace, SetStatus);
        _providerDialogCoordinator = new ProviderDialogCoordinator(
            _providerUi,
            () => DialogBoundsResolver.ResolveAppBounds(GetDialogAnchor()),
            GetDialogAnchor);
        _acpManagementCoordinator = AcpManagementCoordinatorFactory.Create(
            _ownedServices,
            _catalogOptions,
            _chatBackendStates,
            () => _acpUi.RefreshBackendsAsync(),
            agentId => _acpUi.ProbeBackendAsync(agentId),
            () => DialogBoundsResolver.ResolveAppBounds(GetDialogAnchor()),
            GetDialogAnchor);
        _fileEditorWorkspaceCoordinator = new FileEditorWorkspaceCoordinator(
            projectFileSearchService,
            () => PromptReferenceProjectRootResolver.Resolve(GetSelectedThread(), GetProjectById, GetSelectedProject),
            () => ThreadInput,
            () => _threadWorkspaceView,
            DispatchToUiDeferred,
            SyncThreadTabControl,
            SetStatus);
        _threadTabContext = new ThreadTabContext(
            () => ThreadTabControl,
            () => _threadWorkspaceView,
            build => CreateComputedVisual(build),
            GetUiDispatcher,
            () => ObserveUiTask(ActivateDraftTabAsync(), "activate the draft tab"),
            ActivateThreadSurface,
            threadId => ObserveUiTask(CloseThreadAsync(threadId), "close the thread tab"),
            () => ObserveUiTask(CloseDraftTabAsync(), "close the draft tab"),
            threadId => ObserveUiTask(_shellController.OpenThreadAsync(threadId, CancellationToken.None), "open the thread tab"),
            tabId => _fileEditorWorkspaceCoordinator.GetFileTab(tabId),
            tabId => _fileEditorWorkspaceCoordinator.SelectFileTab(tabId),
            tabId => ObserveUiTask(_fileEditorWorkspaceCoordinator.CloseFileTabAsync(tabId), "close the file tab"));
        _threadTabStripCoordinator = new ThreadTabStripCoordinator(
            _threadSelectionContext,
            _threadTabContext,
            () => _fileEditorWorkspaceCoordinator.OpenTabIds,
            () => _fileEditorWorkspaceCoordinator.SelectedTabId);
        _shellCommandSurfaceCoordinator = new ShellCommandSurfaceCoordinator(
            _promptComposerViewModel,
            _threadWorkspaceViewModel,
            _threadCommandCoordinator,
            () => _threadStateCoordinator.Projects,
            OpenFolderAsync,
            OpenModelProvidersAsync,
            _fileEditorWorkspaceCoordinator.ShowOpenFilePickerAsync,
            SkillsManagementCoordinatorFactory.Create(
                _ownedServices,
                _catalogOptions,
                GetSelectedProject,
                GetDialogAnchor,
                path => _fileEditorWorkspaceCoordinator.OpenFilePathAsync(path),
                skillName => _threadCommandCoordinator.ActivateSelectedSkillAsync(skillName),
                SetStatus),
            PluginManagementCoordinatorFactory.Create(_catalogOptions, GetSelectedProject, GetDialogAnchor, SetStatus),
            () => ReadBindableState(() => _promptDraftUiCoordinator.PromptText),
            () => _fileEditorWorkspaceCoordinator.GetSelectedFileTab() is { } fileTab
                ? _fileEditorWorkspaceCoordinator.CloseFileTabAsync(fileTab.TabId)
                : GetSelectedThread() is not null
                    ? CloseSelectedThreadAsync()
                    : CloseDraftTabAsync(),
            SetStatus,
            () => DialogBoundsResolver.ResolveAppBounds(ThreadInput),
            () => ThreadInput,
            GetSelectedThread,
            EnsureThreadTab,
            FocusSidebar,
            FocusPromptEditor,
            ToggleCommandBarMultiLine,
            () => EnsureSessionUsagePresenter().TogglePopupFromIndicator(),
            () => { if (ThreadInput is not null) EnsureThreadInfoPresenter().TogglePopup(ThreadInput); },
            () => _threadWorkspaceView?.OpenExpandedPromptDialog(),
            () => { _ = _threadTabStripCoordinator.TrySelectRelativeTab(-1); return Task.CompletedTask; },
            () => { _ = _threadTabStripCoordinator.TrySelectRelativeTab(1); return Task.CompletedTask; },
            () => ScrollSelectedThreadMessageAsync(static tab => tab.Timeline.ScrollToPreviousMessage()),
            () => ScrollSelectedThreadMessageAsync(static tab => tab.Timeline.ScrollToNextMessage()),
            () => ScrollSelectedThreadMessageAsync(static tab => tab.Timeline.ScrollToFirstMessage()),
            () => ScrollSelectedThreadMessageAsync(static tab => tab.Timeline.ScrollToLastMessage()),
            _ownedServices?.PluginHostBridge);
        _threadHistoryCoordinator = new ThreadHistoryCoordinator(
            _runtimeService,
            EnsureThreadTab,
            threadId => FindThread(threadId),
            threadId => _threadStateCoordinator.FindOpenThread(threadId),
            thread => ThreadHistoryCoordinator.CanLoadThreadHistory(thread) && IsChatBackendReady(new AgentBackendId(thread.BackendId)),
            _threadCommandCoordinator.BuildExecutionOptions,
            (tab, message, showSpinner, tone) => SetThreadStatus(tab, message, showSpinner, tone),
            ClearThreadStatus,
            ResetThreadTab,
            _threadRuntimeEventCoordinator.HandleAgentEvent,
            thread => _threadStateCoordinator.PersistThreadLocalStateAsync(thread));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        PrepareForRun();

        var root = EnsureShellView().Root;

        await Terminal.RunAsync(
                root,
                () => Tick(cancellationToken),
                cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _fileEditorWorkspaceCoordinator.DisposeAsync();
        await _runtimeEventPump.DisposeAsync();
        await _shellController.DisposeAsync();
        await _promptDraftUiCoordinator.DisposeAsync();
        if (_ownedServices is not null) await _ownedServices.DisposeAsync();
    }

    private string? GetDraftProjectRoot()
        => _threadStateCoordinator.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true }
            ? null
            : GetSelectedProject()?.ProjectPath;

    private string? GetThreadProjectRoot(WorkThreadDescriptor thread)
        => GetProjectById(thread.ProjectRef)?.ProjectPath;

    private void ApplyDraftBackendPreference(ChatBackendState backendState)
        => _backendPreferences.ApplyDraftBackendPreference(backendState, GetDraftProjectRoot());

    private void ApplyThreadPreference(OpenThreadState tab)
        => _backendPreferences.ApplyThreadPreference(tab, _viewState, GetThreadProjectRoot(tab.Thread), _chatBackendStates);

    private void RememberGlobalBackendPreference(
        AgentBackendId backendId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort)
        => _backendPreferences.RememberGlobalBackendPreference(backendId, modelId, reasoningEffort);

    private void RememberThreadPreference(
        string threadId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort,
        bool autoScroll,
        bool persistNow)
    {
        _backendPreferences.RememberThreadPreference(_viewState, threadId, modelId, reasoningEffort, autoScroll);
        if (persistNow)
        {
            _ = PersistViewStateAsync();
        }
    }

    private void RefreshSidebarProjection()
        => SidebarUiStateHelpers.RefreshProjection(
            _sidebarCoordinator,
            _threadStateCoordinator,
            _promptDraftUiCoordinator,
            threadId => _threadStateCoordinator.FindOpenThread(threadId),
            VerifyBindableAccess);

    private void SyncSidebarSelectionToCurrentState()
        => _sidebarCoordinator.SyncSelectionToCurrentState(SidebarUiStateHelpers.ResolveCurrentTarget(_threadStateCoordinator));

    private void ApplyPendingSidebarSelection()
        => _sidebarCoordinator.ApplyPendingSelection();

    internal void PrepareForRun() => SetStatus("Connecting to available providers...", showSpinner: true);
    internal Visual GetRoot() => EnsureShellView().Root;

    internal TerminalLoopResult Tick(CancellationToken cancellationToken)
    {
        if (_disableTerminalLoopCallback)
        {
            DrainDeferredUiActions();
            return TerminalLoopResult.Continue;
        }

        _shellAnimationRuntime.Advance();
        var now = DateTimeOffset.UtcNow;
        _workspaceCoordinator.RefreshRunningStatusElapsed(now);
        _sidebarCoordinator.RefreshRecency(now, VerifyBindableAccess);
        _initialCatalogStateCoordinator.EnsureStarted(cancellationToken);
        if (!TryResolveInitialCatalogState(cancellationToken))
        {
            DrainDeferredUiActions();
            return TerminalLoopResult.Continue;
        }

        var result = _terminalLoopCoordinator.OnIteration(cancellationToken);
        DrainDeferredUiActions();
        return result;
    }

    private void ToggleTerminalLoopCallback()
    {
        _disableTerminalLoopCallback = !_disableTerminalLoopCallback;
        SetStatus(
            _disableTerminalLoopCallback
                ? "Loop callback disabled."
                : "Loop callback enabled.",
            tone: _disableTerminalLoopCallback ? StatusTone.Warning : StatusTone.Info);
    }

    private void ToggleCommandBarMultiLine()
    {
        _commandBarMultiLine = !_commandBarMultiLine;
        if (ThreadCommandBar is not null)
        {
            ThreadCommandBar.MultiLine = _commandBarMultiLine;
        }

        SetStatus(
            _commandBarMultiLine
                ? "Command bar multiline enabled."
                : "Command bar single-line enabled.",
            tone: StatusTone.Info);
    }

    private bool TryResolveInitialCatalogState(CancellationToken cancellationToken)
    {
        if (!_initialCatalogStateCoordinator.TryResolve(cancellationToken))
        {
            return false;
        }

        if (!_startupProviderDialogHandled && !_providerUi.HasAnyEnabledProviders())
        {
            _startupProviderDialogHandled = true;
            SetStatus("No model providers are enabled. Open Model Providers (Ctrl+G Ctrl+M) to configure one.", false, StatusTone.Warning);
            _ = OpenModelProvidersAsync();
        }

        return true;
    }

    private void RefreshChatSelectorsForDraftScope(AgentBackendId? preferredBackendId = null)
        => _chatSelectorCoordinator.RefreshForDraftScope(preferredBackendId);

    private void RefreshChatSelectorsForThread(OpenThreadState tab)
        => _chatSelectorCoordinator.RefreshForThread(tab);
    private void SyncChatSelectorItems()
        => _threadWorkspaceView?.SyncChatSelectorItems(_threadWorkspaceViewModel);
    private void OnChatBackendSelectionChanged(int newIndex)
        => ObserveUiTask(_chatSelectorCoordinator.OnBackendSelectionChangedAsync(newIndex), "change the selected provider");
    private void OnChatModelSelectionChanged(int newIndex)
        => _chatSelectorCoordinator.OnModelSelectionChanged(newIndex);
    private void OnChatReasoningSelectionChanged(int newIndex)
        => _chatSelectorCoordinator.OnReasoningSelectionChanged(newIndex);
    private void OnChatAutoScrollChanged()
        => _chatSelectorCoordinator.OnAutoScrollChanged();
    private AgentBackendId GetPreferredBackendId()
        => _chatSelectorCoordinator.GetPreferredBackendId();
    private bool IsChatBackendReady(AgentBackendId backendId)
        => _chatSelectorCoordinator.IsChatBackendReady(backendId);

    private bool TryGetPromptUnavailableStatus(out string message, out StatusTone tone)
        => _chatSelectorCoordinator.TryGetPromptUnavailableStatus(out message, out tone);
    private bool TrySetPromptUnavailableStatus() { if (!TryGetPromptUnavailableStatus(out var message, out var tone)) return false; SetStatus(message, tone: tone); return true; }
    private void UpdatePromptAvailabilityUi()
        => _chatSelectorCoordinator.UpdatePromptAvailabilityUi();

    private void UpdatePromptImageAttachmentsUi() { _promptComposerViewModel.PromptImageAttachmentVersion++; RefreshSidebarProjection(); }
    private void SyncThreadTabControl()
        => _threadTabStripCoordinator.SyncControl();
    private void OnThreadTabControlSelectionChanged(int selectedIndex)
        => _threadTabStripCoordinator.OnSelectionChanged(selectedIndex);
    private void ResetPendingThreadTabSelection()
        => _threadTabStripCoordinator.ResetPendingSelection();

    private CodeAltaShellView EnsureShellView()
    {
        var imageCallbacks = PromptImageWorkspaceCallbackFactory.Create(
            _promptDraftUiCoordinator,
            new PromptImageCapabilityContext(
                GetSelectedThread,
                threadId => _threadStateCoordinator.FindOpenThread(threadId),
                GetPreferredBackendId,
                _chatBackendStates),
            (message, tone) => SetStatus(message, tone: tone));
        _threadWorkspaceView ??= new ThreadWorkspaceView(
            _shellViewModel,
            _threadWorkspaceViewModel,
            _promptComposerViewModel,
            _shellCommandSurfaceCoordinator.BuildWorkspaceCommandBindings(),
            () => CreateUsageComputedVisual(EnsureSessionUsagePresenter().BuildIndicatorVisual),
            () => EnsureSessionUsagePresenter().TogglePopupFromIndicator(),
            anchor => EnsureThreadInfoPresenter().TogglePopup(anchor),
            () => ObserveUiTask(_shellCommandSurfaceCoordinator.ShowHelpAsync(), "show help"),
            () => _shellCommandSurfaceCoordinator.ShowCommandPalette(),
            () => ObserveUiTask(OpenModelProvidersAsync(), "open model providers"),
            _ownedServices?.ProjectFileSearchService ?? NullProjectFileSearchService.Instance,
            () => PromptReferenceProjectRootResolver.Resolve(GetSelectedThread(), GetProjectById, GetSelectedProject),
            acceptedPrompt => ObserveUiTask(_shellCommandSurfaceCoordinator.HandleAcceptedPromptAsync(acceptedPrompt), "submit the current prompt"),
            () => ObserveUiTask(_shellCommandSurfaceCoordinator.SubmitCurrentPromptAsync(steer: false), "submit the current prompt"),
            () => ObserveUiTask(_shellCommandSurfaceCoordinator.SubmitCurrentPromptAsync(steer: true), "steer the current thread"),
            () => ObserveUiTask(_threadCommandCoordinator.ClearSelectedThreadQueueAsync(), "clear the thread queue"),
            queuedPromptId => ObserveUiTask(_threadCommandCoordinator.ConvertSelectedThreadQueuedPromptToSteerAsync(queuedPromptId), "convert the queued prompt to steer"),
            pendingSteerId => _threadCommandCoordinator.DeleteSelectedThreadPendingSteer(pendingSteerId),
            queuedPromptId => _threadCommandCoordinator.DeleteSelectedThreadQueuedPrompt(queuedPromptId),
            (queuedPromptId, remainingCount) => _threadCommandCoordinator.UpdateSelectedThreadQueuedPromptCount(queuedPromptId, remainingCount),
            (queuedPromptId, text) => _threadCommandCoordinator.UpdateSelectedThreadQueuedPromptText(queuedPromptId, text),
            () => ObserveUiTask(_shellCommandSurfaceCoordinator.SubmitCurrentDelegationAsync(), "delegate internal work"),
            () => ObserveUiTask(_shellCommandSurfaceCoordinator.AbortSelectedThreadAsync(), "abort the selected thread"),
            () => ObserveUiTask(_shellCommandSurfaceCoordinator.CompactSelectedThreadAsync(), "compact the selected thread"),
            () => ObserveUiTask(_shellCommandSurfaceCoordinator.CloseCurrentTabAsync(), "close the current tab"),
            OnChatBackendSelectionChanged,
            OnChatModelSelectionChanged,
            OnChatReasoningSelectionChanged,
            selectedIndex => _threadTabStripCoordinator.ObserveBoundSelection(selectedIndex),
            _promptDraftUiCoordinator.PromptTextBinding,
            _shellAnimationRuntime.ThinkingPhase01,
            OnChatAutoScrollChanged,
            imageCallbacks);
        ThreadCommandBar!.MultiLine = _commandBarMultiLine;
        _fileEditorWorkspaceCoordinator.RefreshActiveContent();

        RefreshCatalogAndThreadWorkspace();

        _shellView ??= CodeAltaShellViewFactory.Create(
            _sidebarCoordinator.View.Root,
            _threadWorkspaceView.Root,
            ThreadCommandBar!,
            _shellCommandSurfaceCoordinator,
            OpenAcpManagement,
            ToggleTerminalLoopCallback,
            FocusSidebar,
            FocusPromptEditor,
            () => _fileEditorWorkspaceCoordinator.SelectedTabId is null,
            _ownedServices?.PluginHostBridge);

        return _shellView;
    }


    internal static Visual CreateThreadTabPageContentPlaceholder() => new Placeholder { IsVisible = false };

    private void RefreshShellChrome()
        => _workspaceCoordinator.RefreshShellChrome();
    internal void RefreshCatalogAndThreadWorkspace() { _threadInfoPresenter?.InvalidateSelection(); _workspaceCoordinator.RefreshCatalogAndThreadWorkspace(); }
    private void RefreshHeaderAndThreadWorkspace()
        => _workspaceCoordinator.RefreshHeaderAndThreadWorkspace();
    private void RefreshSelectionAndThreadWorkspace() { _threadInfoPresenter?.InvalidateSelection(); _workspaceCoordinator.RefreshSelectionAndThreadWorkspace(); }
    internal void SelectGlobalScope()
    {
        ActivateThreadSurface();
        _threadStateCoordinator.SelectGlobalScope();
    }

    internal void SelectProjectScope(string projectId)
    {
        ActivateThreadSurface();
        _threadStateCoordinator.SelectProjectScope(projectId);
    }
    private void EnsureSelectionDefaults() => _threadStateCoordinator.EnsureSelectionDefaults();
    internal void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info) => _workspaceCoordinator.SetStatus(message, showSpinner, tone);
    internal void SetProviderSessionLoadStatus(string? message) => _workspaceCoordinator.SetProviderSessionLoadStatus(message);
    private void SetThreadStatus(
        OpenThreadState tab,
        string message,
        bool showSpinner = false,
        StatusTone tone = StatusTone.Info,
        bool hasCustomStatus = true)
        => _workspaceCoordinator.SetThreadStatus(tab, message, showSpinner, tone, hasCustomStatus);
    private void ClearThreadStatus(OpenThreadState tab)
        => _workspaceCoordinator.ClearThreadStatus(tab);
    private void InvalidateSelectedSessionUsage()
        => _workspaceCoordinator.InvalidateSelectedSessionUsage();

    private bool IsSelectedThread(string threadId)
        => !string.IsNullOrWhiteSpace(threadId) &&
           string.Equals(_threadStateCoordinator.Selection.SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase);

    internal void SetReadyStatusForCurrentSelection()
        => _workspaceCoordinator.SetReadyStatusForCurrentSelection();

    private void ObserveUiTask(Task task, string operation)
        => _ = UiTaskDiagnostics.ObserveAsync(task, operation, SetStatus);

    private SessionUsagePresenter EnsureSessionUsagePresenter()
        => _sessionUsagePresenter ??= PopupPresenterFactory.CreateSessionUsagePresenter(
            _sessionUsageViewModel,
            () => ThreadPaneLayout?.App,
            () => ThreadInput,
            build => CreateUsageComputedVisual(build));

    private ThreadInfoPresenter EnsureThreadInfoPresenter()
        => _threadInfoPresenter ??= PopupPresenterFactory.CreateThreadInfoPresenter(
            () => ThreadPaneLayout?.App,
            () => ThreadInput,
            new ThreadInfoService(_agentHub, _threadSelectionContext, _chatBackendStates),
            DispatchToUi,
            build => CreateComputedVisual(build));

    private T ReadBindableState<T>(Func<T> read) { ArgumentNullException.ThrowIfNull(read); return UiDispatch.Invoke(GetUiDispatcher(), () => { VerifyBindableAccess(); return read(); }); }

    internal void SetShellInitialized(bool isInitialized)
        => _workspaceCoordinator.SetShellInitialized(isInitialized);

    private void DispatchToUi(Action action) { ArgumentNullException.ThrowIfNull(action); var dispatcher = GetUiDispatcher(); UiDispatch.Post(dispatcher, action, allowInline: ShouldRunInlineOnCurrentThread(dispatcher.CheckAccess(), _terminalLoopCoordinator.HasStarted)); }
    private void DispatchToUiDeferred(Action action) { ArgumentNullException.ThrowIfNull(action); _deferredUiActionQueue.Enqueue(action); }

    internal static bool CanAccessBindableState(bool dispatcherHasAccess, bool terminalLoopStarted)
        => !terminalLoopStarted || dispatcherHasAccess;

    private void VerifyBindableAccess()
    {
        var dispatcher = GetUiDispatcher();
        if (CanAccessBindableState(dispatcher.CheckAccess(), _terminalLoopCoordinator.HasStarted))
        {
            return;
        }

        throw new InvalidOperationException("Bindable view-model state must be accessed on the UI thread.");
    }

    internal static bool ShouldRunInlineOnCurrentThread(bool dispatcherHasAccess, bool terminalLoopStarted)
        => !terminalLoopStarted || dispatcherHasAccess;

    internal static bool ShouldRunDeferredUiActionInlineOnCurrentThread(bool dispatcherHasAccess, bool terminalLoopStarted)
        => !terminalLoopStarted && dispatcherHasAccess;

    private void DrainDeferredUiActions()
        => _deferredUiActionQueue.Drain();

    private IUiDispatcher GetUiDispatcher()
        => _uiDispatcher ??= new TerminalUiDispatcher(Dispatcher.Current);

    private ComputedVisual CreateComputedVisual(Func<Visual> build)
        => _workspaceCoordinator.CreateComputedVisual(build);

    private ComputedVisual CreateUsageComputedVisual(Func<Visual> build)
        => _workspaceCoordinator.CreateUsageComputedVisual(build);

    private async Task ActivateDraftTabAsync()
    {
        ActivateThreadSurface();
        ResetPendingThreadTabSelection();
        _threadStateCoordinator.DraftTabOpen = true;
        _threadStateCoordinator.SelectedThreadId = null;
        _viewState.SelectedThreadId = null;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync();
        RefreshSelectionAndThreadWorkspace();
    }

    private async Task CloseDraftTabAsync()
    {
        ActivateThreadSurface();
        if (_threadStateCoordinator.Selection.Target is WorkspaceTarget.Draft)
        {
            _threadStateCoordinator.SelectedThreadId = _viewState.OpenThreadIds.FirstOrDefault();
            _viewState.SelectedThreadId = _threadStateCoordinator.Selection.SelectedThreadId;
        }

        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync();
        RefreshSelectionAndThreadWorkspace();
    }

    private Task OpenFolderAsync(string folderPath, bool includeHidden)
        => _shellController.OpenFolderAsync(folderPath, includeHidden, CancellationToken.None);

    private bool GetAutoApproveEnabled() => DefaultAutoApproveEnabled;
    private async Task PersistViewStateAsync()
        => await _threadStateCoordinator.PersistViewStateAsync();
    internal Task InitializeChatBackendsAsync(CancellationToken cancellationToken)
        => _chatBackendInitializationCoordinator.InitializeAsync(cancellationToken);
    internal Task InitializeChatBackendAsync(AgentBackendId backendId, CancellationToken cancellationToken)
        => _chatBackendInitializationCoordinator.RefreshBackendAsync(backendId, cancellationToken);

    internal void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        bool pruneMissingThreads = true)
        => _threadStateCoordinator.ApplyRecoveredCatalogState(projects, threads, pruneMissingThreads);

    internal void UpsertProject(ProjectDescriptor project)
        => _threadStateCoordinator.UpsertProject(project);

    internal void TrySchedulePendingStartupThreadRestore(CancellationToken cancellationToken)
        => _threadStateCoordinator.TrySchedulePendingStartupThreadRestore(cancellationToken);

    private async Task RestoreStartupThreadHistoryAsync(string? threadId, CancellationToken cancellationToken)
        => await _threadStateCoordinator.RestoreStartupThreadHistoryAsync(threadId, cancellationToken);

    private async Task RegisterCreatedThreadAsync(WorkThreadDescriptor thread)
        => await _threadStateCoordinator.RegisterCreatedThreadAsync(thread);

    internal void OpenThread(string threadId) { ActivateThreadSurface(); _threadStateCoordinator.OpenThread(threadId); }
    internal void FocusPromptEditor() { ActivateThreadSurface(); ThreadPaneLayout?.App?.Focus(ThreadInput); }
    private void FocusPromptTarget() => ThreadPaneLayout?.App?.Focus(ThreadInput);

    private Task ScrollSelectedThreadMessageAsync(Action<OpenThreadState> scroll)
    {
        ArgumentNullException.ThrowIfNull(scroll);

        if (_fileEditorWorkspaceCoordinator.SelectedTabId is not null ||
            _threadStateCoordinator.Selection.Target is not WorkspaceTarget.Thread ||
            GetSelectedThread() is not { } thread)
        {
            SetStatus("Open a thread tab before navigating messages.", false, StatusTone.Warning);
            return Task.CompletedTask;
        }

        var tab = EnsureThreadTab(thread);
        if (!tab.Timeline.HasNavigableMessages)
        {
            SetStatus("No user or assistant messages to navigate in this thread.", false, StatusTone.Info);
            return Task.CompletedTask;
        }

        scroll(tab);
        return Task.CompletedTask;
    }

    internal void OpenAcpManagement() { if (_acpManagementCoordinator is null) { SetStatus("ACP management is unavailable in this app instance.", tone: StatusTone.Warning); return; } _acpManagementCoordinator.Open(); }
    internal Task OpenModelProvidersAsync() => _providerDialogCoordinator.OpenAsync();
    internal void FocusSidebar() { SyncSidebarSelectionToCurrentState(); ApplyPendingSidebarSelection(); _sidebarCoordinator.View.Tree.App?.Focus(_sidebarCoordinator.View.Tree); }
    private async Task CloseSelectedThreadAsync()
        => await _threadStateCoordinator.CloseSelectedThreadAsync();

    private async Task CloseThreadAsync(string threadId)
        => await _threadStateCoordinator.CloseThreadAsync(threadId);

    private Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
        => _threadHistoryCoordinator.EnsureLoadedAsync(thread, cancellationToken);

    internal void HandleRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
        => _threadRuntimeEventCoordinator.ApplyRuntimeEvent(runtimeEvent);

    private OpenThreadState EnsureThreadTab(WorkThreadDescriptor thread)
        => _threadStateCoordinator.EnsureThreadTab(thread);

    private void ResetThreadTab(OpenThreadState tab)
        => _threadStateCoordinator.ResetThreadTab(tab);

    private void ActivateThreadSurface()
    {
        _fileEditorWorkspaceCoordinator.ActivateThreadSurface();
        DispatchToUiDeferred(() => ThreadPaneLayout?.App?.Focus(ThreadInput));
    }

    private ProjectDescriptor? GetSelectedProject()
        => _threadStateCoordinator.GetSelectedProject();

    private ProjectDescriptor? GetProjectById(string? projectId)
        => _threadStateCoordinator.GetProjectById(projectId);

    private WorkThreadDescriptor? GetSelectedThread()
        => _threadStateCoordinator.GetSelectedThread();

    private WorkThreadDescriptor? FindThread(string? threadId)
        => _threadStateCoordinator.FindThread(threadId);

}
