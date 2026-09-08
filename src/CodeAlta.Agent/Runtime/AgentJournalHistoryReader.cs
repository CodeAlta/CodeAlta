using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

// Mandatory production parser/containment seams. No filesystem acquisition or catalog discovery here.
internal static class AgentJournalHistoryReader
{
    internal const int PageBytes = 256 * 1024;
    internal const int RecordBytes = 128 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal readonly record struct Stamp(long Length, long LastWriteUtcTicks);

    internal static async Task<T> OpenContainedAsync<T>(string sessionsRoot, string path,
        Func<string, CancellationToken, Task<T>> open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sessionsRoot) || !Path.IsPathFullyQualified(path)) throw Failure("outside_root");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsRoot)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root, comparison) || fullPath.Length <= root.Length) throw Failure("outside_root");
        return await open(fullPath, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateCursor(string sessionId, AgentSessionHistoryCursor? cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (cursor is not null && (!string.Equals(cursor.SessionId, sessionId, StringComparison.Ordinal) ||
            cursor.Length <= 0 || cursor.Offset <= 0 || cursor.Offset >= cursor.Length ||
            cursor.LastWriteUtcTicks < 0 || cursor.LastWriteUtcTicks > DateTime.MaxValue.Ticks))
            throw Failure("invalid_cursor");
    }

    internal static async Task<AgentSessionHistoryPage> ReadAsync(Stream stream, string sessionId,
        AgentSessionHistoryCursor? cursor, Func<Stamp> getStamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(getStamp);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCursor(sessionId, cursor);
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("History requires a readable seekable stream.", nameof(stream));
        var stamp = getStamp();
        if (stamp.Length < 0 || stamp.Length != stream.Length ||
            (cursor is not null && (cursor.Length != stamp.Length || cursor.LastWriteUtcTicks != stamp.LastWriteUtcTicks)))
            throw Failure("history_changed");

        // Four BOM probe bytes and one cursor-boundary byte are the only reads outside the page window.
        var prefix = new byte[4];
        stream.Position = 0;
        var prefixLength = await FillAsync(stream, prefix.AsMemory(0, (int)Math.Min(4, stamp.Length)), cancellationToken).ConfigureAwait(false);
        if ((prefixLength >= 2 && ((prefix[0] == 0xff && prefix[1] == 0xfe) || (prefix[0] == 0xfe && prefix[1] == 0xff))) ||
            (prefixLength >= 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff))
            throw Failure("unsupported_format");
        var bom = prefixLength >= 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf ? 3 : 0;
        var start = cursor?.Offset ?? bom;
        if (cursor is not null)
        {
            stream.Position = start - 1;
            if (await FillAsync(stream, prefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 1 || prefix[0] != (byte)'\n')
                throw Failure("invalid_cursor");
        }
        stream.Position = start;
        var buffer = new byte[(int)Math.Min(PageBytes, stamp.Length - start)];
        var count = await FillAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (count != buffer.Length) throw Failure("history_changed");
        var entries = new List<AgentSessionHistoryEntry>();
        var position = 0;
        var physicalRecords = 0;
        var tailOmitted = false;
        while (position < count && physicalRecords < 100)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var newline = buffer.AsSpan(position, count - position).IndexOf((byte)'\n');
            var terminated = newline >= 0;
            var length = terminated ? newline : count - position;
            if (length > RecordBytes) throw Failure("record_too_large");
            // A partial record at the page limit remains entirely unconsumed for the next page.
            if (!terminated && start + count < stamp.Length) break;
            var end = position + length + (terminated ? 1 : 0);
            var record = buffer.AsMemory(position, length);
            if (terminated && length > 0 && record.Span[^1] == (byte)'\r') record = record[..^1];
            if (record.Span.IndexOf((byte)'\r') >= 0 || record.Span.IndexOf((byte)0) >= 0) throw Failure("unsupported_format");
            string text;
            try { text = StrictUtf8.GetString(record.Span); }
            catch (DecoderFallbackException) { throw Failure("unsupported_format"); }
            if (!string.IsNullOrWhiteSpace(text))
            {
                AgentEvent value;
                try
                {
                    value = JsonSerializer.Deserialize(text, AgentJsonSerializerContext.Default.AgentEvent)
                        ?? throw new JsonException("Null journal event.");
                }
                catch (JsonException)
                {
                    if (start + end != stamp.Length) throw Failure("corrupt_record");
                    tailOmitted = true;
                    position = end;
                    break;
                }
                if (value is not AgentRawEvent { BackendEventType: "local.sessionSummary" or "local.sessionState" or "codealta.sessionHeader" or "codealta.sessionState" })
                    entries.Add(new(start + position, value));
            }
            physicalRecords++;
            position = end;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (getStamp() != stamp || stream.Length != stamp.Length) throw Failure("history_changed");
        var nextOffset = start + position;
        var next = nextOffset < stamp.Length ? new AgentSessionHistoryCursor(sessionId, stamp.Length, stamp.LastWriteUtcTicks, nextOffset) : null;
        return new(entries.ToArray(), next, tailOmitted);
    }

    private static async Task<int> FillAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer[count..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return count;
    }

    private static AgentSessionHistoryException Failure(string code) => new(code);
}
