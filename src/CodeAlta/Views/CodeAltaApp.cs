using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Tabs;
using CodeAlta.Presentation.Usage;
using CodeAlta.Presentation.Workspace;
using CodeAlta.ViewModels;
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
    private const int MaxRecentThreadsPerProject = 3;
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
    private readonly ThreadCreationCoordinator _threadCreationCoordinator;
    private readonly PromptDraftUiCoordinator _promptDraftUiCoordinator;
    private readonly CodeAltaShellViewModel _shellViewModel = new();
    private readonly SidebarViewModel _sidebarViewModel = new();
    private readonly ThreadWorkspaceViewModel _threadWorkspaceViewModel = new();
    private readonly PromptComposerViewModel _promptComposerViewModel = new();
    private readonly SessionUsageViewModel _sessionUsageViewModel = new();
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates = ChatBackendPresentation.CreateBackendStates();
    private readonly SidebarCoordinator _sidebarCoordinator;
    private readonly ChatSelectorCoordinator _chatSelectorCoordinator;
    private readonly ThreadTabStripCoordinator _threadTabStripCoordinator;
    private readonly ChatPreferenceContext _chatPreferenceContext;
    private readonly ChatSelectorUiContext _chatSelectorUiContext;
    private readonly ShellAnimationRuntime _shellAnimationRuntime = new();
    private readonly ShellWorkspaceContext _shellWorkspaceContext;
    private readonly ThreadSelectionContext _threadSelectionContext;
    private readonly ThreadTabContext _threadTabContext;
    private readonly WorkspaceRefreshContext _workspaceRefreshContext;
    private CodeAltaShellView? _shellView;
    private ThreadWorkspaceView? _threadWorkspaceView;
    private SessionUsagePresenter? _sessionUsagePresenter;
    private IUiDispatcher? _uiDispatcher;
    private Task<ShellThreadStateCoordinator.InitialCatalogState>? _initialCatalogStateTask;
    private bool _initialCatalogStateResolved;
    private bool _disableTerminalLoopCallback;
    private WorkThreadViewState _viewState
    {
        get => _threadStateCoordinator.ViewState;
        set => _threadStateCoordinator.ViewState = value;
    }

    private bool _draftTabOpen
    {
        get => _threadStateCoordinator.DraftTabOpen;
        set => _threadStateCoordinator.DraftTabOpen = value;
    }

    private bool _globalScopeSelected
    {
        get => _threadStateCoordinator.GlobalScopeSelected;
        set => _threadStateCoordinator.GlobalScopeSelected = value;
    }

    private string? _selectedProjectId
    {
        get => _threadStateCoordinator.SelectedProjectId;
        set => _threadStateCoordinator.SelectedProjectId = value;
    }

    private string? _selectedThreadId
    {
        get => _threadStateCoordinator.SelectedThreadId;
        set => _threadStateCoordinator.SelectedThreadId = value;
    }

    private string? _pendingStartupThreadRestoreId
    {
        get => _threadStateCoordinator.PendingStartupThreadRestoreId;
        set => _threadStateCoordinator.PendingStartupThreadRestoreId = value;
    }
    private Visual? ThreadPaneLayout => _threadWorkspaceView?.ThreadPaneLayout;
    private VSplitter? ThreadBodySplitter => _threadWorkspaceView?.ThreadBodySplitter;
    private ChatPromptEditor? ThreadInput => _threadWorkspaceView?.ThreadInput;
    private CommandBar? ThreadCommandBar => _threadWorkspaceView?.ThreadCommandBar;
    private Select<ChatBackendOption>? ChatBackendSelect => _threadWorkspaceView?.ChatBackendSelect;
    private Select<ChatModelOption>? ChatModelSelect => _threadWorkspaceView?.ChatModelSelect;
    private Select<ChatReasoningOption>? ChatReasoningSelect => _threadWorkspaceView?.ChatReasoningSelect;
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
        return Create(ownedServices);
    }
    internal static CodeAltaApp Create(CodeAltaOwnedServices ownedServices)
    {
        ArgumentNullException.ThrowIfNull(ownedServices);
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
        _backendPreferences = new ChatBackendPreferenceCoordinator(new CodeAltaConfigStore(catalogOptions), UiLogger);
        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _agentHub = agentHub;
        _knownProjectImporter = knownProjectImporter ?? new KnownProjectImporter(agentHub, projectCatalog);
        _ownedServices = ownedServices;
        _promptDraftUiCoordinator = new PromptDraftUiCoordinator(new PromptDraftCoordinator());
        _shellController = new CodeAltaShellController(
            new CodeAltaShellBridge(this),
            _knownProjectImporter,
            new ProjectCatalogLoader(projectCatalog),
            new RecoverableThreadSource(_runtimeService));
        _runtimeEventPump = new RuntimeEventPump(_runtimeService, _shellController);
        _terminalLoopCoordinator = new TerminalLoopCoordinator(
            _shellController,
            _runtimeEventPump,
            dispatcher => _uiDispatcher = dispatcher,
            ApplyPendingSidebarSelection);
        _threadStateCoordinator = new ShellThreadStateCoordinator(
            projectCatalog,
            threadCatalog,
            GetUiDispatcher,
            () => ThreadPaneLayout?.GetAbsoluteBounds(),
            thread => IsChatBackendReady(new AgentBackendId(thread.BackendId)),
            ApplyThreadPreference,
            RememberThreadPreference,
            EnsureThreadHistoryLoadedAsync,
            RefreshSelectionAndThreadWorkspace,
            RefreshCatalogAndThreadWorkspace,
            ResetPendingThreadTabSelection,
            threadId => _threadWorkspaceView?.RemoveTabPage(threadId),
            SetStatus);
        _threadSelectionContext = new ThreadSelectionContext(
            _threadStateCoordinator,
            EnsureThreadHistoryLoadedAsync,
            IsSelectedThread);
        _chatSelectorUiContext = new ChatSelectorUiContext(
            () => ChatBackendSelect,
            () => ChatModelSelect,
            () => ChatReasoningSelect,
            () => ThreadInput,
            GetUiDispatcher,
            VerifyBindableAccess);
        _chatPreferenceContext = new ChatPreferenceContext(
            ApplyDraftBackendPreference,
            ApplyThreadPreference,
            RememberGlobalBackendPreference,
            RememberThreadPreference);
        _workspaceRefreshContext = new WorkspaceRefreshContext(
            InvalidateSelectedSessionUsage,
            RefreshHeaderAndThreadWorkspace);
        _sidebarCoordinator = new SidebarCoordinator(
            _sidebarViewModel,
            _catalogOptions,
            _shellController,
            MaxRecentThreadsPerProject);
        _chatSelectorCoordinator = new ChatSelectorCoordinator(
            _threadWorkspaceViewModel,
            _promptComposerViewModel,
            _chatBackendStates,
            _chatSelectorUiContext,
            _threadSelectionContext,
            _chatPreferenceContext,
            _workspaceRefreshContext);
        _shellWorkspaceContext = new ShellWorkspaceContext(
            GetPreferredBackendId,
            () =>
            {
                var hasStatus = TryGetPromptUnavailableStatus(out var message, out var tone);
                return (hasStatus, message, tone);
            },
            () => ThreadPaneLayout,
            () => ThreadBodySplitter,
            () => ThreadInput,
            EnsureSelectionDefaults,
            RefreshSidebarProjection,
            SyncSidebarSelectionToCurrentState,
            () => _threadPromptQueueCoordinator!.RefreshSelectedThreadQueueUi(),
            () => RefreshChatSelectorsForDraftScope(),
            RefreshChatSelectorsForThread,
            _promptDraftUiCoordinator.SyncPromptText,
            UpdatePromptAvailabilityUi,
            SyncThreadTabControl,
            DispatchToUi,
            VerifyBindableAccess);
        _workspaceCoordinator = new ShellWorkspaceCoordinator(
            _shellViewModel,
            _sessionUsageViewModel,
            _chatBackendStates,
            _threadSelectionContext,
            _shellWorkspaceContext,
            _shellAnimationRuntime.WelcomePhase01,
            _catalogOptions.GlobalRoot);
        _threadPromptQueueCoordinator = new ThreadPromptQueueCoordinator(
            _threadWorkspaceViewModel,
            _threadSelectionContext,
            UpdatePromptAvailabilityUi,
            DispatchToUi,
            VerifyBindableAccess,
            (tab, prompt, cancellationToken) => _threadCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: false, cancellationToken),
            (tab, prompt, cancellationToken) => _threadCommandCoordinator!.DispatchQueuedPromptAsync(tab, prompt, steer: true, cancellationToken));
        _chatBackendInitializationCoordinator = new ChatBackendInitializationCoordinator(
            _agentHub,
            _chatBackendStates,
            DispatchToUi,
            RefreshHeaderAndThreadWorkspace);
        _threadTabContext = new ThreadTabContext(
            () => ThreadTabControl,
            () => _threadWorkspaceView,
            build => CreateComputedVisual(build),
            GetUiDispatcher,
            () => _ = ActivateDraftTabAsync(),
            threadId => _ = CloseThreadAsync(threadId),
            () => _ = CloseDraftTabAsync(),
            threadId => _ = _shellController.OpenThreadAsync(threadId, CancellationToken.None));
        _threadTabStripCoordinator = new ThreadTabStripCoordinator(
            _threadSelectionContext,
            _threadTabContext);
        _threadRuntimeEventCoordinator = new ThreadRuntimeEventCoordinator(
            threadId => FindThread(threadId),
            threadId => _threadStateCoordinator.FindOpenThread(threadId),
            GetAutoApproveEnabled,
            IsSelectedThread,
            InvalidateSelectedSessionUsage,
            RefreshShellChrome,
            SetStatus,
            (tab, message, showSpinner, tone) => SetThreadStatus(tab, message, showSpinner, tone),
            ClearThreadStatus,
            (tab, contentId) => _threadPromptQueueCoordinator.ConsumePendingSteerForLiveUserContent(tab, contentId),
            tab => _threadPromptQueueCoordinator.ClearPendingSteers(tab),
            (tab, cancellationToken) => _threadCommandCoordinator!.DrainQueuedPromptAsync(tab, cancellationToken));
        _threadCreationCoordinator = new ThreadCreationCoordinator(
            _runtimeService,
            _catalogOptions,
            GetPreferredBackendId,
            GetSelectedProject,
            () => _globalScopeSelected,
            () => ReadBindableState(() => _sidebarViewModel.DraftThreadTitle?.Trim()),
            (backendId, workingDirectory, projectRoots) => _threadCommandCoordinator!.BuildPreferredExecutionOptions(backendId, workingDirectory, projectRoots),
            RememberThreadPreference,
            RegisterCreatedThreadAsync,
            ClearThreadTitleDraft,
            SetStatus);
        _threadCommandCoordinator = new ThreadCommandCoordinator(
            _runtimeService,
            _catalogOptions,
            _chatBackendStates,
            _threadSelectionContext,
            _chatSelectorUiContext,
            _chatPreferenceContext,
            new ThreadCommandContext(
                TrySetPromptUnavailableStatus,
                title => _threadCreationCoordinator.CreateGlobalThreadAsync(title),
                title => _threadCreationCoordinator.CreateProjectThreadAsync(title),
                PersistViewStateAsync,
                () => DefaultAutoApproveEnabled,
                _promptDraftUiCoordinator.ClearDraftPromptText,
                SetReadyStatusForCurrentSelection,
                ClearThreadInput,
                RefreshHeaderAndThreadWorkspace,
                RefreshCatalogAndThreadWorkspace,
                SetStatus,
                (tab, message, showSpinner, tone) => SetThreadStatus(tab, message, showSpinner, tone),
                _threadRuntimeEventCoordinator.TryRenderInteraction),
            _threadPromptQueueCoordinator,
            _promptComposerViewModel);
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
            _threadRuntimeEventCoordinator.HandleAgentEvent);
    }

    /// <summary>
    /// Runs the terminal UI.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        PrepareForRun();

        var root = EnsureShellView().Root;

        await Terminal.RunAsync(
                root,
                () => Tick(cancellationToken),
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

    private string? GetDraftProjectRoot()
        => _globalScopeSelected ? null : GetSelectedProject()?.ProjectPath;

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

    private string? GetExpandedSidebarProjectId()
        => GetSelectedThread()?.ProjectRef ?? _selectedProjectId;

    private SidebarSelectionTarget ResolveSidebarTargetForCurrentState()
        => SidebarSelectionResolver.ResolveCurrentTarget(
            _selectedThreadId,
            _selectedProjectId,
            _globalScopeSelected);

    private void RefreshSidebarProjection()
    {
        _sidebarCoordinator.RefreshProjection(
            _threadStateCoordinator.Projects,
            _threadStateCoordinator.Threads,
            GetExpandedSidebarProjectId(),
            ResolveSidebarTargetForCurrentState(),
            VerifyBindableAccess);
    }

    private void SyncSidebarSelectionToCurrentState()
        => _sidebarCoordinator.SyncSelectionToCurrentState(ResolveSidebarTargetForCurrentState());

    private void ApplyPendingSidebarSelection()
        => _sidebarCoordinator.ApplyPendingSelection();

    internal void PrepareForRun()
    {
        _shellViewModel.HeaderText = BuildHeaderText();
        SetStatus("Connecting to available backends...", showSpinner: true);
    }

    internal Visual GetRoot()
        => EnsureShellView().Root;

    internal TerminalLoopResult Tick(CancellationToken cancellationToken)
    {
        if (_disableTerminalLoopCallback)
        {
            return TerminalLoopResult.Continue;
        }

        _shellAnimationRuntime.Advance();
        EnsureInitialCatalogStateStarted(cancellationToken);
        if (!TryResolveInitialCatalogState(cancellationToken))
        {
            return TerminalLoopResult.Continue;
        }

        return _terminalLoopCoordinator.OnIteration(cancellationToken);
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

    private void EnsureInitialCatalogStateStarted(CancellationToken cancellationToken)
        => _initialCatalogStateTask ??= _threadStateCoordinator.LoadInitialCatalogStateAsync(cancellationToken);

    private bool TryResolveInitialCatalogState(CancellationToken cancellationToken)
    {
        if (_initialCatalogStateResolved)
        {
            return true;
        }

        var task = _initialCatalogStateTask;
        if (task is null || !task.IsCompleted)
        {
            return false;
        }

        try
        {
            _threadStateCoordinator.ApplyInitialCatalogState(task.GetAwaiter().GetResult());
            _shellViewModel.HeaderText = BuildHeaderText();
            RefreshCatalogAndThreadWorkspace();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load saved state: {ex.Message}", tone: StatusTone.Error);
        }

        _initialCatalogStateResolved = true;
        return true;
    }

    private void RefreshChatSelectorsForDraftScope(AgentBackendId? preferredBackendId = null)
        => _chatSelectorCoordinator.RefreshForDraftScope(preferredBackendId);
    private void RefreshChatSelectorsForThread(OpenThreadState tab)
        => _chatSelectorCoordinator.RefreshForThread(tab);
    private void OnChatBackendSelectionChanged(int newIndex)
        => _chatSelectorCoordinator.OnBackendSelectionChanged(newIndex);
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

    private bool TrySetPromptUnavailableStatus()
    {
        if (!TryGetPromptUnavailableStatus(out var message, out var tone)) return false;
        SetStatus(message, tone: tone);
        return true;
    }

    private void UpdatePromptAvailabilityUi()
        => _chatSelectorCoordinator.UpdatePromptAvailabilityUi();
    private void SyncThreadTabControl()
        => _threadTabStripCoordinator.SyncControl();
    private void OnThreadTabControlSelectionChanged(int selectedIndex)
        => _threadTabStripCoordinator.OnSelectionChanged(selectedIndex);
    private void ResetPendingThreadTabSelection()
        => _threadTabStripCoordinator.ResetPendingSelection();

    private CodeAltaShellView EnsureShellView()
    {
        _threadWorkspaceView ??= new ThreadWorkspaceView(
            _shellViewModel,
            _threadWorkspaceViewModel,
            _promptComposerViewModel,
            () => CreateUsageComputedVisual(EnsureSessionUsagePresenter().BuildIndicatorVisual),
            () => _ = _threadCommandCoordinator.SendSelectedThreadPromptAsync(steer: false),
            () => _ = _threadCommandCoordinator.SendSelectedThreadPromptAsync(steer: true),
            () => _ = _threadCommandCoordinator.ClearSelectedThreadQueueAsync(),
            queuedPromptId => _ = _threadCommandCoordinator.ConvertSelectedThreadQueuedPromptToSteerAsync(queuedPromptId),
            queuedPromptId => _threadCommandCoordinator.DeleteSelectedThreadQueuedPrompt(queuedPromptId),
            (queuedPromptId, remainingCount) => _threadCommandCoordinator.UpdateSelectedThreadQueuedPromptCount(queuedPromptId, remainingCount),
            (queuedPromptId, text) => _threadCommandCoordinator.UpdateSelectedThreadQueuedPromptText(queuedPromptId, text),
            () => _ = _threadCommandCoordinator.DelegateSelectedThreadAsync(),
            () => _ = _threadCommandCoordinator.AbortSelectedThreadAsync(),
            () => _ = _threadCommandCoordinator.CompactSelectedThreadAsync(),
            () => _ = GetSelectedThread() is not null ? CloseSelectedThreadAsync() : CloseDraftTabAsync(),
            OnChatBackendSelectionChanged,
            OnChatModelSelectionChanged,
            OnChatReasoningSelectionChanged,
            selectedIndex => _threadTabStripCoordinator.ObserveBoundSelection(selectedIndex),
            _promptDraftUiCoordinator.PromptTextBinding,
            _shellAnimationRuntime.ThinkingPhase01,
            OnChatAutoScrollChanged);

        RefreshCatalogAndThreadWorkspace();

        if (_shellView is null)
        {
            _shellView = new CodeAltaShellView(
                _shellViewModel,
                _sidebarCoordinator.View.Root,
                _threadWorkspaceView.Root,
                ThreadCommandBar!);
            _shellView.Root.AddCommand(new Command
            {
                Id = "CodeAlta.Diagnostics.ToggleTerminalLoop",
                LabelMarkup = "Loop",
                DescriptionMarkup = "Toggle per-frame loop work.",
                Gesture = new KeyGesture(TerminalKey.F4),
                Presentation = CommandPresentation.CommandBar,
                Execute = _ => ToggleTerminalLoopCallback(),
            });
        }

        return _shellView;
    }

    internal static Visual CreateThreadTabPageContentPlaceholder()
        // The active thread flow is hosted by the splitter, so tabs need a detached placeholder.
        => new Placeholder
        {
            IsVisible = false,
        };

    private void RefreshShellChrome()
        => _workspaceCoordinator.RefreshShellChrome();
    internal void RefreshCatalogAndThreadWorkspace()
        => _workspaceCoordinator.RefreshCatalogAndThreadWorkspace();
    private void RefreshHeaderAndThreadWorkspace()
        => _workspaceCoordinator.RefreshHeaderAndThreadWorkspace();
    private void RefreshSelectionAndThreadWorkspace()
        => _workspaceCoordinator.RefreshSelectionAndThreadWorkspace();
    internal void SelectGlobalScope()
        => _threadStateCoordinator.SelectGlobalScope();
    internal void SelectProjectScope(string projectId)
        => _threadStateCoordinator.SelectProjectScope(projectId);
    private void EnsureSelectionDefaults()
        => _threadStateCoordinator.EnsureSelectionDefaults();
    private string BuildHeaderText()
        => _workspaceCoordinator.BuildHeaderText();
    internal void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
        => _workspaceCoordinator.SetStatus(message, showSpinner, tone);

    private void SetThreadStatus(
        OpenThreadState tab,
        string message,
        bool showSpinner = false,
        StatusTone tone = StatusTone.Info,
        bool hasCustomStatus = true)
        => _workspaceCoordinator.SetThreadStatus(tab, message, showSpinner, tone, hasCustomStatus);
    private void ClearThreadStatus(OpenThreadState tab)
        => _workspaceCoordinator.ClearThreadStatus(tab);
    private void InvalidateThreadChrome()
        => _workspaceCoordinator.InvalidateThreadChrome();
    private void InvalidateSelectedSessionUsage()
        => _workspaceCoordinator.InvalidateSelectedSessionUsage();

    private bool IsSelectedThread(string threadId)
        => !string.IsNullOrWhiteSpace(threadId) &&
           string.Equals(_selectedThreadId, threadId, StringComparison.OrdinalIgnoreCase);
    internal void SetReadyStatusForCurrentSelection()
        => _workspaceCoordinator.SetReadyStatusForCurrentSelection();

    private SessionUsagePresenter EnsureSessionUsagePresenter()
    {
        _sessionUsagePresenter ??= new SessionUsagePresenter(
            _sessionUsageViewModel,
            markdown => (ThreadPaneLayout?.App)?.Terminal.Clipboard.TrySetText(markdown),
            build => CreateUsageComputedVisual(build));
        return _sessionUsagePresenter;
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
        => _workspaceCoordinator.SetShellInitialized(isInitialized);

    private void DispatchToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = GetUiDispatcher();
        UiDispatch.Post(
            dispatcher,
            action,
            allowInline: ShouldRunInlineOnCurrentThread(
                dispatcher.CheckAccess(),
                _terminalLoopCoordinator.HasStarted));
    }

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

    private IUiDispatcher GetUiDispatcher()
        => _uiDispatcher ??= new TerminalUiDispatcher(Dispatcher.Current);

    private ComputedVisual CreateComputedVisual(Func<Visual> build)
        => _workspaceCoordinator.CreateComputedVisual(build);

    private ComputedVisual CreateUsageComputedVisual(Func<Visual> build)
        => _workspaceCoordinator.CreateUsageComputedVisual(build);

    private void ClearThreadInput()
    {
        UiDispatch.Invoke(
            GetUiDispatcher(),
            () =>
            {
                _promptDraftUiCoordinator.ClearPromptText();
                return 0;
            });
    }

    private void ClearThreadTitleDraft()
        => DispatchToUi(() => _sidebarViewModel.DraftThreadTitle = string.Empty);

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
        => await _threadStateCoordinator.PersistViewStateAsync().ConfigureAwait(false);
    internal async Task InitializeChatBackendsAsync(CancellationToken cancellationToken)
        => await _chatBackendInitializationCoordinator.InitializeAsync(cancellationToken).ConfigureAwait(false);

    internal void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads)
        => _threadStateCoordinator.ApplyRecoveredCatalogState(projects, threads);

    internal void TrySchedulePendingStartupThreadRestore(CancellationToken cancellationToken)
        => _threadStateCoordinator.TrySchedulePendingStartupThreadRestore(cancellationToken);

    private async Task RestoreStartupThreadHistoryAsync(string? threadId, CancellationToken cancellationToken)
        => await _threadStateCoordinator.RestoreStartupThreadHistoryAsync(threadId, cancellationToken).ConfigureAwait(false);

    private async Task RegisterCreatedThreadAsync(WorkThreadDescriptor thread)
        => await _threadStateCoordinator.RegisterCreatedThreadAsync(thread).ConfigureAwait(false);

    internal void OpenThread(string threadId)
        => _threadStateCoordinator.OpenThread(threadId);

    private async Task CloseSelectedThreadAsync()
        => await _threadStateCoordinator.CloseSelectedThreadAsync().ConfigureAwait(false);

    private async Task CloseThreadAsync(string threadId)
        => await _threadStateCoordinator.CloseThreadAsync(threadId).ConfigureAwait(false);

    private Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
        => _threadHistoryCoordinator.EnsureLoadedAsync(thread, cancellationToken);

    internal void HandleRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
        => _threadRuntimeEventCoordinator.ApplyRuntimeEvent(runtimeEvent);

    private OpenThreadState EnsureThreadTab(WorkThreadDescriptor thread)
        => _threadStateCoordinator.EnsureThreadTab(thread);

    private void ResetThreadTab(OpenThreadState tab)
        => _threadStateCoordinator.ResetThreadTab(tab);

    private ProjectDescriptor? GetSelectedProject()
        => _threadStateCoordinator.GetSelectedProject();

    private ProjectDescriptor? GetProjectById(string? projectId)
        => _threadStateCoordinator.GetProjectById(projectId);

    private WorkThreadDescriptor? GetSelectedThread()
        => _threadStateCoordinator.GetSelectedThread();

    private WorkThreadDescriptor? FindThread(string? threadId)
        => _threadStateCoordinator.FindThread(threadId);

}
