using System.Runtime.ExceptionServices;

namespace CodeAlta.Agent;

/// <summary>
/// Caches a provider-independent snapshot of persisted CodeAlta agent sessions.
/// </summary>
public sealed class AgentSessionCatalog : IAgentSessionCatalog
{
    private readonly IAgentSessionStore _store;
    private readonly object _gate = new();
    private IReadOnlyList<AgentSessionMetadata>? _snapshot;
    private CatalogLoadState? _loadState;
    private long _version;
    // The sessions of the snapshot that changed since it was loaded, with the generation of their last change: the next
    // listing reads them again alone, instead of the whole list, when the store can list one session.
    private readonly Dictionary<string, long> _staleSessions = new(StringComparer.OrdinalIgnoreCase);
    private long _staleGeneration;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentSessionCatalog"/> class.
    /// </summary>
    /// <param name="store">Provider-independent session store.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="store" /> is <see langword="null" />.</exception>
    public AgentSessionCatalog(IAgentSessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var load = GetOrStartLoad();
        if (load.Snapshot is not null &&
            await RefreshStaleSessionsAsync(load.Snapshot, cancellationToken).ConfigureAwait(false) is { } refreshed)
        {
            load = new CatalogLoadHandle(refreshed, null);
        }
        else if (load.Snapshot is not null)
        {
            load = GetOrStartLoad();
        }

        if (load.Snapshot is not null)
        {
            foreach (var session in load.Snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MatchesFilter(session, filter))
                {
                    yield return session;
                }
            }

            yield break;
        }

