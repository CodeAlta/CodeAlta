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
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;
using XenoAtom.Terminal.UI.Threading;

internal sealed partial class CodeAltaTerminalUi : IAsyncDisposable
{
    private static readonly Logger UiLogger = LogManager.GetLogger("CodeAlta.UI");
    private const int MaxRecentThreadsPerProject = 3;
    private const int MaxTabTitleLength = 18;

    private readonly ProjectCatalog _projectCatalog;
    private readonly WorkThreadCatalog _threadCatalog;
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly AgentHub _agentHub;
    private readonly CodeAltaShellViewModel _viewModel = new();
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates = CreateChatBackendStates();
    private readonly Dictionary<string, ThreadTabState> _threadTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _runtimeEventsCts = new();
    private readonly State<int> _viewRefreshState = new(0);

    private IReadOnlyList<ProjectDescriptor> _projects = [];
    private IReadOnlyList<WorkThreadDescriptor> _threads = [];
    private WorkThreadViewState _viewState = new();
    private Dispatcher? _dispatcher;
    private Spinner? _statusSpinner;
    private DockLayout? _threadPaneLayout;
    private Visual? _threadBottomPanel;
    private VSplitter? _threadBodySplitter;
    private ChatPromptEditor? _threadInput;
    private Visual? _threadInputView;
    private Button? _sendPromptButton;
    private CommandBar? _threadCommandBar;
    private Select<ChatBackendOption>? _chatBackendSelect;
    private Select<ChatModelOption>? _chatModelSelect;
    private Select<ChatReasoningOption>? _chatReasoningSelect;
    private TreeView? _sidebarTree;
    private ComputedVisual? _tabStripVisual;
    private ComputedVisual? _threadHeaderVisual;
    private Task? _runtimeEventsTask;
    private Task? _startupRefreshTask;
    private CancellationTokenSource? _startupRefreshCts;
    private bool _chatSelectorsRefreshing;
    private bool _statusBusy;
    private bool _terminalLoopStarted;
    private bool _globalScopeSelected = true;
    private bool _sidebarSelectionSyncEnabled = true;
    private SidebarSelectionTarget? _lastSidebarSelectedTarget;
    private string? _selectedProjectId;
    private string? _selectedThreadId;
    private string? _pendingStartupThreadRestoreId;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeAltaTerminalUi"/> class.
    /// </summary>
    public CodeAltaTerminalUi(
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        AgentHub agentHub)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(agentHub);

        _projectCatalog = projectCatalog;
        _threadCatalog = threadCatalog;
        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _agentHub = agentHub;
    }

    /// <summary>
    /// Runs the terminal UI.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _dispatcher = Dispatcher.Current;

        await LoadCatalogStateAsync(cancellationToken).ConfigureAwait(false);
        _viewModel.HeaderText = BuildHeaderText();

        _statusSpinner = new Spinner();
        _statusSpinner.IsActive(() => _viewModel.StatusBusy);
        _statusSpinner.IsVisible(() => _viewModel.StatusBusy);

        SetStatus("Connecting to available backends...", showSpinner: true);

        var root = new DockLayout(
            top: new TextBlock
            {
                Wrap = false,
            }.Text(() => _viewModel.HeaderText),
            content: BuildMainView(),
            bottom: null);

        _runtimeEventsTask = Task.Run(() => PumpRuntimeEventsAsync(_runtimeEventsCts.Token), CancellationToken.None);
        await Terminal.RunAsync(
                root,
                () =>
                {
                    _terminalLoopStarted = true;
                    StartStartupRefresh(cancellationToken);
                    TrySchedulePendingStartupThreadRestore(cancellationToken);
                    SyncSidebarSelection();
                    return TerminalLoopResult.Continue;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _runtimeEventsCts.Cancel();

        if (_runtimeEventsTask is not null)
        {
            try
            {
                await _runtimeEventsTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _startupRefreshCts?.Cancel();
        if (_startupRefreshTask is not null)
        {
            try
            {
                await _startupRefreshTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _runtimeEventsCts.Dispose();
        _startupRefreshCts?.Dispose();
    }

    private sealed class ThreadTabState
    {
        public ThreadTabState(WorkThreadDescriptor thread, DocumentFlow flow)
        {
            Thread = thread;
            Flow = flow;
        }

        public WorkThreadDescriptor Thread { get; set; }

        public DocumentFlow Flow { get; }

        public AgentBackendId BackendId { get; set; } = AgentBackendIds.Codex;

        public string? ModelId { get; set; }

        public AgentReasoningEffort? ReasoningEffort { get; set; }

        public bool HistoryLoaded { get; set; }

        public bool HistoryLoading { get; set; }

        public Task? HistoryLoadTask { get; set; }

        public PendingAssistantState? PendingAssistant { get; set; }

        public Dictionary<string, ChatContentState> ContentStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> ActivityStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> InteractionStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> PlanStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ToolCallEntryState> ToolCallStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, AgentPermissionRequest> PermissionRequests { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, AgentUserInputRequest> UserInputRequests { get; } = new(StringComparer.Ordinal);

        public ToolCallGroupState? ActiveToolCallGroup { get; set; }

        public bool HasSeenUserPrompt { get; set; }

        public string? StatusMessage { get; set; }

        public StatusTone StatusTone { get; set; } = StatusTone.Ready;

        public bool StatusBusy { get; set; }

        public bool HasCustomStatus { get; set; }
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
