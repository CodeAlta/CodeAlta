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

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AgentRuntimePathLayout _layout;
    private readonly AgentSessionJournalFile _journalFile;
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
    }

    /// <summary>
    /// Creates a session store that shares this journal store's in-memory per-file locks.
    /// </summary>
    /// <returns>A session store for the same agent-runtime layout.</returns>
    public FileSystemAgentSessionStore CreateSessionStore()
        => new(_layout, _journalFile);

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

        await _journalFile.EnsureFirstLineAsync(
                GetPath(session.SessionId, session.CreatedAt),
                CreateHeaderEvent(session).ToJson(),
                Utf8WithoutBom,
                IsSessionHeaderLine,
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

        var latestState = await ReadLatestStateUncachedAsync(path, cancellationToken).ConfigureAwait(false);
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

    private static async Task<SessionViewLocalState?> ReadLatestStateUncachedAsync(string path, CancellationToken cancellationToken)
    {
        await foreach (var line in ReadLinesFromEndAsync(path, 64 * 1024, cancellationToken).ConfigureAwait(false))
        {
            if (!TryDeserializeRawEvent(line, out var rawEvent) || rawEvent.BackendEventType != SessionStateEventType)
            {
                continue;
            }

            return rawEvent.Raw.Deserialize(SessionViewJournalJsonSerializerContext.Default.SessionViewLocalState);
        }

        return null;
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

    private async Task<SessionViewJournalHeader?> ReadHeaderFromPathAsync(string path, CancellationToken cancellationToken)
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

    private static async IAsyncEnumerable<string> ReadLinesFromEndAsync(
        string path,
        int chunkSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
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
                            yield return DecodeReversedLine(lineBuffer, lineLength).TrimEnd('\r');
                            lineLength = 0;
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
                yield return DecodeReversedLine(lineBuffer, lineLength).TrimEnd('\r');
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(lineBuffer);
        }
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
