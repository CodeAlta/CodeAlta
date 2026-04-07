using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Roles;
using CodeAlta.Persistence;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Owns per-thread coordinator sessions, recovers backend-owned project/global threads, and projects sanitized runtime events.
/// </summary>
public sealed class WorkThreadRuntimeService : IAsyncDisposable
{
    private static readonly Regex ScheduleBlockRegex = new(
        @"```codealta_schedule\s*\n.*?```",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly AgentHub _agentHub;
    private readonly ProjectCatalog _projectCatalog;
    private readonly WorkThreadCatalog _threadCatalog;
    private readonly RoleProfileStore _roleProfileStore;
    private readonly AgentInstructionTemplateProvider _instructionTemplateProvider;
    private readonly CatalogOptions _catalogOptions;
    private readonly Channel<WorkThreadRuntimeEvent> _events = Channel.CreateUnbounded<WorkThreadRuntimeEvent>();
    private readonly Dictionary<string, ThreadSessionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkThreadRuntimeService"/> class.
    /// </summary>
    public WorkThreadRuntimeService(
        AgentHub agentHub,
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        RoleProfileStore roleProfileStore,
        AgentInstructionTemplateProvider instructionTemplateProvider,
        CatalogOptions catalogOptions)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(roleProfileStore);
        ArgumentNullException.ThrowIfNull(instructionTemplateProvider);
        ArgumentNullException.ThrowIfNull(catalogOptions);

        _agentHub = agentHub;
        _projectCatalog = projectCatalog;
        _threadCatalog = threadCatalog;
        _roleProfileStore = roleProfileStore;
        _instructionTemplateProvider = instructionTemplateProvider;
        _catalogOptions = catalogOptions;
    }

    /// <summary>
    /// Streams sanitized runtime events across all active threads.
    /// </summary>
    public IAsyncEnumerable<WorkThreadRuntimeEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
        => _events.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Lists recoverable user-facing threads from backend session history.
    /// </summary>
    public async Task<IReadOnlyList<WorkThreadDescriptor>> ListRecoverableThreadsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var internalThreads = await _threadCatalog.LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<WorkThreadDescriptor>(internalThreads.Count);
        results.AddRange(internalThreads);

