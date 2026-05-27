using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Persists local raw-API session journals on the filesystem.
/// </summary>
public sealed class FileSystemLocalAgentSessionStore : ILocalAgentSessionStore
{
    private const string SessionSummaryEventType = "local.sessionSummary";
    private const string SessionStateEventType = "local.sessionState";
    private const string CodeAltaThreadHeaderEventType = "codealta.threadHeader";
    private const string CodeAltaThreadStateEventType = "codealta.threadState";
    private const int MetadataProbeHeadByteCount = 64 * 1024;
    private const int MetadataProbeTailByteCount = 256 * 1024;
    private const int DefaultMaxConcurrentMetadataProjections = 8;

    private static readonly TimeSpan ReadRetryTime = TimeSpan.FromMilliseconds(250);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly LocalAgentRuntimePathLayout _layout;
    private readonly LocalAgentSessionJournalFile _journalFile;
    private readonly int _maxConcurrentMetadataProjections;
    private readonly ConcurrentDictionary<string, CachedSessionProjection> _metadataProjectionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemLocalAgentSessionStore"/> class.
    /// </summary>
    /// <param name="layout">Filesystem layout.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="layout"/> is <see langword="null" />.</exception>
    public FileSystemLocalAgentSessionStore(LocalAgentRuntimePathLayout layout)
        : this(layout, new LocalAgentSessionJournalFile())
    {
    }

    internal FileSystemLocalAgentSessionStore(LocalAgentRuntimePathLayout layout, LocalAgentSessionJournalFile journalFile)
        : this(layout, journalFile, DefaultMaxConcurrentMetadataProjections)
    {
    }

