using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime.Actors;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Owns per-thread coordinator sessions, recovers backend-owned project/global threads, and projects sanitized runtime events.
/// </summary>
public sealed class WorkThreadRuntimeService : IAsyncDisposable
{
    private static readonly Regex ScheduleBlockRegex = new(
        @"```codealta_schedule\s*\n.*?```",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParentNotificationBlockRegex = new(
        @"<notify-parent(?:\s+kind=""(?<kind>[^""]+)"")?\s*>(?<body>.*?)</notify-parent>",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly AgentHub _agentHub;
    private readonly ProjectCatalog _projectCatalog;
    private readonly WorkThreadCatalog _threadCatalog;
    private readonly AgentInstructionTemplateProvider _instructionTemplateProvider;
    private readonly CatalogOptions _catalogOptions;
    private readonly SkillCatalog _skillCatalog;
    private readonly BoundedRuntimeEventStream<WorkThreadRuntimeEvent> _events = new();
    private readonly WorkThreadActorRegistry _threadActors = new(mailboxCapacity: 128);
    private readonly ConcurrentDictionary<string, ThreadSessionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkThreadRuntimeService"/> class.
    /// </summary>
    public WorkThreadRuntimeService(
        AgentHub agentHub,
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        AgentInstructionTemplateProvider instructionTemplateProvider,
        CatalogOptions catalogOptions,
        SkillCatalog? skillCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(instructionTemplateProvider);
        ArgumentNullException.ThrowIfNull(catalogOptions);

        _agentHub = agentHub;
        _projectCatalog = projectCatalog;
        _threadCatalog = threadCatalog;
        _instructionTemplateProvider = instructionTemplateProvider;
        _catalogOptions = catalogOptions;
        _skillCatalog = skillCatalog ?? new SkillCatalog();
    }

    /// <summary>
    /// Gets the skill catalog used when building instructions and activating skills.
    /// </summary>
    public SkillCatalog SkillCatalog => _skillCatalog;

    /// <summary>
    /// Streams sanitized runtime events across all active threads.
    /// </summary>
    public IAsyncEnumerable<WorkThreadRuntimeEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
        => _events.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Gets the approximate number of runtime events dropped because event consumers fell behind.
    /// </summary>
    public long DroppedRuntimeEventCount => _events.DroppedCount;

    /// <summary>
    /// Gets an active thread descriptor from the runtime's in-memory coordinator session table.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active in-memory thread descriptor when present; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="threadId" /> is empty.</exception>
    public async Task<WorkThreadDescriptor?> TryGetActiveThreadDescriptorAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        return _entries.TryGetValue(threadId, out var entry) && !entry.IsTerminated
            ? entry.ToDescriptor()
            : null;
    }

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
        var results = new List<RecoverableThreadCandidate>();

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
            .Select(static group => group.OrderByDescending(static candidate => candidate.IsCodeAltaSession).First())
            .ToArray();

        var threads = deduplicatedByThreadId
            .Select(static candidate => candidate.Thread)
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();

        await ApplyPersistedThreadLocalStateAsync(threads, cancellationToken).ConfigureAwait(false);
        return threads;

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

    private async Task ApplyPersistedThreadLocalStateAsync(
        IReadOnlyList<WorkThreadDescriptor> threads,
        CancellationToken cancellationToken)
    {
        foreach (var thread in threads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkThreadLocalState? localState;
            try
            {
                localState = await _threadCatalog.JournalStore
                    .ReadLatestStateAsync(thread.ThreadId, thread.CreatedAt, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (localState is null)
            {
                continue;
            }

            ApplyPersistedThreadLocalState(thread, localState);
        }
    }

    private static void ApplyPersistedThreadLocalState(WorkThreadDescriptor thread, WorkThreadLocalState localState)
    {
        if (localState.Archived)
        {
            thread.Status = WorkThreadStatus.Archived;
        }

        if (!string.IsNullOrWhiteSpace(localState.ProviderKey))
        {
            var providerKey = localState.ProviderKey.Trim();
            thread.ProviderKey = providerKey;
            thread.BackendId = providerKey;
        }

        if (!string.IsNullOrWhiteSpace(localState.ModelId))
        {
            thread.ModelId = localState.ModelId;
        }

        if (localState.ReasoningEffort is { } reasoningEffort)
        {
            thread.ReasoningEffort = reasoningEffort;
        }

        if (localState.MessageCount is { } messageCount)
        {
            thread.MessageCount = messageCount;
        }

        if (!string.IsNullOrWhiteSpace(localState.ParentThreadId))
        {
            thread.ParentThreadId = localState.ParentThreadId;
        }

        if (localState.CreatedBy is not null)
        {
            thread.CreatedBy = localState.CreatedBy;
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
        if (!string.IsNullOrWhiteSpace(thread.ThreadId))
        {
            try
            {
                deletedByBackend = await _agentHub.DeleteSessionAsync(
                        new AgentBackendId(thread.BackendId),
                        thread.ThreadId,
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
    /// Reads persisted CodeAlta-owned local-runtime history for a recoverable thread without resuming the session.
    /// </summary>
    /// <param name="thread">The thread descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored events when available; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="thread" /> is <see langword="null" />.</exception>
    public async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryAsync(
        WorkThreadDescriptor thread,
        CancellationToken cancellationToken = default)
        => await TryReadStoredHistoryAsync(thread, onUnavailable: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads persisted CodeAlta-owned local-runtime history for a recoverable thread without resuming the session.
    /// </summary>
    /// <param name="thread">The thread descriptor.</param>
    /// <param name="onUnavailable">Optional callback invoked when a local history file exists but cannot be read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored events when available; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="thread" /> is <see langword="null" />.</exception>
    public async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryAsync(
        WorkThreadDescriptor thread,
        Action<Exception>? onUnavailable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (string.IsNullOrWhiteSpace(thread.ThreadId))
        {
            return null;
        }

        try
        {
            var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(_catalogOptions.GlobalRoot));
            return await store.ReadEventsAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            onUnavailable?.Invoke(ex);
            return null;
        }
    }

    /// <summary>
    /// Creates a new global thread session and returns its descriptor.
    /// </summary>
    public async Task<WorkThreadDescriptor> CreateGlobalThreadAsync(
        WorkThreadExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
        => await CreateGlobalThreadAsync(options, title, parentThreadId: null, createdBy: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new global thread session with optional durable lineage and returns its descriptor.
    /// </summary>
    public async Task<WorkThreadDescriptor> CreateGlobalThreadAsync(
        WorkThreadExecutionOptions options,
        string? title,
        string? parentThreadId,
        AltaActorProvenance? createdBy,
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
            WorkingDirectory = options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(title) ? "Global Thread" : title.Trim(),
            Status = WorkThreadStatus.Draft,
            ParentThreadId = NormalizeOptionalText(parentThreadId),
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = "Global overview and coordination thread.",
            ModelId = options.Model,
            ReasoningEffort = options.ReasoningEffort,
        };

        try
        {
            await EnsureCoordinatorSessionAsync(thread, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishRuntimeFailureEvent(thread, ex);
            throw;
        }

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
        => await CreateProjectThreadAsync(project, options, title, parentThreadId: null, createdBy: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new project thread session with optional durable lineage and returns its descriptor.
    /// </summary>
    public async Task<WorkThreadDescriptor> CreateProjectThreadAsync(
        ProjectDescriptor project,
        WorkThreadExecutionOptions options,
        string? title,
        string? parentThreadId,
        AltaActorProvenance? createdBy,
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
            ProjectRef = project.Id,
            WorkingDirectory = options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(title) ? project.DisplayName : title.Trim(),
            Status = WorkThreadStatus.Draft,
            ParentThreadId = NormalizeOptionalText(parentThreadId),
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = $"Project thread for {project.DisplayName}.",
            ModelId = options.Model,
            ReasoningEffort = options.ReasoningEffort,
        };

        try
        {
            await EnsureCoordinatorSessionAsync(thread, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishRuntimeFailureEvent(thread, ex);
            throw;
        }

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
        if (string.IsNullOrWhiteSpace(thread.ThreadId))
        {
            return await EnsureCoordinatorSessionCoreAsync(thread, options, cancellationToken).ConfigureAwait(false);
        }

        var actor = _threadActors.GetOrCreate(thread.ThreadId);
        return await actor.QueryAsync(
                actorCancellationToken => EnsureCoordinatorSessionCoreAsync(thread, options, actorCancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<AgentId> EnsureCoordinatorSessionCoreAsync(
        WorkThreadDescriptor thread,
        WorkThreadExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);

        var project = await ResolveProjectAsync(thread, cancellationToken).ConfigureAwait(false);
        var instructions = _instructionTemplateProvider.BuildCoordinatorInstructions(thread, project);
        var developerInstructions = UsesProviderManagedSkills(options.BackendId) ? null : instructions.DeveloperInstructions;
        var additionalDeveloperInstructions = AppendPromptPart(BuildParentNotificationGuidance(thread), options.AdditionalDeveloperInstructions);
        var tools = options.Tools;

        ThreadSessionEntry? previousEntry = null;
        AgentId agentId;

        if (!string.IsNullOrWhiteSpace(thread.ThreadId) &&
            _entries.TryGetValue(thread.ThreadId, out var existing) &&
            existing.Matches(options))
        {
            return existing.AgentId;
        }

        if (!string.IsNullOrWhiteSpace(thread.ThreadId))
        {
            _entries.TryRemove(thread.ThreadId, out previousEntry);
        }

        if (previousEntry is not null)
        {
            await previousEntry.DisposeAsync(_agentHub).ConfigureAwait(false);
        }

        var identity = await _agentHub.RegisterAgentAsync(
                options.BackendId,
                cancellationToken)
            .ConfigureAwait(false);
        agentId = identity.AgentId;

        var previousThreadId = string.IsNullOrWhiteSpace(thread.ThreadId) ? null : thread.ThreadId;
        var replacingDraftSession = previousThreadId is not null && ShouldReplaceDraftSession(thread, options.BackendId);
        if (previousThreadId is null && CanCreateSessionWithRequestedThreadId(options.BackendId))
        {
            thread.ThreadId = Guid.CreateVersion7().ToString();
        }

        var requestedThreadId = replacingDraftSession
            ? null
            : NormalizeOptionalText(thread.ThreadId);

        var sessionOptions = new AgentSessionResumeOptions
        {
            ThreadId = requestedThreadId,
            Title = NormalizeOptionalText(thread.Title),
            ProviderKey = options.ProviderKey ?? thread.ResolvedProviderKey,
            Model = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            Streaming = true,
            WorkingDirectory = options.WorkingDirectory,
            ProjectRoots = options.ProjectRoots,
            SystemMessage = AppendPromptPart(instructions.SystemMessage, options.AdditionalSystemMessage),
            DeveloperInstructions = AppendPromptPart(developerInstructions, additionalDeveloperInstructions),
            Tools = tools,
            OnPermissionRequest = options.OnPermissionRequest,
            OnUserInputRequest = options.OnUserInputRequest,
        };

        var startNewSession = previousThreadId is null || ShouldReplaceDraftSession(thread, options.BackendId);
        if (startNewSession)
        {
            var createdSessionId = await _agentHub.StartSessionAsync(agentId, sessionOptions, cancellationToken).ConfigureAwait(false);
            thread.ThreadId = string.IsNullOrWhiteSpace(createdSessionId)
                ? previousThreadId ?? thread.ThreadId
                : createdSessionId;
        }
        else
        {
            var resumedSessionId = await _agentHub.ResumeSessionAsync(agentId, thread.ThreadId, sessionOptions, cancellationToken).ConfigureAwait(false);
            if (previousThreadId is null)
            {
                thread.ThreadId = resumedSessionId;
            }
        }

        thread.BackendId = options.BackendId.Value;
        thread.ProviderKey = options.ProviderKey ?? options.BackendId.Value;
        thread.WorkingDirectory = options.WorkingDirectory;
        PublishThreadCatalogEvent(thread);
        PublishSessionLifecycleEvent(thread.ThreadId, previousThreadId);

        ThreadSessionEntry? entry = null;
        var actor = _threadActors.GetOrCreate(thread.ThreadId);
        var projector = new EventProjector(
            thread.ThreadId,
            runtimeEvent => _events.TryPublish(runtimeEvent),
            @event => entry?.ObserveEvent(@event));
        var subscription = await _agentHub.SubscribeSessionEventsAsync(
                agentId,
                @event => _ = PostAgentEventToActorAsync(actor, thread.ThreadId, projector, @event),
                cancellationToken)
            .ConfigureAwait(false);

        entry = new ThreadSessionEntry(
            thread.ThreadId,
            agentId,
            thread.Kind,
            thread.Status,
            options.BackendId,
            options.ProviderKey ?? thread.ResolvedProviderKey,
            thread.ProjectRef,
            thread.ParentThreadId,
            thread.CreatedBy,
            thread.CreatedAt,
            thread.Title,
            options.WorkingDirectory,
            options.Model,
            options.ReasoningEffort,
            options.AdditionalSystemMessage,
            options.AdditionalDeveloperInstructions,
            projector,
            subscription);

        _entries[thread.ThreadId] = entry;

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

        try
        {
            var threadStateUpdated = false;
            var agentId = await _threadActors.GetOrCreate(thread.ThreadId).QueryAsync(
                    async actorCancellationToken =>
                    {
                        var ensuredAgentId = await EnsureCoordinatorSessionCoreAsync(thread, options, actorCancellationToken).ConfigureAwait(false);
                        thread.MarkStarted(DateTimeOffset.UtcNow);
                        threadStateUpdated = true;

                        return ensuredAgentId;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (threadStateUpdated)
            {
                PublishThreadCatalogEvent(thread);
            }

            var runStartedAt = DateTimeOffset.UtcNow;
            var runId = await _agentHub.RunAsync(agentId, sendOptions, cancellationToken).ConfigureAwait(false);
            if (await MarkActiveRunIfStillInFlightAsync(thread.ThreadId, runId, runStartedAt, cancellationToken).ConfigureAwait(false))
            {
                PublishRunSubmittedEvent(thread.ThreadId, runId, runStartedAt);
            }

            return runId;
        }
        catch (OperationCanceledException)
        {
            var activeRunId = await ClearActiveRunAsync(thread.ThreadId, CancellationToken.None).ConfigureAwait(false);
            PublishRunFinishedEvent(
                thread.ThreadId,
                activeRunId,
                WorkThreadLifecycleEventKind.RunAborted,
                "Runtime run cancelled.",
                DateTimeOffset.UtcNow);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ClearActiveRunAsync(thread.ThreadId, CancellationToken.None).ConfigureAwait(false);
            PublishRuntimeFailureEvent(thread, ex);
            throw;
        }
    }

    /// <summary>
    /// Persists a headless prompt queue item for later submission by the owning runtime/front-end queue drain path.
    /// </summary>
    /// <param name="thread">Target thread.</param>
    /// <param name="prompt">Prompt text to submit later.</param>
    /// <param name="kind">Prompt dispatch kind, such as <c>send</c>, <c>message</c>, or <c>request</c>.</param>
    /// <param name="submittedBy">Durable caller attribution for the queueing actor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted queue item.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="thread"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="prompt"/> or <paramref name="kind"/> is empty.</exception>
    public async Task<WorkThreadQueuedPrompt> QueuePromptAsync(
        WorkThreadDescriptor thread,
        string prompt,
        string kind,
        AltaActorProvenance? submittedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var actor = _threadActors.GetOrCreate(thread.ThreadId);
        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    var item = new WorkThreadQueuedPrompt
                    {
                        QueueItemId = "queue-" + Guid.NewGuid().ToString("N"),
                        Kind = kind,
                        Prompt = prompt,
                        PromptPreview = CreatePromptPreview(prompt),
                        State = "queued",
                        SubmittedBy = submittedBy,
                        CreatedAt = timestamp,
                    };

                    var localState = await ReadLatestLocalStateAsync(thread.ThreadId, thread.CreatedAt, actorCancellationToken).ConfigureAwait(false) ?? new WorkThreadLocalState();
                    CopyThreadMetadata(thread, localState);
                    localState.QueuedPrompts ??= [];
                    localState.QueuedPrompts.Add(item);
                    localState.PromptProvenance ??= [];
                    localState.PromptProvenance.Add(new WorkThreadPromptProvenance
                    {
                        PromptId = item.QueueItemId,
                        Kind = kind,
                        Queued = true,
                        PromptPreview = item.PromptPreview,
                        SubmittedBy = submittedBy,
                        CreatedAt = timestamp,
                    });

                    TrimLocalStateHistory(localState);
                    await _threadCatalog.JournalStore.AppendStateAsync(thread, localState, actorCancellationToken).ConfigureAwait(false);
                    _events.TryPublish(new WorkThreadQueueRuntimeEvent(
                        thread.ThreadId,
                        timestamp,
                        QueuedPromptCount(localState),
                        item.QueueItemId,
                        item.PromptPreview,
                        IsEnqueued: true));
                    return item;
                },
                cancellationToken)
            .ConfigureAwait(false);
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

        var agentId = await _threadActors.GetOrCreate(thread.ThreadId).QueryAsync(
                async actorCancellationToken =>
                {
                    var entry = await GetActiveCoordinatorSessionForSteeringAsync(thread, options, actorCancellationToken).ConfigureAwait(false);
                    return entry.AgentId;
                },
                cancellationToken)
            .ConfigureAwait(false);

        return await _agentHub.SteerAsync(agentId, steerOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether the thread's active coordinator session has an in-flight run.
    /// </summary>
    public async Task<bool> HasActiveRunAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (string.IsNullOrWhiteSpace(thread.ThreadId))
        {
            return false;
        }

        if (!_threadActors.TryGet(thread.ThreadId, out var actor))
        {
            return false;
        }

        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    return _entries.TryGetValue(thread.ThreadId, out var entry) && entry.HasActiveRun;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether the thread has an active coordinator session in this runtime process.
    /// </summary>
    /// <param name="threadId">The durable thread identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when this runtime owns a non-terminated coordinator session.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="threadId"/> is empty.</exception>
    public async Task<bool> HasActiveCoordinatorSessionAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (!_threadActors.TryGet(threadId, out var actor))
        {
            return false;
        }

        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    return _entries.TryGetValue(threadId, out var entry) && !entry.IsTerminated;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Aborts active work in the thread coordinator session.
    /// </summary>
    public async Task AbortAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (_disposed)
        {
            return;
        }

        WorkThreadActorCommandResult result;
        try
        {
            var actor = _threadActors.GetOrCreate(threadId);
            result = await actor.ExecuteReservedAsync(
                    async actorCancellationToken =>
                    {
                        var entry = await GetEntryAsync(threadId, actorCancellationToken).ConfigureAwait(false);
                        await _agentHub.AbortAsync(entry.AgentId, actorCancellationToken).ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (_disposed && ex is ObjectDisposedException or ChannelClosedException)
        {
            return;
        }

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                result.Message ?? $"Failed to abort thread '{threadId}'.",
                result.Exception);
        }
    }

    private static bool UsesProviderManagedSkills(AgentBackendId backendId)
        => backendId == AgentBackendIds.Codex || backendId == AgentBackendIds.Copilot;

    private static string? BuildParentNotificationGuidance(WorkThreadDescriptor thread)
        => string.IsNullOrWhiteSpace(thread.ParentThreadId)
            ? null
            : $"Parent thread: `{thread.ParentThreadId}`. CodeAlta auto-forwards your final assistant reply. For progress/intermediate parent updates, include `<notify-parent>update text</notify-parent>` in an assistant reply.";

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

    private async Task<WorkThreadLocalState?> ReadLatestLocalStateAsync(
        string threadId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _threadCatalog.JournalStore.ReadLatestStateAsync(threadId, createdAt, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void CopyThreadMetadata(WorkThreadDescriptor thread, WorkThreadLocalState localState)
    {
        localState.ProviderKey = thread.ResolvedProviderKey;
        localState.ModelId = thread.ModelId;
        localState.ReasoningEffort = thread.ReasoningEffort;
        localState.Archived = thread.Status == WorkThreadStatus.Archived;
        localState.MessageCount = thread.MessageCount;
        localState.ParentThreadId = thread.ParentThreadId;
        localState.CreatedBy = thread.CreatedBy;
    }

    private static int QueuedPromptCount(WorkThreadLocalState localState)
        => localState.QueuedPrompts.Count(static prompt => IsPendingQueuedPromptState(prompt.State));

    private static bool IsPendingQueuedPromptState(string? state)
        => string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(state, "submitting", StringComparison.OrdinalIgnoreCase);

    private static string CreatePromptPreview(string prompt)
        => prompt.Length <= 160 ? prompt : prompt[..160];

    private static void TrimLocalStateHistory(WorkThreadLocalState localState)
    {
        const int MaxPromptProvenanceRecords = 200;
        if (localState.PromptProvenance.Count > MaxPromptProvenanceRecords)
        {
            localState.PromptProvenance.RemoveRange(0, localState.PromptProvenance.Count - MaxPromptProvenanceRecords);
        }

        const int MaxQueuedPromptRecords = 200;
        if (localState.QueuedPrompts.Count > MaxQueuedPromptRecords)
        {
            localState.QueuedPrompts.RemoveRange(0, localState.QueuedPrompts.Count - MaxQueuedPromptRecords);
        }
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

    /// <summary>
    /// Detaches and disposes the active coordinator session for a thread when present.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when an active coordinator session was detached; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DetachThreadSessionAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var actor = _threadActors.GetOrCreate(threadId);
        var detached = await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    _entries.TryRemove(threadId, out var entry);

                    if (entry is null)
                    {
                        return false;
                    }

                    await entry.DisposeAsync(_agentHub).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (detached)
        {
            await _threadActors.RemoveAsync(threadId, cancelPending: false).ConfigureAwait(false);
        }

        return detached;
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

        var result = await _threadActors.GetOrCreate(thread.ThreadId).ExecuteAsync(
                async actorCancellationToken =>
                {
                    _events.TryPublish(new WorkThreadHostEvent(
                        thread.ThreadId,
                        DateTimeOffset.UtcNow,
                        AgentSessionUpdateKind.CompactionStarted,
                        $"Manual compaction requested for '{thread.Title}'."));

                    var agentId = await EnsureCoordinatorSessionCoreAsync(thread, options, actorCancellationToken).ConfigureAwait(false);
                    var outcome = await _agentHub.CompactAsync(agentId, actorCancellationToken).ConfigureAwait(false);
                    if (outcome is not null)
                    {
                        _events.TryPublish(new WorkThreadHostEvent(
                            thread.ThreadId,
                            DateTimeOffset.UtcNow,
                            AgentSessionUpdateKind.CompactionCompleted,
                            outcome.Message ?? (outcome.Success ? "Manual compaction completed." : "Manual compaction failed.")));
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                result.Message ?? $"Failed to compact thread '{thread.ThreadId}'.",
                result.Exception);
        }
    }

    /// <summary>
    /// Gets sanitized history for an active thread session.
    /// </summary>
    public async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(string threadId, CancellationToken cancellationToken = default)
    {
        var actor = _threadActors.GetOrCreate(threadId);
        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var entry = await GetEntryAsync(threadId, actorCancellationToken).ConfigureAwait(false);
                    var history = await _agentHub.GetSessionHistoryAsync(entry.AgentId, actorCancellationToken).ConfigureAwait(false);
                    return entry.Projector.ProjectHistory(history);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _events.Complete();
        await _threadActors.DisposeAsync().ConfigureAwait(false);

        foreach (var entry in _entries.Values)
        {
            await entry.DisposeAsync(_agentHub).ConfigureAwait(false);
        }

        _entries.Clear();
    }

    private static bool CanCreateSessionWithRequestedThreadId(AgentBackendId backendId)
        => !string.Equals(backendId.Value, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase);

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

        await Task.CompletedTask.ConfigureAwait(false);
        if (!_entries.TryGetValue(threadId, out var entry))
        {
            throw new InvalidOperationException($"Thread '{threadId}' does not have an active coordinator session.");
        }

        return entry;
    }

    private async Task<ThreadSessionEntry> GetActiveCoordinatorSessionForSteeringAsync(
        WorkThreadDescriptor thread,
        WorkThreadExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(thread.ThreadId))
        {
            throw new InvalidOperationException("Cannot steer a thread without an active coordinator session.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        if (!_entries.TryGetValue(thread.ThreadId, out var entry) || entry.IsTerminated)
        {
            throw new InvalidOperationException(
                $"Thread '{thread.ThreadId}' does not have an active coordinator session to steer.");
        }

        if (!string.Equals(entry.BackendId.Value, options.BackendId.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Thread '{thread.ThreadId}' active coordinator session does not match the requested provider.");
        }

        if (!entry.HasActiveRun)
        {
            throw new InvalidOperationException(
                $"Thread '{thread.ThreadId}' does not have an active coordinator run to steer.");
        }

        return entry;
    }

    private async Task<bool> MarkActiveRunIfStillInFlightAsync(
        string threadId,
        AgentRunId runId,
        DateTimeOffset runStartedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        if (!_threadActors.TryGet(threadId, out var actor))
        {
            return false;
        }

        try
        {
            return await actor.QueryAsync(
                    _ =>
                    {
                        if (_entries.TryGetValue(threadId, out var entry))
                        {
                            return ValueTask.FromResult(entry.MarkActiveRunIfStillInFlight(runId, runStartedAt));
                        }

                        return ValueTask.FromResult(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException) when (_disposed)
        {
            return false;
        }
    }

    private async Task<AgentRunId?> ClearActiveRunAsync(string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId) || !_threadActors.TryGet(threadId, out var actor))
        {
            return null;
        }

        try
        {
            return await actor.QueryAsync(
                    _ =>
                    {
                        if (_entries.TryGetValue(threadId, out var entry))
                        {
                            return ValueTask.FromResult(entry.ClearActiveRun());
                        }

                        return ValueTask.FromResult<AgentRunId?>(null);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException) when (_disposed)
        {
            return null;
        }
    }

    private async Task PostAgentEventToActorAsync(
        WorkThreadActor actor,
        string threadId,
        EventProjector projector,
        AgentEvent @event)
    {
        try
        {
            var parentNotifications = await actor.QueryAsync(_ =>
                {
                    var sanitized = projector.Project(@event);
                    var notifications = _entries.TryGetValue(threadId, out var entry)
                        ? entry.TakeParentNotifications(sanitized)
                        : Array.Empty<ParentNotificationWork>();
                    return ValueTask.FromResult(notifications);
                })
                .ConfigureAwait(false);

            foreach (var notification in parentNotifications)
            {
                await DeliverParentNotificationAsync(notification).ConfigureAwait(false);
            }

            if (IsQueueDrainTrigger(@event))
            {
                await TryDrainNextQueuedPromptAsync(threadId).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsQueueDrainTrigger(AgentEvent @event)
        => @event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }
            or AgentErrorEvent;

    private async Task TryDrainNextQueuedPromptAsync(string threadId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        var work = await TryMarkNextQueuedPromptSubmittingAsync(threadId).ConfigureAwait(false);
        if (work is null)
        {
            return;
        }

        try
        {
            var runStartedAt = DateTimeOffset.UtcNow;
            var runId = await _agentHub.RunAsync(
                    work.AgentId,
                    new AgentSendOptions { Input = AgentInput.Text(work.Prompt.Prompt) },
                    CancellationToken.None)
                .ConfigureAwait(false);
            await MarkQueuedPromptSubmittedAsync(threadId, work.Prompt.QueueItemId, runId, runStartedAt, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_disposed && ex is OperationCanceledException)
            {
                return;
            }

            await MarkQueuedPromptFailedAsync(threadId, work.Prompt.QueueItemId, ex.Message, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
    }

    private async Task<QueuedPromptDrainWork?> TryMarkNextQueuedPromptSubmittingAsync(string threadId)
    {
        var actor = _threadActors.GetOrCreate(threadId);
        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    if (!_entries.TryGetValue(threadId, out var entry) || entry.IsTerminated || entry.HasActiveRun || entry.QueueDrainInProgress)
                    {
                        return null;
                    }

                    var localState = await ReadLatestLocalStateAsync(threadId, entry.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                    if (localState is null || localState.QueuedPrompts.Count == 0)
                    {
                        return null;
                    }

                    var item = localState.QueuedPrompts.FirstOrDefault(static prompt => string.Equals(prompt.State, "queued", StringComparison.OrdinalIgnoreCase));
                    if (item is null)
                    {
                        return null;
                    }

                    var timestamp = DateTimeOffset.UtcNow;
                    item.State = "submitting";
                    item.DrainedAt = timestamp;
                    item.LastError = null;
                    await _threadCatalog.JournalStore.AppendStateAsync(entry.ToDescriptor(), localState, actorCancellationToken).ConfigureAwait(false);
                    entry.BeginQueueDrain();
                    PublishQueueChanged(threadId, localState, item, timestamp, isEnqueued: false);
                    return new QueuedPromptDrainWork(entry.AgentId, CloneQueuedPrompt(item));
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task MarkQueuedPromptSubmittedAsync(
        string threadId,
        string queueItemId,
        AgentRunId runId,
        DateTimeOffset runStartedAt,
        DateTimeOffset timestamp)
        => await UpdateQueuedPromptStateAsync(
                threadId,
                queueItemId,
                timestamp,
                item =>
                {
                    item.State = "submitted";
                    item.RunId = runId.Value;
                    item.DrainedAt = timestamp;
                    item.LastError = null;
                },
                provenance => provenance.RunId = runId.Value,
                entry =>
                {
                    entry.CompleteQueueDrain();
                    entry.MarkActiveRunIfStillInFlight(runId, runStartedAt);
                })
            .ConfigureAwait(false);

    private async Task MarkQueuedPromptFailedAsync(string threadId, string queueItemId, string error, DateTimeOffset timestamp)
        => await UpdateQueuedPromptStateAsync(
                threadId,
                queueItemId,
                timestamp,
                item =>
                {
                    item.State = "failed";
                    item.DrainedAt = timestamp;
                    item.LastError = error;
                },
                updateProvenance: null,
                entry => entry.CompleteQueueDrain())
            .ConfigureAwait(false);

    private async Task UpdateQueuedPromptStateAsync(
        string threadId,
        string queueItemId,
        DateTimeOffset timestamp,
        Action<WorkThreadQueuedPrompt> updateItem,
        Action<WorkThreadPromptProvenance>? updateProvenance,
        Action<ThreadSessionEntry>? updateEntry)
    {
        var actor = _threadActors.GetOrCreate(threadId);
        await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    try
                    {
                        if (!_entries.TryGetValue(threadId, out var currentEntry))
                        {
                            return false;
                        }

                        var localState = await ReadLatestLocalStateAsync(threadId, currentEntry.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                        if (localState is null)
                        {
                            return false;
                        }

                        var item = localState.QueuedPrompts.FirstOrDefault(prompt => string.Equals(prompt.QueueItemId, queueItemId, StringComparison.Ordinal));
                        if (item is null)
                        {
                            return false;
                        }

                        updateItem(item);
                        if (updateProvenance is not null)
                        {
                            var provenance = localState.PromptProvenance.FirstOrDefault(prompt => string.Equals(prompt.PromptId, queueItemId, StringComparison.Ordinal));
                            if (provenance is not null)
                            {
                                updateProvenance(provenance);
                            }
                        }

                        await _threadCatalog.JournalStore.AppendStateAsync(currentEntry.ToDescriptor(), localState, actorCancellationToken).ConfigureAwait(false);
                        PublishQueueChanged(threadId, localState, item, timestamp, isEnqueued: false);
                        return true;
                    }
                    finally
                    {
                        if (updateEntry is not null && _entries.TryGetValue(threadId, out var entry))
                        {
                            updateEntry(entry);
                        }
                    }
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void PublishQueueChanged(string threadId, WorkThreadLocalState localState, WorkThreadQueuedPrompt item, DateTimeOffset timestamp, bool isEnqueued)
    {
        _events.TryPublish(new WorkThreadQueueRuntimeEvent(
            threadId,
            timestamp,
            QueuedPromptCount(localState),
            item.QueueItemId,
            item.PromptPreview,
            isEnqueued));
    }

    private static WorkThreadQueuedPrompt CloneQueuedPrompt(WorkThreadQueuedPrompt item)
        => new()
        {
            QueueItemId = item.QueueItemId,
            Kind = item.Kind,
            Prompt = item.Prompt,
            PromptPreview = item.PromptPreview,
            State = item.State,
            RunId = item.RunId,
            SubmittedBy = item.SubmittedBy,
            CreatedAt = item.CreatedAt,
            DrainedAt = item.DrainedAt,
            LastError = item.LastError,
        };

    private static IReadOnlyList<ParentNotificationPayload> ExtractParentNotificationBlocks(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var matches = ParentNotificationBlockRegex.Matches(content);
        if (matches.Count == 0)
        {
            return [];
        }

        var results = new List<ParentNotificationPayload>(matches.Count);
        foreach (Match match in matches)
        {
            var body = match.Groups["body"].Value.Trim();
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            results.Add(new ParentNotificationPayload(NormalizeParentNotificationKind(match.Groups["kind"].Value), body));
        }

        return results;
    }

    private static string StripParentNotificationBlocks(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return ParentNotificationBlockRegex.Replace(content, string.Empty).Trim();
    }

    private static string NormalizeParentNotificationKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "note" or "progress" or "result" or "handoff" => normalized,
            _ => "progress",
        };
    }

    private async Task DeliverParentNotificationAsync(ParentNotificationWork notification)
    {
        try
        {
            var parent = await TryResolveThreadForParentDeliveryAsync(notification.ParentThreadId, CancellationToken.None).ConfigureAwait(false);
            if (parent is null)
            {
                PublishParentNotificationWarning(notification.SourceThreadId, $"Parent session '{notification.ParentThreadId}' was not found for automatic child-session notification.");
                return;
            }

            var prompt = BuildParentPeerAgentMessage(parent, notification);
            var submittedBy = new AltaActorProvenance
            {
                Kind = "agent",
                SourceThreadId = notification.SourceThreadId,
                SourceProjectId = notification.SourceProjectId,
                SourceAgentId = notification.SourceAgentId,
                CorrelationId = notification.CorrelationId,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            if (await HasActiveRunAsync(parent, CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    var runId = await SteerAsync(
                            parent,
                            CreateParentDeliveryExecutionOptions(parent),
                            new AgentSteerOptions { Input = AgentInput.Text(prompt) },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await PersistPromptProvenanceAsync(parent, runId.Value, queued: false, "parent-notify", prompt, submittedBy, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    PublishParentNotificationWarning(notification.SourceThreadId, $"Parent session '{notification.ParentThreadId}' could not be steered; queued the child-session notification instead. {ex.Message}");
                }
            }

            await QueuePromptAsync(parent, prompt, "parent-notify", submittedBy, CancellationToken.None).ConfigureAwait(false);
            await TryDrainNextQueuedPromptAsync(parent.ThreadId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || _disposed)
        {
            PublishParentNotificationWarning(notification.SourceThreadId, $"Automatic parent notification failed without affecting the child session: {ex.Message}");
        }
    }

    private async Task<WorkThreadDescriptor?> TryResolveThreadForParentDeliveryAsync(string threadId, CancellationToken cancellationToken)
    {
        WorkThreadDescriptor? thread = null;
        try
        {
            var threads = await ListRecoverableThreadsAsync(cancellationToken).ConfigureAwait(false);
            thread = threads.FirstOrDefault(candidate => string.Equals(candidate.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
        }

        if (thread is null && _entries.TryGetValue(threadId, out var entry))
        {
            var now = DateTimeOffset.UtcNow;
            thread = new WorkThreadDescriptor
            {
                ThreadId = threadId,
                Kind = WorkThreadKind.ProjectThread,
                BackendId = entry.BackendId.Value,
                ProviderKey = entry.ProviderKey,
                ProjectRef = entry.ProjectId,
                ParentThreadId = entry.ParentThreadId,
                WorkingDirectory = entry.WorkingDirectory,
                Title = threadId,
                Status = WorkThreadStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                LastActiveAt = now,
                StartedAt = now,
            };
        }

        if (thread is not null)
        {
            await ApplyLocalThreadStateAsync(thread, cancellationToken).ConfigureAwait(false);
        }

        return thread;
    }

    private async Task ApplyLocalThreadStateAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken)
    {
        try
        {
            var localState = await _threadCatalog.JournalStore
                .ReadLatestStateAsync(thread.ThreadId, thread.CreatedAt, cancellationToken)
                .ConfigureAwait(false);
            if (localState is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(localState.ParentThreadId))
            {
                thread.ParentThreadId = localState.ParentThreadId;
            }

            if (localState.CreatedBy is not null)
            {
                thread.CreatedBy = localState.CreatedBy;
            }

            if (localState.Archived)
            {
                thread.Status = WorkThreadStatus.Archived;
            }

            if (localState.MessageCount is not null)
            {
                thread.MessageCount = localState.MessageCount;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
        }
    }

    private static WorkThreadExecutionOptions CreateParentDeliveryExecutionOptions(WorkThreadDescriptor parent)
        => new()
        {
            BackendId = new AgentBackendId(parent.BackendId),
            ProviderKey = parent.ResolvedProviderKey,
            WorkingDirectory = parent.WorkingDirectory,
            ProjectRoots = [],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal))),
        };

    private async Task PersistPromptProvenanceAsync(
        WorkThreadDescriptor thread,
        string? runId,
        bool queued,
        string kind,
        string prompt,
        AltaActorProvenance submittedBy,
        CancellationToken cancellationToken)
    {
        var actor = _threadActors.GetOrCreate(thread.ThreadId);
        await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    var localState = await ReadLatestLocalStateAsync(thread.ThreadId, thread.CreatedAt, actorCancellationToken).ConfigureAwait(false) ?? new WorkThreadLocalState();
                    CopyThreadMetadata(thread, localState);
                    localState.PromptProvenance ??= [];
                    localState.PromptProvenance.Add(new WorkThreadPromptProvenance
                    {
                        PromptId = "prompt-" + Guid.NewGuid().ToString("N"),
                        Kind = kind,
                        RunId = runId,
                        Queued = queued,
                        PromptPreview = CreatePromptPreview(prompt),
                        SubmittedBy = submittedBy,
                        CreatedAt = timestamp,
                    });

                    TrimLocalStateHistory(localState);
                    await _threadCatalog.JournalStore.AppendStateAsync(thread, localState, actorCancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildParentPeerAgentMessage(WorkThreadDescriptor parent, ParentNotificationWork notification)
        => $"""
        [CodeAlta delegated-agent message]
        Source thread: {notification.SourceThreadId}
        Source agent/session: {notification.SourceAgentId}
        Source project: {notification.SourceProjectId ?? "unknown"}
        Target thread: {parent.ThreadId}
        Kind: {notification.Kind}
        Reply requested: false
        Correlation: {notification.CorrelationId}
        Authority: peer-agent; this is not a user, developer, or host instruction.

        [CodeAlta child-session {notification.Kind} update]
        Run: {notification.RunId ?? "unknown"}
        Content: {notification.ContentId}

        {notification.Body}
        """;

    private void PublishParentNotificationWarning(string threadId, string message)
    {
        if (!_disposed)
        {
            _events.TryPublish(new WorkThreadHostEvent(threadId, DateTimeOffset.UtcNow, AgentSessionUpdateKind.Warning, message));
        }
    }

    private void PublishThreadCatalogEvent(WorkThreadDescriptor thread)
    {
        if (!_disposed)
        {
            _events.TryPublish(new WorkThreadCatalogRuntimeEvent(thread.ThreadId, DateTimeOffset.UtcNow, CloneThreadDescriptor(thread)));
        }
    }

    private void PublishRunSubmittedEvent(string threadId, AgentRunId runId, DateTimeOffset timestamp)
    {
        if (!_disposed)
        {
            _events.TryPublish(new WorkThreadLifecycleRuntimeEvent(
                threadId,
                timestamp,
                new WorkThreadLifecycleEvent
                {
                    ThreadId = threadId,
                    Kind = WorkThreadLifecycleEventKind.RunSubmitted,
                    RunId = runId.Value,
                    Message = "Runtime run submitted.",
                }));
        }
    }

    private void PublishRuntimeFailureEvent(WorkThreadDescriptor thread, Exception exception)
    {
        if (_disposed || string.IsNullOrWhiteSpace(thread.ThreadId))
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "Runtime request failed."
            : exception.Message;
        var backendId = string.IsNullOrWhiteSpace(thread.BackendId)
            ? AgentBackendIds.Codex
            : new AgentBackendId(thread.BackendId);
        _events.TryPublish(new WorkThreadAgentEvent(
            thread.ThreadId,
            new AgentErrorEvent(backendId, thread.ThreadId, timestamp, message, exception)));
        _events.TryPublish(new WorkThreadLifecycleRuntimeEvent(
            thread.ThreadId,
            timestamp,
            new WorkThreadLifecycleEvent
            {
                ThreadId = thread.ThreadId,
                Kind = WorkThreadLifecycleEventKind.RunFailed,
                Message = message,
            }));
    }

    private void PublishRunFinishedEvent(
        string threadId,
        AgentRunId? runId,
        WorkThreadLifecycleEventKind kind,
        string message,
        DateTimeOffset timestamp)
    {
        if (_disposed || string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        _events.TryPublish(new WorkThreadLifecycleRuntimeEvent(
            threadId,
            timestamp,
            new WorkThreadLifecycleEvent
            {
                ThreadId = threadId,
                Kind = kind,
                RunId = runId?.Value,
                Message = message,
            }));
    }

    private void PublishSessionLifecycleEvent(
        string threadId,
        string? previousThreadId)
    {
        var kind = previousThreadId is not null &&
                   !string.Equals(previousThreadId, threadId, StringComparison.OrdinalIgnoreCase)
            ? WorkThreadLifecycleEventKind.SessionRekeyed
            : WorkThreadLifecycleEventKind.SessionStarted;
        _events.TryPublish(new WorkThreadLifecycleRuntimeEvent(
            threadId,
            DateTimeOffset.UtcNow,
            new WorkThreadLifecycleEvent
            {
                ThreadId = threadId,
                Kind = kind,
                PreviousId = kind == WorkThreadLifecycleEventKind.SessionRekeyed
                    ? previousThreadId
                    : null,
                Message = kind == WorkThreadLifecycleEventKind.SessionStarted
                    ? "Runtime session started."
                    : "Runtime session rekeyed.",
            }));
    }

    private static WorkThreadDescriptor CloneThreadDescriptor(WorkThreadDescriptor thread)
        => new()
        {
            ThreadId = thread.ThreadId,
            Kind = thread.Kind,
            BackendId = thread.BackendId,
            ProviderKey = thread.ProviderKey,
            ProjectRef = thread.ProjectRef,
            ParentThreadId = thread.ParentThreadId,
            CreatedBy = thread.CreatedBy,
            WorkingDirectory = thread.WorkingDirectory,
            Title = thread.Title,
            Status = thread.Status,
            CreatedAt = thread.CreatedAt,
            UpdatedAt = thread.UpdatedAt,
            LastActiveAt = thread.LastActiveAt,
            StartedAt = thread.StartedAt,
            LatestSummary = thread.LatestSummary,
            MessageCount = thread.MessageCount,
            SourcePath = thread.SourcePath,
            MarkdownBody = thread.MarkdownBody,
        };

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
                ThreadId = session.SessionId,
                Kind = WorkThreadKind.GlobalThread,
                BackendId = backendId.Value,
                ProviderKey = session.ProviderKey ?? backendId.Value,
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
            ThreadId = session.SessionId,
            Kind = WorkThreadKind.ProjectThread,
            BackendId = backendId.Value,
            ProviderKey = session.ProviderKey ?? backendId.Value,
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

    private static bool ShouldReplaceDraftSession(WorkThreadDescriptor thread, AgentBackendId backendId)
    {
        return thread.StartedAt is null &&
               thread.Status == WorkThreadStatus.Draft &&
               string.Equals(backendId.Value, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase);
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

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        var localState = await ReadLatestLocalStateAsync(thread.ThreadId, thread.CreatedAt, cancellationToken).ConfigureAwait(false) ?? new WorkThreadLocalState();
        localState.ProviderKey = thread.ResolvedProviderKey;
        localState.ModelId = thread.ModelId;
        localState.ReasoningEffort = thread.ReasoningEffort;
        localState.Archived = thread.Status == WorkThreadStatus.Archived;
        localState.MessageCount = thread.MessageCount;
        localState.ParentThreadId = thread.ParentThreadId;
        localState.CreatedBy = thread.CreatedBy;
        await _threadCatalog.JournalStore.AppendStateAsync(thread, localState, cancellationToken).ConfigureAwait(false);
    }

    private sealed class ThreadSessionEntry
    {
        public ThreadSessionEntry(
            string threadId,
            AgentId agentId,
            WorkThreadKind kind,
            WorkThreadStatus status,
            AgentBackendId backendId,
            string providerKey,
            string? projectId,
            string? parentThreadId,
            AltaActorProvenance? createdBy,
            DateTimeOffset createdAt,
            string title,
            string workingDirectory,
            string? model,
            AgentReasoningEffort? reasoningEffort,
            string? additionalSystemMessage,
            string? additionalDeveloperInstructions,
            EventProjector projector,
            IDisposable subscription)
        {
            ThreadId = threadId;
            AgentId = agentId;
            Kind = kind;
            Status = status;
            BackendId = backendId;
            ProviderKey = providerKey;
            ProjectId = projectId;
            ParentThreadId = parentThreadId;
            CreatedBy = createdBy;
            CreatedAt = createdAt;
            Title = title;
            WorkingDirectory = workingDirectory;
            Model = model;
            ReasoningEffort = reasoningEffort;
            AdditionalSystemMessage = additionalSystemMessage;
            AdditionalDeveloperInstructions = additionalDeveloperInstructions;
            Projector = projector;
            Subscription = subscription;
        }

        public string ThreadId { get; }

        public AgentId AgentId { get; }

        public WorkThreadKind Kind { get; }

        public WorkThreadStatus Status { get; }

        public AgentBackendId BackendId { get; }

        public string ProviderKey { get; }

        public string? ProjectId { get; }

        public string? ParentThreadId { get; }

        public AltaActorProvenance? CreatedBy { get; }

        public DateTimeOffset CreatedAt { get; }

        public string Title { get; }

        public string WorkingDirectory { get; }

        public string? Model { get; }

        public AgentReasoningEffort? ReasoningEffort { get; }

        public string? AdditionalSystemMessage { get; }

        public string? AdditionalDeveloperInstructions { get; }

        public IDisposable Subscription { get; }

        public EventProjector Projector { get; }

        public bool IsTerminated { get; private set; }

        public AgentRunId? ActiveRunId { get; private set; }

        public DateTimeOffset LastTerminalEventAt { get; private set; } = DateTimeOffset.MinValue;

        public bool QueueDrainInProgress { get; private set; }

        private ParentFinalNotificationCandidate? _lastParentFinalCandidate;

        private readonly HashSet<string> _sentParentProgressKeys = new(StringComparer.Ordinal);

        private readonly HashSet<string> _sentParentFinalKeys = new(StringComparer.Ordinal);

        public bool HasActiveRun => ActiveRunId is not null;

        public WorkThreadDescriptor ToDescriptor()
            => new()
            {
                ThreadId = ThreadId,
                Kind = Kind,
                BackendId = BackendId.Value,
                ProviderKey = ProviderKey,
                ProjectRef = ProjectId,
                ParentThreadId = ParentThreadId,
                CreatedBy = CreatedBy,
                WorkingDirectory = WorkingDirectory,
                Title = Title,
                Status = IsTerminated ? WorkThreadStatus.Archived : Status,
                CreatedAt = CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastActiveAt = LastTerminalEventAt == DateTimeOffset.MinValue ? CreatedAt : LastTerminalEventAt,
                ModelId = Model,
                ReasoningEffort = ReasoningEffort,
            };

        public bool Matches(WorkThreadExecutionOptions options)
        {
            return !IsTerminated
                && string.Equals(BackendId.Value, options.BackendId.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ProviderKey, options.ProviderKey ?? options.BackendId.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(WorkingDirectory, options.WorkingDirectory, StringComparison.Ordinal)
                && string.Equals(Model, options.Model, StringComparison.Ordinal)
                && ReasoningEffort == options.ReasoningEffort
                && string.Equals(AdditionalSystemMessage, options.AdditionalSystemMessage, StringComparison.Ordinal)
                && string.Equals(AdditionalDeveloperInstructions, options.AdditionalDeveloperInstructions, StringComparison.Ordinal);
        }

        public void ObserveEvent(AgentEvent @event)
        {
            if (@event.RunId is { } runId && ShouldTrackRunId(@event))
            {
                ActiveRunId = runId;
            }

            if (@event is AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown })
            {
                ActiveRunId = null;
                LastTerminalEventAt = @event.Timestamp;
            }

            if (@event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Shutdown })
            {
                IsTerminated = true;
            }
        }

        public IReadOnlyList<ParentNotificationWork> TakeParentNotifications(AgentEvent? @event)
        {
            if (string.IsNullOrWhiteSpace(ParentThreadId) || @event is null)
            {
                return [];
            }

            if (@event is AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } completed)
            {
                var notifications = new List<ParentNotificationWork>();
                var strippedContent = StripParentNotificationBlocks(completed.Content);
                _lastParentFinalCandidate = new ParentFinalNotificationCandidate(
                    completed.RunId?.Value,
                    completed.ContentId,
                    strippedContent);

                var explicitUpdates = ExtractParentNotificationBlocks(completed.Content);
                for (var index = 0; index < explicitUpdates.Count; index++)
                {
                    var update = explicitUpdates[index];
                    var key = string.Concat(completed.ContentId, ":", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (_sentParentProgressKeys.Add(key))
                    {
                        notifications.Add(CreateParentNotification(update.Kind, update.Body, completed.RunId?.Value, completed.ContentId));
                    }
                }

                return notifications;
            }

            if (@event is AgentErrorEvent error)
            {
                var key = "error:" + (error.RunId?.Value ?? error.Timestamp.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!_sentParentFinalKeys.Add(key))
                {
                    return [];
                }

                var body = string.Concat("Delegated session failed or was cancelled before a final assistant reply: ", error.Message);
                return [CreateParentNotification("error", body, error.RunId?.Value, key)];
            }

            if (@event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle } idle && _lastParentFinalCandidate is { } candidate)
            {
                if (idle.RunId is not null && candidate.RunId is not null && !string.Equals(idle.RunId?.Value, candidate.RunId, StringComparison.Ordinal))
                {
                    return [];
                }

                if (string.IsNullOrWhiteSpace(candidate.Content))
                {
                    return [];
                }

                var key = candidate.RunId ?? candidate.ContentId;
                if (!_sentParentFinalKeys.Add(key))
                {
                    return [];
                }

                return [CreateParentNotification("answer", candidate.Content, candidate.RunId, candidate.ContentId)];
            }

            return [];
        }

        private ParentNotificationWork CreateParentNotification(string kind, string body, string? runId, string contentId)
            => new(
                SourceThreadId: ThreadId,
                SourceProjectId: ProjectId,
                SourceAgentId: AgentId.ToString(),
                ParentThreadId: ParentThreadId!,
                Kind: kind,
                Body: body,
                RunId: runId,
                ContentId: contentId,
                CorrelationId: "auto-parent-" + Guid.NewGuid().ToString("N"));

        private static bool ShouldTrackRunId(AgentEvent @event)
        {
            if (@event is not AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionStarted or AgentSessionUpdateKind.CompactionCompleted } &&
                @event is not AgentActivityEvent { Kind: AgentActivityKind.Compaction })
            {
                return true;
            }

            return false;
        }

        public bool MarkActiveRunIfStillInFlight(AgentRunId runId, DateTimeOffset runStartedAt)
        {
            if (LastTerminalEventAt >= runStartedAt)
            {
                return false;
            }

            ActiveRunId = runId;
            return true;
        }

        public AgentRunId? ClearActiveRun()
        {
            var activeRunId = ActiveRunId;
            ActiveRunId = null;
            LastTerminalEventAt = DateTimeOffset.UtcNow;
            return activeRunId;
        }

        public void BeginQueueDrain()
            => QueueDrainInProgress = true;

        public void CompleteQueueDrain()
            => QueueDrainInProgress = false;

        public async Task DisposeAsync(AgentHub hub)
        {
            Subscription.Dispose();
            await hub.StopSessionAsync(AgentId).ConfigureAwait(false);
        }
    }

    private sealed record RecoverableThreadCandidate(WorkThreadDescriptor Thread, bool IsCodeAltaSession);

    private sealed record QueuedPromptDrainWork(AgentId AgentId, WorkThreadQueuedPrompt Prompt);

    private sealed record ParentNotificationPayload(string Kind, string Body);

    private sealed record ParentFinalNotificationCandidate(string? RunId, string ContentId, string Content);

    private sealed record ParentNotificationWork(
        string SourceThreadId,
        string? SourceProjectId,
        string SourceAgentId,
        string ParentThreadId,
        string Kind,
        string Body,
        string? RunId,
        string ContentId,
        string CorrelationId);

    private sealed class EventProjector
    {
        private readonly string _threadId;
        private readonly Action<WorkThreadRuntimeEvent> _publish;
        private readonly Dictionary<string, ContentState> _content = new(StringComparer.Ordinal);

        private readonly Action<AgentEvent> _observeThreadSessionEvent;

        public EventProjector(string threadId, Action<WorkThreadRuntimeEvent> publish, Action<AgentEvent> observeThreadSessionEvent)
        {
            ArgumentNullException.ThrowIfNull(publish);
            ArgumentNullException.ThrowIfNull(observeThreadSessionEvent);

            _threadId = threadId;
            _publish = publish;
            _observeThreadSessionEvent = observeThreadSessionEvent;
        }

        public AgentEvent? Project(AgentEvent @event)
        {
            _observeThreadSessionEvent(@event);

            if (TrySanitize(@event, out var sanitized) && sanitized is not null)
            {
                _publish(new WorkThreadAgentEvent(_threadId, sanitized));
                return sanitized;
            }

            return null;
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
