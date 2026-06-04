using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime.Actors;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Owns per-session coordinator sessions, recovers project/global sessions, and projects sanitized runtime events.
/// </summary>
public sealed class SessionRuntimeService : IAsyncDisposable
{
    private static readonly Regex ScheduleBlockRegex = new(
        @"```codealta_schedule\s*\n.*?```",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParentNotificationBlockRegex = new(
        @"<notify-parent(?:\s+kind=""(?<kind>[^""]+)"")?\s*>(?<body>.*?)</notify-parent>",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly AgentHub _agentHub;
    private readonly IAgentSessionCatalog _agentSessionCatalog;
    private readonly ProjectCatalog _projectCatalog;
    private readonly SessionViewCatalog _sessionViewCatalog;
    private readonly AgentInstructionTemplateProvider _instructionTemplateProvider;
    private readonly CatalogOptions _catalogOptions;
    private readonly CodeAltaConfigStore _configStore;
    private readonly SkillCatalog _skillCatalog;
    private readonly BoundedRuntimeEventStream<SessionRuntimeEvent> _events = new();
    private readonly SessionActorRegistry _sessionActors = new(mailboxCapacity: 128);
    private readonly ConcurrentDictionary<string, RuntimeSessionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionRuntimeService"/> class.
    /// </summary>
    public SessionRuntimeService(
        AgentHub agentHub,
        IAgentSessionCatalog agentSessionCatalog,
        ProjectCatalog projectCatalog,
        SessionViewCatalog sessionViewCatalog,
        AgentInstructionTemplateProvider instructionTemplateProvider,
        CatalogOptions catalogOptions,
        SkillCatalog? skillCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(agentSessionCatalog);
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(sessionViewCatalog);
        ArgumentNullException.ThrowIfNull(instructionTemplateProvider);
        ArgumentNullException.ThrowIfNull(catalogOptions);

        _agentHub = agentHub;
        _agentSessionCatalog = agentSessionCatalog;
        _projectCatalog = projectCatalog;
        _sessionViewCatalog = sessionViewCatalog;
        _instructionTemplateProvider = instructionTemplateProvider;
        _catalogOptions = catalogOptions;
        _configStore = new CodeAltaConfigStore(catalogOptions);
        _skillCatalog = skillCatalog ?? new SkillCatalog();
    }

    /// <summary>
    /// Gets the skill catalog used when building instructions and activating skills.
    /// </summary>
    public SkillCatalog SkillCatalog => _skillCatalog;

    /// <summary>
    /// Streams sanitized runtime events across all active sessions.
    /// </summary>
    public IAsyncEnumerable<SessionRuntimeEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
        => _events.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Appends a CodeAlta-authored session event to the session journal and publishes it to runtime subscribers.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="event">The event to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes after the event is appended.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> or <paramref name="event"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the event session id does not match <paramref name="session"/>.</exception>
    public async Task AppendSessionEventAsync(
        SessionViewDescriptor session,
        AgentEvent @event,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(@event);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(session.SessionId, @event.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The event session id must match the target session.", nameof(@event));
        }

        await _sessionViewCatalog.JournalStore.EnsureHeaderAsync(session, cancellationToken).ConfigureAwait(false);
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        await store.AppendEventsAsync(
                session.ProviderId,
                session.ResolvedProviderKey,
                session.SessionId,
                [@event],
                cancellationToken)
            .ConfigureAwait(false);
        await _agentSessionCatalog.InvalidateAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        _events.TryPublish(new SessionAgentEvent(session.SessionId, @event));
    }

    /// <summary>
    /// Gets the approximate number of runtime events dropped because event consumers fell behind.
    /// </summary>
    public long DroppedRuntimeEventCount => _events.DroppedCount;

    /// <summary>
    /// Gets an active session descriptor from the runtime's in-memory coordinator session table.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active in-memory session descriptor when present; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> is empty.</exception>
    public async Task<SessionViewDescriptor?> TryGetActiveSessionDescriptorAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated)
        {
            return null;
        }

        var descriptor = entry.ToDescriptor();
        var localState = await ReadLatestLocalStateAsync(sessionId, entry.CreatedAt, cancellationToken).ConfigureAwait(false);
        if (localState is not null)
        {
            var activeProviderId = descriptor.ProviderId;
            var activeProviderKey = descriptor.ProviderKey;
            var activeModelId = descriptor.ModelId;
            var activeReasoningEffort = descriptor.ReasoningEffort;
            var activeAgentPromptId = descriptor.AgentPromptId;

            ApplyPersistedSessionLocalState(descriptor, localState);

            descriptor.ProviderId = activeProviderId;
            descriptor.ProviderKey = activeProviderKey;
            if (!string.IsNullOrWhiteSpace(activeModelId))
            {
                descriptor.ModelId = activeModelId;
            }

            if (activeReasoningEffort is not null)
            {
                descriptor.ReasoningEffort = activeReasoningEffort;
            }

            if (!string.IsNullOrWhiteSpace(activeAgentPromptId))
            {
                descriptor.AgentPromptId = activeAgentPromptId;
            }
        }

        return descriptor;
    }

    /// <summary>
    /// Lists recoverable user-facing sessions from the session catalog.
    /// </summary>
    public IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(CancellationToken cancellationToken = default)
        => ListRecoverableSessionsAsync(shouldListProviderSessions: null, cancellationToken);

