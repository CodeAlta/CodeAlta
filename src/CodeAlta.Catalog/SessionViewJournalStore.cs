using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog;

/// <summary>
/// Stores durable session metadata in the session JSONL journal.
/// </summary>
public sealed class SessionViewJournalStore
{
    /// <summary>
    /// Provider raw-event type used for the first-line session header.
    /// </summary>
    public const string SessionHeaderEventType = "codealta.sessionHeader";

    /// <summary>
    /// Provider raw-event type used for append-only session state snapshots.
    /// </summary>
    public const string SessionStateEventType = "codealta.sessionState";

    private const int LineageProbeHeadByteCount = 128 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AgentRuntimePathLayout _layout;
    private readonly AgentSessionJournalFile _journalFile;
    private readonly SessionJournalSqliteCache _sessionCache;
    private readonly ConcurrentDictionary<string, CachedLatestState> _latestStateCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionViewJournalStore" /> class.
    /// </summary>
    /// <param name="options">Catalog options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is <see langword="null" />.</exception>
    public SessionViewJournalStore(CatalogOptions options)
        : this(options, new AgentSessionJournalFile())
    {
    }

    internal SessionViewJournalStore(CatalogOptions options, AgentSessionJournalFile journalFile)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(journalFile);
        _layout = new AgentRuntimePathLayout(options.GlobalRoot);
        _journalFile = journalFile;
        _sessionCache = new SessionJournalSqliteCache(options);
    }

    /// <summary>
    /// Creates a session store that shares this journal store's in-memory per-file locks.
    /// </summary>
    /// <returns>A session store for the same agent-runtime layout.</returns>
    public FileSystemAgentSessionStore CreateSessionStore()
        => new(_layout, _journalFile, _sessionCache);

    internal IAgentSessionProjectionCache ProjectionCache => _sessionCache;

    /// <summary>
    /// Ensures a missing or empty session journal starts with a CodeAlta session header.
    /// </summary>
    /// <param name="session">Session descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task EnsureHeaderAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.SessionId) || session.CreatedAt == default)
        {
            return;
        }

        var path = GetPath(session.SessionId, session.CreatedAt);
        await _journalFile.EnsureFirstLineAsync(
                path,
                CreateHeaderEvent(session).ToJson(),
                Utf8WithoutBom,
                IsSessionHeaderLine,
                cancellationToken)
            .ConfigureAwait(false);
        await _sessionCache.UpsertSessionViewHeaderAsync(
                session.SessionId,
                path,
                SessionViewJournalHeader.FromDescriptor(session),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a session state snapshot to the session journal.
    /// </summary>
    /// <param name="session">Session descriptor.</param>
    /// <param name="state">State snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AppendStateAsync(SessionViewDescriptor session, SessionViewLocalState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(session.SessionId) || session.CreatedAt == default)
        {
            return;
        }

        var path = GetPath(session.SessionId, session.CreatedAt);
        await _journalFile.AppendLinesWithRequiredFirstLineAsync(
                path,
                CreateHeaderEvent(session).ToJson(),
                [CreateStateEvent(session, state).ToJson()],
                Utf8WithoutBom,
                IsSessionHeaderLine,
                cancellationToken)
            .ConfigureAwait(false);
        InvalidateLatestStateCache(path);
        await _sessionCache.UpsertSessionViewHeaderAsync(
                session.SessionId,
                path,
                SessionViewJournalHeader.FromDescriptor(session),
                cancellationToken)
            .ConfigureAwait(false);
        await _sessionCache.UpsertSessionViewStateAsync(session.SessionId, path, state, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the first-line session header for a journal.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="createdAt">Session creation timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The header, or <see langword="null" /> when the journal has no header.</returns>
    public async Task<SessionViewJournalHeader?> ReadHeaderAsync(string sessionId, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (createdAt == default)
        {
            return null;
        }

        return await ReadHeaderFromPathAsync(GetPath(sessionId, createdAt), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists first-line session headers from all session journals.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered headers.</returns>
    public async Task<IReadOnlyList<SessionViewJournalHeader>> ListHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_layout.SessionsRootPath))
        {
            return [];
        }

        var results = new List<SessionViewJournalHeader>();
        foreach (var path in Directory.EnumerateFiles(_layout.SessionsRootPath, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = await ReadHeaderFromPathAsync(path, cancellationToken).ConfigureAwait(false);
            if (header is not null)
            {
                results.Add(header);
            }
        }

        return results;
    }

    /// <summary>
    /// Reads the latest state snapshot by probing near the end of the journal.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="createdAt">Session creation timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest state snapshot, or <see langword="null" />.</returns>
    public async Task<SessionViewLocalState?> ReadLatestStateAsync(string sessionId, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (createdAt == default)
        {
            return null;
        }

        var path = GetPath(sessionId, createdAt);
        var before = GetFileStamp(path);
        if (before is null)
        {
            return null;
        }

        var cacheKey = Path.GetFullPath(path);
        if (_latestStateCache.TryGetValue(cacheKey, out var cached) && cached.Stamp == before)
        {
            return CloneLocalState(cached.State);
        }

        var latestState = await ReadLatestStateFromPathAsync(path, cancellationToken).ConfigureAwait(false);
        var after = GetFileStamp(path);
        if (before == after)
        {
            _latestStateCache[cacheKey] = new CachedLatestState(before.Value, CloneLocalState(latestState));
        }

        return latestState;
    }

    private static SessionViewLocalState? CloneLocalState(SessionViewLocalState? state)
    {
        if (state is null)
        {
            return null;
        }

        return new SessionViewLocalState
        {
            ProviderKey = state.ProviderKey,
            ModelId = state.ModelId,
            ReasoningEffort = state.ReasoningEffort,
            Archived = state.Archived,
            MessageCount = state.MessageCount,
            ParentSessionId = state.ParentSessionId,
            CreatedBy = state.CreatedBy,
            PromptProvenance = state.PromptProvenance?.Select(static provenance => new SessionViewPromptProvenance
            {
                PromptId = provenance.PromptId,
                Kind = provenance.Kind,
                RunId = provenance.RunId,
                Queued = provenance.Queued,
                PromptPreview = provenance.PromptPreview,
                SubmittedBy = provenance.SubmittedBy,
                CreatedAt = provenance.CreatedAt,
            }).ToList() ?? [],
            QueuedPrompts = state.QueuedPrompts?.Select(static prompt => new SessionViewQueuedPrompt
            {
                QueueItemId = prompt.QueueItemId,
                Kind = prompt.Kind,
                Prompt = prompt.Prompt,
                PromptPreview = prompt.PromptPreview,
                State = prompt.State,
                RunId = prompt.RunId,
                SubmittedBy = prompt.SubmittedBy,
                CreatedAt = prompt.CreatedAt,
                DrainedAt = prompt.DrainedAt,
                LastError = prompt.LastError,
            }).ToList() ?? [],
        };
    }

    internal static async Task<SessionViewLocalState?> ReadLatestStateFromPathAsync(string path, CancellationToken cancellationToken)
    {
        var latestState = await TryReadLatestStateFromEndAsync(path, 64 * 1024, cancellationToken).ConfigureAwait(false);
        return HasLineage(latestState)
            ? latestState
            : await MergeMissingLineageFromJournalAsync(path, latestState, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SessionViewLocalState?> TryReadLatestStateFromEndAsync(
        string path,
        int chunkSize,
        CancellationToken cancellationToken)
    {
        SessionViewLocalState? latestState = null;
        await ReadLinesFromEndAsync(
                path,
                chunkSize,
                line =>
                {
                    if (!TryDeserializeRawEvent(line, out var rawEvent) || rawEvent.BackendEventType != SessionStateEventType)
                    {
                        return true;
                    }

                    latestState = rawEvent.Raw.Deserialize(SessionViewJournalJsonSerializerContext.Default.SessionViewLocalState);
                    return false;
                },
                cancellationToken)
            .ConfigureAwait(false);
        return latestState;
    }

    private static async Task<SessionViewLocalState?> MergeMissingLineageFromJournalAsync(
        string path,
        SessionViewLocalState? latestState,
        CancellationToken cancellationToken)
    {
        SessionViewLocalState? headLatestState = null;
        SessionViewLocalState? lineageState = null;
        await ReadLinesFromStartAsync(
                path,
                line =>
                {
                    if (!TryDeserializeRawEvent(line, out var rawEvent))
                    {
                        return true;
                    }

                    if (rawEvent.BackendEventType == SessionHeaderEventType)
                    {
                        var header = rawEvent.Raw.Deserialize(SessionViewJournalJsonSerializerContext.Default.SessionViewJournalHeader);
                        if (header is not null)
                        {
                            lineageState = MergeLineage(lineageState, header.ParentSessionId, header.CreatedBy);
                            if (HasLineage(lineageState))
                            {
                                return false;
                            }
                        }

                        return NeedsMoreLineage(latestState, lineageState);
                    }

                    if (rawEvent.BackendEventType == SessionStateEventType)
                    {
                        var state = rawEvent.Raw.Deserialize(SessionViewJournalJsonSerializerContext.Default.SessionViewLocalState);
                        if (state is not null)
                        {
                            headLatestState = state;
                            lineageState = MergeLineage(lineageState, state.ParentSessionId, state.CreatedBy);
                        }
                    }

                    return NeedsMoreLineage(latestState, lineageState);
                },
                cancellationToken)
            .ConfigureAwait(false);

        latestState ??= headLatestState;

        if (latestState is null)
        {
            return lineageState;
        }

        if (lineageState is null)
        {
            return latestState;
        }

        latestState.ParentSessionId = string.IsNullOrWhiteSpace(latestState.ParentSessionId)
            ? lineageState.ParentSessionId
            : latestState.ParentSessionId;
        latestState.CreatedBy ??= lineageState.CreatedBy;
        return latestState;
    }

    private static bool HasLineage(SessionViewLocalState? state)
        => !string.IsNullOrWhiteSpace(state?.ParentSessionId) || state?.CreatedBy is not null;

    private static bool NeedsMoreLineage(SessionViewLocalState? latestState, SessionViewLocalState? lineageState)
    {
        if (latestState is null)
        {
            return true;
        }

        return !HasLineage(lineageState);
    }

    private static SessionViewLocalState? MergeLineage(
        SessionViewLocalState? current,
        string? parentSessionId,
        AltaActorProvenance? createdBy)
    {
        if (string.IsNullOrWhiteSpace(parentSessionId) && createdBy is null)
        {
            return current;
        }

        current ??= new SessionViewLocalState();
        if (!string.IsNullOrWhiteSpace(parentSessionId))
        {
            current.ParentSessionId = parentSessionId;
        }

        if (createdBy is not null)
        {
            current.CreatedBy = createdBy;
        }

        return current;
    }

    private static async Task ReadLinesFromStartAsync(
        string path,
        Func<string, bool> handleLine,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var lineBuffer = ArrayPool<byte>.Shared.Rent(4096);
        var remainingBytes = Math.Min(LineageProbeHeadByteCount, stream.Length);
        var lineLength = 0;
        var isFirstLine = true;
        try
        {
            while (remainingBytes > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, remainingBytes);
                var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                remainingBytes -= read;
                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        var line = DecodeForwardLine(lineBuffer, lineLength, isFirstLine).TrimEnd('\r');
                        isFirstLine = false;
                        lineLength = 0;
                        if (!handleLine(line))
                        {
                            return;
                        }

                        continue;
                    }

                    if (lineLength == lineBuffer.Length)
                    {
                        var replacement = ArrayPool<byte>.Shared.Rent(lineBuffer.Length * 2);
                        Array.Copy(lineBuffer, replacement, lineLength);
                        ArrayPool<byte>.Shared.Return(lineBuffer);
                        lineBuffer = replacement;
                    }

                    lineBuffer[lineLength++] = value;
                }
            }

            if (lineLength > 0 && stream.Position == stream.Length)
            {
                var line = DecodeForwardLine(lineBuffer, lineLength, isFirstLine).TrimEnd('\r');
                _ = handleLine(line);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(lineBuffer);
        }
    }

    private string GetPath(string sessionId, DateTimeOffset createdAt)
        => _layout.GetSessionFilePath(sessionId, createdAt);

    private static AgentRawEvent CreateHeaderEvent(SessionViewDescriptor session)
        => new(
            new ModelProviderId(session.ProviderId),
            session.SessionId,
            session.CreatedAt,
            SessionHeaderEventType,
            JsonSerializer.SerializeToElement(SessionViewJournalHeader.FromDescriptor(session), SessionViewJournalJsonSerializerContext.Default.SessionViewJournalHeader));

    private static AgentRawEvent CreateStateEvent(SessionViewDescriptor session, SessionViewLocalState state)
        => new(
            new ModelProviderId(session.ProviderId),
            session.SessionId,
            DateTimeOffset.UtcNow,
            SessionStateEventType,
            JsonSerializer.SerializeToElement(state, SessionViewJournalJsonSerializerContext.Default.SessionViewLocalState));

    internal static async Task<SessionViewJournalHeader?> ReadHeaderFromPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        using var reader = new StreamReader(stream, Utf8WithoutBom, detectEncodingFromByteOrderMarks: true);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line) || !TryDeserializeRawEvent(line, out var rawEvent) || rawEvent.BackendEventType != SessionHeaderEventType)
        {
            return null;
        }

        return rawEvent.Raw.Deserialize(SessionViewJournalJsonSerializerContext.Default.SessionViewJournalHeader);
    }

    private static async Task ReadLinesFromEndAsync(
        string path,
        int chunkSize,
        Func<string, bool> handleLine,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var length = stream.Length;
        var position = length;
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        var lineBuffer = ArrayPool<byte>.Shared.Rent(4096);
        var lineLength = 0;
        var previousByteWasLineFeed = false;
        try
        {
            while (position > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(chunkSize, position);
                position -= count;
                var read = 0;
                stream.Seek(position, SeekOrigin.Begin);
                while (read < count)
                {
                    var current = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
                    if (current == 0)
                    {
                        break;
                    }

                    read += current;
                }

                for (var index = read - 1; index >= 0; index--)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        if (lineLength > 0)
                        {
                            var line = DecodeReversedLine(lineBuffer, lineLength).TrimEnd('\r');
                            lineLength = 0;
                            if (!handleLine(line))
                            {
                                return;
                            }
                        }

                        previousByteWasLineFeed = true;
                        continue;
                    }

                    if (previousByteWasLineFeed && value == (byte)'\r')
                    {
                        previousByteWasLineFeed = false;
                        continue;
                    }

                    previousByteWasLineFeed = false;
                    if (lineLength == lineBuffer.Length)
                    {
                        var replacement = ArrayPool<byte>.Shared.Rent(lineBuffer.Length * 2);
                        Array.Copy(lineBuffer, replacement, lineLength);
                        ArrayPool<byte>.Shared.Return(lineBuffer);
                        lineBuffer = replacement;
                    }

                    lineBuffer[lineLength++] = value;
                }
            }

            if (lineLength > 0)
            {
                _ = handleLine(DecodeReversedLine(lineBuffer, lineLength).TrimEnd('\r'));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(lineBuffer);
        }
    }

    private static string DecodeForwardLine(byte[] line, int length, bool stripBom)
    {
        const int Utf8BomLength = 3;
        if (stripBom &&
            length >= Utf8BomLength &&
            line[0] == 0xEF &&
            line[1] == 0xBB &&
            line[2] == 0xBF)
        {
            return Utf8WithoutBom.GetString(line, Utf8BomLength, length - Utf8BomLength);
        }

        return Utf8WithoutBom.GetString(line, 0, length);
    }

    private static string DecodeReversedLine(byte[] reversedLine, int length)
    {
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            for (var index = 0; index < length; index++)
            {
                rented[index] = reversedLine[length - index - 1];
            }

            return Utf8WithoutBom.GetString(rented, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static FileStamp? GetFileStamp(string path)
    {
        var fileInfo = new FileInfo(path);
        return fileInfo.Exists
            ? new FileStamp(fileInfo.LastWriteTimeUtc, fileInfo.Length)
            : null;
    }

    private void InvalidateLatestStateCache(string path)
        => _latestStateCache.TryRemove(Path.GetFullPath(path), out _);

    private static bool TryDeserializeRawEvent(string? line, out RawJournalEvent rawEvent)
    {
        rawEvent = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("$type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "raw", StringComparison.Ordinal) ||
                !root.TryGetProperty("backendEventType", out var eventTypeElement) ||
                !root.TryGetProperty("raw", out var rawElement))
            {
                return false;
            }

            rawEvent = new RawJournalEvent(eventTypeElement.GetString() ?? string.Empty, rawElement.Clone());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSessionHeaderLine(string? line)
        => TryDeserializeRawEvent(line, out var rawEvent) && rawEvent.BackendEventType == SessionHeaderEventType;

    private readonly record struct RawJournalEvent(string BackendEventType, JsonElement Raw);

    private readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length);

    private sealed record CachedLatestState(FileStamp Stamp, SessionViewLocalState? State);
}

/// <summary>
/// Describes durable first-line session metadata stored in a session journal.
/// </summary>
public sealed class SessionViewJournalHeader
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public SessionViewKind Kind { get; set; }

    // Persisted session-view journals used backend_id before provider terminology; keep the wire name stable.
    [JsonPropertyName("backend_id")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("provider_key")]
    public string? ProviderKey { get; set; }

    [JsonPropertyName("project_ref")]
    public string? ProjectRef { get; set; }

    [JsonPropertyName("parent_session_id")]
    public string? ParentSessionId { get; set; }

    [JsonPropertyName("created_by")]
    public AltaActorProvenance? CreatedBy { get; set; }

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    public static SessionViewJournalHeader FromDescriptor(SessionViewDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new SessionViewJournalHeader
        {
            SessionId = descriptor.SessionId,
            Kind = descriptor.Kind,
            ProviderId = descriptor.ProviderId,
            ProviderKey = descriptor.ProviderKey,
            ProjectRef = descriptor.ProjectRef,
            ParentSessionId = descriptor.ParentSessionId,
            CreatedBy = descriptor.CreatedBy,
            WorkingDirectory = descriptor.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(descriptor.Title) ? null : descriptor.Title,
            CreatedAt = descriptor.CreatedAt,
        };
    }

    public SessionViewDescriptor ToDescriptor()
        => new()
        {
            SessionId = SessionId,
            Kind = Kind,
            ProviderId = ProviderId,
            ProviderKey = ProviderKey,
            ProjectRef = ProjectRef,
            ParentSessionId = ParentSessionId,
            CreatedBy = CreatedBy,
            WorkingDirectory = WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(Title) ? SessionId : Title,
            Status = SessionViewStatus.Active,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            LastActiveAt = CreatedAt,
        };
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SessionViewJournalHeader))]
[JsonSerializable(typeof(SessionViewLocalState))]
[JsonSerializable(typeof(SessionViewPreference))]
[JsonSerializable(typeof(AltaActorProvenance))]
[JsonSerializable(typeof(SessionViewQueuedPrompt))]
[JsonSerializable(typeof(SessionViewPromptProvenance))]
internal sealed partial class SessionViewJournalJsonSerializerContext : JsonSerializerContext;
