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

    private readonly ChatBackendPreferenceCoordinator _backendPreferences;
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly AgentHub _agentHub;
    private readonly KnownProjectImporter _knownProjectImporter;
    private readonly CodeAltaOwnedServices? _ownedServices;
    private readonly CodeAltaShellController _shellController;
    private readonly RuntimeEventPump _runtimeEventPump;
    private readonly ShellThreadStateCoordinator _threadStateCoordinator;
    private readonly ShellWorkspaceCoordinator _workspaceCoordinator;
    private readonly ThreadHistoryCoordinator _threadHistoryCoordinator;
    private readonly ThreadRuntimeEventCoordinator _threadRuntimeEventCoordinator;
    private readonly ThreadCommandCoordinator _threadCommandCoordinator;
    private readonly CodeAltaShellViewModel _shellViewModel = new();
    private readonly SidebarViewModel _sidebarViewModel = new();
    private readonly ThreadWorkspaceViewModel _threadWorkspaceViewModel = new();
    private readonly PromptComposerViewModel _promptComposerViewModel = new();
    private readonly SessionUsageViewModel _sessionUsageViewModel = new();
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates = ChatBackendPresentation.CreateBackendStates();
    private readonly SidebarCoordinator _sidebarCoordinator;
    private readonly ChatSelectorCoordinator _chatSelectorCoordinator;
    private readonly ThreadTabStripCoordinator _threadTabStripCoordinator;

    private CodeAltaShellView? _shellView;
    private ThreadWorkspaceView? _threadWorkspaceView;
    private SessionUsagePresenter? _sessionUsagePresenter;
    private IUiDispatcher? _uiDispatcher;
    private bool _terminalLoopStarted;

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

        _backendPreferences = new ChatBackendPreferenceCoordinator(new CodeAltaConfigStore(catalogOptions), UiLogger);
        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _agentHub = agentHub;
        _knownProjectImporter = knownProjectImporter ?? new KnownProjectImporter(agentHub, projectCatalog);
        _ownedServices = ownedServices;
        _shellController = new CodeAltaShellController(
            new CodeAltaShellBridge(this),
            _knownProjectImporter,
            new ProjectCatalogLoader(projectCatalog),
            new RecoverableThreadSource(_runtimeService));
        _runtimeEventPump = new RuntimeEventPump(_runtimeService, _shellController);
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
        _workspaceCoordinator = new ShellWorkspaceCoordinator(
            _shellViewModel,
            _sessionUsageViewModel,
            _chatBackendStates,
            GetSelectedThread,
            GetSelectedProject,
            EnsureThreadTab,
            () => _globalScopeSelected,
            GetPreferredBackendId,
            () =>
            {
                var hasStatus = TryGetPromptUnavailableStatus(out var message, out var tone);
                return (hasStatus, message, tone);
            },
            IsSelectedThread,
            () => ThreadPaneLayout,
            () => ThreadBodySplitter,
            () => ThreadInput,
            EnsureSelectionDefaults,
            RefreshSidebarProjection,
            SyncSidebarSelectionToCurrentState,
            () => RefreshChatSelectorsForDraftScope(),
            RefreshChatSelectorsForThread,
            UpdatePromptAvailabilityUi,
            SyncThreadTabControl,
            DispatchToUi,
            VerifyBindableAccess,
            _catalogOptions.GlobalRoot);
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
        _threadRuntimeEventCoordinator = new ThreadRuntimeEventCoordinator(
            threadId => FindThread(threadId),
            threadId => _threadStateCoordinator.FindOpenThread(threadId),
            GetAutoApproveEnabled,
            IsSelectedThread,
            InvalidateSelectedSessionUsage,
            RefreshShellChrome,
            SetStatus,
            (tab, message, showSpinner, tone) => SetThreadStatus(tab, message, showSpinner, tone),
            ClearThreadStatus);
        _threadCommandCoordinator = new ThreadCommandCoordinator(
            _runtimeService,
            _catalogOptions,
            _chatBackendStates,
            GetUiDispatcher,
            () => ThreadInput,
            () => ChatBackendSelect,
            () => ChatModelSelect,
            () => ChatReasoningSelect,
            GetSelectedThread,
            GetSelectedProject,
            projectId => GetProjectById(projectId),
            EnsureThreadTab,
            threadId => _threadStateCoordinator.FindOpenThread(threadId),
            EnsureThreadHistoryLoadedAsync,
            () => _globalScopeSelected,
            () => _selectedProjectId,
            GetPreferredBackendId,
            () => TrySetPromptUnavailableStatus(),
            CreateGlobalThreadAsync,
            CreateProjectThreadAsync,
            RegisterDelegatedThread,
            PersistViewStateAsync,
            GetAutoApproveEnabled,
            RememberThreadPreference,
            SetReadyStatusForCurrentSelection,
            ClearThreadInput,
            RefreshHeaderAndThreadWorkspace,
            RefreshCatalogAndThreadWorkspace,
            SetStatus,
            (tab, message, showSpinner, tone) => SetThreadStatus(tab, message, showSpinner, tone),
            _threadRuntimeEventCoordinator.TryRenderInteraction);
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
            _threadStateCoordinator.Projects,
            _threadStateCoordinator.Threads,
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

        RefreshCatalogAndThreadWorkspace();

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
        => _workspaceCoordinator.CreateComputedVisual(build);

    private ComputedVisual CreateUsageComputedVisual(Func<Visual> build)
        => _workspaceCoordinator.CreateUsageComputedVisual(build);

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
        => await _threadStateCoordinator.PersistViewStateAsync().ConfigureAwait(false);

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
        => await _threadStateCoordinator.LoadCatalogStateAsync(cancellationToken).ConfigureAwait(false);

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
                RefreshHeaderAndThreadWorkspace();
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
                    RefreshHeaderAndThreadWorkspace();
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
                    RefreshHeaderAndThreadWorkspace();
                });
        }
    }

    internal void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads)
        => _threadStateCoordinator.ApplyRecoveredCatalogState(projects, threads);

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
        => _threadStateCoordinator.TrySchedulePendingStartupThreadRestore(cancellationToken);

    private async Task RestoreStartupThreadHistoryAsync(string? threadId, CancellationToken cancellationToken)
        => await _threadStateCoordinator.RestoreStartupThreadHistoryAsync(threadId, cancellationToken).ConfigureAwait(false);

    private async Task<WorkThreadDescriptor?> CreateGlobalThreadAsync()
    {
        try
        {
            SetStatus("Creating global thread...", showSpinner: true);
            var title = ReadBindableState(() => _sidebarViewModel.DraftThreadTitle?.Trim());
            var executionOptions = _threadCommandCoordinator.BuildPreferredExecutionOptions(
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
            var executionOptions = _threadCommandCoordinator.BuildPreferredExecutionOptions(
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
        => await _threadStateCoordinator.RegisterCreatedThreadAsync(thread).ConfigureAwait(false);

    private OpenThreadState RegisterDelegatedThread(WorkThreadDescriptor child, OpenThreadState sourceTab)
        => _threadStateCoordinator.RegisterDelegatedThread(child, sourceTab);

    internal void OpenThread(string threadId)
        => _threadStateCoordinator.OpenThread(threadId);

    private async Task CloseSelectedThreadAsync()
        => await _threadStateCoordinator.CloseSelectedThreadAsync().ConfigureAwait(false);

    private async Task CloseThreadAsync(string threadId)
        => await _threadStateCoordinator.CloseThreadAsync(threadId).ConfigureAwait(false);

    private Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
        => _threadHistoryCoordinator.EnsureLoadedAsync(thread, cancellationToken);

    private Task SendSelectedThreadPromptAsync(bool steer)
        => _threadCommandCoordinator.SendSelectedThreadPromptAsync(steer);

    private Task DelegateSelectedThreadAsync()
        => _threadCommandCoordinator.DelegateSelectedThreadAsync();

    private Task AbortSelectedThreadAsync()
        => _threadCommandCoordinator.AbortSelectedThreadAsync();

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

    private WorkThreadDescriptor[] GetThreadsForProject(string projectId, bool includeInternal)
        => _threadStateCoordinator.GetThreadsForProject(projectId, includeInternal);

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
