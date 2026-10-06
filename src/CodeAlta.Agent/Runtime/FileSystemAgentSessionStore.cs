using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Persists local raw-API session journals on the filesystem.
/// </summary>
public sealed class FileSystemAgentSessionStore : IAgentSessionJournalStore, Images.IAgentSessionAttachmentStore
{
    private const string SessionSummaryEventType = "local.sessionSummary";
    private const string SessionStateEventType = "local.sessionState";
    private const string CodeAltaSessionHeaderEventType = "codealta.sessionHeader";
    private const string CodeAltaSessionStateEventType = "codealta.sessionState";
    private const int MetadataProbeHeadByteCount = 64 * 1024;
    private const int MetadataProbeTailByteCount = 256 * 1024;
    private const int DefaultMaxConcurrentMetadataProjections = 8;

    private static readonly TimeSpan ReadRetryTime = TimeSpan.FromMilliseconds(250);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AgentRuntimePathLayout _layout;
    private readonly AgentSessionJournalFile _journalFile;
    private readonly IAgentSessionProjectionCache? _projectionCache;
    private readonly int _maxConcurrentMetadataProjections;
    private readonly ConcurrentDictionary<string, CachedSessionProjection> _metadataProjectionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemAgentSessionStore"/> class.
    /// </summary>
    /// <param name="layout">Filesystem layout.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="layout"/> is <see langword="null" />.</exception>
    public FileSystemAgentSessionStore(AgentRuntimePathLayout layout)
        : this(layout, new AgentSessionJournalFile())
    {
    }

    internal FileSystemAgentSessionStore(
        AgentRuntimePathLayout layout,
        AgentSessionJournalFile journalFile,
        IAgentSessionProjectionCache? projectionCache = null)
        : this(layout, journalFile, DefaultMaxConcurrentMetadataProjections, projectionCache)
    {
    }

