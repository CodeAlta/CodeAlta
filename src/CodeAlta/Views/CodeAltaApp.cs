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

internal sealed partial class CodeAltaApp : IAsyncDisposable
{
    internal static readonly Logger UiLogger = LogManager.GetLogger("CodeAlta.UI");
    private static readonly Lazy<FigletFont> WelcomeFigletFont = new(LoadWelcomeFigletFont);
    private const int MaxRecentThreadsPerProject = 3;
    private const int MaxTabTitleLength = 18;
    private const int StatusPrefixWidth = 2;
    private const string DraftTabId = "__draft__";
    private const string ReadyStatusMessage = "Prompt ready";
    private const string ThinkingStatusMessage = "Thinking...";
    private const bool DefaultAutoApproveEnabled = true;

    private readonly ProjectCatalog _projectCatalog;
    private readonly WorkThreadCatalog _threadCatalog;
    private readonly CodeAltaConfigStore _configStore;
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly AgentHub _agentHub;
    private readonly KnownProjectImporter _knownProjectImporter;
    private readonly CodeAltaOwnedServices? _ownedServices;
    private readonly CodeAltaShellController _shellController;
    private readonly RuntimeEventPump _runtimeEventPump;
    private readonly CodeAltaShellViewModel _shellViewModel = new();
    private readonly SidebarViewModel _sidebarViewModel = new();
    private readonly ThreadWorkspaceViewModel _threadWorkspaceViewModel = new();
    private readonly PromptComposerViewModel _promptComposerViewModel = new();
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates = ChatBackendPresentation.CreateBackendStates();
    private readonly Dictionary<string, ThreadTabState> _threadTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly State<int> _viewRefreshState = new(0);

