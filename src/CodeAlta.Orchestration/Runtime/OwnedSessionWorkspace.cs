using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Host-owned, bounded admission and drainage of direct cached workspace reads.</summary>
/// <remarks>Caller cancellation stops only the wait. Snapshot limits do not bound the underlying catalog scan.</remarks>
public sealed class OwnedSessionWorkspace : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> _projects;
    private readonly Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> _sessions;
    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> _history;
    private readonly HashSet<ReadOperation> _active = [];
    private bool _closed;
    private Task? _disposal;

    internal OwnedSessionWorkspace(ProjectCatalog projects, SessionViewJournalStore journals)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(journals);
        // This exact construction always supplies the journal's projection cache and shared file locks.
        // Never replace it with AgentSessionCatalog or a new/uncached filesystem store.
        var store = journals.CreateSessionStore();
        _projects = projects.LoadAsync;
        _sessions = token => store.ListSessionsAsync(filter: null, cancellationToken: token);
        _history = store.ReadHistoryPageAsync;
    }

    // Literal callback seam, internal to qualified tests; production uses only the constructor above.
    internal OwnedSessionWorkspace(
        Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> projects,
        Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> sessions,
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> history)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(history);
        _projects = projects;
        _sessions = sessions;
        _history = history;
    }

    /// <summary>Reads a complete direct-store workspace snapshot, independent of a cancelled caller wait.</summary>
    /// <exception cref="ObjectDisposedException">Read admission is closed.</exception>
    /// <exception cref="InvalidOperationException">Eight actual reads are already admitted.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">A catalog or direct-store read fails.</exception>
    public Task<OwnedWorkspaceSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        => Admit(ReadSnapshotCoreAsync, cancellationToken);

    /// <summary>Reads one bounded persisted history page using the same cached store and journal locks.</summary>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Read admission is closed.</exception>
    /// <exception cref="InvalidOperationException">Eight actual reads are already admitted.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">Lookup, cursor validation or the history read fails.</exception>
    public Task<AgentSessionHistoryPage> ReadHistoryPageAsync(
        string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => _history(sessionId, cursor, CancellationToken.None), cancellationToken);
    }

    private async Task<OwnedWorkspaceSnapshot> ReadSnapshotCoreAsync()
    {
        var projectRead = _projects(CancellationToken.None);
        var projects = await projectRead.ConfigureAwait(false);
        var sessions = new List<AgentSessionMetadata>();
        // Fully consume the cached iterator, including its final awaited cache-completion write.
        await foreach (var session in _sessions(CancellationToken.None).ConfigureAwait(false)) sessions.Add(session);
        return new(projects, sessions);
    }

    private Task<T> Admit<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        ReadOperation operation;
        Task<T> work;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_active.Count == 8) throw new InvalidOperationException("Owned workspace read capacity reached.");
            operation = new ReadOperation();
            _active.Add(operation);
            work = RunAsync(operation, read);
            operation.Work = work;
            operation.Observer = ObserveAsync(operation);
        }
        operation.Launch.TrySetResult();
        return work.WaitAsync(cancellationToken);
    }

    private static async Task<T> RunAsync<T>(ReadOperation operation, Func<Task<T>> read)
    {
        await operation.Launch.Task.ConfigureAwait(false);
        var actual = read();
        return await actual.ConfigureAwait(false);
    }

    private async Task ObserveAsync(ReadOperation operation)
    {
        try { await operation.Work.ConfigureAwait(false); }
        catch (Exception ex) { operation.Failure = ex; }
        finally { lock (_gate) _active.Remove(operation); }
    }

    /// <summary>Closes admission and joins all actual reads and their fault observers; does not dispose dependencies.</summary>
    /// <remarks>Noncooperative reads keep this task pending. No timeout is treated as termination.</remarks>
    /// <exception cref="Exception">One read failed; its original exception is propagated.</exception>
    /// <exception cref="AggregateException">Multiple reads failed.</exception>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _closed = true;
            return new(_disposal = DrainAsync(_active.ToArray()));
        }
    }

    private static async Task DrainAsync(ReadOperation[] operations)
    {
        foreach (var operation in operations) await operation.Observer.ConfigureAwait(false);
        // Completed, observed request errors do not poison later host shutdown or accumulate forever.
        var failures = operations.Where(operation => operation.Failure is not null).Select(operation => operation.Failure!).ToArray();
        if (failures.Length == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Length > 1) throw new AggregateException(failures);
    }

    private sealed class ReadOperation
    {
        internal readonly TaskCompletionSource Launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work = Task.CompletedTask;
        internal Task Observer = Task.CompletedTask;
        internal Exception? Failure;
    }
}

/// <summary>A complete persisted workspace read; frontends independently bound their wire projection.</summary>
/// <param name="Projects">Persisted projects.</param>
/// <param name="Sessions">Direct cached-store session metadata.</param>
public sealed record OwnedWorkspaceSnapshot(IReadOnlyList<ProjectDescriptor> Projects, IReadOnlyList<AgentSessionMetadata> Sessions);