        foreach (var backendId in _agentHub.ListRegisteredBackends())
        {
            IReadOnlyList<AgentSessionMetadata> sessions;
            try
            {
                sessions = await _agentHub.ListSessionsAsync(backendId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (KeyNotFoundException)
            {
                continue;
            }
            catch
            {
                continue;
            }

            foreach (var session in sessions)
            {
                var thread = TryCreateRecoverableThread(backendId, session, projects);
                if (thread is not null)
                {
                    results.Add(thread);
                }
            }
        }

        return results
            .GroupBy(static thread => thread.ThreadId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();
    }

    /// <summary>
    /// Deletes a thread through the backend when supported and persists local hidden-thread metadata.
    /// </summary>
    /// <param name="thread">The thread to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the backend deleted the thread; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DeleteThreadAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var deletedByBackend = false;
        if (!string.IsNullOrWhiteSpace(thread.BackendSessionId))
        {
            try
            {
                deletedByBackend = await _agentHub.DeleteSessionAsync(
                        new AgentBackendId(thread.BackendId),
                        thread.BackendSessionId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        thread.Status = WorkThreadStatus.Archived;

        if (thread.Kind == WorkThreadKind.InternalThread)
        {
            await _threadCatalog.SaveInternalAsync(thread, cancellationToken).ConfigureAwait(false);
        }

        await UpdateThreadLocalStateAsync(thread, cancellationToken).ConfigureAwait(false);
        return deletedByBackend;
    }

    /// <summary>
    /// Persists machine-local thread metadata for a recoverable thread.
    /// </summary>
    /// <param name="thread">The thread whose local state should be updated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PersistThreadLocalStateAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        await UpdateThreadLocalStateAsync(thread, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new global thread session and returns its descriptor.
    /// </summary>
    public async Task<WorkThreadDescriptor> CreateGlobalThreadAsync(
        WorkThreadExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var now = DateTimeOffset.UtcNow;
        var thread = new WorkThreadDescriptor
        {
            ThreadId = string.Empty,
            Kind = WorkThreadKind.GlobalThread,
            BackendId = options.BackendId.Value,
            BackendSessionId = string.Empty,
            WorkingDirectory = options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(title) ? "Global Thread" : title.Trim(),
            Status = WorkThreadStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = "Global overview and coordination thread.",
        };

        await EnsureCoordinatorSessionAsync(thread, options, cancellationToken).ConfigureAwait(false);
        return thread;
    }

    /// <summary>
    /// Creates a new project thread session and returns its descriptor.
    /// </summary>
    public async Task<WorkThreadDescriptor> CreateProjectThreadAsync(
        ProjectDescriptor project,
        WorkThreadExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        var now = DateTimeOffset.UtcNow;
        var thread = new WorkThreadDescriptor
        {
            ThreadId = string.Empty,
            Kind = WorkThreadKind.ProjectThread,
            BackendId = options.BackendId.Value,
            BackendSessionId = string.Empty,
            ProjectRef = project.Id,
            WorkingDirectory = options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(title) ? project.DisplayName : title.Trim(),
            Status = WorkThreadStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = $"Project thread for {project.DisplayName}.",
        };

        await EnsureCoordinatorSessionAsync(thread, options, cancellationToken).ConfigureAwait(false);
        return thread;
    }

    /// <summary>
    /// Creates a host-owned internal child thread for delegated work.
    /// </summary>
    public async Task<WorkThreadDescriptor> CreateInternalThreadAsync(
        WorkThreadDescriptor parentThread,
        ProjectDescriptor? project,
        WorkThreadExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentThread);
        ArgumentNullException.ThrowIfNull(options);

        var now = DateTimeOffset.UtcNow;
        var targetProjectId = project?.Id ?? parentThread.ProjectRef;
        var displayTitle = string.IsNullOrWhiteSpace(title)
            ? $"Internal · {parentThread.Title}"
            : title.Trim();

        var thread = new WorkThreadDescriptor
        {
            ThreadId = string.Empty,
            Kind = WorkThreadKind.InternalThread,
            BackendId = options.BackendId.Value,
            BackendSessionId = string.Empty,
            ProjectRef = targetProjectId,
            ParentThreadId = parentThread.ThreadId,
            WorkingDirectory = options.WorkingDirectory,
            Title = displayTitle,
            Status = WorkThreadStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = $"Delegated from '{parentThread.Title}'.",
        };

        await EnsureCoordinatorSessionAsync(thread, options, cancellationToken).ConfigureAwait(false);
        await _threadCatalog.SaveInternalAsync(thread, cancellationToken).ConfigureAwait(false);

        _events.Writer.TryWrite(new WorkThreadHostEvent(
            parentThread.ThreadId,
            now,
            AgentSessionUpdateKind.Handoff,
            $"Delegated work to internal thread '{thread.Title}'."));
        _events.Writer.TryWrite(new WorkThreadHostEvent(
            thread.ThreadId,
            now,
            AgentSessionUpdateKind.Handoff,
            $"Created internal thread from '{parentThread.Title}'."));

        return thread;
    }

    /// <summary>
    /// Ensures that the thread has an active coordinator session.
    /// </summary>
    public async Task<AgentId> EnsureCoordinatorSessionAsync(
        WorkThreadDescriptor thread,
        WorkThreadExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);

        var project = await ResolveProjectAsync(thread, cancellationToken).ConfigureAwait(false);
        var agentRoots = BuildAgentRoots(options.ProjectRoots);
        var coordinatorProfile = await _roleProfileStore.GetByIdAsync(agentRoots, "coordinator", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Coordinator profile was not available.");
        var instructions = _instructionTemplateProvider.BuildCoordinatorInstructions(thread, project, coordinatorProfile);

        ThreadSessionEntry? previousEntry = null;
        AgentId agentId;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(thread.ThreadId) &&
                _entries.TryGetValue(thread.ThreadId, out var existing) &&
                existing.Matches(options, thread.BackendSessionId))
            {
                return existing.AgentId;
            }

            if (!string.IsNullOrWhiteSpace(thread.ThreadId) && _entries.TryGetValue(thread.ThreadId, out previousEntry))
            {
                _entries.Remove(thread.ThreadId);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (previousEntry is not null)
        {
            await previousEntry.DisposeAsync(_agentHub).ConfigureAwait(false);
        }

        var identity = await _agentHub.RegisterAgentAsync(
                "coordinator",
                ToAgentScope(thread),
                options.BackendId,
                cancellationToken)
            .ConfigureAwait(false);
        agentId = identity.AgentId;

        var sessionOptions = new AgentSessionResumeOptions
        {
            Model = options.Model ?? coordinatorProfile.DefaultModel,
            ReasoningEffort = options.ReasoningEffort ?? ParseReasoningEffort(coordinatorProfile.DefaultReasoningEffort),
            Streaming = true,
            WorkingDirectory = options.WorkingDirectory,
            ProjectRoots = options.ProjectRoots,
            SystemMessage = instructions.SystemMessage,
            DeveloperInstructions = instructions.DeveloperInstructions,
            Tools = options.Tools,
            OnPermissionRequest = options.OnPermissionRequest,
            OnUserInputRequest = options.OnUserInputRequest,
        };

        string backendSessionId;
        if (string.IsNullOrWhiteSpace(thread.BackendSessionId))
        {
            backendSessionId = await _agentHub.StartSessionAsync(agentId, sessionOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            backendSessionId = await _agentHub.ResumeSessionAsync(agentId, thread.BackendSessionId, sessionOptions, cancellationToken).ConfigureAwait(false);
        }

        thread.BackendId = options.BackendId.Value;
        thread.BackendSessionId = backendSessionId;
        thread.WorkingDirectory = options.WorkingDirectory;
        thread.ThreadId = CreateThreadId(options.BackendId, backendSessionId);

        ThreadSessionEntry? entry = null;
        var projector = new EventProjector(
            thread.ThreadId,
            _events.Writer,
            () => entry?.MarkTerminated());
        var subscription = await _agentHub.SubscribeSessionEventsAsync(agentId, projector.Project, cancellationToken).ConfigureAwait(false);

        entry = new ThreadSessionEntry(
            agentId,
            options.BackendId,
            backendSessionId,
            options.WorkingDirectory,
            options.Model ?? coordinatorProfile.DefaultModel,
            options.ReasoningEffort ?? ParseReasoningEffort(coordinatorProfile.DefaultReasoningEffort),
            projector,
            subscription);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries[thread.ThreadId] = entry;
        }
        finally
        {
            _gate.Release();
        }

        return agentId;
    }

    /// <summary>
    /// Sends input to the coordinator session for a thread.
    /// </summary>
    public async Task<AgentRunId> SendAsync(
        WorkThreadDescriptor thread,
        WorkThreadExecutionOptions options,
        AgentSendOptions sendOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sendOptions);

        var agentId = await EnsureCoordinatorSessionAsync(thread, options, cancellationToken).ConfigureAwait(false);
        if (thread.StartedAt is null)
        {
            thread.MarkStarted(DateTimeOffset.UtcNow);
        }

        return await _agentHub.RunAsync(agentId, sendOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Steers the current coordinator run for a thread.
    /// </summary>
    public async Task<AgentRunId> SteerAsync(
        WorkThreadDescriptor thread,
        WorkThreadExecutionOptions options,
        AgentSteerOptions steerOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(steerOptions);

        var agentId = await EnsureCoordinatorSessionAsync(thread, options, cancellationToken).ConfigureAwait(false);
        return await _agentHub.SteerAsync(agentId, steerOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an explicit host handoff from one thread into another.
    /// </summary>
    public async Task<AgentRunId> HandoffAsync(
        WorkThreadDescriptor sourceThread,
        WorkThreadDescriptor targetThread,
        WorkThreadExecutionOptions targetOptions,
        string instruction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceThread);
        ArgumentNullException.ThrowIfNull(targetThread);
        ArgumentNullException.ThrowIfNull(targetOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);

        var timestamp = DateTimeOffset.UtcNow;
        _events.Writer.TryWrite(new WorkThreadHostEvent(
            sourceThread.ThreadId,
            timestamp,
            AgentSessionUpdateKind.Handoff,
            $"Handed off work to thread '{targetThread.Title}'."));

        _events.Writer.TryWrite(new WorkThreadHostEvent(
            targetThread.ThreadId,
            timestamp,
            AgentSessionUpdateKind.Handoff,
            $"Received handoff from thread '{sourceThread.Title}'."));

        var handoffInput = AgentInput.Text(
            $"Handoff from thread '{sourceThread.Title}' ({sourceThread.ThreadId}): {instruction}");
        return await SendAsync(targetThread, targetOptions, new AgentSendOptions { Input = handoffInput }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Aborts active work in the thread coordinator session.
    /// </summary>
    public async Task AbortAsync(string threadId, CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(threadId, cancellationToken).ConfigureAwait(false);
        await _agentHub.AbortAsync(entry.AgentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers a manual compaction for a thread coordinator session.
    /// </summary>
    public async Task CompactAsync(
        WorkThreadDescriptor thread,
        WorkThreadExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(options);

        _events.Writer.TryWrite(new WorkThreadHostEvent(
            thread.ThreadId,
            DateTimeOffset.UtcNow,
            AgentSessionUpdateKind.CompactionStarted,
            $"Manual compaction requested for '{thread.Title}'."));

        var agentId = await EnsureCoordinatorSessionAsync(thread, options, cancellationToken).ConfigureAwait(false);
        var outcome = await _agentHub.CompactAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (outcome is not null)
        {
            _events.Writer.TryWrite(new WorkThreadHostEvent(
                thread.ThreadId,
                DateTimeOffset.UtcNow,
                AgentSessionUpdateKind.CompactionCompleted,
                outcome.Message ?? (outcome.Success ? "Manual compaction completed." : "Manual compaction failed.")));
        }
    }

    /// <summary>
    /// Gets sanitized history for an active thread session.
    /// </summary>
    public async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(string threadId, CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(threadId, cancellationToken).ConfigureAwait(false);
        var history = await _agentHub.GetSessionHistoryAsync(entry.AgentId, cancellationToken).ConfigureAwait(false);
        return entry.Projector.ProjectHistory(history);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _events.Writer.TryComplete();

        foreach (var entry in _entries.Values)
        {
            await entry.DisposeAsync(_agentHub).ConfigureAwait(false);
        }

        _entries.Clear();
        _gate.Dispose();
    }

    /// <summary>
    /// Creates the stable UI thread id for a backend-owned thread.
    /// </summary>
    public static string CreateThreadId(AgentBackendId backendId, string backendSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendSessionId);
        return $"{backendId.Value}:{backendSessionId}";
    }

    private async Task<ProjectDescriptor?> ResolveProjectAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(thread.ProjectRef))
        {
            return null;
        }

        return await _projectCatalog.GetByIdAsync(thread.ProjectRef, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ThreadSessionEntry> GetEntryAsync(string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread id is required.", nameof(threadId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(threadId, out var entry))
            {
                throw new InvalidOperationException($"Thread '{threadId}' does not have an active coordinator session.");
            }

            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    private WorkThreadDescriptor? TryCreateRecoverableThread(
        AgentBackendId backendId,
        AgentSessionMetadata session,
        IReadOnlyList<ProjectDescriptor> projects)
    {
        var cwd = session.Context?.Cwd ?? session.WorkspacePath;
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        var normalizedCwd = NormalizePath(cwd);
        if (string.Equals(normalizedCwd, NormalizePath(_catalogOptions.GlobalRoot), StringComparison.OrdinalIgnoreCase))
        {
            return new WorkThreadDescriptor
            {
                ThreadId = CreateThreadId(backendId, session.SessionId),
                Kind = WorkThreadKind.GlobalThread,
                BackendId = backendId.Value,
                BackendSessionId = session.SessionId,
                WorkingDirectory = normalizedCwd,
                Title = BuildThreadTitle(session, "Global Thread"),
                Status = WorkThreadStatus.Active,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                LastActiveAt = session.UpdatedAt,
                StartedAt = session.CreatedAt,
                LatestSummary = session.Summary,
            };
        }

        var project = projects.FirstOrDefault(candidate =>
            string.Equals(NormalizePath(candidate.ProjectPath), normalizedCwd, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return null;
        }

        return new WorkThreadDescriptor
        {
            ThreadId = CreateThreadId(backendId, session.SessionId),
            Kind = WorkThreadKind.ProjectThread,
            BackendId = backendId.Value,
            BackendSessionId = session.SessionId,
            ProjectRef = project.Id,
            WorkingDirectory = normalizedCwd,
            Title = BuildThreadTitle(session, project.DisplayName),
            Status = WorkThreadStatus.Active,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            LastActiveAt = session.UpdatedAt,
            StartedAt = session.CreatedAt,
            LatestSummary = session.Summary,
        };
    }

    private static string BuildThreadTitle(AgentSessionMetadata session, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(session.Summary))
        {
            var summary = session.Summary.Trim();
            var firstLine = summary.Split(['\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                return firstLine.Length <= 80 ? firstLine : firstLine[..80];
            }
        }

        return fallback;
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = @"\\" + trimmed[8..];
        }
        else if (trimmed.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[4..];
        }

        return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private AgentScope ToAgentScope(WorkThreadDescriptor thread)
    {
        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => new AgentScope { Kind = AgentScopeKind.Global },
            WorkThreadKind.ProjectThread => new AgentScope { Kind = AgentScopeKind.Project, Id = thread.ProjectRef },
            WorkThreadKind.InternalThread when !string.IsNullOrWhiteSpace(thread.ProjectRef) => new AgentScope { Kind = AgentScopeKind.Project, Id = thread.ProjectRef },
            _ => new AgentScope { Kind = AgentScopeKind.Global },
        };
    }

    private IReadOnlyList<string> BuildAgentRoots(IReadOnlyList<string> projectRoots, bool includeGlobal = true)
    {
        var roots = new List<string>();
        if (includeGlobal)
        {
            roots.Add(_catalogOptions.AgentsRoot);
        }

        foreach (var projectRoot in projectRoots.Where(static root => !string.IsNullOrWhiteSpace(root)))
        {
            roots.Add(Path.Combine(projectRoot, ".codealta", "agents"));
        }

        return roots;
    }

    private static AgentReasoningEffort? ParseReasoningEffort(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            _ => null,
        };
    }

    private async Task UpdateThreadLocalStateAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken)
    {
        var viewState = await _threadCatalog.LoadViewStateAsync(cancellationToken).ConfigureAwait(false);
        viewState.ThreadStates[thread.ThreadId] = new WorkThreadLocalState
        {
            Archived = thread.Status == WorkThreadStatus.Archived,
            MessageCount = thread.MessageCount,
        };

        viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await _threadCatalog.SaveViewStateAsync(viewState, cancellationToken).ConfigureAwait(false);
    }

    private sealed class ThreadSessionEntry
    {
        public ThreadSessionEntry(
            AgentId agentId,
            AgentBackendId backendId,
            string backendSessionId,
            string workingDirectory,
            string? model,
            AgentReasoningEffort? reasoningEffort,
            EventProjector projector,
            IDisposable subscription)
        {
            AgentId = agentId;
            BackendId = backendId;
            BackendSessionId = backendSessionId;
            WorkingDirectory = workingDirectory;
            Model = model;
            ReasoningEffort = reasoningEffort;
            Projector = projector;
            Subscription = subscription;
        }

        public AgentId AgentId { get; }

        public AgentBackendId BackendId { get; }

        public string BackendSessionId { get; }

        public string WorkingDirectory { get; }

        public string? Model { get; }

        public AgentReasoningEffort? ReasoningEffort { get; }

        public IDisposable Subscription { get; }

        public EventProjector Projector { get; }

        public bool IsTerminated { get; private set; }

        public bool Matches(WorkThreadExecutionOptions options, string backendSessionId)
        {
            return !IsTerminated
                && string.Equals(BackendId.Value, options.BackendId.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(BackendSessionId, backendSessionId, StringComparison.Ordinal)
                && string.Equals(WorkingDirectory, options.WorkingDirectory, StringComparison.Ordinal)
                && string.Equals(Model, options.Model, StringComparison.Ordinal)
                && ReasoningEffort == options.ReasoningEffort;
        }

        public void MarkTerminated()
            => IsTerminated = true;

        public async Task DisposeAsync(AgentHub hub)
        {
            Subscription.Dispose();
            await hub.StopSessionAsync(AgentId).ConfigureAwait(false);
        }
    }

    private sealed class EventProjector
    {
        private readonly string _threadId;
        private readonly ChannelWriter<WorkThreadRuntimeEvent> _writer;
        private readonly Dictionary<string, ContentState> _content = new(StringComparer.Ordinal);

        private readonly Action _markThreadSessionTerminated;

        public EventProjector(string threadId, ChannelWriter<WorkThreadRuntimeEvent> writer, Action markThreadSessionTerminated)
        {
            ArgumentNullException.ThrowIfNull(markThreadSessionTerminated);

            _threadId = threadId;
            _writer = writer;
            _markThreadSessionTerminated = markThreadSessionTerminated;
        }

        public void Project(AgentEvent @event)
        {
            if (@event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Shutdown })
            {
                _markThreadSessionTerminated();
            }

            if (TrySanitize(@event, out var sanitized) && sanitized is not null)
            {
                _writer.TryWrite(new WorkThreadAgentEvent(_threadId, sanitized));
            }
        }

        public IReadOnlyList<AgentEvent> ProjectHistory(IReadOnlyList<AgentEvent> history)
        {
            var results = new List<AgentEvent>(history.Count);
            foreach (var @event in history)
            {
                if (TrySanitize(@event, out var sanitized) && sanitized is not null)
                {
                    results.Add(sanitized);
                }
            }

            return results;
        }

        private bool TrySanitize(AgentEvent @event, out AgentEvent? sanitized)
        {
            switch (@event)
            {
                case AgentContentDeltaEvent delta when delta.Kind == AgentContentKind.Assistant:
                    sanitized = SanitizeDelta(delta);
                    return sanitized is not null;
                case AgentContentCompletedEvent completed when completed.Kind == AgentContentKind.Assistant:
                    sanitized = SanitizeCompleted(completed);
                    return sanitized is not null;
                default:
                    sanitized = @event;
                    return true;
            }
        }

        private AgentEvent? SanitizeDelta(AgentContentDeltaEvent delta)
        {
            if (!_content.TryGetValue(delta.ContentId, out var state))
            {
                state = new ContentState();
                _content[delta.ContentId] = state;
            }

            state.Raw.Append(delta.Delta);
            var stripped = StripScheduleBlocks(state.Raw.ToString());
            if (stripped.Length == 0)
            {
                return null;
            }

            string deltaText;
            if (stripped.StartsWith(state.PreviousSanitized, StringComparison.Ordinal))
            {
                deltaText = stripped[state.PreviousSanitized.Length..];
            }
            else
            {
                deltaText = stripped;
            }

            state.PreviousSanitized = stripped;
            return string.IsNullOrEmpty(deltaText) ? null : delta with { Delta = deltaText };
        }

        private AgentEvent? SanitizeCompleted(AgentContentCompletedEvent completed)
        {
            var stripped = StripScheduleBlocks(completed.Content);
            _content[completed.ContentId] = new ContentState
            {
                PreviousSanitized = stripped,
            };

            return string.IsNullOrWhiteSpace(stripped)
                ? null
                : completed with { Content = stripped };
        }

        private static string StripScheduleBlocks(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            var stripped = ScheduleBlockRegex.Replace(content, string.Empty);
            return stripped.Trim();
        }

        private sealed class ContentState
        {
            public StringBuilder Raw { get; } = new();

            public string PreviousSanitized { get; set; } = string.Empty;
        }
    }
}
