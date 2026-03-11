using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Threading;

internal sealed partial class CodeAltaTerminalUi : IAsyncDisposable
{
    private static readonly Logger UiLogger = LogManager.GetLogger("CodeAlta.UI");

    private readonly ProjectCatalog _projectCatalog;
    private readonly WorkThreadCatalog _threadCatalog;
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly AgentHub _agentHub;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates = CreateChatBackendStates();
    private readonly Dictionary<string, ThreadTabState> _threadTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _runtimeEventsCts = new();
    private readonly State<bool> _chatAutoApproveState = new(true);
    private readonly State<int> _viewRefreshState = new(0);

    private IReadOnlyList<ProjectDescriptor> _projects = [];
    private IReadOnlyList<WorkThreadDescriptor> _threads = [];
    private WorkThreadViewState _viewState = new();
    private Dispatcher? _dispatcher;
    private TextBlock? _header;
    private TextBlock? _status;
    private Spinner? _statusSpinner;
    private DockLayout? _threadPaneLayout;
    private VStack? _threadBottomPanel;
    private ChatPromptEditor? _threadInput;
    private Visual? _threadInputView;
    private TextBox? _newThreadTitleInput;
    private Select<ChatBackendOption>? _chatBackendSelect;
    private Select<ChatModelOption>? _chatModelSelect;
    private Select<ChatReasoningOption>? _chatReasoningSelect;
    private CheckBox? _chatAutoApproveCheckBox;
    private Markup? _chatBackendStatusMarkup;
    private ComputedVisual? _projectsVisual;
    private ComputedVisual? _globalThreadsVisual;
    private ComputedVisual? _projectThreadsVisual;
    private ComputedVisual? _tabStripVisual;
    private ComputedVisual? _threadHeaderVisual;
    private Task? _runtimeEventsTask;
    private bool _chatSelectorsRefreshing;
    private bool _chatBindingEventsSubscribed;
    private volatile bool _chatAutoApproveEnabled = true;
    private string? _selectedProjectId;
    private string? _selectedThreadId;

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
        SubscribeChatBindingEvents();

        await LoadCatalogStateAsync(cancellationToken).ConfigureAwait(false);
        await InitializeChatBackendsAsync(cancellationToken).ConfigureAwait(false);

        _header = new TextBlock
        {
            Wrap = false,
            Text = BuildHeaderText(),
        };

        _status = new TextBlock
        {
            Wrap = true,
            Text = "Select a project or global thread and start working.",
        };

        _statusSpinner = new Spinner();
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

        var root = new DockLayout(
            top: _header,
            content: BuildMainView(),
            bottom: statusBar);

        _runtimeEventsTask = Task.Run(() => PumpRuntimeEventsAsync(_runtimeEventsCts.Token), CancellationToken.None);
        await Terminal.RunAsync(root, () => TerminalLoopResult.Continue, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        UnsubscribeChatBindingEvents();
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

        _runtimeEventsCts.Dispose();
    }

