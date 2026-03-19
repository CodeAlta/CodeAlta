using System.ComponentModel;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.ViewModels;
using XenoAtom.Ansi;
using XenoAtom.Logging;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Figlet;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;
using XenoAtom.Terminal.UI.Threading;

internal sealed class CodeAltaApp : IAsyncDisposable
{
    internal static readonly Logger UiLogger = LogManager.GetLogger("CodeAlta.UI");
    private const int MaxRecentThreadsPerProject = 3;
    internal const string DraftTabId = "__draft__";
    private const bool DefaultAutoApproveEnabled = true;

    private readonly ProjectCatalog _projectCatalog;
    private readonly WorkThreadCatalog _threadCatalog;
    private readonly ChatBackendPreferenceCoordinator _backendPreferences;
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly AgentHub _agentHub;
    private readonly KnownProjectImporter _knownProjectImporter;
    private readonly CodeAltaOwnedServices? _ownedServices;
    private readonly CodeAltaShellController _shellController;
    private readonly RuntimeEventPump _runtimeEventPump;
    private readonly ThreadHistoryCoordinator _threadHistoryCoordinator;
    private readonly CodeAltaShellViewModel _shellViewModel = new();
    private readonly SidebarViewModel _sidebarViewModel = new();
    private readonly ThreadWorkspaceViewModel _threadWorkspaceViewModel = new();
    private readonly PromptComposerViewModel _promptComposerViewModel = new();
    private readonly SessionUsageViewModel _sessionUsageViewModel = new();
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates = ChatBackendPresentation.CreateBackendStates();
    private readonly Dictionary<string, OpenThreadState> _threadTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly State<int> _viewRefreshState = new(0);
    private readonly State<int> _usageRefreshState = new(0);
    private readonly ShellSelectionState _selection = new();
    private readonly SidebarCoordinator _sidebarCoordinator;
    private readonly ChatSelectorCoordinator _chatSelectorCoordinator;
    private readonly ThreadTabStripCoordinator _threadTabStripCoordinator;

    private IReadOnlyList<ProjectDescriptor> _projects = [];
    private IReadOnlyList<WorkThreadDescriptor> _threads = [];
    private CodeAltaShellView? _shellView;
    private ThreadWorkspaceView? _threadWorkspaceView;
    private SessionUsagePresenter? _sessionUsagePresenter;
    private IUiDispatcher? _uiDispatcher;
    private bool _terminalLoopStarted;

    private WorkThreadViewState _viewState
    {
        get => _selection.ViewState;
        set => _selection.ViewState = value;
    }

    private bool _draftTabOpen
    {
        get => _selection.DraftTabOpen;
        set => _selection.DraftTabOpen = value;
    }

    private bool _globalScopeSelected
    {
        get => _selection.GlobalScopeSelected;
        set => _selection.GlobalScopeSelected = value;
    }

    private string? _selectedProjectId
    {
        get => _selection.SelectedProjectId;
        set => _selection.SelectedProjectId = value;
    }

    private string? _selectedThreadId
    {
        get => _selection.SelectedThreadId;
        set => _selection.SelectedThreadId = value;
    }

    private string? _pendingStartupThreadRestoreId
    {
        get => _selection.PendingStartupThreadRestoreId;
        set => _selection.PendingStartupThreadRestoreId = value;
    }

    private Visual? ThreadPaneLayout => _threadWorkspaceView?.ThreadPaneLayout;

    private VSplitter? ThreadBodySplitter => _threadWorkspaceView?.ThreadBodySplitter;

    private ChatPromptEditor? ThreadInput => _threadWorkspaceView?.ThreadInput;

    private CommandBar? ThreadCommandBar => _threadWorkspaceView?.ThreadCommandBar;

    private Select<ChatBackendOption>? ChatBackendSelect => _threadWorkspaceView?.ChatBackendSelect;

    private Select<ChatModelOption>? ChatModelSelect => _threadWorkspaceView?.ChatModelSelect;

    private Select<ChatReasoningOption>? ChatReasoningSelect => _threadWorkspaceView?.ChatReasoningSelect;

    private CheckBox? ChatAutoScrollCheckBox => _threadWorkspaceView?.ChatAutoScrollCheckBox;

    private TabControl? ThreadTabControl => _threadWorkspaceView?.ThreadTabControl;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeAltaApp"/> class.
    /// </summary>
    public CodeAltaApp(
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub)
        : this(
            projectCatalog,
            threadCatalog,
            runtimeService,
            catalogOptions,
            agentHub,
            knownProjectImporter: null,
            ownedServices: null)
    {
    }

    public static async Task<CodeAltaApp> CreateAsync(CancellationToken cancellationToken)
    {
        var ownedServices = await CodeAltaOwnedServices.CreateAsync(cancellationToken).ConfigureAwait(false);
        return new CodeAltaApp(
            ownedServices.ProjectCatalog,
            ownedServices.ThreadCatalog,
            ownedServices.RuntimeService,
            ownedServices.CatalogOptions,
            ownedServices.AgentHub,
            new KnownProjectImporter(ownedServices.AgentHub, ownedServices.ProjectCatalog),
            ownedServices);
    }

    private CodeAltaApp(
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub,
        KnownProjectImporter? knownProjectImporter,
        CodeAltaOwnedServices? ownedServices)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(agentHub);

