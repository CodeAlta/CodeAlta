using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.DotNet;
using CodeAlta.Mcp;
using CodeAlta.Orchestration.Mcp;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;
using CodeAlta.Search;
using CodeAlta.Workspaces;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;
using XenoAtom.Terminal.UI.Threading;
using XenoAtom.Logging;

internal sealed class CodeAltaTerminalUi : IAsyncDisposable
{
    private static readonly Logger ChatLogger = LogManager.GetLogger("CodeAlta.Chat");
    private readonly WorkspaceCatalog _workspaceCatalog;
    private readonly WorkspaceResolver _workspaceResolver;
    private readonly TaskRepository _taskRepository;
    private readonly SearchService _searchService;
    private readonly DotNetIndexService _dotNetIndexService;
    private readonly DotNetDiagnosticsService _dotNetDiagnosticsService;
    private readonly Indexer _indexer;
    private readonly CodeAltaMcpServerFactory _mcpFactory;
    private readonly McpToolBridge _mcpToolBridge;
    private readonly AgentHub _agentHub;
    private readonly Dictionary<string, ChatAgentConnection> _chatConnections = new(StringComparer.OrdinalIgnoreCase);

    private DockLayout? _root;
    private TextBlock? _header;
    private TextBlock? _status;
    private Spinner? _statusSpinner;
    private Dispatcher? _dispatcher;

    private TerminalScreen _screen = TerminalScreen.Home;
    private string? _activeWorkspaceKey;
    private string? _activeProjectKey;

    private TextBlock? _workspacesOutput;
    private TextBox? _workspaceKeyInput;
    private TextBlock? _tasksOutput;
    private TextBox? _taskTitleInput;
    private TextBlock? _searchOutput;
    private TextBox? _searchQueryInput;
    private TextBlock? _jobsOutput;
    private TextBlock? _mcpOutput;
    private DocumentFlow? _chatFlow;
    private ChatPromptEditor? _chatInput;
    private Visual? _chatInputView;
    private MarkdownMarkupConverter? _chatMarkdownConverter;
    private Select<ChatBackendOption>? _chatBackendSelect;
    private Select<ChatModelOption>? _chatModelSelect;
    private Select<ChatReasoningOption>? _chatReasoningSelect;
    private CheckBox? _chatAutoApproveCheckBox;
    private Markup? _chatBackendStatusMarkup;
    private AgentBackendId _chatBackendId = AgentBackendIds.Codex;
    private bool _chatBackendsInitializing;
    private bool _chatSelectorsRefreshing;
    private volatile bool _chatAutoApproveEnabled = true;
    private bool _chatBindingEventsSubscribed;
    private readonly State<bool> _chatAutoApproveState = new(true);
    private readonly State<int> _chatBackendSelectedIndex = new(0);
    private readonly State<int> _chatModelSelectedIndex = new(0);
    private readonly State<int> _chatReasoningSelectedIndex = new(0);
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates = CreateChatBackendStates();
    private readonly object _chatTimelineLock = new();
    private PendingAssistantState? _chatPendingAssistant;
    private readonly Dictionary<string, ChatContentState> _chatContentStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _chatActivityStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _chatInteractionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatStatusState> _chatPlanStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentPermissionRequest> _chatPermissionRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentUserInputRequest> _chatUserInputRequests = new(StringComparer.Ordinal);

    public CodeAltaTerminalUi(
        WorkspaceCatalog workspaceCatalog,
        WorkspaceResolver workspaceResolver,
        TaskRepository taskRepository,
        SearchService searchService,
        DotNetIndexService dotNetIndexService,
        DotNetDiagnosticsService dotNetDiagnosticsService,
        Indexer indexer,
        CodeAltaMcpServerFactory mcpFactory,
        McpToolBridge mcpToolBridge,
        AgentHub agentHub)
    {
        ArgumentNullException.ThrowIfNull(workspaceCatalog);
        ArgumentNullException.ThrowIfNull(workspaceResolver);
        ArgumentNullException.ThrowIfNull(taskRepository);
        ArgumentNullException.ThrowIfNull(searchService);
        ArgumentNullException.ThrowIfNull(dotNetIndexService);
        ArgumentNullException.ThrowIfNull(dotNetDiagnosticsService);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(mcpFactory);
        ArgumentNullException.ThrowIfNull(mcpToolBridge);
        ArgumentNullException.ThrowIfNull(agentHub);

        _workspaceCatalog = workspaceCatalog;
        _workspaceResolver = workspaceResolver;
        _taskRepository = taskRepository;
        _searchService = searchService;
        _dotNetIndexService = dotNetIndexService;
        _dotNetDiagnosticsService = dotNetDiagnosticsService;
        _indexer = indexer;
        _mcpFactory = mcpFactory;
        _mcpToolBridge = mcpToolBridge;
        _agentHub = agentHub;
        _chatConnections.Add(AgentBackendIds.Codex.Value, new ChatAgentConnection(agentHub, HandleChatAgentEvent));
        _chatConnections.Add(AgentBackendIds.Copilot.Value, new ChatAgentConnection(agentHub, HandleChatAgentEvent));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _dispatcher = Dispatcher.Current;
        SubscribeChatBindingEvents();
        _header = new TextBlock
        {
            Wrap = false,
            Text = BuildHeaderText(),
        };

        _status = new TextBlock
        {
            Wrap = true,
            Text = "Ready. Use the buttons below to navigate.",
        };
        _statusSpinner = new Spinner()
            .Style(SpinnerStyles.Dots)
            .Tone(ControlTone.Primary);
        _statusSpinner.IsActive = false;
        _statusSpinner.IsVisible = false;

        var statusBar = new HStack(
            [
                _statusSpinner,
                _status,
            ])
        {
            Spacing = 1,
        };

        var footer = BuildFooter();
        var bottom = new VStack(
            [
                footer,
                statusBar,
            ])
        {
            Spacing = 1,
        };

        _root = new DockLayout(
            top: _header,
            content: BuildHomeView(),
            bottom: bottom);

        await Terminal.RunAsync(_root, () => TerminalLoopResult.Continue, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        UnsubscribeChatBindingEvents();
        foreach (var connection in _chatConnections.Values)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Visual BuildFooter()
    {
        return new WrapHStack(
            [
                new Button(new TextBlock("Home")).Click(() => Show(TerminalScreen.Home)),
                new Button(new TextBlock("Chat")).Click(() => Show(TerminalScreen.Chat)),
                new Button(new TextBlock("Workspaces")).Click(() => Show(TerminalScreen.Workspaces)),
                new Button(new TextBlock("Tasks")).Click(() => Show(TerminalScreen.Tasks)),
                new Button(new TextBlock("Search")).Click(() => Show(TerminalScreen.Search)),
                new Button(new TextBlock("Jobs")).Click(() => Show(TerminalScreen.Jobs)),
                new Button(new TextBlock("MCP")).Click(() => Show(TerminalScreen.Mcp)),
                //new Button(new TextBlock("Quit")).Click(() => _app?.Stop()),
            ])
        {
            Spacing = 2,
            RunSpacing = 0,
        };
    }

    private void Show(TerminalScreen screen)
    {
        _screen = screen;
        PostToUi(
            () =>
            {
                if (_root is null || _header is null)
                {
                    return;
                }

                _header.Text = BuildHeaderText();
                _root.Content = screen switch
                {
                    TerminalScreen.Home => BuildHomeView(),
                    TerminalScreen.Chat => BuildChatView(),
                    TerminalScreen.Workspaces => BuildWorkspacesView(),
                    TerminalScreen.Tasks => BuildTasksView(),
                    TerminalScreen.Search => BuildSearchView(),
                    TerminalScreen.Jobs => BuildJobsView(),
                    TerminalScreen.Mcp => BuildMcpView(),
                    _ => BuildHomeView(),
                };
            });
    }

    private Visual BuildHomeView()
    {
        return new VStack(
            [
                new TextBlock("CodeAlta (preview)"),
                new TextBlock("This is a minimal headless TUI host over durable state (SQLite + artifacts) and MCP tools."),
                new TextBlock("Use Workspaces/Tasks/Search/Jobs/MCP to exercise services without relying on a full agent runtime."),
                new TextBlock("Note: Some capabilities depend on optional local machine configuration (e.g., sqlite-vec extension path)."),
            ])
        {
            Spacing = 1,
        };
    }

    private Visual BuildChatView()
    {
        _chatMarkdownConverter ??= new MarkdownMarkupConverter();

        _chatFlow ??= new DocumentFlow
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            ItemPadding = new Thickness(1, 1, 0, 0),
        };
        _chatFlow.ScrollToTail();

        _chatInput ??= new ChatPromptEditor(
                text => _ = SendChatMessageAsync(text))
            .PromptMarkup("[primary]>[/] ")
            .ContinuationPromptMarkup("[muted]·[/] ")
            .EnableWordHints(false)
            .Highlighter(HighlightMarkdown)
            .MinHeight(3)
            .MaxHeight(9);
        _chatInputView ??= _chatInput.Scrollable();
        _chatBackendSelect ??= new Select<ChatBackendOption>()
            .SelectedIndex(_chatBackendSelectedIndex)
            .SelectionChanged((_, e) => OnChatBackendSelectionChanged(e.NewIndex))
            .MinWidth(12)
            .MaxWidth(20);
        _chatModelSelect ??= new Select<ChatModelOption>()
            .SelectedIndex(_chatModelSelectedIndex)
            .SelectionChanged((_, e) => OnChatModelSelectionChanged(e.NewIndex))
            .MinWidth(18)
            .MaxWidth(36)
            .HorizontalAlignment(Align.Stretch);
        _chatReasoningSelect ??= new Select<ChatReasoningOption>()
            .SelectedIndex(_chatReasoningSelectedIndex)
            .SelectionChanged((_, e) => OnChatReasoningSelectionChanged(e.NewIndex))
            .MinWidth(12)
            .MaxWidth(22);
        _chatAutoApproveCheckBox ??= new CheckBox("Auto-Approve")
            .IsChecked(_chatAutoApproveState);
        _chatBackendStatusMarkup ??= new Markup(string.Empty)
        {
            Wrap = true,
        };

        RefreshChatSelectors();
        _ = InitializeChatBackendsAsync();

        var help = new Markup(
            "[dim]Enter accepts the prompt. Use markdown for formatting; assistant messages will render markdown.[/]");

        var controls = new WrapHStack(
            [
                new Button(new TextBlock("Clear")).Click(ClearChat),
                new Button(new TextBlock("Refresh Backends")).Click(() => _ = RefreshChatBackendsAsync()),
                new Button(new TextBlock("Abort")).Click(() => _ = AbortChatAsync()),
                new Button(new TextBlock("Send")).Click(() => _chatInput?.Accept()),
            ])
        {
            Spacing = 2,
            RunSpacing = 0,
        };

        return new DockLayout(
            top: new VStack([new TextBlock("Chat"), help, controls]) { Spacing = 1 },
            content: _chatFlow,
            bottom: new VStack(
                [
                    _chatInputView,
                    new WrapHStack(
                        [
                            new VStack([new TextBlock("Agent"), _chatBackendSelect]) { Spacing = 0 },
                            new VStack([new TextBlock("Model"), _chatModelSelect]) { Spacing = 0 },
                            new VStack([new TextBlock("Reasoning"), _chatReasoningSelect]) { Spacing = 0 },
                            new VStack([new TextBlock("Approvals"), _chatAutoApproveCheckBox]) { Spacing = 0 },
                        ])
                    {
                        Spacing = 2,
                        RunSpacing = 1,
                    },
                    _chatBackendStatusMarkup,
                ])
            {
                Spacing = 1,
            });

        void HighlightMarkdown(in PromptEditorHighlightRequest request, List<StyledRun> runs)
        {
            var converter = _chatMarkdownConverter;
            if (converter is null)
            {
                return;
            }

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

    private ChatAgentConnection GetChatConnection(AgentBackendId backendId)
    {
        if (_chatConnections.TryGetValue(backendId.Value, out var connection))
        {
            return connection;
        }

        throw new KeyNotFoundException($"No chat connection is registered for backend '{backendId.Value}'.");
    }

    private async Task InitializeChatBackendsAsync()
    {
        if (_chatBackendsInitializing ||
            _chatBackendStates.Values.All(static state => state.Availability != ChatBackendAvailability.Unknown))
        {
            return;
        }

        _chatBackendsInitializing = true;
        RefreshChatBackendStatusMarkup();
        try
        {
            var initializationTasks = _chatBackendStates.Values
                .Select(InitializeChatBackendAsync)
                .ToArray();
            await Task.WhenAll(initializationTasks).ConfigureAwait(false);
        }
        finally
        {
            _chatBackendsInitializing = false;
            RefreshChatSelectors();
        }

        if (_chatBackendStates.Values.Any(static state => state.Availability == ChatBackendAvailability.Ready))
        {
            SetStatus("Chat backends initialized.");
        }
        else
        {
            SetStatus("No supported chat backend was detected.");
        }
    }

    private async Task RefreshChatBackendsAsync()
    {
        if (_chatBackendsInitializing)
        {
            return;
        }

        foreach (var backendState in _chatBackendStates.Values)
        {
            backendState.Availability = ChatBackendAvailability.Unknown;
            backendState.StatusMessage = "Not initialized.";
            backendState.Models.Clear();
            backendState.SelectedModelId = null;
            backendState.SelectedReasoningEffort = null;
        }

        RefreshChatSelectors();
        await InitializeChatBackendsAsync().ConfigureAwait(false);
    }

    private async Task InitializeChatBackendAsync(ChatBackendState backendState)
    {
        backendState.Availability = ChatBackendAvailability.Connecting;
        backendState.StatusMessage = "Connecting...";
        RefreshChatSelectors();

        try
        {
            var models = await _agentHub.ListModelsAsync(backendState.BackendId).ConfigureAwait(false);

            backendState.Models.Clear();
            backendState.Models.AddRange(models);
            if (backendState.Models.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(backendState.SelectedModelId) ||
                    backendState.Models.All(model => !string.Equals(model.Id, backendState.SelectedModelId, StringComparison.Ordinal)))
                {
                    backendState.SelectedModelId = backendState.Models[0].Id;
                }
            }
            else
            {
                backendState.SelectedModelId = null;
            }

            backendState.SelectedReasoningEffort = NormalizeReasoningEffort(
                backendState.SelectedReasoningEffort,
                GetSelectedModel(backendState));
            RefreshChatSelectors();

            await EnsureChatSessionAsync(backendState.BackendId, updateStatus: false).ConfigureAwait(false);

            backendState.Availability = ChatBackendAvailability.Ready;
            backendState.StatusMessage = BuildReadyStatusMessage(backendState);
        }
        catch (FileNotFoundException ex)
        {
            backendState.Availability = ChatBackendAvailability.Unsupported;
            backendState.StatusMessage = BuildUnsupportedBackendMessage(backendState, ex.Message);
        }
        catch (Exception ex)
        {
            backendState.Availability = ChatBackendAvailability.Failed;
            backendState.StatusMessage = BuildFailedBackendMessage(backendState, ex.Message);
        }

        RefreshChatSelectors();
    }

    private void OnChatBackendSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var backendOptions = BuildChatBackendOptions();
        if ((uint)newIndex >= (uint)backendOptions.Count)
        {
            return;
        }

        _chatBackendId = ResolveChatBackendSelection(_chatBackendId, backendOptions[newIndex].BackendId, adoptRequestedBackend: true);
        RefreshChatSelectors();
        RefreshChatBackendStatusMarkup();
        _ = EnsureSelectedChatBackendReadyAsync();
    }

    private void OnChatModelSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var backendState = _chatBackendStates[_chatBackendId.Value];
        var modelOptions = BuildChatModelOptions(backendState);
        if (modelOptions.Count == 0)
        {
            backendState.SelectedModelId = null;
            backendState.SelectedReasoningEffort = NormalizeReasoningEffort(
                backendState.SelectedReasoningEffort,
                model: null);
            RefreshChatSelectors();
            return;
        }

        var clampedIndex = Math.Clamp(newIndex, 0, modelOptions.Count - 1);
        backendState.SelectedModelId = modelOptions[clampedIndex].ModelId;
        backendState.SelectedReasoningEffort = NormalizeReasoningEffort(
            backendState.SelectedReasoningEffort,
            GetSelectedModel(backendState));
        RefreshChatSelectors();
        _ = EnsureSelectedChatBackendReadyAsync();
    }

