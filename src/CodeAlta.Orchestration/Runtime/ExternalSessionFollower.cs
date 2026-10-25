using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using XenoAtom.Logging;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What happened to a session another process writes.</summary>
public enum ExternalSessionChangeKind
{
    /// <summary>A marker named the session, which is now listed: the command line created it.</summary>
    Listed,

    /// <summary>Another process took the lease of the session: it runs or shows it, and the session is read-only here.</summary>
    Held,

    /// <summary>The journal of a session another process holds grew, and its listing was projected again.</summary>
    Updated,

    /// <summary>The other process released the lease: the session can be continued here.</summary>
    Released,
}

/// <summary>A change of a session another process writes.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Kind">What happened.</param>
/// <param name="HolderKind">The kind of the process that holds it (<c>altacli</c>, <c>desktop</c>, <c>tui</c>), when it holds it.</param>
public sealed record ExternalSessionChange(string SessionId, ExternalSessionChangeKind Kind, string? HolderKind);

/// <summary>A session another process holds, as it is followed.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="HolderKind">The kind of the holder (<c>altacli</c>, <c>desktop</c>, <c>tui</c>); null when its lease does not say it.</param>
/// <param name="HolderProcessId">The process of the holder, when the lease says it.</param>
/// <param name="JournalLength">The length of its journal when it was last projected, which grows as the session runs; null when it has none yet.</param>
public sealed record ExternalSessionHold(string SessionId, string? HolderKind, int? HolderProcessId, long? JournalLength);

/// <summary>
/// Lists and follows the sessions another process writes into the sessions folder of a host: the command line names each
/// session it creates in a marker (<see cref="SessionMarkerStore"/>), and holds the lease of each session it runs
/// (<see cref="SessionLeaseStore"/>). The follower projects the sessions the markers name into the application database,
/// then deletes the markers; it follows the sessions another process holds, projecting their journals again as they grow
/// (about once a second, only while they are held), and once more when they are released. A marker whose journal is not a
/// session yet is looked at again at that pace too.
/// </summary>
/// <remarks>
/// Only a host that owns the application database of its state root runs one (CodeAlta Desktop and TUI). The folders are
/// watched, and listed again on a bounded period, since a watcher can drop events. The projection reads only what the listing
/// reads of a journal. <see cref="Changed"/> is raised on the follower's own task: its handlers must return quickly.
/// </remarks>
public sealed class ExternalSessionFollower : IAsyncDisposable
{
    private static readonly Logger Log = LogManager.GetLogger("CodeAlta.ExternalSessions");

    private readonly SessionMarkerStore _markers;
    private readonly SessionLeaseStore _leases;
    private readonly FileSystemAgentSessionStore _store;
    private readonly IAgentSessionCatalog _catalog;
    private readonly SessionRuntimeService _runtime;
    private readonly object _gate = new();
    private readonly Dictionary<string, Followed> _followed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly CancellationTokenSource _stop = new();
    // One pass at a time: the period, a sign of the watchers, or a caller's refresh.
    private readonly SemaphoreSlim _pass = new(1, 1);
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private int _disposed;

