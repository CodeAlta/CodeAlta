using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Host-owned, bounded admission and drainage of direct cached workspace reads.</summary>
/// <remarks>Caller cancellation stops only the wait. Snapshot limits do not bound the underlying catalog scan. When every
/// slot is held, a caller waits a bounded time for one, then is refused with <see cref="OwnedWorkspaceBusyException"/>;
/// snapshots have slots of their own, so that they never hold back the other reads.</remarks>
public sealed class OwnedSessionWorkspace : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> _projects;
    private readonly Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> _sessions;
    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> _history;
    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> _tailHistory;
    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? _timelineHistory;
    private readonly Func<AgentHistoryRevision, long, long, long, CancellationToken, Task<AgentHistorySourceChunk>>? _historySource;
    private readonly Func<string, long, int, Task<PromptImageReadResult>>? _promptImage;
    private readonly Func<string, long, CancellationToken, Task<AgentEvent?>>? _historyRecord;
    private readonly Func<string, CancellationToken, Task<string>> _notes = static (_, _) => Task.FromException<string>(new InvalidOperationException("Notes reader not configured."));
    private readonly HashSet<ReadOperation> _active = [];
    // Eight actual reads of history, notes, images, usage and prompts; the snapshots, which list every session, have
    // their own slots so that they never hold the others back.
    private readonly SemaphoreSlim _readSlots = new(ReadSlots, ReadSlots);
    private readonly SemaphoreSlim _snapshotSlots = new(SnapshotSlots, SnapshotSlots);
    private bool _closed;
    private Task? _disposal;

    /// <summary>The actual reads, other than snapshots, admitted at once.</summary>
    public const int ReadSlots = 8;

    /// <summary>The actual snapshot reads admitted at once, beside the other reads.</summary>
    public const int SnapshotSlots = 4;

    /// <summary>How long a read waits for a slot before it is refused as busy.</summary>
    public static readonly TimeSpan ReadAdmissionWait = TimeSpan.FromSeconds(2);

    /// <summary>How long a snapshot waits for a slot before it is refused as busy.</summary>
    public static readonly TimeSpan SnapshotAdmissionWait = TimeSpan.FromSeconds(5);

    private const string AttachmentsSuffix = ".attachments";
    // Distinguishes the stamps of this workspace from those of another host.
    private readonly string _instance = Guid.NewGuid().ToString("N");

    // The journal file of a session, for the stamps of timeline reads; production sets the store's, tests their own.
    internal Func<string, CancellationToken, Task<string?>>? JournalPaths { get; init; }

    // Test seam: the waits for a slot, shortened by tests.
    internal TimeSpan ReadWait { get; init; } = ReadAdmissionWait;
    internal TimeSpan SnapshotWait { get; init; } = SnapshotAdmissionWait;

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
        _tailHistory = store.ReadHistoryTailPageAsync;
        _timelineHistory = store.ReadTimelinePageAsync;
        _historySource = store.ReadHistorySourceAsync;
        _historyRecord = store.ReadHistoryRecordAsync;
        // The journal the store reads, found as the store finds it: its attachment folder is named after it.
        JournalPaths = async (sessionId, token) => await store.GetAttachmentDirectoryAsync(sessionId, token).ConfigureAwait(false) is { } folder
            && folder.EndsWith(AttachmentsSuffix, StringComparison.Ordinal) ? folder[..^AttachmentsSuffix.Length] + ".jsonl" : null;
        UsageUpdates = store.ReadUsageUpdatesAsync;
        SystemPrompts = store.ReadSystemPromptAsync;
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

    // Literal usage test seam; production always sets the shared cached store's reader in its constructor.
    internal Func<string, CancellationToken, Task<IReadOnlyList<AgentSessionUsageUpdate>>>? UsageUpdates { get; init; }

    /// <summary>Reads complete notes through the same eight-actual-read admission (bounded wait) and drain.</summary>
    /// <param name="sessionId">An explicit backend session identity.</param>
    /// <param name="cancellationToken">Cancels only this wait, never the retained actual read.</param>
    /// <returns>Exact latest Markdown, including empty notes.</returns>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="ObjectDisposedException">Read admission is closed.</exception>
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/> (asynchronous; nothing was read).</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">An admitted downstream read fails asynchronously.</exception>
    public Task<string> ReadNotesMarkdownAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => _notes(sessionId, CancellationToken.None), cancellationToken);
    }

    /// <summary>Reads a complete direct-store workspace snapshot, independent of a cancelled caller wait.</summary>
    /// <exception cref="ObjectDisposedException">Read admission is closed.</exception>
    /// <exception cref="OwnedWorkspaceBusyException">No snapshot slot was free within <see cref="SnapshotAdmissionWait"/>.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">A catalog or direct-store read fails.</exception>
    public Task<OwnedWorkspaceSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        => Admit(_snapshotSlots, SnapshotWait, ReadSnapshotCoreAsync, cancellationToken, CancellationToken.None);

    /// <summary>Reads one bounded persisted history page using the same cached store and journal locks.</summary>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Read admission is closed.</exception>
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/>.</exception>
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
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/>.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">Lookup, cursor validation or the history read fails.</exception>
    public Task<AgentSessionHistoryPage> ReadHistoryTailPageAsync(
        string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => _tailHistory(sessionId, cursor, CancellationToken.None), cancellationToken);
    }

    /// <summary>Reads an extended timeline page through the shared eight-read admission (bounded wait) and actual-work drain.</summary>
    /// <exception cref="ArgumentException">Session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/>.</exception>
    /// <exception cref="InvalidOperationException">The route is not configured.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">The retained store read fails.</exception>
    public Task<AgentSessionHistoryPage> ReadTimelinePageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => (_timelineHistory ?? throw new InvalidOperationException("Timeline reader not configured."))
            (sessionId, cursor, CancellationToken.None), cancellationToken);
    }

    /// <summary>
    /// Reads the newest extended timeline page, unless the journal still has the stamp a previous read of it returned:
    /// then only that is said, after one status read of the journal, without reading or locking it.
    /// </summary>
    /// <remarks>
    /// The stamp names this workspace, the session and the journal's length, last write and creation times. A read
    /// returns one only when the journal had it before and after the page was read, with the page's revision: an append,
    /// a replacement or a rewrite of the journal changes it, and any doubt leaves the read without a stamp, so that the
    /// next read is a whole one. Like the revisions of history pages, a rewrite that keeps all three is not seen.
    /// </remarks>
    /// <param name="sessionId">Selected durable session identity.</param>
    /// <param name="knownStamp">The stamp the previous read returned, or null to read the page.</param>
    /// <param name="cancellationToken">Cancels this wait, not an admitted underlying read.</param>
    /// <returns>The page and its stamp (null when unsure), or no page when the journal still has <paramref name="knownStamp"/>.</returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/>.</exception>
    /// <exception cref="InvalidOperationException">The route is not configured.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="Exception">The retained store read fails.</exception>
    public Task<OwnedTimelineTail> ReadTimelineTailAsync(string sessionId, string? knownStamp, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => ReadTimelineTailCoreAsync(sessionId, knownStamp), cancellationToken);
    }

    private async Task<OwnedTimelineTail> ReadTimelineTailCoreAsync(string sessionId, string? known)
    {
        var timeline = _timelineHistory ?? throw new InvalidOperationException("Timeline reader not configured.");
        string? path = null;
        // Finding the journal can fail as the read would: the read then reports it.
        try { path = JournalPaths is null ? null : await JournalPaths(sessionId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        var before = path is null ? null : JournalStamp(sessionId, path);
        if (known is not null && before is not null && string.Equals(before.Value.Text, known, StringComparison.Ordinal))
            return new(null, known);
        var page = await timeline(sessionId, null, CancellationToken.None).ConfigureAwait(false);
        var stable = before is { } stamp && page.Revision is { } revision && revision.Length == stamp.Length
            && revision.LastWriteUtcTicks == stamp.LastWriteUtcTicks && JournalStamp(sessionId, path!) == stamp;
        return new(page, stable ? before!.Value.Text : null);
    }

    // One status read of the journal; null when it is not there or cannot be read.
    private (string Text, long Length, long LastWriteUtcTicks)? JournalStamp(string sessionId, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var length = info.Length;
            var written = info.LastWriteTimeUtc.Ticks;
            return (string.Create(CultureInfo.InvariantCulture, $"j1:{_instance}:{sessionId.Length}:{sessionId}:{length}:{written}:{info.CreationTimeUtc.Ticks}"), length, written);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads a bounded literal source chunk through the same admitted/drained store owner.</summary>
    /// <exception cref="ArgumentNullException">Revision is null.</exception>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/>.</exception>
    /// <exception cref="InvalidOperationException">The route is not configured.</exception>
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
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/>.</exception>
    /// <exception cref="InvalidOperationException">The route is not configured.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    /// <exception cref="AgentSessionHistoryException">The session is missing, the offset is no record boundary, or the record is too large or not readable.</exception>
    public Task<AgentEvent?> ReadHistoryRecordAsync(string sessionId, long offset, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => (_historyRecord ?? throw new InvalidOperationException("Record reader not configured."))
            (sessionId, offset, CancellationToken.None), cancellationToken);
    }

    /// <summary>
    /// Reads the usage of every request to the model that the journal of a session records, through the same
    /// admitted/drained store owner.
    /// </summary>
    /// <param name="sessionId">Selected durable session identity.</param>
    /// <param name="cancellationToken">
    /// Cancels this wait and the read itself: unlike a bounded page, it reads the whole journal, which nothing has to
    /// finish once its caller is gone.
    /// </param>
    /// <returns>The usage updates in journal order, up to the length the journal has when the read starts.</returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/>.</exception>
    /// <exception cref="InvalidOperationException">The route is not configured.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels.</exception>
    /// <exception cref="AgentSessionHistoryException">The session is missing, or its journal or one of its usage records cannot be read.</exception>
    public Task<IReadOnlyList<AgentSessionUsageUpdate>> ReadUsageUpdatesAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => (UsageUpdates ?? throw new InvalidOperationException("Usage reader not configured."))
            (sessionId, cancellationToken), cancellationToken, cancellationToken);
    }

    // Literal prompt test seam; production always sets the shared cached store's reader in its constructor.
    internal Func<string, long, CancellationToken, Task<AgentSystemPromptRecord>>? SystemPrompts { get; init; }

    /// <summary>
    /// Reads the system prompt record that starts at a journal offset, with the one it replaced, through the same
    /// admitted/drained store owner.
    /// </summary>
    /// <param name="sessionId">Selected durable session identity.</param>
    /// <param name="offset">Starting byte offset of the record, as a history page reported it.</param>
    /// <param name="cancellationToken">
    /// Cancels this wait and the read itself: finding the record before reads the journal up to the offset, which
    /// nothing has to finish once its caller is gone.
    /// </param>
    /// <returns>The record and, unless it is the first prompt of the session, the prompt record before it.</returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/>.</exception>
    /// <exception cref="InvalidOperationException">The route is not configured.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels.</exception>
    /// <exception cref="AgentSessionHistoryException">The session is missing, the offset starts no system prompt record, or the record is too large or not readable.</exception>
    public Task<AgentSystemPromptRecord> ReadSystemPromptAsync(string sessionId, long offset, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Admit(() => (SystemPrompts ?? throw new InvalidOperationException("System prompt reader not configured."))
            (sessionId, offset, cancellationToken), cancellationToken, cancellationToken);
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
    /// <exception cref="OwnedWorkspaceBusyException">No read slot was free within <see cref="ReadAdmissionWait"/>.</exception>
    /// <exception cref="InvalidOperationException">The route is not configured.</exception>
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
        return new(projects, sessions) { SessionHeaders = ReadSessionHeaders(sessions) };
    }

    /// <summary>
    /// Takes the journal header of each listed session from the session list, which records it: the evidence of the
    /// session's scope. A header that names the session and its working directory is the session's, and the instant it
    /// records is the session's creation time: <paramref name="sessions"/> is updated where the listed time is another one.
    /// </summary>
    /// <remarks>
    /// No journal is opened: the session list and the workspace snapshot read only the projection of the sessions in
    /// the application database. A session's list entry comes from the provider's own record. Sessions created before
    /// the two shared one instant have a record stamped a few milliseconds after the header, until they are resumed.
    /// </remarks>
    /// <param name="sessions">The sessions as listed.</param>
    /// <returns>The header of each session the list records one for, by session identifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    public static Dictionary<string, SessionViewJournalHeader> ReadSessionHeaders(List<AgentSessionMetadata> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var headers = new Dictionary<string, SessionViewJournalHeader>(StringComparer.Ordinal);
        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            if (session.SessionId.Length is < 1 or > 256 || SessionViewJournalHeader.FromJson(session.JournalHeaderJson) is not { } header) continue;
            if (header.SessionId != session.SessionId || header.WorkingDirectory != session.WorkspacePath
                || header.CreatedAt == default || !headers.TryAdd(session.SessionId, header)) continue;
            if (header.CreatedAt != session.CreatedAt) sessions[index] = session with { CreatedAt = header.CreatedAt };
        }
        return headers;
    }

    private Task<T> Admit<T>(Func<Task<T>> read, CancellationToken cancellationToken)
        => Admit(_readSlots, ReadWait, read, cancellationToken, CancellationToken.None);

    private Task<T> Admit<T>(Func<Task<T>> read, CancellationToken cancellationToken, CancellationToken readCancellation)
        => Admit(_readSlots, ReadWait, read, cancellationToken, readCancellation);

    // readCancellation is the token the actual read was given, when it was given one: the end it asks for is no failure.
    // A free slot admits the read at once; otherwise the caller waits for one, at most `wait`, and is refused as busy.
    // A slot is given back only when the actual read and its observer end, never when a caller stops waiting.
    private Task<T> Admit<T>(SemaphoreSlim slots, TimeSpan wait, Func<Task<T>> read, CancellationToken cancellationToken, CancellationToken readCancellation)
    {
        ReadOperation? operation = null;
        Task<T>? work = null;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_closed, this);
            if (slots.Wait(0)) (operation, work) = Launch(slots, read, readCancellation);
        }
        if (operation is null || work is null) return WaitForSlotAsync(slots, wait, read, cancellationToken, readCancellation);
        operation.Launch.TrySetResult();
        return work.WaitAsync(cancellationToken);
    }

    private async Task<T> WaitForSlotAsync<T>(SemaphoreSlim slots, TimeSpan wait, Func<Task<T>> read,
        CancellationToken cancellationToken, CancellationToken readCancellation)
    {
        // A canceled wait holds no slot; a slot given at the same instant is given back below.
        if (!await slots.WaitAsync(wait, cancellationToken).ConfigureAwait(false)) throw new OwnedWorkspaceBusyException();
        ReadOperation operation;
        Task<T> work;
        lock (_gate)
        {
            if (_closed || cancellationToken.IsCancellationRequested)
            {
                slots.Release();
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_closed, this);
            }
            (operation, work) = Launch(slots, read, readCancellation);
        }
        operation.Launch.TrySetResult();
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Called under the gate with a slot taken: the operation owns it until its observer ends.
    private (ReadOperation Operation, Task<T> Work) Launch<T>(SemaphoreSlim slots, Func<Task<T>> read, CancellationToken readCancellation)
    {
        var operation = new ReadOperation { ReadCancellation = readCancellation, Slots = slots };
        _active.Add(operation);
        var work = RunAsync(operation, read);
        operation.Work = work;
        operation.Observer = ObserveAsync(operation);
        return (operation, work);
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
        catch (OperationCanceledException) when (operation.ReadCancellation.IsCancellationRequested) { }
        catch (Exception ex) { operation.Failure = ex; }
        finally
        {
            lock (_gate) _active.Remove(operation);
            operation.Slots!.Release();
        }
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
        internal CancellationToken ReadCancellation;
        internal SemaphoreSlim? Slots;
    }
}

