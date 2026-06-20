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
using CodeAlta.Presentation.Sessions;
using CodeAlta.Presentation.Usage;
using CodeAlta.Presentation.Workspace;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.ViewModels;
using XenoAtom.Logging;
using SessionView = CodeAlta.Catalog.SessionViewDescriptor;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Threading;

namespace CodeAlta.Views;

// CodeAltaApp intentionally remains the TUI shell composition root. Add behavior to named owners first.
internal sealed class CodeAltaApp : IAsyncDisposable, IShellFrontendHostLifecycle
{
    internal static readonly Logger UiLogger = LogManager.GetLogger("CodeAlta.UI");
    internal const string DraftTabId = "__draft__";
    private readonly ModelProviderPreferenceCoordinator _modelProviderPreferences;
    private readonly SessionRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly KnownProjectImporter _knownProjectImporter;
    private readonly CodeAltaOwnedServices? _ownedServices;
    private readonly CodeAltaShellController _shellController;
    private readonly RuntimeEventPump _runtimeEventPump;
    private readonly TerminalLoopCoordinator _terminalLoopCoordinator;
    private readonly ShellFrontendHost _frontendHost;
    private readonly FrontendEventPublisher _frontendEvents;
    private readonly ShellProjectionCoordinator _projectionCoordinator;
    private readonly ModelProviderInitializationCoordinator _modelProviderInitializationCoordinator;
    private readonly ShellSessionStateCoordinator _sessionStateCoordinator;
    private readonly ShellWorkspaceCoordinator _workspaceCoordinator;
    private readonly SessionHistoryCoordinator _sessionHistoryCoordinator;
    private readonly SessionRuntimeEventCoordinator _sessionRuntimeEventCoordinator;
    private readonly SessionPromptQueueCoordinator _sessionPromptQueueCoordinator;
    private readonly SessionCommandCoordinator _sessionCommandCoordinator;
    private readonly ShellCommandSurfaceCoordinator _shellCommandSurfaceCoordinator;
    private readonly SessionCreationCoordinator _sessionCreationCoordinator;
    private readonly PromptDraftUiCoordinator _promptDraftUiCoordinator;
    private readonly CodeAltaShellViewModel _shellViewModel;
    private readonly SidebarViewModel _sidebarViewModel;
    private readonly SessionWorkspaceViewModel _sessionWorkspaceViewModel;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly SessionUsageViewModel _sessionUsageViewModel;
    private readonly Dictionary<string, ModelProviderState> _modelProviderStates;
    private readonly SidebarCoordinator _sidebarCoordinator;
    private readonly NavigatorActionCoordinator _navigatorActionCoordinator;
    private readonly ModelProviderSelectorCoordinator _modelProviderSelectorCoordinator;
    private readonly AgentPromptSelectorCoordinator _agentPromptSelector;
    private readonly SessionTabStripCoordinator _sessionTabStripCoordinator;
    private readonly InMemoryShellTabService _shellTabService = new();
    private readonly ShellAnimationRuntime _shellAnimationRuntime = new();
    private readonly DeferredUiActionQueue _deferredUiActionQueue = new();
    private readonly ShellWorkspaceContext _shellWorkspaceContext;
    private readonly SessionSelectionContext _sessionSelectionContext;
    private readonly SessionTabContext _sessionTabContext;
    private readonly WorkspaceRefreshContext _workspaceRefreshContext;
    private readonly ProviderFrontendCoordinator _providerUi;
    private readonly ProviderDialogCoordinator _providerDialogCoordinator;
    private readonly PromptDialogCoordinator _promptDialogCoordinator;
    private readonly ReminderUiCoordinator _reminderUiCoordinator;
    private readonly FileEditorWorkspaceCoordinator _fileEditorWorkspaceCoordinator;
    private readonly InitialCatalogStateCoordinator _initialCatalogStateCoordinator;
    private readonly Action _openModels;
    private CodeAltaShellView? _shellView;
    private SessionWorkspaceView? _sessionWorkspaceView;
    private SessionUsagePresenter? _sessionUsagePresenter;
    private SessionInfoPresenter? _sessionInfoPresenter;
    private readonly IUiDispatcher _uiDispatcher = new TerminalUiDispatcher(Dispatcher.Current);
    private bool _disableTerminalLoopCallback;
    private bool _commandBarMultiLine;
    private bool _startupProviderDialogHandled;
    private SessionViewViewState _viewState
    {
        get => _sessionStateCoordinator.ViewState;
        set => _sessionStateCoordinator.ViewState = value;
    }
    internal Visual? SessionPaneLayout => _sessionWorkspaceView?.SessionPaneLayout;
    internal ChatPromptEditor? SessionInput => _sessionWorkspaceView?.SessionInput;
    private Visual GetDialogAnchor() => SessionInput is { } input ? input : _sidebarCoordinator.View.Tree;

