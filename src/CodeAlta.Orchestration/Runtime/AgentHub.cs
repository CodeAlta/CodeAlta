using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Persistence;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Owns active orchestrated agents, sessions, and run coordination.
/// </summary>
public sealed class AgentHub : IAsyncDisposable
{
    private readonly AgentBackendFactory _backendFactory;
    private readonly AgentRepository _agentRepository;
    private readonly Dictionary<AgentId, AgentIdentity> _agents = new();
    private readonly Dictionary<AgentId, SessionEntry> _sessions = new();
    private readonly Dictionary<string, IAgentBackend> _backends = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<IAgentBackend>> _backendInitializationTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<AgentSessionMetadata>> _sessionMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<OrchestrationEvent> _events = Channel.CreateUnbounded<OrchestrationEvent>();
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentHub"/> class.
    /// </summary>
    /// <param name="backendFactory">Agent backend factory.</param>
    /// <param name="agentRepository">Agent repository.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="backendFactory"/> or <paramref name="agentRepository"/> is <see langword="null"/>.
    /// </exception>
    public AgentHub(AgentBackendFactory backendFactory, AgentRepository agentRepository)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(agentRepository);

        _backendFactory = backendFactory;
        _agentRepository = agentRepository;
    }

    /// <summary>
    /// Streams orchestration events.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Orchestration events.</returns>
    public IAsyncEnumerable<OrchestrationEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
    {
        return _events.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>
    /// Registers an agent identity and persists it.
    /// </summary>
    /// <param name="roleId">Role id.</param>
    /// <param name="scope">Scope.</param>
    /// <param name="backendId">Backend id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The registered identity.</returns>
    public async Task<AgentIdentity> RegisterAgentAsync(
        string roleId,
        AgentScope scope,
        AgentBackendId backendId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roleId))
        {
            throw new ArgumentException("Role id is required.", nameof(roleId));
        }

        ArgumentNullException.ThrowIfNull(scope);

        var identity = new AgentIdentity
        {
            AgentId = AgentId.NewVersion7(),
            RoleId = roleId.Trim(),
            Scope = scope,
            BackendId = backendId,
        };

        await _agentRepository.UpsertAgentAsync(
            new AgentRecord
            {
                AgentId = identity.AgentId,
                Role = identity.RoleId,
                ScopeKind = scope.Kind.ToString().ToLowerInvariant(),
                ScopeId = scope.Id,
                BackendId = identity.BackendId.Value,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _agents[identity.AgentId] = identity;
        }
        finally
        {
            _gate.Release();
        }

        return identity;
    }

    /// <summary>
    /// Starts a backend session for an agent.
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="options">Session creation options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The started session id.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the agent is not registered.</exception>
    public async Task<string> StartSessionAsync(
        AgentId agentId,
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        AgentIdentity identity;
        SessionEntry? previousEntry = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_agents.TryGetValue(agentId, out identity!))
            {
                throw new InvalidOperationException($"Agent '{agentId}' is not registered.");
            }
        }
        finally
        {
            _gate.Release();
        }

        var backend = await GetOrCreateBackendAsync(identity.BackendId, cancellationToken).ConfigureAwait(false);
        var session = await backend.CreateSessionAsync(options, cancellationToken).ConfigureAwait(false);

        var sessionEntry = new SessionEntry(session, backend);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(agentId, out previousEntry))
            {
                _sessions.Remove(agentId);
            }

            _sessions[agentId] = sessionEntry;
            InvalidateSessionMetadataCacheUnsafe(identity.BackendId);
        }
        finally
        {
            _gate.Release();
        }

        if (previousEntry is not null)
        {
            await previousEntry.DisposeAsync().ConfigureAwait(false);
        }

        await _agentRepository.UpsertSessionAsync(
            new AgentSessionRecord
            {
                SessionId = session.SessionId,
                AgentId = agentId,
                BackendSessionId = session.SessionId,
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken).ConfigureAwait(false);

        return session.SessionId;
    }

    /// <summary>
    /// Resumes a backend session for an agent.
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="backendSessionId">The backend session identifier.</param>
    /// <param name="options">Session resume options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resumed session id.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the agent is not registered.</exception>
    public async Task<string> ResumeSessionAsync(
        AgentId agentId,
        string backendSessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendSessionId);
        ArgumentNullException.ThrowIfNull(options);

        AgentIdentity identity;
        SessionEntry? previousEntry = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_agents.TryGetValue(agentId, out identity!))
            {
                throw new InvalidOperationException($"Agent '{agentId}' is not registered.");
            }
        }
        finally
        {
            _gate.Release();
        }

        var backend = await GetOrCreateBackendAsync(identity.BackendId, cancellationToken).ConfigureAwait(false);
        var session = await backend.ResumeSessionAsync(backendSessionId, options, cancellationToken).ConfigureAwait(false);
        var sessionEntry = new SessionEntry(session, backend);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(agentId, out previousEntry))
            {
                _sessions.Remove(agentId);
            }

            _sessions[agentId] = sessionEntry;
            InvalidateSessionMetadataCacheUnsafe(identity.BackendId);
        }
        finally
        {
            _gate.Release();
        }

        if (previousEntry is not null)
        {
            await previousEntry.DisposeAsync().ConfigureAwait(false);
        }

        await _agentRepository.UpsertSessionAsync(
                new AgentSessionRecord
                {
                    SessionId = session.SessionId,
                    AgentId = agentId,
                    BackendSessionId = session.SessionId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastUsedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return session.SessionId;
    }

    /// <summary>
    /// Lists the available models for a backend.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The available backend models.</returns>
    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        AgentBackendId backendId,
        CancellationToken cancellationToken = default)
    {
        var backend = await GetOrCreateBackendAsync(backendId, cancellationToken).ConfigureAwait(false);
        return await backend.ListModelsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists sessions known to a backend.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="filter">Optional backend session filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The backend session metadata.</returns>
    public async Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
        AgentBackendId backendId,
        AgentSessionListFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await GetOrCreateBackendAsync(backendId, cancellationToken).ConfigureAwait(false);
        if (!CanCacheSessionMetadata(backendId))
        {
            return await backend.ListSessionsAsync(filter, cancellationToken).ConfigureAwait(false);
        }

        var key = backendId.Value;
        IReadOnlyList<AgentSessionMetadata>? sessions;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionMetadataCache.TryGetValue(key, out sessions))
            {
                return FilterSessionMetadata(sessions, filter);
            }
        }
        finally
        {
            _gate.Release();
        }

        var loadedSessions = (await backend.ListSessionsAsync(filter: null, cancellationToken).ConfigureAwait(false)).ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessionMetadataCache.TryGetValue(key, out sessions))
            {
                sessions = loadedSessions;
                _sessionMetadataCache[key] = sessions;
            }
        }
        finally
        {
            _gate.Release();
        }

        return FilterSessionMetadata(sessions, filter);
    }

    /// <summary>
    /// Deletes a backend-owned session when supported.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="sessionId">The backend session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the backend deleted the session; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DeleteSessionAsync(
        AgentBackendId backendId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var backend = await GetOrCreateBackendAsync(backendId, cancellationToken).ConfigureAwait(false);
        var deleted = await backend.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (deleted)
        {
            await InvalidateSessionMetadataCacheAsync(backendId, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    /// <summary>
    /// Unloads a backend runtime when it has no active sessions.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a cached backend existed and was unloaded.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the backend still owns active sessions.</exception>
    public async Task<bool> UnloadBackendAsync(AgentBackendId backendId, CancellationToken cancellationToken = default)
    {
        IAgentBackend? backend = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.Values.Any(entry => string.Equals(entry.Backend.BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Backend '{backendId.Value}' cannot be reloaded while it has active sessions. Close ACP threads first.");
            }

            if (_backends.TryGetValue(backendId.Value, out backend))
            {
                _backends.Remove(backendId.Value);
            }

            InvalidateSessionMetadataCacheUnsafe(backendId);
        }
        finally
        {
            _gate.Release();
        }

        if (backend is null)
        {
            return false;
        }

        try
        {
            await backend.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await backend.DisposeAsync().ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Returns whether a backend currently owns any active sessions.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the backend has an active session.</returns>
    public async Task<bool> HasActiveSessionsAsync(AgentBackendId backendId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _sessions.Values.Any(entry => string.Equals(entry.Backend.BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns whether a backend uses the CodeAlta-owned shared session metadata store.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true" /> when sessions can be recovered once from the shared store.</returns>
    public async Task<bool> UsesSharedSessionMetadataStoreAsync(
        AgentBackendId backendId,
        CancellationToken cancellationToken = default)
    {
        var backend = await GetOrCreateBackendAsync(backendId, cancellationToken).ConfigureAwait(false);
        return backend is IAgentSharedSessionMetadataBackend;
    }

    /// <summary>
    /// Lists registered backend identifiers known to the orchestration runtime.
    /// </summary>
    /// <returns>The registered backend identifiers.</returns>
    public IReadOnlyList<AgentBackendId> ListRegisteredBackends()
        => _backendFactory.ListRegisteredBackends();

    /// <summary>
    /// Sends input through an active agent session.
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="options">Send options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The backend run id.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the agent has no active session.</exception>
    public async Task<AgentRunId> RunAsync(
        AgentId agentId,
        AgentSendOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entry = await AcquireSessionEntryAsync(agentId, cancellationToken).ConfigureAwait(false);

        var runGateHeld = false;
        try
        {
            await entry.RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            runGateHeld = true;

            var runId = await entry.Session.SendAsync(options, cancellationToken).ConfigureAwait(false);
            _events.Writer.TryWrite(new RunStartedEvent(DateTimeOffset.UtcNow, agentId, runId));
            _events.Writer.TryWrite(new RunCompletedEvent(DateTimeOffset.UtcNow, agentId, runId));

            await _agentRepository.UpsertSessionAsync(
                new AgentSessionRecord
                {
                    SessionId = entry.Session.SessionId,
                    AgentId = agentId,
                    BackendSessionId = entry.Session.SessionId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastUsedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);

            return runId;
        }
        catch (Exception ex)
        {
            _events.Writer.TryWrite(new RunFailedEvent(DateTimeOffset.UtcNow, agentId, ex.Message));
            throw;
        }
        finally
        {
            if (runGateHeld)
            {
                entry.RunGate.Release();
            }

            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Steers an active agent run without starting a new one.
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="options">Steering options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The backend run id that accepted the steering input.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the agent has no active session.</exception>
    public async Task<AgentRunId> SteerAsync(
        AgentId agentId,
        AgentSteerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entry = await AcquireSessionEntryAsync(agentId, cancellationToken).ConfigureAwait(false);

        try
        {
            var runId = await entry.Session.SteerAsync(options, cancellationToken).ConfigureAwait(false);
            _events.Writer.TryWrite(new RunStartedEvent(DateTimeOffset.UtcNow, agentId, runId));
            _events.Writer.TryWrite(new RunCompletedEvent(DateTimeOffset.UtcNow, agentId, runId));
            return runId;
        }
        catch (Exception ex)
        {
            _events.Writer.TryWrite(new RunFailedEvent(DateTimeOffset.UtcNow, agentId, ex.Message));
            throw;
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Subscribes to normalized agent events from the active agent session.
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="handler">Event handler.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="IDisposable"/> that unsubscribes when disposed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the agent has no active session.</exception>
    public async Task<IDisposable> SubscribeSessionEventsAsync(
        AgentId agentId,
        Action<AgentEvent> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var entry = await AcquireSessionEntryAsync(agentId, cancellationToken).ConfigureAwait(false);
        try
        {
            return entry.Session.Subscribe(handler);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Retrieves the stored history for the active agent session (best effort).
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session event history.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the agent has no active session.</exception>
    public async Task<IReadOnlyList<AgentEvent>> GetSessionHistoryAsync(
        AgentId agentId,
        CancellationToken cancellationToken = default)
    {
        var entry = await AcquireSessionEntryAsync(agentId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await entry.Session.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Aborts/cancels the currently running work in the active agent session (best effort).
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the agent has no active session.</exception>
    public async Task AbortAsync(AgentId agentId, CancellationToken cancellationToken = default)
    {
        var entry = await AcquireSessionEntryAsync(agentId, cancellationToken).ConfigureAwait(false);
        try
        {
            await entry.Session.AbortAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Triggers a manual compaction in the active agent session.
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the agent has no active session.</exception>
    public async Task<AgentCompactionOutcome?> CompactAsync(AgentId agentId, CancellationToken cancellationToken = default)
    {
        var entry = await AcquireSessionEntryAsync(agentId, cancellationToken).ConfigureAwait(false);
        var runGateHeld = false;
        try
        {
            await entry.RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            runGateHeld = true;

            if (entry.Session is IAgentCompactionOutcomeProvider compactionOutcomeProvider)
            {
                return await compactionOutcomeProvider.CompactWithOutcomeAsync(cancellationToken).ConfigureAwait(false);
            }

            await entry.Session.CompactAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        finally
        {
            if (runGateHeld)
            {
                entry.RunGate.Release();
            }

            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Stops and disposes the active session for an agent, if present.
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task StopSessionAsync(AgentId agentId, CancellationToken cancellationToken = default)
    {
        SessionEntry? entry = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(agentId, out entry))
            {
                _sessions.Remove(agentId);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (entry is not null)
        {
            await entry.DisposeAsync().ConfigureAwait(false);
        }
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

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        _sessionMetadataCache.Clear();

        foreach (var backend in _backends.Values)
        {
            try
            {
                await backend.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore shutdown exceptions from backend runtimes.
            }

            await backend.DisposeAsync().ConfigureAwait(false);
        }

        _backends.Clear();
        _gate.Dispose();
    }

    private async Task<IAgentBackend> GetOrCreateBackendAsync(
        AgentBackendId backendId,
        CancellationToken cancellationToken)
    {
        var key = backendId.Value;
        Task<IAgentBackend> initializationTask;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backends.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (!_backendInitializationTasks.TryGetValue(key, out initializationTask!))
            {
                initializationTask = CreateAndStartBackendAsync(backendId, key, cancellationToken);
                _backendInitializationTasks[key] = initializationTask;
            }
        }
        finally
        {
            _gate.Release();
        }

        return await initializationTask.ConfigureAwait(false);
    }

    private async Task<IAgentBackend> CreateAndStartBackendAsync(
        AgentBackendId backendId,
        string key,
        CancellationToken cancellationToken)
    {
        IAgentBackend? created = null;
        try
        {
            created = _backendFactory.Create(backendId);
            await created.StartAsync(cancellationToken).ConfigureAwait(false);

            IAgentBackend? existing = null;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _backendInitializationTasks.Remove(key);
                if (_backends.TryGetValue(key, out existing))
                {
                    return existing;
                }

                _backends[key] = created;
                return created;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _backendInitializationTasks.Remove(key);
            }
            finally
            {
                _gate.Release();
            }

            if (created is not null)
            {
                try
                {
                    await created.StopAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore shutdown exceptions from backend runtimes that did not finish starting.
                }

                await created.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task InvalidateSessionMetadataCacheAsync(AgentBackendId backendId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InvalidateSessionMetadataCacheUnsafe(backendId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SessionEntry> AcquireSessionEntryAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        SessionEntry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(agentId, out entry!))
            {
                throw new InvalidOperationException($"Agent '{agentId}' does not have an active session.");
            }

            if (!entry.TryAddReference())
            {
                throw new InvalidOperationException($"Agent '{agentId}' does not have an active session.");
            }
        }
        finally
        {
            _gate.Release();
        }

        return entry;
    }

    private void InvalidateSessionMetadataCacheUnsafe(AgentBackendId backendId)
        => _sessionMetadataCache.Remove(backendId.Value);

    private static bool CanCacheSessionMetadata(AgentBackendId backendId)
        => string.Equals(backendId.Value, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(backendId.Value, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<AgentSessionMetadata> FilterSessionMetadata(
        IReadOnlyList<AgentSessionMetadata> sessions,
        AgentSessionListFilter? filter)
    {
        if (filter is null)
        {
            return sessions;
        }

        return sessions.Where(session => MatchesSessionFilter(session, filter)).ToArray();
    }

    private static bool MatchesSessionFilter(AgentSessionMetadata session, AgentSessionListFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Cwd) &&
            !string.Equals(session.Context?.Cwd ?? session.WorkspacePath, filter.Cwd, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.GitRoot) &&
            !string.Equals(session.Context?.GitRoot, filter.GitRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Repository) &&
            !string.Equals(session.Context?.Repository, filter.Repository, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Branch) &&
            !string.Equals(session.Context?.Branch, filter.Branch, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private sealed class SessionEntry : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _disposedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _idleCompletion;
        private int _activeReferences;
        private bool _disposeStarted;

        public SessionEntry(IAgentSession session, IAgentBackend backend)
        {
            Session = session;
            Backend = backend;
        }

        public IAgentSession Session { get; }

        public IAgentBackend Backend { get; }

        public SemaphoreSlim RunGate { get; } = new(initialCount: 1, maxCount: 1);

        public bool TryAddReference()
        {
            lock (_sync)
            {
                if (_disposeStarted)
                {
                    return false;
                }

                _activeReferences++;
                return true;
            }
        }

        public void ReleaseReference()
        {
            TaskCompletionSource? idleCompletion = null;
            lock (_sync)
            {
                if (_activeReferences <= 0)
                {
                    throw new InvalidOperationException("Session entry reference count is already zero.");
                }

                _activeReferences--;
                if (_activeReferences == 0 && _disposeStarted)
                {
                    idleCompletion = _idleCompletion;
                }
            }

            idleCompletion?.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            Task? disposeTask;
            Task? idleTask = null;
            lock (_sync)
            {
                if (_disposeStarted)
                {
                    disposeTask = _disposedCompletion.Task;
                }
                else
                {
                    _disposeStarted = true;
                    disposeTask = null;
                    if (_activeReferences > 0)
                    {
                        _idleCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        idleTask = _idleCompletion.Task;
                    }
                }
            }

            if (disposeTask is not null)
            {
                await disposeTask.ConfigureAwait(false);
                return;
            }

            try
            {
                if (idleTask is not null)
                {
                    try
                    {
                        await Session.AbortAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore best-effort abort failures while the session is being disposed.
                    }

                    await idleTask.ConfigureAwait(false);
                }

                RunGate.Dispose();
                await Session.DisposeAsync().ConfigureAwait(false);
                _disposedCompletion.TrySetResult();
            }
            catch (Exception ex)
            {
                _disposedCompletion.TrySetException(ex);
                throw;
            }
        }
    }
}