    internal FileSystemAgentSessionStore(
        AgentRuntimePathLayout layout,
        AgentSessionJournalFile journalFile,
        int maxConcurrentMetadataProjections,
        IAgentSessionProjectionCache? projectionCache = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(journalFile);
        if (maxConcurrentMetadataProjections < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentMetadataProjections), "Concurrency must be greater than zero.");
        }

        _layout = layout;
        _journalFile = journalFile;
        _projectionCache = projectionCache;
        _maxConcurrentMetadataProjections = maxConcurrentMetadataProjections;
    }

    /// <inheritdoc />
    public async Task UpsertSessionAsync(
        AgentSessionSummary session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sessionFile = await GetOrCreateSessionFilePathAsync(
            session.SessionId,
            session.CreatedAt,
            cancellationToken).ConfigureAwait(false);
        var snapshotEvent = new AgentRawEvent(
            session.ProviderId,
            session.SessionId,
            session.UpdatedAt,
            SessionSummaryEventType,
            JsonSerializer.SerializeToElement(session, AgentJsonSerializerContext.Default.AgentSessionSummary),
            null);

        await AppendLinesAsync(sessionFile, [snapshotEvent.ToJson()], cancellationToken).ConfigureAwait(false);
        await UpsertCacheFromFileAsync(sessionFile, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentSessionSummary?> GetSessionAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: false, cancellationToken).ConfigureAwait(false);
        if (projection is null || projection.Summary is null)
        {
            return null;
        }

        return MatchesScope(projection.Summary, protocolFamily, providerKey)
            ? projection.Summary
            : null;
    }

    /// <inheritdoc />
    public async Task<AgentSessionMetadata?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: false, cancellationToken).ConfigureAwait(false);
        return projection?.Summary is null
            ? null
            : ToMetadata(projection.Summary, projection.State, projection.ViewState);
    }

    /// <inheritdoc />
    public async Task<AgentSessionSummary?> GetSessionSummaryAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: false, cancellationToken).ConfigureAwait(false);
        return projection?.Summary;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentSessionSummary> ListSessionSummariesAsync(
        string protocolFamily,
        string providerKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var session in ListSessionSummariesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (MatchesScope(session, protocolFamily, providerKey))
            {
                yield return session;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var projection in ListSessionProjectionsAsync(cancellationToken).ConfigureAwait(false))
        {
            var metadata = ToMetadata(projection.Projection.Summary!, projection.Projection.State, projection.Projection.ViewState);
            if (MatchesFilter(metadata, filter))
            {
                yield return metadata;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentSessionSummary> ListSessionSummariesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var projection in ListSessionProjectionsAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return projection.Projection.Summary!;
        }
    }

    private async IAsyncEnumerable<ListedSessionProjection> ListSessionProjectionsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_projectionCache is not null)
        {
            await foreach (var projection in _projectionCache
                .ListSessionsAsync(CreateCacheProjectionContext(), cancellationToken)
                .ConfigureAwait(false))
            {
                _sessionFiles[projection.Summary.SessionId] = projection.JournalPath;
                yield return new ListedSessionProjection(
                    projection.JournalPath,
                    new SessionProjection(projection.Summary, projection.State, [], projection.ViewState));
            }

            yield break;
        }

        if (!Directory.Exists(_layout.SessionsRootPath))
        {
            yield break;
        }

        var sessionFiles = Directory
            .EnumerateFiles(_layout.SessionsRootPath, "*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(static sessionFile => File.GetLastWriteTimeUtc(sessionFile))
            .ThenByDescending(static sessionFile => sessionFile, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var offset = 0; offset < sessionFiles.Length; offset += _maxConcurrentMetadataProjections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(_maxConcurrentMetadataProjections, sessionFiles.Length - offset);
            var tasks = new Task<ListedSessionProjection?>[count];
            for (var index = 0; index < count; index++)
            {
                tasks[index] = ProjectSessionFileForListingAsync(sessionFiles[offset + index], cancellationToken);
            }

            foreach (var task in tasks)
            {
                var listedProjection = await task.ConfigureAwait(false);
                if (listedProjection?.Projection.Summary is null)
                {
                    continue;
                }

                _sessionFiles[listedProjection.Projection.Summary.SessionId] = listedProjection.SessionFile;
                yield return listedProjection;
            }
        }
    }

    private async Task<ListedSessionProjection?> ProjectSessionFileForListingAsync(
        string sessionFile,
        CancellationToken cancellationToken)
    {
        try
        {
            var projection = await ProjectSessionFileAsync(sessionFile, includeHistory: false, cancellationToken).ConfigureAwait(false);
            return projection.Summary is null
                ? null
                : new ListedSessionProjection(sessionFile, projection);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reconciles the configured projection cache with journals on disk when a cache is available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reconciliation statistics, or a no-op result when no projection cache is configured.</returns>
    /// <exception cref="AgentSessionCacheLockedException">Thrown when the cache database is locked.</exception>
    public Task<AgentSessionCacheReconciliationResult> ReconcileCacheAsync(CancellationToken cancellationToken = default)
        => _projectionCache is null
            ? Task.FromResult(new AgentSessionCacheReconciliationResult(false, 0, 0))
            : _projectionCache.ReconcileAsync(CreateCacheProjectionContext(), cancellationToken);

    private AgentSessionCacheProjectionContext CreateCacheProjectionContext()
        => new(_layout.SessionsRootPath, ProjectSessionFileForCacheAsync);

    private Task<AgentSessionCacheProjection?> ProjectSessionFileForCacheAsync(
        string sessionFile,
        CancellationToken cancellationToken)
        => ProjectSessionFileForCacheAsync(sessionFile, cancellationToken, ownsJournalGate: false);

    private async Task<AgentSessionCacheProjection?> ProjectSessionFileForCacheAsync(
        string sessionFile,
        CancellationToken cancellationToken,
        bool ownsJournalGate)
    {
        var before = GetFileStamp(sessionFile);
        if (before is null)
        {
            return null;
        }

        try
        {
            var projection = ownsJournalGate
                ? await ProjectSessionMetadataFileAsync(sessionFile, cancellationToken).ConfigureAwait(false)
                : await ProjectSessionFileAsync(sessionFile, includeHistory: false, cancellationToken).ConfigureAwait(false);
            if (projection.Summary is null)
            {
                return null;
            }

            var after = GetFileStamp(sessionFile);
            var stamp = after == before.Value ? after.Value : before.Value;
            _sessionFiles[projection.Summary.SessionId] = sessionFile;
            return new AgentSessionCacheProjection(
                Path.GetFullPath(sessionFile),
                new AgentSessionCacheFileStamp(stamp.LastWriteTimeUtc, stamp.Length),
                projection.Summary,
                projection.State,
                projection.ViewState);
        }
        catch (IOException) when (!ownsJournalGate)
        {
            return null;
        }
        catch (JsonException) when (!ownsJournalGate)
        {
            return null;
        }
    }

    private async Task UpsertCacheFromFileAsync(string sessionFile, CancellationToken cancellationToken, bool ownsJournalGate = false)
    {
        if (_projectionCache is null)
        {
            return;
        }

        var projection = await ProjectSessionFileForCacheAsync(sessionFile, cancellationToken, ownsJournalGate).ConfigureAwait(false);
        if (projection is not null)
        {
            await _projectionCache.UpsertSessionAsync(projection, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task RemoveCacheAsync(string sessionId, CancellationToken cancellationToken)
        => _projectionCache is null
            ? Task.CompletedTask
            : _projectionCache.RemoveSessionAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public async Task AppendEventsAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        IReadOnlyList<AgentEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolFamily);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return;
        }

        var sessionFile = await GetExistingSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await AppendLinesAsync(
                sessionFile,
                events.Select(static @event => @event.ToJson()).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        await UpsertCacheFromFileAsync(sessionFile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one bounded page of persisted events without materializing complete history.</summary>
    /// <param name="sessionId">Durable catalog identity, not necessarily an event's provider/runtime identity.</param>
    /// <param name="cursor">A previous page's continuation, or null to start at the beginning.</param>
    /// <param name="cancellationToken">Cancels lookup, gate admission and asynchronous reads.</param>
    /// <returns>At most 100 physical records' projected events and an optional continuation.</returns>
    /// <remarks>
    /// Journal input is bounded to 256 KiB plus five probe bytes per page; records to 128 KiB.
    /// Only UTF-8 LF/CRLF journals are supported here. Existing complete-history readers are unchanged.
    /// Lookup retains normal cache errors and directory discovery; those costs are not bounded by paging.
    /// Containment guards the history open, not earlier cache existence probes or copied-cache metadata.
    /// File length/time detects changes, not same-stamp rewrites or all external-writer races.
    /// The shared gate is in-process only; lexical containment does not isolate reparse points.
    /// </remarks>
    /// <exception cref="ArgumentException">The session identifier is empty.</exception>
    /// <exception cref="AgentSessionHistoryException">The cursor, journal format, revision or resolved path cannot be used.</exception>
    /// <exception cref="IOException">Journal access fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public async Task<AgentSessionHistoryPage> ReadHistoryPageAsync(
        string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
        => await ReadHistoryPageCoreAsync(sessionId, cursor, tail: false, cancellationToken).ConfigureAwait(false);

    /// <summary>Reads one bounded page backwards from the journal tail or an older-page cursor, returning events in journal order.</summary>
    /// <param name="sessionId">Durable catalog identity.</param>
    /// <param name="cursor">The exclusive older-page boundary, or null for the current journal tail.</param>
    /// <param name="cancellationToken">Cancels lookup, gate admission and asynchronous reads.</param>
    /// <returns>Up to 100 physical records from a 256 KiB window and an optional older-page cursor.</returns>
    /// <remarks>Unvisited older records are not validated until paged. Uses the same cache resolution, root, locks and revision checks as forward paging.</remarks>
    /// <exception cref="ArgumentException">The session identifier is empty.</exception>
    /// <exception cref="AgentSessionHistoryException">The cursor, journal format, revision or resolved path cannot be used.</exception>
    /// <exception cref="IOException">Journal access fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public Task<AgentSessionHistoryPage> ReadHistoryTailPageAsync(
        string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
        => ReadHistoryPageCoreAsync(sessionId, cursor, tail: true, cancellationToken);

    private async Task<AgentSessionHistoryPage> ReadHistoryPageCoreAsync(
        string sessionId, AgentSessionHistoryCursor? cursor, bool tail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        AgentJournalHistoryReader.ValidateCursor(sessionId, cursor);
        return await ReadHistoryStreamAsync(sessionId, (stream, stamp, token) => tail
            ? AgentJournalHistoryReader.ReadTailAsync(stream, sessionId, cursor, stamp, token)
            : AgentJournalHistoryReader.ReadAsync(stream, sessionId, cursor, stamp, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a reverse timeline page, admitting one record up to 8 MiB through bounded incremental reads.</summary>
    /// <param name="sessionId">Selected catalog identity.</param><param name="cursor">Exclusive older boundary.</param>
    /// <param name="cancellationToken">Cancels lookup, lock admission and reading.</param>
    /// <returns>A revisioned page with source ranges; legacy page limits are unchanged.</returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="IOException">Lookup, format, size, revision or reading fails.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public Task<AgentSessionHistoryPage> ReadTimelinePageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
    {
        AgentJournalHistoryReader.ValidateCursor(sessionId, cursor);
        return ReadHistoryStreamAsync(sessionId, (stream, stamp, token) =>
            AgentJournalHistoryReader.ReadTimelineAsync(stream, sessionId, cursor, stamp, token), cancellationToken);
    }

    /// <summary>Reads at most 16 KiB of UTF-8 source from a selected revision/range, without modifying the journal.</summary>
    /// <param name="revision">Expected journal identity and revision.</param><param name="start">Record start.</param>
    /// <param name="end">Exclusive source end.</param><param name="offset">Next UTF-8 byte boundary.</param>
    /// <param name="cancellationToken">Cancels lookup, lock admission and reading.</param>
    /// <returns>The literal chunk and next position; no unbounded accumulation.</returns>
    /// <exception cref="ArgumentNullException">Revision is null.</exception>
    /// <exception cref="ArgumentException">Session identity is blank.</exception>
    /// <exception cref="IOException">Lookup, range, revision, encoding or reading fails.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public Task<AgentHistorySourceChunk> ReadHistorySourceAsync(AgentHistoryRevision revision, long start, long end, long offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return ReadHistoryStreamAsync(revision.SessionId, (stream, stamp, token) =>
            AgentJournalHistoryReader.ReadSourceAsync(stream, revision, start, end, offset, stamp, token), cancellationToken);
    }

    /// <summary>Reads the one persisted event that starts at a journal offset, as a page entry reported it.</summary>
    /// <param name="sessionId">Selected catalog identity.</param>
    /// <param name="offset">Starting byte offset of the physical record.</param>
    /// <param name="cancellationToken">Cancels lookup, lock admission and reading.</param>
    /// <returns>The event, or null when the record is blank.</returns>
    /// <remarks>A record is read up to 8 MiB. No journal revision is checked: a journal only grows.</remarks>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="AgentSessionHistoryException">
    /// The session is missing, the offset is not a record boundary, or the record is too large or not readable.
    /// </exception>
    /// <exception cref="IOException">Journal access fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public Task<AgentEvent?> ReadHistoryRecordAsync(string sessionId, long offset, CancellationToken cancellationToken)
        => ReadHistoryStreamAsync(sessionId, (stream, _, token) =>
            AgentJournalHistoryReader.ReadRecordAsync(stream, offset, token), cancellationToken);

    private async Task<T> ReadHistoryStreamAsync<T>(string sessionId,
        Func<Stream, Func<AgentJournalHistoryReader.Stamp>, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var path = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (path is null) throw new AgentSessionHistoryException("missing_session");
        return await AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path, (containedPath, token) =>
            _journalFile.WithPathLockAsync(containedPath, async () =>
            {
                await using var stream = await OpenHistoryReadStreamAsync(containedPath, token).ConfigureAwait(false);
                AgentJournalHistoryReader.Stamp Stamp()
                {
                    var stamp = GetFileStamp(containedPath);
                    return stamp is null ? throw new AgentSessionHistoryException("history_changed")
                        : new AgentJournalHistoryReader.Stamp(stamp.Value.Length, stamp.Value.LastWriteTimeUtc.Ticks);
                }
                return await read(stream, Stamp, token).ConfigureAwait(false);
            }, token), cancellationToken).ConfigureAwait(false);
    }

    // Disable managed read-ahead so the stream requests respect the page-plus-probe byte budget.
    private static Task<FileStream> OpenHistoryReadStreamAsync(string path, CancellationToken cancellationToken)
        => AgentSessionJournalFile.RetryFileOperationAsync(
            () => Task.FromResult(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true)),
            ReadRetryTime, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: true, cancellationToken).ConfigureAwait(false);
        if (projection is null || projection.Summary is null || !MatchesScope(projection.Summary, protocolFamily, providerKey))
        {
            return [];
        }

        return projection.History;
    }

    /// <summary>
    /// Reads canonical session events by session identifier without applying a provider-scope filter.
    /// </summary>
    /// <param name="sessionId">Local session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The canonical event list when the session exists; otherwise an empty list.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> is empty.</exception>
    public async Task<IReadOnlyList<AgentEvent>> ReadEventsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: true, cancellationToken).ConfigureAwait(false);
        return projection?.History ?? [];
    }

    /// <inheritdoc />
    public async Task UpsertStateAsync(
        AgentSessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var sessionFile = await GetExistingSessionFilePathAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
        var ProviderId = await ResolveProviderIdAsync(state.SessionId, state.ProviderKey, cancellationToken).ConfigureAwait(false);
        var snapshotEvent = new AgentRawEvent(
            ProviderId,
            state.SessionId,
            state.UpdatedAt,
            SessionStateEventType,
            JsonSerializer.SerializeToElement(state, AgentJsonSerializerContext.Default.AgentSessionState),
            null);

        await AppendLinesAsync(sessionFile, [snapshotEvent.ToJson()], cancellationToken).ConfigureAwait(false);
        await UpsertCacheFromFileAsync(sessionFile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the last canonical notes event, retaining only one event rather than the whole history.</summary>
    /// <param name="sessionId">An existing identifier in this store's configured root, not a path.</param>
    /// <param name="cancellationToken">Cancels lookup, gate admission, or reading.</param>
    /// <returns>The latest event in journal order, or null if there are no notes events.</returns>
    /// <remarks>
    /// The first read of a journal scans it completely. Later reads through stores sharing the same journal owner
    /// reuse that scan: an unchanged length and last-write time is answered without opening the journal, and a
    /// longer journal is read only past the already scanned records. The journal lock is held to order the read
    /// after admitted writes, not while scanning. Same-stamp rewrites are not detected.
    /// </remarks>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    /// <exception cref="InvalidOperationException">The session does not exist.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="InvalidDataException">A notes event has no Markdown value.</exception>
    /// <exception cref="JsonException">A canonical journal record is malformed (existing trailing-record tolerance applies).</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<AgentNotesEvent?> ReadLatestNotesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = await GetExistingSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await ReadLatestNotesAtPathAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the latest notes with lexical containment of the notes-content open.</summary>
    /// <param name="sessionId">An existing session identifier, never a path grant.</param>
    /// <param name="cancellationToken">Cancels lookup, gate admission or reading.</param>
    /// <returns>The last canonical notes event in journal order, or null. Existing trailing-record tolerance applies.</returns>
    /// <remarks>Shares the legacy parser and journal lock. The complete scan is not bounded by a renderer limit.
    /// Containment does not cover prior cache metadata/existence probes, reparse points or external races.
    /// Scan reuse is as described for <see cref="ReadLatestNotesAsync"/>.</remarks>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="InvalidOperationException">No session exists.</exception>
    /// <exception cref="AgentSessionHistoryException">The resolved notes path is outside the sessions root.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
    /// <exception cref="InvalidDataException">A notes event has no Markdown.</exception>
    /// <exception cref="JsonException">A canonical record is malformed.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<AgentNotesEvent?> ReadLatestNotesContainedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = await GetExistingSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path, ReadLatestNotesAtPathAsync, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentNotesEvent?> ReadLatestNotesAtPathAsync(string path, CancellationToken cancellationToken)
    {
        var scan = await _journalFile.ReadLatestNotesAsync(
            path,
            static async (file, token) => await OpenReadStreamAsync(file, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return scan.Supported
            ? scan.Latest
            : await ReadLatestNotesWithDetectedEncodingAsync(path, cancellationToken).ConfigureAwait(false);
    }

    // BOM-detected legacy encodings keep the complete tolerant scan under the journal gate.
    private Task<AgentNotesEvent?> ReadLatestNotesWithDetectedEncodingAsync(string path, CancellationToken cancellationToken)
        => _journalFile.WithPathLockAsync(path, async () =>
        {
            AgentNotesEvent? latest = null;
            await foreach (var entry in ReadJournalEventsAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (entry is AgentNotesEvent notes)
                {
                    if (notes.Markdown is null)
                    {
                        throw new InvalidDataException("A notes journal event has no Markdown value.");
                    }

                    latest = notes;
                }
            }

            return latest;
        }, cancellationToken);

    /// <summary>Appends notes to an existing journal and delivers acknowledged feedback in journal-write order.</summary>
    /// <param name="notes">The canonical notes event.</param>
    /// <param name="committed">Feedback after the record and metadata cache are acknowledged. Must not reenter this journal's operations.</param>
    /// <param name="cancellationToken">Cancels lookup or admission; after write admission the record and feedback finish without caller cancellation.</param>
    /// <returns>A task completing after acknowledged persistence and feedback.</returns>
    /// <remarks>
    /// Every existing record is validated before the append, without tolerating a malformed final record. Records
    /// already scanned by a notes read or append through the same journal owner are not read again, and the others
    /// are read before write admission without the journal lock, as <see cref="ReadLatestNotesAsync"/> does. The lock
    /// is held to validate what was written in the meantime and to append, not while scanning a whole journal.
    /// Rewrites of already scanned records are detected only as far as notes reads detect them.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The session does not exist.</exception>
    /// <exception cref="IOException">The write failed; no rollback of partial filesystem I/O is promised.</exception>
    /// <exception cref="JsonException">Existing canonical content, including the final record, is malformed. No bytes are changed.</exception>
    /// <exception cref="InvalidDataException">Existing notes lack Markdown or the journal has a non-UTF-8 BOM. No bytes are changed.</exception>
    /// <exception cref="OperationCanceledException">Canceled before write admission.</exception>
    /// <exception cref="AgentNotesCommittedException">The record was committed but subsequent cache or observer feedback failed.</exception>
    public async Task AppendNotesAsync(AgentNotesEvent notes, Func<Task> committed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(notes.Markdown);
        ArgumentNullException.ThrowIfNull(committed);
        cancellationToken.ThrowIfCancellationRequested();
        var path = await GetExistingSessionFilePathAsync(notes.SessionId, cancellationToken).ConfigureAwait(false);
        // Like opening the append handle, the scan waits for a journal another process holds until canceled.
        await _journalFile.AppendNotesLineAsync(path, notes.ToJson(), Utf8WithoutBom,
            static async (file, token) => await OpenReadStreamAsync(file, maxRetryTime: null, token).ConfigureAwait(false), async () =>
        {
            try
            {
                await UpsertCacheFromFileAsync(path, CancellationToken.None, ownsJournalGate: true).ConfigureAwait(false);
                await committed().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new AgentNotesCommittedException(notes.SessionId, exception);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentSessionState?> GetStateAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: false, cancellationToken).ConfigureAwait(false);
        if (projection is null || projection.Summary is null || projection.State is null)
        {
            return null;
        }

        return MatchesScope(projection.Summary, protocolFamily, providerKey)
            ? projection.State
            : null;
    }

    /// <inheritdoc />
    public async Task<AgentSessionState?> GetStateAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: false, cancellationToken).ConfigureAwait(false);
        return projection?.State;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSessionAsync(
        string protocolFamily,
        string providerKey,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var sessionFile = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFile is null || !File.Exists(sessionFile))
        {
            await RemoveCacheAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var projection = await TryProjectSessionAsync(sessionId, includeHistory: false, cancellationToken).ConfigureAwait(false);
        if (projection?.Summary is not null && !MatchesScope(projection.Summary, protocolFamily, providerKey))
        {
            return false;
        }

        var deleted = false;
        await _journalFile.WithPathLockAsync(
                sessionFile,
                () =>
                {
                    if (!File.Exists(sessionFile))
                    {
                        return Task.CompletedTask;
                    }

                    File.Delete(sessionFile);
                    _sessionFiles.TryRemove(sessionId, out _);
                    InvalidateMetadataProjectionCache(sessionFile);
                    _journalFile.ForgetLatestNotes(sessionFile);
                    DeleteEmptySessionDirectories(Path.GetDirectoryName(sessionFile));
                    deleted = true;
                    return Task.CompletedTask;
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (deleted)
        {
            await RemoveCacheAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var sessionFile = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFile is null || !File.Exists(sessionFile))
        {
            await RemoveCacheAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var deleted = false;
        await _journalFile.WithPathLockAsync(
                sessionFile,
                () =>
                {
                    if (!File.Exists(sessionFile))
                    {
                        return Task.CompletedTask;
                    }

                    File.Delete(sessionFile);
                    _sessionFiles.TryRemove(sessionId, out _);
                    InvalidateMetadataProjectionCache(sessionFile);
                    _journalFile.ForgetLatestNotes(sessionFile);
                    DeleteEmptySessionDirectories(Path.GetDirectoryName(sessionFile));
                    deleted = true;
                    return Task.CompletedTask;
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (deleted)
        {
            await RemoveCacheAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    private async Task<string> GetOrCreateSessionFilePathAsync(
        string sessionId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var sessionFile = _layout.GetSessionFilePath(sessionId, createdAt);
        _sessionFiles[sessionId] = sessionFile;
        return sessionFile;
    }

    private async Task<string> GetExistingSessionFilePathAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var sessionFile = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFile is null)
        {
            throw new InvalidOperationException($"Local session '{sessionId}' does not exist.");
        }

        return sessionFile;
    }

    /// <inheritdoc />
    /// <remarks>The folder is beside the journal and has its name: <c>&lt;session&gt;.attachments</c>.</remarks>
    public async ValueTask<string?> GetAttachmentDirectoryAsync(string sessionId, CancellationToken cancellationToken)
    {
        var sessionFile = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return sessionFile is null || Path.GetDirectoryName(sessionFile) is not { Length: > 0 } directory
            ? null
            : Path.Combine(directory, Path.GetFileNameWithoutExtension(sessionFile) + ".attachments");
    }

    private async Task<string?> TryGetSessionFilePathAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (_sessionFiles.TryGetValue(sessionId, out var cachedPath) && File.Exists(cachedPath))
        {
            return cachedPath;
        }

        if (_projectionCache is not null)
        {
            var cachedProjection = await _projectionCache
                .GetSessionAsync(sessionId, CreateCacheProjectionContext(), cancellationToken)
                .ConfigureAwait(false);
            if (cachedProjection is not null && File.Exists(cachedProjection.JournalPath))
            {
                _sessionFiles[sessionId] = cachedProjection.JournalPath;
                return cachedProjection.JournalPath;
            }
        }

        if (!Directory.Exists(_layout.SessionsRootPath))
        {
            return null;
        }

        foreach (var sessionFile in Directory.EnumerateFiles(_layout.SessionsRootPath, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(sessionFile),
                    sessionId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            _sessionFiles[sessionId] = sessionFile;
            return sessionFile;
        }

        return null;
    }

    private async Task<ModelProviderId> ResolveProviderIdAsync(
        string sessionId,
        string providerKey,
        CancellationToken cancellationToken)
    {
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: false, cancellationToken).ConfigureAwait(false);
        return projection?.Summary?.ProviderId ?? new ModelProviderId(providerKey);
    }

    internal async Task CommitProviderSelectionAsync(AgentTransferSnapshot original, AgentSessionSummary summary,
        AgentSessionState state, string? header, IReadOnlyList<string> additionalSnapshots, CancellationToken token)
    {
        var path = await TryGetSessionFilePathAsync(summary.SessionId, token).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The transfer session no longer exists.");
        var snapshots = new List<string>
        {
            new AgentRawEvent(summary.ProviderId, summary.SessionId, summary.UpdatedAt, SessionSummaryEventType,
                JsonSerializer.SerializeToElement(summary, AgentJsonSerializerContext.Default.AgentSessionSummary)).ToJson(),
            new AgentRawEvent(summary.ProviderId, summary.SessionId, state.UpdatedAt, SessionStateEventType,
                JsonSerializer.SerializeToElement(state, AgentJsonSerializerContext.Default.AgentSessionState)).ToJson(),
        };
        snapshots.AddRange(additionalSnapshots);
        await _journalFile.ReplaceWithSnapshotsAsync(path, original.Revision, header, snapshots, token).ConfigureAwait(false);
    }

    internal async Task<AgentTransferSnapshot> ReadTransferSnapshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        var path = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The original transfer session does not exist.");
        return await _journalFile.WithPathLockAsync(path, async () =>
        {
            var before = GetFileStamp(path) ?? throw new KeyNotFoundException("The original transfer journal does not exist.");
            // Unlike display/history reads, transfer must reject a malformed final record.
            var projection = await ProjectSessionFileWithHistoryAsync(path, cancellationToken, strict: true).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (GetFileStamp(path) != before)
                throw new InvalidOperationException("The journal changed during transfer preparation.");
            var summary = projection.Summary;
            var state = projection.State;
            if (summary is null || state is null || summary.SessionId != sessionId || state.SessionId != sessionId
                || !string.Equals(summary.ProviderKey, state.ProviderKey, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(summary.ProtocolFamily, state.ProtocolFamily, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The original session requires recovery before provider transfer.");
            if (projection.History.Any(value => value.SessionId != sessionId))
                throw new InvalidOperationException("The journal contains another session's history.");
            return new AgentTransferSnapshot(summary, state, projection.History,
                new AgentHistoryRevision(sessionId, before.Length, before.LastWriteTimeUtc.Ticks));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionProjection?> TryProjectSessionAsync(
        string sessionId,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        if (!includeHistory && _projectionCache is not null)
        {
            var cachedProjection = await _projectionCache
                .GetSessionAsync(sessionId, CreateCacheProjectionContext(), cancellationToken)
                .ConfigureAwait(false);
            if (cachedProjection is not null)
            {
                _sessionFiles[cachedProjection.Summary.SessionId] = cachedProjection.JournalPath;
                return new SessionProjection(cachedProjection.Summary, cachedProjection.State, [], cachedProjection.ViewState);
            }
        }

        var sessionFile = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFile is null || !File.Exists(sessionFile))
        {
            return null;
        }

        var projection = await ProjectSessionFileAsync(sessionFile, includeHistory, cancellationToken).ConfigureAwait(false);
        return includeHistory
            ? projection
            : projection with { ViewState = null };
    }

    private async Task<SessionProjection> ProjectSessionFileAsync(
        string sessionFile,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        return await _journalFile.WithPathLockAsync(
                sessionFile,
                () => includeHistory
                    ? ProjectSessionFileWithHistoryAsync(sessionFile, cancellationToken)
                    : ProjectSessionMetadataFileAsync(sessionFile, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SessionProjection> ProjectSessionMetadataFileAsync(
        string sessionFile,
        CancellationToken cancellationToken)
    {
        var cacheKey = Path.GetFullPath(sessionFile);
        var before = GetFileStamp(sessionFile);
        if (before is not null &&
            _metadataProjectionCache.TryGetValue(cacheKey, out var cached) &&
            cached.Stamp == before)
        {
            return cached.Projection;
        }

        var projection = await ProjectSessionMetadataFileUncachedAsync(sessionFile, cancellationToken).ConfigureAwait(false);
        var after = GetFileStamp(sessionFile);
        if (before is not null && before == after)
        {
            _metadataProjectionCache[cacheKey] = new CachedSessionProjection(before.Value, projection);
        }

        return projection;
    }

    private async Task<SessionProjection> ProjectSessionMetadataFileUncachedAsync(
        string sessionFile,
        CancellationToken cancellationToken)
    {
        AgentSessionSummary? summary = null;
        AgentSessionState? state = null;

        foreach (var line in await ReadMetadataProbeLinesAsync(sessionFile, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                ProjectMetadataSnapshot(document.RootElement, ref summary, ref state);
            }
            catch (JsonException)
            {
            }
        }

        return NormalizeProjection(new SessionProjection(summary, state, []));
    }

    private static async Task<IReadOnlyList<string>> ReadMetadataProbeLinesAsync(
        string sessionFile,
        CancellationToken cancellationToken)
    {
        var length = new FileInfo(sessionFile).Length;
        if (length <= 0)
        {
            return [];
        }

        if (length <= MetadataProbeTailByteCount)
        {
            return await ReadHeadLinesAsync(sessionFile, (int)length, cancellationToken).ConfigureAwait(false);
        }

        var lines = new List<string>(await ReadHeadLinesAsync(sessionFile, MetadataProbeHeadByteCount, cancellationToken).ConfigureAwait(false));
        lines.AddRange(await ReadTailLinesAsync(sessionFile, MetadataProbeTailByteCount, cancellationToken).ConfigureAwait(false));
        return lines;
    }

    private async Task<SessionProjection> ProjectSessionFileWithHistoryAsync(
        string sessionFile,
        CancellationToken cancellationToken,
        bool strict = false)
    {
        AgentSessionSummary? summary = null;
        AgentSessionState? state = null;
        var history = new List<AgentEvent>();

        await using var stream = await OpenReadStreamAsync(sessionFile, cancellationToken).ConfigureAwait(false);
        await foreach (var @event in ReadJournalEventsAsync(stream, tolerateIncompleteTail: !strict, cancellationToken).ConfigureAwait(false))
        {
            if (@event is AgentRawEvent rawEvent)
            {
                if (rawEvent.BackendEventType == SessionSummaryEventType)
                {
                    var snapshot = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentSessionSummary);
                    if (snapshot is not null)
                    {
                        summary = MergeSummarySnapshot(summary, snapshot);
                    }

                    continue;
                }

                if (rawEvent.BackendEventType == SessionStateEventType)
                {
                    var snapshot = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentSessionState);
                    if (snapshot is not null)
                    {
                        state = snapshot;
                    }

                    continue;
                }

                if (rawEvent.BackendEventType is CodeAltaSessionHeaderEventType or CodeAltaSessionStateEventType)
                {
                    continue;
                }
            }

            history.Add(@event);
        }

        return NormalizeProjection(new SessionProjection(summary, state, history));
    }

    private static void ProjectMetadataSnapshot(
        JsonElement element,
        ref AgentSessionSummary? summary,
        ref AgentSessionState? state)
    {
        if (!element.TryGetProperty("$type", out var typeElement) ||
            !string.Equals(typeElement.GetString(), "raw", StringComparison.Ordinal))
        {
            return;
        }

        if (!element.TryGetProperty("backendEventType", out var eventTypeElement) ||
            !element.TryGetProperty("raw", out var rawElement))
        {
            return;
        }

        var eventType = eventTypeElement.GetString();
        if (string.Equals(eventType, SessionSummaryEventType, StringComparison.Ordinal))
        {
            var snapshot = rawElement.Deserialize(AgentJsonSerializerContext.Default.AgentSessionSummary);
            if (snapshot is not null)
            {
                summary = MergeSummarySnapshot(summary, snapshot);
            }

            return;
        }

        if (string.Equals(eventType, SessionStateEventType, StringComparison.Ordinal))
        {
            var snapshot = rawElement.Deserialize(AgentJsonSerializerContext.Default.AgentSessionState);
            if (snapshot is not null)
            {
                state = snapshot;
            }
        }
    }

    private static SessionProjection NormalizeProjection(SessionProjection projection)
    {
        var summary = NormalizeSummary(projection.Summary);
        var state = NormalizeState(projection.State, summary);
        return projection with { Summary = summary, State = state };
    }

    private static AgentSessionSummary? NormalizeSummary(AgentSessionSummary? summary)
    {
        if (summary is null)
        {
            return null;
        }

        var providerKey = NormalizeOptionalText(summary.ProviderKey)
            ?? NormalizeOptionalText(summary.ProviderId.Value)
            ?? string.Empty;
        var ProviderId = string.IsNullOrWhiteSpace(summary.ProviderId.Value)
            ? new ModelProviderId(providerKey)
            : summary.ProviderId;
        return summary with
        {
            ProviderId = ProviderId,
            ProviderKey = providerKey,
            ProtocolFamily = summary.ProtocolFamily ?? string.Empty,
        };
    }

    private static AgentSessionState? NormalizeState(AgentSessionState? state, AgentSessionSummary? summary)
    {
        if (state is null)
        {
            return null;
        }

        return state with
        {
            ProviderKey = NormalizeOptionalText(state.ProviderKey) ?? summary?.ProviderKey ?? string.Empty,
            ProtocolFamily = NormalizeOptionalText(state.ProtocolFamily) ?? summary?.ProtocolFamily ?? string.Empty,
        };
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AgentSessionSummary MergeSummarySnapshot(
        AgentSessionSummary? current,
        AgentSessionSummary snapshot)
    {
        if (current is null)
        {
            return snapshot;
        }

        return snapshot with
        {
            ParentSessionId = NormalizeOptionalText(snapshot.ParentSessionId) ?? NormalizeOptionalText(current.ParentSessionId),
            CreatedBySessionId = NormalizeOptionalText(snapshot.CreatedBySessionId) ?? NormalizeOptionalText(current.CreatedBySessionId),
            CreatedByRunId = snapshot.CreatedByRunId ?? current.CreatedByRunId,
        };
    }

    private async IAsyncEnumerable<AgentEvent> ReadJournalEventsAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadStreamAsync(path, cancellationToken).ConfigureAwait(false);
        await foreach (var entry in ReadJournalEventsAsync(stream, tolerateIncompleteTail: true, cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    private static async IAsyncEnumerable<AgentEvent> ReadJournalEventsAsync(
        Stream stream,
        bool tolerateIncompleteTail,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Utf8WithoutBom, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AgentEvent? @event;
            try
            {
                @event = JsonSerializer.Deserialize(line, AgentJsonSerializerContext.Default.AgentEvent)
                    ?? throw new JsonException("Journal line deserialized to null.");
            }
            catch (JsonException) when (tolerateIncompleteTail && reader.Peek() < 0)
            {
                yield break;
            }

            yield return @event;
        }

        // Canonical writes are UTF-8. Tolerant reads may detect legacy BOMs, but
        // appending UTF-8 to a different encoding would acknowledge unreadable notes.
        if (!tolerateIncompleteTail && reader.CurrentEncoding.CodePage != Utf8WithoutBom.CodePage)
        {
            throw new InvalidDataException("Notes cannot append to a non-UTF-8 journal. No journal bytes were changed.");
        }
    }

    private static async Task<IReadOnlyList<string>> ReadTailLinesAsync(
        string path,
        int byteCount,
        CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadStreamAsync(path, cancellationToken).ConfigureAwait(false);
        var length = stream.Length;
        var count = (int)Math.Min(byteCount, length);
        if (count == 0)
        {
            return [];
        }

        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            stream.Seek(-count, SeekOrigin.End);
            var read = 0;
            while (read < count)
            {
                var current = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
                if (current == 0)
                {
                    break;
                }

                read += current;
            }

            var text = Utf8WithoutBom.GetString(buffer, 0, read);
            if (count < length)
            {
                var firstNewline = text.IndexOf('\n');
                text = firstNewline >= 0 ? text[(firstNewline + 1)..] : string.Empty;
            }

            return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadHeadLinesAsync(
        string path,
        int byteCount,
        CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadStreamAsync(path, cancellationToken).ConfigureAwait(false);
        var length = stream.Length;
        var count = (int)Math.Min(byteCount, length);
        if (count == 0)
        {
            return [];
        }

        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var read = 0;
            while (read < count)
            {
                var current = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
                if (current == 0)
                {
                    break;
                }

                read += current;
            }

            var text = Utf8WithoutBom.GetString(buffer, 0, read);
            if (count < length)
            {
                var lastNewline = text.LastIndexOf('\n');
                text = lastNewline >= 0 ? text[..lastNewline] : string.Empty;
            }

            return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task AppendLinesAsync(
        string path,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path '{path}' did not resolve to a parent directory.");
        Directory.CreateDirectory(directory);

        await _journalFile.AppendLinesAsync(path, lines, Utf8WithoutBom, cancellationToken)
            .ConfigureAwait(false);
        InvalidateMetadataProjectionCache(path);
    }

    private static Task<FileStream> OpenReadStreamAsync(string path, CancellationToken cancellationToken)
        => OpenReadStreamAsync(path, ReadRetryTime, cancellationToken);

    private static Task<FileStream> OpenReadStreamAsync(string path, TimeSpan? maxRetryTime, CancellationToken cancellationToken)
        => AgentSessionJournalFile.RetryFileOperationAsync(
            () => Task.FromResult(new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true)),
            maxRetryTime,
            cancellationToken);

    private static FileStamp? GetFileStamp(string path)
    {
        var fileInfo = new FileInfo(path);
        return fileInfo.Exists
            ? new FileStamp(fileInfo.LastWriteTimeUtc, fileInfo.Length)
            : null;
    }

    private void InvalidateMetadataProjectionCache(string path)
        => _metadataProjectionCache.TryRemove(Path.GetFullPath(path), out _);

    private static bool MatchesScope(AgentSessionSummary summary, string protocolFamily, string providerKey)
    {
        return string.Equals(summary.ProtocolFamily, protocolFamily, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(summary.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase);
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

    private static AgentSessionMetadata ToMetadata(
        AgentSessionSummary summary,
        AgentSessionState? state,
        AgentSessionViewStateMetadata? localState = null)
        => new(
            SessionId: summary.SessionId,
            CreatedAt: summary.CreatedAt,
            UpdatedAt: summary.UpdatedAt,
            Summary: summary.Summary,
            Context: summary.WorkingDirectory is null ? null : new AgentSessionContext(summary.WorkingDirectory),
            WorkspacePath: summary.WorkingDirectory,
            Details: new RawApiSessionMetadataDetails(
                ProviderSessionId: state?.ProviderSessionId,
                Title: summary.Title),
            ProtocolFamily: summary.ProtocolFamily,
            ProviderKey: summary.ProviderKey,
            ModelId: summary.ModelId,
            ReasoningEffort: summary.ReasoningEffort,
            AgentPromptId: summary.AgentPromptId,
            ParentSessionId: summary.ParentSessionId,
            CreatedBySessionId: summary.CreatedBySessionId,
            CreatedByRunId: summary.CreatedByRunId,
            ViewState: localState);

    private void DeleteEmptySessionDirectories(string? directory)
    {
        var sessionsRoot = Path.GetFullPath(_layout.SessionsRootPath);
        while (!string.IsNullOrWhiteSpace(directory) &&
               Directory.Exists(directory) &&
               Path.GetFullPath(directory).StartsWith(sessionsRoot, StringComparison.OrdinalIgnoreCase) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            if (string.Equals(Path.GetFullPath(directory), sessionsRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private sealed record SessionProjection(
        AgentSessionSummary? Summary,
        AgentSessionState? State,
        IReadOnlyList<AgentEvent> History,
        AgentSessionViewStateMetadata? ViewState = null);

    private sealed record ListedSessionProjection(string SessionFile, SessionProjection Projection);

    private readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length);

    private sealed record CachedSessionProjection(FileStamp Stamp, SessionProjection Projection);
}
