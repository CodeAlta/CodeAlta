using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using System.Runtime.ExceptionServices;

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
    private volatile bool _disposed;
    private readonly object _disposalGate = new();
    private Task? _disposeTask;
    private SessionEntry[]? _disposalEntries;

    internal IReadOnlyList<ModelProviderDescriptor> SelectionProviders => _modelProviderRegistry.ListProviders();

    /// <summary>The version of the registration of a provider (<see cref="ModelProviderRegistry.GetRegistrationVersion"/>); 0 when it is not registered.</summary>
    internal long GetProviderRegistrationVersion(string? providerKey)
        => string.IsNullOrWhiteSpace(providerKey) ? 0 : _modelProviderRegistry.GetRegistrationVersion(new ModelProviderId(providerKey.Trim()));

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

        return await RunAsync(sessionHandleId, () => options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentRunId> RunAsync(
        AgentSessionHandleId sessionHandleId,
        Func<AgentSendOptions?> takeOptions,
        CancellationToken cancellationToken)
    {
        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        var original = entry.Lifetime.RecordRun(() => entry.Coordinator.RunAsync(sessionHandleId, takeOptions, _events, cancellationToken));
        try
        {
            original.Launch();
            if (await original.Outcome.ConfigureAwait(false) is not null)
                ExceptionDispatchInfo.Capture(original.Failure!).Throw();
            return ((Task<AgentRunId>)original.Original!).GetAwaiter().GetResult();
        }
        finally
        {
            entry.Lifetime.CompleteReference(original.Failure, original);
        }
    }

    // The provider of the session started a turn by itself (a background command of an agent CLI ended): a run is
    // started for it, in its turn among the runs of the session, so that it is shown and recorded as any other.
    private async Task RunProviderInitiatedAsync(AgentSessionHandleId sessionHandleId, IAgentProviderInitiatedRuns session)
    {
        try
        {
            await RunAsync(sessionHandleId, session.TakeProviderInitiatedRun, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nobody waits for this run. A session that is gone has nothing to show, and the failure of a run is
            // reported by the session itself, as for a run that was sent.
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

    /// <summary>Asks the provider of an existing attachment to stop one of its background tasks.</summary>
    /// <param name="sessionHandleId">Existing attachment identity; never acquired from a catalog or replaced.</param>
    /// <param name="taskId">The identity of the task for its provider.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the provider took the request; false for a session whose provider has no background tasks.</returns>
    /// <exception cref="ArgumentException">The task identity is blank.</exception>
    /// <exception cref="InvalidOperationException">The handle no longer admits references.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The hub or provider is disposed.</exception>
    public async Task<bool> StopBackgroundTaskAsync(AgentSessionHandleId sessionHandleId, string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try { return await entry.Coordinator.StopBackgroundTaskAsync(taskId, cancellationToken).ConfigureAwait(false); }
        finally { entry.ReleaseReference(); }
    }

    /// <summary>Sets the permission mode the next runs of an attached session request, without attaching it again.</summary>
    /// <param name="sessionHandleId">Existing attachment identity.</param>
    /// <param name="permissionMode">One of the permission modes of the provider, or null for the one it is configured with.</param>
    /// <param name="cancellationToken">Cancels admission.</param>
    /// <returns>Whether the session took the mode; false for a session that does not take one.</returns>
    /// <exception cref="InvalidOperationException">The handle no longer admits references.</exception>
    /// <exception cref="OperationCanceledException">Admission was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The hub or provider is disposed.</exception>
    public async Task<bool> SetPermissionModeAsync(AgentSessionHandleId sessionHandleId, string? permissionMode, CancellationToken cancellationToken = default)
    {
        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try { return entry.Coordinator.SetPermissionMode(permissionMode); }
        finally { entry.ReleaseReference(); }
    }

    /// <summary>Signals only the expected run on an existing attachment and joins original cancellation work.</summary>
    /// <param name="sessionHandleId">Existing attachment identity; never acquired from a catalog or replaced.</param>
    /// <param name="expectedRunId">Immutable original provider run identity.</param>
    /// <param name="cancellationToken">Cancels admission, not cancellation work already admitted by the provider.</param>
    /// <returns>Exact provider cancellation outcome, not confirmation that the run stopped.</returns>
    /// <exception cref="ArgumentException">The run identity is blank.</exception>
    /// <exception cref="InvalidOperationException">The handle no longer admits references.</exception>
    /// <exception cref="NotSupportedException">The session lacks exact cancellation; no fallback is invoked.</exception>
    /// <exception cref="OperationCanceledException">Admission was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The hub or provider is disposed.</exception>
    /// <exception cref="Exception">Provider cancellation failed, possibly after signalling.</exception>
    public async Task<AgentTargetedAbortOutcome> AbortRunAsync(AgentSessionHandleId sessionHandleId,
        AgentRunId expectedRunId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRunId.Value);
        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try { return await entry.Coordinator.AbortRunAsync(expectedRunId, cancellationToken).ConfigureAwait(false); }
        finally { entry.ReleaseReference(); }
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

    /// <summary>Attempts settled idle compaction on this exact attachment without waiting for run admission.</summary>
    /// <param name="sessionHandleId">The existing session attachment; never replaced or acquired from a catalog.</param>
    /// <param name="cancellationToken">Cancels admission or actual compaction.</param>
    /// <returns>Null only for busy refusal without starting compaction; otherwise the actual settled outcome.</returns>
    /// <exception cref="InvalidOperationException">The handle is not active.</exception>
    /// <exception cref="NotSupportedException">The session does not support idle compaction; unconditional compaction is never used.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="ObjectDisposedException">The hub or session is disposed.</exception>
    /// <exception cref="Exception">The actual provider compaction failed.</exception>
    public async Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(AgentSessionHandleId sessionHandleId, CancellationToken cancellationToken = default)
    {
        var entry = await AcquireSessionEntryAsync(sessionHandleId, cancellationToken).ConfigureAwait(false);
        try { return await entry.Coordinator.TryCompactWhenIdleAsync(cancellationToken).ConfigureAwait(false); }
        finally { entry.ReleaseReference(); }
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
            _sessions.TryGetValue(sessionHandleId, out entry);
        }
        finally
        {
            _gate.Release();
        }

        if (entry is not null)
        {
            entry.Lifetime.BeginShutdown();
            try { await entry.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                if (entry.Lifetime.DependenciesReleased)
                {
                    await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try { _sessions.Remove(sessionHandleId); }
                    finally { _gate.Release(); }
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource launch;
        Task original;
        lock (_disposalGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            original = _disposeTask = DisposeCoreAsync(launch.Task);
        }
        launch.TrySetResult();
        return new(original);
    }

    private async Task DisposeCoreAsync(Task launch)
    {
        await launch.ConfigureAwait(false);
        _events.Complete();

        SessionEntry[] sessions;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            sessions = _sessions.Values.ToArray();
            _disposalEntries = sessions;
        }
        finally
        {
            _gate.Release();
        }

        // Start every independent abort before any session disposal joins an active reference.
        foreach (var session in sessions) session.Lifetime.BeginShutdown();
        var disposals = sessions.Select(session => new SessionPermissionService.DeliveryStage(() => session.DisposeAsync().AsTask())).ToArray();
        foreach (var disposal in disposals) disposal.Launch();
        var failures = new List<Exception>();
        foreach (var disposal in disposals)
            if (await disposal.Outcome.ConfigureAwait(false) is not null) failures.Add(disposal.Failure!);
        if (failures.Any(OwnedProviderEventForwarding.HasRetention))
            throw new AgentDependencyRetentionException("hub", "session disposal", failures, new { Owner = this, Entries = _disposalEntries, Disposals = disposals });
        _sessions.Clear();
        _gate.Dispose();
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessions[handleId] = entry;
        }
        finally
        {
            _gate.Release();
        }

        if (session is IAgentProviderInitiatedRuns providerRuns)
        {
            // The handler is called while the provider is read: the run starts elsewhere.
            entry.Coordinator.ObserveProviderRuns(providerRuns.OnProviderInitiatedRun(
                () => _ = Task.Run(() => RunProviderInitiatedAsync(handleId, providerRuns))));
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
        runtime.StopOriginal = new SessionPermissionService.DeliveryStage(() => runtime.StopAsync());
        runtime.StopOriginal.Launch();
        var stopFailure = await runtime.StopOriginal.Outcome.ConfigureAwait(false);
        if (stopFailure is not null && OwnedProviderEventForwarding.HasRetention(runtime.StopOriginal.Failure!))
            throw new AgentDependencyRetentionException("provider runtime", "stop", [runtime.StopOriginal.Failure!], runtime);
        runtime.DisposalOriginal = new SessionPermissionService.DeliveryStage(() => runtime.DisposeAsync().AsTask());
        runtime.DisposalOriginal.Launch();
        if (await runtime.DisposalOriginal.Outcome.ConfigureAwait(false) is not null)
            throw new AgentDependencyRetentionException("provider runtime", "disposal",
                stopFailure is null ? [runtime.DisposalOriginal.Failure!] : [runtime.StopOriginal.Failure!, runtime.DisposalOriginal.Failure!], runtime);
        if (stopFailure is not null) ExceptionDispatchInfo.Capture(runtime.StopOriginal.Failure!).Throw();
    }

    private sealed class ProviderSessionRuntimeLease : IAsyncDisposable
    {
        private readonly AgentRuntime? _runtime;
        private readonly IModelProviderSessionRuntime? _sessionRuntime;
        internal SessionPermissionService.DeliveryStage? StopOriginal { get; set; }
        internal SessionPermissionService.DeliveryStage? DisposalOriginal { get; set; }

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
        private readonly CoordinatorFailureOwner _failureOwner;
        private IDisposable? _providerRuns;

        public AgentSessionCoordinator(IAgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            _session = session;
            _failureOwner = new CoordinatorFailureOwner(this);
        }

        /// <summary>Keeps the registration of the runs the provider of the session asks for, until the session is released.</summary>
        public void ObserveProviderRuns(IDisposable registration) => _providerRuns = registration;

        // The options are taken once the run has its turn: a run the provider asked for has nothing left to show
        // when a run that was waiting before it read the turn of the provider, and then does not start.
        public async Task<AgentRunId> RunAsync(
            AgentSessionHandleId sessionHandleId,
            Func<AgentSendOptions?> takeOptions,
            BoundedRuntimeEventStream<OrchestrationEvent> events,
            CancellationToken cancellationToken)
        {
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var invocation = new OwnedSessionCommandService.OriginalInvocation();
            try
            {
                if (takeOptions() is not { } options) return default;
                var runId = await _failureOwner.RunAsync(invocation, () => _session.SendAsync(options, cancellationToken)).ConfigureAwait(false);
                events.TryPublish(new RunStartedEvent(DateTimeOffset.UtcNow, sessionHandleId, runId));
                events.TryPublish(new RunCompletedEvent(DateTimeOffset.UtcNow, sessionHandleId, runId));
                return runId;
            }
            catch (Exception ex)
            {
                events.TryPublish(new RunFailedEvent(DateTimeOffset.UtcNow, sessionHandleId, (invocation.AwaitedFailure ?? ex).Message));
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
            var invocation = new OwnedSessionCommandService.OriginalInvocation();
            // This capability explicitly promises concurrent cancellation/control-read safety.
            // In particular retirement must not hold the control gate while joining a traversal
            // whose callback needs that gate. Legacy providers retain their serialization contract.
            if (_session is IAgentTargetedAbortProvider)
            {
                await _failureOwner.AbortAsync(invocation, () => _session.AbortAsync(cancellationToken)).ConfigureAwait(false);
                return;
            }
            await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _failureOwner.AbortAsync(invocation, () => _session.AbortAsync(cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                _controlGate.Release();
            }
        }

        public Task<bool> StopBackgroundTaskAsync(string taskId, CancellationToken cancellationToken)
            => _session is IAgentBackgroundTaskProvider provider ? provider.StopBackgroundTaskAsync(taskId, cancellationToken) : Task.FromResult(false);

        public bool SetPermissionMode(string? permissionMode)
        {
            if (_session is not IAgentPermissionModeProvider provider) return false;
            provider.SetPermissionMode(permissionMode);
            return true;
        }

        public Task<AgentTargetedAbortOutcome> AbortRunAsync(AgentRunId expectedRunId, CancellationToken cancellationToken)
            => _session is IAgentTargetedAbortProvider provider
                ? provider.AbortRunAsync(expectedRunId, cancellationToken)
                : throw new NotSupportedException("The session does not support exact-run cancellation.");

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

        public async Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(CancellationToken cancellationToken)
        {
            if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
            try
            {
                if (_session is not IAgentIdleCompactionProvider provider)
                    throw new NotSupportedException("The session does not support idle compaction.");
                return await provider.TryCompactWhenIdleAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _runGate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _providerRuns, null)?.Dispose();
            await _failureOwner.DisposeAsync(_session.DisposeAsync, () =>
            {
                _runGate.Dispose();
                _controlGate.Dispose();
            }).ConfigureAwait(false);
        }
    }

    // Coordinator-specific evidence ownership, shared with inert fixtures. It does not schedule
    // provider work: the existing run/control gates and entry reference owner retain that authority.
    internal sealed class CoordinatorFailureOwner(object dependencies)
    {
        private readonly object _gate = new();
        private readonly List<(Exception Failure, OwnedSessionCommandService.OriginalInvocation Invocation)> _retained = [];
        private Task? _disposal;
        internal bool Retained { get { lock (_gate) return _retained.Count != 0; } }
        internal OwnedSessionCommandService.OriginalInvocation SessionDisposal { get; } = new();

        internal async Task<T> RunAsync<T>(OwnedSessionCommandService.OriginalInvocation invocation, Func<Task<T>> send)
        {
            ThrowIfRetained();
            try { return await invocation.RunAsync(send).ConfigureAwait(false); }
            catch (Exception failure) { ExceptionDispatchInfo.Throw(Capture(invocation, failure, false)); throw; }
        }

        internal async Task AbortAsync(OwnedSessionCommandService.OriginalInvocation invocation, Func<Task> abort)
        {
            // Abort remains independently eligible, including after a retained send result.
            invocation.Launch(abort);
            if (await invocation.Outcome.ConfigureAwait(false) is { } failure)
                ExceptionDispatchInfo.Throw(Capture(invocation, failure, true));
        }

        internal Task DisposeAsync(Func<ValueTask> disposeSession, Action releaseGates)
        {
            ArgumentNullException.ThrowIfNull(disposeSession);
            ArgumentNullException.ThrowIfNull(releaseGates);
            TaskCompletionSource launch;
            Task disposal;
            lock (_gate)
            {
                if (_disposal is not null) return _disposal;
                launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                disposal = _disposal = DisposeCoreAsync(launch.Task, disposeSession, releaseGates);
            }
            launch.TrySetResult();
            return disposal;
        }

        private async Task DisposeCoreAsync(Task launch, Func<ValueTask> disposeSession, Action releaseGates)
        {
            await launch.ConfigureAwait(false);
            ThrowIfRetained();
            SessionDisposal.Launch(() => disposeSession().AsTask());
            var failure = await SessionDisposal.Outcome.ConfigureAwait(false);
            if (failure is not null) failure = Capture(SessionDisposal, failure, true);
            ThrowIfRetained();
            try { releaseGates(); }
            catch (Exception releaseFailure)
            {
                throw new AgentDependencyRetentionException("hub coordinator", "gate release",
                    failure is null ? [releaseFailure] : [failure, releaseFailure], this);
            }
            if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        }

        private Exception Capture(OwnedSessionCommandService.OriginalInvocation invocation, Exception failure, bool requiresOriginal)
        {
            var evidence = invocation.OriginalFaults is { InnerExceptions.Count: > 1 } faults ? faults : failure;
            if (requiresOriginal && invocation.Original is null)
                evidence = new AgentDependencyRetentionException("hub coordinator", "missing cleanup original", [evidence], invocation);
            if (OwnedProviderEventForwarding.HasRetention(evidence))
            {
                lock (_gate) _retained.Add((evidence, invocation));
            }
            return evidence;
        }

        private void ThrowIfRetained()
        {
            Exception[] failures;
            lock (_gate) failures = _retained.Select(static item => item.Failure).ToArray();
            if (failures.Length != 0)
                throw new AgentDependencyRetentionException("hub coordinator", "retained prerequisite", failures,
                    new { Owner = dependencies, Lifetime = this });
        }
    }

    private sealed class SessionEntry : IAsyncDisposable
    {
        public SessionEntry(AgentSessionCoordinator coordinator, ProviderSessionRuntimeLease providerRuntime)
        {
            Coordinator = coordinator;
            Lifetime = new SessionEntryLifetime(this, () => coordinator.AbortAsync(CancellationToken.None),
                coordinator.DisposeAsync, () => DisposeProviderRuntimeAsync(providerRuntime));
        }
        public AgentSessionCoordinator Coordinator { get; }
        internal SessionEntryLifetime Lifetime { get; }
        public bool TryAddReference() => Lifetime.TryAddReference();
        public void ReleaseReference() => Lifetime.CompleteReference(null, null);
        public ValueTask DisposeAsync() => Lifetime.DisposeAsync();
    }

    internal sealed class SessionEntryLifetime(object dependencies, Func<Task> abort,
        Func<ValueTask> disposeSession, Func<ValueTask> disposeProvider)
    {
        private readonly object _gate = new();
        private readonly List<(Exception Failure, object? Original)> _retained = [];
        private readonly List<SessionPermissionService.DeliveryStage> _runs = [];
        private TaskCompletionSource? _idle;
        private bool _stopping;
        private Task? _disposal;
        internal int ActiveReferences { get; private set; }
        internal int ReleasedReferences { get; private set; }
        internal int RetainedReferences { get { lock (_gate) return _retained.Count; } }
        internal bool DependenciesReleased { get; private set; }
        internal SessionPermissionService.DeliveryStage? Abort { get; private set; }
        internal SessionPermissionService.DeliveryStage? SessionDisposal { get; private set; }
        internal SessionPermissionService.DeliveryStage? ProviderDisposal { get; private set; }

        internal bool TryAddReference()
        {
            lock (_gate)
            {
                if (_stopping || _retained.Count != 0) return false;
                ActiveReferences++;
                return true;
            }
        }
        internal SessionPermissionService.DeliveryStage RecordRun(Func<Task> invoke)
        {
            var run = new SessionPermissionService.DeliveryStage(invoke);
            lock (_gate) _runs.Add(run);
            return run;
        }
        internal void CompleteReference(Exception? failure, object? original)
        {
            lock (_gate)
            {
                if (ActiveReferences == 0) throw new InvalidOperationException("Session reference count is already zero.");
                if (failure is not null && OwnedProviderEventForwarding.HasRetention(failure)) _retained.Add((failure, original));
                else
                {
                    ReleasedReferences++;
                    if (original is SessionPermissionService.DeliveryStage run) _runs.Remove(run);
                }
                if (--ActiveReferences == 0) _idle?.TrySetResult();
            }
        }
        internal void BeginShutdown()
        {
            SessionPermissionService.DeliveryStage control;
            lock (_gate)
            {
                if (_stopping) return;
                _stopping = true;
                if (ActiveReferences != 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                control = Abort = new SessionPermissionService.DeliveryStage(
                    ActiveReferences != 0 || _retained.Count != 0 ? abort : static () => Task.CompletedTask);
            }
            control.Launch();
        }
        internal ValueTask DisposeAsync()
        {
            BeginShutdown();
            TaskCompletionSource launch;
            Task original;
            lock (_gate)
            {
                if (_disposal is not null) return new(_disposal);
                launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                original = _disposal = DisposeCoreAsync(launch.Task, _idle?.Task ?? Task.CompletedTask);
            }
            launch.TrySetResult();
            return new(original);
        }
        private async Task DisposeCoreAsync(Task launch, Task activeReferences)
        {
            await launch.ConfigureAwait(false);
            var failures = new List<Exception>();
            // Abort was initiated before either wait. The idle receipt counts active, not retained uses.
            await activeReferences.ConfigureAwait(false);
            if (await Abort!.Outcome.ConfigureAwait(false) is not null) failures.Add(Abort.Failure!);
            lock (_gate) failures.AddRange(_retained.Select(static use => use.Failure));
            if (Abort.Original is null || failures.Any(OwnedProviderEventForwarding.HasRetention))
                throw new AgentDependencyRetentionException("hub entry", "retained run", failures, new { Owner = dependencies, Lifetime = this });
            SessionDisposal = new SessionPermissionService.DeliveryStage(() => disposeSession().AsTask());
            SessionDisposal.Launch();
            if (await SessionDisposal.Outcome.ConfigureAwait(false) is not null) failures.Add(SessionDisposal.Failure!);
            if (SessionDisposal.Original is null || failures.Any(OwnedProviderEventForwarding.HasRetention))
                throw new AgentDependencyRetentionException("hub entry", "session release", failures, new { Owner = dependencies, Lifetime = this });
            ProviderDisposal = new SessionPermissionService.DeliveryStage(() => disposeProvider().AsTask());
            ProviderDisposal.Launch();
            if (await ProviderDisposal.Outcome.ConfigureAwait(false) is not null)
            {
                failures.Add(ProviderDisposal.Failure!);
                if (ProviderDisposal.Original is null || OwnedProviderEventForwarding.HasRetention(ProviderDisposal.Failure!))
                    throw new AgentDependencyRetentionException("hub entry", "provider release", failures, new { Owner = dependencies, Lifetime = this });
            }
            DependenciesReleased = true;
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }
}
