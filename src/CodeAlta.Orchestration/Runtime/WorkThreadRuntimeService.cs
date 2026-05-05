using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Roles;
using CodeAlta.Catalog.Skills;
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
    private readonly SkillCatalog _skillCatalog;
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
        CatalogOptions catalogOptions,
        SkillCatalog? skillCatalog = null)
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
        _skillCatalog = skillCatalog ?? new SkillCatalog();
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
        => await ListRecoverableThreadsAsync(shouldListBackendSessions: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Lists recoverable user-facing threads from selected backend session history.
    /// </summary>
    /// <param name="shouldListBackendSessions">Optional predicate that returns whether a backend's sessions should be listed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recoverable user-facing threads.</returns>
    public async Task<IReadOnlyList<WorkThreadDescriptor>> ListRecoverableThreadsAsync(
        Func<AgentBackendId, bool>? shouldListBackendSessions,
        CancellationToken cancellationToken = default)
    {
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var internalThreads = await _threadCatalog.LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<RecoverableThreadCandidate>(internalThreads.Count);
        results.AddRange(internalThreads.Select(static thread => new RecoverableThreadCandidate(thread, IsCodeAltaSession: true)));

        var backendIds = _agentHub.ListRegisteredBackends()
            .Where(backendId => shouldListBackendSessions?.Invoke(backendId) != false)
            .OrderBy(static backendId => IsProviderManagedBackend(backendId) ? 1 : 0)
            .ToArray();

        var sharedSessionMetadataBackendIds = await LoadSharedSessionMetadataBackendIdsAsync(backendIds, cancellationToken).ConfigureAwait(false);
        results.AddRange(await LoadSharedLocalRuntimeThreadsAsync(backendIds, projects, cancellationToken).ConfigureAwait(false));

        var sessionResults = await Task.WhenAll(
                backendIds
                    .Where(backendId => !sharedSessionMetadataBackendIds.Contains(backendId.Value))
                    .Select(LoadBackendSessionsAsync))
            .ConfigureAwait(false);

        foreach (var (backendId, sessions) in sessionResults)
        {
            if (sessions is null)
            {
                continue;
            }

            foreach (var session in sessions)
            {
                var thread = TryCreateRecoverableThread(backendId, session, projects);
                if (thread is not null)
                {
                    results.Add(new RecoverableThreadCandidate(thread, !IsProviderManagedBackend(backendId)));
                }
            }
        }

        var deduplicatedByThreadId = results
            .GroupBy(static candidate => candidate.Thread.ThreadId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        var codeAltaSessionIds = deduplicatedByThreadId
            .Where(static candidate => candidate.IsCodeAltaSession && !string.IsNullOrWhiteSpace(candidate.Thread.BackendSessionId))
            .Select(static candidate => candidate.Thread.BackendSessionId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return deduplicatedByThreadId
            .Where(candidate => candidate.IsCodeAltaSession ||
                                !IsProviderManagedBackend(new AgentBackendId(candidate.Thread.BackendId)) ||
                                !codeAltaSessionIds.Contains(candidate.Thread.BackendSessionId ?? string.Empty))
            .Select(static candidate => candidate.Thread)
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();

        async Task<(AgentBackendId BackendId, IReadOnlyList<AgentSessionMetadata>? Sessions)> LoadBackendSessionsAsync(AgentBackendId backendId)
        {
            try
            {
                var sessions = await _agentHub.ListSessionsAsync(backendId, cancellationToken: cancellationToken).ConfigureAwait(false);
                return (backendId, sessions);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (KeyNotFoundException)
            {
                return (backendId, null);
            }
            catch
            {
                return (backendId, null);
            }
        }
    }

    private async Task<IReadOnlySet<string>> LoadSharedSessionMetadataBackendIdsAsync(
        IReadOnlyList<AgentBackendId> backendIds,
        CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(backendIds.Select(LoadSharedBackendIdAsync)).ConfigureAwait(false);
        return results
            .Where(static backendId => backendId is not null)
            .Select(static backendId => backendId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        async Task<string?> LoadSharedBackendIdAsync(AgentBackendId backendId)
        {
            try
            {
                return await _agentHub.UsesSharedSessionMetadataStoreAsync(backendId, cancellationToken).ConfigureAwait(false)
                    ? backendId.Value
                    : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }
    }

    private async Task<IReadOnlyList<RecoverableThreadCandidate>> LoadSharedLocalRuntimeThreadsAsync(
        IReadOnlyList<AgentBackendId> backendIds,
        IReadOnlyList<ProjectDescriptor> projects,
        CancellationToken cancellationToken)
    {
        var loadableBackendIds = backendIds
            .Select(static backendId => backendId.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(_catalogOptions.GlobalRoot));
        var results = new List<RecoverableThreadCandidate>();
        foreach (var session in await store.ListSessionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(session.ProviderKey) || !loadableBackendIds.Contains(session.ProviderKey))
            {
                continue;
            }

            var backendId = new AgentBackendId(session.ProviderKey);
            var thread = TryCreateRecoverableThread(backendId, ToAgentSessionMetadata(session), projects);
            if (thread is not null)
            {
                results.Add(new RecoverableThreadCandidate(thread, IsCodeAltaSession: true));
            }
        }

        return results;
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
            ProviderKey = options.ProviderKey ?? options.BackendId.Value,
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
            ProviderKey = options.ProviderKey ?? options.BackendId.Value,
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
            ProviderKey = options.ProviderKey ?? options.BackendId.Value,
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
        var developerInstructions = UsesProviderManagedSkills(options.BackendId) ? null : instructions.DeveloperInstructions;
        var tools = CreateSessionTools(options, project);

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
            ProviderKey = options.ProviderKey ?? thread.ResolvedProviderKey,
            Model = options.Model ?? coordinatorProfile.DefaultModel,
            ReasoningEffort = options.ReasoningEffort ?? ParseReasoningEffort(coordinatorProfile.DefaultReasoningEffort),
            Streaming = true,
            WorkingDirectory = options.WorkingDirectory,
            ProjectRoots = options.ProjectRoots,
            SystemMessage = AppendPromptPart(instructions.SystemMessage, options.AdditionalSystemMessage),
            DeveloperInstructions = AppendPromptPart(developerInstructions, options.AdditionalDeveloperInstructions),
            Tools = tools,
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
        thread.ProviderKey = options.ProviderKey ?? options.BackendId.Value;
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
            options.ProviderKey ?? thread.ResolvedProviderKey,
            backendSessionId,
            options.WorkingDirectory,
            options.Model ?? coordinatorProfile.DefaultModel,
            options.ReasoningEffort ?? ParseReasoningEffort(coordinatorProfile.DefaultReasoningEffort),
            options.AdditionalSystemMessage,
            options.AdditionalDeveloperInstructions,
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
    /// Activates a CodeAlta-managed skill for a thread through the host-owned local runtime path.
    /// </summary>
    /// <param name="thread">Target thread.</param>
    /// <param name="options">Execution options used to resolve the backing session.</param>
    /// <param name="skillName">Skill name to activate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run identifier that received the activated skill content.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="thread"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="skillName"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the thread backend owns its native skills.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the requested skill cannot be resolved.</exception>
    public async Task<AgentRunId> ActivateSkillAsync(
        WorkThreadDescriptor thread,
        WorkThreadExecutionOptions options,
        string skillName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        if (UsesProviderManagedSkills(options.BackendId))
        {
            throw new InvalidOperationException(
                $"Backend '{options.BackendId.Value}' manages its own native skills; CodeAlta-managed skill activation is not injected into that session.");
        }

        var project = await ResolveProjectAsync(thread, cancellationToken).ConfigureAwait(false);
        var query = BuildSkillCatalogQuery(project, options.ProjectRoots);
        var activation = await _skillCatalog.ActivateAsync(query, skillName, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Skill '{skillName}' was not found or is not activatable for this thread.");

        var input = new AgentInput(
        [
            new AgentInputItem.Skill(activation.Descriptor.Name, activation.Descriptor.SkillFilePath),
            new AgentInputItem.Text(
                $"""
                The user activated the CodeAlta skill '{activation.Descriptor.Name}' for this thread.
                Treat the following host-provided skill content as active session context.

                {activation.Payload}
                """),
        ]);

        return await SendAsync(
                thread,
                options,
                new AgentSendOptions { Input = input },
                cancellationToken)
            .ConfigureAwait(false);
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

    private IReadOnlyList<AgentToolDefinition>? CreateSessionTools(
        WorkThreadExecutionOptions options,
        ProjectDescriptor? project)
    {
        if (UsesProviderManagedSkills(options.BackendId))
        {
            return options.Tools;
        }

        var skillQuery = BuildSkillCatalogQuery(project, options.ProjectRoots);
        var skillActivationTool = SkillSessionToolFactory.CreateActivateTool(_skillCatalog, skillQuery);
        return SkillSessionToolFactory.MergeWithActivationTool(options.Tools, skillActivationTool);
    }

    private static bool UsesProviderManagedSkills(AgentBackendId backendId)
        => backendId == AgentBackendIds.Codex || backendId == AgentBackendIds.Copilot;

    private static string? AppendPromptPart(string? baseText, string? additionalText)
    {
        if (string.IsNullOrWhiteSpace(additionalText))
        {
            return baseText;
        }

        if (string.IsNullOrWhiteSpace(baseText))
        {
            return additionalText.Trim();
        }

        return string.Concat(baseText.TrimEnd(), Environment.NewLine, Environment.NewLine, additionalText.Trim());
    }

    /// <summary>
    /// Detaches and disposes the active coordinator session for a thread when present.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when an active coordinator session was detached; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DetachThreadSessionAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        ThreadSessionEntry? entry = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(threadId, out entry))
            {
                _entries.Remove(threadId);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (entry is null)
        {
            return false;
        }

        await entry.DisposeAsync(_agentHub).ConfigureAwait(false);
        return true;
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
                ProviderKey = session.ProviderKey ?? backendId.Value,
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
            ProviderKey = session.ProviderKey ?? backendId.Value,
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

    private static AgentSessionMetadata ToAgentSessionMetadata(LocalAgentSessionSummary session)
    {
        var context = string.IsNullOrWhiteSpace(session.WorkingDirectory)
            ? null
            : new AgentSessionContext(session.WorkingDirectory, null, null, null);

        return new AgentSessionMetadata(
            session.SessionId,
            session.CreatedAt,
            session.UpdatedAt,
            session.Summary ?? session.Title,
            context,
            session.WorkingDirectory,
            null,
            session.ProtocolFamily,
            session.ProviderKey,
            session.ModelId);
    }

    private static bool IsProviderManagedBackend(AgentBackendId backendId)
        => string.Equals(backendId.Value, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(backendId.Value, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);

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
            roots.Add(Path.Combine(projectRoot, ".alta", "agents"));
        }

        return roots;
    }

    private SkillCatalogQuery BuildSkillCatalogQuery(ProjectDescriptor? project, IReadOnlyList<string> projectRoots)
    {
        var resolvedProjectRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(project?.ProjectPath))
        {
            resolvedProjectRoots.Add(Path.GetFullPath(project.ProjectPath));
        }

        foreach (var projectRoot in projectRoots.Where(static root => !string.IsNullOrWhiteSpace(root)))
        {
            var fullPath = Path.GetFullPath(projectRoot);
            if (!resolvedProjectRoots.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                resolvedProjectRoots.Add(fullPath);
            }
        }

        return new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext
            {
                ProjectRoots = resolvedProjectRoots,
                UserCodeAltaRoot = _catalogOptions.GlobalRoot,
                UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            },
            IncludeInvalid = true,
            IncludeShadowed = true,
            IncludeUntrusted = true,
        };
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
            string providerKey,
            string backendSessionId,
            string workingDirectory,
            string? model,
            AgentReasoningEffort? reasoningEffort,
            string? additionalSystemMessage,
            string? additionalDeveloperInstructions,
            EventProjector projector,
            IDisposable subscription)
        {
            AgentId = agentId;
            BackendId = backendId;
            ProviderKey = providerKey;
            BackendSessionId = backendSessionId;
            WorkingDirectory = workingDirectory;
            Model = model;
            ReasoningEffort = reasoningEffort;
            AdditionalSystemMessage = additionalSystemMessage;
            AdditionalDeveloperInstructions = additionalDeveloperInstructions;
            Projector = projector;
            Subscription = subscription;
        }

        public AgentId AgentId { get; }

        public AgentBackendId BackendId { get; }

        public string ProviderKey { get; }

        public string BackendSessionId { get; }

        public string WorkingDirectory { get; }

        public string? Model { get; }

        public AgentReasoningEffort? ReasoningEffort { get; }

        public string? AdditionalSystemMessage { get; }

        public string? AdditionalDeveloperInstructions { get; }

        public IDisposable Subscription { get; }

        public EventProjector Projector { get; }

        public bool IsTerminated { get; private set; }

        public bool Matches(WorkThreadExecutionOptions options, string backendSessionId)
        {
            return !IsTerminated
                && string.Equals(BackendId.Value, options.BackendId.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ProviderKey, options.ProviderKey ?? options.BackendId.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(BackendSessionId, backendSessionId, StringComparison.Ordinal)
                && string.Equals(WorkingDirectory, options.WorkingDirectory, StringComparison.Ordinal)
                && string.Equals(Model, options.Model, StringComparison.Ordinal)
                && ReasoningEffort == options.ReasoningEffort
                && string.Equals(AdditionalSystemMessage, options.AdditionalSystemMessage, StringComparison.Ordinal)
                && string.Equals(AdditionalDeveloperInstructions, options.AdditionalDeveloperInstructions, StringComparison.Ordinal);
        }

        public void MarkTerminated()
            => IsTerminated = true;

        public async Task DisposeAsync(AgentHub hub)
        {
            Subscription.Dispose();
            await hub.StopSessionAsync(AgentId).ConfigureAwait(false);
        }
    }

    private sealed record RecoverableThreadCandidate(WorkThreadDescriptor Thread, bool IsCodeAltaSession);

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