    private void OnChatReasoningSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var backendState = _chatBackendStates[_chatBackendId.Value];
        var reasoningOptions = BuildChatReasoningOptions(GetSelectedModel(backendState));
        if (reasoningOptions.Count == 0)
        {
            backendState.SelectedReasoningEffort = null;
            RefreshChatSelectors();
            return;
        }

        var clampedIndex = Math.Clamp(newIndex, 0, reasoningOptions.Count - 1);
        backendState.SelectedReasoningEffort = reasoningOptions[clampedIndex].Effort;
        RefreshChatSelectors();
        _ = EnsureSelectedChatBackendReadyAsync();
    }

    private void RefreshChatSelectors()
    {
        var backendOptions = BuildChatBackendOptions();
        var selectedBackendIndex = Math.Clamp(
            backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, _chatBackendId.Value, StringComparison.OrdinalIgnoreCase)),
            0,
            Math.Max(0, backendOptions.Count - 1));
        var selectedBackendState = _chatBackendStates[backendOptions[selectedBackendIndex].BackendId.Value];
        var modelOptions = BuildChatModelOptions(selectedBackendState);
        var selectedModelIndex = Math.Clamp(
            modelOptions.FindIndex(option => string.Equals(option.ModelId, selectedBackendState.SelectedModelId, StringComparison.Ordinal)),
            0,
            Math.Max(0, modelOptions.Count - 1));
        var selectedModel = GetSelectedModel(selectedBackendState);
        var reasoningOptions = BuildChatReasoningOptions(selectedModel);
        var selectedReasoningIndex = Math.Clamp(
            reasoningOptions.FindIndex(option => option.Effort == selectedBackendState.SelectedReasoningEffort),
            0,
            Math.Max(0, reasoningOptions.Count - 1));

        PostToUi(() =>
        {
            if (_chatBackendSelect is null ||
                _chatModelSelect is null ||
                _chatReasoningSelect is null)
            {
                return;
            }

            _chatSelectorsRefreshing = true;
            try
            {
                ReplaceSelectItems(_chatBackendSelect, backendOptions);
                ReplaceSelectItems(_chatModelSelect, modelOptions);
                ReplaceSelectItems(_chatReasoningSelect, reasoningOptions);

                _chatBackendSelectedIndex.Value = selectedBackendIndex;
                _chatModelSelectedIndex.Value = selectedModelIndex;
                _chatReasoningSelectedIndex.Value = selectedReasoningIndex;

                _chatModelSelect.IsEnabled = selectedBackendState.Availability == ChatBackendAvailability.Ready;
                _chatReasoningSelect.IsEnabled = selectedBackendState.Availability == ChatBackendAvailability.Ready;
            }
            finally
            {
                _chatSelectorsRefreshing = false;
            }

            RefreshChatBackendStatusMarkup();
        });
    }

    private void RefreshChatBackendStatusMarkup()
    {
        var markup = BuildChatBackendStatusMarkup(_chatBackendStates.Values, _chatBackendId, _chatBackendsInitializing);
        PostToUi(() =>
        {
            if (_chatBackendStatusMarkup is not null)
            {
                _chatBackendStatusMarkup.Text = markup;
            }
        });
    }

    private static void ReplaceSelectItems<T>(Select<T> select, IReadOnlyList<T> items)
    {
        select.Items.Clear();
        foreach (var item in items)
        {
            select.Items.Add(item);
        }
    }

    private static List<ChatBackendOption> BuildChatBackendOptions()
    {
        return
        [
            new ChatBackendOption(AgentBackendIds.Codex, "Codex"),
            new ChatBackendOption(AgentBackendIds.Copilot, "Copilot"),
        ];
    }

    private static List<ChatModelOption> BuildChatModelOptions(ChatBackendState backendState)
    {
        if (backendState.Models.Count == 0)
        {
            return [new ChatModelOption(null, "(default)")];
        }

        return backendState.Models
            .Select(model => new ChatModelOption(model.Id, model.DisplayName ?? model.Id))
            .ToList();
    }

    internal static List<ChatReasoningOption> BuildChatReasoningOptions(AgentModelInfo? model)
    {
        var options = new List<ChatReasoningOption>
        {
            new(null, "Default"),
        };

        var efforts = model?.SupportedReasoningEfforts is { Count: > 0 } supported
            ? supported
            : Enum.GetValues<AgentReasoningEffort>();

        foreach (var effort in efforts.Distinct())
        {
            options.Add(new ChatReasoningOption(effort, SplitPascalCase(effort.ToString())));
        }

        return options;
    }

    internal static AgentBackendId ResolveChatBackendSelection(
        AgentBackendId currentSelection,
        AgentBackendId requestedBackend,
        bool adoptRequestedBackend)
        => adoptRequestedBackend ? requestedBackend : currentSelection;

    internal static string BuildChatBackendStatusMarkup(
        IEnumerable<ChatBackendState> backendStates,
        AgentBackendId selectedBackendId,
        bool isInitializing)
    {
        var items = backendStates
            .OrderBy(static state => state.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(state =>
            {
                var tone = state.Availability switch
                {
                    ChatBackendAvailability.Ready => "success",
                    ChatBackendAvailability.Unsupported or ChatBackendAvailability.Failed => "warning",
                    ChatBackendAvailability.Connecting => "primary",
                    _ => "muted",
                };
                var icon = state.Availability switch
                {
                    ChatBackendAvailability.Ready => $"{NerdFont.MdCheck}",
                    ChatBackendAvailability.Unsupported => $"{NerdFont.CodWarning}",
                    ChatBackendAvailability.Failed => $"{NerdFont.MdClose}",
                    ChatBackendAvailability.Connecting => $"{NerdFont.MdTimerOutline}",
                    _ => $"{NerdFont.MdHelpBox}",
                };
                var selected = string.Equals(state.BackendId.Value, selectedBackendId.Value, StringComparison.OrdinalIgnoreCase)
                    ? "[bold]"
                    : string.Empty;
                var reset = selected.Length > 0 ? "[/]" : string.Empty;
                var status = string.IsNullOrWhiteSpace(state.StatusMessage) ? string.Empty : $" [dim]- {AnsiMarkup.Escape(state.StatusMessage)}[/]";
                return $"{selected}[{tone}]{icon} {AnsiMarkup.Escape(state.DisplayName)}[/]{reset}{status}";
            });

        var prefix = isInitializing
            ? $"[primary]{NerdFont.MdTimerOutline} Detecting backends...[/] "
            : string.Empty;
        return prefix + string.Join("   ", items);
    }

    private static AgentReasoningEffort? NormalizeReasoningEffort(
        AgentReasoningEffort? selectedReasoningEffort,
        AgentModelInfo? model)
    {
        if (selectedReasoningEffort is null)
        {
            return null;
        }

        if (model?.SupportedReasoningEfforts is not { Count: > 0 } supportedReasoningEfforts)
        {
            return selectedReasoningEffort;
        }

        return supportedReasoningEfforts.Contains(selectedReasoningEffort.Value)
            ? selectedReasoningEffort
            : null;
    }

    private static AgentModelInfo? GetSelectedModel(ChatBackendState backendState)
    {
        return string.IsNullOrWhiteSpace(backendState.SelectedModelId)
            ? null
            : backendState.Models.FirstOrDefault(model =>
                string.Equals(model.Id, backendState.SelectedModelId, StringComparison.Ordinal));
    }

    private async Task EnsureSelectedChatBackendReadyAsync()
    {
        var backendState = _chatBackendStates[_chatBackendId.Value];
        if (_chatBackendsInitializing ||
            backendState.Availability != ChatBackendAvailability.Ready)
        {
            return;
        }

        try
        {
            await EnsureChatSessionAsync(_chatBackendId, updateStatus: false).ConfigureAwait(false);
            backendState.StatusMessage = BuildReadyStatusMessage(backendState);
        }
        catch (Exception ex)
        {
            backendState.Availability = ChatBackendAvailability.Failed;
            backendState.StatusMessage = BuildFailedBackendMessage(backendState, ex.Message);
            SetStatus(backendState.StatusMessage);
        }

        RefreshChatBackendStatusMarkup();
    }

    private static string BuildReadyStatusMessage(ChatBackendState backendState)
    {
        var selectedModel = GetSelectedModel(backendState);
        if (selectedModel is not null)
        {
            return $"Connected · {selectedModel.DisplayName ?? selectedModel.Id}";
        }

        return backendState.Models.Count switch
        {
            0 => "Connected.",
            1 => $"Connected · {backendState.Models[0].DisplayName ?? backendState.Models[0].Id}",
            _ => $"Connected · {backendState.Models.Count} models",
        };
    }

    private static string BuildUnsupportedBackendMessage(ChatBackendState backendState, string message)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? "CLI not found." : message.Trim();
        return $"{backendState.DisplayName} is unavailable: {trimmed}";
    }

    private static string BuildFailedBackendMessage(ChatBackendState backendState, string message)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? "Failed to initialize backend." : message.Trim();
        return $"{backendState.DisplayName} failed: {trimmed}";
    }

    private Visual BuildWorkspacesView()
    {
        _workspacesOutput = new TextBlock { Wrap = true, Text = "Press Refresh to load workspaces." };
        _workspaceKeyInput = new TextBox { Text = _activeWorkspaceKey ?? string.Empty };

        return new VStack(
            [
                new TextBlock("Workspaces"),
                new HStack(
                    [
                        new Button(new TextBlock("Refresh")).Click(() => _ = RefreshWorkspacesAsync()),
                        new TextBlock("Workspace key:"),
                        _workspaceKeyInput,
                        new Button(new TextBlock("Set Active")).Click(SetActiveWorkspaceFromInput),
                        new Button(new TextBlock("Resolve Scope")).Click(() => _ = ResolveActiveScopeAsync()),
                    ])
                    {
                        Spacing = 2,
                    },
                _workspacesOutput,
            ])
        {
            Spacing = 1,
        };
    }

    private Visual BuildTasksView()
    {
        _tasksOutput = new TextBlock { Wrap = true, Text = "Press Refresh to list tasks." };
        _taskTitleInput = new TextBox { Text = string.Empty };

        return new VStack(
            [
                new TextBlock("Tasks"),
                new HStack(
                    [
                        new Button(new TextBlock("Refresh")).Click(() => _ = RefreshTasksAsync()),
                        new TextBlock("New task title:"),
                        _taskTitleInput,
                        new Button(new TextBlock("Create")).Click(() => _ = CreateTaskFromInputAsync()),
                    ])
                    {
                        Spacing = 2,
                    },
                _tasksOutput,
            ])
        {
            Spacing = 1,
        };
    }

    private Visual BuildSearchView()
    {
        _searchOutput = new TextBlock { Wrap = true, Text = "Enter a query and press Run." };
        _searchQueryInput = new TextBox { Text = string.Empty };

        return new VStack(
            [
                new TextBlock("Search"),
                new HStack(
                    [
                        new TextBlock("Query:"),
                        _searchQueryInput,
                        new Button(new TextBlock("Run")).Click(() => _ = RunSearchFromInputAsync()),
                    ])
                    {
                        Spacing = 2,
                    },
                _searchOutput,
            ])
        {
            Spacing = 1,
        };
    }

    private Visual BuildJobsView()
    {
        _jobsOutput = new TextBlock { Wrap = true, Text = "Press Refresh to read background job status." };
        return new VStack(
            [
                new TextBlock("Jobs"),
                new HStack(
                    [
                        new Button(new TextBlock("Refresh")).Click(() => RefreshJobs()),
                        new Button(new TextBlock("Process Index Queue")).Click(() => _ = ProcessIndexQueueAsync()),
                    ])
                    {
                        Spacing = 2,
                    },
                _jobsOutput,
            ])
        {
            Spacing = 1,
        };
    }

    private Visual BuildMcpView()
    {
        _mcpOutput = new TextBlock { Wrap = true, Text = "Press Health Check to list MCP tools." };
        return new VStack(
            [
                new TextBlock("MCP"),
                new HStack(
                    [
                        new Button(new TextBlock("Health Check")).Click(() => _ = McpHealthCheckAsync()),
                    ])
                    {
                        Spacing = 2,
                    },
                _mcpOutput,
            ])
        {
            Spacing = 1,
        };
    }

    private async Task RefreshWorkspacesAsync()
    {
        SetStatus("Loading workspaces...");
        try
        {
            var workspaces = await _workspaceCatalog.LoadAsync().ConfigureAwait(false);
            var text = workspaces.Count == 0
                ? "No workspaces were discovered."
                : string.Join(
                    "\n",
                    workspaces.Select(x => $"- {x.Key} ({x.DisplayName}) projects={x.Projects.Count}"));

            PostToUi(() =>
            {
                if (_workspacesOutput is not null)
                {
                    _workspacesOutput.Text = text;
                }
            });
            SetStatus("Workspaces loaded.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load workspaces: {ex.Message}");
        }
    }

    private void SetActiveWorkspaceFromInput()
    {
        var key = _workspaceKeyInput?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            SetStatus("Workspace key is required.");
            return;
        }

        _activeWorkspaceKey = key;
        _activeProjectKey = null;
        SetStatus($"Active workspace set: {key}");
        PostToUi(() => _header!.Text = BuildHeaderText());
    }

    private async Task ResolveActiveScopeAsync()
    {
        var key = _activeWorkspaceKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            SetStatus("Set an active workspace key first.");
            return;
        }

        SetStatus("Resolving scope...");
        try
        {
            var resolutions = await _workspaceResolver.ResolveAsync(
                ScopeSelector.Workspace(key),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            var text = resolutions.Count == 0
                ? "No matching workspace scope resolutions."
                : string.Join(
                    "\n",
                    resolutions.SelectMany(x =>
                        new[]
                        {
                            $"Workspace: {x.Workspace.Key} ({x.Workspace.DisplayName})",
                        }.Concat(
                            x.Projects.Select(p => $"  - {p.Project.Key} => {p.CheckoutPath}"))));

            PostToUi(() =>
            {
                if (_workspacesOutput is not null)
                {
                    _workspacesOutput.Text = text;
                }
            });

            SetStatus("Scope resolved.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to resolve scope: {ex.Message}");
        }
    }

    private async Task RefreshTasksAsync()
    {
        SetStatus("Loading tasks...");
        try
        {
            var tasks = await _taskRepository.ListAsync(limit: 20).ConfigureAwait(false);
            var text = tasks.Count == 0
                ? "No tasks."
                : string.Join("\n", tasks.Select(x => $"- {x.TaskId} [{x.Status}] {x.Title}"));

            PostToUi(() =>
            {
                if (_tasksOutput is not null)
                {
                    _tasksOutput.Text = text;
                }
            });

            SetStatus("Tasks loaded.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load tasks: {ex.Message}");
        }
    }

    private async Task CreateTaskFromInputAsync()
    {
        var title = _taskTitleInput?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            SetStatus("Task title is required.");
            return;
        }

        SetStatus("Creating task...");
        try
        {
            await _taskRepository.CreateAsync(new CreateTaskRequest { Title = title }).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (_taskTitleInput is not null)
                {
                    _taskTitleInput.Text = string.Empty;
                }
            });
            await RefreshTasksAsync().ConfigureAwait(false);
            SetStatus("Task created.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create task: {ex.Message}");
        }
    }

    private async Task RunSearchFromInputAsync()
    {
        var queryText = _searchQueryInput?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(queryText))
        {
            SetStatus("Search text is required.");
            return;
        }

        SetStatus("Running search...");
        try
        {
            var results = await _searchService.QueryHybridAsync(
                new CodeAlta.Search.SearchQuery
                {
                    Text = queryText,
                    Limit = 10,
                    PrefilterLimit = 50,
                }).ConfigureAwait(false);

            var text = results.Count == 0
                ? "No results."
                : string.Join(
                    "\n",
                    results.Select(x => $"- {x.Title ?? x.SourceId} ({x.LinkUri})"));

            PostToUi(() =>
            {
                if (_searchOutput is not null)
                {
                    _searchOutput.Text = text;
                }
            });

            SetStatus("Search complete.");
        }
        catch (Exception ex)
        {
            SetStatus($"Search failed: {ex.Message}");
        }
    }

    private void RefreshJobs()
    {
        var status = _indexer.Status;
        var text =
            $"Index queue depth: {status.QueueDepth}\n" +
            $"Last completed: {(status.LastCompletedAt is null ? "(never)" : status.LastCompletedAt.Value.ToString("O"))}";

        PostToUi(() =>
        {
            if (_jobsOutput is not null)
            {
                _jobsOutput.Text = text;
            }
        });

        SetStatus("Job status updated.");
    }

    private async Task ProcessIndexQueueAsync()
    {
        SetStatus("Processing index queue...");
        try
        {
            await _indexer.ProcessNextAsync().ConfigureAwait(false);
            RefreshJobs();
            SetStatus("Index queue processed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Index processing failed: {ex.Message}");
        }
    }

    private async Task McpHealthCheckAsync()
    {
        SetStatus("Checking MCP tools...");
        try
        {
            await using var connection = await InProcessMcpConnection.CreateAsync(_mcpFactory).ConfigureAwait(false);
            var tools = await connection.Client.ListToolsAsync().ConfigureAwait(false);
            var text = $"MCP tools: {tools.Count}\n" + string.Join("\n", tools.Take(30).Select(x => $"- {x.Name}"));

            PostToUi(() =>
            {
                if (_mcpOutput is not null)
                {
                    _mcpOutput.Text = text;
                }
            });

            SetStatus("MCP ready.");
        }
        catch (Exception ex)
        {
            SetStatus($"MCP check failed: {ex.Message}");
        }
    }

    private string BuildHeaderText()
    {
        var scope = _activeWorkspaceKey is null
            ? "global"
            : _activeProjectKey is null
                ? $"workspace:{_activeWorkspaceKey}"
                : $"project:{_activeWorkspaceKey}/{_activeProjectKey}";

        return $"CodeAlta | scope={scope} | screen={_screen.ToString().ToLowerInvariant()} | indexQueue={_indexer.Status.QueueDepth}";
    }

    private void ClearChat()
    {
        lock (_chatTimelineLock)
        {
            _chatPendingAssistant = null;
            _chatContentStates.Clear();
            _chatActivityStates.Clear();
            _chatInteractionStates.Clear();
            _chatPlanStates.Clear();
            _chatPermissionRequests.Clear();
            _chatUserInputRequests.Clear();
        }

        PostToUi(() =>
        {
            _chatFlow?.Items.Clear();
            if (_chatInput is not null)
            {
                _chatInput.Text = string.Empty;
            }
        });
    }

    private async Task SendChatMessageAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var flow = _chatFlow;
        if (flow is null)
        {
            return;
        }

        LogChatDebug(
            $"SendChatMessage backend={_chatBackendId.Value} model={_chatBackendStates[_chatBackendId.Value].SelectedModelId ?? "<default>"} reasoning={_chatBackendStates[_chatBackendId.Value].SelectedReasoningEffort?.ToString() ?? "<default>"} autoApprove={_chatAutoApproveEnabled} text={text}");

        var pendingChatMessage = CreatePendingChatMessage(text);
        PostToUi(() =>
        {
            if (_chatInput is not null)
            {
                _chatInput.Text = string.Empty;
            }

            flow.Items.Add(pendingChatMessage.UserItem);
            flow.Items.Add(pendingChatMessage.AssistantItem);
            flow.ScrollToTail();
        });

        lock (_chatTimelineLock)
        {
            _chatPendingAssistant = new PendingAssistantState(
                pendingChatMessage.AssistantItem,
                pendingChatMessage.StreamingMarkdown);
        }

        AgentId agentId;
        try
        {
            agentId = await EnsureChatSessionAsync(_chatBackendId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogChatError($"Failed to ensure chat session for backend={_chatBackendId.Value}", ex);
            RenderRunFailure($"**Failed to start agent session:** {ex.Message}");
            return;
        }

        SetStatus($"Running agent ({_chatBackendId.Value})...", showSpinner: true);
        try
        {
            await _agentHub.RunAsync(
                agentId,
                new AgentSendOptions { Input = AgentInput.Text(text) },
                CancellationToken.None).ConfigureAwait(false);
            LogChatDebug($"Run submitted agentId={agentId.Value} backend={_chatBackendId.Value}");
        }
        catch (Exception ex)
        {
            LogChatError($"Agent run failed agentId={agentId.Value} backend={_chatBackendId.Value}", ex);
            RenderRunFailure($"**Agent run failed:** {ex.Message}");
            SetStatus($"Agent run failed: {ex.Message}");
        }
    }

    private static DocumentFlowItem CreateUserChatItem(string markdown)
        => CreateChatMarkdownItem(
            markdown,
            ChatTimelineTone.User,
            headerOverride: "User Prompt",
            maxCodeBlockHeight: 10).Item;

    private static DocumentFlowItem CreateAssistantChatItem(string markdown)
        => CreateChatMarkdownItem(markdown, ChatTimelineTone.Assistant).Item;

    private static DocumentFlowItem CreateAssistantStreamingChatItem(out MarkdownControl markdownControl)
    {
        var (item, control) = CreateChatMarkdownItem(string.Empty, ChatTimelineTone.Assistant);
        markdownControl = control;
        return item;
    }

    internal static PendingChatMessage CreatePendingChatMessage(string userMarkdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMarkdown);

        var userItem = CreateUserChatItem(userMarkdown);
        var assistantItem = CreateAssistantStreamingChatItem(out var streamingMarkdown);
        return new PendingChatMessage(userItem, assistantItem, streamingMarkdown);
    }

    private async Task AbortChatAsync()
    {
        var connection = GetChatConnection(_chatBackendId);
        var agentId = connection.CurrentAgentId;
        if (agentId is null)
        {
            SetStatus("No active chat agent session.");
            return;
        }

        SetStatus("Aborting agent run...");
        try
        {
            // Abort is best-effort; we don't currently surface cancellation at the hub level beyond the session abort.
            await connection.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            SetStatus("Abort requested.");
        }
        catch (Exception ex)
        {
            SetStatus($"Abort failed: {ex.Message}");
        }
    }

    private async Task<AgentId> EnsureChatSessionAsync(AgentBackendId backendId, bool updateStatus = true)
    {
        var backendState = _chatBackendStates[backendId.Value];
        if (backendState.Availability is ChatBackendAvailability.Unsupported or ChatBackendAvailability.Failed)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(backendState.StatusMessage)
                    ? $"Backend '{backendState.DisplayName}' is not available."
                    : backendState.StatusMessage);
        }

        var connection = GetChatConnection(backendId);
        var selectedModelId = backendState.SelectedModelId;
        var selectedReasoningEffort = backendState.SelectedReasoningEffort;

        if (connection.IsConnected &&
            connection.CurrentAgentId is { } connectedAgentId &&
            connection.ConnectedBackendId is { } connectedBackendId &&
            string.Equals(connectedBackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(connection.ConnectedModel, selectedModelId, StringComparison.Ordinal) &&
            connection.ConnectedReasoningEffort == selectedReasoningEffort)
        {
            return connectedAgentId;
        }

        if (updateStatus)
        {
            SetStatus($"Starting chat session ({backendId.Value})...", showSpinner: true);
        }

        var tools = backendId == AgentBackendIds.Copilot
            ? await _mcpToolBridge.GetToolsAsync().ConfigureAwait(false)
            : null;
        LogChatDebug(
            $"EnsureChatSession backend={backendId.Value} model={selectedModelId ?? "<default>"} reasoning={selectedReasoningEffort?.ToString() ?? "<default>"} autoApprove={_chatAutoApproveEnabled} tools={FormatToolNames(tools)}");

        var agentId = await connection.EnsureConnectedAsync(
                backendId,
                Environment.CurrentDirectory,
                selectedModelId,
                selectedReasoningEffort,
                tools,
                HandleChatPermissionRequestAsync,
                HandleChatUserInputRequestAsync,
                CancellationToken.None)
            .ConfigureAwait(false);
        backendState.Availability = ChatBackendAvailability.Ready;
        backendState.StatusMessage = BuildReadyStatusMessage(backendState);
        RefreshChatBackendStatusMarkup();
        if (updateStatus)
        {
            SetStatus($"Chat session ready ({backendId.Value}).");
        }
        return agentId;
    }

    private Task<AgentPermissionDecision> HandleChatPermissionRequestAsync(
        AgentPermissionRequest request,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        LogChatDebug($"Permission request received backend={request.BackendId.Value} payload={request.ToJson()}");

        AgentPermissionDecision decision;
        if (_chatAutoApproveEnabled)
        {
            decision = new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce);
        }
        else
        {
            decision = new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);
        }

        TryRenderChatInteraction(
            () =>
            {
                RecordChatPermissionRequest(request);
                UpsertChatInteraction(
                    request.InteractionId,
                    null,
                    FormatChatImmediatePermissionDecisionMarkdown(decision, _chatAutoApproveEnabled),
                    ChatTimelineTone.Interaction);
                SetStatus(
                    _chatAutoApproveEnabled
                        ? $"Auto-approved permission request ({request.Kind})."
                        : "Permission requested. Auto-Approve is off, so CodeAlta denied it because terminal approval UI is not implemented yet.",
                    showSpinner: true);
            },
            "permission request");
        LogChatDebug($"Permission request resolved interactionId={request.InteractionId} decision={decision.ToJson()}");

        return Task.FromResult(decision);
    }

    private Task<AgentUserInputResponse> HandleChatUserInputRequestAsync(
        AgentUserInputRequest request,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        LogChatDebug($"User input request received backend={request.BackendId.Value} payload={request.ToJson()}");

        var response = CreateChatUserInputResponse(request, _chatAutoApproveEnabled);
        var hasMeaningfulAnswer = response.Answers.Values.Any(static value => !string.IsNullOrWhiteSpace(value));
        TryRenderChatInteraction(
            () =>
            {
                RecordChatUserInputRequest(request, _chatAutoApproveEnabled);
                UpsertChatInteraction(
                    request.InteractionId,
                    null,
                    FormatChatImmediateUserInputResponseMarkdown(response, _chatAutoApproveEnabled),
                    ChatTimelineTone.Interaction);
                SetStatus(
                    _chatAutoApproveEnabled
                        ? hasMeaningfulAnswer
                            ? "Interactive question received. Auto-Approve selected a default answer so the run can continue."
                            : "Interactive question received. Auto-Approve could not infer a safe answer, so CodeAlta returned an empty response."
                        : "Interactive question received. Auto-Approve is off, so CodeAlta returned an empty response because terminal question prompts are not implemented yet.",
                    showSpinner: true);
            },
            "user input request");
        LogChatDebug($"User input request resolved interactionId={request.InteractionId} response={response.ToJson()}");
        return Task.FromResult(response);
    }

    private void HandleChatAgentEvent(AgentEvent @event)
    {
        LogChatDebug($"Agent event backend={@event.BackendId.Value} selectedBackend={_chatBackendId.Value} type={@event.GetType().Name} payload={@event.ToJson()}");
        if (!string.Equals(@event.BackendId.Value, _chatBackendId.Value, StringComparison.OrdinalIgnoreCase))
        {
            LogChatDebug($"Ignoring event for non-selected backend eventBackend={@event.BackendId.Value} selectedBackend={_chatBackendId.Value}");
            return;
        }

        switch (@event)
        {
            case AgentRawEvent raw:
                RenderChatRawEvent(raw);
                break;
            case AgentContentDeltaEvent delta:
                AppendChatContent(delta);
                break;
            case AgentContentCompletedEvent message:
                FinalizeChatContent(message);
                break;
            case AgentPlanSnapshotEvent plan:
                UpsertChatPlan(plan);
                break;
            case AgentActivityEvent activity:
                UpsertChatActivity(activity);
                break;
            case AgentPermissionRequest permissionRequest:
                RecordChatPermissionRequest(permissionRequest);
                SetStatus($"Permission requested ({permissionRequest.Kind}).", showSpinner: true);
                break;
            case AgentUserInputRequest userInputRequest:
                RecordChatUserInputRequest(userInputRequest, _chatAutoApproveEnabled);
                SetStatus(
                    _chatAutoApproveEnabled
                        ? "Interactive question received. Auto-Approve is selecting a default answer."
                        : "Interactive question received. Terminal question prompts are not implemented yet.",
                    showSpinner: true);
                break;
            case AgentInteractionEvent interaction:
                HandleChatInteraction(interaction);
                break;
            case AgentSessionUpdateEvent update:
                HandleChatSessionUpdate(update);
                break;
            case AgentErrorEvent error:
                RenderChatError(error);
                break;
        }
    }

    private void RenderChatRawEvent(AgentRawEvent raw)
    {
        AppendChatTimelineItem(
            CreateChatMarkdownItem(
                FormatChatRawEventMarkdown(raw),
                ChatTimelineTone.Notice,
                headerOverride: "Raw Event",
                headerSecondary: raw.BackendEventType).Item);
    }

    private void AppendChatContent(AgentContentDeltaEvent delta)
    {
        if (string.IsNullOrEmpty(delta.Delta))
        {
            return;
        }

        var state = GetOrCreateChatContentState(delta.Kind, delta.ContentId);
        string markdownText;
        lock (_chatTimelineLock)
        {
            state.Buffer.Append(delta.Delta);
            markdownText = FormatChatContentMarkdown(delta.Kind, state.Buffer.ToString());
        }

        PostToUi(() =>
        {
            state.Markdown.Markdown = markdownText;
            _chatFlow?.ScrollToTail();
        });
    }

    private void FinalizeChatContent(AgentContentCompletedEvent content)
    {
        var state = GetOrCreateChatContentState(content.Kind, content.ContentId);
        lock (_chatTimelineLock)
        {
            state.Buffer.Clear();
            state.Buffer.Append(content.Content);
        }

        var markdownText = FormatChatContentMarkdown(content.Kind, content.Content);

        PostToUi(() =>
        {
            state.Markdown.Markdown = markdownText;
            _chatFlow?.ScrollToTail();
        });
    }

    private void UpsertChatPlan(AgentPlanSnapshotEvent plan)
    {
        var key = $"plan:{plan.RunId?.Value ?? "session"}";
        UpsertChatStatus(
            _chatPlanStates,
            key,
            FormatChatPlanMarkdown(plan.Snapshot),
            ChatTimelineTone.Notice);
    }

    private void UpsertChatActivity(AgentActivityEvent activity)
    {
        UpsertChatStatus(
            _chatActivityStates,
            $"activity:{activity.ActivityId}",
            FormatChatActivityMarkdown(activity),
            ChatTimelineTone.Activity);

        if (activity.Kind == AgentActivityKind.Turn &&
            activity.Phase == AgentActivityPhase.Started)
        {
            SetStatus($"Running agent ({_chatBackendId.Value})...", showSpinner: true);
        }
    }

    private void HandleChatInteraction(AgentInteractionEvent interaction)
    {
        UpsertChatInteraction(
            interaction.InteractionId,
            null,
            FormatChatInteractionResolutionMarkdown(interaction, includeHeading: false),
            ChatTimelineTone.Interaction);

        switch (interaction.Kind)
        {
            case AgentInteractionKind.PermissionResolved:
                lock (_chatTimelineLock)
                {
                    _chatPermissionRequests.Remove(interaction.InteractionId);
                }

                SetStatus(interaction.Message ?? "Permission resolved.");
                break;

            case AgentInteractionKind.UserInputResolved:
                lock (_chatTimelineLock)
                {
                    _chatUserInputRequests.Remove(interaction.InteractionId);
                }

                SetStatus(interaction.Message ?? "User input resolved.");
                break;
        }
    }

    private void HandleChatSessionUpdate(AgentSessionUpdateEvent update)
    {
        if (update.Kind == AgentSessionUpdateKind.Idle)
        {
            ReplaceEmptyPendingAssistantPlaceholder();
            SetStatus($"Agent idle ({_chatBackendId.Value}).");
            return;
        }

        if (update.Kind == AgentSessionUpdateKind.Started ||
            update.Kind == AgentSessionUpdateKind.Resumed)
        {
            SetStatus(update.Message ?? $"Chat session ready ({_chatBackendId.Value}).");
        }

        UpsertChatStatus(
            dictionary: null,
            key: null,
            markdown: FormatChatSessionUpdateMarkdown(update),
            tone: update.Kind == AgentSessionUpdateKind.Warning ? ChatTimelineTone.Interaction : ChatTimelineTone.Notice,
            headerOverride: "Notice",
            headerSecondary: GetSessionUpdateHeader(update.Kind));
    }

    private void RenderChatError(AgentErrorEvent error)
    {
        SetStatus($"Agent error: {error.Message}");

        PendingAssistantState? pendingAssistant;
        lock (_chatTimelineLock)
        {
            pendingAssistant = _chatPendingAssistant;
            if (pendingAssistant is not null && pendingAssistant.Buffer.Length == 0)
            {
                _chatPendingAssistant = null;
            }
            else
            {
                pendingAssistant = null;
            }
        }

        if (pendingAssistant is not null)
        {
            PostToUi(() =>
            {
                pendingAssistant.Buffer.Append(error.Message);
                pendingAssistant.Markdown.Markdown = error.Message;
                _chatFlow?.ScrollToTail();
            });
            return;
        }

        AppendChatTimelineItem(
            CreateChatMarkdownItem(error.Message, ChatTimelineTone.Interaction, headerOverride: "Error").Item);
    }

    private void RenderRunFailure(string markdown)
    {
        PendingAssistantState? pendingAssistant;
        lock (_chatTimelineLock)
        {
            pendingAssistant = _chatPendingAssistant;
            _chatPendingAssistant = null;
        }

        if (pendingAssistant is not null)
        {
            PostToUi(() =>
            {
                pendingAssistant.Buffer.Append(markdown);
                pendingAssistant.Markdown.Markdown = markdown;
                _chatFlow?.ScrollToTail();
            });
            return;
        }

        AppendChatTimelineItem(CreateChatMarkdownItem(markdown, ChatTimelineTone.Interaction, headerOverride: "Error").Item);
    }

    private void TryRenderChatInteraction(Action action, string context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogChatError($"Failed to render chat {context}", ex);
            SetStatus($"Failed to render chat {context}: {ex.Message}", showSpinner: true);
        }
    }

    private void RecordChatPermissionRequest(AgentPermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_chatTimelineLock)
        {
            _chatPermissionRequests[request.InteractionId] = request;
        }

        UpsertChatInteraction(
            request.InteractionId,
            FormatChatPermissionRequestMarkdown(request),
            null,
            ChatTimelineTone.Interaction,
            headerOverride: "Action Required",
            headerSecondary: "Permission Request");
    }

    private void RecordChatUserInputRequest(AgentUserInputRequest request, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_chatTimelineLock)
        {
            _chatUserInputRequests[request.InteractionId] = request;
        }

        UpsertChatInteraction(
            request.InteractionId,
            FormatChatUserInputRequestMarkdown(request, autoApprove),
            null,
            ChatTimelineTone.Interaction,
            headerOverride: "Action Required",
            headerSecondary: "User Input Request");
    }

    private ChatContentState GetOrCreateChatContentState(AgentContentKind kind, string contentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);

        lock (_chatTimelineLock)
        {
            var key = CreateChatContentKey(kind, contentId);
            if (_chatContentStates.TryGetValue(key, out var state))
            {
                return state;
            }

            if (kind == AgentContentKind.Assistant &&
                _chatPendingAssistant is { ContentId: null } pendingAssistant)
            {
                pendingAssistant.ContentId = contentId;
                _chatPendingAssistant = null;
                state = new ChatContentState(
                    pendingAssistant.Item,
                    pendingAssistant.Markdown,
                    pendingAssistant.Buffer,
                    kind);
                _chatContentStates[key] = state;
                return state;
            }

            state = CreateChatContentState(kind);
            _chatContentStates[key] = state;
            AppendChatTimelineItem(state.Item);
            return state;
        }
    }

    private ChatContentState CreateChatContentState(AgentContentKind kind)
    {
        var (item, markdown) = CreateChatMarkdownItem(
            FormatChatContentMarkdown(kind, string.Empty),
            GetContentTone(kind),
            headerOverride: GetContentHeader(kind));
        return new ChatContentState(item, markdown, new StringBuilder(), kind);
    }

    private void UpsertChatStatus(
        Dictionary<string, ChatStatusState>? dictionary,
        string? key,
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        ChatStatusState state;
        lock (_chatTimelineLock)
        {
            if (dictionary is null || key is null)
            {
                state = CreateChatStatusState(markdown, tone, headerOverride, headerSecondary);
                AppendChatTimelineItem(state.Item);
                return;
            }

            if (!dictionary.TryGetValue(key, out state!))
            {
                state = CreateChatStatusState(markdown, tone, headerOverride, headerSecondary);
                dictionary[key] = state;
                AppendChatTimelineItem(state.Item);
            }

            state.BaseMarkdown = markdown;
        }

        PostToUi(() =>
        {
            state.Markdown.Markdown = state.MarkdownValue;
            _chatFlow?.ScrollToTail();
        });
    }

    private void UpsertChatInteraction(
        string interactionId,
        string? baseMarkdown,
        string? statusMarkdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);

        ChatStatusState state;
        lock (_chatTimelineLock)
        {
            if (!_chatInteractionStates.TryGetValue(interactionId, out state!))
            {
                state = CreateChatStatusState(baseMarkdown ?? statusMarkdown ?? string.Empty, tone, headerOverride, headerSecondary);
                _chatInteractionStates[interactionId] = state;
                AppendChatTimelineItem(state.Item);
            }

            if (!string.IsNullOrWhiteSpace(baseMarkdown))
            {
                state.BaseMarkdown = baseMarkdown;
            }

            if (!string.IsNullOrWhiteSpace(statusMarkdown))
            {
                state.StatusMarkdown = statusMarkdown;
            }
        }

        PostToUi(() =>
        {
            state.Markdown.Markdown = state.MarkdownValue;
            _chatFlow?.ScrollToTail();
        });
    }

    private ChatStatusState CreateChatStatusState(string markdown, ChatTimelineTone tone, string? headerOverride = null, string? headerSecondary = null)
    {
        var (item, control) = CreateChatMarkdownItem(markdown, tone, headerOverride: headerOverride, headerSecondary: headerSecondary);
        return new ChatStatusState(item, control)
        {
            BaseMarkdown = markdown,
        };
    }

    private void AppendChatTimelineItem(DocumentFlowItem item)
    {
        PostToUi(() =>
        {
            _chatFlow?.Items.Add(item);
            _chatFlow?.ScrollToTail();
        });
    }

    private void ReplaceEmptyPendingAssistantPlaceholder()
    {
        PendingAssistantState? pendingAssistant;
        lock (_chatTimelineLock)
        {
            pendingAssistant = _chatPendingAssistant;
            _chatPendingAssistant = null;
        }

        if (pendingAssistant is null || pendingAssistant.Buffer.Length > 0)
        {
            return;
        }

        PostToUi(() =>
        {
            pendingAssistant.Markdown.Markdown = "_No assistant content was returned._";
            _chatFlow?.ScrollToTail();
        });
    }

    private static Dictionary<string, ChatBackendState> CreateChatBackendStates()
    {
        return new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
        {
            [AgentBackendIds.Codex.Value] = new(AgentBackendIds.Codex, "Codex"),
            [AgentBackendIds.Copilot.Value] = new(AgentBackendIds.Copilot, "Copilot"),
        };
    }

    private static Visual CreateChatCardHeader(ChatTimelineTone tone, string? headerOverride, string? headerSecondary)
    {
        var (icon, title, toneName) = GetChatCardHeaderParts(tone, headerOverride);
        if (string.IsNullOrWhiteSpace(headerSecondary))
        {
            return new Markup($"[{toneName}]{icon}[/] [bold]{AnsiMarkup.Escape(title)}[/]");
        }

        return new HStack(
            [
                new Markup($"[{toneName}]{icon}[/] [bold]{AnsiMarkup.Escape(title)}[/]"),
                new Markup($"[dim]- {AnsiMarkup.Escape(headerSecondary)}[/]"),
            ])
        {
            Spacing = 1,
        };
    }

    private static GroupStyle CreateChatGroupStyle(ChatTimelineTone tone)
    {
        var (border, background) = tone switch
        {
            ChatTimelineTone.User => (Color.Rgb(0xB2, 0x8D, 0xFF), Color.RgbA(0xB2, 0x8D, 0xFF, 0x08)),
            ChatTimelineTone.Assistant => (Color.Rgb(0x7D, 0xD3, 0xFC), Color.RgbA(0x7D, 0xD3, 0xFC, 0x06)),
            ChatTimelineTone.Reasoning => (Color.Rgb(0x6B, 0xB8, 0xFF), Color.RgbA(0x6B, 0xB8, 0xFF, 0x10)),
            ChatTimelineTone.Activity => (Color.Rgb(0xA0, 0xA0, 0xA0), Color.RgbA(0xC0, 0xC0, 0xC0, 0x08)),
            ChatTimelineTone.Notice => (Color.Rgb(0x8F, 0xD7, 0xB2), Color.RgbA(0x8F, 0xD7, 0xB2, 0x0A)),
            ChatTimelineTone.Interaction => (Color.Rgb(0xFF, 0xC8, 0x66), Color.RgbA(0xFF, 0xC8, 0x66, 0x0E)),
            _ => (Color.Rgb(0x7D, 0xD3, 0xFC), Color.RgbA(0x7D, 0xD3, 0xFC, 0x06)),
        };

        return GroupStyle.Rounded with
        {
            BorderCellStyle = Style.None.WithForeground(border),
            FocusedBorderCellStyle = Style.None.WithForeground(border) | TextStyle.Bold,
            BackgroundStyle = Style.None.WithBackground(background),
        };
    }

    private static (string Icon, string Title, string ToneName) GetChatCardHeaderParts(ChatTimelineTone tone, string? headerOverride)
    {
        var (icon, defaultTitle, toneName) = GetChatCardDefaults(tone);
        if (!string.IsNullOrWhiteSpace(headerOverride))
        {
            return (icon, headerOverride, toneName);
        }

        return (icon, defaultTitle, toneName);
    }

    private static (string Icon, string Title, string ToneName) GetChatCardDefaults(ChatTimelineTone tone)
        => tone switch
        {
            ChatTimelineTone.User => ($"{NerdFont.MdAccount}", "User Prompt", "accent"),
            ChatTimelineTone.Assistant => ($"{NerdFont.MdRobot}", "Assistant", "success"),
            ChatTimelineTone.Reasoning => ($"{NerdFont.CodLightbulb}", "Reasoning", "primary"),
            ChatTimelineTone.Activity => ($"{NerdFont.CodTools}", "Activity", "muted"),
            ChatTimelineTone.Notice => ($"{NerdFont.CodInfo}", "Notice", "success"),
            ChatTimelineTone.Interaction => ($"{NerdFont.CodLock}", "Action Required", "warning"),
            _ => ($"{NerdFont.MdMessageText}", "Message", "primary"),
        };

    private static ChatMarkdownEntry CreateChatMarkdownItem(
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null,
        int maxCodeBlockHeight = 14)
        => RunOnUiThread(
            static state => CreateChatMarkdownItemCore(state.markdown, state.tone, state.headerOverride, state.headerSecondary, state.maxCodeBlockHeight),
            (markdown, tone, headerOverride, headerSecondary, maxCodeBlockHeight));

    private static ChatMarkdownEntry CreateChatMarkdownItemCore(
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride,
        string? headerSecondary,
        int maxCodeBlockHeight)
    {
        var markdownControl = new MarkdownControl(markdown)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Options = MarkdownRenderOptions.Default with
            {
                WrapCodeBlocks = true,
                MaxCodeBlockHeight = maxCodeBlockHeight,
            },
        };

        var copyButton = new Button(new TextBlock($"{NerdFont.MdContentCopy} Copy"))
            .Click(() =>
            {
                markdownControl.App?.Terminal.Clipboard.TrySetText(markdownControl.Markdown);
            });

        var group = new Group(CreateChatCardHeader(tone, headerOverride, headerSecondary), markdownControl)
            .TopRightText(copyButton)
            .Padding(1)
            .Style(CreateChatGroupStyle(tone))
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        return new ChatMarkdownEntry(
            new DocumentFlowItem
            {
                Content = new FlowDocument().Add(group),
                Alignment = DocumentFlowAlignment.Stretch,
            },
            markdownControl);
    }

    internal static string FormatChatContentMarkdown(AgentContentKind kind, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return kind switch
        {
            AgentContentKind.Assistant => content,
            AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput => FormatChatOutputMarkdown(content),
            _ => content,
        };
    }

    internal static string FormatChatPlanMarkdown(AgentPlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var builder = new StringBuilder();
        if (snapshot.ChangeKind is { } changeKind)
        {
            builder.Append("_").Append(SplitPascalCase(changeKind.ToString())).Append("._");
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Explanation))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append(snapshot.Explanation);
        }

        if (snapshot.Steps is { Count: > 0 } steps)
        {
            foreach (var step in steps)
            {
                builder.AppendLine()
                    .Append("- ")
                    .Append(FormatPlanStepStatus(step.Status))
                    .Append(step.Text);
            }
        }

        return builder.ToString();
    }

    internal static string FormatChatActivityMarkdown(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(activity.Name))
        {
            builder.AppendLine()
                .Append("- Name: `")
                .Append(activity.Name)
                .Append('`');
        }

        if (!string.IsNullOrWhiteSpace(activity.Message))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder
                .Append("- Detail: ")
                .Append(activity.Message);
        }

        return builder.ToString();
    }

    internal static string FormatChatSessionUpdateMarkdown(AgentSessionUpdateEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return update.Message ?? string.Empty;
    }

    internal static string GetSessionUpdateHeader(AgentSessionUpdateKind kind)
        => kind switch
        {
            AgentSessionUpdateKind.Info => $"{NerdFont.CodInfo} Info",
            AgentSessionUpdateKind.Warning => $"{NerdFont.CodWarning} Warning",
            AgentSessionUpdateKind.ModelChanged => $"{NerdFont.MdChat} Model Changed",
            AgentSessionUpdateKind.ModeChanged => $"{NerdFont.MdCubeOutline} Mode Changed",
            AgentSessionUpdateKind.TitleChanged => $"{NerdFont.MdRenameBox} Title Changed",
            AgentSessionUpdateKind.ContextChanged => $"{NerdFont.MdFolder} Context Changed",
            AgentSessionUpdateKind.PlanUpdated => $"{NerdFont.MdProgressWrench} Plan Updated",
            AgentSessionUpdateKind.UsageUpdated => $"{NerdFont.MdPacMan} Usage Updated",
            AgentSessionUpdateKind.CompactionStarted => $"{NerdFont.MdSelectCompare} Compaction Started",
            AgentSessionUpdateKind.CompactionCompleted => $"{NerdFont.MdShieldPlusOutline} Compaction Completed",
            AgentSessionUpdateKind.Handoff => $"{NerdFont.MdServerNetwork} Handoff",
            AgentSessionUpdateKind.Truncated => $"{NerdFont.MdDelete} Session Truncated",
            AgentSessionUpdateKind.Shutdown => $"{NerdFont.MdClose} Session Shutdown",
            AgentSessionUpdateKind.TaskCompleted => $"{NerdFont.MdCheck} Task Completed",
            AgentSessionUpdateKind.DiffUpdated => $"{NerdFont.CodEdit} Diff Updated",
            AgentSessionUpdateKind.Started => $"{NerdFont.MdTimerOutline} Session Started",
            AgentSessionUpdateKind.Resumed => $"{NerdFont.MdAccountArrowRight} Session Resumed",
            AgentSessionUpdateKind.Idle => $"{NerdFont.MdCat} Agent Idle",
            _ => SplitPascalCase(kind.ToString()),
        };

    internal static string FormatChatPermissionRequestMarkdown(AgentPermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder("_The agent is blocked until this permission request is resolved._");

        switch (request)
        {
            case AgentCommandPermissionRequest command:
                builder.AppendLine()
                    .AppendLine()
                    .Append("- Kind: command execution");

                if (!string.IsNullOrWhiteSpace(command.Command))
                {
                    builder.AppendLine()
                        .AppendLine()
                        .Append(FormatChatCodeFence(command.Command, "shell"));
                }

                AppendBullet(builder, "Working directory", command.WorkingDirectory, code: true);
                AppendBullet(builder, "Reason", command.Reason);

                if (command.Actions is { Count: > 0 } actions)
                {
                    builder.AppendLine().AppendLine().AppendLine("**Actions**");
                    foreach (var action in actions)
                    {
                        builder.Append("- ")
                            .Append(ToDisplayLabel(action.Kind));

                        if (!string.IsNullOrWhiteSpace(action.Path))
                        {
                            builder.Append(": `").Append(action.Path).Append('`');
                        }
                        else if (!string.IsNullOrWhiteSpace(action.Query))
                        {
                            builder.Append(": `").Append(action.Query).Append('`');
                        }

                        builder.AppendLine();
                    }
                }

                if (command.Network is { } network)
                {
                    AppendBullet(builder, "Network", $"{network.Protocol}://{network.Host}");
                }

                break;

            case AgentFileChangePermissionRequest fileChange:
                builder.AppendLine()
                    .AppendLine()
                    .Append("- Kind: file change");
                AppendBullet(builder, "Grant root", fileChange.GrantRoot, code: true);
                AppendBullet(builder, "Reason", fileChange.Reason);
                break;

            case AgentGenericPermissionRequest generic:
                builder.AppendLine().AppendLine().Append("- Kind: ").Append(generic.Kind);
                if (TryGetStringProperty(generic.Raw, "toolName", out var toolName))
                {
                    builder.AppendLine().Append("- Tool: `").Append(toolName).Append('`');
                }

                builder.AppendLine()
                    .AppendLine()
                    .Append(FormatChatCodeFence(generic.Raw.GetRawText(), "json"));

                break;

            default:
                builder.AppendLine().AppendLine().Append("- Kind: ").Append(request.Kind);
                break;
        }

        return builder.ToString();
    }

    internal static string FormatChatRawEventMarkdown(AgentRawEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var builder = new StringBuilder()
            .AppendLine($"- Event: `{raw.BackendEventType}`");

        var payload = raw.Raw.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : raw.Raw.GetRawText();

        builder
            .AppendLine()
            .AppendLine("```json")
            .AppendLine(payload)
            .Append("```");

        return builder.ToString();
    }

    internal static string FormatChatUserInputRequestMarkdown(AgentUserInputRequest request)
        => FormatChatUserInputRequestMarkdown(request, autoApprove: false);

    internal static string FormatChatUserInputRequestMarkdown(AgentUserInputRequest request, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder(
                autoApprove
                    ? "_The agent asked a question. Auto-Approve will prefer continue/inspect-style choices or use a neutral fallback answer so the run can continue._"
                    : "_The agent asked a question. Terminal question prompts are not implemented yet, so CodeAlta returns empty answers for now._");

        for (var index = 0; index < request.Form.Prompts.Count; index++)
        {
            var prompt = request.Form.Prompts[index];
            builder.AppendLine()
                .AppendLine()
                .Append("**Question ")
                .Append(index + 1)
                .Append("**");

            AppendBullet(builder, "Id", prompt.Id, code: true);
            if (!string.IsNullOrWhiteSpace(prompt.Header))
            {
                builder.AppendLine().Append("- Header: ").Append(prompt.Header);
            }

            builder.AppendLine().Append("- Question: ").Append(prompt.Question);

            if (prompt.Options is { Count: > 0 } options)
            {
                builder.AppendLine().AppendLine().Append("**Choices**");
                foreach (var option in options)
                {
                    builder.AppendLine().Append("- ").Append(option.Label);
                    if (!string.IsNullOrWhiteSpace(option.Description))
                    {
                        builder.Append(": ").Append(option.Description);
                    }
                }
            }

            builder.AppendLine()
                .Append("- Freeform: ")
                .Append(prompt.AllowFreeform ? "allowed" : "disabled");

            if (prompt.IsSecret)
            {
                builder.AppendLine().Append("- Input: secret");
            }
        }

        return builder.ToString();
    }

    internal static string FormatChatInteractionResolutionMarkdown(AgentInteractionEvent interaction, bool includeHeading)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        var label = interaction.Kind switch
        {
            AgentInteractionKind.PermissionResolved => "Permission Resolved",
            AgentInteractionKind.UserInputResolved => "User Input Resolved",
            _ => interaction.Kind.ToString(),
        };
        var detailsMarkdown = BuildChatInteractionResolutionDetailsMarkdown(interaction);

        if (!includeHeading)
        {
            if (string.IsNullOrWhiteSpace(detailsMarkdown))
            {
                return string.IsNullOrWhiteSpace(interaction.Message)
                    ? "_Status:_ resolved"
                    : $"_Status:_ {interaction.Message}";
            }

            return string.IsNullOrWhiteSpace(interaction.Message)
                ? $"_Status:_ resolved\n\n{detailsMarkdown}"
                : $"_Status:_ {interaction.Message}\n\n{detailsMarkdown}";
        }

        if (string.IsNullOrWhiteSpace(interaction.Message))
        {
            return string.IsNullOrWhiteSpace(detailsMarkdown)
                ? $"**{NerdFont.CodArrowRight} {label}**"
                : $"**{NerdFont.CodArrowRight} {label}**\n\n{detailsMarkdown}";
        }

        return string.IsNullOrWhiteSpace(detailsMarkdown)
            ? $"**{NerdFont.CodArrowRight} {label}**\n\n{interaction.Message}"
            : $"**{NerdFont.CodArrowRight} {label}**\n\n{interaction.Message}\n\n{detailsMarkdown}";
    }

    internal static string FormatChatImmediatePermissionDecisionMarkdown(
        AgentPermissionDecision decision,
        bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var reason = autoApprove
            ? "CodeAlta response: auto-approved this request."
            : "CodeAlta response: denied this request because interactive approval UI is not implemented yet.";
        return $"_Status:_ {reason}\n\n- Decision: {SplitPascalCase(decision.Kind.ToString())}";
    }

    internal static string FormatChatImmediateUserInputResponseMarkdown(
        AgentUserInputResponse response,
        bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(response);

        var builder = new StringBuilder();
        if (autoApprove)
        {
            builder.Append("_Status:_ CodeAlta auto-answered the question.");
        }
        else
        {
            builder.Append("_Status:_ CodeAlta returned an empty answer because terminal question prompts are not implemented yet.");
        }

        foreach (var answer in response.Answers)
        {
            builder.AppendLine()
                .AppendLine()
                .Append("- `")
                .Append(answer.Key)
                .Append("`: ");
            if (string.IsNullOrWhiteSpace(answer.Value))
            {
                builder.Append("_empty_");
            }
            else
            {
                builder.Append('`').Append(answer.Value).Append('`');
            }
        }

        return builder.ToString();
    }

    private static string CreateChatContentKey(AgentContentKind kind, string contentId)
        => $"content:{kind}:{contentId}";

    private static ChatTimelineTone GetContentTone(AgentContentKind kind)
    {
        return kind switch
        {
            AgentContentKind.Assistant => ChatTimelineTone.Assistant,
            AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary => ChatTimelineTone.Reasoning,
            AgentContentKind.Plan or AgentContentKind.Notice => ChatTimelineTone.Notice,
            _ => ChatTimelineTone.Activity,
        };
    }

    private static string FormatPlanStepStatus(AgentPlanStepStatus? status)
    {
        return status switch
        {
            AgentPlanStepStatus.Pending => "[ ] ",
            AgentPlanStepStatus.InProgress => "[~] ",
            AgentPlanStepStatus.Completed => "[x] ",
            _ => string.Empty,
        };
    }

    private static string GetActivityPhaseLabel(AgentActivityPhase phase)
    {
        return phase switch
        {
            AgentActivityPhase.Requested => "Requested",
            AgentActivityPhase.Started => "Started",
            AgentActivityPhase.Progressed => "In Progress",
            AgentActivityPhase.Completed => "Completed",
            AgentActivityPhase.Failed => "Failed",
            AgentActivityPhase.Canceled => "Canceled",
            AgentActivityPhase.Selected => "Selected",
            AgentActivityPhase.Deselected => "Deselected",
            _ => phase.ToString(),
        };
    }

    private static string GetActivityKindLabel(AgentActivityKind kind)
    {
        return kind switch
        {
            AgentActivityKind.Turn => "Turn",
            AgentActivityKind.ToolCall => "Tool Call",
            AgentActivityKind.CommandExecution => "Command Execution",
            AgentActivityKind.FileChange => "File Change",
            AgentActivityKind.McpToolCall => "MCP Tool Call",
            AgentActivityKind.DynamicToolCall => "Dynamic Tool Call",
            AgentActivityKind.CollabAgentToolCall => "Collab Agent Tool Call",
            AgentActivityKind.Subagent => "Subagent",
            AgentActivityKind.Hook => "Hook",
            AgentActivityKind.Skill => "Skill",
            AgentActivityKind.Compaction => "Compaction",
            AgentActivityKind.WebSearch => "Web Search",
            AgentActivityKind.ImageGeneration => "Image Generation",
            _ => SplitPascalCase(kind.ToString()),
        };
    }

    private static string GetActivityIcon(AgentActivityKind kind)
    {
        return kind switch
        {
            AgentActivityKind.ToolCall or AgentActivityKind.McpToolCall or AgentActivityKind.DynamicToolCall => $"{NerdFont.CodTools}",
            AgentActivityKind.CommandExecution => $"{NerdFont.CodTerminalPowershell}",
            AgentActivityKind.FileChange => $"{NerdFont.CodEdit}",
            AgentActivityKind.CollabAgentToolCall or AgentActivityKind.Subagent => $"{NerdFont.MdTestTube}",
            AgentActivityKind.Hook => $"{NerdFont.MdHook}",
            AgentActivityKind.Skill => $"{NerdFont.MdLightbulb}",
            AgentActivityKind.WebSearch => $"{NerdFont.MdWeb}",
            AgentActivityKind.ImageGeneration => $"{NerdFont.MdMatrix}",
            AgentActivityKind.Turn => $"{NerdFont.MdConsole}",
            _ => $"{NerdFont.CodInfo}",
        };
    }

    private static string ToDisplayLabel(AgentCommandPreviewKind kind)
        => kind switch
        {
            AgentCommandPreviewKind.ListFiles => "List Files",
            _ => SplitPascalCase(kind.ToString()),
        };

    private static string GetActivityHeadline(AgentActivityKind kind, AgentActivityPhase phase)
    {
        var label = GetActivityKindLabel(kind);
        return phase switch
        {
            AgentActivityPhase.Requested or AgentActivityPhase.Started => $"Calling {label}",
            AgentActivityPhase.Completed => $"{label} Result",
            AgentActivityPhase.Failed => $"{label} Failed",
            AgentActivityPhase.Canceled => $"{label} Canceled",
            AgentActivityPhase.Progressed => $"{label} Update",
            AgentActivityPhase.Selected => $"{label} Selected",
            AgentActivityPhase.Deselected => $"{label} Deselected",
            _ => $"{label} · {GetActivityPhaseLabel(phase)}",
        };
    }

    private static string FormatChatOutputMarkdown(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return FormatChatCodeFence(content, "text");
    }

    private static string? GetContentHeader(AgentContentKind kind)
        => kind switch
        {
            AgentContentKind.Assistant => null,
            AgentContentKind.Reasoning => "Reasoning",
            AgentContentKind.ReasoningSummary => "Reasoning Summary",
            AgentContentKind.Plan => "Plan",
            AgentContentKind.CommandOutput => "Command Output",
            AgentContentKind.FileChangeOutput => "File Change Output",
            AgentContentKind.ToolOutput => "Tool Output",
            AgentContentKind.Notice => "Notice",
            _ => SplitPascalCase(kind.ToString()),
        };

    private static string FormatChatCodeFence(string content, string language)
    {
        var fence = content.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        return $"{fence}{language}\n{content}\n{fence}";
    }

    private static void AppendBullet(StringBuilder builder, string label, string? value, bool code = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine().Append("- ").Append(label).Append(": ");
        if (code)
        {
            builder.Append('`').Append(value).Append('`');
        }
        else
        {
            builder.Append(value);
        }
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (index > 0 && char.IsUpper(ch) && !char.IsWhiteSpace(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    internal static AgentUserInputResponse CreateChatUserInputResponse(AgentUserInputRequest request, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answers = request.Form.Prompts.ToDictionary(
            static x => x.Id,
            prompt => ResolveChatPromptAnswer(prompt, autoApprove),
            StringComparer.Ordinal);

        return new AgentUserInputResponse(answers);
    }

    private static string ResolveChatPromptAnswer(AgentUserInputPrompt prompt, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (!autoApprove)
        {
            return string.Empty;
        }

        if (prompt.Options is { Count: > 0 } options)
        {
            return SelectPreferredPromptOption(options, prompt.Question);
        }

        if (prompt.IsSecret)
        {
            return string.Empty;
        }

        return prompt.AllowFreeform
            ? "No preference. Use your best judgment and continue."
            : string.Empty;
    }

    private static string SelectPreferredPromptOption(
        IReadOnlyList<AgentUserInputOption> options,
        string? question)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Count == 0)
        {
            return string.Empty;
        }

        var bestIndex = 0;
        var bestScore = int.MinValue;
        for (var index = 0; index < options.Count; index++)
        {
            var score = ScorePromptOption(options[index].Label, question);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return options[bestIndex].Label;
    }

    private static int ScorePromptOption(string label, string? question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var normalizedLabel = label.Trim().ToLowerInvariant();
        var normalizedQuestion = question?.Trim().ToLowerInvariant() ?? string.Empty;
        var score = 0;

        score += ScoreOptionKeywords(
            normalizedLabel,
            "yes",
            "allow",
            "approve",
            "continue",
            "proceed",
            "go ahead",
            "run",
            "use",
            "look",
            "inspect",
            "search",
            "list",
            "read",
            "open",
            "explore",
            "summarize");

        score -= ScoreOptionKeywords(
            normalizedLabel,
            "no",
            "deny",
            "reject",
            "cancel",
            "abort",
            "stop",
            "don't",
            "do not",
            "never",
            "skip",
            "later",
            "different path",
            "specify a different path",
            "provide instructions",
            "inspect locally");

        if (normalizedQuestion.Contains("which option", StringComparison.Ordinal) ||
            normalizedQuestion.Contains("how should i proceed", StringComparison.Ordinal) ||
            normalizedQuestion.Contains("do you want me to", StringComparison.Ordinal))
        {
            score += ScoreOptionKeywords(
                normalizedLabel,
                "continue",
                "proceed",
                "look",
                "inspect",
                "search",
                "list",
                "use",
                "run");

            score -= ScoreOptionKeywords(
                normalizedLabel,
                "provide instructions",
                "different path",
                "stop",
                "cancel");
        }

        return score;
    }

    private static int ScoreOptionKeywords(string value, params string[] keywords)
    {
        var score = 0;
        foreach (var keyword in keywords)
        {
            if (value.Contains(keyword, StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        return score;
    }

    private static string BuildChatInteractionResolutionDetailsMarkdown(AgentInteractionEvent interaction)
    {
        if (interaction.Details is not { } details)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        switch (interaction.Kind)
        {
            case AgentInteractionKind.PermissionResolved:
                if (TryGetStringProperty(details, "decisionKind", out var decisionKind))
                {
                    builder.Append("- Decision: ").Append(SplitPascalCase(decisionKind!));
                }

                break;

            case AgentInteractionKind.UserInputResolved:
                if (details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("answers", out var answers) &&
                    answers.ValueKind == JsonValueKind.Object)
                {
                    var answerLines = new List<string>();
                    foreach (var answer in answers.EnumerateObject())
                    {
                        answerLines.Add(
                            string.IsNullOrWhiteSpace(answer.Value.GetString())
                                ? $"- `{answer.Name}`: _empty_"
                                : $"- `{answer.Name}`: `{answer.Value.GetString()}`");
                    }

                    if (answerLines.Count == 0)
                    {
                        builder.Append("- Answers: _empty_");
                    }
                    else
                    {
                        builder.Append(string.Join(Environment.NewLine, answerLines));
                    }

                    if (answerLines.All(static line => line.EndsWith("_empty_", StringComparison.Ordinal)))
                    {
                        if (builder.Length > 0)
                        {
                            builder.AppendLine();
                        }

                        builder.Append("- Note: Terminal question prompts are not implemented yet.");
                    }
                }

                break;
        }

        return builder.ToString();
    }


    private void SetStatus(string message, bool showSpinner = false)
    {
        PostToUi(() =>
        {
            if (_status is not null)
            {
                _status.Text = message;
            }

            if (_statusSpinner is not null)
            {
                _statusSpinner.IsVisible = showSpinner;
                _statusSpinner.IsActive = showSpinner;
            }
        });
    }

    private void SubscribeChatBindingEvents()
    {
        if (_chatBindingEventsSubscribed)
        {
            return;
        }

        BindingManager.Current.ValueChanged += OnBindingValueChanged;
        _chatBindingEventsSubscribed = true;
    }

    private void UnsubscribeChatBindingEvents()
    {
        if (!_chatBindingEventsSubscribed)
        {
            return;
        }

        BindingManager.Current.ValueChanged -= OnBindingValueChanged;
        _chatBindingEventsSubscribed = false;
    }

    private void OnBindingValueChanged(Binding binding)
    {
        if (!IsChatAutoApproveBinding(binding, _chatAutoApproveState))
        {
            return;
        }

        _chatAutoApproveEnabled = _chatAutoApproveState.Value;
    }

    private void PostToUi(Action action)
    {
        (_dispatcher ?? Dispatcher.Current).Post(action);
    }

    private static void LogChatDebug(string message)
    {
        if (LogManager.IsInitialized && ChatLogger.IsEnabled(LogLevel.Debug))
        {
            ChatLogger.Debug(message);
        }
    }

    private static void LogChatError(string message, Exception exception)
    {
        if (LogManager.IsInitialized && ChatLogger.IsEnabled(LogLevel.Error))
        {
            ChatLogger.Error(exception, message);
        }
    }

    private static string FormatToolNames(IReadOnlyList<AgentToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return "<none>";
        }

        return string.Join(", ", tools.Select(static tool => tool.Spec.Name));
    }

    internal static bool IsChatAutoApproveBinding(Binding binding, State<bool> autoApproveState)
    {
        ArgumentNullException.ThrowIfNull(autoApproveState);

        return ReferenceEquals(binding.Owner, autoApproveState) &&
               string.Equals(binding.Accessor.Name, nameof(State<bool>.Value), StringComparison.Ordinal);
    }

    private static T RunOnUiThread<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher.Current;
        return dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
    }

    private static T RunOnUiThread<TState, T>(Func<TState, T> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher.Current;
        return dispatcher.CheckAccess()
            ? action(state)
            : dispatcher.InvokeAsync(() => action(state)).GetAwaiter().GetResult();
    }

    private enum TerminalScreen
    {
        Home,
        Chat,
        Workspaces,
        Tasks,
        Search,
        Jobs,
        Mcp,
    }

    internal sealed record PendingChatMessage(
        DocumentFlowItem UserItem,
        DocumentFlowItem AssistantItem,
        MarkdownControl StreamingMarkdown);

    internal enum ChatBackendAvailability
    {
        Unknown,
        Connecting,
        Ready,
        Unsupported,
        Failed,
    }

    private enum ChatTimelineTone
    {
        User,
        Assistant,
        Reasoning,
        Activity,
        Notice,
        Interaction,
    }

    internal sealed record ChatBackendOption(
        AgentBackendId BackendId,
        string Label)
    {
        public override string ToString() => Label;
    }

    internal sealed record ChatModelOption(
        string? ModelId,
        string Label)
    {
        public override string ToString() => Label;
    }

    internal sealed record ChatReasoningOption(
        AgentReasoningEffort? Effort,
        string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ChatMarkdownEntry(
        DocumentFlowItem Item,
        MarkdownControl Markdown);

    internal sealed class ChatBackendState(AgentBackendId backendId, string displayName)
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName { get; } = displayName;

        public ChatBackendAvailability Availability { get; set; }

        public string StatusMessage { get; set; } = "Not initialized.";

        public List<AgentModelInfo> Models { get; } = [];

        public string? SelectedModelId { get; set; }

        public AgentReasoningEffort? SelectedReasoningEffort { get; set; }
    }

    private sealed class ChatContentState(
        DocumentFlowItem item,
        MarkdownControl markdown,
        StringBuilder buffer,
        AgentContentKind kind)
    {
        public DocumentFlowItem Item { get; } = item;

        public MarkdownControl Markdown { get; } = markdown;

        public StringBuilder Buffer { get; } = buffer;

        public AgentContentKind Kind { get; } = kind;
    }

    private sealed class PendingAssistantState(
        DocumentFlowItem item,
        MarkdownControl markdown)
    {
        public DocumentFlowItem Item { get; } = item;

        public MarkdownControl Markdown { get; } = markdown;

        public StringBuilder Buffer { get; } = new();

        public string? ContentId { get; set; }
    }

    private sealed class ChatStatusState(
        DocumentFlowItem item,
        MarkdownControl markdown)
    {
        public DocumentFlowItem Item { get; } = item;

        public MarkdownControl Markdown { get; } = markdown;

        public string BaseMarkdown { get; set; } = string.Empty;

        public string? StatusMarkdown { get; set; }

        public string MarkdownValue =>
            string.IsNullOrWhiteSpace(StatusMarkdown)
                ? BaseMarkdown
                : $"{BaseMarkdown}\n\n{StatusMarkdown}";
    }

    private sealed class ChatPromptEditor : PromptEditor
    {
        private readonly Action<string> _onAccepted;

        public ChatPromptEditor(Action<string> onAccepted)
        {
            ArgumentNullException.ThrowIfNull(onAccepted);
            _onAccepted = onAccepted;
        }

        protected override void OnAccepted(PromptEditorAcceptedEventArgs e)
        {
            ArgumentNullException.ThrowIfNull(e);
            _onAccepted(e.Text);
            base.OnAccepted(e);
        }
    }
}