    /// <summary>Creates the follower of the sessions folder of a host.</summary>
    /// <param name="options">The catalog options of the host: its state root holds the sessions, the markers and the leases.</param>
    /// <param name="store">The journal store of the host, whose listing cache the sessions are projected into.</param>
    /// <param name="catalog">The session catalog of the host, told of each session that changed.</param>
    /// <param name="runtime">The runtime of the host: the leases it holds are its own, not another process's.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ExternalSessionFollower(CatalogOptions options, FileSystemAgentSessionStore store, IAgentSessionCatalog catalog, SessionRuntimeService runtime)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(runtime);
        _markers = new SessionMarkerStore(options);
        _leases = new SessionLeaseStore(options);
        _store = store;
        _catalog = catalog;
        _runtime = runtime;
    }

    /// <summary>Gets how often the journals of the sessions another process holds, and the markers kept, are looked at.</summary>
    public TimeSpan FollowInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how often the markers and the leases are listed again without a sign from the watchers.</summary>
    public TimeSpan RelistInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised when a session another process writes was listed, taken, updated or released.</summary>
    public event EventHandler<ExternalSessionChange>? Changed;

    /// <summary>Gets the sessions another process holds now, as last looked at.</summary>
    public IReadOnlyList<ExternalSessionHold> Held
    {
        get
        {
            lock (_gate) return [.. _followed.Values.Select(static followed => followed.ToHold())];
        }
    }

    /// <summary>Gets the session another process holds, as last looked at.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The hold; <see langword="null"/> when no other process holds the session.</returns>
    public ExternalSessionHold? GetHold(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_gate) return _followed.TryGetValue(sessionId.Trim(), out var followed) ? followed.ToHold() : null;
    }

    /// <summary>Starts watching: the markers written while no instance ran are read at once. Repeated calls do nothing.</summary>
    /// <exception cref="ObjectDisposedException">The follower is disposed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            if (_loop is not null) return;
            Watch(_markers.MarkersDirectory);
            Watch(_leases.LeasesDirectory);
            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>
    /// Lists the markers and the leases and follows what changed, once: what the follower does on each sign of its watchers
    /// and on its period.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProjectMarkersAsync(cancellationToken).ConfigureAwait(false);
            DiscoverHeld();
            await FollowAsync(lookUpJournals: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    /// <summary>Stops watching and following.</summary>
    /// <returns>A task that completes when the follower's task ended.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        Task? loop;
        lock (_gate)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
            loop = _loop;
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _stop.Dispose();
    }

    // Under _gate. A folder that does not exist yet is created, so that its first file is seen.
    private void Watch(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, _) => Signal();
            watcher.Deleted += (_, _) => Signal();
            watcher.Renamed += (_, _) => Signal();
            watcher.Changed += (_, _) => Signal();
            watcher.Error += (_, _) => Signal();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            // Without a watcher the period lists the folder again.
            Log.Warn($"The folder '{directory}' is not watched: {exception.Message}");
        }
    }

    private void Signal() => Interlocked.Exchange(ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private async Task RunAsync()
    {
        var token = _stop.Token;
        var lastRelist = DateTimeOffset.MinValue;
        // The sign the loop waits for, kept from one turn to the next: a watcher completes it and puts a new one in its
        // place, which the loop takes only when it lists the folders again.
        var signal = Volatile.Read(ref _signal).Task;
        var pending = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _pass.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    if (signal.IsCompleted || now - lastRelist >= RelistInterval)
                    {
                        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        Interlocked.Exchange(ref _signal, next);
                        signal = next.Task;
                        lastRelist = now;
                        pending = await ProjectMarkersAsync(token).ConfigureAwait(false);
                        DiscoverHeld();
                        await FollowAsync(lookUpJournals: true, token).ConfigureAwait(false);
                    }
                    else
                    {
                        // A marker whose journal is not a session yet is looked at again with the sessions followed.
                        if (pending) pending = await ProjectMarkersAsync(token).ConfigureAwait(false);
                        await FollowAsync(lookUpJournals: false, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _pass.Release();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A pass that fails is made again on the next sign or period: the follower never stops on one failure.
                Log.Warn($"The sessions written by another process could not be read: {exception.Message}");
            }

            bool following;
            lock (_gate) following = _followed.Count > 0;
            var wait = following || pending ? FollowInterval : RelistInterval;
            try
            {
                // A burst of watcher events (the leases of the tabs of this instance) is taken in one pass.
                await Task.WhenAny(signal, Task.Delay(wait, token)).ConfigureAwait(false);
                if (signal.IsCompleted) await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // Projects the sessions the markers name, then deletes the markers; a marker whose journal is gone is dropped, one whose
    // journal is not a session yet is kept for the next pass. Returns whether one was kept.
    private async Task<bool> ProjectMarkersAsync(CancellationToken cancellationToken)
    {
        var kept = false;
        foreach (var marker in _markers.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string journal;
            try
            {
                journal = _markers.GetJournalPath(marker);
            }
            catch (ArgumentException)
            {
                Drop(marker.SessionId);
                continue;
            }

            if (!File.Exists(journal))
            {
                Drop(marker.SessionId);
                continue;
            }

            if (!await _store.ProjectJournalAsync(journal, cancellationToken).ConfigureAwait(false))
            {
                kept = true;
                continue;
            }

            await _catalog.InvalidateAsync(marker.SessionId, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_followed.TryGetValue(marker.SessionId, out var followed)) followed.Journal = journal;
            }

            Drop(marker.SessionId);
            Raise(new ExternalSessionChange(marker.SessionId, ExternalSessionChangeKind.Listed, null));
        }

        return kept;
    }

    private void Drop(string sessionId)
    {
        try
        {
            _markers.Delete(sessionId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Deleted at the next pass; projecting it again changes nothing.
        }
    }

    // The sessions whose lease another process took since the last look.
    private void DiscoverHeld()
    {
        var own = _runtime.GetHeldSessionLeases().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = new List<ExternalSessionChange>();
        foreach (var holder in _leases.ListHeld())
        {
            if (own.Contains(holder.SessionId)) continue;
            lock (_gate)
            {
                if (_followed.ContainsKey(holder.SessionId)) continue;
                _followed[holder.SessionId] = new Followed(holder);
            }

            taken.Add(new ExternalSessionChange(holder.SessionId, ExternalSessionChangeKind.Held, holder.OwnerKind));
        }

        foreach (var change in taken) Raise(change);
    }

    // Projects the journals of the followed sessions that grew, and once more those that were released. The journal of a
    // session is the one its marker names; the store looks for it (in every journal, at worst) only when the folders are
    // listed again (lookUpJournals), not at each look.
    private async Task FollowAsync(bool lookUpJournals, CancellationToken cancellationToken)
    {
        Followed[] followed;
        lock (_gate) followed = [.. _followed.Values];
        foreach (var session in followed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var holder = _leases.TryReadHolder(session.SessionId);
            var released = holder is null || _runtime.GetHeldSessionLeases().Contains(session.SessionId, StringComparer.OrdinalIgnoreCase);
            session.Journal ??= MarkedJournal(session.SessionId);
            if (session.Journal is null && (lookUpJournals || released))
                session.Journal = await _store.GetJournalPathAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            var stamp = session.Journal is { } path ? Stamp(path) : null;
            if (stamp is not null && stamp != session.Stamp)
            {
                if (await _store.ProjectJournalAsync(session.Journal!, cancellationToken).ConfigureAwait(false))
                {
                    session.Stamp = stamp;
                    await _catalog.InvalidateAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
                    if (!released) Raise(new ExternalSessionChange(session.SessionId, ExternalSessionChangeKind.Updated, session.Holder.OwnerKind));
                }
            }

            if (!released) continue;
            lock (_gate) _followed.Remove(session.SessionId);
            // The frontend shows it perhaps: its lease is taken now that the other process let it go.
            _runtime.RetryShownSessionLeases();
            Raise(new ExternalSessionChange(session.SessionId, ExternalSessionChangeKind.Released, null));
        }
    }

    // The journal the marker of a session names, when it has one and the journal is there.
    private string? MarkedJournal(string sessionId)
    {
        try
        {
            return _markers.TryRead(sessionId) is { } marker && _markers.GetJournalPath(marker) is var path && File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static (long Length, DateTime LastWriteUtc)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Raise(ExternalSessionChange change)
    {
        try
        {
            Changed?.Invoke(this, change);
        }
        catch (Exception exception)
        {
            Log.Error(exception, $"A handler of the change of session {change.SessionId} failed.");
        }
    }

    // A session another process holds. Its journal and stamp are written by the follower's task only.
    private sealed class Followed(SessionLeaseHolder holder)
    {
        public string SessionId { get; } = holder.SessionId;

        public SessionLeaseHolder Holder { get; } = holder;

        public string? Journal { get; set; }

        public (long Length, DateTime LastWriteUtc)? Stamp { get; set; }

        public ExternalSessionHold ToHold() => new(SessionId, Holder.OwnerKind, Holder.ProcessId, Stamp?.Length);
    }
}