    private IReadOnlyList<ProjectDescriptor> _projects = [];
    private IReadOnlyList<WorkThreadDescriptor> _threads = [];
    private WorkThreadViewState _viewState = new();
    private CodeAltaConfigDocument _globalConfig = new();
    private readonly Dictionary<string, CodeAltaConfigDocument> _projectConfigCache = new(StringComparer.OrdinalIgnoreCase);
    private CodeAltaShellView? _shellView;
    private SidebarView? _sidebarView;
    private ThreadWorkspaceView? _threadWorkspaceView;
    private SessionUsagePopupView? _sessionUsagePopupView;
    private IUiDispatcher? _uiDispatcher;
    private Spinner? _statusSpinner;
    private Visual? _threadPaneLayout;
    private Visual? _threadBottomPanel;
    private VSplitter? _threadBodySplitter;
    private ChatPromptEditor? _threadInput;
    private Visual? _threadInputView;
    private Button? _sendPromptButton;
    private CommandBar? _threadCommandBar;
    private Select<ChatBackendOption>? _chatBackendSelect;
    private Select<ChatModelOption>? _chatModelSelect;
    private Select<ChatReasoningOption>? _chatReasoningSelect;
    private CheckBox? _chatAutoScrollCheckBox;
    private TreeView? _sidebarTree;
    private TabControl? _threadTabControl;
    private TabPage? _draftTabPage;
    private bool _chatSelectorsRefreshing;
    private bool _statusBusy;
    private StatusTone _statusTone = StatusTone.Ready;
    private bool _syncingThreadTabSelection;
    private bool _syncingThreadTabPages;
    private string? _pendingThreadTabSelectionThreadId;
    private SidebarSelectionTarget? _pendingSidebarSelectionTarget;
    private bool _draftTabOpen;
    private bool _terminalLoopStarted;
    private bool _globalScopeSelected = true;
    private bool _sidebarSelectionSyncEnabled = true;
    private SidebarSelectionTarget? _lastSidebarSelectedTarget;
    private string? _selectedProjectId;
    private string? _selectedThreadId;
    private string? _pendingStartupThreadRestoreId;

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
        _configStore = new CodeAltaConfigStore(catalogOptions);
        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _agentHub = agentHub;
        _knownProjectImporter = knownProjectImporter ?? new KnownProjectImporter(agentHub, projectCatalog);
        _ownedServices = ownedServices;
        _shellController = new CodeAltaShellController(this, _knownProjectImporter, _projectCatalog, _runtimeService);
        _runtimeEventPump = new RuntimeEventPump(_runtimeService, _shellController);
    }

    /// <summary>
    /// Runs the terminal UI.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await LoadCatalogStateAsync(cancellationToken).ConfigureAwait(false);
        _shellViewModel.HeaderText = BuildHeaderText();

        _statusSpinner = new Spinner().Style(SpinnerStyles.Arc);
        _statusSpinner.IsActive(() => _shellViewModel.StatusBusy);
        _statusSpinner.IsVisible(() => _shellViewModel.StatusBusy);

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

    private sealed class ThreadTabState
    {
        public ThreadTabState(WorkThreadDescriptor thread, DocumentFlow flow, ToolCallPresenter toolCalls)
        {
            Thread = thread;
            Flow = flow;
            ToolCalls = toolCalls;
            Session = new ThreadSessionState();
            ViewModel = new ThreadTabViewModel
            {
                ThreadId = thread.ThreadId,
                Title = thread.Title,
            };
        }

        public WorkThreadDescriptor Thread { get; set; }

        public DocumentFlow Flow { get; }

        public ToolCallPresenter ToolCalls { get; }

        public ThreadSessionState Session { get; }

        public ThreadTabViewModel ViewModel { get; }

        public AgentBackendId BackendId
        {
            get => Session.BackendId;
            set => Session.BackendId = value;
        }

        public string? ModelId
        {
            get => Session.ModelId;
            set => Session.ModelId = value;
        }

        public AgentReasoningEffort? ReasoningEffort
        {
            get => Session.ReasoningEffort;
            set => Session.ReasoningEffort = value;
        }

        public bool AutoScroll
        {
            get => Session.AutoScroll;
            set => Session.AutoScroll = value;
        }

        public bool HistoryLoaded
        {
            get => Session.HistoryLoaded;
            set => Session.HistoryLoaded = value;
        }

        public bool HistoryLoading
        {
            get => Session.HistoryLoading;
            set => Session.HistoryLoading = value;
        }

        public Task? HistoryLoadTask
        {
            get => Session.HistoryLoadTask;
            set => Session.HistoryLoadTask = value;
        }

        public List<AgentEvent>? HistoryEvents
        {
            get => Session.HistoryEvents;
            set => Session.HistoryEvents = value;
        }

        public Dictionary<string, AgentPermissionRequest> PermissionRequests => Session.PermissionRequests;

        public Dictionary<string, AgentUserInputRequest> UserInputRequests => Session.UserInputRequests;

        public AgentSessionUsage? Usage
        {
            get => Session.Usage;
            set => Session.Usage = value;
        }

        public List<DocumentFlowItem>? BufferedHistoryItems { get; set; }

        public PendingAssistantState? PendingAssistant { get; set; }

        public Dictionary<string, ChatContentState> ContentStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> ActivityStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> InteractionStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> PlanStates { get; } = new(StringComparer.Ordinal);

        public TabPage? Page { get; set; }

        public TruncatedHistoryState? TruncatedHistory { get; set; }

        public bool HasSeenUserPrompt { get; set; }

        public string? StatusMessage
        {
            get => ViewModel.StatusMessage;
            set => ViewModel.StatusMessage = value;
        }

        public StatusTone StatusTone
        {
            get => ViewModel.StatusTone;
            set => ViewModel.StatusTone = value;
        }

        public bool StatusBusy
        {
            get => ViewModel.StatusBusy;
            set => ViewModel.StatusBusy = value;
        }

        public bool HasCustomStatus
        {
            get => ViewModel.HasCustomStatus;
            set => ViewModel.HasCustomStatus = value;
        }
    }

    private enum SidebarSelectionKind
    {
        GlobalScope,
        ProjectScope,
        Thread,
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

    private sealed record SidebarSelectionTarget(
        SidebarSelectionKind Kind,
        string? ProjectId,
        string? ThreadId)
    {
        public static SidebarSelectionTarget Global()
            => new(SidebarSelectionKind.GlobalScope, null, null);

        public static SidebarSelectionTarget Project(string projectId)
            => new(SidebarSelectionKind.ProjectScope, projectId, null);

        public static SidebarSelectionTarget Thread(string threadId)
            => new(SidebarSelectionKind.Thread, null, threadId);
    }

    internal sealed record InitialThreadSelection(string? SelectedThreadId, string? StartupThreadRestoreId);
}