        _projectCatalog = projectCatalog;
        _threadCatalog = threadCatalog;
        _backendPreferences = new ChatBackendPreferenceCoordinator(new CodeAltaConfigStore(catalogOptions), UiLogger);
        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _agentHub = agentHub;
        _knownProjectImporter = knownProjectImporter ?? new KnownProjectImporter(agentHub, projectCatalog);
        _ownedServices = ownedServices;
        _shellController = new CodeAltaShellController(
            new CodeAltaShellBridge(this),
            _knownProjectImporter,
            new ProjectCatalogLoader(_projectCatalog),
            new RecoverableThreadSource(_runtimeService));
        _runtimeEventPump = new RuntimeEventPump(_runtimeService, _shellController);
        _sidebarCoordinator = new SidebarCoordinator(
            _sidebarViewModel,
            _catalogOptions,
            _shellController,
            MaxRecentThreadsPerProject);
        _chatSelectorCoordinator = new ChatSelectorCoordinator(
            _threadWorkspaceViewModel,
            _promptComposerViewModel,
            _chatBackendStates,
            () => ChatBackendSelect,
            () => ChatModelSelect,
            () => ChatReasoningSelect,
            () => ChatAutoScrollCheckBox,
            GetSelectedThread,
            GetSelectedProject,
            thread => EnsureThreadTab(thread),
            () => _globalScopeSelected,
            () => _draftTabOpen,
            () => _selectedThreadId,
            ApplyDraftBackendPreference,
            ApplyThreadPreference,
            RememberGlobalBackendPreference,
            RememberThreadPreference,
            InvalidateSelectedSessionUsage,
            RefreshHeaderAndThreadWorkspace,
            VerifyBindableAccess,
            GetUiDispatcher);
        _threadTabStripCoordinator = new ThreadTabStripCoordinator(
            () => ThreadTabControl,
            () => _threadWorkspaceView,
            () => _viewState.OpenThreadIds,
            () => _draftTabOpen,
            () => _selectedThreadId,
            () => _globalScopeSelected,
            GetSelectedProject,
            threadId => FindThread(threadId),
            thread => EnsureThreadTab(thread),
            build => CreateComputedVisual(build),
            GetUiDispatcher,
            () => _ = ActivateDraftTabAsync(),
            threadId => _ = CloseThreadAsync(threadId),
            () => _ = CloseDraftTabAsync(),
            threadId => _ = _shellController.OpenThreadAsync(threadId, CancellationToken.None));
        _threadHistoryCoordinator = new ThreadHistoryCoordinator(
            _runtimeService,
            EnsureThreadTab,
            threadId => FindThread(threadId),
            threadId => _threadTabs.GetValueOrDefault(threadId),
            thread => ThreadHistoryCoordinator.CanLoadThreadHistory(thread) && IsChatBackendReady(new AgentBackendId(thread.BackendId)),
            BuildExecutionOptions,
            (tab, message, showSpinner, tone) => SetThreadStatus(tab, message, showSpinner, tone),
            ClearThreadStatus,
            ResetThreadTab,
            HandleAgentEvent);
    }

    /// <summary>
    /// Runs the terminal UI.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await LoadCatalogStateAsync(cancellationToken).ConfigureAwait(false);
        _shellViewModel.HeaderText = BuildHeaderText();

        SetStatus("Connecting to available backends...", showSpinner: true);

        var root = EnsureShellView().Root;

        await Terminal.RunAsync(
                root,
                () =>
                {
                    if (!_terminalLoopStarted)
                    {
                        _terminalLoopStarted = true;
                        _uiDispatcher = new TerminalUiDispatcher(Dispatcher.Current);
                        _shellController.AttachUiDispatcher(_uiDispatcher);
                        _shellController.StartInitialization(cancellationToken);
                        _runtimeEventPump.Start(cancellationToken);
                    }

                    ApplyPendingSidebarSelection();
                    SyncSidebarSelection();
                    return TerminalLoopResult.Continue;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _runtimeEventPump.DisposeAsync().ConfigureAwait(false);
        await _shellController.DisposeAsync().ConfigureAwait(false);

        if (_ownedServices is not null)
        {
            await _ownedServices.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal enum StatusTone
    {
        Info,
        Ready,
        Warning,
        Error,
    }

    internal readonly record struct StatusSnapshot(string Message, bool Busy, StatusTone Tone);

    internal enum OpenTabIndicatorKind
    {
        Running,
        Ready,
        Warning,
        Error,
        Info,
    }

    internal sealed record InitialThreadSelection(string? SelectedThreadId, string? StartupThreadRestoreId);

    private string? GetDraftProjectRoot()
        => _globalScopeSelected ? null : GetSelectedProject()?.ProjectPath;

    private string? GetThreadProjectRoot(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return GetProjectById(thread.ProjectRef)?.ProjectPath;
    }

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

    private string? GetExpandedSidebarProjectId()
    {
        return GetSelectedThread()?.ProjectRef ?? _selectedProjectId;
    }

    private SidebarSelectionTarget ResolveSidebarTargetForCurrentState()
    {
        return SidebarSelectionResolver.ResolveCurrentTarget(
            _selectedThreadId,
            _selectedProjectId,
            _globalScopeSelected);
    }

    private void RefreshSidebarProjection()
    {
        _sidebarCoordinator.RefreshProjection(
            _projects,
            _threads,
            GetExpandedSidebarProjectId(),
            ResolveSidebarTargetForCurrentState(),
            VerifyBindableAccess);
    }

    private void SyncSidebarSelectionToCurrentState()
    {
        _sidebarCoordinator.SyncSelectionToCurrentState(ResolveSidebarTargetForCurrentState());
    }

    private void ApplyPendingSidebarSelection()
    {
        _sidebarCoordinator.ApplyPendingSelection();
    }

    private void SyncSidebarSelection()
    {
        _sidebarCoordinator.SyncSelection(
            () => _ = _shellController.SelectGlobalScopeAsync(CancellationToken.None),
            projectId => _ = _shellController.SelectProjectScopeAsync(projectId, CancellationToken.None),
            threadId => _ = _shellController.OpenThreadAsync(threadId, CancellationToken.None));
    }

    private void RefreshChatSelectorsForDraftScope(AgentBackendId? preferredBackendId = null)
    {
        _chatSelectorCoordinator.RefreshForDraftScope(preferredBackendId);
    }

    private void RefreshChatSelectorsForThread(OpenThreadState tab)
    {
        _chatSelectorCoordinator.RefreshForThread(tab);
    }

    private void OnChatBackendSelectionChanged(int newIndex)
    {
        _chatSelectorCoordinator.OnBackendSelectionChanged(newIndex);
    }

    private void OnChatModelSelectionChanged(int newIndex)
    {
        _chatSelectorCoordinator.OnModelSelectionChanged(newIndex);
    }

    private void OnChatReasoningSelectionChanged(int newIndex)
    {
        _chatSelectorCoordinator.OnReasoningSelectionChanged(newIndex);
    }

    private void OnChatAutoScrollChanged()
    {
        _chatSelectorCoordinator.OnAutoScrollChanged();
    }

    private AgentBackendId GetPreferredBackendId()
    {
        return _chatSelectorCoordinator.GetPreferredBackendId();
    }

    private bool IsChatBackendReady(AgentBackendId backendId)
    {
        return _chatSelectorCoordinator.IsChatBackendReady(backendId);
    }

    private bool TryGetPromptUnavailableStatus(out string message, out StatusTone tone)
    {
        return _chatSelectorCoordinator.TryGetPromptUnavailableStatus(out message, out tone);
    }

    private bool TrySetPromptUnavailableStatus()
    {
        if (!TryGetPromptUnavailableStatus(out var message, out var tone))
        {
            return false;
        }

        SetStatus(message, tone: tone);
        return true;
    }

    private void UpdatePromptAvailabilityUi()
    {
        _chatSelectorCoordinator.UpdatePromptAvailabilityUi();
    }

    private void SyncThreadTabControl()
    {
        _threadTabStripCoordinator.SyncControl();
    }

    private void OnThreadTabControlSelectionChanged(int selectedIndex)
    {
        _threadTabStripCoordinator.OnSelectionChanged(selectedIndex);
    }

    private void ResetPendingThreadTabSelection()
    {
        _threadTabStripCoordinator.ResetPendingSelection();
    }

    private CodeAltaShellView EnsureShellView()
    {
        _threadWorkspaceView ??= new ThreadWorkspaceView(
            _shellViewModel,
            _threadWorkspaceViewModel,
            _promptComposerViewModel,
            () => CreateUsageComputedVisual(EnsureSessionUsagePresenter().BuildIndicatorVisual),
            CreatePromptEditor,
            () => _ = SendSelectedThreadPromptAsync(steer: false),
            OnThreadTabControlSelectionChanged,
            OnChatBackendSelectionChanged,
            OnChatModelSelectionChanged,
            OnChatReasoningSelectionChanged,
            OnChatAutoScrollChanged);

        RefreshSidebarProjection();
        RefreshThreadPaneContent();

        _shellView ??= new CodeAltaShellView(
            _shellViewModel,
            _sidebarCoordinator.View.Root,
            _threadWorkspaceView.Root,
            ThreadCommandBar!);
        return _shellView;
    }

    internal static Visual CreateThreadTabPageContentPlaceholder()
        // The active thread flow is hosted by the splitter, so tabs need a detached placeholder.
        => new Placeholder
        {
            IsVisible = false,
        };

    private void RefreshShellChrome()
        => DispatchToUi(RefreshShellChromeCore);

    internal void RefreshCatalogAndThreadWorkspace()
    {
        DispatchToUi(
            () =>
            {
                RefreshCatalogAndThreadWorkspaceCore();
            });
    }

    private void RefreshHeaderAndThreadWorkspace()
    {
        DispatchToUi(
            () =>
            {
                RefreshHeaderAndThreadWorkspaceCore();
            });
    }

    private void RefreshSelectionAndThreadWorkspace()
    {
        DispatchToUi(
            () =>
            {
                RefreshSelectionAndThreadWorkspaceCore();
            });
    }

    private void RefreshHeaderAndThreadWorkspaceCore()
    {
        VerifyBindableAccess();
        EnsureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshShellChromeCore()
    {
        VerifyBindableAccess();
        EnsureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        RefreshSidebarProjection();
    }

    private void RefreshCatalogAndThreadWorkspaceCore()
    {
        RefreshShellChromeCore();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshSelectionAndThreadWorkspaceCore()
    {
        VerifyBindableAccess();
        EnsureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        SyncSidebarSelectionToCurrentState();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshThreadWorkspaceCore()
    {
        SyncSelectedSessionUsageViewModel();
        _viewRefreshState.Value++;
        _usageRefreshState.Value++;
        RefreshThreadPaneContent();
    }

    private void RefreshThreadPaneContent()
    {
        if (ThreadPaneLayout is null || ThreadBodySplitter is null || ThreadInput is null)
        {
            return;
        }

        SyncThreadTabControl();

        var selectedThread = GetSelectedThread();
        if (selectedThread is null)
        {
            RefreshChatSelectorsForDraftScope();
            UpdatePromptAvailabilityUi();
            ThreadBodySplitter.First = WelcomePaneFactory.Build(GetSelectedProject(), _globalScopeSelected);
            SetReadyStatusForCurrentSelection();

            return;
        }

        var tab = EnsureThreadTab(selectedThread);
        RefreshChatSelectorsForThread(tab);
        UpdatePromptAvailabilityUi();
        ThreadBodySplitter.First = tab.Timeline.Flow;
        SetReadyStatusForCurrentSelection();
    }

    internal void SelectGlobalScope()
    {
        ResetPendingThreadTabSelection();
        _draftTabOpen = true;
        _globalScopeSelected = true;
        _selectedThreadId = null;
        _viewState.SelectedThreadId = null;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        RefreshSelectionAndThreadWorkspace();
    }

    internal void SelectProjectScope(string projectId)
    {
        ResetPendingThreadTabSelection();
        _draftTabOpen = true;
        _globalScopeSelected = false;
        _selectedProjectId = projectId;
        _selectedThreadId = null;
        _viewState.SelectedThreadId = null;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        RefreshSelectionAndThreadWorkspace();
    }

    private void EnsureSelectionDefaults()
    {
        if (!string.IsNullOrWhiteSpace(_selectedThreadId) &&
            _threads.All(thread => !string.Equals(thread.ThreadId, _selectedThreadId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedThreadId = null;
        }

        if (string.IsNullOrWhiteSpace(_selectedProjectId) ||
            _projects.All(project => !string.Equals(project.Id, _selectedProjectId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedProjectId = _projects.FirstOrDefault()?.Id;
        }

        if (!_globalScopeSelected && _selectedProjectId is null)
        {
            _globalScopeSelected = true;
        }
    }

    private string BuildHeaderText()
    {
        return ShellTextFormatter.BuildHeaderText(
            GetSelectedThread(),
            GetSelectedProject(),
            _catalogOptions.GlobalRoot,
            GetPreferredBackendId().Value,
            _globalScopeSelected);
    }

    internal void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
    {
        DispatchToUi(
            () =>
            {
                VerifyBindableAccess();
                _shellViewModel.StatusText = message;
                _shellViewModel.StatusBusy = showSpinner;
                _shellViewModel.StatusTone = tone;
                _shellViewModel.StatusIconMarkup = StatusVisualFormatter.BuildStatusIconMarkup(tone);
            });
    }

    internal static StatusSnapshot ResolveSelectionStatus(
        string readyMessage,
        bool hasThreadStatus,
        string? threadStatusMessage,
        bool threadStatusBusy,
        StatusTone threadStatusTone,
        bool promptUnavailable,
        string? promptUnavailableMessage,
        StatusTone promptUnavailableTone)
    {
        if (hasThreadStatus && !string.IsNullOrWhiteSpace(threadStatusMessage))
        {
            return new StatusSnapshot(threadStatusMessage!, threadStatusBusy, threadStatusTone);
        }

        if (promptUnavailable && !string.IsNullOrWhiteSpace(promptUnavailableMessage))
        {
            return new StatusSnapshot(promptUnavailableMessage!, Busy: false, promptUnavailableTone);
        }

        return new StatusSnapshot(readyMessage, Busy: false, StatusTone.Ready);
    }

    private void SetThreadStatus(
        OpenThreadState tab,
        string message,
        bool showSpinner = false,
        StatusTone tone = StatusTone.Info,
        bool hasCustomStatus = true)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var changed =
            !string.Equals(tab.StatusMessage, message, StringComparison.Ordinal) ||
            tab.StatusBusy != showSpinner ||
            tab.StatusTone != tone ||
            tab.HasCustomStatus != hasCustomStatus;

        tab.StatusMessage = message;
        tab.StatusBusy = showSpinner;
        tab.StatusTone = tone;
        tab.HasCustomStatus = hasCustomStatus;

        if (IsSelectedThread(tab.Thread.ThreadId))
        {
            SetReadyStatusForCurrentSelection();
        }

        if (changed)
        {
            InvalidateThreadChrome();
        }
    }

    private void ClearThreadStatus(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        SetThreadStatus(
            tab,
            ShellTextFormatter.BuildReadyStatusText(tab.Thread, GetSelectedProject(), globalScopeSelected: false),
            tone: StatusTone.Ready,
            hasCustomStatus: false);
    }

    private void InvalidateThreadChrome()
    {
        DispatchToUi(() => _viewRefreshState.Value++);
    }

    private void InvalidateSelectedSessionUsage()
    {
        DispatchToUi(
            () =>
            {
                SyncSelectedSessionUsageViewModel();
                _usageRefreshState.Value++;
            });
    }

    private bool IsSelectedThread(string threadId)
        => !string.IsNullOrWhiteSpace(threadId) &&
           string.Equals(_selectedThreadId, threadId, StringComparison.OrdinalIgnoreCase);

    internal void SetReadyStatusForCurrentSelection()
    {
        var selectedThread = GetSelectedThread();
        var readyMessage = ShellTextFormatter.BuildReadyStatusText(selectedThread, GetSelectedProject(), _globalScopeSelected);
        var promptUnavailable = TryGetPromptUnavailableStatus(out var promptUnavailableMessage, out var promptUnavailableTone);
        if (selectedThread is not null &&
            _threadTabs.TryGetValue(selectedThread.ThreadId, out var selectedTab))
        {
            var snapshot = ResolveSelectionStatus(
                readyMessage,
                selectedTab.HasCustomStatus,
                selectedTab.ViewModel.StatusMessage,
                selectedTab.ViewModel.StatusBusy,
                selectedTab.ViewModel.StatusTone,
                promptUnavailable,
                promptUnavailableMessage,
                promptUnavailableTone);
            SetStatus(snapshot.Message, snapshot.Busy, snapshot.Tone);
            return;
        }

        if (promptUnavailable)
        {
            SetStatus(promptUnavailableMessage, tone: promptUnavailableTone);
            return;
        }

        SetStatus(readyMessage, tone: StatusTone.Ready);
    }

    private SessionUsagePresenter EnsureSessionUsagePresenter()
    {
        _sessionUsagePresenter ??= new SessionUsagePresenter(
            _sessionUsageViewModel,
            markdown => (ThreadPaneLayout?.App)?.Terminal.Clipboard.TrySetText(markdown),
            build => CreateUsageComputedVisual(build));
        return _sessionUsagePresenter;
    }

    private void SyncSelectedSessionUsageViewModel()
    {
        VerifyBindableAccess();
        var selectedThread = GetSelectedThread();
        if (selectedThread is not null)
        {
            var tab = EnsureThreadTab(selectedThread);
            var backendState = _chatBackendStates[tab.BackendId.Value];
            _sessionUsageViewModel.Usage = tab.Usage;
            _sessionUsageViewModel.BackendName = backendState.DisplayName;
            _sessionUsageViewModel.ModelName = tab.ModelId ?? backendState.SelectedModelId;
            return;
        }

        var backendId = GetPreferredBackendId();
        var draftBackendState = _chatBackendStates[backendId.Value];
        _sessionUsageViewModel.Usage = null;
        _sessionUsageViewModel.BackendName = draftBackendState.DisplayName;
        _sessionUsageViewModel.ModelName = draftBackendState.SelectedModelId;
    }

    private T ReadBindableState<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return UiDispatch.Invoke(
            GetUiDispatcher(),
            () =>
            {
                VerifyBindableAccess();
                return read();
            });
    }

    internal void SetShellInitialized(bool isInitialized)
    {
        DispatchToUi(() => _shellViewModel.IsInitialized = isInitialized);
    }

    private void DispatchToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = GetUiDispatcher();
        UiDispatch.Post(
            dispatcher,
            action,
            allowInline: ShouldRunInlineOnCurrentThread(
                dispatcher.CheckAccess(),
                _terminalLoopStarted));
    }

    internal static bool CanAccessBindableState(
        bool dispatcherHasAccess,
        bool terminalLoopStarted)
    {
        if (!terminalLoopStarted)
        {
            return true;
        }

        return dispatcherHasAccess;
    }

    private void VerifyBindableAccess()
    {
        var dispatcher = GetUiDispatcher();
        if (CanAccessBindableState(dispatcher.CheckAccess(), _terminalLoopStarted))
        {
            return;
        }

        throw new InvalidOperationException("Bindable view-model state must be accessed on the UI thread.");
    }

    internal static bool ShouldRunInlineOnCurrentThread(
        bool dispatcherHasAccess,
        bool terminalLoopStarted)
    {
        if (!terminalLoopStarted)
        {
            return true;
        }

        return dispatcherHasAccess;
    }

    private IUiDispatcher GetUiDispatcher()
        => _uiDispatcher ??= new TerminalUiDispatcher(Dispatcher.Current);

    private ComputedVisual CreateComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new ComputedVisual(
            () =>
            {
                var _ = _viewRefreshState.Value;
                return build();
            });
    }

    private ComputedVisual CreateUsageComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new ComputedVisual(
            () =>
            {
                var _ = _usageRefreshState.Value;
                return build();
            });
    }

    private void ClearThreadInput()
    {
        UiDispatch.Invoke(
            GetUiDispatcher(),
            () =>
            {
                ThreadInput!.Text = string.Empty;
                return 0;
            });
    }

    private void ClearThreadTitleDraft()
    {
        DispatchToUi(() => _sidebarViewModel.DraftThreadTitle = string.Empty);
    }

    private async Task ActivateDraftTabAsync()
    {
        ResetPendingThreadTabSelection();
        _draftTabOpen = true;
        _selectedThreadId = null;
        _viewState.SelectedThreadId = null;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshSelectionAndThreadWorkspace();
    }

    private async Task CloseDraftTabAsync()
    {
        _draftTabOpen = false;
        if (string.IsNullOrWhiteSpace(_selectedThreadId))
        {
            _selectedThreadId = _viewState.OpenThreadIds.FirstOrDefault();
            _viewState.SelectedThreadId = _selectedThreadId;
        }

        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshSelectionAndThreadWorkspace();
    }

    private bool GetAutoApproveEnabled()
        => DefaultAutoApproveEnabled;

    private async Task PersistViewStateAsync()
    {
        try
        {
            await _threadCatalog.SaveViewStateAsync(_viewState, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && UiLogger.IsEnabled(LogLevel.Error))
            {
                UiLogger.Error(ex, "Failed to persist thread view state.");
            }
        }
    }

    private static Group CreateSectionGroup(string title, Visual content)
    {
        return new Group(new Markup($"[bold]{title}[/]"), content)
            .Padding(1)
            .Style(XenoAtom.Terminal.UI.Styling.GroupStyle.Rounded);
    }

    private ChatPromptEditor CreatePromptEditor()
    {
        var converter = new MarkdownMarkupConverter();
        var editor = new ChatPromptEditor(text => _ = SendSelectedThreadPromptAsync(steer: false))
            .PromptMarkup("[primary]>[/] ")
            .ContinuationPromptMarkup("[muted]·[/] ")
            .Placeholder(_promptComposerViewModel.Bind.Placeholder)
            .EnterMode(PromptEditorEnterMode.EnterInsertsNewLine)
            .EnableWordHints(true)
            .Highlighter(HighlightMarkdown)
            .MinHeight(3)
            .Style(PromptEditorStyle.Default with
            {
                Padding = new Thickness(0, 0, 1, 0),
                PlaceholderForeground = UiPalette.PromptPlaceholderColor,
            });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.Steer",
            LabelMarkup = "Steer",
            DescriptionMarkup = "Send an immediate steering instruction to the selected thread.",
            Gesture = new KeyGesture(TerminalKey.F5),
            Importance = CommandImportance.Primary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => { _ = SendSelectedThreadPromptAsync(steer: true); },
            CanExecute = _visual => _promptComposerViewModel.CanSteer,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.Delegate",
            LabelMarkup = "Delegate",
            DescriptionMarkup = "Create a delegated internal thread from the current project thread.",
            Gesture = new KeyGesture(TerminalKey.F7),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => { _ = DelegateSelectedThreadAsync(); },
            CanExecute = _visual => _promptComposerViewModel.CanDelegate,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.Abort",
            LabelMarkup = "Abort",
            DescriptionMarkup = "Abort the selected thread run.",
            Gesture = new KeyGesture(TerminalKey.F8),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => { _ = AbortSelectedThreadAsync(); },
            CanExecute = _visual => _promptComposerViewModel.CanAbort,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.CloseTab",
            LabelMarkup = "Close Tab",
            DescriptionMarkup = "Close the current thread tab.",
            Gesture = new KeyGesture(TerminalKey.F9),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => { _ = GetSelectedThread() is not null ? CloseSelectedThreadAsync() : CloseDraftTabAsync(); },
            CanExecute = _visual => _promptComposerViewModel.CanCloseTab,
        });

        return editor;

        void HighlightMarkdown(in PromptEditorHighlightRequest request, List<StyledRun> runs)
        {
            converter.Theme = request.Theme;
            converter.Highlight(SnapshotToString(request.Snapshot), runs);
        }

        static string SnapshotToString(ITextSnapshot snapshot)
        {
            if (snapshot.Length == 0)
            {
                return string.Empty;
            }

            return string.Create(snapshot.Length, snapshot, static (span, s) => s.CopyTo(0, span));
        }
    }
    private async Task LoadCatalogStateAsync(CancellationToken cancellationToken)
    {
        _projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        _threads = await _threadCatalog.LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        _viewState = await _threadCatalog.LoadViewStateAsync(cancellationToken).ConfigureAwait(false);

        var desiredThreadId = _viewState.SelectedThreadId ?? _viewState.OpenThreadIds.FirstOrDefault();
        _selectedThreadId = string.IsNullOrWhiteSpace(desiredThreadId)
            ? null
            : _threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, desiredThreadId, StringComparison.OrdinalIgnoreCase))?.ThreadId;
        _draftTabOpen = _selectedThreadId is null;
        _pendingStartupThreadRestoreId = desiredThreadId;
        var selectedThread = GetSelectedThread();
        _selectedProjectId = selectedThread?.ProjectRef ?? _projects.FirstOrDefault()?.Id;
    }

    internal static InitialThreadSelection ResolveInitialSelection(
        WorkThreadViewState viewState,
        IReadOnlyList<WorkThreadDescriptor> threads)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(threads);

        var selectedThreadId = viewState.SelectedThreadId ?? viewState.OpenThreadIds.FirstOrDefault();
        var selectedThread = string.IsNullOrWhiteSpace(selectedThreadId)
            ? null
            : threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, selectedThreadId, StringComparison.OrdinalIgnoreCase));

        return new InitialThreadSelection(
            selectedThread?.ThreadId,
            selectedThread?.ThreadId);
    }

    internal async Task InitializeChatBackendsAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
                RefreshChatBackendStateAsync(AgentBackendIds.Codex, cancellationToken),
                RefreshChatBackendStateAsync(AgentBackendIds.Copilot, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task RefreshChatBackendStateAsync(AgentBackendId backendId, CancellationToken cancellationToken)
    {
        var state = _chatBackendStates[backendId.Value];
        DispatchToUi(
            () =>
            {
                state.Availability = ChatBackendAvailability.Connecting;
                state.StatusMessage = "Detecting backend...";
                RefreshHeaderAndThreadWorkspaceCore();
            });

        try
        {
            var models = await _agentHub.ListModelsAsync(backendId, cancellationToken).ConfigureAwait(false);
            DispatchToUi(
                () =>
                {
                    state.Models.Clear();
                    state.Models.AddRange(models);
                    state.SelectedModelId = ChatBackendPresentation.ResolvePreferredModelId(models, state.SelectedModelId);
                    state.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
                        ChatBackendPreferenceCoordinator.FindModel(models, state.SelectedModelId),
                        state.SelectedReasoningEffort);
                    state.Availability = ChatBackendAvailability.Ready;
                    state.StatusMessage = ChatBackendPresentation.BuildReadyStatusMessage(state);
                    RefreshHeaderAndThreadWorkspaceCore();
                });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var (availability, statusMessage) = ClassifyBackendInitializationFailure(state, ex);
            DispatchToUi(
                () =>
                {
                    state.Models.Clear();
                    state.SelectedModelId = null;
                    state.SelectedReasoningEffort = null;
                    state.DraftScopeKey = null;
                    state.Availability = availability;
                    state.StatusMessage = statusMessage;
                    RefreshHeaderAndThreadWorkspaceCore();
                });
        }
    }

    internal void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);

        _projects = projects;
        _threads = threads;

        _viewState.OpenThreadIds.RemoveAll(id => _threads.All(thread => !string.Equals(thread.ThreadId, id, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(_viewState.SelectedThreadId) &&
            _viewState.OpenThreadIds.All(id => !string.Equals(id, _viewState.SelectedThreadId, StringComparison.OrdinalIgnoreCase)))
        {
            _viewState.SelectedThreadId = null;
        }

        if (string.IsNullOrWhiteSpace(_selectedThreadId) &&
            !string.IsNullOrWhiteSpace(_pendingStartupThreadRestoreId) &&
            FindThread(_pendingStartupThreadRestoreId) is { } restoredThread)
        {
            if (!_viewState.OpenThreadIds.Contains(restoredThread.ThreadId, StringComparer.OrdinalIgnoreCase))
            {
                _viewState.OpenThreadIds.Insert(0, restoredThread.ThreadId);
            }

            _viewState.SelectedThreadId = restoredThread.ThreadId;
            _selectedThreadId = restoredThread.ThreadId;
            _selectedProjectId = restoredThread.ProjectRef ?? _selectedProjectId;
            _draftTabOpen = false;
        }

        EnsureSelectionDefaults();
        RefreshCatalogAndThreadWorkspace();
    }

    internal static (ChatBackendAvailability Availability, string StatusMessage) ClassifyBackendInitializationFailure(
        ChatBackendState state,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(exception);

        var root = exception.GetBaseException();
        if (root is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ChatBackendAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, root.Message));
        }

        if (root is Win32Exception win32Exception && win32Exception.NativeErrorCode == 2)
        {
            return (ChatBackendAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, root.Message));
        }

        var message = root.Message.Trim();
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return (ChatBackendAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, message));
        }

        return (ChatBackendAvailability.Failed, ChatBackendPresentation.BuildFailedBackendMessage(state, message));
    }

    internal void TrySchedulePendingStartupThreadRestore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_pendingStartupThreadRestoreId))
        {
            return;
        }

        var thread = FindThread(_pendingStartupThreadRestoreId);
        if (thread is null || !IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            return;
        }

        var threadId = _pendingStartupThreadRestoreId;
        _pendingStartupThreadRestoreId = null;
        _ = RestoreStartupThreadHistoryAsync(threadId, cancellationToken);
    }

    private async Task RestoreStartupThreadHistoryAsync(string? threadId, CancellationToken cancellationToken)
    {
        var thread = FindThread(threadId);
        if (thread is null)
        {
            return;
        }

        await EnsureThreadHistoryLoadedAsync(thread, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkThreadDescriptor?> CreateGlobalThreadAsync()
    {
        try
        {
            SetStatus("Creating global thread...", showSpinner: true);
            var title = ReadBindableState(() => _sidebarViewModel.DraftThreadTitle?.Trim());
            var executionOptions = BuildPreferredExecutionOptions(
                GetPreferredBackendId(),
                _catalogOptions.GlobalRoot,
                []);
            var thread = await _runtimeService.CreateGlobalThreadAsync(executionOptions, title).ConfigureAwait(false);
            RememberThreadPreference(thread.ThreadId, executionOptions.Model, executionOptions.ReasoningEffort, autoScroll: true, persistNow: false);
            await RegisterCreatedThreadAsync(thread).ConfigureAwait(false);
            ClearThreadTitleDraft();
            SetStatus(ShellTextFormatter.BuildReadyStatusText(thread, GetSelectedProject(), _globalScopeSelected), tone: StatusTone.Ready);
            return thread;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create global thread: {ex.Message}", tone: StatusTone.Error);
            return null;
        }
    }

    private async Task<WorkThreadDescriptor?> CreateProjectThreadAsync()
    {
        var project = GetSelectedProject();
        if (project is null)
        {
            SetStatus("Select a project before creating a project thread.", tone: StatusTone.Warning);
            return null;
        }

        try
        {
            SetStatus($"Creating thread for '{project.DisplayName}'...", showSpinner: true);
            var title = ReadBindableState(() => _sidebarViewModel.DraftThreadTitle?.Trim());
            var executionOptions = BuildPreferredExecutionOptions(
                GetPreferredBackendId(),
                project.ProjectPath,
                [project.ProjectPath]);
            var thread = await _runtimeService.CreateProjectThreadAsync(project, executionOptions, title).ConfigureAwait(false);
            RememberThreadPreference(thread.ThreadId, executionOptions.Model, executionOptions.ReasoningEffort, autoScroll: true, persistNow: false);
            await RegisterCreatedThreadAsync(thread).ConfigureAwait(false);
            ClearThreadTitleDraft();
            SetStatus(ShellTextFormatter.BuildReadyStatusText(thread, GetSelectedProject(), _globalScopeSelected), tone: StatusTone.Ready);
            return thread;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create project thread: {ex.Message}", tone: StatusTone.Error);
            return null;
        }
    }

    private async Task RegisterCreatedThreadAsync(WorkThreadDescriptor thread)
    {
        var threads = _threads.ToList();
        threads.RemoveAll(existing => string.Equals(existing.ThreadId, thread.ThreadId, StringComparison.OrdinalIgnoreCase));
        threads.Add(thread);
        _threads = threads
            .OrderByDescending(static item => item.LastActiveAt)
            .ToArray();

        _draftTabOpen = false;
        OpenThread(thread.ThreadId);
        await EnsureThreadHistoryLoadedAsync(thread).ConfigureAwait(false);
    }

    internal void OpenThread(string threadId)
    {
        var thread = FindThread(threadId);
        if (thread is null)
        {
            SetStatus($"Thread '{threadId}' was not found.", tone: StatusTone.Warning);
            return;
        }

        ResetPendingThreadTabSelection();
        EnsureThreadTab(thread);
        if (!_viewState.OpenThreadIds.Contains(threadId, StringComparer.OrdinalIgnoreCase))
        {
            _viewState.OpenThreadIds.Add(threadId);
        }

        _viewState.SelectedThreadId = threadId;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        _selectedThreadId = threadId;
        if (ThreadHistoryCoordinator.CanLoadThreadHistory(thread) && !IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            _pendingStartupThreadRestoreId = thread.ThreadId;
        }

        _ = PersistViewStateAsync();
        RefreshSelectionAndThreadWorkspace();
        _ = EnsureThreadHistoryLoadedAsync(thread);
    }

    private async Task CloseSelectedThreadAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedThreadId))
        {
            return;
        }

        await CloseThreadAsync(_selectedThreadId).ConfigureAwait(false);
    }

    private async Task CloseThreadAsync(string threadId)
    {
        ResetPendingThreadTabSelection();
        _viewState.OpenThreadIds.RemoveAll(id => string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase));
        _threadWorkspaceView?.RemoveTabPage(threadId);
        _threadTabs.Remove(threadId);
        if (string.Equals(_selectedThreadId, threadId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedThreadId = _viewState.OpenThreadIds.FirstOrDefault();
            _viewState.SelectedThreadId = _selectedThreadId;
        }

        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshSelectionAndThreadWorkspace();
    }

    private Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
        => _threadHistoryCoordinator.EnsureLoadedAsync(thread, cancellationToken);

    private async Task SendSelectedThreadPromptAsync(bool steer)
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            if (steer)
            {
                SetStatus("Start the thread before steering it.", tone: StatusTone.Warning);
                return;
            }

            if (TrySetPromptUnavailableStatus())
            {
                return;
            }

            thread = _globalScopeSelected
                ? await CreateGlobalThreadAsync().ConfigureAwait(false)
                : await CreateProjectThreadAsync().ConfigureAwait(false);
            if (thread is null)
            {
                return;
            }
        }
        else if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            SetReadyStatusForCurrentSelection();
            return;
        }

        var prompt = UiDispatch.Invoke(GetUiDispatcher(), () => ThreadInput?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var tab = EnsureThreadTab(thread);
        await EnsureThreadHistoryLoadedAsync(thread).ConfigureAwait(false);
        tab.Timeline.ReplaceTruncatedHistoryLoadButton();
        ClearThreadInput();
        try
        {
            SetThreadStatus(tab, StatusVisualFormatter.BuildThinkingStatusText(), showSpinner: true);
            var executionOptions = BuildExecutionOptions(thread, tab);
            if (steer)
            {
                _ = await _runtimeService.SteerAsync(
                        thread,
                        executionOptions,
                        new AgentSteerOptions { Input = AgentInput.Text(prompt) })
                    .ConfigureAwait(false);
            }
            else
            {
                _ = await _runtimeService.SendAsync(
                        thread,
                        executionOptions,
                        new AgentSendOptions { Input = AgentInput.Text(prompt) })
                    .ConfigureAwait(false);
            }

            thread.MarkStarted(DateTimeOffset.UtcNow);
            tab.HistoryLoaded = true;
            RefreshHeaderAndThreadWorkspace();
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && UiLogger.IsEnabled(LogLevel.Error))
            {
                UiLogger.Error(ex, $"Failed to send prompt for thread {thread.ThreadId}");
            }

            tab.Timeline.RenderFailure($"Failed to send prompt: {ex.Message}");
            SetThreadStatus(tab, $"Failed to send prompt: {ex.Message}", tone: StatusTone.Error);
        }
    }

    private async Task DelegateSelectedThreadAsync()
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            SetStatus("Open a thread before delegating work.", tone: StatusTone.Warning);
            return;
        }

        if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = EnsureThreadTab(thread);
        var prompt = UiDispatch.Invoke(GetUiDispatcher(), () => ThreadInput?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(prompt))
        {
            SetStatus("Enter delegation instructions before creating an internal thread.", tone: StatusTone.Warning);
            return;
        }

        var targetProject = GetProjectById(thread.ProjectRef ?? _selectedProjectId);
        if (targetProject is null)
        {
            SetStatus("Select a project before delegating internal work.", tone: StatusTone.Warning);
            return;
        }

        try
        {
            SetThreadStatus(tab, $"Delegating internal work from '{thread.Title}'...", showSpinner: true);
            var executionOptions = new WorkThreadExecutionOptions
            {
                BackendId = tab.BackendId,
                WorkingDirectory = targetProject.ProjectPath,
                ProjectRoots = [targetProject.ProjectPath],
                Model = tab.ModelId,
                ReasoningEffort = tab.ReasoningEffort,
                OnPermissionRequest = (request, cancellationToken) => HandleThreadPermissionRequestAsync(CreateTransientThreadKey(tab.BackendId, targetProject.ProjectPath), request, cancellationToken),
                OnUserInputRequest = (request, cancellationToken) => HandleThreadUserInputRequestAsync(CreateTransientThreadKey(tab.BackendId, targetProject.ProjectPath), request, cancellationToken),
            };

            var child = await _runtimeService.CreateInternalThreadAsync(
                thread,
                targetProject,
                executionOptions,
                title: SummarizeThreadContent(prompt),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            RememberThreadPreference(child.ThreadId, executionOptions.Model, executionOptions.ReasoningEffort, tab.AutoScroll, persistNow: false);

            _threads = _threads
                .Where(existing => !string.Equals(existing.ThreadId, child.ThreadId, StringComparison.OrdinalIgnoreCase))
                .Append(child)
                .OrderByDescending(static item => item.LastActiveAt)
                .ToArray();

            EnsureThreadTab(child);
            if (!_viewState.OpenThreadIds.Contains(child.ThreadId, StringComparer.OrdinalIgnoreCase))
            {
                _viewState.OpenThreadIds.Add(child.ThreadId);
                _viewState.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var childTab = EnsureThreadTab(child);
            childTab.BackendId = tab.BackendId;
            childTab.ModelId = tab.ModelId;
            childTab.ReasoningEffort = tab.ReasoningEffort;
            childTab.AutoScroll = tab.AutoScroll;

            _ = await _runtimeService.SendAsync(
                    child,
                    new WorkThreadExecutionOptions
                    {
                        BackendId = tab.BackendId,
                        WorkingDirectory = targetProject.ProjectPath,
                        ProjectRoots = [targetProject.ProjectPath],
                        Model = tab.ModelId,
                        ReasoningEffort = tab.ReasoningEffort,
                        OnPermissionRequest = (request, cancellationToken) => HandleThreadPermissionRequestAsync(child.ThreadId, request, cancellationToken),
                        OnUserInputRequest = (request, cancellationToken) => HandleThreadUserInputRequestAsync(child.ThreadId, request, cancellationToken),
                    },
                    new AgentSendOptions
                    {
                        Input = AgentInput.Text(
                            $"Delegated from thread '{thread.Title}' ({thread.ThreadId}): {prompt}")
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);

            ClearThreadInput();
            SetThreadStatus(tab, $"Delegation started · {child.Title}", tone: StatusTone.Ready);
            await PersistViewStateAsync().ConfigureAwait(false);
            RefreshCatalogAndThreadWorkspace();
        }
        catch (Exception ex)
        {
            UiLogger.Error(ex, "Failed to delegate internal thread.");
            SetThreadStatus(tab, $"Failed to delegate internal thread: {ex.Message}", tone: StatusTone.Error);
        }
    }

    private async Task AbortSelectedThreadAsync()
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            return;
        }

        try
        {
            await _runtimeService.AbortAsync(thread.ThreadId).ConfigureAwait(false);
            var tab = EnsureThreadTab(thread);
            SetThreadStatus(tab, $"Stopped · {thread.Title}", tone: StatusTone.Warning);
        }
        catch (Exception ex)
        {
            var tab = EnsureThreadTab(thread);
            SetThreadStatus(tab, $"Failed to abort '{thread.Title}': {ex.Message}", tone: StatusTone.Error);
        }
    }

    internal void HandleRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
    {
        var thread = FindThread(runtimeEvent.ThreadId);
        if (thread is null)
        {
            return;
        }

        switch (runtimeEvent)
        {
            case WorkThreadAgentEvent agentEvent:
                UpdateThreadFromAgentEvent(thread, agentEvent.Event);
                if (_threadTabs.TryGetValue(thread.ThreadId, out var tab))
                {
                    tab.HistoryEvents?.Add(agentEvent.Event);
                    TryRenderThreadInteraction(tab, () => HandleAgentEvent(thread, tab, agentEvent.Event), "agent event");
                }

                break;

            case WorkThreadHostEvent hostEvent:
                UpdateThreadSummary(thread, hostEvent.Message, hostEvent.Timestamp);
                if (_threadTabs.TryGetValue(thread.ThreadId, out var hostTab))
                {
                    TryRenderThreadInteraction(
                        hostTab,
                        () => hostTab.Timeline.AddStatus(
                            hostEvent.Timestamp,
                            markdown: hostEvent.Message,
                            tone: ChatTimelineTone.Notice,
                            headerOverride: "Notice",
                            headerSecondary: ChatMarkdownFormatter.GetSessionUpdateHeader(hostEvent.Kind)),
                        "host event");
                }

                break;
        }

        if (ShouldRefreshShellChromeAfterRuntimeEvent(runtimeEvent))
        {
            RefreshShellChrome();
        }
    }

    private void HandleAgentEvent(WorkThreadDescriptor thread, OpenThreadState tab, AgentEvent @event)
    {
        if (!tab.HistoryLoading && ShouldPromoteAgentEventToThinking(@event))
        {
            SetThreadStatus(tab, StatusVisualFormatter.BuildThinkingStatusText(), showSpinner: true);
        }

        switch (@event)
        {
            case AgentContentDeltaEvent delta:
                if (tab.Timeline.ToolCalls.TryHandleContent(delta))
                {
                    break;
                }

                if (!ChatMarkdownFormatter.ShouldDisplayContentDelta(delta))
                {
                    break;
                }

                tab.Timeline.AppendContent(delta);
                break;

            case AgentContentCompletedEvent completed:
                if (tab.Timeline.ToolCalls.TryHandleContent(completed))
                {
                    break;
                }

                if (tab.Timeline.ShouldSkipEmptyAssistantCompletion(completed))
                {
                    break;
                }

                if (!ChatMarkdownFormatter.ShouldDisplayCompletedContent(completed))
                {
                    break;
                }

                tab.Timeline.FinalizeContent(completed);
                if (completed.Kind == AgentContentKind.Assistant && !string.IsNullOrWhiteSpace(completed.Content))
                {
                    thread.LatestSummary = SummarizeThreadContent(completed.Content);
                }

                break;

            case AgentPlanSnapshotEvent planEvent:
                tab.Timeline.UpsertPlanStatus(
                    "plan",
                    planEvent.Timestamp,
                    ChatMarkdownFormatter.FormatChatPlanMarkdown(planEvent.Snapshot),
                    ChatTimelineTone.Notice,
                    headerOverride: "Plan");
                break;

            case AgentActivityEvent activity:
                if (tab.Timeline.ToolCalls.TryHandleActivity(activity))
                {
                    break;
                }

                if (!ChatMarkdownFormatter.ShouldDisplayActivity(activity))
                {
                    break;
                }

                tab.Timeline.UpsertActivityStatus(
                    activity.ActivityId,
                    activity.Timestamp,
                    ChatMarkdownFormatter.FormatChatActivityMarkdown(activity),
                    ChatTimelineTone.Activity,
                    headerOverride: ChatMarkdownFormatter.GetActivityHeadline(activity.Kind, activity.Phase));
                break;

            case AgentRawEvent raw:
                if (!ChatMarkdownFormatter.ShouldDisplayRawEvent(raw))
                {
                    break;
                }

                tab.Timeline.AddStatus(
                    raw.Timestamp,
                    ChatMarkdownFormatter.FormatChatRawEventMarkdown(raw),
                    ChatTimelineTone.Activity,
                    headerOverride: "Raw Event");
                break;

            case AgentPermissionRequest permissionRequest:
                if (!ChatMarkdownFormatter.ShouldDisplayPermissionRequest(GetAutoApproveEnabled()))
                {
                    break;
                }

                tab.PermissionRequests[permissionRequest.InteractionId] = permissionRequest;
                tab.Timeline.UpsertInteraction(
                    permissionRequest.InteractionId,
                    permissionRequest.Timestamp,
                    ChatMarkdownFormatter.FormatChatPermissionRequestMarkdown(permissionRequest),
                    null,
                    ChatTimelineTone.Interaction,
                    "Action Required",
                    "Permission Request");
                break;

            case AgentUserInputRequest userInputRequest:
                var autoApproveEnabled = GetAutoApproveEnabled();
                tab.UserInputRequests[userInputRequest.InteractionId] = userInputRequest;
                tab.Timeline.UpsertInteraction(
                    userInputRequest.InteractionId,
                    userInputRequest.Timestamp,
                    ChatMarkdownFormatter.FormatChatUserInputRequestMarkdown(userInputRequest, autoApproveEnabled),
                    null,
                    ChatTimelineTone.Interaction,
                    "Action Required",
                    "User Input Request");
                break;

            case AgentInteractionEvent interaction:
                if (!ChatMarkdownFormatter.ShouldDisplayInteraction(interaction, GetAutoApproveEnabled()))
                {
                    tab.PermissionRequests.Remove(interaction.InteractionId);
                    tab.UserInputRequests.Remove(interaction.InteractionId);
                    break;
                }

                tab.Timeline.UpsertInteraction(
                    interaction.InteractionId,
                    interaction.Timestamp,
                    null,
                    ChatMarkdownFormatter.FormatChatInteractionResolutionMarkdown(interaction, includeHeading: false),
                    ChatTimelineTone.Interaction);
                tab.PermissionRequests.Remove(interaction.InteractionId);
                tab.UserInputRequests.Remove(interaction.InteractionId);
                break;

            case AgentSessionUpdateEvent update:
                if (update.Usage is { } usage)
                {
                    tab.Usage = SessionUsageAggregator.Merge(tab.Usage, usage);
                    if (IsSelectedThread(thread.ThreadId))
                    {
                        InvalidateSelectedSessionUsage();
                    }
                }

                if (update.Kind == AgentSessionUpdateKind.Idle)
                {
                    ClearThreadStatus(tab);
                    break;
                }

                if (!ChatMarkdownFormatter.ShouldDisplaySessionUpdate(update))
                {
                    break;
                }

                tab.Timeline.AddStatus(
                    update.Timestamp,
                    ChatMarkdownFormatter.FormatChatSessionUpdateMarkdown(update),
                    update.Kind == AgentSessionUpdateKind.Warning ? ChatTimelineTone.Interaction : ChatTimelineTone.Notice,
                    headerOverride: "Notice",
                    headerSecondary: ChatMarkdownFormatter.GetSessionUpdateHeader(update.Kind));
                if (!string.IsNullOrWhiteSpace(update.Message))
                {
                    thread.LatestSummary = SummarizeThreadContent(update.Message);
                }

                break;

            case AgentErrorEvent error:
                tab.Timeline.RenderError(error.Message, error.Timestamp);
                thread.LatestSummary = SummarizeThreadContent(error.Message);
                SetThreadStatus(tab, error.Message, tone: StatusTone.Error);
                break;
        }
    }

    internal static bool ShouldPromoteAgentEventToThinking(AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return @event switch
        {
            AgentContentDeltaEvent { Delta.Length: > 0 } => true,
            AgentContentCompletedEvent completed when !string.IsNullOrWhiteSpace(completed.Content) => true,
            AgentPlanSnapshotEvent => true,
            AgentActivityEvent { Phase: AgentActivityPhase.Requested or AgentActivityPhase.Started or AgentActivityPhase.Progressed or AgentActivityPhase.Completed } => true,
            AgentSessionUpdateEvent
            {
                Kind: AgentSessionUpdateKind.Started
                    or AgentSessionUpdateKind.Resumed
                    or AgentSessionUpdateKind.PlanUpdated
                    or AgentSessionUpdateKind.CompactionStarted
            } => true,
            _ => false,
        };
    }

    internal static bool ShouldRefreshShellChromeAfterRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        return runtimeEvent is not WorkThreadAgentEvent
        {
            Event: AgentSessionUpdateEvent
            {
                Kind: AgentSessionUpdateKind.UsageUpdated
            }
        };
    }

    private void TryRenderThreadInteraction(OpenThreadState tab, Action action, string context)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && UiLogger.IsEnabled(LogLevel.Error))
            {
                UiLogger.Error(ex, $"Failed to render thread {context}");
            }

            SetStatus($"Failed to render thread {context}: {ex.Message}", tone: StatusTone.Error);
            tab.Timeline.ClearPendingAssistant();
        }
    }

    private async Task<AgentPermissionDecision> HandleThreadPermissionRequestAsync(
        string threadId,
        AgentPermissionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var autoApproveEnabled = GetAutoApproveEnabled();
        var decision = autoApproveEnabled
            ? new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)
            : new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);

        if (ChatMarkdownFormatter.ShouldDisplayPermissionRequest(autoApproveEnabled) && _threadTabs.TryGetValue(threadId, out var tab))
        {
            TryRenderThreadInteraction(
                tab,
                () =>
                {
                    tab.Timeline.UpsertInteraction(
                        request.InteractionId,
                        request.Timestamp,
                        ChatMarkdownFormatter.FormatChatPermissionRequestMarkdown(request),
                        ChatMarkdownFormatter.FormatChatImmediatePermissionDecisionMarkdown(decision, autoApproveEnabled),
                        ChatTimelineTone.Interaction,
                        "Action Required",
                        "Permission Request");
                },
                "permission request");
        }

        return decision;
    }

    private async Task<AgentUserInputResponse> HandleThreadUserInputRequestAsync(
        string threadId,
        AgentUserInputRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var autoApproveEnabled = GetAutoApproveEnabled();
        var response = ChatPromptResponseBuilder.CreateResponse(request, autoApproveEnabled);
        if (_threadTabs.TryGetValue(threadId, out var tab))
        {
            TryRenderThreadInteraction(
                tab,
                () =>
                {
                    tab.Timeline.UpsertInteraction(
                        request.InteractionId,
                        request.Timestamp,
                        ChatMarkdownFormatter.FormatChatUserInputRequestMarkdown(request, autoApproveEnabled),
                        ChatMarkdownFormatter.FormatChatImmediateUserInputResponseMarkdown(response, autoApproveEnabled),
                        ChatTimelineTone.Interaction,
                        "Action Required",
                        "User Input Request");
                },
                "user input request");
        }

        return response;
    }

    private WorkThreadExecutionOptions BuildExecutionOptions(WorkThreadDescriptor thread, OpenThreadState tab)
    {
        var workingDirectory = ResolveWorkingDirectory(thread);
        var projectRoots = ResolveProjectRoots(thread);
        return new WorkThreadExecutionOptions
        {
            BackendId = new AgentBackendId(thread.BackendId),
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = tab.ModelId,
            ReasoningEffort = tab.ReasoningEffort,
            OnPermissionRequest = (request, cancellationToken) => HandleThreadPermissionRequestAsync(thread.ThreadId, request, cancellationToken),
            OnUserInputRequest = (request, cancellationToken) => HandleThreadUserInputRequestAsync(thread.ThreadId, request, cancellationToken),
        };
    }

    private WorkThreadExecutionOptions BuildPreferredExecutionOptions(
        AgentBackendId backendId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots)
    {
        var backendState = _chatBackendStates[backendId.Value];
        var model = UiDispatch.Invoke(
            GetUiDispatcher(),
            () =>
            {
                if (ChatBackendSelect is null || ChatModelSelect is null)
                {
                    return backendState.SelectedModelId;
                }

                var backendOptions = ChatBackendPresentation.BuildBackendOptions();
                if ((uint)ChatBackendSelect.SelectedIndex < (uint)backendOptions.Count &&
                    string.Equals(backendOptions[ChatBackendSelect.SelectedIndex].BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    var modelOptions = ChatBackendPresentation.BuildModelOptions(backendState);
                    if ((uint)ChatModelSelect.SelectedIndex < (uint)modelOptions.Count)
                    {
                        return modelOptions[ChatModelSelect.SelectedIndex].ModelId;
                    }
                }

                return backendState.SelectedModelId;
            });

        var reasoning = UiDispatch.Invoke(
            GetUiDispatcher(),
            () =>
            {
                if (ChatBackendSelect is null || ChatReasoningSelect is null)
                {
                    return backendState.SelectedReasoningEffort;
                }

                var backendOptions = ChatBackendPresentation.BuildBackendOptions();
                if ((uint)ChatBackendSelect.SelectedIndex < (uint)backendOptions.Count &&
                    string.Equals(backendOptions[ChatBackendSelect.SelectedIndex].BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    var selectedModel = backendState.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, model, StringComparison.Ordinal));
                    var reasoningOptions = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
                    if ((uint)ChatReasoningSelect.SelectedIndex < (uint)reasoningOptions.Count)
                    {
                        return reasoningOptions[ChatReasoningSelect.SelectedIndex].Effort;
                    }
                }

                return backendState.SelectedReasoningEffort;
            });

        return new WorkThreadExecutionOptions
        {
            BackendId = backendId,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = model,
            ReasoningEffort = reasoning,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = (request, cancellationToken) => HandleThreadUserInputRequestAsync(CreateTransientThreadKey(backendId, workingDirectory), request, cancellationToken),
        };
    }

    private static string CreateTransientThreadKey(AgentBackendId backendId, string workingDirectory)
        => $"{backendId.Value}:{workingDirectory}";

    private string ResolveWorkingDirectory(WorkThreadDescriptor thread)
    {
        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => _catalogOptions.GlobalRoot,
            WorkThreadKind.ProjectThread or WorkThreadKind.InternalThread when GetProjectById(thread.ProjectRef) is { } project => project.ProjectPath,
            _ => thread.WorkingDirectory,
        };
    }

    private IReadOnlyList<string> ResolveProjectRoots(WorkThreadDescriptor thread)
    {
        if (GetProjectById(thread.ProjectRef) is { } project)
        {
            return [project.ProjectPath];
        }

        return [];
    }

    private void UpdateThreadFromAgentEvent(WorkThreadDescriptor thread, AgentEvent @event)
    {
        thread.UpdatedAt = @event.Timestamp;
        thread.LastActiveAt = @event.Timestamp;

        switch (@event)
        {
            case AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } completed when !string.IsNullOrWhiteSpace(completed.Content):
                thread.LatestSummary = SummarizeThreadContent(completed.Content);
                break;
            case AgentSessionUpdateEvent update when !string.IsNullOrWhiteSpace(update.Message):
                if (update.Kind == AgentSessionUpdateKind.UsageUpdated)
                {
                    break;
                }

                thread.LatestSummary = SummarizeThreadContent(update.Message);
                break;
            case AgentErrorEvent error when !string.IsNullOrWhiteSpace(error.Message):
                thread.LatestSummary = SummarizeThreadContent(error.Message);
                break;
        }
    }

    private void UpdateThreadSummary(WorkThreadDescriptor thread, string message, DateTimeOffset timestamp)
    {
        thread.UpdatedAt = timestamp;
        thread.LastActiveAt = timestamp;
        thread.LatestSummary = SummarizeThreadContent(message);
    }

    private static string SummarizeThreadContent(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length <= 120)
        {
            return normalized;
        }

        return normalized[..117].TrimEnd() + "...";
    }

    private OpenThreadState EnsureThreadTab(WorkThreadDescriptor thread)
    {
        if (_threadTabs.TryGetValue(thread.ThreadId, out var existing))
        {
            existing.Thread = thread;
            existing.ViewModel.ThreadId = thread.ThreadId;
            existing.ViewModel.Title = thread.Title;
            return existing;
        }

        OpenThreadState? state = null;
        var timeline = new ThreadTimelinePresenter(
            GetUiDispatcher(),
            () => state!.AutoScroll,
            () => ThreadPaneLayout?.GetAbsoluteBounds());
        state = new OpenThreadState(thread, timeline);
        state.BackendId = new AgentBackendId(thread.BackendId);
        state.ViewModel.Title = thread.Title;
        state.StatusMessage = ShellTextFormatter.BuildReadyStatusText(thread, GetSelectedProject(), globalScopeSelected: false);

        ApplyThreadPreference(state);
        RememberThreadPreference(thread.ThreadId, state.ModelId, state.ReasoningEffort, state.AutoScroll, persistNow: false);

        _threadTabs[thread.ThreadId] = state;
        return state;
    }

    private void ResetThreadTab(OpenThreadState tab)
    {
        tab.Timeline.Reset();
        tab.PermissionRequests.Clear();
        tab.UserInputRequests.Clear();
    }

    private ProjectDescriptor? GetSelectedProject()
    {
        var selectedThread = GetSelectedThread();
        return selectedThread?.ProjectRef is { } projectId
            ? GetProjectById(projectId)
            : GetProjectById(_selectedProjectId);
    }

    private ProjectDescriptor? GetProjectById(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return _projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));
    }

    private WorkThreadDescriptor? GetSelectedThread()
        => FindThread(_selectedThreadId);

    private WorkThreadDescriptor? FindThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return _threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
    }

    private WorkThreadDescriptor[] GetThreadsForProject(string projectId, bool includeInternal)
    {
        return _threads
            .Where(thread => string.Equals(thread.ProjectRef, projectId, StringComparison.OrdinalIgnoreCase))
            .Where(thread => includeInternal || thread.Kind == WorkThreadKind.ProjectThread)
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();
    }

    internal static string BuildThreadScopeSummary(
        WorkThreadDescriptor thread,
        IReadOnlyList<ProjectDescriptor> projects,
        string globalRoot)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(projects);

        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => $"Global thread · {globalRoot}",
            WorkThreadKind.ProjectThread when projects.FirstOrDefault(project => string.Equals(project.Id, thread.ProjectRef, StringComparison.OrdinalIgnoreCase)) is { } project
                => $"{project.DisplayName} · {project.ProjectPath}",
            WorkThreadKind.InternalThread when projects.FirstOrDefault(project => string.Equals(project.Id, thread.ProjectRef, StringComparison.OrdinalIgnoreCase)) is { } internalProject
                => $"Internal · {internalProject.DisplayName}",
            WorkThreadKind.InternalThread => "Internal delegated thread",
            _ => thread.WorkingDirectory,
        };
    }

    internal static IReadOnlyList<WorkThreadDescriptor> FilterThreadsForProject(
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? projectId,
        bool includeInternal)
    {
        ArgumentNullException.ThrowIfNull(threads);

        return threads
            .Where(thread => string.Equals(thread.ProjectRef, projectId, StringComparison.OrdinalIgnoreCase))
            .Where(thread => includeInternal || thread.Kind == WorkThreadKind.ProjectThread)
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();
    }

}
