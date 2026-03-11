using System.Text;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Workspaces;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Threading;
using XenoAtom.Logging;

internal sealed partial class CodeAltaTerminalUi : IAsyncDisposable
{
    private static readonly Logger UiLogger = LogManager.GetLogger("CodeAlta.UI");
    private readonly WorkspaceCatalog _workspaceCatalog;
    private readonly WorkspaceResolver _workspaceResolver;
    private readonly WorkThreadCatalog _threadCatalog;
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly WorkspaceCatalogOptions _catalogOptions;
    private readonly AgentHub _agentHub;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates = CreateChatBackendStates();
    private readonly Dictionary<string, ThreadTabState> _threadTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _projectScopeCheckBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _runtimeEventsCts = new();
    private readonly State<bool> _chatAutoApproveState = new(true);
    private bool _chatBindingEventsSubscribed;
    private volatile bool _chatAutoApproveEnabled = true;
    private IReadOnlyList<WorkspaceDescriptor> _workspaces = [];
    private IReadOnlyList<WorkThreadDescriptor> _threads = [];
    private WorkThreadViewState _viewState = new();
    private Dispatcher? _dispatcher;
    private DockLayout? _root;
    private TextBlock? _header;
    private TextBlock? _status;
    private Spinner? _statusSpinner;
    private Select<WorkspaceOption>? _workspaceSelect;
    private Select<ProjectFilterOption>? _projectFilterSelect;
    private Select<ChatBackendOption>? _chatBackendSelect;
    private Select<ChatModelOption>? _chatModelSelect;
    private Select<ChatReasoningOption>? _chatReasoningSelect;
    private CheckBox? _chatAutoApproveCheckBox;
    private Markup? _chatBackendStatusMarkup;
    private ChatPromptEditor? _threadInput;
    private Visual? _threadInputView;
    private TextBox? _newWorkspaceKeyInput;
    private TextBox? _newWorkspaceNameInput;
    private TextBox? _newWorkspaceRootInput;
    private TextBox? _newProjectKeyInput;
    private TextBox? _newProjectNameInput;
    private TextBox? _newProjectRepoInput;
    private TextBox? _newProjectBranchInput;
    private TextBox? _newThreadTitleInput;
    private CheckBox? _allProjectsCheckBox;
    private Task? _runtimeEventsTask;
    private bool _chatSelectorsRefreshing;
    private string? _selectedWorkspaceId;
    private string? _selectedProjectId;
    private string? _selectedThreadId;

    public CodeAltaTerminalUi(
        WorkspaceCatalog workspaceCatalog,
        WorkspaceResolver workspaceResolver,
        WorkThreadCatalog threadCatalog,
        WorkThreadRuntimeService runtimeService,
        WorkspaceCatalogOptions catalogOptions,
        AgentHub agentHub)
    {
        ArgumentNullException.ThrowIfNull(workspaceCatalog);
        ArgumentNullException.ThrowIfNull(workspaceResolver);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(agentHub);

        _workspaceCatalog = workspaceCatalog;
        _workspaceResolver = workspaceResolver;
        _threadCatalog = threadCatalog;
        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _agentHub = agentHub;
    }

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
            Text = "Ready. Select a workspace, open a thread, and start working.",
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

        _root = new DockLayout(
            top: _header,
            content: BuildMainView(),
            bottom: statusBar);

