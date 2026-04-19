using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Persists local raw-API session journals on the filesystem.
/// </summary>
public sealed class FileSystemLocalAgentSessionStore : ILocalAgentSessionStore
{
    private const string SessionSummaryEventType = "local.sessionSummary";
    private const string SessionStateEventType = "local.sessionState";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly LocalAgentRuntimePathLayout _layout;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemLocalAgentSessionStore"/> class.
    /// </summary>
    /// <param name="layout">Filesystem layout.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="layout"/> is <see langword="null" />.</exception>
    public FileSystemLocalAgentSessionStore(LocalAgentRuntimePathLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
    }

    /// <inheritdoc />
    public async Task<LocalAgentProviderDescriptor> UpsertProviderAsync(
        LocalAgentProviderDescriptor provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var providerPath = _layout.GetProviderDescriptorPath(provider.ProtocolFamily, provider.ProviderKey);
        await WriteFileAtomicallyAsync(
                providerPath,
                provider.ToJson(),
                cancellationToken)
            .ConfigureAwait(false);
        return provider;
    }

    /// <inheritdoc />
    public async Task<LocalAgentProviderDescriptor?> GetProviderAsync(
        string protocolFamily,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        var providerPath = _layout.GetProviderDescriptorPath(protocolFamily, providerKey);
        if (!File.Exists(providerPath))
        {
            return null;
        }

        return await ReadJsonFileAsync(
                providerPath,
                AgentJsonSerializerContext.Default.LocalAgentProviderDescriptor,
                cancellationToken)
            .ConfigureAwait(false);
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
        var projection = await TryProjectSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (projection is null || projection.Summary is null)
        {
            return null;
        }

        return MatchesScope(projection.Summary, protocolFamily, providerKey)
            ? projection.Summary
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalAgentSessionSummary>> ListSessionsAsync(
        string protocolFamily,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_layout.SessionsRootPath))
        {
            return [];
        }

        var results = new List<LocalAgentSessionSummary>();
        foreach (var sessionFile in Directory.EnumerateFiles(_layout.SessionsRootPath, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var projection = await ProjectSessionFileAsync(sessionFile, cancellationToken).ConfigureAwait(false);
                var summary = projection.Summary;
                if (summary is null || !MatchesScope(summary, protocolFamily, providerKey))
                {
                    continue;
                }

                results.Add(summary);
                _sessionFiles[summary.SessionId] = sessionFile;
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
        }

        return results
            .OrderByDescending(static session => session.UpdatedAt)
            .ThenByDescending(static session => session.CreatedAt)
            .ToArray();
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
        var projection = await TryProjectSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (projection is null || projection.Summary is null || !MatchesScope(projection.Summary, protocolFamily, providerKey))
        {
            return [];
        }

        return projection.History;
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
        var projection = await TryProjectSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (projection is null || projection.Summary is null || projection.State is null)
        {
            return null;
        }

        return MatchesScope(projection.Summary, protocolFamily, providerKey)
            ? projection.State
            : null;
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

        var projection = await TryProjectSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (projection?.Summary is not null && !MatchesScope(projection.Summary, protocolFamily, providerKey))
        {
            return false;
        }

        var pathLock = GetPathLock(sessionFile);
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(sessionFile))
            {
                return false;
            }

            File.Delete(sessionFile);
            _sessionFiles.TryRemove(sessionId, out _);
            DeleteEmptySessionDirectories(Path.GetDirectoryName(sessionFile));
            return true;
        }
        finally
        {
            pathLock.Release();
        }
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
        var projection = await TryProjectSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return projection?.Summary?.BackendId ?? new AgentBackendId(providerKey);
    }

    private async Task<SessionProjection?> TryProjectSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var sessionFile = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFile is null || !File.Exists(sessionFile))
        {
            return null;
        }

        return await ProjectSessionFileAsync(sessionFile, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionProjection> ProjectSessionFileAsync(
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
            }

            history.Add(@event);
        }

        return new SessionProjection(summary, state, history);
    }

    private async IAsyncEnumerable<AgentEvent> ReadJournalEventsAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
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

    private async Task AppendLinesAsync(
        string path,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path '{path}' did not resolve to a parent directory.");
        Directory.CreateDirectory(directory);

        var pathLock = GetPathLock(path);
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await using var writer = new StreamWriter(stream, Utf8WithoutBom);
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    private async Task WriteFileAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path '{path}' did not resolve to a parent directory.");
        Directory.CreateDirectory(directory);

        var pathLock = GetPathLock(path);
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(tempPath, content, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Move(tempPath, path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    private SemaphoreSlim GetPathLock(string path)
        => _pathLocks.GetOrAdd(path, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));

    private static async Task<T?> ReadJsonFileAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesScope(LocalAgentSessionSummary summary, string protocolFamily, string providerKey)
    {
        return string.Equals(summary.ProtocolFamily, protocolFamily, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(summary.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase);
    }

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
}
