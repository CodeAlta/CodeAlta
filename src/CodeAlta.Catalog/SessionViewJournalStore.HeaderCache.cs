using System.Collections.Concurrent;
using System.Text.Json;

namespace CodeAlta.Catalog;

public sealed partial class SessionViewJournalStore
{
    // More journals than the lists of a host read the headers of: beyond it the remembered headers start again from none.
    private const int MaximumCachedHeaders = 4096;
    private readonly ConcurrentDictionary<string, CachedHeader> _headerCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the first-line session header of a journal, as <see cref="ReadHeaderAsync"/> does, without reading the
    /// journal again while it keeps the same length, last write and creation times.
    /// </summary>
    /// <remarks>
    /// The header is the first line of the journal: appending records does not change it, but every append changes the
    /// length, and every replacement of the journal (a move, a change of provider) its times or length, so the journal
    /// is read again then. A header is remembered only when the journal had the same stamp before and after it was
    /// read. Like the other stamp-validated caches of journals, a rewrite that keeps the length and both times is not
    /// seen. Each call returns a header of its own.
    /// </remarks>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="createdAt">Session creation timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The header, or <see langword="null" /> when the journal has no header.</returns>
    /// <exception cref="ArgumentException">The session identifier is blank.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="JsonException">The header cannot be deserialized.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<SessionViewJournalHeader?> ReadHeaderCachedAsync(string sessionId, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (createdAt == default)
        {
            return null;
        }

        var path = GetPath(sessionId, createdAt);
        var key = Path.GetFullPath(path);
        var before = GetHeaderStamp(path);
        if (before is not null && _headerCache.TryGetValue(key, out var cached) && cached.Stamp == before)
        {
            return cached.Raw?.Deserialize(SessionViewJournalJsonSerializerContext.Default.SessionViewJournalHeader);
        }

        var raw = await ReadHeaderRawFromPathAsync(path, cancellationToken).ConfigureAwait(false);
        var header = raw?.Deserialize(SessionViewJournalJsonSerializerContext.Default.SessionViewJournalHeader);
        if (before is not null && GetHeaderStamp(path) == before)
        {
            if (_headerCache.Count >= MaximumCachedHeaders)
            {
                _headerCache.Clear();
            }

            _headerCache[key] = new CachedHeader(before.Value, raw);
        }
        else
        {
            _headerCache.TryRemove(key, out _);
        }

        return header;
    }

    private static HeaderStamp? GetHeaderStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new HeaderStamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : null;
    }

    private readonly record struct HeaderStamp(long Length, long LastWriteUtcTicks, long CreationUtcTicks);

    private sealed record CachedHeader(HeaderStamp Stamp, JsonElement? Raw);
}
