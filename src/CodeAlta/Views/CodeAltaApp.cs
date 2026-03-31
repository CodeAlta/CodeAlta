using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.Catalog;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Tabs;
using CodeAlta.Presentation.Threads;
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
    private readonly ShellWorkspaceContext _shellWorkspaceContext;
    private readonly ThreadSelectionContext _threadSelectionContext;
    private readonly ThreadTabContext _threadTabContext;
    private readonly WorkspaceRefreshContext _workspaceRefreshContext;
    private CodeAltaShellView? _shellView;
    private ThreadWorkspaceView? _threadWorkspaceView;
    private SessionUsagePresenter? _sessionUsagePresenter;
    private ThreadInfoPresenter? _threadInfoPresenter;
    private IUiDispatcher? _uiDispatcher;
    private Task<ShellThreadStateCoordinator.InitialCatalogState>? _initialCatalogStateTask;
    private bool _initialCatalogStateResolved;
    private bool _disableTerminalLoopCallback;
    private WorkThreadViewState _viewState
    {
        get => _threadStateCoordinator.ViewState;
        set => _threadStateCoordinator.ViewState = value;
    }
    private Visual? ThreadPaneLayout => _threadWorkspaceView?.ThreadPaneLayout;
    private VSplitter? ThreadBodySplitter => _threadWorkspaceView?.ThreadBodySplitter;
    private ChatPromptEditor? ThreadInput => _threadWorkspaceView?.ThreadInput;
    private CommandBar? ThreadCommandBar => _threadWorkspaceView?.ThreadCommandBar;
    private TabControl? ThreadTabControl => _threadWorkspaceView?.ThreadTabControl;

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
        var composition = CodeAltaFrontendComposition.Create(
            projectCatalog,
            threadCatalog,
            runtimeService,
            catalogOptions,
            agentHub,
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
                IsSelectedThread = IsSelectedThread,
                ApplyDraftBackendPreference = ApplyDraftBackendPreference,
                RememberGlobalBackendPreference = RememberGlobalBackendPreference,
                InvalidateSelectedSessionUsage = InvalidateSelectedSessionUsage,
                RefreshHeaderAndThreadWorkspace = RefreshHeaderAndThreadWorkspace,
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
                SyncPromptText = session => _promptDraftUiCoordinator!.SyncPromptText(session),
                UpdatePromptAvailabilityUi = UpdatePromptAvailabilityUi,
                SyncThreadTabControl = SyncThreadTabControl,
                DispatchToUi = DispatchToUi,
                DispatchToUiDeferred = DispatchToUiDeferred,
                VerifyBindableAccess = VerifyBindableAccess,
                GetAutoApproveEnabled = GetAutoApproveEnabled,
                RefreshShellChrome = RefreshShellChrome,
                SetThreadStatus = (tab, message, showSpinner, tone) => SetThreadStatus(tab, message, showSpinner, tone),
                ClearThreadStatus = ClearThreadStatus,
                TrySetPromptUnavailableStatus = TrySetPromptUnavailableStatus,
                SetReadyStatusForCurrentSelection = SetReadyStatusForCurrentSelection,
                ClearDraftPromptText = () => _promptDraftUiCoordinator!.ClearDraftPromptText(),
                ClearPromptText = () => _promptDraftUiCoordinator!.ClearPromptText(),
                IsPromptTextEmpty = () => ReadBindableState(() => string.IsNullOrWhiteSpace(_promptDraftUiCoordinator!.PromptText)),
                RestorePromptText = prompt => DispatchToUi(() => _promptDraftUiCoordinator!.PromptText = prompt),
                PersistViewStateAsync = PersistViewStateAsync,
                RegisterCreatedThreadAsync = RegisterCreatedThreadAsync,
            });
        _backendPreferences = composition.BackendPreferences;
        _shellController = composition.ShellController;
        _runtimeEventPump = composition.RuntimeEventPump;
        _terminalLoopCoordinator = composition.TerminalLoopCoordinator;
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
        _chatSelectorCoordinator = composition.ChatSelectorCoordinator;
        _chatPreferenceContext = composition.ChatPreferenceContext;
        _shellWorkspaceContext = composition.ShellWorkspaceContext;
        _threadSelectionContext = composition.ThreadSelectionContext;
        _workspaceRefreshContext = composition.WorkspaceRefreshContext;
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
        _shellCommandSurfaceCoordinator = new ShellCommandSurfaceCoordinator(
            _promptComposerViewModel,
            _threadWorkspaceViewModel,
            _threadCommandCoordinator,
            () => ReadBindableState(() => _promptDraftUiCoordinator.PromptText),
            CloseCurrentShellTabAsync,
            SetStatus,
            () => ThreadPaneLayout?.GetAbsoluteBounds(),
            () => ThreadInput,
            GetSelectedThread,
            EnsureThreadTab,
            () => EnsureSessionUsagePresenter().TogglePopupFromIndicator(),
            () =>
            {
                if (ThreadInput is not null)
                {
                    EnsureThreadInfoPresenter().TogglePopup(ThreadInput);
                }
            },
            () => _threadWorkspaceView?.OpenExpandedPromptDialog());
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
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _runtimeEventPump.DisposeAsync().ConfigureAwait(false);
        await _shellController.DisposeAsync().ConfigureAwait(false);
        await _promptDraftUiCoordinator.DisposeAsync().ConfigureAwait(false);
        if (_ownedServices is not null) await _ownedServices.DisposeAsync().ConfigureAwait(false);
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

    private string? GetExpandedSidebarProjectId()
        => GetSelectedThread()?.ProjectRef ?? _threadStateCoordinator.Selection.SelectedProjectId;

    private SidebarSelectionTarget ResolveSidebarTargetForCurrentState()
        => SidebarSelectionResolver.ResolveCurrentTarget(
            _threadStateCoordinator.Selection.SelectedThreadId,
            _threadStateCoordinator.Selection.SelectedProjectId,
            _threadStateCoordinator.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true });

    private void RefreshSidebarProjection()
    {
        _sidebarCoordinator.RefreshProjection(
            _threadStateCoordinator.Projects,
            _threadStateCoordinator.Threads,
            GetExpandedSidebarProjectId(),
            ResolveSidebarTargetForCurrentState(),
            _threadStateCoordinator.NavigatorSettings,
            threadId => _threadStateCoordinator.FindOpenThread(threadId) is { } tab
                ? new ThreadVisualState(tab.StatusBusy, tab.HasPromptDraft)
                : new ThreadVisualState(false, _promptDraftUiCoordinator.HasPersistedPromptDraft(threadId)),
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
        _sidebarCoordinator.RefreshRecency(DateTimeOffset.UtcNow, VerifyBindableAccess);
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
            _shellCommandSurfaceCoordinator.BuildWorkspaceCommandBindings(),
            () => CreateUsageComputedVisual(EnsureSessionUsagePresenter().BuildIndicatorVisual),
            () => EnsureSessionUsagePresenter().TogglePopupFromIndicator(),
            anchor => EnsureThreadInfoPresenter().TogglePopup(anchor),
            () => _ = _shellCommandSurfaceCoordinator.ShowHelpAsync(),
            () => _shellCommandSurfaceCoordinator.ShowCommandPalette(),
            acceptedPrompt => _ = _shellCommandSurfaceCoordinator.HandleAcceptedPromptAsync(acceptedPrompt),
            () => _ = _shellCommandSurfaceCoordinator.SubmitCurrentPromptAsync(steer: false),
            () => _ = _shellCommandSurfaceCoordinator.SubmitCurrentPromptAsync(steer: true),
            () => _ = _threadCommandCoordinator.ClearSelectedThreadQueueAsync(),
            queuedPromptId => _ = _threadCommandCoordinator.ConvertSelectedThreadQueuedPromptToSteerAsync(queuedPromptId),
            queuedPromptId => _threadCommandCoordinator.DeleteSelectedThreadQueuedPrompt(queuedPromptId),
            (queuedPromptId, remainingCount) => _threadCommandCoordinator.UpdateSelectedThreadQueuedPromptCount(queuedPromptId, remainingCount),
            (queuedPromptId, text) => _threadCommandCoordinator.UpdateSelectedThreadQueuedPromptText(queuedPromptId, text),
            () => _ = _shellCommandSurfaceCoordinator.SubmitCurrentDelegationAsync(),
            () => _ = _shellCommandSurfaceCoordinator.AbortSelectedThreadAsync(),
            () => _ = _shellCommandSurfaceCoordinator.CompactSelectedThreadAsync(),
            () => _ = _shellCommandSurfaceCoordinator.CloseCurrentTabAsync(),
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
            _shellView.Root.AddCommand(new Command
            {
                Id = "CodeAlta.Shell.CommandPalette",
                LabelMarkup = "Command Palette",
                Name = "palette",
                SearchText = "commands palette command search",
                DescriptionMarkup = "Search and run available shell commands.",
                Gesture = new KeyGesture(TerminalChar.CtrlP, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandBar,
                Execute = _ => _shellCommandSurfaceCoordinator.ShowCommandPalette(),
            });
        }

        return _shellView;
    }

    internal static Visual CreateThreadTabPageContentPlaceholder() => new Placeholder { IsVisible = false };

    private void RefreshShellChrome()
        => _workspaceCoordinator.RefreshShellChrome();
    internal void RefreshCatalogAndThreadWorkspace()
    {
        _threadInfoPresenter?.InvalidateSelection();
        _workspaceCoordinator.RefreshCatalogAndThreadWorkspace();
    }
    private void RefreshHeaderAndThreadWorkspace()
        => _workspaceCoordinator.RefreshHeaderAndThreadWorkspace();
    private void RefreshSelectionAndThreadWorkspace()
    {
        _threadInfoPresenter?.InvalidateSelection();
        _workspaceCoordinator.RefreshSelectionAndThreadWorkspace();
    }
    internal void SelectGlobalScope() => _threadStateCoordinator.SelectGlobalScope();
    internal void SelectProjectScope(string projectId) => _threadStateCoordinator.SelectProjectScope(projectId);
    private void EnsureSelectionDefaults() => _threadStateCoordinator.EnsureSelectionDefaults();
    private string BuildHeaderText() => _workspaceCoordinator.BuildHeaderText();
    internal void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info) => _workspaceCoordinator.SetStatus(message, showSpinner, tone);

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
            allowInline: ShouldRunInlineOnCurrentThread(dispatcher.CheckAccess(), _terminalLoopCoordinator.HasStarted));
    }

    private void DispatchToUiDeferred(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = GetUiDispatcher();
        UiDispatch.Post(
            dispatcher,
            action,
            allowInline: ShouldRunDeferredUiActionInlineOnCurrentThread(
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

    internal static bool ShouldRunDeferredUiActionInlineOnCurrentThread(bool dispatcherHasAccess, bool terminalLoopStarted)
        => !terminalLoopStarted && dispatcherHasAccess;

    private IUiDispatcher GetUiDispatcher()
        => _uiDispatcher ??= new TerminalUiDispatcher(Dispatcher.Current);

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
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshSelectionAndThreadWorkspace();
    }

    private async Task CloseDraftTabAsync()
    {
        if (_threadStateCoordinator.Selection.Target is WorkspaceTarget.Draft)
        {
            _threadStateCoordinator.SelectedThreadId = _viewState.OpenThreadIds.FirstOrDefault();
            _viewState.SelectedThreadId = _threadStateCoordinator.Selection.SelectedThreadId;
        }

        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshSelectionAndThreadWorkspace();
    }

    private Task CloseCurrentShellTabAsync()
        => GetSelectedThread() is not null ? CloseSelectedThreadAsync() : CloseDraftTabAsync();

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