/// <summary>
/// A workspace read that waited its bounded time for one of the admitted slots, all held by other actual reads. It was
/// not started: asking again later can succeed.
/// </summary>
public sealed class OwnedWorkspaceBusyException : InvalidOperationException
{
    /// <summary>Initializes the refusal.</summary>
    public OwnedWorkspaceBusyException() : base("Owned workspace read capacity reached.") { }
}

/// <summary>The newest timeline page of a session, or the news that its journal did not change.</summary>
/// <param name="Page">The page; null when the journal still has the stamp the caller knew.</param>
/// <param name="Stamp">The stamp of the journal the page was read from, null when unsure; the known one when unchanged.</param>
public sealed record OwnedTimelineTail(AgentSessionHistoryPage? Page, string? Stamp)
{
    /// <summary>Gets whether the journal still has the stamp the caller knew, and nothing was read.</summary>
    public bool Unchanged => Page is null;
}

/// <summary>A complete persisted workspace read; frontends independently bound their wire projection.</summary>
/// <param name="Projects">Persisted projects.</param>
/// <param name="Sessions">Direct cached-store session metadata.</param>
public sealed record OwnedWorkspaceSnapshot(IReadOnlyList<ProjectDescriptor> Projects, IReadOnlyList<AgentSessionMetadata> Sessions)
{
    /// <summary>The journal headers the session list records for the listed sessions; absent headers grant no scope authority.</summary>
    public IReadOnlyDictionary<string, SessionViewJournalHeader> SessionHeaders { get; init; } = new Dictionary<string, SessionViewJournalHeader>();
}
