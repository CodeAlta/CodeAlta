using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.Events;
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
using CodeAlta.Plugins.Abstractions;
using CodeAlta.ViewModels;
using XenoAtom.Logging;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Threading;

namespace CodeAlta.Views;

// CodeAltaApp intentionally remains the TUI shell composition root.
// Add behavior to named owners first; keep methods here only to wire composition or adapt terminal controls.
internal sealed class CodeAltaApp : IAsyncDisposable, IShellFrontendHostLifecycle
{
    internal static readonly Logger UiLogger = LogManager.GetLogger("CodeAlta.UI");
    internal const string DraftTabId = "__draft__";
    private const bool DefaultAutoApproveEnabled = true;
    private readonly ModelProviderPreferenceCoordinator _modelProviderPreferences;
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly AgentHub _agentHub;
    private readonly KnownProjectImporter _knownProjectImporter;
    private readonly CodeAltaOwnedServices? _ownedServices;
    private readonly CodeAltaShellController _shellController;
    private readonly RuntimeEventPump _runtimeEventPump;
    private readonly TerminalLoopCoordinator _terminalLoopCoordinator;
    private readonly ShellFrontendHost _frontendHost;
    private readonly FrontendEventPublisher _frontendEvents;
    private readonly ShellProjectionCoordinator _projectionCoordinator;
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
    private readonly ModelProviderSelectorCoordinator _modelProviderSelectorCoordinator;
    private readonly ThreadTabStripCoordinator _threadTabStripCoordinator;
    private readonly InMemoryShellTabService _shellTabService = new();
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
    private readonly IUiDispatcher _uiDispatcher = new TerminalUiDispatcher(Dispatcher.Current);
    private bool _disableTerminalLoopCallback;
    private bool _commandBarMultiLine;
    private bool _startupProviderDialogHandled;
    private WorkThreadViewState _viewState
    {
        get => _threadStateCoordinator.ViewState;
        set => _threadStateCoordinator.ViewState = value;
    }
    internal Visual? ThreadPaneLayout => _threadWorkspaceView?.ThreadPaneLayout;
    internal ChatPromptEditor? ThreadInput => _threadWorkspaceView?.ThreadInput;
    private Visual GetDialogAnchor() => ThreadInput is { } input ? input : _sidebarCoordinator.View.Tree;
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
        _modelProviderPreferences = new ModelProviderPreferenceCoordinator(new CodeAltaConfigStore(catalogOptions), UiLogger);
        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _agentHub = agentHub;
        _knownProjectImporter = knownProjectImporter ?? new KnownProjectImporter(agentHub, backendDescriptors, projectCatalog, catalogOptions);
        _ownedServices = ownedServices;
        _frontendHost = new ShellFrontendHost(this);
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
            this,
            codexInstallProgress,
            ownedServices?.PluginHostBridge);
        _modelProviderPreferences = composition.ModelProviderPreferences;
        _shellController = composition.ShellController;
        _runtimeEventPump = composition.RuntimeEventPump;
        _terminalLoopCoordinator = composition.TerminalLoopCoordinator;
        _frontendEvents = composition.FrontendEvents;
        _shellTabService.SetFrontendEvents(_frontendEvents);
        _chatBackendInitializationCoordinator = composition.ChatBackendInitializationCoordinator;
        _threadStateCoordinator = composition.ThreadStateCoordinator;
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
        _modelProviderSelectorCoordinator = composition.ModelProviderSelectorCoordinator;
        _projectionCoordinator = new ShellProjectionCoordinator(
            _frontendEvents,
            _workspaceCoordinator,
            _modelProviderSelectorCoordinator,
            _threadPromptQueueCoordinator);
        _shellWorkspaceContext = composition.ShellWorkspaceContext;
        _threadSelectionContext = composition.ThreadSelectionContext;
        _workspaceRefreshContext = composition.WorkspaceRefreshContext;
        _initialCatalogStateCoordinator = new InitialCatalogStateCoordinator(
            cancellationToken => _threadStateCoordinator.LoadInitialCatalogStateAsync(cancellationToken),
            _threadStateCoordinator.ApplyInitialCatalogState,
            PublishStartupCatalogProjectionReady,
            FocusPromptEditor,
            SetStatus);
        _acpUi = new AcpFrontendCoordinator(
            _ownedServices,
            _chatBackendInitializationCoordinator,
            _chatBackendStates,
            DispatchToUi,
            composition.FrontendEvents,
            SetStatus);
        _providerUi = new ProviderFrontendCoordinator(_ownedServices, _catalogOptions, _chatBackendInitializationCoordinator, _chatBackendStates, DispatchToUi, composition.FrontendEvents, SetStatus);
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
            _shellTabService,
            () => PromptReferenceProjectRootResolver.Resolve(GetSelectedThread(), GetProjectById, GetSelectedProject),
            () => ThreadInput,
            () => _threadWorkspaceView,
            build => CreateComputedVisual(build),
            DispatchToUiDeferred,
            SyncThreadTabControl,
            SetStatus);
        _threadTabContext = new ThreadTabContext(
            new DelegatingThreadTabSurfacePort(
                () => ThreadTabControl,
                () => _threadWorkspaceView,
                build => CreateComputedVisual(build),
                _uiDispatcher),
            new DelegatingThreadTabLifecyclePort(
                () => ObserveUiTask(ActivateDraftTabAsync, "activate the draft tab"),
                ActivateThreadSurface,
                threadId => ObserveUiTask(() => CloseThreadTabAsync(threadId), "close the thread tab"),
                () => ObserveUiTask(CloseDraftTabAsync, "close the draft tab"),
                threadId => ObserveUiTask(() => _shellController.OpenThreadAsync(threadId, CancellationToken.None), "open the thread tab")),
            new DelegatingFileEditorTabPort(
                tabId => _fileEditorWorkspaceCoordinator.GetFileTab(tabId),
                tabId => _fileEditorWorkspaceCoordinator.SelectFileTab(tabId),
                tabId => ObserveUiTask(() => _fileEditorWorkspaceCoordinator.CloseFileTabAsync(tabId), "close the file tab")));
        _threadTabStripCoordinator = new ThreadTabStripCoordinator(
            _threadSelectionContext,
            _threadTabContext,
            _shellTabService);
        composition.DraftTabReplacement.Bind(_threadTabStripCoordinator.ReplaceDraftTabWithThread);
        var input = new DelegatingShellPromptInputService(() => ReadBindableState(() => _promptDraftUiCoordinator.PromptText), _threadCommandCoordinator.IsCurrentPromptEmpty);
        var threadSvc = new DelegatingShellThreadCommandService(GetSelectedThread, EnsureThreadTab);
        var dialogs = new DelegatingShellDialogCommandService(
            () => DialogBoundsResolver.ResolveAppBounds(ThreadInput), () => ThreadInput, () => _threadStateCoordinator.Projects,
            OpenFolderAsync, OpenModelProvidersAsync, _fileEditorWorkspaceCoordinator.ShowOpenFilePickerAsync,
            SkillsManagementCoordinatorFactory.Create(_ownedServices, _catalogOptions, GetSelectedProject, GetDialogAnchor, _fileEditorWorkspaceCoordinator.OpenFilePathAsync, _threadCommandCoordinator.ActivateSelectedSkillAsync, SetStatus),
            PluginManagementCoordinatorFactory.Create(_catalogOptions, GetSelectedProject, GetDialogAnchor, _fileEditorWorkspaceCoordinator.OpenFilePathAsync),
            () => EnsureSessionUsagePresenter().TogglePopupFromIndicator(),
            () => { if (ThreadInput is not null) EnsureThreadInfoPresenter().TogglePopup(ThreadInput); },
            () => _threadWorkspaceView?.OpenExpandedPromptDialog());
        var navigation = new DelegatingShellNavigationCommandService(
            FocusSidebar, FocusPromptEditor,
            () => { _ = _threadTabStripCoordinator.TrySelectRelativeTab(-1); return Task.CompletedTask; },
            () => { _ = _threadTabStripCoordinator.TrySelectRelativeTab(1); return Task.CompletedTask; },
            () => ScrollSelectedThreadMessageAsync(static tab => tab.Timeline.ScrollToPreviousMessage()), () => ScrollSelectedThreadMessageAsync(static tab => tab.Timeline.ScrollToNextMessage()),
            () => ScrollSelectedThreadMessageAsync(static tab => tab.Timeline.ScrollToFirstMessage()), () => ScrollSelectedThreadMessageAsync(static tab => tab.Timeline.ScrollToLastMessage()));
        var tabCommands = new DelegatingShellTabCommandService(() => _threadTabStripCoordinator.CloseSelectedTabAsync());
        var status = new DelegatingShellStatusService(SetStatus);
        var plugins = new PluginHostCommandService(_ownedServices?.PluginHostBridge);
        _shellCommandSurfaceCoordinator = ShellCommandSurfaceComposition.Create(_promptComposerViewModel, _threadWorkspaceViewModel, _threadCommandCoordinator, input, threadSvc, dialogs, navigation, tabCommands, status, plugins, ToggleCommandBarMultiLine);
        _threadHistoryCoordinator = new ThreadHistoryCoordinator(
            _runtimeService,
            EnsureThreadTab,
            _threadStateCoordinator.FindThread,
            threadId => _threadStateCoordinator.FindOpenThread(threadId),
            thread => ThreadHistoryCoordinator.CanLoadThreadHistory(thread) && IsModelProviderReady(new AgentBackendId(thread.BackendId)),
            _threadCommandCoordinator.BuildExecutionOptions,
            (tab, message, showSpinner, tone) => SetThreadStatus(tab, message, showSpinner, tone),
            ClearThreadStatus,
            ResetThreadTab,
            _threadRuntimeEventCoordinator.HandleAgentEventAsync,
            thread => _threadStateCoordinator.PersistThreadLocalStateAsync(thread));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
        => await _frontendHost.RunAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _frontendHost.DisposeAsync();

    async ValueTask IShellFrontendHostLifecycle.DisposeFrontendAsync()
    {
        _projectionCoordinator.Dispose();
        await PersistViewStateAsync();
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

    internal void ApplyDraftModelProviderPreference(ChatBackendState backendState)
        => _modelProviderPreferences.ApplyDraftModelProviderPreference(backendState, GetDraftProjectRoot());

    internal void ApplyThreadPreference(OpenThreadState tab)
        => _modelProviderPreferences.ApplyThreadPreference(tab, _viewState, GetThreadProjectRoot(tab.Thread), _chatBackendStates);

    internal void RememberGlobalModelProviderPreference(
        AgentBackendId backendId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort)
        => _modelProviderPreferences.RememberGlobalModelProviderPreference(backendId, modelId, reasoningEffort);

    internal void RememberThreadPreference(
        string threadId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort,
        bool persistNow)
    {
        _modelProviderPreferences.RememberThreadPreference(_viewState, threadId, modelId, reasoningEffort);
        if (persistNow)
        {
            _ = PersistViewStateAsync();
        }
    }

    internal void RefreshSidebarProjection()
        => SidebarUiStateHelpers.RefreshProjection(
            _sidebarCoordinator,
            _threadStateCoordinator,
            _promptDraftUiCoordinator,
            threadId => _threadStateCoordinator.FindOpenThread(threadId),
            VerifyBindableAccess);

    internal void SyncSidebarSelectionToCurrentState()
        => _sidebarCoordinator.SyncSelectionToCurrentState(SidebarUiStateHelpers.ResolveCurrentTarget(_threadStateCoordinator));

    internal void ApplyPendingSidebarSelection()
        => _sidebarCoordinator.ApplyPendingSelection();

    public void PrepareForRun() => SetStatus("Connecting to available providers...", showSpinner: true);
    public Visual GetRoot() => EnsureShellView().Root;

    public TerminalLoopResult Tick(CancellationToken cancellationToken)
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

    internal void RefreshModelProviderSelectorsForDraftScope(AgentBackendId? preferredBackendId = null)
        => _modelProviderSelectorCoordinator.RefreshForDraftScope(preferredBackendId);

    internal void RefreshModelProviderSelectorsForThread(OpenThreadState tab)
        => _modelProviderSelectorCoordinator.RefreshForThread(tab);
    internal void SyncModelProviderSelectorItems()
        => _threadWorkspaceView?.SyncModelProviderSelectorItems(_threadWorkspaceViewModel);
    private void OnModelProviderSelectionChanged(int newIndex)
        => ObserveUiTask(() => _modelProviderSelectorCoordinator.OnModelProviderSelectionChangedAsync(newIndex), "change the selected provider");
    private void OnModelSelectionChanged(int newIndex)
        => _modelProviderSelectorCoordinator.OnModelSelectionChanged(newIndex);
    private void OnReasoningSelectionChanged(int newIndex)
        => _modelProviderSelectorCoordinator.OnReasoningSelectionChanged(newIndex);
    private AgentBackendId GetPreferredModelProviderId()
        => _modelProviderSelectorCoordinator.GetPreferredModelProviderId();
    internal bool IsModelProviderReady(AgentBackendId backendId)
        => _modelProviderSelectorCoordinator.IsModelProviderReady(backendId);

    private bool TryGetPromptUnavailableStatus(out string message, out StatusTone tone)
        => _modelProviderSelectorCoordinator.TryGetPromptUnavailableStatus(out message, out tone);
    internal bool TrySetPromptUnavailableStatus() { if (!TryGetPromptUnavailableStatus(out var message, out var tone)) return false; SetStatus(message, tone: tone); return true; }
    internal void ApplyPromptAvailabilityProjection()
        => _modelProviderSelectorCoordinator.ApplyPromptAvailabilityProjection();

    internal void ApplyQueuedPromptProjection() => _threadPromptQueueCoordinator.RefreshSelectedThreadQueueUi();

    internal void UpdatePromptImageAttachmentsUi() { _promptComposerViewModel.PromptImageAttachmentVersion++; RefreshSidebarProjection(); }
    internal void SyncThreadTabControl()
        => _threadTabStripCoordinator.SyncControl();
    private void OnThreadTabControlSelectionChanged(int selectedIndex)
        => _threadTabStripCoordinator.OnSelectionChanged(selectedIndex);
    internal void ResetPendingThreadTabSelection()
        => _threadTabStripCoordinator.ResetPendingSelection();

    private CodeAltaShellView EnsureShellView()
    {
        if (_shellView is not null) return _shellView;

        var projectFileSearch = _ownedServices?.ProjectFileSearchService ?? NullProjectFileSearchService.Instance;
        var promptRoot = () => PromptReferenceProjectRootResolver.Resolve(GetSelectedThread(), GetProjectById, GetSelectedProject);
        var imageCallbacks = PromptImageWorkspaceCallbackFactory.Create(
            _promptDraftUiCoordinator,
            new PromptImageCapabilityContext(GetSelectedThread, threadId => _threadStateCoordinator.FindOpenThread(threadId), GetPreferredModelProviderId, _chatBackendStates),
            (message, tone) => SetStatus(message, tone: tone));
        var openHelp = () => ObserveUiTask(() => _shellCommandSurfaceCoordinator.ShowHelpAsync(), "show help");
        var showPalette = () => _shellCommandSurfaceCoordinator.ShowCommandPalette();
        var shellSurface = CodeAltaShellViewFactory.CreateSurface(new CodeAltaShellSurfaceOptions
        {
            ShellViewModel = _shellViewModel,
            WorkspaceViewModel = _threadWorkspaceViewModel,
            PromptComposerViewModel = _promptComposerViewModel,
            WorkspaceCommandBindings = _shellCommandSurfaceCoordinator.BuildWorkspaceCommandBindings(),
            WorkspaceChromeController = ThreadWorkspaceChromeController.Create(() => CreateUsageComputedVisual(EnsureSessionUsagePresenter().BuildIndicatorVisual), () => ShellPluginFooterComposer.ComposeRegion(_ownedServices?.PluginHostBridge, PluginUiRegion.ThreadStatus), anchor => EnsureThreadInfoPresenter().TogglePopup(anchor), () => ObserveUiTask(OpenModelProvidersAsync, "open model providers")),
            PromptComposerController = PromptComposerViewController.Create(acceptedPrompt => ObserveUiTask(() => _shellCommandSurfaceCoordinator.HandleAcceptedPromptAsync(acceptedPrompt), "submit the current prompt"), () => ObserveUiTask(() => _shellCommandSurfaceCoordinator.SubmitCurrentPromptAsync(steer: false), "submit the current prompt"), () => ObserveUiTask(() => _shellCommandSurfaceCoordinator.AbortSelectedThreadAsync(), "abort the selected thread"), openHelp, showPalette),
            QueuedPromptController = QueuedPromptStripController.Create(markdown => (_threadWorkspaceView?.ThreadPaneLayout.App)?.Terminal.Clipboard.TrySetText(markdown), queuedPromptId => ObserveUiTask(() => _threadCommandCoordinator.ConvertSelectedThreadQueuedPromptToSteerAsync(queuedPromptId), "convert the queued prompt to steer"), pendingSteerId => _threadCommandCoordinator.DeleteSelectedThreadPendingSteer(pendingSteerId), queuedPromptId => _threadCommandCoordinator.DeleteSelectedThreadQueuedPrompt(queuedPromptId), (queuedPromptId, remainingCount) => _threadCommandCoordinator.UpdateSelectedThreadQueuedPromptCount(queuedPromptId, remainingCount), (queuedPromptId, text) => _threadCommandCoordinator.UpdateSelectedThreadQueuedPromptText(queuedPromptId, text), (onAccepted, placeholder) => ThreadWorkspaceView.CreateStyledPromptEditor(onAccepted, openHelp, showPalette, projectFileSearch, promptRoot, placeholder)),
            ModelProviderSelectorController = ModelProviderSelectorController.Create(OnModelProviderSelectionChanged, OnModelSelectionChanged, OnReasoningSelectionChanged, () => ObserveUiTask(() => _shellCommandSurfaceCoordinator.CompactSelectedThreadAsync(), "compact the selected thread")),
            ThreadTabHostController = ThreadTabHostController.Create(selectedIndex => _threadTabStripCoordinator.ObserveBoundSelection(selectedIndex)),
            ProjectFileSearchService = projectFileSearch,
            GetPromptReferenceProjectRoot = promptRoot,
            PromptText = _promptDraftUiCoordinator.PromptTextBinding,
            ThinkingAnimationPhase01 = _shellAnimationRuntime.ThinkingPhase01,
            PromptImageCallbacks = imageCallbacks,
            Sidebar = _sidebarCoordinator.View.Root,
            ShellCommandSurfaceCoordinator = _shellCommandSurfaceCoordinator,
            OpenAcpManager = OpenAcpManagement,
            ToggleTerminalLoopCallback = ToggleTerminalLoopCallback,
            CanUseCommandPalette = () => _fileEditorWorkspaceCoordinator.SelectedTabId is null,
            ComposePluginFooter = commandBar => ShellPluginFooterComposer.Compose(commandBar, _ownedServices?.PluginHostBridge),
            CommandBarMultiLine = _commandBarMultiLine,
        });
        _threadWorkspaceView = shellSurface.WorkspaceView;
        _shellView = shellSurface.ShellView;
        _frontendEvents.Publish(new CatalogChangedEvent());
        return _shellView;
    }

    internal void ApplyShellChromeProjection() => _workspaceCoordinator.ApplyShellChromeProjection();

    internal void ApplyCatalogProjection() { _threadInfoPresenter?.InvalidateSelection(); _workspaceCoordinator.ApplyCatalogProjection(); }

    internal void PublishStartupCatalogProjectionReady()
        => _frontendEvents.Publish(new StartupCatalogProjectionReadyEvent());

    internal void ApplyHeaderProjection() => _workspaceCoordinator.ApplyHeaderProjection();

    internal void ApplySelectionProjection() { _threadInfoPresenter?.InvalidateSelection(); _workspaceCoordinator.ApplySelectionProjection(); }

    internal void SelectGlobalScope() { _threadStateCoordinator.SelectGlobalScope(); ActivateThreadSurface(); }

    internal void SelectProjectScope(string projectId) { _threadStateCoordinator.SelectProjectScope(projectId); ActivateThreadSurface(); }
    internal void EnsureSelectionDefaults() => _threadStateCoordinator.EnsureSelectionDefaults();
    internal void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info) => _workspaceCoordinator.SetStatus(message, showSpinner, tone);
    internal void SetProviderSessionLoadStatus(string? message) => _workspaceCoordinator.SetProviderSessionLoadStatus(message);
    internal void SetThreadStatus(
        OpenThreadState tab,
        string message,
        bool showSpinner = false,
        StatusTone tone = StatusTone.Info,
        bool hasCustomStatus = true)
        => _workspaceCoordinator.SetThreadStatus(tab, message, showSpinner, tone, hasCustomStatus);
    internal void ClearThreadStatus(OpenThreadState tab)
        => _workspaceCoordinator.ClearThreadStatus(tab);
    internal void ApplySessionUsageProjection() => _workspaceCoordinator.ApplySessionUsageProjection();
    internal void ApplyThreadChromeProjection() => _workspaceCoordinator.ApplyThreadChromeProjection();

    internal bool IsSelectedThread(string threadId)
        => !string.IsNullOrWhiteSpace(threadId) &&
           string.Equals(_threadStateCoordinator.Selection.SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase);

    internal void SetReadyStatusForCurrentSelection() => _workspaceCoordinator.SetReadyStatusForCurrentSelection();

    private void ObserveUiTask(Func<Task> taskFactory, string operation)
        => _ = UiTaskDiagnostics.ObserveAsync(taskFactory, operation, SetStatus);

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

    internal void DispatchToUi(Action action) { ArgumentNullException.ThrowIfNull(action); var dispatcher = GetUiDispatcher(); UiDispatch.Post(dispatcher, action, allowInline: ShouldRunInlineOnCurrentThread(dispatcher.CheckAccess(), _terminalLoopCoordinator.HasStarted)); }
    internal void DispatchToUiDeferred(Action action) { ArgumentNullException.ThrowIfNull(action); _deferredUiActionQueue.Enqueue(action); }

    internal static bool CanAccessBindableState(bool dispatcherHasAccess, bool terminalLoopStarted)
        => !terminalLoopStarted || dispatcherHasAccess;

    internal void VerifyBindableAccess()
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

    internal IUiDispatcher GetUiDispatcher()
        => _uiDispatcher;

    internal string? LoadPromptDraft(string threadId)
        => _promptDraftUiCoordinator!.LoadPromptDraft(threadId);

    internal void DeletePromptDraft(string threadId)
        => _promptDraftUiCoordinator!.DeletePersistedPromptDraft(threadId);

    internal void RemoveThreadTabPage(string threadId, ShellTabCloseReason reason)
    {
        _ = _shellTabService.CloseTabAsync(new ShellTabId(threadId), reason);
        _threadWorkspaceView?.RemoveTabPage(threadId);
    }

    internal void RekeyThreadIdentity(string oldThreadId, WorkThreadDescriptor thread)
        => _threadStateCoordinator.RekeyThreadIdentity(oldThreadId, thread);

    internal bool HasWorkspaceSurface()
        => _threadWorkspaceView is not null;

    internal void SyncPromptText(ThreadSessionState? session)
        => _promptDraftUiCoordinator!.SyncPromptText(session);

    internal void ClearDraftPromptText()
        => _promptDraftUiCoordinator!.ClearDraftPromptText();

    internal void ClearPromptText()
        => _promptDraftUiCoordinator!.ClearPrompt();

    internal bool IsPromptTextEmpty()
        => ReadBindableState(() => string.IsNullOrWhiteSpace(_promptDraftUiCoordinator!.PromptText) && !_promptDraftUiCoordinator.HasCurrentPromptImages);

    internal string? GetPromptText()
        => ReadBindableState(() => _promptDraftUiCoordinator!.PromptText);

    internal void RestorePromptText(string prompt)
        => DispatchToUi(() => _promptDraftUiCoordinator!.PromptText = prompt);

    internal IReadOnlyList<PromptImageAttachment> SnapshotPromptImages()
        => ReadBindableState(() => _promptDraftUiCoordinator!.SnapshotPromptImages());

    internal void RestorePromptImages(IReadOnlyList<PromptImageAttachment> images)
        => DispatchToUi(() => _promptDraftUiCoordinator!.RestorePromptImages(images));

    private ComputedVisual CreateComputedVisual(Func<Visual> build)
        => _workspaceCoordinator.CreateComputedVisual(build);

    private ComputedVisual CreateUsageComputedVisual(Func<Visual> build)
        => _workspaceCoordinator.CreateUsageComputedVisual(build);

    private async Task ActivateDraftTabAsync()
    {
        ResetPendingThreadTabSelection();
        _threadStateCoordinator.DraftTabOpen = true;
        _threadStateCoordinator.SelectedThreadId = null;
        _viewState.SelectedThreadId = null;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        ActivateThreadSurface();
        await PersistViewStateAsync();
        _frontendEvents.Publish(new SelectionChangedEvent());
    }

    private async Task CloseDraftTabAsync()
    {
        if (_threadStateCoordinator.Selection.Target is WorkspaceTarget.Draft)
        {
            _threadStateCoordinator.SelectedThreadId = _viewState.OpenThreadIds.FirstOrDefault();
            _viewState.SelectedThreadId = _threadStateCoordinator.Selection.SelectedThreadId;
        }

        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await _shellTabService.CloseTabAsync(new ShellTabId(DraftTabId), ShellTabCloseReason.UserDetached);
        ActivateThreadSurface();
        await PersistViewStateAsync();
        _frontendEvents.Publish(new SelectionChangedEvent());
    }

    private Task OpenFolderAsync(string folderPath, bool includeHidden)
        => _shellController.OpenFolderAsync(folderPath, includeHidden, CancellationToken.None);

    internal bool GetAutoApproveEnabled() => DefaultAutoApproveEnabled;
    internal async Task PersistViewStateAsync()
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

    internal Task RegisterCreatedThreadAsync(WorkThreadDescriptor thread)
        => _threadStateCoordinator.RegisterCreatedThreadAsync(thread);

    internal void OpenThread(string threadId)
    {
        if (_threadStateCoordinator.OpenThread(threadId) == OpenThreadResult.NotFound)
        {
            SetStatus($"Thread '{threadId}' was not found.", false, StatusTone.Warning);
            return;
        }

        ActivateThreadSurface();
    }
    internal void FocusPromptEditor() { ActivateThreadSurface(); ThreadPaneLayout?.App?.Focus(ThreadInput); }
    internal void FocusPromptTarget() => ThreadPaneLayout?.App?.Focus(ThreadInput);

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
    private async Task CloseThreadTabAsync(string threadId)
        => await _threadStateCoordinator.CloseThreadTabAsync(threadId);

    internal Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
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
        _threadTabStripCoordinator.SelectCurrentThreadSurfaceTab();
        SyncThreadTabControl();
        DispatchToUiDeferred(() => ThreadPaneLayout?.App?.Focus(ThreadInput));
    }

    private ProjectDescriptor? GetSelectedProject()
        => _threadStateCoordinator.GetSelectedProject();

    private ProjectDescriptor? GetProjectById(string? projectId)
        => _threadStateCoordinator.GetProjectById(projectId);

    private WorkThreadDescriptor? GetSelectedThread()
        => _threadStateCoordinator.GetSelectedThread();

}