        await foreach (var session in StreamLoadAsync(load.State!, filter, cancellationToken).ConfigureAwait(false))
        {
            yield return session;
        }
    }

    /// <inheritdoc />
    public Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            InvalidateAll();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// While the catalog holds a snapshot and its store can list one session, only that session is read again, at the next
    /// listing, the way the store lists it; otherwise the whole snapshot is invalidated.
    /// </remarks>
    public Task InvalidateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_snapshot is not null && _store is IAgentSessionListingSource)
            {
                _staleSessions[sessionId.Trim()] = ++_staleGeneration;
            }
            else
            {
                InvalidateAll();
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifySessionCreatedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return InvalidateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task NotifySessionResumedAsync(string sessionId, CancellationToken cancellationToken = default)
        => InvalidateAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task NotifySessionDeletedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return InvalidateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var deleted = await _store.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await NotifySessionDeletedAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <inheritdoc />
    public Task NotifySessionUpdatedAsync(string sessionId, CancellationToken cancellationToken = default)
        => InvalidateAsync(sessionId, cancellationToken);

    // Under _gate.
    private void InvalidateAll()
    {
        var loadState = _loadState;
        _version++;
        _snapshot = null;
        _loadState = null;
        _staleSessions.Clear();
        loadState?.Complete(error: null);
    }

    // Reads the stale sessions of a snapshot again, one by one, and returns the snapshot with them; null when the snapshot
    // is no longer the one to use (it was invalidated, or the store does not know a stale session): the caller loads again.
    private async Task<IReadOnlyList<AgentSessionMetadata>?> RefreshStaleSessionsAsync(
        IReadOnlyList<AgentSessionMetadata> snapshot,
        CancellationToken cancellationToken)
    {
        if (_store is not IAgentSessionListingSource listing)
        {
            return snapshot;
        }

        while (true)
        {
            KeyValuePair<string, long>[] stale;
            long version;
            lock (_gate)
            {
                if (_snapshot is null)
                {
                    return null;
                }

                snapshot = _snapshot;
                if (_staleSessions.Count == 0)
                {
                    return snapshot;
                }

                stale = [.. _staleSessions];
                version = _version;
            }

            var refreshed = new List<AgentSessionMetadata>(stale.Length);
            foreach (var (sessionId, _) in stale)
            {
                var (known, session) = await listing.TryGetListedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (!known)
                {
                    lock (_gate)
                    {
                        if (_version == version)
                        {
                            InvalidateAll();
                        }
                    }

                    return null;
                }

                if (session is not null)
                {
                    refreshed.Add(session);
                }
            }

            lock (_gate)
            {
                if (_version != version || !ReferenceEquals(_snapshot, snapshot))
                {
                    continue;
                }

                var replaced = new HashSet<string>(stale.Select(static pair => pair.Key), StringComparer.OrdinalIgnoreCase);
                _snapshot = snapshot
                    .Where(session => !replaced.Contains(session.SessionId))
                    .Concat(refreshed)
                    .OrderByDescending(static session => session.UpdatedAt)
                    .ThenByDescending(static session => session.SessionId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var (sessionId, generation) in stale)
                {
                    if (_staleSessions.TryGetValue(sessionId, out var current) && current == generation)
                    {
                        _staleSessions.Remove(sessionId);
                    }
                }
            }
        }
    }

    private CatalogLoadHandle GetOrStartLoad()
    {
        CatalogLoadState? state;
        var startLoad = false;
        lock (_gate)
        {
            if (_snapshot is not null)
            {
                return new CatalogLoadHandle(_snapshot, null);
            }

            state = _loadState;
            if (state is null)
            {
                state = new CatalogLoadState(_version);
                _loadState = state;
                startLoad = true;
            }
        }

        if (startLoad)
        {
            _ = Task.Run(() => LoadSnapshotAsync(state), CancellationToken.None);
        }

        return new CatalogLoadHandle(null, state);
    }

    private async Task LoadSnapshotAsync(CatalogLoadState state)
    {
        try
        {
            var sessions = new List<AgentSessionMetadata>();
            await foreach (var session in _store.ListSessionsAsync(filter: null, CancellationToken.None).ConfigureAwait(false))
            {
                sessions.Add(session);
                if (!TryPublishLoadedSession(state, session))
                {
                    return;
                }
            }

            var snapshot = sessions
                .OrderByDescending(static session => session.UpdatedAt)
                .ThenByDescending(static session => session.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            lock (_gate)
            {
                if (_version == state.Version && ReferenceEquals(_loadState, state))
                {
                    _snapshot = snapshot;
                    _loadState = null;
                    state.Complete(error: null);
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_version == state.Version && ReferenceEquals(_loadState, state))
                {
                    _loadState = null;
                    state.Complete(ex);
                }
            }
        }
    }

    private bool TryPublishLoadedSession(CatalogLoadState state, AgentSessionMetadata session)
    {
        lock (_gate)
        {
            if (_version != state.Version || !ReferenceEquals(_loadState, state))
            {
                return false;
            }

            state.Sessions.Add(session);
            state.SignalChange();
            return true;
        }
    }

    private async IAsyncEnumerable<AgentSessionMetadata> StreamLoadAsync(
        CatalogLoadState state,
        AgentSessionListFilter? filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var nextIndex = 0;
        while (true)
        {
            AgentSessionMetadata? session = null;
            Task? waitTask = null;
            Exception? error = null;
            var completed = false;

            lock (_gate)
            {
                if (nextIndex < state.Sessions.Count)
                {
                    session = state.Sessions[nextIndex++];
                }
                else if (state.IsCompleted)
                {
                    completed = true;
                    error = state.Error;
                }
                else
                {
                    waitTask = state.ChangeSignal.Task;
                }
            }

            if (session is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MatchesFilter(session, filter))
                {
                    yield return session;
                }

                continue;
            }

            if (completed)
            {
                if (error is not null)
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }

                yield break;
            }

            await waitTask!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool MatchesFilter(AgentSessionMetadata session, AgentSessionListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

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

    private readonly record struct CatalogLoadHandle(
        IReadOnlyList<AgentSessionMetadata>? Snapshot,
        CatalogLoadState? State);

    private sealed class CatalogLoadState
    {
        public CatalogLoadState(long version)
        {
            Version = version;
        }

        public long Version { get; }

        public List<AgentSessionMetadata> Sessions { get; } = [];

        public TaskCompletionSource ChangeSignal { get; private set; } = CreateChangeSignal();

        public bool IsCompleted { get; private set; }

        public Exception? Error { get; private set; }

        public void SignalChange()
        {
            var signal = ChangeSignal;
            ChangeSignal = CreateChangeSignal();
            signal.TrySetResult();
        }

        public void Complete(Exception? error)
        {
            if (IsCompleted)
            {
                return;
            }

            Error = error;
            IsCompleted = true;
            ChangeSignal.TrySetResult();
        }

        private static TaskCompletionSource CreateChangeSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