    public CodeAltaApp(
        ProjectCatalog projectCatalog,
        SessionViewCatalog sessionCatalog,
        SessionRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub)
        : this(
            CodeAltaOwnedServices.CreateBuiltInProviderDescriptors(),
            projectCatalog,
            sessionCatalog,
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
    internal static CodeAltaApp Create(CodeAltaOwnedServices ownedServices, CodeAltaUpdateService? updateService = null)
    {
        ArgumentNullException.ThrowIfNull(ownedServices);
        return new(
            ownedServices.ProviderDescriptors,
            ownedServices.ProjectCatalog,
            ownedServices.SessionViewCatalog,
            ownedServices.RuntimeService,
            ownedServices.CatalogOptions,
            ownedServices.AgentHub,
            ownedServices.ProjectFileSearchService,
            new KnownProjectImporter(ownedServices.AgentSessionCatalog, ownedServices.ProjectCatalog),
            ownedServices,
            updateService);
    }

    private CodeAltaApp(IReadOnlyList<ModelProviderDescriptor> providerDescriptors,
        ProjectCatalog projectCatalog,
        SessionViewCatalog sessionCatalog,
        SessionRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub,
        IProjectFileSearchService projectFileSearchService,
        KnownProjectImporter? knownProjectImporter,
        CodeAltaOwnedServices? ownedServices,
        CodeAltaUpdateService? updateService = null)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(sessionCatalog);
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        _modelProviderPreferences = new ModelProviderPreferenceCoordinator(new CodeAltaConfigStore(catalogOptions), UiLogger);
        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _knownProjectImporter = knownProjectImporter ?? new KnownProjectImporter(
            new AgentSessionCatalog(sessionCatalog.JournalStore.CreateSessionStore()),
            projectCatalog);
        _ownedServices = ownedServices;
        _frontendHost = new ShellFrontendHost(this);
        var composition = CodeAltaFrontendComposition.Create(
            providerDescriptors,
            projectCatalog,
            sessionCatalog,
            runtimeService,
            catalogOptions,
            agentHub,
            projectFileSearchService,
            new CodeAltaShellBridge(this),
            _knownProjectImporter,
            this,
            ownedServices?.CurrentProject,
            ownedServices?.PluginHostBridge,
            ownedServices?.ModelProviderRegistry,
            ownedServices?.ProviderInit);
        _modelProviderPreferences = composition.ModelProviderPreferences;
        _shellController = composition.ShellController;
        _runtimeEventPump = composition.RuntimeEventPump;
        _terminalLoopCoordinator = composition.TerminalLoopCoordinator;
        _frontendEvents = composition.FrontendEvents;
        _shellTabService.SetFrontendEvents(_frontendEvents);
        _modelProviderInitializationCoordinator = composition.ModelProviderInitializationCoordinator;
        _sessionStateCoordinator = composition.SessionStateCoordinator;
        _workspaceCoordinator = composition.WorkspaceCoordinator;
        _sessionRuntimeEventCoordinator = composition.SessionRuntimeEventCoordinator;
        _sessionPromptQueueCoordinator = composition.SessionPromptQueueCoordinator;
        _sessionCommandCoordinator = composition.SessionCommandCoordinator;
        _sessionCreationCoordinator = composition.SessionCreationCoordinator;
        _promptDraftUiCoordinator = composition.PromptDraftUiCoordinator;
        _shellViewModel = composition.ShellViewModel;
        _sidebarViewModel = composition.SidebarViewModel;
        _sessionWorkspaceViewModel = composition.SessionWorkspaceViewModel;
        _promptComposerViewModel = composition.PromptComposerViewModel;
        _sessionUsageViewModel = composition.SessionUsageViewModel;
        _modelProviderStates = composition.ModelProviderStates;
        _sidebarCoordinator = composition.SidebarCoordinator;
        _navigatorActionCoordinator = composition.NavigatorActionCoordinator;
        _modelProviderSelectorCoordinator = composition.ModelProviderSelectorCoordinator;
        _agentPromptSelector = composition.AgentPromptSelectorCoordinator;
        _openModels = composition.ModelCatalogCoordinator.Open;
        _reminderUiCoordinator = composition.ReminderUiCoordinator;
        _projectionCoordinator = new ShellProjectionCoordinator(
            _frontendEvents,
            _workspaceCoordinator,
            _modelProviderSelectorCoordinator,
            _sessionPromptQueueCoordinator);
        _shellWorkspaceContext = composition.ShellWorkspaceContext;
        _sessionSelectionContext = composition.SessionSelectionContext;
        _workspaceRefreshContext = composition.WorkspaceRefreshContext;
        _initialCatalogStateCoordinator = new InitialCatalogStateCoordinator(
            cancellationToken => _sessionStateCoordinator.LoadInitialCatalogStateAsync(cancellationToken),
            _sessionStateCoordinator.ApplyInitialCatalogState,
            PublishStartupCatalogProjectionReady,
            FocusPromptEditor,
            SetStatus);
        _providerUi = new ProviderFrontendCoordinator(_ownedServices, _catalogOptions, _modelProviderInitializationCoordinator, _modelProviderStates, DispatchToUi, composition.FrontendEvents, SetStatus);
        _providerDialogCoordinator = new ProviderDialogCoordinator(
            _providerUi,
            () => DialogBoundsResolver.ResolveAppBounds(GetDialogAnchor()),
            GetDialogAnchor);
        _promptDialogCoordinator = new PromptDialogCoordinator(
            _catalogOptions,
            GetSelectedProject,
            GetDialogAnchor,
            () => { _agentPromptSelector.RefreshPrompts(); _sessionWorkspaceView?.SyncActivePromptPanelProjection(); },
            (message, tone) => SetStatus(message, tone: tone));
        _fileEditorWorkspaceCoordinator = new FileEditorWorkspaceCoordinator(
            projectFileSearchService,
            _shellTabService,
            ResolvePromptRoot,
            () => SessionInput,
            () => _sessionWorkspaceView,
            build => CreateComputedVisual(build),
            DispatchToUiDeferred,
            SyncSessionTabControl,
            SetStatus);
        _sessionTabContext = new SessionTabContext(
            new DelegatingSessionTabSurfacePort(
                () => _sessionWorkspaceView?.SessionTabControl,
                () => _sessionWorkspaceView,
                build => CreateComputedVisual(build),
                _uiDispatcher),
            new DelegatingSessionTabLifecyclePort(
                () => ObserveUiTask(ActivateDraftTabAsync, SR.T("activate the draft tab")),
                ActivateSessionSurface,
                sessionId => ObserveUiTask(() => CloseSessionTabAsync(sessionId), SR.T("close the session tab")),
                () => ObserveUiTask(CloseDraftTabAsync, SR.T("close the draft tab")),
                sessionId => ObserveUiTask(() => _shellController.OpenSessionAsync(sessionId, CancellationToken.None), SR.T("open the session tab"))),
            new DelegatingFileEditorTabPort(
                tabId => _fileEditorWorkspaceCoordinator.GetFileTab(tabId),
                tabId => _fileEditorWorkspaceCoordinator.SelectFileTab(tabId),
                tabId => ObserveUiTask(() => _fileEditorWorkspaceCoordinator.CloseFileTabAsync(tabId), SR.T("close the file tab"))));
        _sessionTabStripCoordinator = new SessionTabStripCoordinator(
            _sessionSelectionContext, _sessionTabContext, _shellTabService, _shellAnimationRuntime.WelcomePhase01, () => _promptDraftUiCoordinator.HasCurrentPromptDraft, _sessionRuntimeEventCoordinator.IsSessionRunning);
        composition.DraftTabReplacement.Bind(_sessionTabStripCoordinator.ReplaceDraftTabWithSession);
        var input = new DelegatingShellPromptInputService(() => ReadBindableState(() => _promptDraftUiCoordinator.PromptText), _sessionCommandCoordinator.IsCurrentPromptEmpty);
        var sessionSvc = new DelegatingShellSessionCommandService(GetSelectedSession, EnsureSessionTab);
        var dialogs = new DelegatingShellDialogCommandService(
            () => DialogBoundsResolver.ResolveAppBounds(SessionInput), () => SessionInput, () => _sessionStateCoordinator.Projects,
            OpenFolderAsync, OpenModelProvidersAsync, _providerDialogCoordinator.RefreshAsync, OpenPromptsAsync, () => new AboutDialog(() => DialogBoundsResolver.ResolveAppBounds(GetDialogAnchor()), GetDialogAnchor, _shellAnimationRuntime.WelcomePhase01, updateService).Show(), composition.ModelCatalogCoordinator.Open, _sidebarCoordinator.OpenLogs, _fileEditorWorkspaceCoordinator.ShowOpenFilePickerAsync,
            SkillsManagementCoordinatorFactory.Create(_ownedServices, _catalogOptions, GetSelectedProject, GetDialogAnchor, _fileEditorWorkspaceCoordinator.OpenFilePathAsync, _sessionCommandCoordinator.ActivateSelectedSkillAsync, SetStatus),
            PluginManagementCoordinatorFactory.Create(_catalogOptions, GetSelectedProject, GetDialogAnchor, _fileEditorWorkspaceCoordinator.OpenFilePathAsync),
            _sidebarCoordinator.OpenNavigatorSettings,
            () => EnsureSessionUsagePresenter().TogglePopupFromIndicator(),
            () => { if (SessionInput is not null) EnsureSessionInfoPresenter().TogglePopup(SessionInput); },
            () => _sessionWorkspaceView?.OpenExpandedPromptDialog(), ToggleCommandBarMultiLine, _reminderUiCoordinator.Open);
        var navigation = new DelegatingShellNavigationCommandService(
            FocusSidebar, FocusPromptEditor, FocusModelProviderSelector, () => SidebarUiStateHelpers.ToggleNavigator(_sidebarCoordinator.View, FocusPromptTarget),
            () => { _ = _sessionTabStripCoordinator.TrySelectRelativeTab(-1); return Task.CompletedTask; },
            () => { _ = _sessionTabStripCoordinator.TrySelectRelativeTab(1); return Task.CompletedTask; },
            () => ScrollSelectedSessionMessageAsync(static tab => tab.Timeline.ScrollToPreviousMessage()), () => ScrollSelectedSessionMessageAsync(static tab => tab.Timeline.ScrollToNextMessage()),
            () => ScrollSelectedSessionMessageAsync(static tab => tab.Timeline.ScrollToFirstMessage()), () => ScrollSelectedSessionMessageAsync(static tab => tab.Timeline.ScrollToLastMessage()));
        var tabs = new DelegatingShellTabCommandService(() => _sessionTabStripCoordinator.CloseSelectedTabAsync());
        var status = new DelegatingShellStatusService(SetStatus);
        var plugins = new PluginHostCommandService(_ownedServices?.PluginHostBridge);
        _shellCommandSurfaceCoordinator = ShellCommandSurfaceComposition.Create(_promptComposerViewModel, _sessionWorkspaceViewModel, _sessionCommandCoordinator, input, sessionSvc, dialogs, navigation, tabs, status, plugins, _agentPromptSelector, ToggleTerminalLoopCallback, () => SessionInput is not null, () => _sessionWorkspaceView?.SessionCommandBar.MultiLine ?? false);
        _sessionHistoryCoordinator = new SessionHistoryCoordinator(
            _runtimeService,
            EnsureSessionTab,
            _sessionStateCoordinator.FindSession,
            sessionId => _sessionStateCoordinator.FindOpenSession(sessionId),
            SessionHistoryCoordinator.CanLoadSessionHistory,
            _sessionCommandCoordinator.BuildExecutionOptions,
            (tab, message, showSpinner, tone) => SetSessionStatus(tab, message, showSpinner, tone),
            ClearSessionStatus,
            ResetSessionTab,
            _sessionRuntimeEventCoordinator.HandleAgentEventAsync,
            session => _sessionStateCoordinator.PersistSessionLocalStateAsync(session),
            tab => _frontendEvents.Publish(new SessionUsageChangedEvent(tab.SessionView.SessionId)),
            _sessionRuntimeEventCoordinator.ProjectLoadedHistory,
            DispatchToUiAsync);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
        => await _frontendHost.RunAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _frontendHost.DisposeAsync();

    async ValueTask IShellFrontendHostLifecycle.DisposeFrontendAsync()
    {
        _projectionCoordinator.Dispose();
        _reminderUiCoordinator.Dispose();
        await PersistViewStateAsync();
        await _fileEditorWorkspaceCoordinator.DisposeAsync();
        await _runtimeEventPump.DisposeAsync();
        await _shellController.DisposeAsync();
        await _promptDraftUiCoordinator.DisposeAsync();
        if (_ownedServices is not null) await _ownedServices.DisposeAsync();
    }

    private string? GetDraftProjectRoot()
        => _sessionStateCoordinator.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true }
            ? null
            : GetSelectedProject()?.ProjectPath;