    /// <summary>
    /// Lists recoverable user-facing sessions from the session catalog.
    /// </summary>
    /// <param name="shouldListProviderSessions">Optional predicate that returns whether a provider's sessions should be listed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recoverable user-facing sessions.</returns>
    public async IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(
        Func<ModelProviderId, bool>? shouldListProviderSessions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var metadata in _agentSessionCatalog.ListSessionsAsync(filter: null, cancellationToken).ConfigureAwait(false))
        {
            var session = TryCreateRecoverableSession(metadata, projects);
            if (session is null)
            {
                continue;
            }

            if (shouldListProviderSessions is not null &&
                !shouldListProviderSessions(new ModelProviderId(session.ResolvedProviderKey)))
            {
                continue;
            }

            await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
            yield return session;
        }
    }

    private async Task ApplyPersistedSessionLocalStateAsync(
        IReadOnlyList<SessionViewDescriptor> sessions,
        CancellationToken cancellationToken)
    {
        foreach (var session in sessions)
        {
            await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyPersistedSessionLocalStateAsync(
        SessionViewDescriptor session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionViewLocalState? localState;
        try
        {
            localState = await _sessionViewCatalog.JournalStore
                .ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (localState is null)
        {
            return;
        }

        ApplyPersistedSessionLocalState(session, localState);
    }

    private void ApplyPersistedSessionLocalState(SessionViewDescriptor session, SessionViewLocalState localState)
    {
        if (localState.Archived)
        {
            session.Status = SessionViewStatus.Archived;
        }

        if (!string.IsNullOrWhiteSpace(localState.ProviderKey))
        {
            var providerKey = localState.ProviderKey.Trim();
            session.ProviderKey = providerKey;
            session.ProviderId = providerKey;
        }

        if (!string.IsNullOrWhiteSpace(localState.ModelId))
        {
            session.ModelId = localState.ModelId;
        }

        if (localState.ReasoningEffort is { } reasoningEffort)
        {
            session.ReasoningEffort = reasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(localState.AgentPromptId))
        {
            session.AgentPromptId = ResolveKnownAgentPromptId(localState.AgentPromptId, session.WorkingDirectory);
        }

        if (localState.MessageCount is { } messageCount)
        {
            session.MessageCount = messageCount;
        }

        var parentSessionId = ResolveParentSessionId(localState.ParentSessionId, localState.CreatedBy?.SourceSessionId);
        if (!string.IsNullOrWhiteSpace(parentSessionId))
        {
            session.ParentSessionId = parentSessionId;
        }

        if (localState.CreatedBy is not null)
        {
            session.CreatedBy = localState.CreatedBy;
        }
    }

    /// <summary>
    /// Deletes a session from the session catalog when present and persists local hidden-session metadata otherwise.
    /// </summary>
    /// <param name="session">The session view to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the session existed and was deleted; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DeleteSessionAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var deleted = false;
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            deleted = await _agentSessionCatalog.DeleteSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        session.Status = SessionViewStatus.Archived;
        if (!deleted)
        {
            await UpdateSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    /// <summary>
    /// Persists machine-local session metadata for a recoverable session.
    /// </summary>
    /// <param name="session">The session whose local state should be updated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PersistSessionLocalStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            await _agentSessionCatalog.NotifySessionUpdatedAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        await UpdateSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        PublishSessionAgentConfigurationEvent(session);
    }

    /// <summary>
    /// Records a pending agent prompt selection for an active coordinator session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="agentPromptId">The selected agent prompt identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> or <paramref name="agentPromptId" /> is empty.</exception>
    public async Task SetActiveSessionAgentPromptIdAsync(
        string sessionId,
        string agentPromptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentPromptId);
        if (!_sessionActors.TryGet(sessionId, out var actor))
        {
            return;
        }

        await actor.QueryAsync(
                actorCancellationToken =>
                {
                    actorCancellationToken.ThrowIfCancellationRequested();
                    if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated)
                    {
                        entry.PendingAgentPromptId = agentPromptId.Trim();
                    }

                    return ValueTask.FromResult(true);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads persisted CodeAlta-owned agent-runtime history for a recoverable session without resuming the session.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored events when available; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session" /> is <see langword="null" />.</exception>
    public async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryAsync(
        SessionViewDescriptor session,
        CancellationToken cancellationToken = default)
        => await TryReadStoredHistoryAsync(session, onUnavailable: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads persisted CodeAlta-owned agent-runtime history for a recoverable session without resuming the session.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="onUnavailable">Optional callback invoked when a local history file exists but cannot be read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored events when available; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session" /> is <see langword="null" />.</exception>
    public async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryAsync(
        SessionViewDescriptor session,
        Action<Exception>? onUnavailable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            return null;
        }

        try
        {
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            return await store.ReadEventsAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
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
    /// Creates a new global session and returns its descriptor.
    /// </summary>
    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(
        SessionExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
        => await CreateGlobalSessionAsync(options, title, parentSessionId: null, createdBy: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new global session with optional durable lineage and returns its descriptor.
    /// </summary>
    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var now = DateTimeOffset.UtcNow;
        var session = new SessionViewDescriptor
        {
            SessionId = string.Empty,
            Kind = SessionViewKind.GlobalSession,
            ProviderId = options.ProviderId.Value,
            ProviderKey = options.ProviderKey ?? options.ProviderId.Value,
            WorkingDirectory = options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(title) ? "Global Session" : title.Trim(),
            Status = SessionViewStatus.Draft,
            ParentSessionId = NormalizeOptionalText(parentSessionId),
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = "Global overview and coordination session.",
            ModelId = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            AgentPromptId = NormalizeOptionalText(options.AgentPromptId),
        };

        try
        {
            await EnsureCoordinatorSessionAsync(session, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishRuntimeFailureEvent(session, ex);
            throw;
        }

        return session;
    }

    /// <summary>
    /// Creates a new project session and returns its descriptor.
    /// </summary>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
        => await CreateProjectSessionAsync(project, options, title, parentSessionId: null, createdBy: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new project session with optional durable lineage and returns its descriptor.
    /// </summary>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        var requestedProjectId = project.Id;
        var previousProject = await _projectCatalog.GetByPathAsync(project.ProjectPath, cancellationToken).ConfigureAwait(false);
        var restoredArchivedProject = previousProject?.Archived == true;
        project = await _projectCatalog.EnsurePersistedAsync(project, cancellationToken).ConfigureAwait(false);
        var persistedNewProject = previousProject is null &&
            string.Equals(project.Id, requestedProjectId, StringComparison.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        var session = new SessionViewDescriptor
        {
            SessionId = string.Empty,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = options.ProviderId.Value,
            ProviderKey = options.ProviderKey ?? options.ProviderId.Value,
            ProjectRef = project.Id,
            WorkingDirectory = options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(title) ? project.DisplayName : title.Trim(),
            Status = SessionViewStatus.Draft,
            ParentSessionId = NormalizeOptionalText(parentSessionId),
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = $"Project session for {project.DisplayName}.",
            ModelId = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            AgentPromptId = NormalizeOptionalText(options.AgentPromptId),
        };

        try
        {
            await EnsureCoordinatorSessionAsync(session, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await RollBackProjectPersistenceAsync(project, previousProject, persistedNewProject, restoredArchivedProject).ConfigureAwait(false);
            }
            catch (Exception rollbackException) when (rollbackException is not OperationCanceledException)
            {
                // Preserve the original session-start failure; rollback is best effort cleanup of transient project persistence.
            }

            if (ex is not OperationCanceledException)
            {
                PublishRuntimeFailureEvent(session, ex);
            }

            throw;
        }

        return session;
    }

    private async Task RollBackProjectPersistenceAsync(
        ProjectDescriptor project,
        ProjectDescriptor? previousProject,
        bool persistedNewProject,
        bool restoredArchivedProject)
    {
        if (persistedNewProject)
        {
            await _projectCatalog.DeleteAsync(project, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (restoredArchivedProject && previousProject is not null)
        {
            await _projectCatalog.SaveAsync(previousProject, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures that the session has an active coordinator session.
    /// </summary>
    public async Task<AgentSessionHandleId> EnsureCoordinatorSessionAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            return await EnsureCoordinatorSessionCoreAsync(session, options, cancellationToken).ConfigureAwait(false);
        }

        var actor = _sessionActors.GetOrCreate(session.SessionId);
        return await actor.QueryAsync(
                actorCancellationToken => EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<AgentSessionHandleId> EnsureCoordinatorSessionCoreAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);

        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);
        RuntimeSessionEntry? existing = null;
        var pendingAgentPromptId = default(string?);
        if (!string.IsNullOrWhiteSpace(session.SessionId) &&
            _entries.TryGetValue(session.SessionId, out existing) &&
            !existing.IsTerminated)
        {
            pendingAgentPromptId = NormalizeOptionalText(existing.PendingAgentPromptId);
        }

        var effectiveAgentPromptId = pendingAgentPromptId
            ?? NormalizeOptionalText(options.AgentPromptId)
            ?? NormalizeOptionalText(session.AgentPromptId);
        session.AgentPromptId = effectiveAgentPromptId;
        var instructions = _instructionTemplateProvider.BuildCoordinatorInstructions(session, project, options.Model, session.AgentPromptId);
        var providerProviderId = new ModelProviderId(options.ProviderId.Value);
        var developerInstructions = instructions.DeveloperInstructions;
        var additionalDeveloperInstructions = AppendPromptPart(BuildParentNotificationGuidance(session), options.AdditionalDeveloperInstructions);
        var tools = options.Tools;

        RuntimeSessionEntry? previousEntry = null;
        AgentSessionHandleId sessionHandleId;

        if (existing is not null && existing.Matches(options, session.AgentPromptId))
        {
            existing.PendingAgentPromptId = null;
            return existing.SessionHandleId;
        }

        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            _entries.TryRemove(session.SessionId, out previousEntry);
        }

        if (previousEntry is not null)
        {
            await previousEntry.DisposeAsync(_agentHub).ConfigureAwait(false);
        }

        var previousSessionId = string.IsNullOrWhiteSpace(session.SessionId) ? null : session.SessionId;
        if (previousSessionId is null)
        {
            session.SessionId = Guid.CreateVersion7().ToString();
        }

        var requestedSessionId = NormalizeOptionalText(session.SessionId);

        var sessionOptions = new AgentSessionResumeOptions
        {
            SessionId = requestedSessionId,
            ParentSessionId = NormalizeOptionalText(session.ParentSessionId),
            CreatedBySessionId = NormalizeOptionalText(session.CreatedBy?.SourceSessionId),
            Title = NormalizeOptionalText(session.Title),
            ProviderKey = options.ProviderKey ?? session.ResolvedProviderKey,
            Model = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            Streaming = true,
            WorkingDirectory = options.WorkingDirectory,
            ProjectRoots = options.ProjectRoots,
            SystemMessage = AppendPromptPart(instructions.SystemMessage, options.AdditionalSystemMessage),
            DeveloperInstructions = AppendPromptPart(developerInstructions, additionalDeveloperInstructions),
            AgentPromptId = NormalizeOptionalText(session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName,
            Tools = tools,
            OnPermissionRequest = options.OnPermissionRequest,
            OnUserInputRequest = options.OnUserInputRequest,
        };

        var startNewSession = previousSessionId is null;

        if (startNewSession)
        {
            var handle = await _agentHub.StartSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);
            sessionHandleId = handle.HandleId;
        }
        else
        {
            AgentSessionHandle handle;
            try
            {
                handle = await _agentHub.ResumeSessionAsync(session.SessionId, sessionOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                handle = await _agentHub.StartSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);
            }

            sessionHandleId = handle.HandleId;
        }

        session.ProviderId = options.ProviderId.Value;
        session.ProviderKey = options.ProviderKey ?? options.ProviderId.Value;
        session.WorkingDirectory = options.WorkingDirectory;
        session.ModelId = options.Model;
        session.ReasoningEffort = options.ReasoningEffort;
        session.AgentPromptId = effectiveAgentPromptId ?? session.AgentPromptId;
        await UpsertSessionMetadataAsync(session, options, cancellationToken).ConfigureAwait(false);
        await UpdateSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        if (startNewSession)
        {
            await _agentSessionCatalog.NotifySessionCreatedAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _agentSessionCatalog.NotifySessionResumedAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        PublishSessionCatalogEvent(session);
        PublishSessionLifecycleEvent(session.SessionId);

        RuntimeSessionEntry? entry = null;
        var actor = _sessionActors.GetOrCreate(session.SessionId);
        var projector = new EventProjector(
            session.SessionId,
            runtimeEvent => _events.TryPublish(runtimeEvent),
            @event => entry?.ObserveEvent(@event));
        var subscription = await _agentHub.SubscribeSessionEventsAsync(
                sessionHandleId,
                @event => _ = PostAgentEventToActorAsync(actor, session.SessionId, projector, @event),
                cancellationToken)
            .ConfigureAwait(false);

        entry = new RuntimeSessionEntry(
            session.SessionId,
            sessionHandleId,
            session.Kind,
            session.Status,
            providerProviderId,
            options.ProviderKey ?? session.ResolvedProviderKey,
            session.ProjectRef,
            session.ParentSessionId,
            session.CreatedBy,
            session.CreatedAt,
            session.Title,
            options.WorkingDirectory,
            options.Model,
            options.ReasoningEffort,
            session.AgentPromptId,
            options.AdditionalSystemMessage,
            options.AdditionalDeveloperInstructions,
            options.ProjectRoots,
            tools,
            CreateToolSignatures(options.Tools),
            options.OnPermissionRequest,
            options.OnUserInputRequest,
            projector,
            subscription);

        _entries[session.SessionId] = entry;

        return sessionHandleId;
    }

    /// <summary>
    /// Sends input to the coordinator session for a session.
    /// </summary>
    public async Task<AgentRunId> SendAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        AgentSendOptions sendOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sendOptions);

        try
        {
            var sessionStateUpdated = false;
            var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(
                    async actorCancellationToken =>
                    {
                        var ensuredHandleId = await EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken).ConfigureAwait(false);
                        session.MarkStarted(DateTimeOffset.UtcNow);
                        sessionStateUpdated = true;

                        return ensuredHandleId;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (sessionStateUpdated)
            {
                PublishSessionCatalogEvent(session);
            }

            var runStartedAt = DateTimeOffset.UtcNow;
            var runId = await _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken).ConfigureAwait(false);
            if (await MarkActiveRunIfStillInFlightAsync(session.SessionId, runId, runStartedAt, cancellationToken).ConfigureAwait(false))
            {
                PublishRunSubmittedEvent(session.SessionId, runId, runStartedAt);
            }

            return runId;
        }
        catch (OperationCanceledException)
        {
            var activeRunId = await ClearActiveRunAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            PublishRunFinishedEvent(
                session.SessionId,
                activeRunId,
                SessionLifecycleEventKind.RunAborted,
                "Runtime run cancelled.",
                DateTimeOffset.UtcNow);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ClearActiveRunAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            PublishRuntimeFailureEvent(session, ex);
            throw;
        }
    }

    /// <summary>
    /// Persists a headless prompt queue item for later submission by the owning runtime/front-end queue drain path.
    /// </summary>
    /// <param name="session">Target session.</param>
    /// <param name="prompt">Prompt text to submit later.</param>
    /// <param name="kind">Prompt dispatch kind, such as <c>send</c>, <c>message</c>, or <c>request</c>.</param>
    /// <param name="submittedBy">Durable caller attribution for the queueing actor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted queue item.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="prompt"/> or <paramref name="kind"/> is empty.</exception>
    public async Task<SessionViewQueuedPrompt> QueuePromptAsync(
        SessionViewDescriptor session,
        string prompt,
        string kind,
        AltaActorProvenance? submittedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var actor = _sessionActors.GetOrCreate(session.SessionId);
        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    var item = new SessionViewQueuedPrompt
                    {
                        QueueItemId = "queue-" + Guid.NewGuid().ToString("N"),
                        Kind = kind,
                        Prompt = prompt,
                        PromptPreview = CreatePromptPreview(prompt),
                        State = "queued",
                        SubmittedBy = submittedBy,
                        CreatedAt = timestamp,
                    };

                    var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false) ?? new SessionViewLocalState();
                    CopySessionMetadata(session, localState);
                    localState.QueuedPrompts ??= [];
                    localState.QueuedPrompts.Add(item);
                    localState.PromptProvenance ??= [];
                    localState.PromptProvenance.Add(new SessionViewPromptProvenance
                    {
                        PromptId = item.QueueItemId,
                        Kind = kind,
                        Queued = true,
                        PromptPreview = item.PromptPreview,
                        SubmittedBy = submittedBy,
                        CreatedAt = timestamp,
                    });

                    TrimLocalStateHistory(localState);
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                    _events.TryPublish(new SessionQueueRuntimeEvent(
                        session.SessionId,
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
    /// Activates a CodeAlta-managed skill for a session through the host-owned agent runtime path.
    /// </summary>
    /// <param name="session">Target session.</param>
    /// <param name="options">Execution options used to resolve the backing session.</param>
    /// <param name="skillName">Skill name to activate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run identifier that received the activated skill content.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="skillName"/> is empty.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the requested skill cannot be resolved.</exception>
    public async Task<AgentRunId> ActivateSkillAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        string skillName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);
        var query = BuildSkillCatalogQuery(project, options.ProjectRoots);
        var activation = await _skillCatalog.ActivateAsync(query, skillName, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Skill '{skillName}' was not found or is not activatable for this session.");

        var input = new AgentInput(
        [
            new AgentInputItem.Skill(activation.Descriptor.Name, activation.Descriptor.SkillFilePath),
            new AgentInputItem.Text(
                $"""
                The user activated the CodeAlta skill '{activation.Descriptor.Name}' for this session.
                Treat the following host-provided skill content as active session context.

                {activation.Payload}
                """),
        ]);

        return await SendAsync(
                session,
                options,
                new AgentSendOptions { Input = input },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Steers the current coordinator run for a session.
    /// </summary>
    public async Task<AgentRunId> SteerAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        AgentSteerOptions steerOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(steerOptions);

        var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(
                async actorCancellationToken =>
                {
                    var entry = await GetActiveRuntimeSessionForSteeringAsync(session, options, actorCancellationToken).ConfigureAwait(false);
                    return entry.SessionHandleId;
                },
                cancellationToken)
            .ConfigureAwait(false);

        return await _agentHub.SteerAsync(sessionHandleId, steerOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether the session's active coordinator session has an in-flight run.
    /// </summary>
    public async Task<bool> HasActiveRunAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            return false;
        }

        if (!_sessionActors.TryGet(session.SessionId, out var actor))
        {
            return false;
        }

        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    return _entries.TryGetValue(session.SessionId, out var entry) && entry.HasActiveRun;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether the session has an active coordinator session in this runtime process.
    /// </summary>
    /// <param name="sessionId">The durable session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when this runtime owns a non-terminated coordinator session.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId"/> is empty.</exception>
    public async Task<bool> HasActiveCoordinatorSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_sessionActors.TryGet(sessionId, out var actor))
        {
            return false;
        }

        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    return _entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Aborts active work in the session coordinator session.
    /// </summary>
    public async Task AbortAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_disposed)
        {
            return;
        }

        SessionActorCommandResult result;
        try
        {
            var actor = _sessionActors.GetOrCreate(sessionId);
            result = await actor.ExecuteReservedAsync(
                    async actorCancellationToken =>
                    {
                        var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);
                        await _agentHub.AbortAsync(entry.SessionHandleId, actorCancellationToken).ConfigureAwait(false);
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
                result.Message ?? $"Failed to abort session '{sessionId}'.",
                result.Exception);
        }
    }

    private static string? BuildParentNotificationGuidance(SessionViewDescriptor session)
        => string.IsNullOrWhiteSpace(session.ParentSessionId)
            ? null
            : $"Parent session: `{session.ParentSessionId}`. CodeAlta auto-forwards your final assistant reply. For progress/intermediate parent updates, include `<notify-parent>update text</notify-parent>` in an assistant reply.";

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

    private async Task<SessionViewLocalState?> ReadLatestLocalStateAsync(
        string sessionId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _sessionViewCatalog.JournalStore.ReadLatestStateAsync(sessionId, createdAt, cancellationToken).ConfigureAwait(false);
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

    private static void CopySessionMetadata(SessionViewDescriptor session, SessionViewLocalState localState)
    {
        localState.ProviderKey = session.ResolvedProviderKey;
        localState.ModelId = session.ModelId;
        localState.ReasoningEffort = session.ReasoningEffort;
        localState.AgentPromptId = NormalizeOptionalText(session.AgentPromptId);
        localState.Archived = session.Status == SessionViewStatus.Archived;
        localState.MessageCount = session.MessageCount;
        localState.ParentSessionId = session.ParentSessionId;
        localState.CreatedBy = session.CreatedBy;
    }

    private static int QueuedPromptCount(SessionViewLocalState localState)
        => localState.QueuedPrompts.Count(static prompt => IsPendingQueuedPromptState(prompt.State));

    private static bool IsPendingQueuedPromptState(string? state)
        => string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(state, "submitting", StringComparison.OrdinalIgnoreCase);

    private static string CreatePromptPreview(string prompt)
        => prompt.Length <= 160 ? prompt : prompt[..160];

    private static void TrimLocalStateHistory(SessionViewLocalState localState)
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
            GlobalDisabledSkillNames = _configStore.LoadGlobalDisabledSkillNames(),
            ProjectDisabledSkillNames = _configStore.LoadProjectDisabledSkillNames(project?.ProjectPath ?? resolvedProjectRoots.FirstOrDefault()),
            IncludeInvalid = true,
            IncludeShadowed = true,
            IncludeUntrusted = true,
        };
    }

    /// <summary>
    /// Detaches and disposes the active coordinator session for a session when present.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when an active coordinator session was detached; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DetachRuntimeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var actor = _sessionActors.GetOrCreate(sessionId);
        var detached = await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    _entries.TryRemove(sessionId, out var entry);

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
            await _sessionActors.RemoveAsync(sessionId, cancelPending: false).ConfigureAwait(false);
        }

        return detached;
    }

    /// <summary>
    /// Triggers a manual compaction for a session coordinator session.
    /// </summary>
    public async Task CompactAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        var result = await _sessionActors.GetOrCreate(session.SessionId).ExecuteAsync(
                async actorCancellationToken =>
                {
                    _events.TryPublish(new SessionHostEvent(
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        AgentSessionUpdateKind.CompactionStarted,
                        $"Manual compaction requested for '{session.Title}'."));

                    var sessionHandleId = await EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken).ConfigureAwait(false);
                    var outcome = await _agentHub.CompactAsync(sessionHandleId, actorCancellationToken).ConfigureAwait(false);
                    if (outcome is not null)
                    {
                        _events.TryPublish(new SessionHostEvent(
                            session.SessionId,
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
                result.Message ?? $"Failed to compact session '{session.SessionId}'.",
                result.Exception);
        }
    }

    /// <summary>
    /// Gets sanitized history for an active session.
    /// </summary>
    public async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var actor = _sessionActors.GetOrCreate(sessionId);
        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);
                    var history = await _agentHub.GetSessionHistoryAsync(entry.SessionHandleId, actorCancellationToken).ConfigureAwait(false);
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
        await _sessionActors.DisposeAsync().ConfigureAwait(false);

        foreach (var entry in _entries.Values)
        {
            await entry.DisposeAsync(_agentHub).ConfigureAwait(false);
        }

        _entries.Clear();
    }

    private async Task<ProjectDescriptor?> ResolveProjectAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ProjectRef))
        {
            return null;
        }

        return await _projectCatalog.GetByIdAsync(session.ProjectRef, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeSessionEntry> GetEntryAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        await Task.CompletedTask.ConfigureAwait(false);
        if (!_entries.TryGetValue(sessionId, out var entry))
        {
            throw new InvalidOperationException($"Session '{sessionId}' does not have an active coordinator session.");
        }

        return entry;
    }

    private async Task<RuntimeSessionEntry> GetActiveRuntimeSessionForSteeringAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            throw new InvalidOperationException("Cannot steer a session without an active coordinator session.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        if (!_entries.TryGetValue(session.SessionId, out var entry) || entry.IsTerminated)
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId}' does not have an active coordinator session to steer.");
        }

        if (!string.Equals(entry.ProviderId.Value, options.ProviderId.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId}' active coordinator session does not match the requested provider.");
        }

        if (!entry.HasActiveRun)
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId}' does not have an active coordinator run to steer.");
        }

        return entry;
    }

    private async Task<bool> MarkActiveRunIfStillInFlightAsync(
        string sessionId,
        AgentRunId runId,
        DateTimeOffset runStartedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!_sessionActors.TryGet(sessionId, out var actor))
        {
            return false;
        }

        try
        {
            return await actor.QueryAsync(
                    _ =>
                    {
                        if (_entries.TryGetValue(sessionId, out var entry))
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

    private async Task<AgentRunId?> ClearActiveRunAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessionActors.TryGet(sessionId, out var actor))
        {
            return null;
        }

        try
        {
            return await actor.QueryAsync(
                    _ =>
                    {
                        if (_entries.TryGetValue(sessionId, out var entry))
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
        SessionActor actor,
        string sessionId,
        EventProjector projector,
        AgentEvent @event)
    {
        try
        {
            var parentNotifications = await actor.QueryAsync(_ =>
                {
                    var sanitized = projector.Project(@event);
                    var notifications = _entries.TryGetValue(sessionId, out var entry)
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
                await TryDrainNextQueuedPromptAsync(sessionId).ConfigureAwait(false);
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

    private async Task TryDrainNextQueuedPromptAsync(string sessionId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var work = await TryMarkNextQueuedPromptSubmittingAsync(sessionId).ConfigureAwait(false);
        if (work is null)
        {
            return;
        }

        try
        {
            var runStartedAt = DateTimeOffset.UtcNow;
            var runId = await _agentHub.RunAsync(
                    work.SessionHandleId,
                    new AgentSendOptions { Input = AgentInput.Text(work.Prompt.Prompt) },
                    CancellationToken.None)
                .ConfigureAwait(false);
            await MarkQueuedPromptSubmittedAsync(sessionId, work.Prompt.QueueItemId, runId, runStartedAt, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_disposed && ex is OperationCanceledException)
            {
                return;
            }

            await MarkQueuedPromptFailedAsync(sessionId, work.Prompt.QueueItemId, ex.Message, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
    }

    private async Task<QueuedPromptDrainWork?> TryMarkNextQueuedPromptSubmittingAsync(string sessionId)
    {
        var actor = _sessionActors.GetOrCreate(sessionId);
        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated || entry.HasActiveRun || entry.QueueDrainInProgress)
                    {
                        return null;
                    }

                    var localState = await ReadLatestLocalStateAsync(sessionId, entry.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                    if (localState is null || localState.QueuedPrompts.Count == 0)
                    {
                        return null;
                    }

                    var item = localState.QueuedPrompts.FirstOrDefault(static prompt => string.Equals(prompt.State, "queued", StringComparison.OrdinalIgnoreCase));
                    if (item is null)
                    {
                        return null;
                    }

                    var sessionHandleId = entry.SessionHandleId;
                    if (!string.IsNullOrWhiteSpace(entry.PendingAgentPromptId))
                    {
                        var session = entry.ToDescriptor();
                        sessionHandleId = await EnsureCoordinatorSessionCoreAsync(session, entry.ToExecutionOptions(), actorCancellationToken).ConfigureAwait(false);
                        if (!_entries.TryGetValue(sessionId, out entry) || entry.IsTerminated || entry.HasActiveRun || entry.QueueDrainInProgress)
                        {
                            return null;
                        }
                    }

                    var timestamp = DateTimeOffset.UtcNow;
                    item.State = "submitting";
                    item.DrainedAt = timestamp;
                    item.LastError = null;
                    CopySessionMetadata(entry.ToDescriptor(), localState);
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(entry.ToDescriptor(), localState, actorCancellationToken).ConfigureAwait(false);
                    entry.BeginQueueDrain();
                    PublishQueueChanged(sessionId, localState, item, timestamp, isEnqueued: false);
                    return new QueuedPromptDrainWork(sessionHandleId, CloneQueuedPrompt(item));
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task MarkQueuedPromptSubmittedAsync(
        string sessionId,
        string queueItemId,
        AgentRunId runId,
        DateTimeOffset runStartedAt,
        DateTimeOffset timestamp)
        => await UpdateQueuedPromptStateAsync(
                sessionId,
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

    private async Task MarkQueuedPromptFailedAsync(string sessionId, string queueItemId, string error, DateTimeOffset timestamp)
        => await UpdateQueuedPromptStateAsync(
                sessionId,
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
        string sessionId,
        string queueItemId,
        DateTimeOffset timestamp,
        Action<SessionViewQueuedPrompt> updateItem,
        Action<SessionViewPromptProvenance>? updateProvenance,
        Action<RuntimeSessionEntry>? updateEntry)
    {
        var actor = _sessionActors.GetOrCreate(sessionId);
        await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    try
                    {
                        if (!_entries.TryGetValue(sessionId, out var currentEntry))
                        {
                            return false;
                        }

                        var localState = await ReadLatestLocalStateAsync(sessionId, currentEntry.CreatedAt, actorCancellationToken).ConfigureAwait(false);
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

                        await _sessionViewCatalog.JournalStore.AppendStateAsync(currentEntry.ToDescriptor(), localState, actorCancellationToken).ConfigureAwait(false);
                        PublishQueueChanged(sessionId, localState, item, timestamp, isEnqueued: false);
                        return true;
                    }
                    finally
                    {
                        if (updateEntry is not null && _entries.TryGetValue(sessionId, out var entry))
                        {
                            updateEntry(entry);
                        }
                    }
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void PublishQueueChanged(string sessionId, SessionViewLocalState localState, SessionViewQueuedPrompt item, DateTimeOffset timestamp, bool isEnqueued)
    {
        _events.TryPublish(new SessionQueueRuntimeEvent(
            sessionId,
            timestamp,
            QueuedPromptCount(localState),
            item.QueueItemId,
            item.PromptPreview,
            isEnqueued));
    }

    private static SessionViewQueuedPrompt CloneQueuedPrompt(SessionViewQueuedPrompt item)
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
            var parent = await TryResolveSessionForParentDeliveryAsync(notification.ParentSessionId, CancellationToken.None).ConfigureAwait(false);
            if (parent is null)
            {
                PublishParentNotificationWarning(notification.SourceSessionId, $"Parent session '{notification.ParentSessionId}' was not found for automatic child-session notification.");
                return;
            }

            var prompt = BuildParentPeerAgentMessage(parent, notification);
            var submittedBy = new AltaActorProvenance
            {
                Kind = "agent",
                SourceSessionId = notification.SourceSessionId,
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
                    PublishParentNotificationWarning(notification.SourceSessionId, $"Parent session '{notification.ParentSessionId}' could not be steered; queued the child-session notification instead. {ex.Message}");
                }
            }

            await QueuePromptAsync(parent, prompt, "parent-notify", submittedBy, CancellationToken.None).ConfigureAwait(false);
            await TryDrainNextQueuedPromptAsync(parent.SessionId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || _disposed)
        {
            PublishParentNotificationWarning(notification.SourceSessionId, $"Automatic parent notification failed without affecting the child session: {ex.Message}");
        }
    }

    private async Task<SessionViewDescriptor?> TryResolveSessionForParentDeliveryAsync(string sessionId, CancellationToken cancellationToken)
    {
        SessionViewDescriptor? session = null;
        try
        {
            await foreach (var candidate in ListRecoverableSessionsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(candidate.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    session = candidate;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
        }

        if (session is null && _entries.TryGetValue(sessionId, out var entry))
        {
            var now = DateTimeOffset.UtcNow;
            session = new SessionViewDescriptor
            {
                SessionId = sessionId,
                Kind = SessionViewKind.ProjectSession,
                ProviderId = entry.ProviderId.Value,
                ProviderKey = entry.ProviderKey,
                ProjectRef = entry.ProjectId,
                ParentSessionId = entry.ParentSessionId,
                WorkingDirectory = entry.WorkingDirectory,
                Title = sessionId,
                Status = SessionViewStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                LastActiveAt = now,
                StartedAt = now,
            };
        }

        if (session is not null)
        {
            await ApplyLocalSessionStateAsync(session, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    private async Task ApplyLocalSessionStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        try
        {
            var localState = await _sessionViewCatalog.JournalStore
                .ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken)
                .ConfigureAwait(false);
            if (localState is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(localState.ParentSessionId))
            {
                session.ParentSessionId = localState.ParentSessionId;
            }

            if (localState.CreatedBy is not null)
            {
                session.CreatedBy = localState.CreatedBy;
            }

            if (localState.Archived)
            {
                session.Status = SessionViewStatus.Archived;
            }

            if (localState.MessageCount is not null)
            {
                session.MessageCount = localState.MessageCount;
            }

            if (!string.IsNullOrWhiteSpace(localState.AgentPromptId))
            {
                session.AgentPromptId = ResolveKnownAgentPromptId(localState.AgentPromptId, session.WorkingDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
        }
    }

    private async Task UpsertSessionMetadataAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) || session.CreatedAt == default)
        {
            return;
        }

        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        await store.UpsertSessionAsync(
                new AgentSessionSummary
                {
                    SessionId = session.SessionId,
                    ProviderId = new ModelProviderId(options.ProviderId.Value),
                    ProtocolFamily = options.ProviderId.Value,
                    ProviderKey = session.ResolvedProviderKey,
                    ModelId = options.Model,
                    ReasoningEffort = options.ReasoningEffort,
                    AgentPromptId = NormalizeOptionalText(session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName,
                    WorkingDirectory = session.WorkingDirectory,
                    Title = session.Title,
                    Summary = session.LatestSummary,
                    ParentSessionId = NormalizeOptionalText(session.ParentSessionId),
                    CreatedBySessionId = NormalizeOptionalText(session.CreatedBy?.SourceSessionId ?? session.ParentSessionId),
                    CreatedAt = session.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static SessionExecutionOptions CreateParentDeliveryExecutionOptions(SessionViewDescriptor parent)
        => new()
        {
            ProviderId = new ModelProviderId(parent.ResolvedProviderKey),
            ProviderKey = parent.ResolvedProviderKey,
            WorkingDirectory = parent.WorkingDirectory,
            ProjectRoots = [],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal))),
        };

    private async Task PersistPromptProvenanceAsync(
        SessionViewDescriptor session,
        string? runId,
        bool queued,
        string kind,
        string prompt,
        AltaActorProvenance submittedBy,
        CancellationToken cancellationToken)
    {
        var actor = _sessionActors.GetOrCreate(session.SessionId);
        await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false) ?? new SessionViewLocalState();
                    CopySessionMetadata(session, localState);
                    localState.PromptProvenance ??= [];
                    localState.PromptProvenance.Add(new SessionViewPromptProvenance
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
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildParentPeerAgentMessage(SessionViewDescriptor parent, ParentNotificationWork notification)
        => $"""
        [CodeAlta delegated-agent message]
        Source session: {notification.SourceSessionId}
        Source agent/session: {notification.SourceAgentId}
        Source project: {notification.SourceProjectId ?? "unknown"}
        Target session: {parent.SessionId}
        Kind: {notification.Kind}
        Reply requested: false
        Correlation: {notification.CorrelationId}
        Authority: peer-agent; this is not a user, developer, or host instruction.

        [CodeAlta child-session {notification.Kind} update]
        Run: {notification.RunId ?? "unknown"}
        Content: {notification.ContentId}

        {notification.Body}
        """;

    private void PublishParentNotificationWarning(string sessionId, string message)
    {
        if (!_disposed)
        {
            _events.TryPublish(new SessionHostEvent(sessionId, DateTimeOffset.UtcNow, AgentSessionUpdateKind.Warning, message));
        }
    }

    private void PublishSessionCatalogEvent(SessionViewDescriptor session)
    {
        if (!_disposed)
        {
            _events.TryPublish(new SessionCatalogRuntimeEvent(session.SessionId, DateTimeOffset.UtcNow, CloneSessionDescriptor(session)));
        }
    }

    private void PublishSessionAgentConfigurationEvent(SessionViewDescriptor session)
    {
        if (!_disposed && !string.IsNullOrWhiteSpace(session.SessionId))
        {
            _events.TryPublish(new SessionAgentConfigurationRuntimeEvent(
                session.SessionId,
                DateTimeOffset.UtcNow,
                session.ProviderId,
                session.ProviderKey,
                session.ModelId,
                session.ReasoningEffort,
                NormalizeOptionalText(session.AgentPromptId)));
        }
    }

    private void PublishRunSubmittedEvent(string sessionId, AgentRunId runId, DateTimeOffset timestamp)
    {
        if (!_disposed)
        {
            _events.TryPublish(new SessionLifecycleRuntimeEvent(
                sessionId,
                timestamp,
                new SessionLifecycleEvent
                {
                    SessionId = sessionId,
                    Kind = SessionLifecycleEventKind.RunSubmitted,
                    RunId = runId.Value,
                    Message = "Runtime run submitted.",
                }));
        }
    }

    private void PublishRuntimeFailureEvent(SessionViewDescriptor session, Exception exception)
    {
        if (_disposed || string.IsNullOrWhiteSpace(session.SessionId))
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "Runtime request failed."
            : exception.Message;
        var ProviderId = string.IsNullOrWhiteSpace(session.ProviderId)
            ? ModelProviderIds.Codex
            : new ModelProviderId(session.ProviderId);
        _events.TryPublish(new SessionAgentEvent(
            session.SessionId,
            new AgentErrorEvent(ProviderId, session.SessionId, timestamp, message, exception)));
        _events.TryPublish(new SessionLifecycleRuntimeEvent(
            session.SessionId,
            timestamp,
            new SessionLifecycleEvent
            {
                SessionId = session.SessionId,
                Kind = SessionLifecycleEventKind.RunFailed,
                Message = message,
            }));
    }

    private void PublishRunFinishedEvent(
        string sessionId,
        AgentRunId? runId,
        SessionLifecycleEventKind kind,
        string message,
        DateTimeOffset timestamp)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _events.TryPublish(new SessionLifecycleRuntimeEvent(
            sessionId,
            timestamp,
            new SessionLifecycleEvent
            {
                SessionId = sessionId,
                Kind = kind,
                RunId = runId?.Value,
                Message = message,
            }));
    }

    private void PublishSessionLifecycleEvent(string sessionId)
    {
        _events.TryPublish(new SessionLifecycleRuntimeEvent(
            sessionId,
            DateTimeOffset.UtcNow,
            new SessionLifecycleEvent
            {
                SessionId = sessionId,
                Kind = SessionLifecycleEventKind.SessionStarted,
                Message = "Runtime session started.",
            }));
    }

    private static SessionViewDescriptor CloneSessionDescriptor(SessionViewDescriptor session)
        => new()
        {
            SessionId = session.SessionId,
            Kind = session.Kind,
            ProviderId = session.ProviderId,
            ProviderKey = session.ProviderKey,
            ProjectRef = session.ProjectRef,
            ParentSessionId = session.ParentSessionId,
            CreatedBy = session.CreatedBy,
            WorkingDirectory = session.WorkingDirectory,
            Title = session.Title,
            Status = session.Status,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            LastActiveAt = session.LastActiveAt,
            StartedAt = session.StartedAt,
            LatestSummary = session.LatestSummary,
            ModelId = session.ModelId,
            ReasoningEffort = session.ReasoningEffort,
            AgentPromptId = session.AgentPromptId,
            MessageCount = session.MessageCount,
            SourcePath = session.SourcePath,
            MarkdownBody = session.MarkdownBody,
        };

    private SessionViewDescriptor? TryCreateRecoverableSession(
        AgentSessionMetadata session,
        IReadOnlyList<ProjectDescriptor> projects)
    {
        if (string.IsNullOrWhiteSpace(session.ProviderKey))
        {
            return null;
        }

        var providerKey = session.ProviderKey.Trim();
        var cwd = session.Context?.Cwd ?? session.WorkspacePath;
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        var normalizedCwd = NormalizePath(cwd);
        var parentSessionId = ResolveParentSessionId(session.ParentSessionId, session.CreatedBySessionId);
        if (string.Equals(normalizedCwd, NormalizePath(_catalogOptions.GlobalRoot), StringComparison.OrdinalIgnoreCase))
        {
            return new SessionViewDescriptor
            {
                SessionId = session.SessionId,
                Kind = SessionViewKind.GlobalSession,
                ProviderId = providerKey,
                ProviderKey = providerKey,
                WorkingDirectory = normalizedCwd,
                Title = BuildSessionTitle(session, "Global Session"),
                Status = SessionViewStatus.Active,
                ParentSessionId = parentSessionId,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                LastActiveAt = session.UpdatedAt,
                StartedAt = session.CreatedAt,
                LatestSummary = session.Summary,
                AgentPromptId = ResolveKnownAgentPromptId(session.AgentPromptId, projectRoot: null),
            };
        }

        var project = projects.FirstOrDefault(candidate =>
            string.Equals(NormalizePath(candidate.ProjectPath), normalizedCwd, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return null;
        }

        return new SessionViewDescriptor
        {
            SessionId = session.SessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = providerKey,
            ProviderKey = providerKey,
            ProjectRef = project.Id,
            WorkingDirectory = normalizedCwd,
            Title = BuildSessionTitle(session, project.DisplayName),
            Status = SessionViewStatus.Active,
            ParentSessionId = parentSessionId,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            LastActiveAt = session.UpdatedAt,
            StartedAt = session.CreatedAt,
            LatestSummary = session.Summary,
            AgentPromptId = ResolveKnownAgentPromptId(session.AgentPromptId, project.ProjectPath),
        };
    }

    private string? ResolveKnownAgentPromptId(string? promptId, string? projectRoot)
    {
        var normalized = NormalizeOptionalText(promptId);
        if (normalized is null)
        {
            return null;
        }

        var query = new AgentPromptCatalogQuery
        {
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = !string.IsNullOrWhiteSpace(projectRoot),
            UserCodeAltaRoot = _catalogOptions.GlobalRoot,
            UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return new AgentPromptCatalog().ListEffectivePrompts(query)
            .Any(prompt => string.Equals(prompt.PromptName, normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : AgentPromptCatalog.DefaultPromptName;
    }

    private static string BuildSessionTitle(AgentSessionMetadata session, string fallback)
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

    private static string? ResolveParentSessionId(string? parentSessionId, string? createdBySessionId)
        => NormalizeOptionalText(parentSessionId) ?? NormalizeOptionalText(createdBySessionId);

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

    private static IReadOnlyList<string> CreateToolSignatures(IReadOnlyList<AgentToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return [];
        }

        return tools
            .Select(static tool => string.Join(
                '\u001f',
                tool.Spec.Name,
                tool.Spec.Description,
                tool.Spec.InputSchema.GetRawText()))
            .OrderBy(static signature => signature, StringComparer.Ordinal)
            .ToArray();
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

    private async Task UpdateSessionLocalStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false) ?? new SessionViewLocalState();
        localState.ProviderKey = session.ResolvedProviderKey;
        localState.ModelId = session.ModelId;
        localState.ReasoningEffort = session.ReasoningEffort;
        localState.AgentPromptId = NormalizeOptionalText(session.AgentPromptId);
        localState.Archived = session.Status == SessionViewStatus.Archived;
        localState.MessageCount = session.MessageCount;
        localState.ParentSessionId = session.ParentSessionId;
        localState.CreatedBy = session.CreatedBy;
        await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, cancellationToken).ConfigureAwait(false);
    }

    private sealed class RuntimeSessionEntry
    {
        public RuntimeSessionEntry(
            string sessionId,
            AgentSessionHandleId sessionHandleId,
            SessionViewKind kind,
            SessionViewStatus status,
            ModelProviderId providerId,
            string providerKey,
            string? projectId,
            string? parentSessionId,
            AltaActorProvenance? createdBy,
            DateTimeOffset createdAt,
            string title,
            string workingDirectory,
            string? model,
            AgentReasoningEffort? reasoningEffort,
            string? agentPromptId,
            string? additionalSystemMessage,
            string? additionalDeveloperInstructions,
            IReadOnlyList<string> projectRoots,
            IReadOnlyList<AgentToolDefinition>? tools,
            IReadOnlyList<string> toolSignatures,
            AgentPermissionRequestHandler onPermissionRequest,
            AgentUserInputRequestHandler? onUserInputRequest,
            EventProjector projector,
            IDisposable subscription)
        {
            SessionId = sessionId;
            SessionHandleId = sessionHandleId;
            Kind = kind;
            Status = status;
            ProviderId = providerId;
            ProviderKey = providerKey;
            ProjectId = projectId;
            ParentSessionId = parentSessionId;
            CreatedBy = createdBy;
            CreatedAt = createdAt;
            Title = title;
            WorkingDirectory = workingDirectory;
            Model = model;
            ReasoningEffort = reasoningEffort;
            AgentPromptId = NormalizeOptionalText(agentPromptId);
            AdditionalSystemMessage = additionalSystemMessage;
            AdditionalDeveloperInstructions = additionalDeveloperInstructions;
            ProjectRoots = projectRoots.ToArray();
            Tools = tools?.ToArray() ?? [];
            ToolSignatures = toolSignatures;
            OnPermissionRequest = onPermissionRequest;
            OnUserInputRequest = onUserInputRequest;
            Projector = projector;
            Subscription = subscription;
        }

        public string SessionId { get; }

        public AgentSessionHandleId SessionHandleId { get; }

        public SessionViewKind Kind { get; }

        public SessionViewStatus Status { get; }

        public ModelProviderId ProviderId { get; }

        public string ProviderKey { get; }

        public string? ProjectId { get; }

        public string? ParentSessionId { get; }

        public AltaActorProvenance? CreatedBy { get; }

        public DateTimeOffset CreatedAt { get; }

        public string Title { get; }

        public string WorkingDirectory { get; }

        public string? Model { get; }

        public AgentReasoningEffort? ReasoningEffort { get; }

        public string? AgentPromptId { get; }

        public string? PendingAgentPromptId { get; set; }

        public string? AdditionalSystemMessage { get; }

        public string? AdditionalDeveloperInstructions { get; }

        public IReadOnlyList<string> ProjectRoots { get; }

        public IReadOnlyList<AgentToolDefinition> Tools { get; }

        public IReadOnlyList<string> ToolSignatures { get; }

        public AgentPermissionRequestHandler OnPermissionRequest { get; }

        public AgentUserInputRequestHandler? OnUserInputRequest { get; }

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

        public SessionViewDescriptor ToDescriptor()
            => new()
            {
                SessionId = SessionId,
                Kind = Kind,
                ProviderId = ProviderId.Value,
                ProviderKey = ProviderKey,
                ProjectRef = ProjectId,
                ParentSessionId = ParentSessionId,
                CreatedBy = CreatedBy,
                WorkingDirectory = WorkingDirectory,
                Title = Title,
                Status = IsTerminated ? SessionViewStatus.Archived : Status,
                CreatedAt = CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastActiveAt = LastTerminalEventAt == DateTimeOffset.MinValue ? CreatedAt : LastTerminalEventAt,
                ModelId = Model,
                ReasoningEffort = ReasoningEffort,
                AgentPromptId = PendingAgentPromptId ?? AgentPromptId,
            };

        public SessionExecutionOptions ToExecutionOptions()
            => new()
            {
                ProviderId = ProviderId,
                ProviderKey = ProviderKey,
                WorkingDirectory = WorkingDirectory,
                ProjectRoots = ProjectRoots,
                Model = Model,
                ReasoningEffort = ReasoningEffort,
                AgentPromptId = PendingAgentPromptId ?? AgentPromptId,
                Tools = Tools,
                AdditionalSystemMessage = AdditionalSystemMessage,
                AdditionalDeveloperInstructions = AdditionalDeveloperInstructions,
                OnPermissionRequest = OnPermissionRequest,
                OnUserInputRequest = OnUserInputRequest,
            };

        public bool Matches(SessionExecutionOptions options, string? agentPromptId)
        {
            var resolvedAgentPromptId = NormalizeOptionalText(agentPromptId) ?? NormalizeOptionalText(options.AgentPromptId);
            return !IsTerminated
                && string.Equals(ProviderId.Value, options.ProviderId.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ProviderKey, options.ProviderKey ?? options.ProviderId.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(WorkingDirectory, options.WorkingDirectory, StringComparison.Ordinal)
                && string.Equals(Model, options.Model, StringComparison.Ordinal)
                && ReasoningEffort == options.ReasoningEffort
                && string.Equals(AgentPromptId, resolvedAgentPromptId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(AdditionalSystemMessage, options.AdditionalSystemMessage, StringComparison.Ordinal)
                && string.Equals(AdditionalDeveloperInstructions, options.AdditionalDeveloperInstructions, StringComparison.Ordinal)
                && ToolSignatures.SequenceEqual(CreateToolSignatures(options.Tools), StringComparer.Ordinal);
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
            if (string.IsNullOrWhiteSpace(ParentSessionId) || @event is null)
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
                SourceSessionId: SessionId,
                SourceProjectId: ProjectId,
                SourceAgentId: SessionHandleId.ToString(),
                ParentSessionId: ParentSessionId!,
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
            await hub.StopSessionAsync(SessionHandleId).ConfigureAwait(false);
        }
    }

    private sealed record QueuedPromptDrainWork(AgentSessionHandleId SessionHandleId, SessionViewQueuedPrompt Prompt);

    private sealed record ParentNotificationPayload(string Kind, string Body);

    private sealed record ParentFinalNotificationCandidate(string? RunId, string ContentId, string Content);

    private sealed record ParentNotificationWork(
        string SourceSessionId,
        string? SourceProjectId,
        string SourceAgentId,
        string ParentSessionId,
        string Kind,
        string Body,
        string? RunId,
        string ContentId,
        string CorrelationId);

    private sealed class EventProjector
    {
        private readonly string _sessionId;
        private readonly Action<SessionRuntimeEvent> _publish;
        private readonly Dictionary<string, ContentState> _content = new(StringComparer.Ordinal);

        private readonly Action<AgentEvent> _observeRuntimeSessionEvent;

        public EventProjector(string sessionId, Action<SessionRuntimeEvent> publish, Action<AgentEvent> observeRuntimeSessionEvent)
        {
            ArgumentNullException.ThrowIfNull(publish);
            ArgumentNullException.ThrowIfNull(observeRuntimeSessionEvent);

            _sessionId = sessionId;
            _publish = publish;
            _observeRuntimeSessionEvent = observeRuntimeSessionEvent;
        }

        public AgentEvent? Project(AgentEvent @event)
        {
            _observeRuntimeSessionEvent(@event);

            if (TrySanitize(@event, out var sanitized) && sanitized is not null)
            {
                _publish(new SessionAgentEvent(_sessionId, sanitized));
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