    internal FileSystemLocalAgentSessionStore(
        LocalAgentRuntimePathLayout layout,
        LocalAgentSessionJournalFile journalFile,
        int maxConcurrentMetadataProjections)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(journalFile);
        if (maxConcurrentMetadataProjections < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentMetadataProjections), "Concurrency must be greater than zero.");
        }

        _layout = layout;
        _journalFile = journalFile;
        _maxConcurrentMetadataProjections = maxConcurrentMetadataProjections;
    }

    /// <inheritdoc />
    public async Task UpsertSessionAsync(
        LocalAgentSessionSummary session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sessionFile = await GetOrCreateSessionFilePathAsync(
            session.SessionId,
            session.CreatedAt,
            cancellationToken).ConfigureAwait(false);
        var snapshotEvent = new AgentRawEvent(
            session.BackendId,
            session.SessionId,
            session.UpdatedAt,
            SessionSummaryEventType,
            JsonSerializer.SerializeToElement(session, AgentJsonSerializerContext.Default.LocalAgentSessionSummary),
            null);

        await AppendLinesAsync(sessionFile, [snapshotEvent.ToJson()], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LocalAgentSessionSummary?> GetSessionAsync(
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
            : ToMetadata(projection.Summary, projection.State);
    }

    /// <inheritdoc />
    public async Task<LocalAgentSessionSummary?> GetSessionSummaryAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: false, cancellationToken).ConfigureAwait(false);
        return projection?.Summary;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LocalAgentSessionSummary> ListSessionSummariesAsync(
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
            var metadata = ToMetadata(projection.Projection.Summary!, projection.Projection.State);
            if (MatchesFilter(metadata, filter))
            {
                yield return metadata;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LocalAgentSessionSummary> ListSessionSummariesAsync(
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
    }

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
    /// Reads canonical session events by local session identifier without applying a provider-scope filter.
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
        LocalAgentSessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var sessionFile = await GetExistingSessionFilePathAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
        var backendId = await ResolveBackendIdAsync(state.SessionId, state.ProviderKey, cancellationToken).ConfigureAwait(false);
        var snapshotEvent = new AgentRawEvent(
            backendId,
            state.SessionId,
            state.UpdatedAt,
            SessionStateEventType,
            JsonSerializer.SerializeToElement(state, AgentJsonSerializerContext.Default.LocalAgentSessionState),
            null);

        await AppendLinesAsync(sessionFile, [snapshotEvent.ToJson()], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LocalAgentSessionState?> GetStateAsync(
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
    public async Task<LocalAgentSessionState?> GetStateAsync(
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
                    DeleteEmptySessionDirectories(Path.GetDirectoryName(sessionFile));
                    deleted = true;
                    return Task.CompletedTask;
                },
                cancellationToken)
            .ConfigureAwait(false);
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
                    DeleteEmptySessionDirectories(Path.GetDirectoryName(sessionFile));
                    deleted = true;
                    return Task.CompletedTask;
                },
                cancellationToken)
            .ConfigureAwait(false);
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

    private async Task<string?> TryGetSessionFilePathAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (_sessionFiles.TryGetValue(sessionId, out var cachedPath) && File.Exists(cachedPath))
        {
            return cachedPath;
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

    private async Task<AgentBackendId> ResolveBackendIdAsync(
        string sessionId,
        string providerKey,
        CancellationToken cancellationToken)
    {
        var projection = await TryProjectSessionAsync(sessionId, includeHistory: false, cancellationToken).ConfigureAwait(false);
        return projection?.Summary?.BackendId ?? new AgentBackendId(providerKey);
    }

    private async Task<SessionProjection?> TryProjectSessionAsync(
        string sessionId,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        var sessionFile = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFile is null || !File.Exists(sessionFile))
        {
            return null;
        }

        return await ProjectSessionFileAsync(sessionFile, includeHistory, cancellationToken).ConfigureAwait(false);
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
        LocalAgentSessionSummary? summary = null;
        LocalAgentSessionState? state = null;

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
        var lines = new List<string>(await ReadHeadLinesAsync(sessionFile, MetadataProbeHeadByteCount, cancellationToken).ConfigureAwait(false));
        lines.AddRange(await ReadTailLinesAsync(sessionFile, MetadataProbeTailByteCount, cancellationToken).ConfigureAwait(false));
        return lines;
    }

    private async Task<SessionProjection> ProjectSessionFileWithHistoryAsync(
        string sessionFile,
        CancellationToken cancellationToken)
    {
        LocalAgentSessionSummary? summary = null;
        LocalAgentSessionState? state = null;
        var history = new List<AgentEvent>();

        await foreach (var @event in ReadJournalEventsAsync(sessionFile, cancellationToken).ConfigureAwait(false))
        {
            if (@event is AgentRawEvent rawEvent)
            {
                if (rawEvent.BackendEventType == SessionSummaryEventType)
                {
                    var snapshot = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentSessionSummary);
                    if (snapshot is not null)
                    {
                        summary = snapshot;
                    }

                    continue;
                }

                if (rawEvent.BackendEventType == SessionStateEventType)
                {
                    var snapshot = rawEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentSessionState);
                    if (snapshot is not null)
                    {
                        state = snapshot;
                    }

                    continue;
                }

                if (rawEvent.BackendEventType is CodeAltaThreadHeaderEventType or CodeAltaThreadStateEventType)
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
        ref LocalAgentSessionSummary? summary,
        ref LocalAgentSessionState? state)
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
            var snapshot = rawElement.Deserialize(AgentJsonSerializerContext.Default.LocalAgentSessionSummary);
            if (snapshot is not null)
            {
                summary = snapshot;
            }

            return;
        }

        if (string.Equals(eventType, SessionStateEventType, StringComparison.Ordinal))
        {
            var snapshot = rawElement.Deserialize(AgentJsonSerializerContext.Default.LocalAgentSessionState);
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

    private static LocalAgentSessionSummary? NormalizeSummary(LocalAgentSessionSummary? summary)
    {
        if (summary is null)
        {
            return null;
        }

        var providerKey = NormalizeOptionalText(summary.ProviderKey)
            ?? NormalizeOptionalText(summary.BackendId.Value)
            ?? string.Empty;
        var backendId = string.IsNullOrWhiteSpace(summary.BackendId.Value)
            ? new AgentBackendId(providerKey)
            : summary.BackendId;
        return summary with
        {
            BackendId = backendId,
            ProviderKey = providerKey,
            ProtocolFamily = summary.ProtocolFamily ?? string.Empty,
        };
    }

    private static LocalAgentSessionState? NormalizeState(LocalAgentSessionState? state, LocalAgentSessionSummary? summary)
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

    private async IAsyncEnumerable<AgentEvent> ReadJournalEventsAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadStreamAsync(path, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Utf8WithoutBom, detectEncodingFromByteOrderMarks: true);
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
            catch (JsonException) when (reader.Peek() < 0)
            {
                yield break;
            }

            yield return @event;
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
        => LocalAgentSessionJournalFile.RetryFileOperationAsync(
            () => Task.FromResult(new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true)),
            ReadRetryTime,
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

    private static bool MatchesScope(LocalAgentSessionSummary summary, string protocolFamily, string providerKey)
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
        LocalAgentSessionSummary summary,
        LocalAgentSessionState? state)
        => new(
            summary.SessionId,
            summary.CreatedAt,
            summary.UpdatedAt,
            summary.Summary,
            summary.WorkingDirectory is null ? null : new AgentSessionContext(summary.WorkingDirectory),
            summary.WorkingDirectory,
            new RawApiSessionMetadataDetails(
                ProviderSessionId: state?.ProviderSessionId,
                Title: summary.Title),
            summary.ProtocolFamily,
            summary.ProviderKey,
            summary.ModelId,
            summary.ParentSessionId,
            summary.CreatedBySessionId,
            summary.CreatedByRunId);

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
        LocalAgentSessionSummary? Summary,
        LocalAgentSessionState? State,
        IReadOnlyList<AgentEvent> History);

    private sealed record ListedSessionProjection(string SessionFile, SessionProjection Projection);

    private readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length);

    private sealed record CachedSessionProjection(FileStamp Stamp, SessionProjection Projection);
}
