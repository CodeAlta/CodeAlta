using System.Runtime.ExceptionServices;
using System.Text.Json;
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
    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> _tailHistory;
    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? _timelineHistory;
    private readonly Func<AgentHistoryRevision, long, long, long, CancellationToken, Task<AgentHistorySourceChunk>>? _historySource;
    private readonly SessionViewJournalStore? _journals;
    private readonly Func<string, long, int, Task<PromptImageReadResult>>? _promptImage;
    private readonly Func<string, long, CancellationToken, Task<AgentEvent?>>? _historyRecord;
    private readonly Func<string, CancellationToken, Task<string>> _notes = static (_, _) => Task.FromException<string>(new InvalidOperationException("Notes reader not configured."));
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
        _journals = journals;
        _projects = projects.LoadAsync;
        _sessions = token => store.ListSessionsAsync(filter: null, cancellationToken: token);
        _history = store.ReadHistoryPageAsync;
        _tailHistory = store.ReadHistoryTailPageAsync;
        _timelineHistory = store.ReadTimelinePageAsync;
        _historySource = store.ReadHistorySourceAsync;
        _historyRecord = store.ReadHistoryRecordAsync;
        var images = new PromptImageAttachmentStore(projects.Options);
        _promptImage = (sessionId, offset, index) => ReadPromptImageCoreAsync(store, images, sessionId, offset, index);
    }

    internal OwnedSessionWorkspace(ProjectCatalog projects, SessionViewJournalStore journals, SessionRuntimeService runtime)
        : this(projects, journals)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _notes = runtime.GetOwnedNotesMarkdownAsync;
    }

    // Literal callback seam, internal to qualified tests; production uses only the constructors above.
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
        _tailHistory = history; // Literal test seam; production always uses the reverse store reader.
    }

    internal OwnedSessionWorkspace(
        Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> projects,
        Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> sessions,
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> history,
        Func<string, CancellationToken, Task<string>> notes)
        : this(projects, sessions, history)
    {
        ArgumentNullException.ThrowIfNull(notes);
        _notes = notes;
    }

    // Literal extended-history test seam; production uses the shared cached store above.
    internal OwnedSessionWorkspace(
        Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> projects,
        Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> sessions,
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> timeline,
        Func<AgentHistoryRevision, long, long, long, CancellationToken, Task<AgentHistorySourceChunk>> source)
        : this(projects, sessions, timeline)
    {
        ArgumentNullException.ThrowIfNull(source);
        _timelineHistory = timeline;
        _historySource = source;
    }

    /// <summary>Reads complete notes through the same eight-actual-read admission and drain.</summary>
    /// <param name="sessionId">An explicit backend session identity.</param>
    /// <param name="cancellationToken">Cancels only this wait, never the retained actual read.</param>
    /// <returns>Exact latest Markdown, including empty notes.</returns>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="ObjectDisposedException">Read admission is closed.</exception>
    /// <exception cref="InvalidOperationException">Eight actual reads are admitted (synchronous admission refusal).</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">An admitted downstream read fails asynchronously.</exception>
    public Task<string> ReadNotesMarkdownAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => _notes(sessionId, CancellationToken.None), cancellationToken);
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

    /// <summary>Reads a bounded reverse journal page through the same admission, cached store and locks.</summary>
    /// <param name="sessionId">Selected durable session identity.</param>
    /// <param name="cursor">Exclusive boundary of the newer page, or null for the tail.</param>
    /// <param name="cancellationToken">Cancels this wait, not an admitted underlying read.</param>
    /// <returns>Canonical events in journal order and an optional older-page cursor.</returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Read admission is closed.</exception>
    /// <exception cref="InvalidOperationException">Eight actual reads are already admitted.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">Lookup, cursor validation or the history read fails.</exception>
    public Task<AgentSessionHistoryPage> ReadHistoryTailPageAsync(
        string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => _tailHistory(sessionId, cursor, CancellationToken.None), cancellationToken);
    }

    /// <summary>Reads an extended timeline page through the shared eight-read admission and actual-work drain.</summary>
    /// <exception cref="ArgumentException">Session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="InvalidOperationException">Admission is full or the route is not configured.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">The retained store read fails.</exception>
    public Task<AgentSessionHistoryPage> ReadTimelinePageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => (_timelineHistory ?? throw new InvalidOperationException("Timeline reader not configured."))
            (sessionId, cursor, CancellationToken.None), cancellationToken);
    }

    /// <summary>Reads a bounded literal source chunk through the same admitted/drained store owner.</summary>
    /// <exception cref="ArgumentNullException">Revision is null.</exception>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="InvalidOperationException">Admission is full or the route is not configured.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">The retained store read fails.</exception>
    public Task<AgentHistorySourceChunk> ReadHistorySourceAsync(AgentHistoryRevision revision, long start, long end, long offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.SessionId);
        return Admit(() => (_historySource ?? throw new InvalidOperationException("Source reader not configured."))
            (revision, start, end, offset, CancellationToken.None), cancellationToken);
    }

    /// <summary>Reads the one persisted event that starts at a journal offset through the same admitted/drained store owner.</summary>
    /// <param name="sessionId">Selected durable session identity.</param>
    /// <param name="offset">Starting byte offset of the record, as a history page reported it.</param>
    /// <param name="cancellationToken">Cancels this wait, not an admitted underlying read.</param>
    /// <returns>The event, or null when the record is blank.</returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="InvalidOperationException">Admission is full or the route is not configured.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="AgentSessionHistoryException">The session is missing, the offset is no record boundary, or the record is too large or not readable.</exception>
    public Task<AgentEvent?> ReadHistoryRecordAsync(string sessionId, long offset, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => (_historyRecord ?? throw new InvalidOperationException("Record reader not configured."))
            (sessionId, offset, CancellationToken.None), cancellationToken);
    }

    /// <summary>Reads one image of a persisted user message or tool result through the same admitted/drained store owner.</summary>
    /// <param name="sessionId">Selected durable session identity.</param>
    /// <param name="offset">Journal offset of the user message or of the tool output, as a history page reported it.</param>
    /// <param name="index">Position of the image among those of the message (<see cref="PromptImageHistory.ReadImages"/>).</param>
    /// <param name="cancellationToken">Cancels this wait, not an admitted underlying read.</param>
    /// <returns>
    /// The image, when the message records a file of the session's prompt-image folder that is a supported
    /// image of at most <see cref="PromptImageHistory.MaximumImageBytes"/>; otherwise the reason.
    /// </returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="InvalidOperationException">Admission is full or the route is not configured.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">The session lookup fails.</exception>
    public Task<PromptImageReadResult> ReadPromptImageAsync(string sessionId, long offset, int index, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => (_promptImage ?? throw new InvalidOperationException("Prompt image reader not configured."))
            (sessionId, offset, index), cancellationToken);
    }

    private static async Task<PromptImageReadResult> ReadPromptImageCoreAsync(FileSystemAgentSessionStore store,
        PromptImageAttachmentStore images, string sessionId, long offset, int index)
    {
        var session = await store.GetSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (session is null) return new(PromptImageReadStatus.MissingSession);
        AgentEvent? record;
        try { record = await store.ReadHistoryRecordAsync(sessionId, offset, CancellationToken.None).ConfigureAwait(false); }
        catch (AgentSessionHistoryException error)
        {
            return new(error.Code switch
            {
                "missing_session" => PromptImageReadStatus.MissingSession,
                "invalid_cursor" => PromptImageReadStatus.MissingRecord,
                _ => PromptImageReadStatus.ReadFailed,
            });
        }

        if (record is not AgentContentCompletedEvent { Kind: AgentContentKind.User or AgentContentKind.ToolOutput } message) return new(PromptImageReadStatus.MissingRecord);
        var recorded = PromptImageHistory.ReadImages(message.Details);
        if (index < 0 || index >= recorded.Count) return new(PromptImageReadStatus.MissingImage);
        string directory;
        // The folder images are saved to: the path comes from the record, the authority from the session.
        try { directory = images.GetAttachmentDirectory(new SessionViewDescriptor { SessionId = sessionId, CreatedAt = session.CreatedAt }); }
        catch (ArgumentException) { return new(PromptImageReadStatus.OutsideStore); }
        return await PromptImageHistory.ReadFileAsync(directory, recorded[index].Path, PromptImageHistory.MaximumImageBytes, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<OwnedWorkspaceSnapshot> ReadSnapshotCoreAsync()
    {
        var projectRead = _projects(CancellationToken.None);
        var projects = await projectRead.ConfigureAwait(false);
        var sessions = new List<AgentSessionMetadata>();
        // Fully consume the cached iterator, including its final awaited cache-completion write.
        await foreach (var session in _sessions(CancellationToken.None).ConfigureAwait(false)) sessions.Add(session);
        var headers = _journals is null ? new Dictionary<string, SessionViewJournalHeader>(StringComparer.Ordinal)
            : await ReadSessionHeadersAsync(sessions, _journals, CancellationToken.None).ConfigureAwait(false);
        return new(projects, sessions) { SessionHeaders = headers };
    }

    /// <summary>
    /// Reads the journal header of each listed session that the bounded desktop projection can show: the
    /// evidence of its scope. A header that names the session and its working directory is the session's,
    /// and the instant it records is the session's creation time: <paramref name="sessions"/> is updated
    /// where the listed time is another one.
    /// </summary>
    /// <remarks>
    /// A session's list entry comes from the provider's own record. Sessions created before the two shared
    /// one instant have a record stamped a few milliseconds after the header, until they are resumed.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller canceled.</exception>
    public static async Task<Dictionary<string, SessionViewJournalHeader>> ReadSessionHeadersAsync(
        List<AgentSessionMetadata> sessions, SessionViewJournalStore journals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(journals);
        var headers = new Dictionary<string, SessionViewJournalHeader>(StringComparer.Ordinal);
        // Only the rows eligible for the bounded desktop projection need header scope evidence.
        var eligible = sessions.Select(static (session, index) => (session, index))
            .OrderByDescending(static value => value.session.UpdatedAt)
            .ThenByDescending(static value => value.session.SessionId, StringComparer.Ordinal).Take(500).ToArray();
        foreach (var (session, index) in eligible)
        {
            if (session.SessionId.Length is < 1 or > 256 || session.CreatedAt == default) continue;
            SessionViewJournalHeader? header;
            try { header = await journals.ReadHeaderAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Header enrichment is optional authority, not a reason to omit a cached session row.
                continue;
            }
            if (header is null || header.SessionId != session.SessionId || header.WorkingDirectory != session.WorkspacePath
                || header.CreatedAt == default || !headers.TryAdd(session.SessionId, header)) continue;
            if (header.CreatedAt != session.CreatedAt) sessions[index] = session with { CreatedAt = header.CreatedAt };
        }
        return headers;
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
public sealed record OwnedWorkspaceSnapshot(IReadOnlyList<ProjectDescriptor> Projects, IReadOnlyList<AgentSessionMetadata> Sessions)
{
    /// <summary>Exact persisted journal headers for at most the first 500 displayed sessions; absent headers grant no scope authority.</summary>
    public IReadOnlyDictionary<string, SessionViewJournalHeader> SessionHeaders { get; init; } = new Dictionary<string, SessionViewJournalHeader>();
}
