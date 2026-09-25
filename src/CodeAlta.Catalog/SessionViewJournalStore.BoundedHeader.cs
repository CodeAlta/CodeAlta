using System.Text;
using System.Text.Json;

namespace CodeAlta.Catalog;

/// <summary>Status of a bounded exact persisted session-header read.</summary>
public enum BoundedSessionHeaderStatus
{
    /// <summary>The requested first-line header was parsed.</summary>
    Found,
    /// <summary>The exact journal is absent.</summary>
    Missing,
    /// <summary>The first line exceeded the actual-byte budget.</summary>
    Incomplete,
    /// <summary>The identifier or persisted header was invalid.</summary>
    Invalid,
    /// <summary>An observed link/reparse point or I/O failure prevented a trustworthy read.</summary>
    ReadError,
}

/// <summary>A first-line-only journal result; no history, cache or session body is read.</summary>
/// <param name="Status">Explicit bounded read outcome.</param>
/// <param name="Header">Parsed header only when found.</param>
/// <param name="BytesRead">Actual bytes consumed through the first newline or the over-limit sentinel.</param>
public sealed record BoundedSessionHeaderResult(BoundedSessionHeaderStatus Status, SessionViewJournalHeader? Header, int BytesRead);

public sealed partial class SessionViewJournalStore
{
    /// <summary>Maximum actual bytes read to find and parse the first line, plus one over-limit sentinel.</summary>
    public const int MaximumBoundedHeaderBytes = 32 * 1024;

    /// <summary>Reads only the exact date-sharded, first-line persisted header without scanning journals or caches.</summary>
    /// <remarks>Requires a collision-free filename identity. Observed links are refused, but external writers and
    /// path swaps are not atomically excluded; this is not cross-process snapshot or authorization evidence by itself.</remarks>
    /// <param name="sessionId">Known exact session ID; invalid filename aliases are refused.</param>
    /// <param name="createdAt">Known persisted creation timestamp used for date sharding.</param>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>Header, missing, incomplete, invalid or read-error status and actual bytes consumed.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled the read.</exception>
    public async Task<BoundedSessionHeaderResult> ReadBoundedHeaderAsync(string? sessionId, DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BoundedSessionHeaderResult result(BoundedSessionHeaderStatus status, int count = 0, SessionViewJournalHeader? header = null)
            => new(status, header, count);
        if (sessionId is not { Length: > 0 and <= 256 } || sessionId != sessionId.Trim()
            || sessionId is "." or ".." || sessionId.Any(static c => char.IsControl(c) || char.IsSurrogate(c)
                || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            || createdAt == default || _layout.RootPath.Length > 4096 || !Path.IsPathFullyQualified(_layout.RootPath))
            return result(BoundedSessionHeaderStatus.Invalid);
        var bytes = 0;
        try
        {
            var path = _layout.GetSessionFilePath(sessionId, createdAt);
            if (path.Length > 4096) return result(BoundedSessionHeaderStatus.Invalid);
            // Check from the filesystem root downward, including the journal itself.
            var ancestors = new Stack<DirectoryInfo>();
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
                ancestors.Push(directory);
            while (ancestors.Count > 0)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(ancestors.Pop().FullName); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                { return result(BoundedSessionHeaderStatus.Missing); }
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
                    return result(BoundedSessionHeaderStatus.ReadError);
            }
            FileAttributes fileAttributes;
            try { fileAttributes = File.GetAttributes(path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { return result(BoundedSessionHeaderStatus.Missing); }
            if (fileAttributes.HasFlag(FileAttributes.ReparsePoint) || fileAttributes.HasFlag(FileAttributes.Directory))
                return result(BoundedSessionHeaderStatus.ReadError);
            var buffer = new byte[MaximumBoundedHeaderBytes + 1];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            while (bytes < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // No buffered chunk is allowed to consume history bytes beyond this first newline.
                var next = stream.ReadByte();
                if (next < 0) return result(BoundedSessionHeaderStatus.Invalid, bytes);
                buffer[bytes++] = (byte)next;
                if (next != '\n') continue;
                var lineLength = bytes - 1;
                if (lineLength >= MaximumBoundedHeaderBytes) return result(BoundedSessionHeaderStatus.Incomplete, bytes);
                var line = new UTF8Encoding(false, true).GetString(buffer.AsSpan(0, lineLength)).TrimEnd('\r');
                using var document = JsonDocument.Parse(line);
                if (DuplicateProperties(document.RootElement) || !document.RootElement.TryGetProperty("raw", out var raw)
                    || DuplicateProperties(raw) || !raw.TryGetProperty("kind", out var kind)
                    || !raw.TryGetProperty("session_id", out _) || !raw.TryGetProperty("created_at", out _)
                    || !raw.TryGetProperty("working_directory", out _) || !raw.TryGetProperty("backend_id", out _)
                    || !raw.TryGetProperty("provider_key", out _) || kind.ValueKind != JsonValueKind.String
                    || kind.GetString() is not (nameof(SessionViewKind.GlobalSession) or nameof(SessionViewKind.ProjectSession)
                        or nameof(SessionViewKind.InternalSession)))
                    return result(BoundedSessionHeaderStatus.Invalid, bytes);
                if (!TryDeserializeRawEvent(line, out var rawEvent) || rawEvent.BackendEventType != SessionHeaderEventType)
                    return result(BoundedSessionHeaderStatus.Invalid, bytes);
                var header = rawEvent.Raw.Deserialize(SessionViewJournalJsonSerializerContext.Default.SessionViewJournalHeader);
                cancellationToken.ThrowIfCancellationRequested();
                return header is null || header.SessionId != sessionId || header.CreatedAt != createdAt
                    ? result(BoundedSessionHeaderStatus.Invalid, bytes)
                    : result(BoundedSessionHeaderStatus.Found, bytes, header);
            }
            return result(BoundedSessionHeaderStatus.Incomplete, bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return result(BoundedSessionHeaderStatus.ReadError, bytes); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException
            or NotSupportedException or DecoderFallbackException)
        { return result(BoundedSessionHeaderStatus.Invalid, bytes); }
    }

    private static bool DuplicateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) return true;
        return false;
    }
}
