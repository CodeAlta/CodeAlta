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
public sealed partial class SessionRuntimeService : IAsyncDisposable
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
    private readonly SessionDiscoveryScope? _discoveryScope;
    private readonly CatalogOptions _catalogOptions;
    private readonly CodeAltaConfigStore _configStore;
    private readonly SkillCatalog _skillCatalog;
    private readonly SessionRuntimeEventPublisher _events = new();
    private readonly Guid _runtimeInstanceId = Guid.NewGuid();
    private readonly SessionActorRegistry _sessionActors = new(mailboxCapacity: 128);
    private readonly ConcurrentDictionary<string, RuntimeSessionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly OwnedProviderEventForwarding _forwarding = new();
    private readonly ConcurrentDictionary<string, Task> _transitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _identityGate = new();
    private readonly HashSet<string> _newSessionIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Gets the optional borrowed catalog used for agent-prompt metadata lookup.</summary>
    /// <remarks>Null preserves fresh default-catalog construction at each existing lookup site.
    /// The supplying owner retains the catalog and its locator through runtime cleanup.</remarks>
    internal AgentPromptCatalog? PromptCatalog { get; init; }

    private Task<T> AdmitAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _forwarding.RunAsync(body);
    }

    private Task AdmitAsync(Func<Task> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _forwarding.RunAsync(body);
    }

    private SessionActor GetActorForWork(string sessionId)
    {
        lock (_identityGate)
        {
            if (_sessionActors.TryGet(sessionId, out var existing)) return existing;
            ObjectDisposedException.ThrowIf(_disposed, this);
            var actor = _sessionActors.GetOrCreate(sessionId);
            return actor;
        }
    }

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
        : this(agentHub, agentSessionCatalog, projectCatalog, sessionViewCatalog, instructionTemplateProvider, catalogOptions, skillCatalog, false)
    {
    }

    /// <summary>Initializes a runtime with an explicit owned automatic-permission policy.</summary>
    /// <remarks>Automatic approval grants tools the host's privileges; roots are not a sandbox.</remarks>
    /// <exception cref="ArgumentNullException">A required runtime dependency is null.</exception>
    public SessionRuntimeService(
        AgentHub agentHub, IAgentSessionCatalog agentSessionCatalog, ProjectCatalog projectCatalog,
        SessionViewCatalog sessionViewCatalog, AgentInstructionTemplateProvider instructionTemplateProvider,
        CatalogOptions catalogOptions, SkillCatalog? skillCatalog, bool autoApproveOwnedPermissions)
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
        _discoveryScope = instructionTemplateProvider.DiscoveryScope;
        _catalogOptions = catalogOptions;
        _configStore = new CodeAltaConfigStore(catalogOptions);
        _skillCatalog = skillCatalog ?? new SkillCatalog();
        Permissions = new SessionPermissionService(autoApproveOwnedPermissions);
    }

    /// <summary>Gets application-owned pending permissions, independent of attached frontend presentations.</summary>
    public SessionPermissionService Permissions { get; }

    /// <summary>Gets committed bounded live display state, independent of the original lossy event/effects stream.</summary>
    public RuntimeDisplayProjection Display => _events.Display;

    /// <summary>Gets the output the running tool calls have written so far, which no journal keeps.</summary>
    public RuntimeToolOutputProjection ToolOutput => _events.ToolOutput;

    /// <summary>
    /// Gets the skill catalog used when building instructions and activating skills.
    /// </summary>
    public SkillCatalog SkillCatalog => _skillCatalog;

    /// <summary>
    /// Streams sanitized runtime events across all active sessions.
    /// </summary>
    /// <param name="cancellationToken">Cancels admission or channel reads; buffered events may still be yielded after admission.</param>
    /// <returns>The exclusive original-event sequence, not a broadcast or recoverable display snapshot.</returns>
    /// <remarks>
    /// Admission occurs on the first enumeration move, not when obtaining the sequence or enumerator.
    /// A pre-canceled token neither claims admission nor consumes buffered events. Only one enumerator may
    /// be active; use <see cref="Display"/> for independent bounded display observations instead of a competing reader.
    /// Dispose the enumerator on detach. Admission is released only after actual enumeration termination or
    /// disposal; cancellation or runtime completion alone does not release an enumerator suspended at a yield.
    /// After admission, underlying channel cancellation semantics apply: available buffered events may be
    /// yielded despite cancellation. Completion drains accepted events; a successor consumes remaining events,
    /// without replay. Original object references and accepted-event order are preserved by this stream,
    /// but newest publications can still be dropped under pressure. Enumeration is not acknowledgement of
    /// frontend/plugin effects, a history recovery guarantee, or ownership of a run. Detach does not stop the runtime.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Another original-event enumerator owns admission.</exception>
    /// <exception cref="OperationCanceledException">Admission or a channel read is canceled.</exception>
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
        var effectWorkingDirectory = session.WorkingDirectory;
        var sessionId = session.SessionId;
        var projectId = session.ProjectRef;
        await AdmitAsync(() => AppendSessionEventOwnedBodyAsync(session, @event, sessionId, projectId, effectWorkingDirectory, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendSessionEventOwnedBodyAsync(SessionViewDescriptor session, AgentEvent @event, string sessionId, string? projectId, string effectWorkingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(@event);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(sessionId, @event.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The event session id must match the target session.", nameof(@event));
        }

        await _sessionViewCatalog.JournalStore.EnsureHeaderAsync(session, cancellationToken).ConfigureAwait(false);
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        var publication = new LiveEventPublication(this);
        await publication.CompleteAsync(async mark =>
        {
        await store.AppendEventsAsync(
                session.ProviderId,
                session.ResolvedProviderKey,
                sessionId,
                [@event],
                cancellationToken)
            .ConfigureAwait(false);
        await _agentSessionCatalog.InvalidateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _events.TryPublish(new SessionAgentEvent(sessionId, @event));
        mark(CapturePluginEvent(@event, sessionId, projectId, effectWorkingDirectory));
        }, static () => { }, ObserveLivePluginEventAsync,
            () => publication.Published is null ? Task.CompletedTask : InvalidateFileSearchCacheAsync(@event, effectWorkingDirectory).AsTask(), _forwarding.RetainDependencies).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the approximate number of runtime events dropped because event consumers fell behind.
    /// </summary>
    public long DroppedRuntimeEventCount => _events.DroppedCount;

    /// <summary>Reads durable notes for a known active or recoverable session, without requiring a frontend view or starting providers.</summary>
    /// <param name="sessionId">An identifier resolved only within the configured backend root.</param>
    /// <param name="cancellationToken">Cancels lookup or reading.</param>
    /// <returns>The exact latest Markdown, or empty when no notes event exists.</returns>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    /// <exception cref="SessionNotesSessionNotFoundException">No known session matches the identifier and scope.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="System.Text.Json.JsonException">A canonical journal record cannot be decoded.</exception>
    /// <exception cref="ObjectDisposedException">The runtime has been disposed.</exception>
    public async Task<string> GetNotesMarkdownAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetNotesMarkdownOwnedBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<string> GetNotesMarkdownOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await ResolveNotesSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var notes = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .ReadLatestNotesAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        return notes is null ? string.Empty : notes.Markdown;
    }

    /// <summary>Reads known-session notes with lexical containment of the notes-content open.</summary>
    /// <param name="sessionId">A backend-resolved active or recoverable session, not a renderer path or descriptor.</param>
    /// <param name="cancellationToken">Cancels lookup/read or the caller wait; runtime retains admitted work.</param>
    /// <returns>Exact latest Markdown, or empty for no notes, an empty Set or Clear.</returns>
    /// <remarks>No provider activation, writes or events. Scan costs, prior cache probes and reparse/external races
    /// are not bounded or isolated by the content-open containment check.</remarks>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="SessionNotesSessionNotFoundException">No known session matches the configured scope.</exception>
    /// <exception cref="InvalidOperationException">The resolved journal no longer exists.</exception>
    /// <exception cref="AgentSessionHistoryException">The notes-content path is outside the sessions root.</exception>
    /// <exception cref="IOException">The journal cannot be read or its notes are invalid.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="System.Text.Json.JsonException">A canonical journal record is malformed.</exception>
    /// <exception cref="ObjectDisposedException">Runtime admission has closed.</exception>
    /// <exception cref="OperationCanceledException">The operation or caller wait is canceled.</exception>
    public async Task<string> GetOwnedNotesMarkdownAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetOwnedNotesMarkdownBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<string> GetOwnedNotesMarkdownBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await ResolveNotesSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var notes = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .ReadLatestNotesContainedAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        return notes is null ? string.Empty : notes.Markdown;
    }

    /// <summary>Appends session notes and delivers feedback in the existing per-journal write order.</summary>
    /// <param name="sessionId">A known backend session identifier; never a renderer grant or arbitrary path.</param>
    /// <param name="markdown">Exact replacement Markdown.</param>
    /// <param name="kind">Set or cleared. Cleared requires empty Markdown.</param>
    /// <param name="committed">Synchronous, non-reentrant presentation notification after acknowledgment. Must not block on UI work.</param>
    /// <param name="cancellationToken">Cancels before write admission, not an acknowledged commit.</param>
    /// <returns>A task completing after persistence and feedback.</returns>
    /// <exception cref="ArgumentException">The identifier or update is invalid.</exception>
    /// <exception cref="ArgumentNullException">Markdown or feedback is null.</exception>
    /// <exception cref="SessionNotesSessionNotFoundException">No known session matches.</exception>
    /// <exception cref="OperationCanceledException">Canceled before write admission.</exception>
    /// <exception cref="IOException">The write failed; partial I/O is not claimed to be rolled back.</exception>
    /// <exception cref="AgentNotesCommittedException">Persistence succeeded but subsequent feedback failed.</exception>
    /// <exception cref="ObjectDisposedException">The runtime has been disposed.</exception>
    public async Task UpdateNotesAsync(string sessionId, string markdown, AgentNotesUpdateKind kind,
        Action<AgentNotesEvent> committed, CancellationToken cancellationToken = default)
        // The journal owns the cancellation cutoff: after commit, feedback must settle rather
        // than presenting a cancelled waiter as though the write had not happened.
        => await AdmitAsync(() => UpdateNotesOwnedBodyAsync(sessionId, markdown, kind, committed, cancellationToken), cancellationToken).ConfigureAwait(false);

    private async Task UpdateNotesOwnedBodyAsync(string sessionId, string markdown, AgentNotesUpdateKind kind,
        Action<AgentNotesEvent> committed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(committed);
        if (kind is not (AgentNotesUpdateKind.Set or AgentNotesUpdateKind.Cleared) ||
            (kind == AgentNotesUpdateKind.Cleared && markdown.Length != 0))
        {
            throw new ArgumentException("Invalid notes update.", nameof(kind));
        }

        var session = await ResolveNotesSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var notes = new AgentNotesEvent(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, null, kind, markdown);
        var publication = new LiveEventPublication(this);
        await publication.CompleteAsync(mark => _sessionViewCatalog.JournalStore.CreateSessionStore().AppendNotesAsync(notes, async () =>
        {
            await _agentSessionCatalog.InvalidateAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            _events.TryPublish(new SessionAgentEvent(session.SessionId, notes));
            mark(CapturePluginEvent(notes, session.SessionId, session.ProjectId, session.WorkingDirectory));
            committed(notes);
        }, cancellationToken), static () => { }, ObserveLivePluginEventAsync,
            static () => Task.CompletedTask, _forwarding.RetainDependencies).ConfigureAwait(false);
    }

    private async Task<(string SessionId, ModelProviderId ProviderId, string? ProjectId, string WorkingDirectory)> ResolveNotesSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated)
        {
            return (entry.SessionId, !string.IsNullOrWhiteSpace(entry.ProviderId.Value)
                ? entry.ProviderId
                : new ModelProviderId(entry.ProviderKey), entry.ProjectId, entry.WorkingDirectory);
        }

        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new SessionNotesSessionNotFoundException(sessionId);
        }

        return await ResolveRecoveredPluginEventContextAsync(metadata.SessionId, metadata.ProviderKey,
            metadata.Context?.Cwd, metadata.WorkspacePath, _catalogOptions.GlobalRoot, NormalizePath,
            async () => (await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
                .Select(static project => (project.Id, project.ProjectPath))).ConfigureAwait(false);
    }

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
        => await AdmitAsync(() => TryGetActiveSessionDescriptorOwnedBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionViewDescriptor?> TryGetActiveSessionDescriptorOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
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
    /// Gets the git worktree a session works in, while its folder exists: null for a session that works in the
    /// folder of its project, and for one that is not known.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The folder of the worktree, or null.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    public async Task<string?> GetSessionWorktreeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        // A session that is attached works where its attachment runs.
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated) return ExistingWorktree(entry.WorktreeDirectory);
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return ExistingWorktree(session?.WorktreeDirectory);
    }

    internal async Task<SessionViewDescriptor?> ResolveOwnedSessionAsync(string sessionId, CancellationToken cancellationToken)
        => await AdmitAsync(() => ResolveOwnedSessionBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    internal Task<bool> RenameOwnedSessionAsync(string sessionId, string? projectId, string workspacePath, string title)
        => AdmitAsync(() => GetActorForWork(sessionId).QueryAsync(async token =>
        {
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            var metadata = await store.GetSessionAsync(sessionId, token).ConfigureAwait(false);
            if (metadata is null) return false;
            var projects = await _projectCatalog.LoadAsync(token).ConfigureAwait(false);
            var session = TryCreateRecoverableSession(metadata, projects);
            if (session is null || session.Kind != (projectId is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession)
                || session.ProjectRef != projectId || session.WorkingDirectory != workspacePath) return false;
            var summary = await store.GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false);
            if (summary is null) return false;
            await store.UpsertSessionAsync(summary with { Title = title }, token).ConfigureAwait(false);
            if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated) entry.Title = title;
            await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, token).ConfigureAwait(false);
            return true;
        }).AsTask(), CancellationToken.None);

    private async Task<SessionViewDescriptor?> ResolveOwnedSessionBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var session = TryCreateRecoverableSession(metadata, projects);
        if (session is not null)
        {
            // The session is shown under the name it was given. A send that attaches it again leaves its saved title.
            if (GivenTitle(metadata, session, projects) is { } title) session.Title = title;
            if (metadata.ViewState is not null)
            {
                ApplyCachedSessionLocalState(session, metadata.ViewState);
            }
            else
            {
                await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
            }
        }
        return session;
    }

    /// <summary>
    /// Lists recoverable user-facing sessions from the session catalog.
    /// </summary>
    public IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(CancellationToken cancellationToken = default)
        => ListRecoverableSessionsAsync(shouldListProviderSessions: null, cancellationToken);

    /// <summary>
    /// Reconciles the recoverable-session cache with external journal additions, changes, and deletions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true" /> when cached visible session metadata changed.</returns>
    public async Task<bool> ReconcileRecoverableSessionCacheAsync(CancellationToken cancellationToken = default)
        => await AdmitAsync(() => ReconcileRecoverableSessionCacheOwnedBodyAsync(cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> ReconcileRecoverableSessionCacheOwnedBodyAsync(CancellationToken cancellationToken)
    {
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        var result = await store.ReconcileCacheAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Changed)
        {
            return false;
        }

        await _agentSessionCatalog.InvalidateAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

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

            // A session that was named is listed under its name, not under the first line of its last answer: a
            // parent finds its sub-agents by the titles it gave them.
            if (GivenTitle(metadata, session, projects) is { } title) session.Title = title;
            if (metadata.ViewState is not null)
            {
                ApplyCachedSessionLocalState(session, metadata.ViewState);
            }
            else
            {
                await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
            }

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

    private void ApplyCachedSessionLocalState(SessionViewDescriptor session, AgentSessionViewStateMetadata localState)
    {
        var viewState = new SessionViewLocalState
        {
            ProviderKey = localState.ProviderKey,
            ModelId = localState.ModelId,
            ReasoningEffort = localState.ReasoningEffort,
            AgentPromptId = localState.AgentPromptId,
            Archived = localState.Archived,
            MessageCount = localState.MessageCount,
            ParentSessionId = localState.ParentSessionId,
            CreatedBy = DeserializeCachedCreatedBy(localState.CreatedByJson),
        };
        ApplyPersistedSessionLocalState(session, viewState);
    }

    private static AltaActorProvenance? DeserializeCachedCreatedBy(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(
                json,
                SessionViewJournalJsonSerializerContext.Default.AltaActorProvenance);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a session from the session catalog when present and persists local hidden-session metadata otherwise.
    /// </summary>
    /// <param name="session">The session view to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the session existed and was deleted; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DeleteSessionAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => DeleteSessionOwnedBodyAsync(session, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> DeleteSessionOwnedBodyAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
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
        => await AdmitAsync(() => PersistSessionLocalStateOwnedBodyAsync(session, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task PersistSessionLocalStateOwnedBodyAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
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
        => await AdmitAsync(() => SetActiveSessionAgentPromptIdOwnedBodyAsync(sessionId, agentPromptId, CancellationToken.None), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task SetActiveSessionAgentPromptIdOwnedBodyAsync(string sessionId, string agentPromptId, CancellationToken cancellationToken)
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
                    if (_entries.TryGetValue(sessionId, out var entry) && (!entry.IsTerminated || _transitions.ContainsKey(sessionId)))
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
        => await AdmitAsync(() => TryReadStoredHistoryOwnedBodyAsync(session, onUnavailable, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryOwnedBodyAsync(SessionViewDescriptor session,
        Action<Exception>? onUnavailable, CancellationToken cancellationToken)
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
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(
        SessionExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
        => await CreateGlobalSessionAsync(options, title, parentSessionId: null, createdBy: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new global session with optional durable lineage and returns its descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => CreateGlobalSessionOwnedBodyAsync(options, title, parentSessionId, createdBy, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionViewDescriptor> CreateGlobalSessionOwnedBodyAsync(SessionExecutionOptions options, string? title,
        string? parentSessionId, AltaActorProvenance? createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateDiscoveryPaths(null, options);

        var now = DateTimeOffset.UtcNow;
        var session = new SessionViewDescriptor
        {
            SessionId = string.Empty,
            Kind = SessionViewKind.GlobalSession,
            ProviderId = options.ProviderId.Value,
            ProviderKey = options.ProviderKey ?? options.ProviderId.Value,
            WorkingDirectory = options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(title) ? UnnamedGlobalSessionTitle : title.Trim(),
            Status = SessionViewStatus.Draft,
            ParentSessionId = NormalizeOptionalText(parentSessionId),
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = UnnamedGlobalSessionSummary,
            ModelId = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            AgentPromptId = NormalizeOptionalText(options.AgentPromptId),
        };

        var failureCapture = new RuntimeFailureCapture();
        try
        {
            await EnsureCoordinatorSessionWithFailureCaptureAsync(session, options, cancellationToken, failureCapture).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ObserveRuntimeFailureAsync(session, failureCapture.Original ?? ex, ex).ConfigureAwait(false);
            throw;
        }

        return session;
    }

    /// <summary>
    /// Creates a new project session and returns its descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
        => await CreateProjectSessionAsync(project, options, title, parentSessionId: null, createdBy: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new project session with optional durable lineage and returns its descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        CancellationToken cancellationToken = default)
        => await CreateProjectSessionAsync(project, options, title, parentSessionId, createdBy, worktreeDirectory: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new project session that works in a git worktree of its project, or in the folder of its
    /// project, and returns its descriptor.
    /// </summary>
    /// <remarks>
    /// The session belongs to its project whatever folder it works in: its working directory is the folder of
    /// the project, and the worktree is recorded beside it. The worktree is an argument of the creation, never a
    /// property of the options, so that nothing that copies options can lose it.
    /// </remarks>
    /// <param name="project">The project of the session.</param>
    /// <param name="options">How the session runs; its working directory is the folder of the project.</param>
    /// <param name="title">The title, or null for the name of the project.</param>
    /// <param name="parentSessionId">The session that this one continues the work of, if any.</param>
    /// <param name="createdBy">Who creates the session when it is not the user.</param>
    /// <param name="worktreeDirectory">The folder of the project in a git worktree, which exists; null for the folder of the project.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The descriptor of the session.</returns>
    /// <exception cref="ArgumentException">
    /// A supplied scoped working or project path is invalid or outside the instruction boundary, or the worktree is not an absolute folder.
    /// </exception>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        string? worktreeDirectory,
        CancellationToken cancellationToken = default)
    {
        if (worktreeDirectory is not null && !Path.IsPathFullyQualified(worktreeDirectory))
            throw new ArgumentException("A worktree is an absolute folder.", nameof(worktreeDirectory));
        return await AdmitAsync(() => CreateProjectSessionOwnedBodyAsync(project, options, title, parentSessionId, createdBy, worktreeDirectory, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionViewDescriptor> CreateProjectSessionOwnedBodyAsync(ProjectDescriptor project, SessionExecutionOptions options,
        string? title, string? parentSessionId, AltaActorProvenance? createdBy, string? worktreeDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        ValidateDiscoveryPaths(null, options, project);

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
            WorktreeDirectory = NormalizeOptionalText(worktreeDirectory),
            Title = string.IsNullOrWhiteSpace(title) ? project.DisplayName : title.Trim(),
            Status = SessionViewStatus.Draft,
            ParentSessionId = NormalizeOptionalText(parentSessionId),
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = UnnamedProjectSessionSummary(project),
            ModelId = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            AgentPromptId = NormalizeOptionalText(options.AgentPromptId),
        };

        var failureCapture = new RuntimeFailureCapture();
        try
        {
            await EnsureCoordinatorSessionWithFailureCaptureAsync(session, options, cancellationToken, failureCapture).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Exception escaping = ex;
            try
            {
                await RollBackProjectPersistenceAsync(project, previousProject, persistedNewProject, restoredArchivedProject).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                escaping = new AggregateException(ex, rollbackException);
            }

            if (ex is not OperationCanceledException)
            {
                await ObserveRuntimeFailureAsync(session, failureCapture.Original ?? ex, escaping).ConfigureAwait(false);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(escaping);
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
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<AgentSessionHandleId> EnsureCoordinatorSessionAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken = default)
        => await EnsureCoordinatorSessionWithFailureCaptureAsync(session, options, cancellationToken, null).ConfigureAwait(false);

    private async Task<AgentSessionHandleId> EnsureCoordinatorSessionWithFailureCaptureAsync(
        SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken cancellationToken, RuntimeFailureCapture? failureCapture)
    {
        return await AdmitAsync(async () =>
        {
            var entry = await ResolveCoordinatorEntryAsync(session, options, failureCapture: failureCapture).ConfigureAwait(false);
            return entry.SessionHandleId;
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task<AgentSessionHandleId> EnsureOwnedCoordinatorSessionAsync(SessionViewDescriptor session, SessionExecutionOptions options, bool useExplicitPrompt = false)
        => AdmitAsync(async () =>
        {
            var entry = await ResolveCoordinatorEntryAsync(session, options, ownedCommand: true, useExplicitPrompt: useExplicitPrompt).ConfigureAwait(false);
            return entry.SessionHandleId;
        }, CancellationToken.None);

    private void ReserveSessionIdentity(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_identityGate)
        {
            if (!string.IsNullOrWhiteSpace(session.SessionId)) return;
            session.SessionId = Guid.CreateVersion7().ToString();
            _newSessionIds.Add(session.SessionId);
        }
    }

    private async Task<RuntimeSessionEntry> ResolveCoordinatorEntryAsync(SessionViewDescriptor session, SessionExecutionOptions options, bool history = false, bool ownedCommand = false, RuntimeFailureCapture? failureCapture = null, bool useExplicitPrompt = false)
    {
        ReserveSessionIdentity(session);
        ArgumentNullException.ThrowIfNull(options);
        string? preparedPrompt = null;
        while (true)
        {
            ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
            var actor = GetActorForWork(session.SessionId);
            var prepared = await actor.QueryAsync(
                actorCancellationToken => history && _entries.TryGetValue(session.SessionId, out var active) && !active.IsTerminated && !active.Attachment.IsRetiring
                    ? ValueTask.FromResult(new CoordinatorPreparation(active, null))
                    : EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken, ownedCommand, failureCapture, useExplicitPrompt, preparedPrompt),
                CancellationToken.None).ConfigureAwait(false);
            if (prepared.Entry is not null) return prepared.Entry;
            // Keep the pending prompt consumed by this request across its transition join.
            // Otherwise stale caller options immediately recreate the just-prepared attachment.
            preparedPrompt = prepared.SelectedPrompt ?? preparedPrompt;
            await prepared.Transition!.ConfigureAwait(false);
        }
    }

    // Actor prepare only: callers join the returned ticket outside the mailbox.
    private async ValueTask<CoordinatorPreparation> EnsureCoordinatorSessionCoreAsync(
        SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken actorCancellationToken, bool ownedCommand = false, RuntimeFailureCapture? failureCapture = null, bool useExplicitPrompt = false, string? preparedPrompt = null)
    {
        actorCancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
        if (_transitions.TryGetValue(session.SessionId, out var transition))
            return new CoordinatorPreparation(null, transition);
        _entries.TryGetValue(session.SessionId, out var existing);
        var prompt = (useExplicitPrompt ? null : NormalizeOptionalText(existing?.PendingAgentPromptId))
            ?? preparedPrompt ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId);
        // An attachment runs in one folder. A session whose worktree is gone, or that was given one, is attached
        // again where it works now; an attachment that is busy, or that this caller does not own, stays where it is.
        var settled = existing is not null && (existing.HasActiveRun || existing.QueueDrainInProgress || (ownedCommand && !HasOwnedCommandDefaults(existing)));
        var worktree = settled ? existing!.WorktreeDirectory : ExistingWorktree(session.WorktreeDirectory);
        bool Reusable(RuntimeSessionEntry entry) => entry.Matches(options, prompt) && string.Equals(entry.WorktreeDirectory, worktree, StringComparison.Ordinal)
            && UsesCurrentProvider(entry);
        // A provider whose settings were saved is registered again: an idle attachment is attached again, to run with
        // them. One that runs, or that another caller configured, keeps the provider it started with, and so does one
        // whose provider is no longer registered.
        bool UsesCurrentProvider(RuntimeSessionEntry entry)
        {
            if (entry.HasActiveRun || entry.QueueDrainInProgress || (ownedCommand && !HasOwnedCommandDefaults(entry)))
                return true;
            var current = _agentHub.GetProviderRegistrationVersion(entry.ProviderKey);
            return current == 0 || current == entry.ProviderRegistrationVersion;
        }
        if (ownedCommand && existing is not null && !Reusable(existing)
            && (existing.HasActiveRun || existing.QueueDrainInProgress || !HasOwnedCommandDefaults(existing)))
            throw new InvalidOperationException("Cannot change configuration of an active or externally owned attachment.");
        // Carry a matching but incompatible entry to send admission without consuming its pending
        // prompt or updating the descriptor. Admission rechecks under the actor and owns rejection.
        if (ownedCommand && existing is not null && !existing.Attachment.IsRetiring
            && !HasOwnedCommandDefaults(existing) && Reusable(existing))
            return new CoordinatorPreparation(existing, null);
        // Preserve validation/instruction-build-before-retirement behavior. The body is retained.
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ValidateDiscoveryPaths(session, options);
        var project = await ResolveProjectAsync(session, actorCancellationToken).ConfigureAwait(false);
        ValidateDiscoveryPaths(session, options, project);
        session.AgentPromptId = prompt;
        _instructionTemplateProvider.BuildCoordinatorInstructions(session, project, options.Model, prompt);
        if (existing is not null && !existing.Attachment.IsRetiring && Reusable(existing))
        {
            existing.PendingAgentPromptId = null;
            return new CoordinatorPreparation(existing, null);
        }
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirement = existing is null ? Task.CompletedTask : _forwarding.RetireAsync(existing.Attachment);
        Task? ticket = null;
        transition = _forwarding.RunAsync(async () =>
        {
            await launch.Task.ConfigureAwait(false);
            Exception? bodyFailure = null;
            try
            {
                await retirement.ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
                await CreateCoordinatorSessionAsync(session, options, ticket!, existing, prompt, CancellationToken.None, failureCapture).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                bodyFailure = failure;
                failureCapture?.Capture(failure);
                throw;
            }
            finally
            {
                try
                {
                await GetActorForWork(session.SessionId).QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, ticket))
                    {
                        _transitions.TryRemove(session.SessionId, out var completedTransition);
                        // Callbacks that arrive from now on count as activity of the published attachment.
                        if (_entries.TryGetValue(session.SessionId, out var published)) published.OpenActivity();
                    }
                    return ValueTask.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure) when (bodyFailure is not null)
                {
                    throw new AggregateException(bodyFailure, cleanupFailure);
                }
            }
        }, external: false);
        _transitions[session.SessionId] = transition;
        ticket = transition;
        launch.TrySetResult();
        return new CoordinatorPreparation(null, transition, prompt);
    }

    private sealed record CoordinatorPreparation(RuntimeSessionEntry? Entry, Task? Transition, string? SelectedPrompt = null);

    private async ValueTask<AgentSessionHandleId> CreateCoordinatorSessionAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        Task ticket,
        RuntimeSessionEntry? previousEntry,
        string? selectedPrompt,
        CancellationToken cancellationToken,
        RuntimeFailureCapture? failureCapture)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ValidateDiscoveryPaths(session, options);

        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);
        ValidateDiscoveryPaths(session, options, project);

        // The session works in its worktree while that folder exists. Once it is gone the session works in the
        // folder of its project again, and no longer records the worktree. The instructions name the folder.
        var recordedWorktree = NormalizeOptionalText(session.WorktreeDirectory);
        var worktree = ExistingWorktree(recordedWorktree);
        session.WorktreeDirectory = worktree;
        // What the session said and did so far names the folder that is gone: it is told where it works now.
        var worktreeGone = recordedWorktree is not null && worktree is null
            ? $"The git worktree this session worked in, `{recordedWorktree}`, is no longer there. The session now works in the folder of the project, `{options.WorkingDirectory}`: use that folder, and not the one that is gone."
            : null;
        var effectiveAgentPromptId = selectedPrompt;
        session.AgentPromptId = effectiveAgentPromptId;
        var instructions = _instructionTemplateProvider.BuildCoordinatorInstructions(session, project, options.Model, session.AgentPromptId);
        var agentPromptUsage = ResolveAgentPromptUsage(instructions.PromptBundle, project?.ProjectPath);
        var providerProviderId = new ModelProviderId(options.ProviderId.Value);
        var developerInstructions = instructions.DeveloperInstructions;
        var additionalDeveloperInstructions = AppendPromptPart(AppendPromptPart(BuildParentNotificationGuidance(session), worktreeGone), options.AdditionalDeveloperInstructions);
        var tools = options.Tools;

        AgentSessionHandleId sessionHandleId;
        bool startNewSession;
        lock (_identityGate) startNewSession = _newSessionIds.Remove(session.SessionId);

        // An attachment writes the title of the session. A session that starts, or that is not stored yet, takes the
        // title it is attached with. A stored session keeps its saved title, which is its name or, when it was never
        // named, what it was created with: a list names such a session by the first line of its summary, and that
        // line saved as its title would be taken for its name and no longer follow what the session says.
        var stored = startNewSession ? null : await StoredTitleAsync(session, cancellationToken).ConfigureAwait(false);
        var title = stored is { } kept ? kept.Saved : NormalizeOptionalText(session.Title);
        var shownTitle = stored?.Given ?? session.Title;
        var requestedSessionId = NormalizeOptionalText(session.SessionId);
        var systemMessage = AppendPromptPart(instructions.SystemMessage, options.AdditionalSystemMessage);
        var finalDeveloperInstructions = AppendPromptPart(developerInstructions, additionalDeveloperInstructions);
        IReadOnlyList<AgentInstructionTransformationInfo> instructionTransformations = [];
        var instructionsAlreadyComposed = false;
        if (options.InstructionProcessor is not null)
        {
            var processing = await options.InstructionProcessor(
                    new SessionInstructionProcessingRequest
                    {
                        SessionId = requestedSessionId,
                        ProjectId = project?.Id,
                        ProjectPath = project?.ProjectPath,
                        ProviderId = options.ProviderId.Value,
                        Model = options.Model,
                        SystemMessage = systemMessage,
                        DeveloperInstructions = finalDeveloperInstructions,
                        ActiveToolNames = (tools ?? []).Select(static tool => tool.Spec.Name).ToArray(),
                        Manifest = new Dictionary<string, string>
                        {
                            ["agentPromptId"] = NormalizeOptionalText(session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName,
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(processing.CancelReason))
            {
                throw new InvalidOperationException(processing.CancelReason);
            }

            systemMessage = processing.SystemMessage;
            finalDeveloperInstructions = processing.DeveloperInstructions;
            instructionTransformations = processing.Transformations;
            instructionsAlreadyComposed = instructionTransformations.Count > 0;
        }

        var sessionOptions = new AgentSessionResumeOptions
        {
            SessionId = requestedSessionId,
            ParentSessionId = NormalizeOptionalText(session.ParentSessionId),
            CreatedBySessionId = NormalizeOptionalText(session.CreatedBy?.SourceSessionId),
            // The journal header records this instant; the provider's own record must not name a later one.
            CreatedAt = session.CreatedAt == default ? null : session.CreatedAt,
            Title = title,
            ProviderKey = options.ProviderKey ?? session.ResolvedProviderKey,
            Model = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            Streaming = true,
            WorkingDirectory = options.WorkingDirectory,
            WorktreeDirectory = worktree,
            LeaveWorktree = worktree is null,
            ProjectRoots = options.ProjectRoots,
            SystemMessage = systemMessage,
            DeveloperInstructions = finalDeveloperInstructions,
            InstructionsAlreadyComposed = instructionsAlreadyComposed,
            InstructionTransformations = instructionTransformations,
            AgentPromptId = NormalizeOptionalText(session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName,
            AgentPromptUsage = agentPromptUsage,
            Tools = tools,
            OnPermissionRequest = options.OnPermissionRequest,
            OnUserInputRequest = options.OnUserInputRequest,
        };

        // Read before the runtime is created: a provider registered again in between is only attached again once more.
        var providerVersion = _agentHub.GetProviderRegistrationVersion(sessionOptions.ProviderKey);
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

        var attachment = _forwarding.RegisterAttachment(
            session.SessionId, sessionHandleId.ToString(),
            () => _agentHub.AbortAsync(sessionHandleId, CancellationToken.None),
            () => _agentHub.StopSessionAsync(sessionHandleId, CancellationToken.None));
        // A retirement before this assignment cannot have a bound owned send: publication is later.
        attachment.CloseOwnedPermissions = () => CloseOwnedQueueAttachmentAsync(attachment);
        try
        {
        session.ProviderId = options.ProviderId.Value;
        session.ProviderKey = options.ProviderKey ?? options.ProviderId.Value;
        session.WorkingDirectory = options.WorkingDirectory;
        session.WorktreeDirectory = worktree;
        session.ModelId = options.Model;
        session.ReasoningEffort = options.ReasoningEffort;
        session.AgentPromptId = effectiveAgentPromptId ?? session.AgentPromptId;
        await UpsertSessionMetadataAsync(session, options, title, cancellationToken).ConfigureAwait(false);
        var actor = GetActorForWork(session.SessionId);
        // Queue mutations share this mailbox. Keep the durable read/modify/append indivisible
        // with respect to them, without joining setup or retirement from inside the actor.
        await actor.QueryAsync(async actorCancellationToken =>
        {
            await UpdateSessionLocalStateAsync(session, actorCancellationToken).ConfigureAwait(false);
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
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
        var projector = new EventProjector(
            session.SessionId,
            runtimeEvent => _events.TryPublish(runtimeEvent),
            @event => entry?.ObserveEvent(@event));
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
            shownTitle,
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
            options.InstructionProcessor,
            projector,
            attachment)
        {
            WorktreeDirectory = worktree,
            ProviderRegistrationVersion = providerVersion,
        };

        projector.Entry = entry;
        var subscription = await _agentHub.SubscribeSessionEventsAsync(
                sessionHandleId,
                // Whether a callback counts as activity is decided when it arrives, not when the actor reaches it:
                // one delivered while the attachment is still being set up (a provider may call back from inside
                // the subscription itself) must not count or be skipped depending on how fast it is forwarded.
                @event => _ = PostAgentEventToActorAsync(actor, session.SessionId, projector, @event, entry.ActivityOpen),
                cancellationToken)
            .ConfigureAwait(false);
        attachment.InstallSubscription(subscription);
        attachment.CompleteSetup();
        await actor.QueryAsync(_ =>
        {
            ObjectDisposedException.ThrowIf(_forwarding.IsClosed || attachment.IsRetiring, this);
            if (!_transitions.TryGetValue(session.SessionId, out var currentTicket) || !ReferenceEquals(currentTicket, ticket))
                throw new InvalidOperationException("The coordinator transition no longer owns publication.");
            if (previousEntry is not null && !string.Equals(previousEntry.PendingAgentPromptId, selectedPrompt, StringComparison.Ordinal))
                entry.PendingAgentPromptId = previousEntry.PendingAgentPromptId;
            _entries[session.SessionId] = entry;
            return ValueTask.FromResult(true);
        }, CancellationToken.None).ConfigureAwait(false);

        return sessionHandleId;
        }
        catch (Exception failure)
        {
            failureCapture?.Capture(failure);
            // Signal setup before joining retirement: retirement may already be awaiting this record.
            attachment.CompleteSetup();
            try { await _forwarding.RetireAsync(attachment).ConfigureAwait(false); }
            catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
            throw;
        }
    }

    private async Task PublishRunSubmittedIfStillInFlightAsync(
        SessionViewDescriptor session,
        AgentRunId runId,
        DateTimeOffset runStartedAt,
        CancellationToken cancellationToken,
        RuntimeSessionEntry capturedEntry)
    {
        if (await MarkActiveRunIfStillInFlightAsync(session.SessionId, runId, runStartedAt, cancellationToken).ConfigureAwait(false))
        {
            PublishRunSubmittedEvent(session.SessionId, runId, runStartedAt);
        }

        async Task<bool> MarkActiveRunIfStillInFlightAsync(string sessionId, AgentRunId id, DateTimeOffset started, CancellationToken token)
            => await GetActorForWork(sessionId).QueryAsync(_ =>
                ValueTask.FromResult(capturedEntry.MarkActiveRunIfStillInFlight(id, started)), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<AgentRunId?> ClearCapturedRunAsync(RuntimeSessionEntry? entry)
        => entry is null ? null : await GetActorForWork(entry.SessionId).QueryAsync(_ =>
            ValueTask.FromResult(entry.ClearActiveRun()), CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Sends input to the coordinator session for a session.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<AgentRunId> SendAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        AgentSendOptions sendOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sendOptions);

        return await SendAsync(session, options, sendOptions, cancellationToken, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AgentRunId> SendAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        AgentSendOptions sendOptions,
        CancellationToken cancellationToken,
        CancellationToken coordinationCancellationToken)
        => await AdmitAsync(() => SendOwnedBodyAsync(session, options, sendOptions, cancellationToken, CancellationToken.None), coordinationCancellationToken)
            .WaitAsync(coordinationCancellationToken).ConfigureAwait(false);

    private async Task<AgentRunId> SendOwnedBodyAsync(
        SessionViewDescriptor session, SessionExecutionOptions options, AgentSendOptions sendOptions,
        CancellationToken cancellationToken, CancellationToken coordinationCancellationToken,
        SessionPermissionService.OwnedPermissionExecution? permissionExecution = null, bool ownedCommand = false,
        OwnedSessionAskExecution? askExecution = null, OwnedAskSubmission? askSubmission = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sendOptions);

        RuntimeSessionEntry? capturedEntry = null;
        OwnedProviderEventForwarding.Use? handleUse = null;
        CancellationTokenSource? execution = null;
        var failureCapture = new RuntimeFailureCapture();
        var executionReleased = false;
        var runInvocation = new OwnedSessionCommandService.OriginalInvocation();
        var permissionClose = new OwnedSessionCommandService.OriginalInvocation();
        var failures = new List<Exception>();
        AgentRunId result = default;
        var ownedDefaultsRejected = false;
        try
        {
            while (true)
            {
            var candidate = await ResolveCoordinatorEntryAsync(session, options, ownedCommand: ownedCommand, failureCapture: failureCapture).ConfigureAwait(false);
            GetActorForWork(session.SessionId);
            var sessionStateUpdated = false;
            var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(
                    async actorCancellationToken =>
                    {
                        await Task.CompletedTask.ConfigureAwait(false);
                        if (!_entries.TryGetValue(session.SessionId, out var current) || !ReferenceEquals(current, candidate)
                            || !candidate.Matches(options, NormalizeOptionalText(candidate.PendingAgentPromptId)
                                ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId)))
                            return default(AgentSessionHandleId);
                        handleUse = candidate.Attachment.TryAcquireHandleUse();
                        if (handleUse is null) return default(AgentSessionHandleId);
                        // Matches deliberately ignores callbacks. Reject before taking ownership of
                        // any run/start/prompt state; a different caller may already have an active run.
                        if (ownedCommand && !HasOwnedCommandDefaults(candidate))
                        {
                            ownedDefaultsRejected = true;
                            return default(AgentSessionHandleId);
                        }
                        candidate.PendingAgentPromptId = null;
                        capturedEntry = candidate;
                        session.MarkStarted(DateTimeOffset.UtcNow);
                        sessionStateUpdated = true;

                        return candidate.SessionHandleId;
                    },
                    coordinationCancellationToken)
                .ConfigureAwait(false);
            if (ownedDefaultsRejected)
                throw new InvalidOperationException("Owned command requires matching host-owned session defaults.");
            if (handleUse is null) continue;

            if (sessionStateUpdated)
            {
                PublishSessionCatalogEvent(session);
            }

            var runStartedAt = DateTimeOffset.UtcNow;
            execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, candidate.Attachment.Cancellation.Token);
            AgentRunId runId;
            try
            {
                if (permissionExecution is not null)
                {
                    if (!await Permissions.BindOwnedExecutionAsync(permissionExecution, _runtimeInstanceId,
                        candidate.Attachment, candidate.ProviderId).ConfigureAwait(false))
                        throw new OperationCanceledException("Owned permission execution cannot bind to this attachment.");
                    sendOptions = new AgentSendOptions
                    {
                        Input = sendOptions.Input,
                        AskId = sendOptions.AskId,
                        SourceSessionId = sendOptions.SourceSessionId,
                        AdditionalTools = sendOptions.AdditionalTools,
                        OnPermissionRequest = Permissions.CreateOwnedCommandHandler(permissionExecution),
                        OnUserInputRequest = Permissions.CreateOwnedUserInputHandler(permissionExecution),
                        EnableUserInputTool = permissionExecution.EnableUserInput,
                        RunLifecycle = OwnedSessionAskExecution.Combine(sendOptions.RunLifecycle, Permissions.CreateOwnedRunLifecycle(permissionExecution)),
                    };
                }
                if (askExecution is not null)
                {
                    askExecution.Bind(_runtimeInstanceId, candidate.Attachment.Ordinal, candidate.ProviderId);
                    sendOptions = askExecution.Compose(sendOptions,
                        includeTool: !candidate.Tools.Any(static tool => string.Equals(tool.Spec.Name, "alta", StringComparison.Ordinal)));
                }
                Task<AgentRunId>? runOriginal = null;
                runInvocation.Launch(() => runOriginal = RunCapturedAsync(sessionHandleId, sendOptions, execution.Token));
                if (await runInvocation.Outcome.ConfigureAwait(false) is { } runFailure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(runFailure);
                runId = await runOriginal!.ConfigureAwait(false);
                askSubmission?.RecordRunReturned(runId);
            }
            catch (Exception failure)
            {
                failureCapture.Capture(failure);
                throw;
            }
            await PublishRunSubmittedIfStillInFlightAsync(session, runId, runStartedAt, coordinationCancellationToken, candidate).ConfigureAwait(false);

            result = runId;
            break;
            }
        }
        catch (Exception failure)
        {
            failureCapture.Capture(failure);
            failures.Add(failure);
        }
        finally
        {
            // Close independent interaction controls before joining either, retaining the actual
            // permission original and its outcome. A failed close never releases the linked source.
            if (permissionExecution is not null) permissionClose.Launch(() => Permissions.CloseOwnedExecutionAsync(permissionExecution));
            try { askExecution?.Close(); }
            catch (Exception failure)
            {
                failures.Add(new AgentDependencyRetentionException("runtime send", "ask close", [failure], askExecution!));
            }
            if (permissionExecution is not null && await permissionClose.Outcome.ConfigureAwait(false) is { } closeFailure)
                failures.Add(permissionClose.Original is null
                    ? new AgentDependencyRetentionException("runtime send", "permission close launch", [closeFailure], permissionClose)
                    : closeFailure);
            if (!failures.Any(OwnedProviderEventForwarding.HasRetention))
            {
                try { execution?.Dispose(); executionReleased = true; }
                catch (Exception failure) { failures.Add(new AgentDependencyRetentionException("runtime send", "source release", [failure], execution!)); }
                if (!failures.Any(OwnedProviderEventForwarding.HasRetention))
                {
                    try { handleUse?.Dispose(); }
                    catch (Exception failure) { failures.Add(new AgentDependencyRetentionException("runtime send", "handle release", [failure], handleUse!)); }
                }
            }
        }
        if (failures.Count == 0) return result;
        Exception escaping = failures.Count == 1 ? failures[0] : new AggregateException(failures);
        if (OwnedProviderEventForwarding.HasRetention(escaping))
        {
            escaping = new AgentDependencyRetentionException("runtime send", "retained terminal prerequisites", failures,
                new { Runtime = this, Source = execution, Use = handleUse, Run = runInvocation, Close = permissionClose, Ask = askExecution, Permission = permissionExecution });
            handleUse?.Retain(escaping);
            _forwarding.RetainDependencies(escaping, this);
        }
        // A policy refusal still affects only the owned command receipt, never another caller's run.
        if (!ownedDefaultsRejected)
        {
            var original = failureCapture.Original ?? escaping;
            try
            {
                var activeRunId = await ClearCapturedRunAsync(capturedEntry).ConfigureAwait(false);
                if (original is OperationCanceledException)
                    PublishRunFinishedEvent(session.SessionId, activeRunId, SessionLifecycleEventKind.RunAborted, "Runtime run cancelled.", DateTimeOffset.UtcNow);
            }
            catch (Exception cleanupFailure) { escaping = new AggregateException(escaping, cleanupFailure); }
            var prerequisite = new LiveEventObservationPrerequisite(
                new { Runtime = this, Source = execution, Use = handleUse, Run = runInvocation, Close = permissionClose, Ask = askExecution, Permission = permissionExecution },
                executionReleased && (handleUse is null || handleUse.IsReleased), escaping);
            await ObserveRuntimeFailureAsync(session, original, escaping, prerequisite).ConfigureAwait(false);
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(escaping);
        return default;
    }

    internal Task<AgentRunId> SendOwnedCommandAsync(SessionViewDescriptor session, SessionExecutionOptions options,
        AgentSendOptions sendOptions, SessionPermissionService.OwnedPermissionExecution? permissionExecution, CancellationToken cancellationToken,
        OwnedSessionAskExecution? askExecution = null, OwnedAskSubmission? askSubmission = null)
        => AdmitAsync(() => SendOwnedBodyAsync(session, options, sendOptions, cancellationToken, CancellationToken.None, permissionExecution,
            ownedCommand: true, askExecution: askExecution, askSubmission: askSubmission), CancellationToken.None);

    // Fixed default-policy check only; per-operation association remains in the permission mailbox.
    private bool HasOwnedCommandDefaults(RuntimeSessionEntry candidate)
        => ReferenceEquals(candidate.OnPermissionRequest, Permissions.OwnedDefaultPermissionHandler)
            && ReferenceEquals(candidate.OnUserInputRequest, Permissions.OwnedDefaultUserInputHandler);

    private Task<AgentRunId> RunCapturedAsync(AgentSessionHandleId sessionHandleId, AgentSendOptions sendOptions, CancellationToken cancellationToken)
        => _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken);

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
        => await AdmitAsync(() => QueuePromptOwnedBodyAsync(session, prompt, kind, submittedBy, CancellationToken.None), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionViewQueuedPrompt> QueuePromptOwnedBodyAsync(SessionViewDescriptor session, string prompt, string kind,
        AltaActorProvenance? submittedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var actor = GetActorForWork(session.SessionId);
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
                        IsEnqueued: true)
                    { QueueKind = item.Kind });
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
    /// <exception cref="ArgumentException">Thrown when <paramref name="skillName"/> is empty or a supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the requested skill cannot be resolved.</exception>
    public async Task<AgentRunId> ActivateSkillAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        string skillName,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => ActivateSkillOwnedBodyAsync(session, options, skillName, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<AgentRunId> ActivateSkillOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options,
        string skillName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        var activation = await CreateSkillActivationAsync(session, options, skillName, cancellationToken).ConfigureAwait(false);

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
    /// Resolves a CodeAlta-managed skill activation payload for a session without starting an agent run.
    /// </summary>
    /// <param name="session">Target session.</param>
    /// <param name="options">Execution options used to resolve project-local skill roots.</param>
    /// <param name="skillName">Skill name to activate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved activation payload.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="skillName"/> is empty or a supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the requested skill cannot be resolved.</exception>
    public async Task<SkillActivation> CreateSkillActivationAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        string skillName,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => CreateSkillActivationOwnedBodyAsync(session, options, skillName, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SkillActivation> CreateSkillActivationOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options,
        string skillName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        ValidateDiscoveryPaths(session, options);
        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);
        var query = BuildSkillCatalogQuery(project, options.ProjectRoots);
        return await _skillCatalog.ActivateAsync(query, skillName, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Skill '{skillName}' was not found or is not activatable for this session.");
    }

    /// <summary>
    /// Steers the current coordinator run for a session.
    /// </summary>
    public async Task<AgentRunId> SteerAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        AgentSteerOptions steerOptions,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => SteerOwnedBodyAsync(session, options, steerOptions, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<AgentRunId> SteerOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options,
        AgentSteerOptions steerOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(steerOptions);

        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
        GetActorForWork(session.SessionId);
        var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(
                async actorCancellationToken =>
                {
                    var entry = await GetActiveRuntimeSessionForSteeringAsync(session, options, actorCancellationToken).ConfigureAwait(false);
                    handleUse = entry.Attachment.TryAcquireHandleUse()
                        ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                    return entry.SessionHandleId;
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handleUse!.Attachment.Cancellation.Token);
        return await SteerCapturedAsync(sessionHandleId, steerOptions, execution.Token).ConfigureAwait(false);
        }
        finally { handleUse?.Dispose(); }
    }

    private async Task<AgentRunId> SteerCapturedAsync(AgentSessionHandleId sessionHandleId, AgentSteerOptions steerOptions, CancellationToken cancellationToken)
    {
        return await _agentHub.SteerAsync(sessionHandleId, steerOptions, cancellationToken).ConfigureAwait(false);
    }

    // Only the command owner supplies execution cancellation; transport cancellation never reaches
    // accepted work. This path cannot acquire a coordinator or substitute a newly observed target.
    internal Task<AgentRunId> SteerOwnedCommandAsync(OwnedTextSteerRequest request, CancellationToken executionCancellationToken)
        => AdmitAsync(() => SteerOwnedCommandBodyAsync(request, executionCancellationToken), CancellationToken.None);

    private async Task<AgentRunId> SteerOwnedCommandBodyAsync(OwnedTextSteerRequest request, CancellationToken executionCancellationToken)
    {
        executionCancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedRuntimeInstanceId != _runtimeInstanceId || !_sessionActors.TryGet(request.SessionId, out var actor))
            throw new InvalidOperationException("The observed steering target is unavailable.");
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            var handle = await actor.QueryAsync(_ =>
            {
                if (_transitions.ContainsKey(request.SessionId) || !_entries.TryGetValue(request.SessionId, out var entry)
                    || entry.IsTerminated || entry.Attachment.Ordinal != request.ExpectedAttachmentGeneration
                    || entry.ActiveRunId is not { } activeRun || !string.Equals(activeRun.Value, request.ExpectedRunId, StringComparison.Ordinal)
                    || !HasOwnedCommandDefaults(entry))
                    throw new InvalidOperationException("The observed steering target is stale or is not owned.");
                handleUse = entry.Attachment.TryAcquireHandleUse()
                    ?? throw new InvalidOperationException("The observed steering attachment is retiring.");
                return ValueTask.FromResult(entry.SessionHandleId);
            }, CancellationToken.None).ConfigureAwait(false);
            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });
            var expectedRun = new AgentRunId(request.ExpectedRunId);
            var returnedRun = await lifetime.RunAsync(token => SteerCapturedAsync(handle, new AgentSteerOptions
            {
                Input = AgentInput.Text(request.Text),
                ExpectedRunId = expectedRun,
            }, token), executionCancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);
            if (returnedRun != expectedRun)
                throw new InvalidOperationException("The provider returned a different steering target.");
            return returnedRun;
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            _forwarding.RetainDependencies(failure, this);
            throw;
        }
        finally { handleUse?.Dispose(); }
    }

    // Recorded idleness only permits a provider attempt. The provider gate proves idle against
    // intervening sends; neither this route nor its mailbox query publishes a started event.
    internal Task<AgentCompactionOutcome?> CompactOwnedCommandAsync(OwnedCompactRequest request, CancellationToken executionCancellationToken)
        => AdmitAsync(() => CompactOwnedCommandBodyAsync(request, executionCancellationToken), CancellationToken.None);

    private async Task<AgentCompactionOutcome?> CompactOwnedCommandBodyAsync(OwnedCompactRequest request, CancellationToken executionCancellationToken)
    {
        executionCancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedRuntimeInstanceId != _runtimeInstanceId || !_sessionActors.TryGet(request.SessionId, out var actor))
            throw new InvalidOperationException("The observed compaction target is unavailable.");
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            var handle = await actor.QueryAsync(_ =>
            {
                if (_transitions.ContainsKey(request.SessionId) || !_entries.TryGetValue(request.SessionId, out var entry)
                    || entry.IsTerminated || entry.Attachment.Ordinal != request.ExpectedAttachmentGeneration || !HasOwnedCommandDefaults(entry))
                    throw new InvalidOperationException("The observed compaction target is stale or is not owned.");
                if (entry.ActiveRunId is not null || entry.QueueDrainInProgress)
                    return ValueTask.FromResult<AgentSessionHandleId?>(null);
                handleUse = entry.Attachment.TryAcquireHandleUse()
                    ?? throw new InvalidOperationException("The observed compaction attachment is retiring.");
                return ValueTask.FromResult<AgentSessionHandleId?>(entry.SessionHandleId);
            }, CancellationToken.None).ConfigureAwait(false);
            if (handle is null) return null;
            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });
            return await lifetime.RunAsync(token => _agentHub.TryCompactWhenIdleAsync(handle.Value, token),
                executionCancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            _forwarding.RetainDependencies(failure, this);
            throw;
        }
        finally { handleUse?.Dispose(); }
    }

    /// <summary>
    /// Asks the provider of a session to stop one of its background tasks. The end of the task is told by the
    /// events of the session; a task that already ended is not an error.
    /// </summary>
    /// <param name="sessionId">The session, which must be attached in this runtime.</param>
    /// <param name="taskId">The identity of the task, as the state of the session lists it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the provider took the request; false when the session is not attached, or its provider has no such tasks.</returns>
    /// <exception cref="ArgumentException">An identity is blank.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    public Task<bool> StopBackgroundTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return AdmitAsync(() => StopBackgroundTaskBodyAsync(sessionId, taskId, cancellationToken), cancellationToken);
    }

    private async Task<bool> StopBackgroundTaskBodyAsync(string sessionId, string taskId, CancellationToken cancellationToken)
    {
        if (!_sessionActors.TryGet(sessionId, out var actor)) return false;
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            // A task goes on outside the runs: neither a run nor a queue that drains holds this back.
            var handle = await actor.QueryAsync(_ =>
            {
                if (_transitions.ContainsKey(sessionId) || !_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated
                    || !entry.BackgroundTasks.Any(task => task.Outcome is null && string.Equals(task.TaskId, taskId, StringComparison.Ordinal)))
                    return ValueTask.FromResult<AgentSessionHandleId?>(null);
                handleUse = entry.Attachment.TryAcquireHandleUse();
                return ValueTask.FromResult(handleUse is null ? (AgentSessionHandleId?)null : entry.SessionHandleId);
            }, CancellationToken.None).ConfigureAwait(false);
            if (handle is null) return false;
            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });
            return await lifetime.RunAsync(token => _agentHub.StopBackgroundTaskAsync(handle.Value, taskId, token),
                cancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            _forwarding.RetainDependencies(failure, this);
            throw;
        }
        finally { handleUse?.Dispose(); }
    }

    internal Task<AgentTargetedAbortOutcome?> AbortRunOwnedCommandAsync(OwnedAbortRunRequest request, CancellationToken executionCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return AdmitAsync(() => AbortRunOwnedCommandBodyAsync(request, executionCancellationToken), CancellationToken.None);
    }

    private async Task<AgentTargetedAbortOutcome?> AbortRunOwnedCommandBodyAsync(OwnedAbortRunRequest request, CancellationToken executionCancellationToken)
    {
        executionCancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedRuntimeInstanceId != _runtimeInstanceId || !_sessionActors.TryGet(request.SessionId, out var actor)) return null;
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            var handle = await actor.QueryAsync(_ =>
            {
                if (_transitions.ContainsKey(request.SessionId) || !_entries.TryGetValue(request.SessionId, out var entry)
                    || entry.IsTerminated || entry.QueueDrainInProgress || entry.Attachment.Ordinal != request.ExpectedAttachmentGeneration
                    || !HasOwnedCommandDefaults(entry)) return ValueTask.FromResult<AgentSessionHandleId?>(null);
                handleUse = entry.Attachment.TryAcquireHandleUse();
                return ValueTask.FromResult(handleUse is null ? (AgentSessionHandleId?)null : entry.SessionHandleId);
            }, CancellationToken.None).ConfigureAwait(false);
            if (handle is null) return null;
            // Event-derived ActiveRunId is not admission authority. Only the captured provider can
            // atomically validate the unchanged expected run; no permission invalidation occurs here.
            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });
            return await lifetime.RunAsync(token => _agentHub.AbortRunAsync(handle.Value, new AgentRunId(request.ExpectedRunId), token),
                executionCancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            _forwarding.RetainDependencies(failure, this);
            throw;
        }
        finally { handleUse?.Dispose(); }
    }

    /// <summary>Captures immutable current-runtime facts for one session without discovery or acquisition.</summary>
    /// <param name="sessionId">The nonblank durable session identifier.</param>
    /// <param name="cancellationToken">Cancels admission or the caller's wait, not already admitted runtime-owned work.</param>
    /// <returns>A point-in-time observation; a missing entry is explicitly absent, not idle or completed.</returns>
    /// <remarks>
    /// Uses the existing session actor when present; never creates an actor, coordinator or provider, and
    /// never reads catalogs, journals or Display. Entry and transition facts are copied synchronously on
    /// that actor; attachment retirement is read through its existing owner gate. Missing-actor absence
    /// is observed at registry lookup. The result can become stale immediately. Runtime instance and
    /// attachment generation identify ownership, not state revisions or effect watermarks. Consumers
    /// must fence obsolete selection/request results themselves. Queue depth, provider quiescence,
    /// history recovery and an atomic history/original-stream handshake are not provided.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The session identifier is null.</exception>
    /// <exception cref="ArgumentException">The session identifier is empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">Admission or the caller's wait was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The runtime or session actor no longer admits queries.</exception>
    public async Task<SessionRuntimeCurrentState> GetCurrentStateAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetCurrentStateOwnedBodyAsync(sessionId), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionRuntimeCurrentState> GetCurrentStateOwnedBodyAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_sessionActors.TryGet(sessionId, out var actor))
            return new(_runtimeInstanceId, sessionId, false, null);

        return await actor.QueryAsync(_ =>
        {
            return ValueTask.FromResult(CaptureCurrentState(sessionId));
        }, CancellationToken.None).ConfigureAwait(false);
    }

    // Called only on the existing session actor.
    private SessionRuntimeCurrentState CaptureCurrentState(string sessionId)
    {
        SessionRuntimeCurrentEntry? snapshot = null;
        if (_entries.TryGetValue(sessionId, out var entry))
            snapshot = new(entry.Attachment.Ordinal, entry.IsTerminated, entry.Attachment.IsRetiring,
                entry.ActiveRunId?.Value, entry.QueueDrainInProgress, entry.ProviderId.Value, entry.ProviderKey,
                entry.Model, entry.ReasoningEffort, entry.AgentPromptId, entry.PendingAgentPromptId)
            { Activity = new(entry.ActivityTimestamp, entry.ActivityEvents, entry.OmittedActivityEvents), BackgroundTasks = entry.BackgroundTasks };
        return new(_runtimeInstanceId, sessionId, _transitions.ContainsKey(sessionId), snapshot);
    }

    /// <summary>
    /// Reads the latest bounded, typed usage event admitted by the existing attachment's actor.
    /// Does not activate a session, read history, probe a provider or establish completeness/freshness.
    /// </summary>
    /// <param name="sessionId">Exact session identifier to inspect.</param>
    /// <param name="cancellationToken">Cancels the caller's wait, not work already admitted to the actor.</param>
    /// <returns>Attachment-scoped last-observed usage or explicit absence/transition state.</returns>
    /// <exception cref="ArgumentNullException">The session identifier is null.</exception>
    /// <exception cref="ArgumentException">The session identifier is blank.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closed.</exception>
    /// <exception cref="OperationCanceledException">The caller's wait is canceled.</exception>
    public async Task<SessionRuntimeUsageState> GetUsageStateAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetUsageStateOwnedBodyAsync(sessionId), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionRuntimeUsageState> GetUsageStateOwnedBodyAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_sessionActors.TryGet(sessionId, out var actor))
            return new(_runtimeInstanceId, sessionId, false, null, false, false, null, 0);

        return await actor.QueryAsync(_ =>
        {
            var transitioning = _transitions.ContainsKey(sessionId);
            if (!_entries.TryGetValue(sessionId, out var entry))
                return ValueTask.FromResult(new SessionRuntimeUsageState(_runtimeInstanceId, sessionId,
                    transitioning, null, false, false, null, 0));
            var retiring = entry.Attachment.IsRetiring;
            return ValueTask.FromResult(new SessionRuntimeUsageState(_runtimeInstanceId, sessionId,
                transitioning, entry.Attachment.Ordinal, retiring, entry.IsTerminated,
                transitioning || retiring || entry.IsTerminated ? null : entry.LastObservedUsage,
                entry.OmittedUsageEvents));
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the sessions that have a run in flight right now. It is a reading for the user (a question
    /// before the application exits), taken without waiting for any session: a run that starts or ends at
    /// this instant may or may not be counted.
    /// </summary>
    public int CountActiveRuns() => _entries.Values.Count(static entry => !entry.IsTerminated && entry.HasActiveRun);

    /// <summary>
    /// Lists what the sessions this runtime holds are doing right now: whether each runs, whether its provider
    /// works in the background, and whether its last run failed. Like <see cref="CountActiveRuns"/> it is a
    /// reading for the user, taken without waiting for any session. A session the runtime does not hold (one
    /// that was not opened or sent to since the application started) is not listed.
    /// </summary>
    /// <returns>One entry for each session the runtime holds, in no particular order.</returns>
    public IReadOnlyList<SessionRuntimeOverview> ListOverview()
        => [.. _entries.Values
            .Where(static entry => !entry.IsTerminated)
            .Select(static entry => new SessionRuntimeOverview(entry.SessionId, entry.ProjectId, entry.Title,
                entry.HasActiveRun || entry.QueueDrainInProgress, entry.BackgroundTasks.Count(static task => task.Outcome is null), entry.LastRunFailed))];

    /// <summary>
    /// Lists the sessions that are at work right now, each with the folder it works in: its worktree when it has
    /// one, the folder of its project otherwise. Like <see cref="CountActiveRuns"/> it is a reading taken without
    /// waiting for any session; it answers whether a checkout can be removed or moved to another branch.
    /// </summary>
    public IReadOnlyList<SessionWorkFolder> ListBusySessionFolders()
        => [.. _entries.Values
            .Where(static entry => !entry.IsTerminated && (entry.HasActiveRun || entry.QueueDrainInProgress))
            .Select(static entry => new SessionWorkFolder(entry.SessionId, entry.WorktreeDirectory ?? entry.WorkingDirectory, entry.WorktreeDirectory is not null))];

    /// <summary>
    /// Returns whether the session's active coordinator session has an in-flight run.
    /// </summary>
    public async Task<bool> HasActiveRunAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => HasActiveRunOwnedBodyAsync(session, CancellationToken.None), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> HasActiveRunOwnedBodyAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
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
        => await AdmitAsync(() => HasActiveCoordinatorSessionOwnedBodyAsync(sessionId, CancellationToken.None), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> HasActiveCoordinatorSessionOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
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
        cancellationToken.ThrowIfCancellationRequested();
        if (_forwarding.IsClosed) return;
        await AdmitAsync(() => AbortOwnedBodyAsync(sessionId, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AbortOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_disposed)
        {
            return;
        }

        SessionActorCommandResult result;
        RuntimeSessionEntry? capturedEntry = null;
        OwnedProviderEventForwarding.Use? handleUse = null;
        var admission = new OwnedSessionCommandService.OriginalInvocation();
        RuntimeAbortLifetime? lifetime = null;
        try
        {
            var actor = GetActorForWork(sessionId);
            result = await admission.RunAsync(() => actor.ExecuteReservedAsync(
                    async actorCancellationToken =>
                    {
                        var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);
                        handleUse = entry.Attachment.TryAcquireHandleUse()
                            ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                        capturedEntry = entry;
                    },
                    CancellationToken.None).AsTask())
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    result.Message ?? $"Failed to abort session '{sessionId}'.", result.Exception);
            }
            lifetime = new RuntimeAbortLifetime(new { Runtime = this, Entry = capturedEntry, Use = handleUse, Admission = admission });
            await lifetime.RunAsync(() => Permissions.InvalidateOwnedAttachmentAsync(capturedEntry!.Attachment),
                token => _agentHub.AbortAsync(capturedEntry!.SessionHandleId, token),
                cancellationToken, capturedEntry!.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            var dependencies = new { Runtime = this, Entry = capturedEntry, Use = handleUse, Admission = admission, Lifetime = lifetime };
            var retained = new AgentDependencyRetentionException("runtime abort", "retained use", [failure], dependencies);
            handleUse?.Retain(retained);
            _forwarding.RetainDependencies(retained, dependencies);
            throw retained;
        }
        catch (Exception ex) when (_disposed && ex is ObjectDisposedException or ChannelClosedException)
        {
            return;
        }
        finally { handleUse?.Dispose(); }
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

    private void ValidateDiscoveryPaths(SessionViewDescriptor? session, SessionExecutionOptions? options, ProjectDescriptor? project = null)
    {
        if (_discoveryScope is null)
        {
            return;
        }

        if (session?.WorkingDirectory is not null)
        {
            _discoveryScope.ValidateProjectPath(session.WorkingDirectory, nameof(session.WorkingDirectory));
        }

        if (options is not null)
        {
            _discoveryScope.ValidateProjectPath(options.WorkingDirectory, nameof(options.WorkingDirectory));
            foreach (var root in options.ProjectRoots)
            {
                _discoveryScope.ValidateProjectPath(root, nameof(options.ProjectRoots));
            }
        }

        ValidateDiscoveryProjectRoot(project?.ProjectPath);
    }

    private void ValidateDiscoveryProjectRoot(string? projectRoot)
    {
        if (_discoveryScope is not null && projectRoot is not null)
        {
            _discoveryScope.ValidateProjectPath(projectRoot, nameof(projectRoot));
        }
    }

    private SkillCatalogQuery BuildSkillCatalogQuery(ProjectDescriptor? project, IReadOnlyList<string> projectRoots)
    {
        if (_discoveryScope is not null)
        {
            ValidateDiscoveryProjectRoot(project?.ProjectPath);
            foreach (var root in projectRoots)
            {
                _discoveryScope.ValidateProjectPath(root, nameof(projectRoots));
            }
        }

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
                UserProfileRoot = _discoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
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
        => await AdmitAsync(() => DetachOwnedBodyAsync(sessionId), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> DetachOwnedBodyAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var actor = GetActorForWork(sessionId);
        while (true)
        {
            var preparation = await actor.QueryAsync(_ =>
            {
                if (_transitions.TryGetValue(sessionId, out var current))
                    return ValueTask.FromResult((Claimed: false, Work: (Task?)current));
                if (!_entries.TryGetValue(sessionId, out var entry))
                    return ValueTask.FromResult((Claimed: false, Work: (Task?)null));
                var retirement = _forwarding.RetireAsync(entry.Attachment);
                var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? ticket = null;
                var work = _forwarding.RunAsync(async () =>
                {
                    await launch.Task.ConfigureAwait(false);
                    try { await retirement.ConfigureAwait(false); }
                    finally
                    {
                        await actor.QueryAsync(token =>
                        {
                            if (_entries.TryGetValue(sessionId, out var active) && ReferenceEquals(active, entry))
                                _entries.TryRemove(sessionId, out var removedEntry);
                            if (_transitions.TryGetValue(sessionId, out var currentTicket) && ReferenceEquals(currentTicket, ticket))
                                _transitions.TryRemove(sessionId, out var removedTransition);
                            return ValueTask.FromResult(true);
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                }, external: false);
                _transitions[sessionId] = work;
                ticket = work;
                launch.TrySetResult();
                return ValueTask.FromResult((Claimed: true, Work: (Task?)work));
            }, CancellationToken.None).ConfigureAwait(false);
            if (preparation.Work is null) return false;
            await preparation.Work.ConfigureAwait(false);
            if (preparation.Claimed) return true;
        }
    }

    /// <summary>
    /// Triggers a manual compaction for a session coordinator session.
    /// </summary>
    public async Task CompactAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => CompactOwnedBodyAsync(session, options, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task CompactOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        ReserveSessionIdentity(session);
        GetActorForWork(session.SessionId);
        var result = await _sessionActors.GetOrCreate(session.SessionId).ExecuteAsync(
                async actorCancellationToken =>
                {
                    _events.TryPublish(new SessionHostEvent(
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        AgentSessionUpdateKind.CompactionStarted,
                        $"Manual compaction requested for '{session.Title}'."));

                    await Task.CompletedTask.ConfigureAwait(false);
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                result.Message ?? $"Failed to compact session '{session.SessionId}'.",
                result.Exception);
        }

        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            while (handleUse is null)
            {
                var entry = await ResolveCoordinatorEntryAsync(session, options).ConfigureAwait(false);
                handleUse = await GetActorForWork(session.SessionId).QueryAsync(_ =>
                {
                    var use = _entries.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, entry)
                        && entry.Matches(options, NormalizeOptionalText(entry.PendingAgentPromptId)
                            ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId))
                        ? entry.Attachment.TryAcquireHandleUse() : null;
                    if (use is not null) entry.PendingAgentPromptId = null;
                    return ValueTask.FromResult(use);
                }, CancellationToken.None).ConfigureAwait(false);
                if (handleUse is null) continue;
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Attachment.Cancellation.Token);
                var outcome = await _agentHub.CompactAsync(entry.SessionHandleId, execution.Token).ConfigureAwait(false);
                if (outcome is not null && ShouldPublishHostCompactionOutcome(outcome))
                {
                    _events.TryPublish(new SessionHostEvent(
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        AgentSessionUpdateKind.CompactionCompleted,
                        outcome.Message ?? (outcome.Success ? "Manual compaction completed." : "Manual compaction failed.")));
                }
            }
        }
        finally { handleUse?.Dispose(); }
    }

    private static bool ShouldPublishHostCompactionOutcome(AgentCompactionOutcome outcome)
        => outcome is
        {
            MessagesRemoved: null,
            TokensRemoved: null,
            PreCompactionTokens: null,
            PostCompactionTokens: null,
        };

    /// <summary>
    /// Gets sanitized history for a session, reusing an active coordinator session when present.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="options">Execution options used to resume the coordinator session when it is not already active.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sanitized session event history.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session" /> or <paramref name="options" /> is <see langword="null" />.</exception>
    /// <remarks>
    /// History loading is a read operation. When a coordinator session is already attached, this method intentionally
    /// does not compare or apply <paramref name="options" /> because replacing a live attachment can cancel in-flight work.
    /// </remarks>
    public async Task<IReadOnlyList<AgentEvent>> GetOrResumeHistoryAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetOrResumeHistoryOwnedBodyAsync(session, options, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<AgentEvent>> GetOrResumeHistoryOwnedBodyAsync(
        SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        while (true)
        {
            var entry = await ResolveCoordinatorEntryAsync(session, options, history: true).ConfigureAwait(false);
            var use = await GetActorForWork(session.SessionId).QueryAsync(_ =>
                ValueTask.FromResult(_entries.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, entry)
                    ? entry.Attachment.TryAcquireHandleUse() : null), CancellationToken.None).ConfigureAwait(false);
            if (use is null) continue;
            using (use)
            using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Attachment.Cancellation.Token))
                return await GetProjectedHistoryAsync(entry, execution.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets sanitized history for an active session.
    /// </summary>
    public async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetHistoryOwnedBodyAsync(sessionId, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<AgentEvent>> GetHistoryOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        var actor = GetActorForWork(sessionId);
        var selected = await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);
                    var use = entry.Attachment.TryAcquireHandleUse()
                        ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                    return (Entry: entry, Use: use);
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        using (selected.Use)
        using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, selected.Entry.Attachment.Cancellation.Token))
            return await GetProjectedHistoryAsync(selected.Entry, execution.Token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AgentEvent>> GetProjectedHistoryAsync(
        RuntimeSessionEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var history = await _agentHub.GetSessionHistoryAsync(entry.SessionHandleId, cancellationToken).ConfigureAwait(false);
        return await GetActorForWork(entry.SessionId).QueryAsync(async _ =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return entry.Projector.ProjectHistory(history);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_identityGate)
        {
        _disposed = true;
        return new ValueTask(_forwarding.CloseAsync(
            () => Permissions.DisposeAsync().AsTask(),
            async () =>
            {
                await _sessionActors.DisposeAsync().ConfigureAwait(false);
                _entries.Clear();
            }, _events.Complete));
        }
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

    private Task PostAgentEventToActorAsync(
        SessionActor actor,
        string sessionId,
        EventProjector projector,
        AgentEvent @event,
        bool arrivedOnOpenActivity)
        => _forwarding.Forward(projector.Entry!.Attachment,
            use => PostAgentEventToActorCoreAsync(actor, sessionId, projector, @event, arrivedOnOpenActivity, use));

    private async Task PostAgentEventToActorCoreAsync(
        SessionActor actor, string sessionId, EventProjector projector, AgentEvent @event, bool arrivedOnOpenActivity,
        OwnedProviderEventForwarding.Use projectionUse)
    {
        var projectId = projector.Entry!.ProjectId;
        var workingDirectory = projector.Entry.WorkingDirectory;
        AgentEvent? published = null;
        IReadOnlyList<ParentNotificationWork> notifications = [];
        var actorChoresCompleted = false;
        var publication = new LiveEventPublication(new { Runtime = this, Projector = projector, Use = projectionUse });
        try
        {
            await publication.CompleteAsync(mark => actor.QueryAsync(_ =>
                {
                    if (arrivedOnOpenActivity) ObserveAdmittedActivity(sessionId, projector.Entry!, @event);
                    var sanitized = projector.Project(@event);
                    ObserveAdmittedUsage(sessionId, projector.Entry!, sanitized);
                    published = sanitized;
                    if (sanitized is not null)
                        mark(CapturePluginEvent(sanitized, sessionId, projectId, workingDirectory));
                    RefuseUnavailableOwnedQueue(sessionId);
                    notifications = projector.Entry!.TakeParentNotifications(sanitized);
                    actorChoresCompleted = true;
                    return ValueTask.FromResult(true);
                }).AsTask(), projectionUse.Dispose, ObserveLivePluginEventAsync, () =>
            {
                var effects = new List<Func<Task>>();
                if (published is not null)
                    effects.Add(() => InvalidateFileSearchCacheAsync(published, workingDirectory).AsTask());
                foreach (var notification in notifications)
                    effects.Add(() => DeliverParentNotificationAsync(notification));
                if (actorChoresCompleted && IsQueueDrainTrigger(@event))
                    effects.Add(() => TryDrainNextQueuedPromptAsync(sessionId));
                publication.IndependentWork = new LiveEventIndependentWork(effects);
                return publication.IndependentWork.RunAsync();
            }, (failure, dependencies) =>
            {
                projectionUse.Retain(failure);
                _forwarding.RetainDependencies(failure, dependencies);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (publication.CanToleratePrepublicationFailure)
        {
        }
        catch (InvalidOperationException) when (publication.CanToleratePrepublicationFailure)
        {
        }
        catch (OperationCanceledException) when (_disposed && publication.CanToleratePrepublicationFailure)
        {
        }
        catch (IOException) when (publication.CanToleratePrepublicationFailure)
        {
        }
        catch (UnauthorizedAccessException) when (publication.CanToleratePrepublicationFailure)
        {
        }
    }

    // Actor-only, using the existing admitted callback path before projection (including Shutdown).
    // Like the TUI event reducer, arrival order can move the timestamp backwards. No wall-clock fallback.
    private void ObserveAdmittedActivity(string sessionId, RuntimeSessionEntry entry, AgentEvent @event)
    {
        if (!_entries.TryGetValue(sessionId, out var active) || !ReferenceEquals(active, entry)
            || _transitions.ContainsKey(sessionId) || entry.Attachment.IsRetiring || entry.IsTerminated) return;
        if (@event.SessionId != sessionId || @event.ProviderId != entry.ProviderId || @event.Timestamp.Year <= 1)
        {
            entry.OmittedActivityEvents = entry.OmittedActivityEvents == long.MaxValue ? long.MaxValue : entry.OmittedActivityEvents + 1;
            return;
        }
        entry.ActivityTimestamp = @event.Timestamp;
        entry.ActivityEvents = entry.ActivityEvents == long.MaxValue ? long.MaxValue : entry.ActivityEvents + 1;
    }

    // Actor-only; do not alter the existing event stream/journal path for nonmatching provider callbacks.
    private void ObserveAdmittedUsage(string sessionId, RuntimeSessionEntry entry, AgentEvent? @event)
    {
        if (@event is not AgentSessionUpdateEvent { Usage: not null } update
            || !_entries.TryGetValue(sessionId, out var active) || !ReferenceEquals(active, entry)
            || _transitions.ContainsKey(sessionId) || entry.Attachment.IsRetiring || entry.IsTerminated)
            return;
        if (!string.Equals(update.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(update.ProviderId.Value, entry.ProviderId.Value, StringComparison.Ordinal))
        {
            entry.OmittedUsageEvents = entry.OmittedUsageEvents == long.MaxValue ? long.MaxValue : entry.OmittedUsageEvents + 1;
            return;
        }
        entry.UsageSequence = entry.UsageSequence == long.MaxValue ? long.MaxValue : entry.UsageSequence + 1;
        entry.LastObservedUsage = SessionRuntimeUsageObservation.FromEvent(entry.UsageSequence, update);
    }

    private static bool IsQueueDrainTrigger(AgentEvent @event)
        => @event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }
            or AgentErrorEvent;

    private async Task TryDrainNextQueuedPromptAsync(string sessionId, SessionActor? existingActor = null, OwnedQueuedExecution? ownedTrigger = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var work = await TryMarkNextQueuedPromptSubmittingAsync(sessionId, existingActor, ownedTrigger).ConfigureAwait(false);
        if (work is null)
        {
            return;
        }

        if (work.Owned is { } owned)
        {
            // The runtime-admitted queue body owns execution and cleanup, not this event tail.
            // The claim was published atomically with the slot in the mailbox.
            await owned.Drained.Task.ConfigureAwait(false);
            await TryDrainNextQueuedPromptAsync(sessionId, existingActor).ConfigureAwait(false);
            return;
        }

        try
        {
            // The legacy arm always holds a cloned durable prompt; only the owned arm above has null.
            var runStartedAt = DateTimeOffset.UtcNow;
            var runId = await _agentHub.RunAsync(
                    work.SessionHandleId,
                    new AgentSendOptions { Input = AgentInput.Text(work.Prompt!.Prompt), SourceSessionId = AgentSource(work.Prompt.SubmittedBy) },
                    work.Entry.Attachment.Cancellation.Token)
                .ConfigureAwait(false);
            await MarkQueuedPromptSubmittedAsync(work.Entry, work.Prompt!.QueueItemId, runId, runStartedAt, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (OwnedProviderEventForwarding.HasRetention(ex))
            {
                work.Use.Retain(ex);
                _forwarding.RetainDependencies(ex, work);
            }
            try { await MarkQueuedPromptFailedAsync(work.Entry, work.Prompt!.QueueItemId, ex.Message, DateTimeOffset.UtcNow).ConfigureAwait(false); }
            catch (Exception cleanupFailure) { throw new AggregateException(ex, cleanupFailure); }
            if (OwnedProviderEventForwarding.HasRetention(ex)) throw;
        }
        finally { work.Use.Dispose(); }

        // A fast run can publish Idle before RunAsync returns and before the submitting item is
        // marked submitted. Probe the queue again after clearing QueueDrainInProgress so later
        // queued prompts are not left waiting for a terminal event that already happened.
        await TryDrainNextQueuedPromptAsync(sessionId, existingActor).ConfigureAwait(false);
    }

    private async Task<QueuedPromptDrainWork?> TryMarkNextQueuedPromptSubmittingAsync(string sessionId,
        SessionActor? existingActor = null, OwnedQueuedExecution? ownedTrigger = null)
    {
        var actor = existingActor ?? GetActorForWork(sessionId);
        while (!_disposed)
        {
        Task? transition = null;
        var work = await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    RefuseUnavailableOwnedQueue(sessionId);
                    // A rejected/stale immediate owned trigger is not authority to begin legacy
                    // replacement. Ordinary event/completion drain opportunities remain unchanged.
                    if (ownedTrigger is not null && (!_entries.TryGetValue(sessionId, out var target)
                        || !ReferenceEquals(target.OwnedQueue, ownedTrigger) || !CanUseOwnedQueue(target, ownedTrigger))) return null;
                    if (_transitions.TryGetValue(sessionId, out transition)) return null;
                    if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated || entry.HasActiveRun || entry.QueueDrainInProgress)
                    {
                        return null;
                    }

                    var localState = await ReadLatestLocalStateAsync(sessionId, entry.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                    // Null also represents a failed read; it is not proof that legacy work is absent.
                    if (localState is null) return null;
                    var item = localState.QueuedPrompts.FirstOrDefault(static prompt => string.Equals(prompt.State, "queued", StringComparison.OrdinalIgnoreCase));
                    if (item is null)
                    {
                        // This successful read and the slot claim share the original arbitration.
                        // Never start a second owned drainer after an ambiguous null/busy/fault result.
                        return TryClaimOwnedQueue(entry);
                    }

                    var sessionHandleId = entry.SessionHandleId;
                    // What waits in the queue is run by the attachment of the session, without a send that prepares
                    // it. A session whose worktree was removed meanwhile is attached again first, in the folder of
                    // its project: the answer a child forwards to it is then read, and not lost to a run that
                    // cannot start in a folder that is gone.
                    if (!string.IsNullOrWhiteSpace(entry.PendingAgentPromptId)
                        || (entry.WorktreeDirectory is not null && ExistingWorktree(entry.WorktreeDirectory) is null))
                    {
                        var session = entry.ToDescriptor();
                        var prepared = await EnsureCoordinatorSessionCoreAsync(session, entry.ToExecutionOptions(), actorCancellationToken).ConfigureAwait(false);
                        if (prepared.Transition is not null)
                        {
                            transition = prepared.Transition;
                            return null;
                        }
                        entry = prepared.Entry!;
                        sessionHandleId = entry.SessionHandleId;
                    }

                    var use = entry.Attachment.TryAcquireHandleUse();
                    if (use is null) return null;
                    try
                    {
                    var timestamp = DateTimeOffset.UtcNow;
                    item.State = "submitting";
                    item.DrainedAt = timestamp;
                    item.LastError = null;
                    CopySessionMetadata(entry.ToDescriptor(), localState);
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(entry.ToDescriptor(), localState, actorCancellationToken).ConfigureAwait(false);
                    entry.BeginQueueDrain();
                    PublishQueueChanged(sessionId, localState, item, timestamp, isEnqueued: false);
                    return new QueuedPromptDrainWork(sessionHandleId, CloneQueuedPrompt(item), entry, use);
                    }
                    catch { use.Dispose(); throw; }
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (transition is null) return work;
        try { await transition.ConfigureAwait(false); }
        catch (ObjectDisposedException failure) when (_forwarding.IsClosed
            && failure.ObjectName == typeof(SessionRuntimeService).FullName)
        {
            // A late attachment can be retired by shutdown before publication. The original
            // Ensure caller observes that refusal; an opportunistic queue tail has nothing to drain.
            return null;
        }
        // Re-read durable queue state after transition; never reuse the earlier state/item.
        }
        return null;
    }

    private async Task MarkQueuedPromptSubmittedAsync(
        RuntimeSessionEntry capturedEntry,
        string queueItemId,
        AgentRunId runId,
        DateTimeOffset runStartedAt,
        DateTimeOffset timestamp)
        => await UpdateQueuedPromptStateAsync(
                capturedEntry,
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

    private async Task MarkQueuedPromptFailedAsync(RuntimeSessionEntry capturedEntry, string queueItemId, string error, DateTimeOffset timestamp)
        => await UpdateQueuedPromptStateAsync(
                capturedEntry,
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
        RuntimeSessionEntry currentEntry,
        string queueItemId,
        DateTimeOffset timestamp,
        Action<SessionViewQueuedPrompt> updateItem,
        Action<SessionViewPromptProvenance>? updateProvenance,
        Action<RuntimeSessionEntry>? updateEntry)
    {
        var sessionId = currentEntry.SessionId;
        var actor = GetActorForWork(sessionId);
        await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    try
                    {
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
                        updateEntry?.Invoke(currentEntry);
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
            isEnqueued)
        { QueueKind = item.Kind });
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

            if (await HasActiveRunOwnedBodyAsync(parent, CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    var runId = await SteerOwnedBodyAsync(
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

            await QueuePromptOwnedBodyAsync(parent, prompt, "parent-notify", submittedBy, CancellationToken.None).ConfigureAwait(false);
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
            session = await ResolveParentFromCachedStoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
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
                WorktreeDirectory = entry.WorktreeDirectory,
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

    private async Task<SessionViewDescriptor?> ResolveParentFromCachedStoreAsync(string sessionId, CancellationToken cancellationToken)
    {
        // Finishing path: directly await the shared cached store, never a catalog-list producer.
        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return null;
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var session = TryCreateRecoverableSession(metadata, projects);
        if (session is not null)
        {
            if (metadata.ViewState is not null) ApplyCachedSessionLocalState(session, metadata.ViewState);
            else await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
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
        string? title,
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
                    WorktreeDirectory = NormalizeOptionalText(session.WorktreeDirectory),
                    Title = title,
                    Summary = session.LatestSummary,
                    ParentSessionId = NormalizeOptionalText(session.ParentSessionId),
                    CreatedBySessionId = NormalizeOptionalText(session.CreatedBy?.SourceSessionId ?? session.ParentSessionId),
                    CreatedAt = session.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The worktree a session records, while its folder exists; null otherwise.</summary>
    internal static string? ExistingWorktree(string? worktreeDirectory)
        => string.IsNullOrWhiteSpace(worktreeDirectory) || !Directory.Exists(worktreeDirectory) ? null : worktreeDirectory;

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
        var actor = GetActorForWork(session.SessionId);
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

    private async Task PublishRuntimeFailureEventAsync(SessionViewDescriptor session, Exception exception,
        LiveEventObservationPrerequisite prerequisite)
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
        var publishedEvent = new AgentErrorEvent(ProviderId, session.SessionId, timestamp, message, exception);
        var envelope = CapturePluginEvent(publishedEvent, session.SessionId, session.ProjectRef, session.WorkingDirectory);
        var publication = new LiveEventPublication(this) { ObservationPrerequisite = prerequisite };
        await publication.CompleteAsync(mark =>
        {
        _events.TryPublish(new SessionAgentEvent(envelope.SessionId, publishedEvent));
        mark(envelope);
        return Task.CompletedTask;
        }, static () => { }, ObserveLivePluginEventAsync, () =>
        {
        _events.TryPublish(new SessionLifecycleRuntimeEvent(
            envelope.SessionId,
            timestamp,
            new SessionLifecycleEvent
            {
                SessionId = envelope.SessionId,
                Kind = SessionLifecycleEventKind.RunFailed,
                Message = message,
            }));
        return Task.CompletedTask;
        }, _forwarding.RetainDependencies).ConfigureAwait(false);
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
            WorktreeDirectory = session.WorktreeDirectory,
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
                Title = BuildSessionTitle(session, UnnamedGlobalSessionTitle),
                Status = SessionViewStatus.Active,
                ParentSessionId = parentSessionId,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                LastActiveAt = session.UpdatedAt,
                StartedAt = session.CreatedAt,
                LatestSummary = session.Summary,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
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
            // As recorded: whether the folder is still there is looked at where the session is about to work.
            WorktreeDirectory = NormalizeOptionalText(session.WorktreePath),
            Title = BuildSessionTitle(session, project.DisplayName),
            Status = SessionViewStatus.Active,
            ParentSessionId = parentSessionId,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            LastActiveAt = session.UpdatedAt,
            StartedAt = session.CreatedAt,
            LatestSummary = session.Summary,
            ModelId = session.ModelId,
            ReasoningEffort = session.ReasoningEffort,
            AgentPromptId = ResolveKnownAgentPromptId(session.AgentPromptId, project.ProjectPath),
        };
    }

    internal IReadOnlyList<AgentPromptDescriptor> ListOwnedPrompts(string? projectRoot)
    {
        ValidateDiscoveryProjectRoot(projectRoot);
        return (PromptCatalog ?? new AgentPromptCatalog()).ListEffectivePrompts(new AgentPromptCatalogQuery
        {
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = !string.IsNullOrWhiteSpace(projectRoot),
            UserCodeAltaRoot = _catalogOptions.GlobalRoot,
            UserProfileRoot = _discoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        });
    }

    private string? ResolveKnownAgentPromptId(string? promptId, string? projectRoot)
    {
        ValidateDiscoveryProjectRoot(projectRoot);
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
            UserProfileRoot = _discoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return (PromptCatalog ?? new AgentPromptCatalog()).ListEffectivePrompts(query)
            .Any(prompt => string.Equals(prompt.PromptName, normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : AgentPromptCatalog.DefaultPromptName;
    }

    private AgentPromptUsageInfo? ResolveAgentPromptUsage(SystemPromptBundle? promptBundle, string? projectRoot)
    {
        ValidateDiscoveryProjectRoot(projectRoot);
        var promptName = NormalizeOptionalText(promptBundle?.Manifest.Composition.AgentPromptName);
        if (promptName is null)
        {
            return null;
        }

        var query = new AgentPromptCatalogQuery
        {
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = !string.IsNullOrWhiteSpace(projectRoot),
            UserCodeAltaRoot = _catalogOptions.GlobalRoot,
            UserProfileRoot = _discoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        var descriptor = (PromptCatalog ?? new AgentPromptCatalog()).ResolvePrompt(query, promptName);
        if (descriptor is null)
        {
            return new AgentPromptUsageInfo(promptName, null, null);
        }

        return new AgentPromptUsageInfo(
            descriptor.PromptName,
            NormalizeOptionalText(descriptor.DisplayName),
            FormatAgentPromptSourcePathForTimeline(descriptor.SourcePath, projectRoot));
    }

    internal static string? FormatAgentPromptSourcePathForTimeline(string? sourcePath, string? projectRoot)
    {
        var normalizedSourcePath = NormalizeOptionalText(sourcePath);
        if (normalizedSourcePath is null)
        {
            return null;
        }

        var sourceFullPath = NormalizePath(normalizedSourcePath);
        var normalizedProjectRoot = NormalizeOptionalText(projectRoot);
        if (normalizedProjectRoot is null)
        {
            return sourceFullPath;
        }

        var projectFullPath = NormalizePath(normalizedProjectRoot);
        var relativePath = Path.GetRelativePath(projectFullPath, sourceFullPath);
        return IsProjectRelativePath(relativePath) ? relativePath : sourceFullPath;
    }

    private static bool IsProjectRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
            !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private const string UnnamedGlobalSessionTitle = "Global Session";
    private const string UnnamedGlobalSessionSummary = "Global overview and coordination session.";

    private static string UnnamedProjectSessionSummary(ProjectDescriptor project) => $"Project session for {project.DisplayName}.";

    private static string BuildSessionTitle(AgentSessionMetadata session, string fallback)
        => SummaryTitle(session.Summary) ?? fallback;

    private static string? SummaryTitle(string? summary)
    {
        if (!string.IsNullOrWhiteSpace(summary))
        {
            var firstLine = summary.Trim().Split(['\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                // Cut between two characters and without a space at the end: a list shows this text as it is, and a
                // deletion is confirmed with it.
                return firstLine.Length <= 80 ? firstLine : firstLine[..(char.IsHighSurrogate(firstLine[79]) ? 79 : 80)].TrimEnd();
            }
        }

        return null;
    }

    // The saved title of a stored session and the name it was given, which is null for a session that was never
    // named; null for a session that is not stored.
    private async Task<(string? Saved, string? Given)?> StoredTitleAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId)) return null;
        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return null;
        return (NormalizeOptionalText((metadata.Details as RawApiSessionMetadataDetails)?.Title),
            GivenTitle(metadata, session, await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>
    /// The name a session was given, at its creation or by a rename; null for a session that was never named, whose
    /// saved title is what it was created with or the first line of the summary it was created with.
    /// </summary>
    private static string? GivenTitle(AgentSessionMetadata metadata, SessionViewDescriptor session, IReadOnlyList<ProjectDescriptor> projects)
    {
        var title = NormalizeOptionalText((metadata.Details as RawApiSessionMetadataDetails)?.Title);
        return title is null || IsCreationTitle(title, session.Kind, ProjectOf(session, projects)) ? null : title;
    }

    private static ProjectDescriptor? ProjectOf(SessionViewDescriptor session, IReadOnlyList<ProjectDescriptor> projects)
        => session.Kind == SessionViewKind.GlobalSession ? null
            : projects.FirstOrDefault(candidate => string.Equals(candidate.Id, session.ProjectRef, StringComparison.Ordinal));

    // The titles a session is created with: "Global Session" or the name of its project, or the first line of the
    // summary it is created with. A saved title that is one of them was never chosen by anyone.
    private static bool IsCreationTitle(string title, SessionViewKind? kind, ProjectDescriptor? project) => kind switch
    {
        null => false,
        SessionViewKind.GlobalSession => title is UnnamedGlobalSessionTitle || title == SummaryTitle(UnnamedGlobalSessionSummary),
        _ => project is not null && (title == project.DisplayName || title == SummaryTitle(UnnamedProjectSessionSummary(project))),
    };

    /// <summary>
    /// The title a list of sessions shows for a stored session, and the one its deletion is confirmed with: the name
    /// the session was given, and for a session that was never named the first line of its summary (80 characters at
    /// most), then the title it was created with, then its id.
    /// </summary>
    /// <param name="session">The stored session.</param>
    /// <param name="kind">Whether the session is a global one or the session of a project; <see langword="null" /> when that is not known: its saved title is then taken as its name.</param>
    /// <param name="project">The project of a project session.</param>
    /// <returns>The title of the session in a list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session" /> is <see langword="null" />.</exception>
    public static string ListedTitle(AgentSessionMetadata session, SessionViewKind? kind, ProjectDescriptor? project)
    {
        ArgumentNullException.ThrowIfNull(session);
        var saved = (session.Details as RawApiSessionMetadataDetails)?.Title;
        if (string.IsNullOrWhiteSpace(saved)) saved = null;
        return saved is not null && !IsCreationTitle(saved.Trim(), kind, project) ? saved
            : SummaryTitle(session.Summary) ?? saved ?? session.SessionId;
    }

    // The session of the agent a prompt comes from; null for a prompt of a person, of a reminder or of the host.
    private static string? AgentSource(AltaActorProvenance? submittedBy)
        => string.Equals(submittedBy?.Kind, "agent", StringComparison.OrdinalIgnoreCase) ? NormalizeOptionalText(submittedBy!.SourceSessionId) : null;

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
            "max" => AgentReasoningEffort.Max,
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
            SessionInstructionProcessor? instructionProcessor,
            EventProjector projector,
            OwnedProviderEventForwarding.Attachment attachment)
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
            InstructionProcessor = instructionProcessor;
            Projector = projector;
            Attachment = attachment;
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

        public string Title { get; set; }

        public string WorkingDirectory { get; }

        /// <summary>The git worktree this attachment runs in; null when it runs in <see cref="WorkingDirectory"/>.</summary>
        public string? WorktreeDirectory { get; init; }

        /// <summary>The version of the registration of the provider the runtime of this attachment was created from.</summary>
        public long ProviderRegistrationVersion { get; init; }

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

        public SessionInstructionProcessor? InstructionProcessor { get; }

        public OwnedProviderEventForwarding.Attachment Attachment { get; }

        public EventProjector Projector { get; }

        public bool IsTerminated { get; private set; }

        public SessionRuntimeUsageObservation? LastObservedUsage { get; set; }
        public long UsageSequence { get; set; }
        public long OmittedUsageEvents { get; set; }
        private volatile bool _activityOpen;

        /// <summary>
        /// Whether provider callbacks arriving now count as activity: true once the transition that created
        /// this attachment has completed. Read on the provider's callback thread.
        /// </summary>
        public bool ActivityOpen => _activityOpen;

        public void OpenActivity() => _activityOpen = true;

        public DateTimeOffset? ActivityTimestamp { get; set; }
        public long ActivityEvents { get; set; }
        public long OmittedActivityEvents { get; set; }

        public AgentRunId? ActiveRunId { get; private set; }

        public DateTimeOffset LastTerminalEventAt { get; private set; } = DateTimeOffset.MinValue;

        public bool QueueDrainInProgress { get; private set; }
        internal OwnedQueuedExecution? OwnedQueue { get; set; }

        private ParentFinalNotificationCandidate? _lastParentFinalCandidate;

        private string? _lastParentIdleRunId;

        private readonly HashSet<string> _sentParentProgressKeys = new(StringComparer.Ordinal);

        private readonly HashSet<string> _sentParentFinalKeys = new(StringComparer.Ordinal);

        public bool HasActiveRun => ActiveRunId is not null;

        /// <summary>Whether the last run of this attachment ended with an error, until another run starts.</summary>
        public bool LastRunFailed { get; private set; }

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
                WorktreeDirectory = WorktreeDirectory,
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
                InstructionProcessor = InstructionProcessor,
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
                && (InstructionProcessor is null) == (options.InstructionProcessor is null)
                && ToolSignatures.SequenceEqual(CreateToolSignatures(options.Tools), StringComparer.Ordinal);
        }

        public void ObserveEvent(AgentEvent @event)
        {
            if (@event.RunId is { } runId && ShouldTrackRunId(@event))
            {
                // A run that goes on after a failed one makes that failure a thing of the past.
                if (ActiveRunId is null && @event is not AgentErrorEvent) LastRunFailed = false;
                ActiveRunId = runId;
            }

            if (@event is AgentErrorEvent) LastRunFailed = true;

            if (@event is AgentBackgroundTasksEvent tasks)
            {
                ObserveBackgroundTasks(tasks);
            }

            if (@event is AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown })
            {
                ActiveRunId = null;
                LastTerminalEventAt = @event.Timestamp;
            }

            if (@event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Shutdown })
            {
                IsTerminated = true;
                BackgroundTasks = [];
            }
        }

        /// <summary>The background tasks of the provider: those that go on, then the last that failed or were stopped.</summary>
        public IReadOnlyList<SessionRuntimeBackgroundTask> BackgroundTasks { get; private set; } = [];

        // The tasks that go on are the ones the event lists, all of them. A task that failed or was stopped is
        // kept after them for the call that started it, which otherwise only says that it returned; a task that
        // ran to its end leaves nothing, and neither does one that runs again.
        private void ObserveBackgroundTasks(AgentBackgroundTasksEvent @event)
        {
            // A task says that it ended just before the list that no longer has it: it is not shown as going on meanwhile.
            var live = @event.Tasks.Where(task => !@event.Ended.Any(ended => ended.TaskId == task.TaskId)).Take(MaximumBackgroundTasks)
                .Select(static task => new SessionRuntimeBackgroundTask(task.TaskId, task.Kind, task.Description, task.ToolCallId, task.StartedAt, null))
                .ToArray();
            var ended = @event.Ended
                .Where(static task => task.Outcome is not AgentBackgroundTaskOutcome.Completed && task.ToolCallId is not null)
                .Select(task => new SessionRuntimeBackgroundTask(task.TaskId, BackgroundTasks.FirstOrDefault(known => known.TaskId == task.TaskId)?.Kind ?? "task",
                    task.Summary, task.ToolCallId, null, task.Outcome))
                .Concat(BackgroundTasks.Where(static task => task.Outcome is not null))
                .Where(task => !Array.Exists(live, running => running.TaskId == task.TaskId || running.ToolCallId == task.ToolCallId))
                .DistinctBy(static task => task.TaskId)
                .Take(MaximumBackgroundTasks);
            BackgroundTasks = [.. live, .. ended];
        }

        private const int MaximumBackgroundTasks = 16;

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

                // Event callbacks are forwarded independently; an idle update can reach the
                // mailbox before the completed content from the same run.
                if (completed.RunId?.Value is { } runId && string.Equals(_lastParentIdleRunId, runId, StringComparison.Ordinal))
                    notifications.AddRange(TakeFinalParentNotification(runId));

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

            if (@event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle } idle)
            {
                _lastParentIdleRunId = idle.RunId?.Value;
                return TakeFinalParentNotification(_lastParentIdleRunId);
            }

            return [];
        }

        private IReadOnlyList<ParentNotificationWork> TakeFinalParentNotification(string? runId)
        {
            if (_lastParentFinalCandidate is not { } candidate
                || (runId is not null && candidate.RunId is not null && !string.Equals(runId, candidate.RunId, StringComparison.Ordinal))
                || string.IsNullOrWhiteSpace(candidate.Content))
                return [];

            var key = candidate.RunId ?? candidate.ContentId;
            if (!_sentParentFinalKeys.Add(key)) return [];
            return [CreateParentNotification("answer", candidate.Content, candidate.RunId, candidate.ContentId)];
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

    }

    private sealed record QueuedPromptDrainWork(AgentSessionHandleId SessionHandleId, SessionViewQueuedPrompt? Prompt,
        RuntimeSessionEntry Entry, OwnedProviderEventForwarding.Use Use, OwnedQueuedExecution? Owned = null);

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
        public RuntimeSessionEntry? Entry { get; set; }

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