    private string? GetDraftProjectId()
        => _sessionStateCoordinator.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true }
            ? null
            : GetSelectedProject()?.Id;

    private string? GetSessionProjectRoot(SessionView session)
        => GetProjectById(session.ProjectRef)?.ProjectPath;


    internal void ApplyDraftModelProviderPreference(ModelProviderState providerState)
        => _modelProviderPreferences.ApplyDraftModelProviderPreference(providerState, _viewState, GetDraftProjectRoot(), GetDraftProjectId());

    internal void ApplySessionPreference(OpenSessionState tab)
        => _modelProviderPreferences.ApplySessionPreference(tab, _viewState, GetSessionProjectRoot(tab.SessionView), _modelProviderStates);

    internal void RememberGlobalModelProviderPreference(
        ModelProviderId providerId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort)
    {
        _modelProviderPreferences.RememberGlobalModelProviderPreference(
            _viewState,
            providerId,
            modelId,
            reasoningEffort,
            GetDraftProjectRoot(),
            GetDraftProjectId(),
            _sessionStateCoordinator.Selection.Target is WorkspaceTarget.Draft);
        _ = PersistViewStateAsync();
    }

    internal void RememberSessionPreference(
        string sessionId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort,
        bool persistNow)
    {
        _modelProviderPreferences.RememberSessionPreference(_viewState, sessionId, modelId, reasoningEffort);
        var tab = _sessionStateCoordinator.FindOpenSession(sessionId);
        var session = tab?.SessionView ?? _sessionStateCoordinator.FindSession(sessionId);
        if (session is not null)
        {
            var providerKey = tab?.ProviderId.Value ?? session.ResolvedProviderKey;
            if (!string.IsNullOrWhiteSpace(providerKey))
            {
                session.ProviderKey = providerKey;
                session.ProviderId = providerKey;
            }

            session.ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
            session.ReasoningEffort = reasoningEffort;
            if (persistNow)
            {
                _ = _sessionStateCoordinator.PersistSessionLocalStateAsync(session);
            }
        }

        if (persistNow)
        {
            _ = PersistViewStateAsync();
        }
    }

    internal void RefreshSidebarProjection()
        => SidebarUiStateHelpers.RefreshProjection(_sidebarCoordinator, _sessionStateCoordinator, _promptDraftUiCoordinator, sessionId => _sessionStateCoordinator.FindOpenSession(sessionId), _sessionRuntimeEventCoordinator.IsSessionRunning, _reminderUiCoordinator.HasActiveReminder, VerifyBindableAccess);

    internal void SyncSidebarSelectionToCurrentState()
        => _sidebarCoordinator.SyncSelectionToCurrentState(SidebarUiStateHelpers.ResolveCurrentTarget(_sessionStateCoordinator));

    internal void ApplyPendingSidebarSelection()
        => _sidebarCoordinator.ApplyPendingSelection();

    public void PrepareForRun()
    {
        StartupNavigatorSettingsApplier.Apply(_sessionStateCoordinator, UiLogger);
        SetStatus(SR.T("Connecting providers..."), showSpinner: true);
    }
    public Visual GetRoot() => EnsureShellView().Root;

    public TerminalLoopResult Tick(CancellationToken cancellationToken)
    {
        _shellController.DrainPendingRuntimeEventsForUiFrame();
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
                ? SR.T("Loop callback disabled.")
                : SR.T("Loop callback enabled."),
            tone: _disableTerminalLoopCallback ? StatusTone.Warning : StatusTone.Info);
    }

    private void ToggleCommandBarMultiLine()
    {
        _commandBarMultiLine = !_commandBarMultiLine;
        if (_sessionWorkspaceView?.SessionCommandBar is { } commandBar) commandBar.MultiLine = _commandBarMultiLine;

        SetStatus(
            _commandBarMultiLine
                ? SR.T("Command bar expanded.")
                : SR.T("Command bar collapsed."),
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
            SetStatus(SR.T("No model providers are enabled. Open Model Providers (Ctrl+G Ctrl+R) to configure one."), false, StatusTone.Warning);
            _ = OpenModelProvidersAsync();
        }

        return true;
    }

    private string? ResolvePromptRoot()
        => PromptReferenceProjectRootResolver.Resolve(GetSelectedSession(), GetProjectById, GetSelectedProject, _catalogOptions.GlobalRoot);

    internal void RefreshModelProviderSelectorsForDraftScope(ModelProviderId? id = null)
    {
        _agentPromptSelector.RefreshForDraftScope();
        _modelProviderSelectorCoordinator.RefreshForDraftScope(id);
    }

    internal void RefreshModelProviderSelectorsForSession(OpenSessionState tab)
    {
        _agentPromptSelector.RefreshForSession(tab);
        _modelProviderSelectorCoordinator.RefreshForSession(tab);
    }

    internal void SyncModelProviderSelectorItems() => _sessionWorkspaceView?.SyncModelProviderSelectorItems(_sessionWorkspaceViewModel);
    internal void SyncAgentPromptSelectorItems() => _sessionWorkspaceView?.SyncAgentPromptSelectorItems(_sessionWorkspaceViewModel);
    private void OnAgentPromptSelectionChanged(int newIndex) => _agentPromptSelector.OnAgentPromptSelectionChanged(newIndex);
    private void OnModelProviderSelectionChanged(int newIndex) => ObserveUiTask(() => _modelProviderSelectorCoordinator.OnModelProviderSelectionChangedAsync(newIndex), SR.T("change the selected provider"));
    private void OnModelSelectionChanged(int newIndex) => _modelProviderSelectorCoordinator.OnModelSelectionChanged(newIndex);
    private void OnReasoningSelectionChanged(int newIndex) => _modelProviderSelectorCoordinator.OnReasoningSelectionChanged(newIndex);
    private ModelProviderId GetPreferredProviderId() => _modelProviderSelectorCoordinator.GetPreferredModelProviderId();
    internal bool IsModelProviderReady(ModelProviderId providerId) => _modelProviderSelectorCoordinator.IsModelProviderReady(providerId);

    private bool TryGetPromptUnavailableStatus(out string message, out StatusTone tone)
        => _modelProviderSelectorCoordinator.TryGetPromptUnavailableStatus(out message, out tone);
    internal bool TrySetPromptUnavailableStatus() { if (!TryGetPromptUnavailableStatus(out var message, out var tone)) return false; SetStatus(message, tone: tone); return true; }
    internal void ApplyPromptAvailabilityProjection() => _modelProviderSelectorCoordinator.ApplyPromptAvailabilityProjection();

    internal void ApplyQueuedPromptProjection() => _sessionPromptQueueCoordinator.RefreshSelectedSessionQueueUi();

    internal void UpdatePromptImageAttachmentsUi() { _promptComposerViewModel.PromptImageAttachmentVersion++; _sessionWorkspaceView?.RefreshActivePromptImages(); RefreshSidebarProjection(); }
    internal void SyncActivePromptPanelProjection() => _sessionWorkspaceView?.SyncActivePromptPanelProjection();
    internal bool GetAlwaysEnqueue() => _sessionWorkspaceView?.AlwaysEnqueue ?? _promptComposerViewModel.AlwaysEnqueue;
    internal void SyncSessionTabControl() => _sessionTabStripCoordinator.SyncControl();
    private void OnSessionTabControlSelectionChanged(int selectedIndex) => _sessionTabStripCoordinator.OnSelectionChanged(selectedIndex);
    internal void ResetPendingSessionTabSelection() => _sessionTabStripCoordinator.ResetPendingSelection();

    private CodeAltaShellView EnsureShellView()
    {
        if (_shellView is not null) return _shellView;

        _sidebarCoordinator.RefreshLocalizedText();
        var pfs = _ownedServices?.ProjectFileSearchService ?? NullProjectFileSearchService.Instance;
        Func<string?> promptRoot = ResolvePromptRoot;
        var pb = _ownedServices?.PluginHostBridge;
        var pec = pb?.GetPromptEditorContributions() ?? [];
        var getPromptComposerSession = PromptComposerSessionBindingFactory.Create(_promptDraftUiCoordinator, new PromptImageCapabilityContext(GetSelectedSession, _sessionStateCoordinator.FindOpenSession, GetPreferredProviderId, _modelProviderStates), (message, tone) => SetStatus(message, tone: tone));
        var openHelp = () => ObserveUiTask(() => _shellCommandSurfaceCoordinator.ShowHelpAsync(), SR.T("show help"));
        var showPalette = () => _shellCommandSurfaceCoordinator.ShowCommandPalette();
        var shellSurface = CodeAltaShellViewFactory.CreateSurface(new CodeAltaShellSurfaceOptions
        {
            ShellViewModel = _shellViewModel,
            WorkspaceViewModel = _sessionWorkspaceViewModel,
            PromptComposerViewModel = _promptComposerViewModel,
            WorkspaceChromeController = SessionWorkspaceChromeController.Create(() => CreateUsageComputedVisual(EnsureSessionUsagePresenter().BuildIndicatorVisual), () => ShellPluginFooterComposer.ComposeRegion(pb, PluginUiRegion.SessionStatus, GetSelectedSession()?.SessionId), anchor => EnsureSessionInfoPresenter().TogglePopup(anchor), () => ObserveUiTask(OpenModelProvidersAsync, SR.T("open model providers")), _reminderUiCoordinator.GetSelectedSessionReminderCount, _reminderUiCoordinator.Open),
            PromptComposerController = PromptComposerViewController.Create(acceptedPrompt => ObserveUiTask(() => _shellCommandSurfaceCoordinator.HandleAcceptedPromptAsync(acceptedPrompt), SR.T("submit the current prompt")), () => ObserveUiTask(() => _shellCommandSurfaceCoordinator.SubmitCurrentPromptAsync(steer: false), SR.T("submit the current prompt")), () => ObserveUiTask(() => _shellCommandSurfaceCoordinator.AbortSelectedSessionAsync(), SR.T("abort the selected session")), openHelp, showPalette),
            QueuedPromptController = QueuedPromptStripController.Create(markdown => (_sessionWorkspaceView?.SessionPaneLayout.App)?.Terminal.Clipboard.TrySetText(markdown), queuedPromptId => ObserveUiTask(() => _sessionCommandCoordinator.ConvertSelectedSessionQueuedPromptToSteerAsync(queuedPromptId), SR.T("convert the queued prompt to steer")), pendingSteerId => _sessionCommandCoordinator.DeleteSelectedSessionPendingSteer(pendingSteerId), queuedPromptId => _sessionCommandCoordinator.DeleteSelectedSessionQueuedPrompt(queuedPromptId), (queuedPromptId, remainingCount) => _sessionCommandCoordinator.UpdateSelectedSessionQueuedPromptCount(queuedPromptId, remainingCount), (queuedPromptId, text) => _sessionCommandCoordinator.UpdateSelectedSessionQueuedPromptText(queuedPromptId, text), (onAccepted, placeholder) => SessionWorkspaceView.CreateStyledPromptEditor(onAccepted, openHelp, showPalette, pfs, promptRoot, pec, placeholder)),
            AgentPromptSelectorController = AgentPromptSelectorController.Create(OnAgentPromptSelectionChanged, () => ObserveUiTask(OpenPromptsAsync, SR.T("open prompts"))),
            ModelProviderSelectorController = ModelProviderSelectorController.Create(OnModelProviderSelectionChanged, OnModelSelectionChanged, OnReasoningSelectionChanged, () => ObserveUiTask(() => _shellCommandSurfaceCoordinator.CompactSelectedSessionAsync(), SR.T("compact the selected session")), _openModels),
            SessionTabHostController = SessionTabHostController.Create(selectedIndex => _sessionTabStripCoordinator.ObserveBoundSelection(selectedIndex)),
            ProjectFileSearchService = pfs,
            GetPromptReferenceProjectRoot = promptRoot,
            PromptEditorContributions = pec,
            GetPromptComposerSession = getPromptComposerSession,
            ThinkingAnimationPhase01 = _shellAnimationRuntime.ThinkingPhase01,
            Sidebar = _sidebarCoordinator.View.Root,
            ShellCommandSurfaceCoordinator = _shellCommandSurfaceCoordinator,
            ComposePluginFooter = commandBar => ShellPluginFooterComposer.Compose(commandBar, pb),
            CommandBarMultiLine = _commandBarMultiLine,
        });
        _sessionWorkspaceView = shellSurface.WorkspaceView;
        _sidebarCoordinator.View.CollapsedChanged += shellSurface.ShellView.SetSidebarCollapsed;
        shellSurface.ShellView.SetSidebarCollapsed(_sidebarCoordinator.View.IsCollapsed);
        _shellView = UiTheme.Set(shellSurface.ShellView, _sessionStateCoordinator);
        _frontendEvents.Publish(new CatalogChangedEvent());
        return _shellView;
    }

    internal void ApplyShellChromeProjection() => _workspaceCoordinator.ApplyShellChromeProjection();

    internal void ApplyCatalogProjection() { _sessionInfoPresenter?.InvalidateSelection(); _workspaceCoordinator.ApplyCatalogProjection(); }

    internal void PublishStartupCatalogProjectionReady()
        => _frontendEvents.Publish(new StartupCatalogProjectionReadyEvent());

    internal void ApplyHeaderProjection() => _workspaceCoordinator.ApplyHeaderProjection();

    internal void ApplySelectionProjection() { _sessionInfoPresenter?.InvalidateSelection(); _workspaceCoordinator.ApplySelectionProjection(); }

    internal void SelectGlobalScope() { _sessionStateCoordinator.SelectGlobalScope(); ActivateSessionSurface(); }

    internal void SelectProjectScope(string projectId) { _sessionStateCoordinator.SelectProjectScope(projectId); ActivateSessionSurface(); }
    internal void EnsureSelectionDefaults() => _sessionStateCoordinator.EnsureSelectionDefaults();
    internal void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info) => _workspaceCoordinator.SetStatus(message, showSpinner, tone);
    internal void SetProviderSessionLoadStatus(string? message) => _workspaceCoordinator.SetProviderSessionLoadStatus(message);
    internal void SetSessionStatus(OpenSessionState tab, string message, bool showSpinner = false, StatusTone tone = StatusTone.Info, bool hasCustomStatus = true) => _workspaceCoordinator.SetSessionStatus(tab, message, showSpinner, tone, hasCustomStatus);
    internal void ClearSessionStatus(OpenSessionState tab) => _workspaceCoordinator.ClearSessionStatus(tab);
    internal void ApplySessionUsageProjection() => _workspaceCoordinator.ApplySessionUsageProjection();
    internal void ApplySessionChromeProjection() => _workspaceCoordinator.ApplySessionChromeProjection();

    internal bool IsSelectedSession(string sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) &&
           string.Equals(_sessionStateCoordinator.Selection.SelectedSessionId, sessionId, StringComparison.OrdinalIgnoreCase);

    internal void SetReadyStatusForCurrentSelection() => _workspaceCoordinator.SetReadyStatusForCurrentSelection();

    private void ObserveUiTask(Func<Task> taskFactory, string operation)
        => _ = UiTaskDiagnostics.ObserveAsync(taskFactory, operation, SetStatus);

    private SessionUsagePresenter EnsureSessionUsagePresenter()
        => _sessionUsagePresenter ??= PopupPresenterFactory.CreateSessionUsagePresenter(
            _sessionUsageViewModel,
            () => SessionPaneLayout?.App,
            () => SessionInput,
            build => CreateUsageComputedVisual(build));

    private SessionInfoPresenter EnsureSessionInfoPresenter()
        => _sessionInfoPresenter ??= PopupPresenterFactory.CreateSessionInfoPresenter(
            () => SessionPaneLayout?.App,
            () => SessionInput,
            new SessionInfoService(_ownedServices!.AgentSessionCatalog, _sessionSelectionContext, _modelProviderStates),
            DispatchToUi,
            build => CreateComputedVisual(build));

    private T ReadBindableState<T>(Func<T> read) { ArgumentNullException.ThrowIfNull(read); return UiDispatch.Invoke(GetUiDispatcher(), () => { VerifyBindableAccess(); return read(); }); }

    internal void SetShellInitialized(bool isInitialized)
        => _workspaceCoordinator.SetShellInitialized(isInitialized);

    internal void DispatchToUi(Action action) { ArgumentNullException.ThrowIfNull(action); var dispatcher = GetUiDispatcher(); UiDispatch.Post(dispatcher, action, allowInline: ShouldRunInlineOnCurrentSession(dispatcher.CheckAccess(), _terminalLoopCoordinator.HasStarted)); }
    internal Task DispatchToUiAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = GetUiDispatcher();
        return UiDispatch.InvokeAsync(dispatcher, action, ShouldRunInlineOnCurrentSession(dispatcher.CheckAccess(), _terminalLoopCoordinator.HasStarted));
    }

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

        throw new InvalidOperationException("Bindable state must be accessed on the UI thread.");
    }

    internal static bool ShouldRunInlineOnCurrentSession(bool dispatcherHasAccess, bool terminalLoopStarted)
        => !terminalLoopStarted || dispatcherHasAccess;

    internal static bool ShouldRunDeferredUiActionInlineOnCurrentSession(bool dispatcherHasAccess, bool terminalLoopStarted)
        => !terminalLoopStarted && dispatcherHasAccess;

    private void DrainDeferredUiActions()
        => _deferredUiActionQueue.Drain();

    internal IUiDispatcher GetUiDispatcher()
        => _uiDispatcher;

    internal string? LoadPromptDraft(string sessionId)
        => _promptDraftUiCoordinator!.LoadPromptDraft(sessionId);

    internal void DeletePromptDraft(string sessionId)
        => _promptDraftUiCoordinator!.DeletePersistedPromptDraft(sessionId);

    internal void RemoveSessionTabPage(string sessionId, ShellTabCloseReason reason)
    {
        _ = _shellTabService.CloseTabAsync(new ShellTabId(sessionId), reason);
        _sessionWorkspaceView?.RemoveTabPage(sessionId);
    }

    internal IReadOnlyList<ShellTabSnapshot> GetShellTabs() => _shellTabService.GetTabs();

    internal void RekeySessionIdentity(string oldSessionId, SessionView session)
        => _sessionStateCoordinator.RekeySessionIdentity(oldSessionId, session);

    internal bool HasWorkspaceSurface()
        => _sessionWorkspaceView is not null;

    internal void SyncPromptText(SessionState? session)
        => _promptDraftUiCoordinator!.SyncPromptText(session);

    internal void ClearDraftPromptText()
        => _promptDraftUiCoordinator!.ClearDraftPromptText();

    internal void ClearPromptText()
        => _promptDraftUiCoordinator!.ClearPrompt();

    internal bool IsPromptTextEmpty()
        => ReadBindableState(() => string.IsNullOrWhiteSpace(_promptDraftUiCoordinator!.PromptText) && !_promptDraftUiCoordinator.HasCurrentPromptImages);

    internal bool HasCurrentPromptDraft()
        => ReadBindableState(() => _promptDraftUiCoordinator!.HasCurrentPromptDraft);

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
        ResetPendingSessionTabSelection();
        _sessionStateCoordinator.DraftTabOpen = true;
        _sessionStateCoordinator.SelectedSessionId = null;
        _viewState.SelectedSessionId = null;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        ActivateSessionSurface();
        await PersistViewStateAsync();
        _frontendEvents.Publish(new SelectionChangedEvent());
    }

    private async Task CloseDraftTabAsync()
    {
        if (_sessionStateCoordinator.Selection.Target is WorkspaceTarget.Draft)
        {
            _sessionStateCoordinator.SelectedSessionId = _viewState.OpenSessionIds.FirstOrDefault();
            _viewState.SelectedSessionId = _sessionStateCoordinator.Selection.SelectedSessionId;
        }

        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await _shellTabService.CloseTabAsync(new ShellTabId(DraftTabId), ShellTabCloseReason.UserDetached);
        ActivateSessionSurface();
        await PersistViewStateAsync();
        _frontendEvents.Publish(new SelectionChangedEvent());
    }

    private Task OpenFolderAsync(string folderPath, bool includeHidden)
        => _shellController.OpenFolderAsync(folderPath, includeHidden, CancellationToken.None);

    internal async Task PersistViewStateAsync()
        => await _sessionStateCoordinator.PersistViewStateAsync();
    internal Task InitializeModelProvidersAsync(CancellationToken cancellationToken)
        => _modelProviderInitializationCoordinator.InitializeAsync(cancellationToken);
    internal Task InitializeModelProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken)
        => _modelProviderInitializationCoordinator.RefreshProviderAsync(providerId, cancellationToken);

    internal void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<SessionView> sessions,
        bool pruneMissingSessions = true)
        => _sessionStateCoordinator.ApplyRecoveredCatalogState(projects, sessions, pruneMissingSessions);

    internal void UpsertProject(ProjectDescriptor project)
        => _sessionStateCoordinator.UpsertProject(project);

    internal void TrySchedulePendingStartupSessionRestore(CancellationToken cancellationToken)
        => _sessionStateCoordinator.TrySchedulePendingStartupSessionRestore(cancellationToken);

    private async Task RestoreStartupSessionHistoryAsync(string? sessionId, CancellationToken cancellationToken)
        => await _sessionStateCoordinator.RestoreStartupSessionHistoryAsync(sessionId, cancellationToken);

    internal Task RegisterCreatedSessionAsync(SessionView session)
        => _sessionStateCoordinator.RegisterCreatedSessionAsync(session);

    internal void OpenSession(string sessionId)
    {
        if (_sessionStateCoordinator.OpenSession(sessionId) == OpenSessionResult.NotFound)
        {
            SetStatus(SR.T("Session '{0}' was not found.", sessionId), false, StatusTone.Warning);
            return;
        }

        ActivateSessionSurface();
    }
    internal void FocusPromptEditor() { ActivateSessionSurface(); SessionPaneLayout?.App?.Focus(SessionInput); }
    internal void FocusPromptTarget() => SessionPaneLayout?.App?.Focus(SessionInput);
    internal void FocusModelProviderSelector() { ActivateSessionSurface(); DispatchToUiDeferred(() => _sessionWorkspaceView?.FocusModelProviderSelector()); }

    internal void FocusReasoningSelector() { ActivateSessionSurface(); DispatchToUiDeferred(() => _sessionWorkspaceView?.FocusReasoningSelector()); }

    private Task ScrollSelectedSessionMessageAsync(Action<OpenSessionState> scroll)
    {
        ArgumentNullException.ThrowIfNull(scroll);

        if (_fileEditorWorkspaceCoordinator.SelectedTabId is not null ||
            _sessionStateCoordinator.Selection.Target is not WorkspaceTarget.Session ||
            GetSelectedSession() is not { } session)
        {
            SetStatus(SR.T("Open a session tab before navigating messages."), false, StatusTone.Warning);
            return Task.CompletedTask;
        }

        var tab = EnsureSessionTab(session);
        if (!tab.Timeline.HasNavigableMessages)
        {
            SetStatus(SR.T("No user or assistant messages to navigate in this session."), false, StatusTone.Info);
            return Task.CompletedTask;
        }

        scroll(tab);
        return Task.CompletedTask;
    }

    internal Task OpenModelProvidersAsync() => _providerDialogCoordinator.OpenAsync();

    internal Task OpenPromptsAsync() => _promptDialogCoordinator.OpenAsync();

    internal void FocusSidebar() { SyncSidebarSelectionToCurrentState(); ApplyPendingSidebarSelection(); _sidebarCoordinator.View.Tree.App?.Focus(_sidebarCoordinator.View.Tree); }
    private async Task CloseSessionTabAsync(string sessionId)
        => await _sessionStateCoordinator.CloseSessionTabAsync(sessionId);

    internal Task EnsureSessionHistoryLoadedAsync(SessionView session, CancellationToken cancellationToken = default)
        => _sessionHistoryCoordinator.EnsureLoadedAsync(session, cancellationToken);

    internal void HandleRuntimeEvent(SessionRuntimeEvent runtimeEvent)
        => _sessionRuntimeEventCoordinator.ApplyRuntimeEvent(runtimeEvent);

    private OpenSessionState EnsureSessionTab(SessionView session)
        => _sessionStateCoordinator.EnsureSessionTab(session);

    private void ResetSessionTab(OpenSessionState tab)
        => _sessionStateCoordinator.ResetSessionTab(tab);

    private void ActivateSessionSurface()
    {
        _fileEditorWorkspaceCoordinator.ActivateSessionSurface();
        _sessionTabStripCoordinator.SelectCurrentSessionSurfaceTab();
        SyncSessionTabControl();
        DispatchToUiDeferred(() => SessionPaneLayout?.App?.Focus(SessionInput));
    }

    private ProjectDescriptor? GetSelectedProject()
        => _sessionStateCoordinator.GetSelectedProject();

    private ProjectDescriptor? GetProjectById(string? projectId)
        => _sessionStateCoordinator.GetProjectById(projectId);

    private SessionView? GetSelectedSession()
        => _sessionStateCoordinator.GetSelectedSession();

}