        _runtimeEventsTask = Task.Run(() => PumpRuntimeEventsAsync(_runtimeEventsCts.Token), CancellationToken.None);
        await Terminal.RunAsync(_root, () => TerminalLoopResult.Continue, cancellationToken).ConfigureAwait(false);
    }

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
        _workspaces = await _workspaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        _threads = await _threadCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (_threads.All(static thread => thread.Kind != WorkThreadKind.Global))
        {
            var timestamp = DateTimeOffset.UtcNow;
            await _threadCatalog.SaveAsync(
                    new WorkThreadDescriptor
                    {
                        ThreadId = "global",
                        Kind = WorkThreadKind.Global,
                        ScopeMode = WorkThreadScopeMode.AllProjects,
                        Title = "Global Thread",
                        Status = WorkThreadStatus.Active,
                        CreatedAt = timestamp,
                        UpdatedAt = timestamp,
                        LastActiveAt = timestamp,
                        LatestSummary = "Cross-workspace overview and delegation surface.",
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            _threads = await _threadCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        _viewState = await _threadCatalog.LoadViewStateAsync(cancellationToken).ConfigureAwait(false);
        if (_viewState.OpenThreadIds.Count == 0)
        {
            _viewState.OpenThreadIds.Add("global");
            _viewState.SelectedThreadId = "global";
            _viewState.UpdatedAt = DateTimeOffset.UtcNow;
            await _threadCatalog.SaveViewStateAsync(_viewState, cancellationToken).ConfigureAwait(false);
        }

        foreach (var threadId in _viewState.OpenThreadIds.ToArray())
        {
            var thread = FindThread(threadId);
            if (thread is null)
            {
                _viewState.OpenThreadIds.Remove(threadId);
                continue;
            }

            EnsureThreadTab(thread);
        }

        _selectedThreadId = _viewState.SelectedThreadId;
        SyncScopeSelectionFromThread(GetSelectedThread());
        EnsureScopeDefaults();
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
        _workspaceSelect ??= new Select<WorkspaceOption>()
            .SelectionChanged((_, e) => OnWorkspaceSelectionChanged(e.NewIndex))
            .MinWidth(24)
            .MaxWidth(36);
        _projectFilterSelect ??= new Select<ProjectFilterOption>()
            .SelectionChanged((_, e) => OnProjectSelectionChanged(e.NewIndex))
            .MinWidth(24)
            .MaxWidth(36);
        _allProjectsCheckBox ??= new CheckBox("All Projects");
        _newWorkspaceKeyInput ??= new TextBox { Text = string.Empty };
        _newWorkspaceNameInput ??= new TextBox { Text = string.Empty };
        _newWorkspaceRootInput ??= new TextBox { Text = @"C:\code" };
        _newProjectKeyInput ??= new TextBox { Text = string.Empty };
        _newProjectNameInput ??= new TextBox { Text = string.Empty };
        _newProjectRepoInput ??= new TextBox { Text = string.Empty };
        _newProjectBranchInput ??= new TextBox { Text = "main" };
        _newThreadTitleInput ??= new TextBox { Text = string.Empty };

        RefreshScopeSelectors();

        return new VStack(
            [
                CreateSectionGroup(
                    "Global",
                    new VStack(
                        [
                            new Button(new TextBlock("Open Global Thread")).Click(() => OpenThread("global")),
                            new TextBlock("Use the global thread to inspect recent work across workspaces and open the target scope."),
                        ])
                    {
                        Spacing = 1,
                    }),
                CreateSectionGroup(
                    "Workspaces",
                    new VStack(
                        [
                            new TextBlock("Workspace"),
                            _workspaceSelect,
                            new Button(new TextBlock("Reload Catalog")).Click(() => _ = ReloadCatalogAsync()),
                            new TextBlock("Key"),
                            _newWorkspaceKeyInput,
                            new TextBlock("Name"),
                            _newWorkspaceNameInput,
                            new TextBlock("Default Checkout Root"),
                            _newWorkspaceRootInput,
                            new Button(new TextBlock("Create Workspace")).Click(() => _ = CreateWorkspaceAsync()),
                        ])
                    {
                        Spacing = 1,
                    }),
                CreateSectionGroup("Projects", BuildProjectSection()),
                CreateSectionGroup("Threads", BuildThreadListSection()),
            ])
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
    }

    private Visual BuildProjectSection()
    {
        var selectedWorkspace = GetSelectedWorkspace();
        var children = new List<Visual>
        {
            new TextBlock("Filter"),
            _projectFilterSelect ?? new Select<ProjectFilterOption>(),
        };

        if (selectedWorkspace is null)
        {
            children.Add(new TextBlock("Create or select a workspace before configuring projects."));
            return new VStack([.. children]) { Spacing = 1 };
        }

        children.Add(new TextBlock("Initial Thread Scope"));
        children.Add(_allProjectsCheckBox ?? new CheckBox("All Projects"));
        foreach (var project in selectedWorkspace.Projects.OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            children.Add(GetOrCreateProjectScopeCheckBox(project));
        }

        children.Add(new TextBlock("Project Key"));
        children.Add(_newProjectKeyInput ?? new TextBox());
        children.Add(new TextBlock("Project Name"));
        children.Add(_newProjectNameInput ?? new TextBox());
        children.Add(new TextBlock("Repo URL"));
        children.Add(_newProjectRepoInput ?? new TextBox());
        children.Add(new TextBlock("Default Branch"));
        children.Add(_newProjectBranchInput ?? new TextBox());
        children.Add(new Button(new TextBlock("Add Project")).Click(() => _ = CreateProjectAsync()));
        return new VStack([.. children]) { Spacing = 1 };
    }

    private Visual BuildThreadListSection()
    {
        var threadButtons = new List<Visual>();
        foreach (var thread in FilterThreadsForSidebar(_threads, _selectedWorkspaceId, _selectedProjectId))
        {
            threadButtons.Add(new Button(new TextBlock(BuildThreadSidebarLabel(thread))).Click(() => OpenThread(thread.ThreadId)));
        }

        if (threadButtons.Count == 0)
        {
            threadButtons.Add(new TextBlock("No threads in the current scope."));
        }

        threadButtons.Add(new TextBlock("Thread Title"));
        threadButtons.Add(_newThreadTitleInput ?? new TextBox());
        threadButtons.Add(new Button(new TextBlock("Create Thread")).Click(() => _ = CreateThreadAsync()));
        return new VStack([.. threadButtons]) { Spacing = 1 };
    }

    private Visual BuildThreadPane()
    {
        var selectedThread = GetSelectedThread();
        if (selectedThread is null)
        {
            return CreateSectionGroup(
                "Thread",
                new TextBlock("Open a thread from the sidebar or create a new workspace thread."));
        }

        var tabState = EnsureThreadTab(selectedThread);
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

        RefreshChatSelectorsForThread(tabState);

        var topChildren = new List<Visual>
        {
            CreateSectionGroup(
                "Open Threads",
                new WrapHStack([.. BuildOpenThreadButtons()])
                {
                    Spacing = 1,
                    RunSpacing = 1,
                }),
            CreateSectionGroup(
                "Selected Thread",
                new VStack(
                    [
                        new TextBlock(selectedThread.Title),
                        new TextBlock(BuildThreadScopeSummary(selectedThread, _workspaces)),
                        new TextBlock($"Status: {selectedThread.Status}"),
                    ])
                {
                    Spacing = 1,
                }),
        };

        if (selectedThread.Kind == WorkThreadKind.Global)
        {
            topChildren.Add(CreateSectionGroup("Recent Workspace Threads", BuildGlobalOverviewSection()));
        }

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

        var bottom = new VStack(
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

        return new DockLayout(
            top: new VStack([.. topChildren]) { Spacing = 1 },
            content: tabState.Flow,
            bottom: bottom);
    }

    private Visual BuildGlobalOverviewSection()
    {
        var items = _threads
            .Where(static thread => thread.Kind == WorkThreadKind.WorkspaceThread)
            .OrderByDescending(static thread => thread.LastActiveAt)
            .Take(6)
            .Select(thread =>
            {
                var summary = string.IsNullOrWhiteSpace(thread.LatestSummary)
                    ? "No durable summary yet."
                    : thread.LatestSummary!;
                return (Visual)new Button(new TextBlock($"{thread.Title} · {summary}")).Click(() => OpenThread(thread.ThreadId));
            })
            .ToList();

        if (items.Count == 0)
        {
            items.Add(new TextBlock("No workspace threads yet."));
        }

        return new VStack([.. items])
        {
            Spacing = 1,
        };
    }

    private IReadOnlyList<Visual> BuildOpenThreadButtons()
    {
        var buttons = new List<Visual>();
        foreach (var threadId in _viewState.OpenThreadIds)
        {
            var thread = FindThread(threadId);
            if (thread is null)
            {
                continue;
            }

            var isSelected = string.Equals(thread.ThreadId, _selectedThreadId, StringComparison.OrdinalIgnoreCase);
            var label = isSelected
                ? $"* {thread.Title}"
                : thread.Title;
            buttons.Add(new Button(new TextBlock(label)).Click(() => OpenThread(thread.ThreadId)));
        }

        return buttons;
    }

    private async Task InitializeChatBackendsAsync(CancellationToken cancellationToken)
    {
        foreach (var backendState in _chatBackendStates.Values)
        {
            try
            {
                backendState.StatusMessage = "Connecting...";
                backendState.Availability = ChatBackendAvailability.Connecting;
                var models = await _agentHub.ListModelsAsync(backendState.BackendId, cancellationToken).ConfigureAwait(false);
                backendState.Models.Clear();
                backendState.Models.AddRange(models);
                backendState.Availability = ChatBackendAvailability.Ready;
                backendState.SelectedModelId ??= backendState.Models.FirstOrDefault()?.Id;
                backendState.SelectedReasoningEffort = NormalizeReasoningEffort(
                    backendState.SelectedReasoningEffort,
                    GetSelectedModel(backendState));
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
        }
    }

    private async Task ReloadCatalogAsync()
    {
        try
        {
            SetStatus("Reloading catalog...", showSpinner: true);
            await LoadCatalogStateAsync(CancellationToken.None).ConfigureAwait(false);
            RefreshView();
            SetStatus("Catalog reloaded.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to reload catalog: {ex.Message}");
        }
    }

    private async Task CreateWorkspaceAsync()
    {
        try
        {
            var draft = ReadWorkspaceDraft();
            var key = draft.Key;
            var name = draft.Name;
            var root = draft.Root;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(root))
            {
                SetStatus("Workspace key, name, and checkout root are required.");
                return;
            }

            var descriptor = new WorkspaceDescriptor
            {
                Id = WorkspaceId.NewVersion7().ToString(),
                Key = key,
                DisplayName = name,
                DefaultCheckoutRoot = root,
                MarkdownBody = $"# {name}\n",
            };

            await _workspaceCatalog.SaveWorkspaceAsync(descriptor, CancellationToken.None).ConfigureAwait(false);
            ClearWorkspaceDraft();
            await ReloadCatalogAsync().ConfigureAwait(false);
            _selectedWorkspaceId = descriptor.Id;
            ResetProjectScopeSelection();
            RefreshView();
            SetStatus($"Created workspace '{descriptor.Key}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create workspace: {ex.Message}");
        }
    }

    private async Task CreateProjectAsync()
    {
        var workspace = GetSelectedWorkspace();
        if (workspace is null)
        {
            SetStatus("Select a workspace before creating a project.");
            return;
        }

        try
        {
            var draft = ReadProjectDraft();
            var key = draft.Key;
            var name = draft.Name;
            var repo = draft.RepoUrl;
            var branch = draft.Branch;
            if (string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(repo) ||
                string.IsNullOrWhiteSpace(branch))
            {
                SetStatus("Project key, name, repo URL, and branch are required.");
                return;
            }

            var descriptor = new ProjectDescriptor
            {
                Id = ProjectId.NewVersion7().ToString(),
                Key = key,
                DisplayName = name,
                RepoUrl = repo,
                DefaultBranch = branch,
                Checkout = new CheckoutRule
                {
                    PathTemplate = "{workspaceKey}\\{projectKey}",
                },
                MarkdownBody = $"# {name}\n",
            };

            await _workspaceCatalog.SaveProjectAsync(descriptor, CancellationToken.None).ConfigureAwait(false);
            if (!workspace.ProjectRefs.Contains(descriptor.Id, StringComparer.OrdinalIgnoreCase))
            {
                workspace.ProjectRefs.Add(descriptor.Id);
            }

            await _workspaceCatalog.SaveWorkspaceAsync(workspace, CancellationToken.None).ConfigureAwait(false);
            ClearProjectDraft();
            await ReloadCatalogAsync().ConfigureAwait(false);
            _selectedWorkspaceId = workspace.Id;
            ResetProjectScopeSelection();
            RefreshView();
            SetStatus($"Added project '{descriptor.Key}' to workspace '{workspace.Key}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create project: {ex.Message}");
        }
    }

    private async Task CreateThreadAsync()
    {
        var workspace = GetSelectedWorkspace();
        if (workspace is null)
        {
            SetStatus("Select a workspace before creating a thread.");
            return;
        }

        try
        {
            var draft = ReadThreadDraft(workspace);
            var selectedProjectRefs = draft.ProjectRefs;
            var scopeMode = draft.ScopeMode;
            var title = draft.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = BuildDefaultThreadTitle(workspace, selectedProjectRefs, scopeMode);
            }

            var timestamp = DateTimeOffset.UtcNow;
            var thread = new WorkThreadDescriptor
            {
                ThreadId = Guid.CreateVersion7().ToString(),
                Kind = WorkThreadKind.WorkspaceThread,
                WorkspaceRef = workspace.Id,
                ProjectRefs = scopeMode == WorkThreadScopeMode.AllProjects ? [] : [.. selectedProjectRefs],
                ScopeMode = scopeMode,
                Title = title!,
                Status = WorkThreadStatus.Draft,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                LastActiveAt = timestamp,
                LatestSummary = "Thread created.",
            };

            await _threadCatalog.SaveAsync(thread, CancellationToken.None).ConfigureAwait(false);
            ClearThreadDraft();
            await ReloadCatalogAsync().ConfigureAwait(false);
            OpenThread(thread.ThreadId);
            SetStatus($"Created thread '{thread.Title}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create thread: {ex.Message}");
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

        var tab = EnsureThreadTab(thread);
        var text = ReadUiValue(() => _threadInput?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("Prompt text is required.");
            return;
        }

        var pending = CreatePendingChatMessage(text);
        tab.PendingAssistant = new PendingAssistantState(pending.AssistantItem, pending.StreamingMarkdown);
        AppendThreadTimelineItem(tab, pending.UserItem);
        AppendThreadTimelineItem(tab, pending.AssistantItem);
        ClearThreadInput();

        try
        {
            SetStatus(steer ? $"Steering '{thread.Title}'..." : $"Running '{thread.Title}'...", showSpinner: true);
            var executionOptions = await BuildExecutionOptionsAsync(thread, tab).ConfigureAwait(false);
            if (steer)
            {
                await _runtimeService.SteerAsync(
                        thread,
                        executionOptions,
                        new AgentSteerOptions { Input = AgentInput.Text(text) },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await _runtimeService.SendAsync(
                        thread,
                        executionOptions,
                        new AgentSendOptions { Input = AgentInput.Text(text) },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            RenderThreadFailure(tab, ex.Message);
            SetStatus($"{(steer ? "Steer" : "Send")} failed: {ex.Message}");
        }
    }

    private async Task AbortSelectedThreadAsync()
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            SetStatus("No thread is selected.");
            return;
        }

        try
        {
            await _runtimeService.AbortAsync(thread.ThreadId, CancellationToken.None).ConfigureAwait(false);
            SetStatus($"Abort requested for '{thread.Title}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Abort failed: {ex.Message}");
        }
    }

    private async Task CloseSelectedThreadAsync()
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            return;
        }

        CloseThread(thread.ThreadId);
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshView();
        SetStatus($"Closed tab '{thread.Title}'.");
    }

    private async Task<WorkThreadExecutionOptions> BuildExecutionOptionsAsync(
        WorkThreadDescriptor thread,
        ThreadTabState tab)
    {
        if (thread.Kind == WorkThreadKind.Global)
        {
            return new WorkThreadExecutionOptions
            {
                BackendId = tab.BackendId,
                WorkingDirectory = _catalogOptions.GlobalRepoRoot,
                Model = tab.ModelId,
                ReasoningEffort = tab.ReasoningEffort,
                OnPermissionRequest = (request, cancellationToken) =>
                    HandleChatPermissionRequestAsync(thread.ThreadId, request, cancellationToken),
                OnUserInputRequest = (request, cancellationToken) =>
                    HandleChatUserInputRequestAsync(thread.ThreadId, request, cancellationToken),
            };
        }

        var workspace = _workspaces.First(threadWorkspace =>
            string.Equals(threadWorkspace.Id, thread.WorkspaceRef, StringComparison.OrdinalIgnoreCase));
        var resolution = (await _workspaceResolver.ResolveAsync(
                ScopeSelector.Workspace(workspace.Key),
                cancellationToken: CancellationToken.None)
            .ConfigureAwait(false))
            .Single();

        var resolvedProjects = thread.ScopeMode == WorkThreadScopeMode.AllProjects
            ? resolution.Projects.ToArray()
            : resolution.Projects
                .Where(project => thread.ProjectRefs.Contains(project.Project.Id, StringComparer.OrdinalIgnoreCase))
                .ToArray();

        var workingDirectory = thread.ScopeMode == WorkThreadScopeMode.SingleProject && resolvedProjects.Length == 1
            ? resolvedProjects[0].CheckoutPath
            : workspace.DefaultCheckoutRoot;

        return new WorkThreadExecutionOptions
        {
            BackendId = tab.BackendId,
            WorkingDirectory = workingDirectory,
            ProjectRoots = resolvedProjects.Select(static project => project.CheckoutPath).ToArray(),
            Model = tab.ModelId,
            ReasoningEffort = tab.ReasoningEffort,
            OnPermissionRequest = (request, cancellationToken) =>
                HandleChatPermissionRequestAsync(thread.ThreadId, request, cancellationToken),
            OnUserInputRequest = (request, cancellationToken) =>
                HandleChatUserInputRequestAsync(thread.ThreadId, request, cancellationToken),
        };
    }

    private Task<AgentPermissionDecision> HandleChatPermissionRequestAsync(
        string threadId,
        AgentPermissionRequest request,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var tab = EnsureThreadTab(threadId);
        var decision = _chatAutoApproveEnabled
            ? new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)
            : new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);

        TryRenderThreadInteraction(
            tab,
            () =>
            {
                tab.PermissionRequests[request.InteractionId] = request;
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
        return Task.FromResult(decision);
    }

    private Task<AgentUserInputResponse> HandleChatUserInputRequestAsync(
        string threadId,
        AgentUserInputRequest request,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var tab = EnsureThreadTab(threadId);
        var response = CreateChatUserInputResponse(request, _chatAutoApproveEnabled);
        TryRenderThreadInteraction(
            tab,
            () =>
            {
                tab.UserInputRequests[request.InteractionId] = request;
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

        return Task.FromResult(response);
    }

    private void HandleRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
    {
        switch (runtimeEvent)
        {
            case WorkThreadAgentEvent agentEvent:
                HandleThreadAgentEvent(agentEvent.ThreadId, agentEvent.Event);
                break;
            case WorkThreadHostEvent hostEvent:
                HandleThreadHostEvent(hostEvent);
                break;
        }
    }

    private void HandleThreadHostEvent(WorkThreadHostEvent hostEvent)
    {
        var thread = FindThread(hostEvent.ThreadId);
        if (thread is not null)
        {
            UpdateThreadSummary(thread, hostEvent.Message, hostEvent.Timestamp);
        }

        if (!_threadTabs.TryGetValue(hostEvent.ThreadId, out var tab))
        {
            return;
        }

        UpsertThreadStatus(
            tab,
            dictionary: null,
            key: null,
            markdown: hostEvent.Message,
            tone: ChatTimelineTone.Notice,
            headerOverride: "Notice",
            headerSecondary: GetSessionUpdateHeader(hostEvent.Kind));
    }

    private void HandleThreadAgentEvent(string threadId, AgentEvent @event)
    {
        var thread = FindThread(threadId);
        if (thread is not null)
        {
            UpdateThreadFromAgentEvent(thread, @event);
        }

        if (!_threadTabs.TryGetValue(threadId, out var tab))
        {
            return;
        }

        switch (@event)
        {
            case AgentRawEvent raw:
                AppendThreadTimelineItem(
                    tab,
                    CreateChatMarkdownItem(
                        FormatChatRawEventMarkdown(raw),
                        ChatTimelineTone.Notice,
                        headerOverride: "Raw Event",
                        headerSecondary: raw.BackendEventType).Item);
                break;

            case AgentContentDeltaEvent delta:
                AppendThreadContent(tab, delta);
                break;

            case AgentContentCompletedEvent completed:
                FinalizeThreadContent(tab, completed);
                break;

            case AgentPlanSnapshotEvent plan:
                UpsertThreadStatus(
                    tab,
                    tab.PlanStates,
                    $"plan:{plan.RunId?.Value ?? "session"}",
                    FormatChatPlanMarkdown(plan.Snapshot),
                    ChatTimelineTone.Notice);
                break;

            case AgentActivityEvent activity:
                UpsertThreadStatus(
                    tab,
                    tab.ActivityStates,
                    $"activity:{activity.ActivityId}",
                    FormatChatActivityMarkdown(activity),
                    ChatTimelineTone.Activity,
                    headerOverride: GetActivityHeadline(activity.Kind, activity.Phase));
                if (activity.Kind == AgentActivityKind.Turn && activity.Phase == AgentActivityPhase.Started)
                {
                    SetStatus($"Running '{tab.Thread.Title}'...", showSpinner: true);
                }

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
                SetStatus($"Permission requested in '{tab.Thread.Title}'.", showSpinner: true);
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
                SetStatus($"Question requested in '{tab.Thread.Title}'.", showSpinner: true);
                break;

            case AgentInteractionEvent interaction:
                UpsertThreadInteraction(
                    tab,
                    interaction.InteractionId,
                    null,
                    FormatChatInteractionResolutionMarkdown(interaction, includeHeading: false),
                    ChatTimelineTone.Interaction);
                if (interaction.Kind == AgentInteractionKind.PermissionResolved)
                {
                    tab.PermissionRequests.Remove(interaction.InteractionId);
                }
                else if (interaction.Kind == AgentInteractionKind.UserInputResolved)
                {
                    tab.UserInputRequests.Remove(interaction.InteractionId);
                }

                break;

            case AgentSessionUpdateEvent update:
                if (update.Kind == AgentSessionUpdateKind.Idle)
                {
                    ReplaceEmptyPendingAssistantPlaceholder(tab);
                    SetStatus($"Thread '{tab.Thread.Title}' is idle.");
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
                break;

            case AgentErrorEvent error:
                RenderThreadError(tab, error.Message);
                SetStatus($"Agent error in '{tab.Thread.Title}': {error.Message}");
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
        var state = new ChatContentState(item, markdown, new StringBuilder(), kind);
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

    private void AppendThreadTimelineItem(ThreadTabState tab, DocumentFlowItem item)
    {
        PostToUi(() =>
        {
            tab.Flow.Items.Add(item);
            tab.Flow.ScrollToTail();
        });
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

        _ = PersistThreadAsync(thread);
    }

    private void UpdateThreadSummary(WorkThreadDescriptor thread, string message, DateTimeOffset timestamp)
    {
        thread.UpdatedAt = timestamp;
        thread.LastActiveAt = timestamp;
        thread.LatestSummary = SummarizeThreadContent(message);
        _ = PersistThreadAsync(thread);
    }

    private async Task PersistThreadAsync(WorkThreadDescriptor thread)
    {
        try
        {
            await _threadCatalog.SaveAsync(thread, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && UiLogger.IsEnabled(LogLevel.Error))
            {
                UiLogger.Error(ex, $"Failed to persist thread '{thread.ThreadId}'.");
            }
        }
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
        SyncScopeSelectionFromThread(thread);
        _ = PersistViewStateAsync();
        RefreshView();
    }

    private void CloseThread(string threadId)
    {
        _viewState.OpenThreadIds.RemoveAll(openThreadId => string.Equals(openThreadId, threadId, StringComparison.OrdinalIgnoreCase));
        _threadTabs.Remove(threadId);
        if (string.Equals(_selectedThreadId, threadId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedThreadId = _viewState.OpenThreadIds.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(_selectedThreadId))
        {
            _selectedThreadId = "global";
            if (!_viewState.OpenThreadIds.Contains("global", StringComparer.OrdinalIgnoreCase))
            {
                _viewState.OpenThreadIds.Add("global");
            }
        }

        _viewState.SelectedThreadId = _selectedThreadId;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private ThreadTabState EnsureThreadTab(string threadId)
    {
        var thread = FindThread(threadId)
            ?? throw new InvalidOperationException($"Thread '{threadId}' was not found.");
        return EnsureThreadTab(thread);
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
        var state = new ThreadTabState(thread, flow);
        if (!string.IsNullOrWhiteSpace(thread.LatestSummary))
        {
            flow.Items.Add(
                CreateChatMarkdownItem(
                    thread.LatestSummary,
                    ChatTimelineTone.Assistant,
                    headerSecondary: "Restored Summary").Item);
        }

        _threadTabs[thread.ThreadId] = state;
        return state;
    }

    private WorkspaceDescriptor? GetSelectedWorkspace()
    {
        if (string.IsNullOrWhiteSpace(_selectedWorkspaceId))
        {
            return null;
        }

        return _workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.Id, _selectedWorkspaceId, StringComparison.OrdinalIgnoreCase));
    }

    private WorkThreadDescriptor? GetSelectedThread()
        => string.IsNullOrWhiteSpace(_selectedThreadId) ? null : FindThread(_selectedThreadId);

    private WorkThreadDescriptor? FindThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return _threads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshScopeSelectors()
    {
        _chatSelectorsRefreshing = true;
        try
        {
            var workspaceOptions = BuildWorkspaceOptions(_workspaces);
            if (_workspaceSelect is not null)
            {
                ReplaceSelectItems(_workspaceSelect, workspaceOptions);
                _workspaceSelect.SelectedIndex = Math.Clamp(GetWorkspaceSelectedIndex(workspaceOptions, _selectedWorkspaceId), 0, Math.Max(0, workspaceOptions.Count - 1));
            }

            var projectOptions = BuildProjectFilterOptions(GetSelectedWorkspace());
            if (_projectFilterSelect is not null)
            {
                ReplaceSelectItems(_projectFilterSelect, projectOptions);
                _projectFilterSelect.SelectedIndex = Math.Clamp(GetProjectSelectedIndex(projectOptions, _selectedProjectId), 0, Math.Max(0, projectOptions.Count - 1));
            }
        }
        finally
        {
            _chatSelectorsRefreshing = false;
        }
    }

    private void RefreshChatSelectorsForThread(ThreadTabState tab)
    {
        _chatSelectorsRefreshing = true;
        try
        {
            var backendOptions = BuildChatBackendOptions();
            ReplaceSelectItems(_chatBackendSelect!, backendOptions);
            _chatBackendSelect.SelectedIndex = Math.Clamp(
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
            ReplaceSelectItems(_chatModelSelect!, modelOptions);
            _chatModelSelect.SelectedIndex = Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, tab.ModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1));

            var selectedModel = backendState.Models.FirstOrDefault(model =>
                string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal))
                ?? GetSelectedModel(backendState);
            var reasoningOptions = BuildChatReasoningOptions(selectedModel);
            ReplaceSelectItems(_chatReasoningSelect!, reasoningOptions);
            _chatReasoningSelect.SelectedIndex = Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == tab.ReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1));

            _chatBackendStatusMarkup!.Text = BuildChatBackendStatusMarkup(_chatBackendStates.Values, tab.BackendId, isInitializing: false);
        }
        finally
        {
            _chatSelectorsRefreshing = false;
        }
    }

    private void OnWorkspaceSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var options = BuildWorkspaceOptions(_workspaces);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        _selectedWorkspaceId = options[newIndex].WorkspaceId;
        _selectedProjectId = null;
        ResetProjectScopeSelection();
        RefreshView();
    }

    private void OnProjectSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var options = BuildProjectFilterOptions(GetSelectedWorkspace());
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        _selectedProjectId = options[newIndex].ProjectId;
        RefreshView();
    }

    private void OnChatBackendSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var tab = GetSelectedThread() is { } thread ? EnsureThreadTab(thread) : null;
        if (tab is null)
        {
            return;
        }

        var options = BuildChatBackendOptions();
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.BackendId = options[newIndex].BackendId;
        RefreshView();
    }

    private void OnChatModelSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var tab = GetSelectedThread() is { } thread ? EnsureThreadTab(thread) : null;
        if (tab is null)
        {
            return;
        }

        var backendState = _chatBackendStates[tab.BackendId.Value];
        var options = BuildChatModelOptions(backendState);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ModelId = options[newIndex].ModelId;
        RefreshView();
    }

    private void OnChatReasoningSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var tab = GetSelectedThread() is { } thread ? EnsureThreadTab(thread) : null;
        if (tab is null)
        {
            return;
        }

        var backendState = _chatBackendStates[tab.BackendId.Value];
        var selectedModel = backendState.Models.FirstOrDefault(model => string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal));
        var options = BuildChatReasoningOptions(selectedModel);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ReasoningEffort = options[newIndex].Effort;
        RefreshView();
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

    private void RefreshView()
    {
        PostToUi(
            () =>
            {
                EnsureScopeDefaults();
                if (_header is not null)
                {
                    _header.Text = BuildHeaderText();
                }

                if (_root is not null)
                {
                    _root.Content = BuildMainView();
                }
            });
    }

    private void EnsureScopeDefaults()
    {
        if (_workspaces.Count == 0)
        {
            _selectedWorkspaceId = null;
            _selectedProjectId = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedWorkspaceId) ||
            _workspaces.All(workspace => !string.Equals(workspace.Id, _selectedWorkspaceId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedWorkspaceId = _workspaces[0].Id;
        }

        var workspace = GetSelectedWorkspace();
        if (workspace is null)
        {
            _selectedProjectId = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_selectedProjectId) &&
            workspace.Projects.All(project => !string.Equals(project.Id, _selectedProjectId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedProjectId = null;
        }
    }

    private void SyncScopeSelectionFromThread(WorkThreadDescriptor? thread)
    {
        if (thread is null || thread.Kind == WorkThreadKind.Global)
        {
            return;
        }

        _selectedWorkspaceId = thread.WorkspaceRef;
        _selectedProjectId = thread.ScopeMode switch
        {
            WorkThreadScopeMode.SingleProject => thread.ProjectRefs.FirstOrDefault(),
            WorkThreadScopeMode.MultiProject => thread.ProjectRefs.FirstOrDefault(),
            _ => null,
        };
    }

    private void ResetProjectScopeSelection()
    {
        _projectScopeCheckBoxes.Clear();
        _allProjectsCheckBox = new CheckBox("All Projects");
    }

    private CheckBox GetOrCreateProjectScopeCheckBox(ProjectDescriptor project)
    {
        if (_projectScopeCheckBoxes.TryGetValue(project.Id, out var existing))
        {
            existing.Text = new TextBlock(project.DisplayName);
            return existing;
        }

        var checkBox = new CheckBox(project.DisplayName);
        _projectScopeCheckBoxes[project.Id] = checkBox;
        return checkBox;
    }

    private string BuildHeaderText()
    {
        var thread = GetSelectedThread();
        var workspace = GetSelectedWorkspace();
        var projectPart = _selectedProjectId is null
            ? "projects=all"
            : $"project={GetSelectedProjectDisplayName()}";

        return thread switch
        {
            null => "CodeAlta | no thread selected",
            { Kind: WorkThreadKind.Global } => $"CodeAlta | thread={thread.Title} | scope=global",
            _ => $"CodeAlta | workspace={workspace?.Key ?? "?"} | {projectPart} | thread={thread.Title}",
        };
    }

    private string GetSelectedProjectDisplayName()
    {
        var workspace = GetSelectedWorkspace();
        var project = workspace?.Projects.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _selectedProjectId, StringComparison.OrdinalIgnoreCase));
        return project?.DisplayName ?? "unknown";
    }

    private static string BuildDefaultThreadTitle(
        WorkspaceDescriptor workspace,
        IReadOnlyList<string> selectedProjectRefs,
        WorkThreadScopeMode scopeMode)
    {
        return scopeMode switch
        {
            WorkThreadScopeMode.AllProjects => $"{workspace.DisplayName} · All Projects",
            WorkThreadScopeMode.SingleProject => $"{workspace.DisplayName} · 1 Project",
            _ => $"{workspace.DisplayName} · {selectedProjectRefs.Count} Projects",
        };
    }

    private static WorkThreadScopeMode ResolveScopeMode(
        IReadOnlyList<string> selectedProjectRefs,
        int workspaceProjectCount,
        bool allProjectsChecked)
    {
        if (allProjectsChecked || (workspaceProjectCount > 0 && selectedProjectRefs.Count == workspaceProjectCount))
        {
            return WorkThreadScopeMode.AllProjects;
        }

        return selectedProjectRefs.Count switch
        {
            <= 1 => WorkThreadScopeMode.SingleProject,
            _ => WorkThreadScopeMode.MultiProject,
        };
    }

    private static List<WorkspaceOption> BuildWorkspaceOptions(IReadOnlyList<WorkspaceDescriptor> workspaces)
    {
        return workspaces
            .OrderBy(static workspace => workspace.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(static workspace => new WorkspaceOption(workspace.Id, workspace.DisplayName))
            .ToList();
    }

    private static int GetWorkspaceSelectedIndex(IReadOnlyList<WorkspaceOption> options, string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return 0;
        }

        var tuple = options
            .Select((option, index) => (option, index))
            .FirstOrDefault(item => string.Equals(item.option.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));
        return tuple.index;
    }

    private static List<ProjectFilterOption> BuildProjectFilterOptions(WorkspaceDescriptor? workspace)
    {
        if (workspace is null)
        {
            return [new ProjectFilterOption(null, "All Projects")];
        }

        var items = new List<ProjectFilterOption> { new(null, "All Projects") };
        items.AddRange(
            workspace.Projects
                .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(static project => new ProjectFilterOption(project.Id, project.DisplayName)));
        return items;
    }

    private static int GetProjectSelectedIndex(IReadOnlyList<ProjectFilterOption> options, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return 0;
        }

        var tuple = options
            .Select((option, index) => (option, index))
            .FirstOrDefault(item => string.Equals(item.option.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        return tuple.index;
    }

    internal static IReadOnlyList<WorkThreadDescriptor> FilterThreadsForSidebar(
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? workspaceId,
        string? projectId)
    {
        ArgumentNullException.ThrowIfNull(threads);

        return threads
            .Where(thread => thread.Kind == WorkThreadKind.WorkspaceThread)
            .Where(thread => string.IsNullOrWhiteSpace(workspaceId) || string.Equals(thread.WorkspaceRef, workspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(thread =>
                string.IsNullOrWhiteSpace(projectId) ||
                thread.ScopeMode == WorkThreadScopeMode.AllProjects ||
                thread.ProjectRefs.Contains(projectId, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();
    }

    internal static string BuildThreadScopeSummary(
        WorkThreadDescriptor thread,
        IReadOnlyList<WorkspaceDescriptor> workspaces)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(workspaces);

        if (thread.Kind == WorkThreadKind.Global)
        {
            return "Global overview across all workspaces.";
        }

        var workspace = workspaces.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, thread.WorkspaceRef, StringComparison.OrdinalIgnoreCase));
        var workspaceLabel = workspace?.DisplayName ?? thread.WorkspaceRef ?? "unknown workspace";
        return thread.ScopeMode switch
        {
            WorkThreadScopeMode.AllProjects => $"{workspaceLabel} · All Projects",
            WorkThreadScopeMode.SingleProject => $"{workspaceLabel} · 1 Project",
            _ => $"{workspaceLabel} · {thread.ProjectRefs.Count} Projects",
        };
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

    private WorkspaceDraft ReadWorkspaceDraft()
    {
        return ReadUiValue(
            () => new WorkspaceDraft(
                _newWorkspaceKeyInput?.Text?.Trim(),
                _newWorkspaceNameInput?.Text?.Trim(),
                _newWorkspaceRootInput?.Text?.Trim()));
    }

    private void ClearWorkspaceDraft()
    {
        ReadUiValue(
            () =>
            {
                _newWorkspaceKeyInput!.Text = string.Empty;
                _newWorkspaceNameInput!.Text = string.Empty;
                return 0;
            });
    }

    private ProjectDraft ReadProjectDraft()
    {
        return ReadUiValue(
            () => new ProjectDraft(
                _newProjectKeyInput?.Text?.Trim(),
                _newProjectNameInput?.Text?.Trim(),
                _newProjectRepoInput?.Text?.Trim(),
                _newProjectBranchInput?.Text?.Trim()));
    }

    private void ClearProjectDraft()
    {
        ReadUiValue(
            () =>
            {
                _newProjectKeyInput!.Text = string.Empty;
                _newProjectNameInput!.Text = string.Empty;
                _newProjectRepoInput!.Text = string.Empty;
                _newProjectBranchInput!.Text = "main";
                return 0;
            });
    }

    private ThreadDraft ReadThreadDraft(WorkspaceDescriptor workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return ReadUiValue(
            () =>
            {
                var selectedProjectRefs = _projectScopeCheckBoxes
                    .Where(static entry => entry.Value.IsChecked)
                    .Select(static entry => entry.Key)
                    .ToArray();
                var scopeMode = ResolveScopeMode(selectedProjectRefs, workspace.Projects.Count, _allProjectsCheckBox?.IsChecked == true);
                return new ThreadDraft(selectedProjectRefs, scopeMode, _newThreadTitleInput?.Text?.Trim());
            });
    }

    private void ClearThreadDraft()
    {
        ReadUiValue(
            () =>
            {
                _newThreadTitleInput!.Text = string.Empty;
                return 0;
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

    private sealed record WorkspaceOption(string WorkspaceId, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ProjectFilterOption(string? ProjectId, string Label)
    {
        public override string ToString() => Label;
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

        public PendingAssistantState? PendingAssistant { get; set; }

        public Dictionary<string, ChatContentState> ContentStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> ActivityStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> InteractionStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ChatStatusState> PlanStates { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, AgentPermissionRequest> PermissionRequests { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, AgentUserInputRequest> UserInputRequests { get; } = new(StringComparer.Ordinal);
    }

    private sealed record WorkspaceDraft(string? Key, string? Name, string? Root);

    private sealed record ProjectDraft(string? Key, string? Name, string? RepoUrl, string? Branch);

    private sealed record ThreadDraft(IReadOnlyList<string> ProjectRefs, WorkThreadScopeMode ScopeMode, string? Title);
}
