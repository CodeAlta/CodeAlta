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
        return await backend.ListSessionsAsync(filter, cancellationToken).ConfigureAwait(false);
    }

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

        SessionEntry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(agentId, out entry!))
            {
                throw new InvalidOperationException($"Agent '{agentId}' does not have an active session.");
            }
        }
        finally
        {
            _gate.Release();
        }

        await entry.RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            entry.RunGate.Release();
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

        SessionEntry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(agentId, out entry!))
            {
                throw new InvalidOperationException($"Agent '{agentId}' does not have an active session.");
            }
        }
        finally
        {
            _gate.Release();
        }

        await entry.RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            entry.RunGate.Release();
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

        SessionEntry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(agentId, out entry!))
            {
                throw new InvalidOperationException($"Agent '{agentId}' does not have an active session.");
            }
        }
        finally
        {
            _gate.Release();
        }

        return entry.Session.Subscribe(handler);
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
        SessionEntry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(agentId, out entry!))
            {
                throw new InvalidOperationException($"Agent '{agentId}' does not have an active session.");
            }
        }
        finally
        {
            _gate.Release();
        }

        return await entry.Session.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Aborts/cancels the currently running work in the active agent session (best effort).
    /// </summary>
    /// <param name="agentId">Agent id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the agent has no active session.</exception>
    public async Task AbortAsync(AgentId agentId, CancellationToken cancellationToken = default)
    {
        SessionEntry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(agentId, out entry!))
            {
                throw new InvalidOperationException($"Agent '{agentId}' does not have an active session.");
            }
        }
        finally
        {
            _gate.Release();
        }

        await entry.Session.AbortAsync(cancellationToken).ConfigureAwait(false);
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

        cancellationToken.ThrowIfCancellationRequested();

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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backends.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = _backendFactory.Create(backendId);
            await created.StartAsync(cancellationToken).ConfigureAwait(false);
            _backends[key] = created;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class SessionEntry : IAsyncDisposable
    {
        public SessionEntry(IAgentSession session, IAgentBackend backend)
        {
            Session = session;
            Backend = backend;
        }

        public IAgentSession Session { get; }

        public IAgentBackend Backend { get; }

        public SemaphoreSlim RunGate { get; } = new(initialCount: 1, maxCount: 1);

        public async ValueTask DisposeAsync()
        {
            RunGate.Dispose();
            await Session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
