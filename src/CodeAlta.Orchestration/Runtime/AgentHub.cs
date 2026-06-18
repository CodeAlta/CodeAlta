using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// In-process CodeAlta agent runtime facade for active session and run coordination.
/// </summary>
/// <remarks>
/// <see cref="AgentHub"/> owns active in-memory session attachments, per-session run/control coordination,
/// subscriptions, and lifecycle events. Persisted session discovery/listing and model-provider probing are owned by
/// the session catalog and provider initialization services, not by this facade.
/// </remarks>
public sealed class AgentHub : IAsyncDisposable
{
    private readonly ModelProviderRegistry _modelProviderRegistry;
    private readonly string? _stateRootPath;
    private readonly IAgentSessionProjectionCache? _sessionProjectionCache;
    private readonly Dictionary<AgentSessionHandleId, SessionEntry> _sessions = new();
    private readonly BoundedRuntimeEventStream<OrchestrationEvent> _events = new();
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentHub"/> class.
    /// </summary>
    /// <param name="modelProviderRegistry">Model provider registry used to create provider runtimes.</param>
    /// <param name="stateRootPath">The agent runtime storage root path.</param>
    /// <param name="sessionProjectionCache">Optional session projection cache shared with the session journal store.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="modelProviderRegistry"/> is <see langword="null"/>.</exception>
    public AgentHub(
        ModelProviderRegistry modelProviderRegistry,
        string? stateRootPath = null,
        IAgentSessionProjectionCache? sessionProjectionCache = null)
    {
        ArgumentNullException.ThrowIfNull(modelProviderRegistry);

        _modelProviderRegistry = modelProviderRegistry;
        _stateRootPath = stateRootPath;
        _sessionProjectionCache = sessionProjectionCache;
    }

    /// <summary>
    /// Streams orchestration events.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Orchestration events.</returns>
    public IAsyncEnumerable<OrchestrationEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
    {
        return _events.ReadAllAsync(cancellationToken);
    }