    private async Task LoadCatalogStateAsync(CancellationToken cancellationToken)
    {
        _projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        _threads = await _runtimeService.ListRecoverableThreadsAsync(cancellationToken).ConfigureAwait(false);
        _viewState = await _threadCatalog.LoadViewStateAsync(cancellationToken).ConfigureAwait(false);

        _viewState.OpenThreadIds.RemoveAll(id => _threads.All(thread => !string.Equals(thread.ThreadId, id, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(_viewState.SelectedThreadId) &&
            _viewState.OpenThreadIds.All(id => !string.Equals(id, _viewState.SelectedThreadId, StringComparison.OrdinalIgnoreCase)))
        {
            _viewState.SelectedThreadId = null;
        }

        foreach (var threadId in _viewState.OpenThreadIds.ToArray())
        {
            var thread = FindThread(threadId);
            if (thread is null)
            {
                continue;
            }

            EnsureThreadTab(thread);
        }

        _selectedThreadId = _viewState.SelectedThreadId ?? _viewState.OpenThreadIds.FirstOrDefault();
        var selectedThread = GetSelectedThread();
        _selectedProjectId = selectedThread?.ProjectRef ?? _projects.FirstOrDefault()?.Id;

        if (selectedThread is not null)
        {
            await EnsureThreadHistoryLoadedAsync(selectedThread, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InitializeChatBackendsAsync(CancellationToken cancellationToken)
    {
        foreach (var backendId in new[] { AgentBackendIds.Codex, AgentBackendIds.Copilot })
        {
            var state = _chatBackendStates[backendId.Value];
            state.Availability = ChatBackendAvailability.Connecting;
            state.StatusMessage = "Detecting backend...";

            try
            {
                var models = await _agentHub.ListModelsAsync(backendId, cancellationToken).ConfigureAwait(false);
                state.Models.Clear();
                state.Models.AddRange(models);
                state.SelectedModelId = models.FirstOrDefault()?.Id;
                state.Availability = ChatBackendAvailability.Ready;
                state.StatusMessage = BuildReadyStatusMessage(state);
            }
            catch (Exception ex)
            {
                state.Models.Clear();
                state.SelectedModelId = null;
                state.Availability = ChatBackendAvailability.Failed;
                state.StatusMessage = BuildFailedBackendMessage(state, ex.Message);
            }
        }
    }

    private async Task PumpRuntimeEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var runtimeEvent in _runtimeService.StreamEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                HandleRuntimeEvent(runtimeEvent);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Visual BuildMainView()
    {
        return new HStack(
            [
                BuildSidebar(),
                BuildThreadPane(),
            ])
        {
            Spacing = 2,
        };
    }

    private Visual BuildSidebar()
    {
        _newThreadTitleInput ??= new TextBox { Text = string.Empty };
        _projectsVisual ??= CreateComputedVisual(BuildProjectsContent);
        _globalThreadsVisual ??= CreateComputedVisual(BuildGlobalThreadsContent);
        _projectThreadsVisual ??= CreateComputedVisual(BuildProjectThreadsContent);

        return new VStack(
            [
                CreateSectionGroup(
                    "Global Threads",
                    new VStack(
                        [
                            new Button(new TextBlock("New Global Thread")).Click(() => _ = CreateGlobalThreadAsync()),
                            _globalThreadsVisual,
                        ])
                    {
                        Spacing = 1,
                    }),
                CreateSectionGroup("Projects", _projectsVisual),
                CreateSectionGroup(
                    "Selected Project",
                    new VStack(
                        [
                            new TextBlock("Thread Title (optional)"),
                            _newThreadTitleInput,
                            new Button(new TextBlock("New Project Thread")).Click(() => _ = CreateProjectThreadAsync()),
                            _projectThreadsVisual,
                        ])
                    {
                        Spacing = 1,
                    }),
                CreateSectionGroup(
                    "Catalog",
                    new VStack(
                        [
                            new Button(new TextBlock("Refresh")).Click(() => _ = ReloadCatalogAsync()),
                        ])
                    {
                        Spacing = 1,
                    }),
            ])
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
    }

    private Visual BuildProjectsContent()
    {
        var children = new List<Visual>();
        foreach (var project in _projects.OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var isSelected = string.Equals(project.Id, _selectedProjectId, StringComparison.OrdinalIgnoreCase);
            var label = isSelected ? $"> {project.DisplayName}" : project.DisplayName;
            children.Add(new Button(new TextBlock(label)).Click(() => SelectProject(project.Id)));
        }

        if (children.Count == 0)
        {
            children.Add(new TextBlock("No projects discovered yet."));
        }

        return new VStack([.. children]) { Spacing = 1 };
    }

    private Visual BuildGlobalThreadsContent()
    {
        var globalThreads = _threads
            .Where(static thread => thread.Kind == WorkThreadKind.GlobalThread)
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();

        if (globalThreads.Length == 0)
        {
            return new TextBlock("No global threads yet.");
        }

        return new VStack([.. globalThreads.Select(CreateThreadOpenButton)]) { Spacing = 1 };
    }

    private Visual BuildProjectThreadsContent()
    {
        var project = GetSelectedProject();
        if (project is null)
        {
            return new TextBlock("Select a project to view and continue its threads.");
        }

        var children = new List<Visual>
        {
            new TextBlock($"Project: {project.DisplayName}"),
        };

        var projectThreads = GetThreadsForProject(project.Id, includeInternal: false);
        if (projectThreads.Length == 0)
        {
            children.Add(new TextBlock("No project threads yet."));
        }
        else
        {
            children.AddRange(projectThreads.Select(CreateThreadOpenButton));
        }

        var internalThreads = GetThreadsForProject(project.Id, includeInternal: true)
            .Where(static thread => thread.Kind == WorkThreadKind.InternalThread)
            .ToArray();
        if (internalThreads.Length > 0)
        {
            children.Add(new TextBlock("Internal Threads"));
            children.AddRange(internalThreads.Select(CreateThreadOpenButton));
        }

        return new VStack([.. children]) { Spacing = 1 };
    }

    private Button CreateThreadOpenButton(WorkThreadDescriptor thread)
    {
        return new Button(new TextBlock(BuildThreadSidebarLabel(thread)))
            .Click(() => OpenThread(thread.ThreadId));
    }

    private Visual BuildThreadPane()
    {
        _tabStripVisual ??= CreateComputedVisual(BuildOpenTabsContent);
        _threadHeaderVisual ??= CreateComputedVisual(BuildSelectedThreadHeader);
        _threadInput ??= CreatePromptEditor();
        _threadInputView ??= _threadInput.Scrollable();
        _chatBackendSelect ??= new Select<ChatBackendOption>()
            .SelectionChanged((_, e) => OnChatBackendSelectionChanged(e.NewIndex))
            .MinWidth(14)
            .MaxWidth(22);
        _chatModelSelect ??= new Select<ChatModelOption>()
            .SelectionChanged((_, e) => OnChatModelSelectionChanged(e.NewIndex))
            .MinWidth(18)
            .MaxWidth(36);
        _chatReasoningSelect ??= new Select<ChatReasoningOption>()
            .SelectionChanged((_, e) => OnChatReasoningSelectionChanged(e.NewIndex))
            .MinWidth(12)
            .MaxWidth(22);
        _chatAutoApproveCheckBox ??= new CheckBox("Auto-Approve")
            .IsChecked(_chatAutoApproveState);
        _chatBackendStatusMarkup ??= new Markup(string.Empty)
        {
            Wrap = true,
        };

        var controls = new WrapHStack(
            [
                new Button(new TextBlock("Send")).Click(() => _ = SendSelectedThreadPromptAsync(steer: false)),
                new Button(new TextBlock("Steer")).Click(() => _ = SendSelectedThreadPromptAsync(steer: true)),
                new Button(new TextBlock("Abort")).Click(() => _ = AbortSelectedThreadAsync()),
                new Button(new TextBlock("Close Tab")).Click(() => _ = CloseSelectedThreadAsync()),
            ])
        {
            Spacing = 2,
            RunSpacing = 1,
        };

        _threadBottomPanel = new VStack(
            [
                _threadInputView,
                controls,
                new WrapHStack(
                    [
                        new VStack([new TextBlock("Backend"), _chatBackendSelect]) { Spacing = 0 },
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
        };

        _threadPaneLayout = new DockLayout(
            top: new VStack(
                [
                    _tabStripVisual,
                    _threadHeaderVisual,
                ])
            {
                Spacing = 1,
            },
            content: new TextBlock("Open or create a thread to start working."),
            bottom: _threadBottomPanel);

        RefreshThreadPaneContent();
        return _threadPaneLayout;
    }

    private Visual BuildOpenTabsContent()
    {
        if (_viewState.OpenThreadIds.Count == 0)
        {
            return new TextBlock("No open tabs.");
        }

        var items = new List<Visual>();
        foreach (var threadId in _viewState.OpenThreadIds)
        {
            var thread = FindThread(threadId);
            if (thread is null)
            {
                continue;
            }

            var isSelected = string.Equals(thread.ThreadId, _selectedThreadId, StringComparison.OrdinalIgnoreCase);
            var title = isSelected ? $"> {thread.Title}" : thread.Title;
            items.Add(
                new HStack(
                    [
                        new Button(new TextBlock(title)).Click(() => OpenThread(thread.ThreadId)),
                        new Button(new TextBlock("x")).Click(() => _ = CloseThreadAsync(thread.ThreadId)),
                    ])
                {
                    Spacing = 1,
                });
        }

        return new WrapHStack([.. items])
        {
            Spacing = 1,
            RunSpacing = 1,
        };
    }

    private Visual BuildSelectedThreadHeader()
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            return CreateSectionGroup("Thread", new TextBlock("No thread selected."));
        }

        return CreateSectionGroup(
            "Thread",
            new VStack(
                [
                    new TextBlock(thread.Title),
                    new TextBlock(BuildThreadScopeSummary(thread, _projects, _catalogOptions.GlobalRoot)),
                    new TextBlock($"Backend: {thread.BackendId}"),
                ])
            {
                Spacing = 1,
            });
    }

    private async Task ReloadCatalogAsync()
    {
        try
        {
            SetStatus("Refreshing project and thread catalog...", showSpinner: true);
            _projects = await _projectCatalog.LoadAsync().ConfigureAwait(false);
            _threads = await _runtimeService.ListRecoverableThreadsAsync().ConfigureAwait(false);
            EnsureSelectionDefaults();
            RefreshView();
            SetStatus("Catalog refreshed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to refresh catalog: {ex.Message}");
        }
    }

    private async Task CreateGlobalThreadAsync()
    {
        try
        {
            SetStatus("Creating global thread...", showSpinner: true);
            var title = ReadUiValue(() => _newThreadTitleInput?.Text?.Trim());
            var executionOptions = BuildPreferredExecutionOptions(
                GetPreferredBackendId(),
                _catalogOptions.GlobalRoot,
                []);
            var thread = await _runtimeService.CreateGlobalThreadAsync(executionOptions, title).ConfigureAwait(false);
            await RegisterCreatedThreadAsync(thread).ConfigureAwait(false);
            ClearThreadTitleDraft();
            SetStatus($"Created global thread '{thread.Title}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create global thread: {ex.Message}");
        }
    }

    private async Task CreateProjectThreadAsync()
    {
        var project = GetSelectedProject();
        if (project is null)
        {
            SetStatus("Select a project before creating a project thread.");
            return;
        }

        try
        {
            SetStatus($"Creating thread for '{project.DisplayName}'...", showSpinner: true);
            var title = ReadUiValue(() => _newThreadTitleInput?.Text?.Trim());
            var executionOptions = BuildPreferredExecutionOptions(
                GetPreferredBackendId(),
                project.ProjectPath,
                [project.ProjectPath]);
            var thread = await _runtimeService.CreateProjectThreadAsync(project, executionOptions, title).ConfigureAwait(false);
            await RegisterCreatedThreadAsync(thread).ConfigureAwait(false);
            ClearThreadTitleDraft();
            SetStatus($"Created thread '{thread.Title}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create project thread: {ex.Message}");
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

        OpenThread(thread.ThreadId);
        await EnsureThreadHistoryLoadedAsync(thread).ConfigureAwait(false);
    }

    private void OpenThread(string threadId)
    {
        var thread = FindThread(threadId);
        if (thread is null)
        {
            SetStatus($"Thread '{threadId}' was not found.");
            return;
        }

        EnsureThreadTab(thread);
        if (!_viewState.OpenThreadIds.Contains(threadId, StringComparer.OrdinalIgnoreCase))
        {
            _viewState.OpenThreadIds.Add(threadId);
        }

        _viewState.SelectedThreadId = threadId;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        _selectedThreadId = threadId;
        _selectedProjectId = thread.ProjectRef ?? _selectedProjectId;
        _ = PersistViewStateAsync();
        RefreshView();
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
        _viewState.OpenThreadIds.RemoveAll(id => string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase));
        _threadTabs.Remove(threadId);
        if (string.Equals(_selectedThreadId, threadId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedThreadId = _viewState.OpenThreadIds.FirstOrDefault();
            _viewState.SelectedThreadId = _selectedThreadId;
        }

        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshView();
    }

    private async Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
    {
        var tab = EnsureThreadTab(thread);
        if (tab.HistoryLoaded || tab.HistoryLoading)
        {
            return;
        }

        tab.HistoryLoading = true;
        try
        {
            SetStatus($"Loading thread '{thread.Title}'...", showSpinner: true);
            var executionOptions = BuildExecutionOptions(thread, tab);
            await _runtimeService.EnsureCoordinatorSessionAsync(thread, executionOptions, cancellationToken).ConfigureAwait(false);
            var history = await _runtimeService.GetHistoryAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
            ResetThreadTab(tab);
            foreach (var @event in history)
            {
                HandleAgentEvent(thread, tab, @event);
            }

            tab.HistoryLoaded = true;
            SetStatus($"Loaded thread '{thread.Title}'.");
        }
        catch (Exception ex)
        {
            RenderThreadFailure(tab, $"Failed to load history: {ex.Message}");
            SetStatus($"Failed to load '{thread.Title}': {ex.Message}");
        }
        finally
        {
            tab.HistoryLoading = false;
        }
    }

    private async Task SendSelectedThreadPromptAsync(bool steer)
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            SetStatus("Open a thread before sending a prompt.");
            return;
        }

        var prompt = ReadUiValue(() => _threadInput?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var tab = EnsureThreadTab(thread);
        ClearThreadInput();
        var pending = CreatePendingChatMessage(prompt);
        AppendThreadTimelineItem(tab, pending.UserItem);
        AppendThreadTimelineItem(tab, pending.AssistantItem);
        tab.PendingAssistant = new PendingAssistantState(pending.AssistantItem, pending.StreamingMarkdown);

        try
        {
            SetStatus($"Running '{thread.Title}'...", showSpinner: true);
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
            RefreshView();
        }
        catch (Exception ex)
        {
            RenderThreadFailure(tab, $"Failed to send prompt: {ex.Message}");
            SetStatus($"Failed to send prompt: {ex.Message}");
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
            SetStatus($"Aborted '{thread.Title}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to abort '{thread.Title}': {ex.Message}");
        }
    }

    private void HandleRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
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
                    TryRenderThreadInteraction(tab, () => HandleAgentEvent(thread, tab, agentEvent.Event), "agent event");
                }

                break;

            case WorkThreadHostEvent hostEvent:
                UpdateThreadSummary(thread, hostEvent.Message, hostEvent.Timestamp);
                if (_threadTabs.TryGetValue(thread.ThreadId, out var hostTab))
                {
                    TryRenderThreadInteraction(
                        hostTab,
                        () => UpsertThreadStatus(
                            hostTab,
                            dictionary: null,
                            key: null,
                            markdown: hostEvent.Message,
                            tone: ChatTimelineTone.Notice,
                            headerOverride: "Notice",
                            headerSecondary: GetSessionUpdateHeader(hostEvent.Kind)),
                        "host event");
                }

                break;
        }

        RefreshView();
    }

    private void HandleAgentEvent(WorkThreadDescriptor thread, ThreadTabState tab, AgentEvent @event)
    {
        switch (@event)
        {
            case AgentContentDeltaEvent delta:
                AppendThreadContent(tab, delta);
                break;

            case AgentContentCompletedEvent completed:
                FinalizeThreadContent(tab, completed);
                if (completed.Kind == AgentContentKind.Assistant && !string.IsNullOrWhiteSpace(completed.Content))
                {
                    thread.LatestSummary = SummarizeThreadContent(completed.Content);
                }

                break;

            case AgentPlanSnapshotEvent planEvent:
                UpsertThreadStatus(
                    tab,
                    tab.PlanStates,
                    "plan",
                    FormatChatPlanMarkdown(planEvent.Snapshot),
                    ChatTimelineTone.Notice,
                    headerOverride: "Plan");
                break;

            case AgentActivityEvent activity:
                UpsertThreadStatus(
                    tab,
                    tab.ActivityStates,
                    activity.ActivityId,
                    FormatChatActivityMarkdown(activity),
                    ChatTimelineTone.Activity,
                    headerOverride: GetActivityHeadline(activity.Kind, activity.Phase));
                break;

            case AgentRawEvent raw:
                UpsertThreadStatus(
                    tab,
                    dictionary: null,
                    key: null,
                    markdown: FormatChatRawEventMarkdown(raw),
                    tone: ChatTimelineTone.Activity,
                    headerOverride: "Raw Event");
                break;

            case AgentPermissionRequest permissionRequest:
                tab.PermissionRequests[permissionRequest.InteractionId] = permissionRequest;
                UpsertThreadInteraction(
                    tab,
                    permissionRequest.InteractionId,
                    FormatChatPermissionRequestMarkdown(permissionRequest),
                    null,
                    ChatTimelineTone.Interaction,
                    "Action Required",
                    "Permission Request");
                break;

            case AgentUserInputRequest userInputRequest:
                tab.UserInputRequests[userInputRequest.InteractionId] = userInputRequest;
                UpsertThreadInteraction(
                    tab,
                    userInputRequest.InteractionId,
                    FormatChatUserInputRequestMarkdown(userInputRequest, _chatAutoApproveEnabled),
                    null,
                    ChatTimelineTone.Interaction,
                    "Action Required",
                    "User Input Request");
                break;

            case AgentInteractionEvent interaction:
                UpsertThreadInteraction(
                    tab,
                    interaction.InteractionId,
                    null,
                    FormatChatInteractionResolutionMarkdown(interaction, includeHeading: false),
                    ChatTimelineTone.Interaction);
                tab.PermissionRequests.Remove(interaction.InteractionId);
                tab.UserInputRequests.Remove(interaction.InteractionId);
                break;

            case AgentSessionUpdateEvent update:
                if (update.Kind == AgentSessionUpdateKind.Idle)
                {
                    ReplaceEmptyPendingAssistantPlaceholder(tab);
                    SetStatus($"Thread '{thread.Title}' is idle.");
                    break;
                }

                UpsertThreadStatus(
                    tab,
                    dictionary: null,
                    key: null,
                    markdown: FormatChatSessionUpdateMarkdown(update),
                    tone: update.Kind == AgentSessionUpdateKind.Warning ? ChatTimelineTone.Interaction : ChatTimelineTone.Notice,
                    headerOverride: "Notice",
                    headerSecondary: GetSessionUpdateHeader(update.Kind));
                if (!string.IsNullOrWhiteSpace(update.Message))
                {
                    thread.LatestSummary = SummarizeThreadContent(update.Message);
                }

                break;

            case AgentErrorEvent error:
                RenderThreadError(tab, error.Message);
                thread.LatestSummary = SummarizeThreadContent(error.Message);
                break;
        }
    }

    private void AppendThreadContent(ThreadTabState tab, AgentContentDeltaEvent delta)
    {
        if (string.IsNullOrEmpty(delta.Delta))
        {
            return;
        }

        var state = GetOrCreateThreadContentState(tab, delta.Kind, delta.ContentId);
        state.Buffer.Append(delta.Delta);
        var markdown = FormatChatContentMarkdown(delta.Kind, state.Buffer.ToString());
        PostToUi(() =>
        {
            state.Markdown.Markdown = markdown;
            tab.Flow.ScrollToTail();
        });
    }

    private void FinalizeThreadContent(ThreadTabState tab, AgentContentCompletedEvent completed)
    {
        var state = GetOrCreateThreadContentState(tab, completed.Kind, completed.ContentId);
        state.Buffer.Clear();
        state.Buffer.Append(completed.Content);
        var markdown = FormatChatContentMarkdown(completed.Kind, completed.Content);
        PostToUi(() =>
        {
            state.Markdown.Markdown = markdown;
            tab.Flow.ScrollToTail();
        });
    }

    private ChatContentState GetOrCreateThreadContentState(ThreadTabState tab, AgentContentKind kind, string contentId)
    {
        var key = CreateChatContentKey(kind, contentId);
        if (tab.ContentStates.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (kind == AgentContentKind.Assistant && tab.PendingAssistant is { ContentId: null } pending)
        {
            pending.ContentId = contentId;
            tab.PendingAssistant = null;
            var pendingState = new ChatContentState(pending.Item, pending.Markdown, pending.Buffer, kind);
            tab.ContentStates[key] = pendingState;
            return pendingState;
        }

        var (item, markdown) = CreateChatMarkdownItem(
            FormatChatContentMarkdown(kind, string.Empty),
            GetContentTone(kind),
            headerOverride: GetContentHeader(kind));
        var state = new ChatContentState(item, markdown, new System.Text.StringBuilder(), kind);
        tab.ContentStates[key] = state;
        AppendThreadTimelineItem(tab, item);
        return state;
    }

    private void UpsertThreadStatus(
        ThreadTabState tab,
        Dictionary<string, ChatStatusState>? dictionary,
        string? key,
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        if (dictionary is null || key is null)
        {
            var state = CreateChatStatusState(markdown, tone, headerOverride, headerSecondary);
            AppendThreadTimelineItem(tab, state.Item);
            return;
        }

        if (!dictionary.TryGetValue(key, out var stateEntry))
        {
            stateEntry = CreateChatStatusState(markdown, tone, headerOverride, headerSecondary);
            dictionary[key] = stateEntry;
            AppendThreadTimelineItem(tab, stateEntry.Item);
        }

        stateEntry.BaseMarkdown = markdown;
        PostToUi(() =>
        {
            stateEntry.Markdown.Markdown = stateEntry.MarkdownValue;
            tab.Flow.ScrollToTail();
        });
    }

    private void UpsertThreadInteraction(
        ThreadTabState tab,
        string interactionId,
        string? baseMarkdown,
        string? statusMarkdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        if (!tab.InteractionStates.TryGetValue(interactionId, out var state))
        {
            state = CreateChatStatusState(baseMarkdown ?? statusMarkdown ?? string.Empty, tone, headerOverride, headerSecondary);
            tab.InteractionStates[interactionId] = state;
            AppendThreadTimelineItem(tab, state.Item);
        }

        if (!string.IsNullOrWhiteSpace(baseMarkdown))
        {
            state.BaseMarkdown = baseMarkdown;
        }

        if (!string.IsNullOrWhiteSpace(statusMarkdown))
        {
            state.StatusMarkdown = statusMarkdown;
        }

        PostToUi(() =>
        {
            state.Markdown.Markdown = state.MarkdownValue;
            tab.Flow.ScrollToTail();
        });
    }

    private static ChatStatusState CreateChatStatusState(
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        var (item, control) = CreateChatMarkdownItem(markdown, tone, headerOverride, headerSecondary);
        return new ChatStatusState(item, control)
        {
            BaseMarkdown = markdown,
        };
    }

    private static void ReplaceEmptyPendingAssistantPlaceholder(ThreadTabState tab)
    {
        var pendingAssistant = tab.PendingAssistant;
        if (pendingAssistant is null || pendingAssistant.Buffer.Length > 0)
        {
            return;
        }

        pendingAssistant.Markdown.Markdown = "_No assistant content was returned._";
        tab.PendingAssistant = null;
    }

    private static void RenderThreadError(ThreadTabState tab, string message)
    {
        var pendingAssistant = tab.PendingAssistant;
        if (pendingAssistant is not null)
        {
            pendingAssistant.Buffer.Append(message);
            pendingAssistant.Markdown.Markdown = message;
            tab.PendingAssistant = null;
            return;
        }

        tab.Flow.Items.Add(CreateChatMarkdownItem(message, ChatTimelineTone.Interaction, headerOverride: "Error").Item);
    }

    private static void RenderThreadFailure(ThreadTabState tab, string markdown)
    {
        var pendingAssistant = tab.PendingAssistant;
        if (pendingAssistant is not null)
        {
            pendingAssistant.Buffer.Append(markdown);
            pendingAssistant.Markdown.Markdown = markdown;
            tab.PendingAssistant = null;
            return;
        }

        tab.Flow.Items.Add(CreateChatMarkdownItem(markdown, ChatTimelineTone.Interaction, headerOverride: "Error").Item);
    }

    private void TryRenderThreadInteraction(ThreadTabState tab, Action action, string context)
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

            SetStatus($"Failed to render thread {context}: {ex.Message}");
            tab.PendingAssistant = null;
        }
    }

    private async Task<AgentPermissionDecision> HandleThreadPermissionRequestAsync(
        string threadId,
        AgentPermissionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var decision = _chatAutoApproveEnabled
            ? new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)
            : new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);

        if (_threadTabs.TryGetValue(threadId, out var tab))
        {
            TryRenderThreadInteraction(
                tab,
                () =>
                {
                    UpsertThreadInteraction(
                        tab,
                        request.InteractionId,
                        FormatChatPermissionRequestMarkdown(request),
                        FormatChatImmediatePermissionDecisionMarkdown(decision, _chatAutoApproveEnabled),
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

        var response = CreateChatUserInputResponse(request, _chatAutoApproveEnabled);
        if (_threadTabs.TryGetValue(threadId, out var tab))
        {
            TryRenderThreadInteraction(
                tab,
                () =>
                {
                    UpsertThreadInteraction(
                        tab,
                        request.InteractionId,
                        FormatChatUserInputRequestMarkdown(request, _chatAutoApproveEnabled),
                        FormatChatImmediateUserInputResponseMarkdown(response, _chatAutoApproveEnabled),
                        ChatTimelineTone.Interaction,
                        "Action Required",
                        "User Input Request");
                },
                "user input request");
        }

        return response;
    }

    private WorkThreadExecutionOptions BuildExecutionOptions(WorkThreadDescriptor thread, ThreadTabState tab)
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
        var model = ReadUiValue(
            () =>
            {
                if (_chatBackendSelect is null || _chatModelSelect is null)
                {
                    return backendState.SelectedModelId;
                }

                var backendOptions = BuildChatBackendOptions();
                if ((uint)_chatBackendSelect.SelectedIndex < (uint)backendOptions.Count &&
                    string.Equals(backendOptions[_chatBackendSelect.SelectedIndex].BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    var modelOptions = BuildChatModelOptions(backendState);
                    if ((uint)_chatModelSelect.SelectedIndex < (uint)modelOptions.Count)
                    {
                        return modelOptions[_chatModelSelect.SelectedIndex].ModelId;
                    }
                }

                return backendState.SelectedModelId;
            });

        var reasoning = ReadUiValue(
            () =>
            {
                if (_chatBackendSelect is null || _chatReasoningSelect is null)
                {
                    return backendState.SelectedReasoningEffort;
                }

                var backendOptions = BuildChatBackendOptions();
                if ((uint)_chatBackendSelect.SelectedIndex < (uint)backendOptions.Count &&
                    string.Equals(backendOptions[_chatBackendSelect.SelectedIndex].BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    var selectedModel = backendState.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, model, StringComparison.Ordinal));
                    var reasoningOptions = BuildChatReasoningOptions(selectedModel);
                    if ((uint)_chatReasoningSelect.SelectedIndex < (uint)reasoningOptions.Count)
                    {
                        return reasoningOptions[_chatReasoningSelect.SelectedIndex].Effort;
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

    private ThreadTabState EnsureThreadTab(WorkThreadDescriptor thread)
    {
        if (_threadTabs.TryGetValue(thread.ThreadId, out var existing))
        {
            existing.Thread = thread;
            return existing;
        }

        var flow = RunOnUiThread(
            static () => new DocumentFlow
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
                ItemPadding = new Thickness(1, 1, 0, 0),
            });

        var state = new ThreadTabState(thread, flow)
        {
            BackendId = new AgentBackendId(thread.BackendId),
        };

        if (_chatBackendStates.TryGetValue(thread.BackendId, out var backendState))
        {
            state.ModelId = backendState.SelectedModelId;
            state.ReasoningEffort = backendState.SelectedReasoningEffort;
        }

        _threadTabs[thread.ThreadId] = state;
        return state;
    }

    private void ResetThreadTab(ThreadTabState tab)
    {
        PostToUi(() => tab.Flow.Items.Clear());
        tab.ContentStates.Clear();
        tab.ActivityStates.Clear();
        tab.InteractionStates.Clear();
        tab.PlanStates.Clear();
        tab.PermissionRequests.Clear();
        tab.UserInputRequests.Clear();
        tab.PendingAssistant = null;
    }

    private void AppendThreadTimelineItem(ThreadTabState tab, DocumentFlowItem item)
    {
        PostToUi(() =>
        {
            tab.Flow.Items.Add(item);
            tab.Flow.ScrollToTail();
        });
    }

    private ProjectDescriptor? GetSelectedProject()
        => GetProjectById(_selectedProjectId);

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

    private void RefreshView()
    {
        PostToUi(
            () =>
            {
                EnsureSelectionDefaults();
                if (_header is not null)
                {
                    _header.Text = BuildHeaderText();
                }

                _viewRefreshState.Value++;
                RefreshThreadPaneContent();
            });
    }

    private void RefreshThreadPaneContent()
    {
        if (_threadPaneLayout is null)
        {
            return;
        }

        var selectedThread = GetSelectedThread();
        if (_threadBottomPanel is not null)
        {
            _threadBottomPanel.IsVisible = selectedThread is not null;
        }

        if (selectedThread is null)
        {
            _threadPaneLayout.Content = new TextBlock("Open or create a thread to start working.");
            return;
        }

        var tab = EnsureThreadTab(selectedThread);
        RefreshChatSelectorsForThread(tab);
        _threadPaneLayout.Content = tab.Flow;
    }

    private void RefreshChatSelectorsForThread(ThreadTabState tab)
    {
        _chatSelectorsRefreshing = true;
        try
        {
            var backendSelect = _chatBackendSelect!;
            var modelSelect = _chatModelSelect!;
            var reasoningSelect = _chatReasoningSelect!;
            var backendOptions = BuildChatBackendOptions();
            ReplaceSelectItems(backendSelect, backendOptions);
            backendSelect.SelectedIndex = Math.Clamp(
                backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, tab.BackendId.Value, StringComparison.OrdinalIgnoreCase)),
                0,
                Math.Max(0, backendOptions.Count - 1));

            var backendState = _chatBackendStates[tab.BackendId.Value];
            if (!string.IsNullOrWhiteSpace(tab.ModelId) &&
                backendState.Models.Any(model => string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal)))
            {
                backendState.SelectedModelId = tab.ModelId;
            }

            var modelOptions = BuildChatModelOptions(backendState);
            ReplaceSelectItems(modelSelect, modelOptions);
            modelSelect.SelectedIndex = Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, tab.ModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1));

            var selectedModel = backendState.Models.FirstOrDefault(model =>
                string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal))
                ?? GetSelectedModel(backendState);
            var reasoningOptions = BuildChatReasoningOptions(selectedModel);
            ReplaceSelectItems(reasoningSelect, reasoningOptions);
            reasoningSelect.SelectedIndex = Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == tab.ReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1));

            backendSelect.IsEnabled = false;
            _chatBackendStatusMarkup!.Text = BuildChatBackendStatusMarkup(_chatBackendStates.Values, tab.BackendId, isInitializing: false);
        }
        finally
        {
            _chatSelectorsRefreshing = false;
        }
    }

    private void OnChatBackendSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var thread = GetSelectedThread();
        if (thread is null || thread.IsBackendLocked)
        {
            return;
        }

        var options = BuildChatBackendOptions();
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        var tab = EnsureThreadTab(thread);
        tab.BackendId = options[newIndex].BackendId;
        RefreshView();
    }

    private void OnChatModelSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var thread = GetSelectedThread();
        if (thread is null)
        {
            return;
        }

        var tab = EnsureThreadTab(thread);
        var backendState = _chatBackendStates[tab.BackendId.Value];
        var options = BuildChatModelOptions(backendState);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ModelId = options[newIndex].ModelId;
    }

    private void OnChatReasoningSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var thread = GetSelectedThread();
        if (thread is null)
        {
            return;
        }

        var tab = EnsureThreadTab(thread);
        var backendState = _chatBackendStates[tab.BackendId.Value];
        var selectedModel = backendState.Models.FirstOrDefault(model => string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal));
        var options = BuildChatReasoningOptions(selectedModel);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ReasoningEffort = options[newIndex].Effort;
    }

    private AgentBackendId GetPreferredBackendId()
    {
        return ReadUiValue(
            () =>
            {
                var options = BuildChatBackendOptions();
                if (_chatBackendSelect is not null &&
                    (uint)_chatBackendSelect.SelectedIndex < (uint)options.Count)
                {
                    return options[_chatBackendSelect.SelectedIndex].BackendId;
                }

                return AgentBackendIds.Codex;
            });
    }

    private void SelectProject(string projectId)
    {
        _selectedProjectId = projectId;
        RefreshView();
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

        if (_selectedThreadId is not null && FindThread(_selectedThreadId) is { ProjectRef: { } projectRef })
        {
            _selectedProjectId = projectRef;
        }
    }

    private string BuildHeaderText()
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            return "CodeAlta | no thread selected";
        }

        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => $"CodeAlta | backend={thread.BackendId} | thread={thread.Title} | scope=global",
            WorkThreadKind.ProjectThread => $"CodeAlta | backend={thread.BackendId} | project={GetProjectById(thread.ProjectRef)?.Slug ?? "?"} | thread={thread.Title}",
            WorkThreadKind.InternalThread => $"CodeAlta | backend={thread.BackendId} | internal={thread.Title}",
            _ => $"CodeAlta | thread={thread.Title}",
        };
    }

    private void SetStatus(string message, bool showSpinner = false)
    {
        PostToUi(
            () =>
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

    private void PostToUi(Action action)
    {
        (_dispatcher ?? Dispatcher.Current).Post(action);
    }

    private T ReadUiValue<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = _dispatcher ?? Dispatcher.Current;
        return dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
    }

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

    private void ClearThreadInput()
    {
        ReadUiValue(
            () =>
            {
                _threadInput!.Text = string.Empty;
                return 0;
            });
    }

    private void ClearThreadTitleDraft()
    {
        ReadUiValue(
            () =>
            {
                if (_newThreadTitleInput is not null)
                {
                    _newThreadTitleInput.Text = string.Empty;
                }

                return 0;
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
        if (IsChatAutoApproveBinding(binding, _chatAutoApproveState))
        {
            _chatAutoApproveEnabled = _chatAutoApproveState.Value;
        }
    }

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

    private static string BuildThreadSidebarLabel(WorkThreadDescriptor thread)
    {
        var summary = string.IsNullOrWhiteSpace(thread.LatestSummary)
            ? thread.Status.ToString()
            : thread.LatestSummary;
        return $"{thread.Title}\n{summary}";
    }

    private static Group CreateSectionGroup(string title, Visual content)
    {
        return new Group(new Markup($"[bold]{title}[/]"), content)
            .Padding(1)
            .Style(XenoAtom.Terminal.UI.Styling.GroupStyle.Rounded);
    }

    private ChatPromptEditor CreatePromptEditor()
    {
        return new ChatPromptEditor(text => _ = SendSelectedThreadPromptAsync(steer: false))
            .PromptMarkup("[primary]>[/] ")
            .ContinuationPromptMarkup("[muted]·[/] ")
            .EnableWordHints(false)
            .MinHeight(3)
            .MaxHeight(9);
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

        public PendingAssistantState? PendingAssistant { get; set; }

        public Dictionary<string, ChatContentState> ContentStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> ActivityStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> InteractionStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> PlanStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, AgentPermissionRequest> PermissionRequests { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, AgentUserInputRequest> UserInputRequests { get; } = new(StringComparer.Ordinal);
    }
}