    /// <summary>
    /// Starts a session using the provider selected by <see cref="AgentSessionCreateOptions.ProviderKey"/>.
    /// </summary>
    /// <param name="options">Session creation options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active session handle.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no provider key was provided, or when the provider returns a different requested session identifier.</exception>
    public async Task<AgentSessionHandle> StartSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var providerId = ResolveProviderId(options);
        var runtime = await CreateStartedProviderRuntimeAsync(providerId, cancellationToken).ConfigureAwait(false);
        IAgentSession? session = null;
        try
        {
            session = await runtime.CreateSessionAsync(options, cancellationToken).ConfigureAwait(false);
            EnsureRuntimePreservedRequestedSessionId(options.SessionId, session.SessionId);
            return await AttachSessionAsync(providerId, runtime, session, options.ParentSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Resumes a session using the provider selected by <see cref="AgentSessionCreateOptions.ProviderKey"/>.
    /// </summary>
    /// <param name="sessionId">The durable session identifier to resume.</param>
    /// <param name="options">Session resume options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active session handle.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no provider key was provided, or when the provider returns a different requested session identifier.</exception>
    public async Task<AgentSessionHandle> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);

        var providerId = ResolveProviderId(options);
        var runtime = await CreateStartedProviderRuntimeAsync(providerId, cancellationToken).ConfigureAwait(false);
        IAgentSession? session = null;
        try
        {
            session = await runtime.ResumeSessionAsync(sessionId, options, cancellationToken).ConfigureAwait(false);
            EnsureRuntimePreservedRequestedSessionId(sessionId, session.SessionId);
            return await AttachSessionAsync(providerId, runtime, session, options.ParentSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            await DisposeProviderRuntimeAsync(runtime).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends input through an active session attachment.
    /// </summary>
    /// <param name="sessionHandleId">The active session handle.</param>
    /// <param name="options">Send options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provider run id.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle does not reference an active session.</exception>
    public async Task<AgentRunId> RunAsync(
        AgentSessionHandleId sessionHandleId,
        AgentSendOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await entry.Coordinator.RunAsync(sessionHandleId, options, _events, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Steers an active run without starting a new one.
    /// </summary>
    /// <param name="sessionHandleId">The active session handle.</param>
    /// <param name="options">Steering options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provider run id that accepted the steering input.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle does not reference an active session.</exception>
    public async Task<AgentRunId> SteerAsync(
        AgentSessionHandleId sessionHandleId,
        AgentSteerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await entry.Coordinator.SteerAsync(sessionHandleId, options, _events, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Subscribes to normalized agent events from the active session attachment.
    /// </summary>
    /// <param name="sessionHandleId">The active session handle.</param>
    /// <param name="handler">Event handler.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="IDisposable"/> that unsubscribes when disposed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the handle does not reference an active session.</exception>
    public async Task<IDisposable> SubscribeSessionEventsAsync(
        AgentSessionHandleId sessionHandleId,
        Action<AgentEvent> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await entry.Coordinator.SubscribeAsync(handler, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Retrieves the stored history for the active session attachment (best effort).
    /// </summary>
    /// <param name="sessionHandleId">The active session handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session event history.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle does not reference an active session.</exception>
    public async Task<IReadOnlyList<AgentEvent>> GetSessionHistoryAsync(
        AgentSessionHandleId sessionHandleId,
        CancellationToken cancellationToken = default)
    {
        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await entry.Coordinator.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Aborts/cancels the currently running work in the active session attachment (best effort).
    /// </summary>
    /// <param name="sessionHandleId">The active session handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the handle does not reference an active session.</exception>
    public async Task AbortAsync(AgentSessionHandleId sessionHandleId, CancellationToken cancellationToken = default)
    {
        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try
        {
            await entry.Coordinator.AbortAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Triggers a manual compaction in the active session attachment.
    /// </summary>
    /// <param name="sessionHandleId">The active session handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The compaction outcome when the session supplies one; otherwise <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handle does not reference an active session.</exception>
    public async Task<AgentCompactionOutcome?> CompactAsync(AgentSessionHandleId sessionHandleId, CancellationToken cancellationToken = default)
    {
        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await entry.Coordinator.CompactAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

    /// <summary>
    /// Stops and disposes the active session attachment for a handle, if present.
    /// </summary>
    /// <param name="sessionHandleId">The active session handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task StopSessionAsync(AgentSessionHandleId sessionHandleId, CancellationToken cancellationToken = default)
    {
        SessionEntry? entry = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(sessionHandleId, out entry))
            {
                _sessions.Remove(sessionHandleId);
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
        _events.Complete();

        SessionEntry[] sessions;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task<AgentSessionHandle> AttachSessionAsync(
        ModelProviderId providerId,
        ProviderSessionRuntimeLease runtime,
        IAgentSession session,
        string? parentSessionId,
        CancellationToken cancellationToken)
    {
        var handleId = AgentSessionHandleId.NewVersion7();
        var normalizedParentSessionId = string.IsNullOrWhiteSpace(parentSessionId) ? null : parentSessionId.Trim();
        var handle = new AgentSessionHandle
        {
            HandleId = handleId,
            SessionId = session.SessionId,
            ProviderId = new ModelProviderId(providerId.Value),
            ParentSessionId = normalizedParentSessionId,
        };
        var entry = new SessionEntry(new AgentSessionCoordinator(session), runtime);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _sessions[handleId] = entry;
        }
        finally
        {
            _gate.Release();
        }

        _events.TryPublish(new AgentSessionAttachedEvent(DateTimeOffset.UtcNow, handleId, session.SessionId, new ModelProviderId(providerId.Value), normalizedParentSessionId));
        return handle;
    }

    private async Task<ProviderSessionRuntimeLease> CreateStartedProviderRuntimeAsync(
        ModelProviderId providerId,
        CancellationToken cancellationToken)
    {
        var providerRuntime = await _modelProviderRegistry.CreateRuntimeAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (providerRuntime is IModelProviderSessionRuntime sessionRuntime)
        {
            var sessionLease = new ProviderSessionRuntimeLease(sessionRuntime);
            try
            {
                await sessionLease.StartAsync(cancellationToken).ConfigureAwait(false);
                return sessionLease;
            }
            catch
            {
                await sessionLease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        if (providerRuntime is not IAgentModelProviderRuntime agentProviderRuntime)
        {
            await providerRuntime.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Model provider '{providerId.Value}' does not expose an agent session runtime.");
        }

        var runtime = new AgentRuntime(
            providerId,
            providerRuntime.Descriptor.DisplayName,
            new AgentRuntimeOptions
            {
                StateRootPath = _stateRootPath,
                SessionProjectionCache = _sessionProjectionCache,
                Providers = [agentProviderRuntime.CreateProviderRegistration()],
            });
        try
        {
            await providerRuntime.StartAsync(cancellationToken).ConfigureAwait(false);
            await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            return new ProviderSessionRuntimeLease(runtime);
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            await providerRuntime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SessionEntry> AcquireSessionEntryAsync(
        AgentSessionHandleId sessionHandleId,
        CancellationToken cancellationToken)
    {
        SessionEntry entry;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(sessionHandleId, out entry!))
            {
                throw new InvalidOperationException($"Agent session handle '{sessionHandleId}' does not have an active session.");
            }

            if (!entry.TryAddReference())
            {
                throw new InvalidOperationException($"Agent session handle '{sessionHandleId}' does not have an active session.");
            }
        }
        finally
        {
            _gate.Release();
        }

        return entry;
    }

    private static ModelProviderId ResolveProviderId(AgentSessionCreateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProviderKey))
        {
            throw new InvalidOperationException("Agent session provider key is required to start or resume a session.");
        }

        return new ModelProviderId(options.ProviderKey.Trim());
    }

    private static void EnsureRuntimePreservedRequestedSessionId(string? requestedSessionId, string actualSessionId)
    {
        if (string.IsNullOrWhiteSpace(requestedSessionId))
        {
            return;
        }

        var requested = requestedSessionId.Trim();
        if (string.Equals(requested, actualSessionId, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Agent runtime returned session id '{actualSessionId}' for requested CodeAlta session id '{requested}'. " +
            "Providers must preserve CodeAlta-owned session identifiers.");
    }

    private static async ValueTask DisposeProviderRuntimeAsync(ProviderSessionRuntimeLease runtime)
    {
        try
        {
            await runtime.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown exceptions from provider runtimes that did not finish starting.
        }

        await runtime.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class ProviderSessionRuntimeLease : IAsyncDisposable
    {
        private readonly AgentRuntime? _runtime;
        private readonly IModelProviderSessionRuntime? _sessionRuntime;

        public ProviderSessionRuntimeLease(AgentRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        public ProviderSessionRuntimeLease(IModelProviderSessionRuntime sessionRuntime)
        {
            _sessionRuntime = sessionRuntime ?? throw new ArgumentNullException(nameof(sessionRuntime));
        }

        public Task StartAsync(CancellationToken cancellationToken)
            => _runtime?.StartAsync(cancellationToken) ?? _sessionRuntime!.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default)
            => _runtime?.StopAsync(cancellationToken) ?? _sessionRuntime!.StopAsync(cancellationToken);

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken)
            => _runtime?.CreateSessionAsync(options, cancellationToken) ?? _sessionRuntime!.CreateSessionAsync(options, cancellationToken);

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken)
            => _runtime?.ResumeSessionAsync(sessionId, options, cancellationToken) ?? _sessionRuntime!.ResumeSessionAsync(sessionId, options, cancellationToken);

        public ValueTask DisposeAsync()
            => _runtime?.DisposeAsync() ?? _sessionRuntime!.DisposeAsync();
    }

    private sealed class AgentSessionCoordinator : IAsyncDisposable
    {
        private readonly IAgentSession _session;
        private readonly SemaphoreSlim _runGate = new(initialCount: 1, maxCount: 1);
        private readonly SemaphoreSlim _controlGate = new(initialCount: 1, maxCount: 1);

        public AgentSessionCoordinator(IAgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            _session = session;
        }

        public async Task<AgentRunId> RunAsync(
            AgentSessionHandleId sessionHandleId,
            AgentSendOptions options,
            BoundedRuntimeEventStream<OrchestrationEvent> events,
            CancellationToken cancellationToken)
        {
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var runId = await _session.SendAsync(options, cancellationToken).ConfigureAwait(false);
                events.TryPublish(new RunStartedEvent(DateTimeOffset.UtcNow, sessionHandleId, runId));
                events.TryPublish(new RunCompletedEvent(DateTimeOffset.UtcNow, sessionHandleId, runId));
                return runId;
            }
            catch (Exception ex)
            {
                events.TryPublish(new RunFailedEvent(DateTimeOffset.UtcNow, sessionHandleId, ex.Message));
                throw;
            }
            finally
            {
                _runGate.Release();
            }
        }

        public async Task<AgentRunId> SteerAsync(
            AgentSessionHandleId sessionHandleId,
            AgentSteerOptions options,
            BoundedRuntimeEventStream<OrchestrationEvent> events,
            CancellationToken cancellationToken)
        {
            await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var runId = await _session.SteerAsync(options, cancellationToken).ConfigureAwait(false);
                events.TryPublish(new RunStartedEvent(DateTimeOffset.UtcNow, sessionHandleId, runId));
                events.TryPublish(new RunCompletedEvent(DateTimeOffset.UtcNow, sessionHandleId, runId));
                return runId;
            }
            catch (Exception ex)
            {
                events.TryPublish(new RunFailedEvent(DateTimeOffset.UtcNow, sessionHandleId, ex.Message));
                throw;
            }
            finally
            {
                _controlGate.Release();
            }
        }

        public async Task<IDisposable> SubscribeAsync(Action<AgentEvent> handler, CancellationToken cancellationToken)
        {
            await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return _session.Subscribe(handler);
            }
            finally
            {
                _controlGate.Release();
            }
        }

        public async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken)
        {
            await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _session.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _controlGate.Release();
            }
        }

        public async Task AbortAsync(CancellationToken cancellationToken)
        {
            await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _session.AbortAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _controlGate.Release();
            }
        }

        public async Task<AgentCompactionOutcome?> CompactAsync(CancellationToken cancellationToken)
        {
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_session is IAgentCompactionOutcomeProvider compactionOutcomeProvider)
                {
                    return await compactionOutcomeProvider.CompactWithOutcomeAsync(cancellationToken).ConfigureAwait(false);
                }

                await _session.CompactAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            finally
            {
                _runGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _runGate.Dispose();
            _controlGate.Dispose();
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class SessionEntry : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly ProviderSessionRuntimeLease _providerRuntime;
        private readonly TaskCompletionSource _disposedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _idleCompletion;
        private int _activeReferences;
        private bool _disposeStarted;

        public SessionEntry(AgentSessionCoordinator coordinator, ProviderSessionRuntimeLease providerRuntime)
        {
            Coordinator = coordinator;
            _providerRuntime = providerRuntime;
        }

        public AgentSessionCoordinator Coordinator { get; }

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
                        await Coordinator.AbortAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore best-effort abort failures while the session is being disposed.
                    }

                    await idleTask.ConfigureAwait(false);
                }

                await Coordinator.DisposeAsync().ConfigureAwait(false);
                await DisposeProviderRuntimeAsync(_providerRuntime).ConfigureAwait(false);
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
