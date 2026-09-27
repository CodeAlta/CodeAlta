using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

internal static partial class AgentJournalHistoryReader
{
    internal const int TimelineRecordBytes = 8 * 1024 * 1024;
    internal const int SourceChunkBytes = 16 * 1024;

    internal static async Task<AgentSessionHistoryPage> ReadTimelineAsync(Stream stream, string sessionId,
        AgentSessionHistoryCursor? cursor, Func<Stamp> getStamp, CancellationToken token)
    {
        ValidateCursor(sessionId, cursor);
        var stamp = getStamp();
        AgentSessionHistoryPage page;
        try { page = await ReadTailAsync(stream, sessionId, cursor, getStamp, token).ConfigureAwait(false); }
        catch (AgentSessionHistoryException error) when (error.Code == "record_too_large")
        {
            // The legacy reader has validated encoding, revision and exclusive cursor boundary.
            // Scan only the last physical record. Earlier records remain for explicit paging.
            CheckStamp(stream, stamp, getStamp);
            var end = cursor?.Offset ?? stamp.Length;
            var prefix = new byte[4];
            stream.Position = 0;
            await FillAsync(stream, prefix.AsMemory(0, (int)Math.Min(4, stamp.Length)), token).ConfigureAwait(false);
            var bom = prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf ? 3 : 0;
            stream.Position = end - 1;
            await FillAsync(stream, prefix.AsMemory(0, 1), token).ConfigureAwait(false);
            var scan = prefix[0] == (byte)'\n' ? end - 1 : end;
            var start = (long)bom;
            var block = new byte[64 * 1024];
            while (scan > bom)
            {
                token.ThrowIfCancellationRequested();
                var from = Math.Max(bom, scan - block.Length);
                // Include the preceding newline probe, but never scan an unbounded record.
                from = Math.Max(from, end - TimelineRecordBytes - 1);
                if (from >= scan) throw Failure("record_too_large");
                stream.Position = from;
                var count = (int)(scan - from);
                if (await FillAsync(stream, block.AsMemory(0, count), token).ConfigureAwait(false) != count)
                    throw Failure("history_changed");
                var newline = block.AsSpan(0, count).LastIndexOf((byte)'\n');
                if (newline >= 0) { start = from + newline + 1; break; }
                scan = from;
            }
            if (end - start > TimelineRecordBytes) throw Failure("record_too_large");
            var bytes = new byte[(int)(end - start)];
            stream.Position = start;
            // Keep individual stream requests bounded even for the exceptional single-record page.
            for (var offset = 0; offset < bytes.Length; offset += block.Length)
            {
                var count = Math.Min(block.Length, bytes.Length - offset);
                if (await FillAsync(stream, bytes.AsMemory(offset, count), token).ConfigureAwait(false) != count)
                    throw Failure("history_changed");
            }
            var size = bytes.Length;
            if (size > 0 && bytes[size - 1] == (byte)'\n')
            {
                size--;
                if (size > 0 && bytes[size - 1] == (byte)'\r') size--;
            }
            if (bytes.AsSpan(0, size).IndexOfAny((byte)'\r', (byte)0) >= 0) throw Failure("unsupported_format");
            string text;
            try { text = StrictUtf8.GetString(bytes, 0, size); }
            catch (DecoderFallbackException) { throw Failure("unsupported_format"); }
            AgentEvent? value = null;
            var omitted = false;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { value = JsonSerializer.Deserialize(text, AgentJsonSerializerContext.Default.AgentEvent) ?? throw new JsonException(); }
                catch (Exception parseError) when (parseError is JsonException or NotSupportedException)
                {
                    if (end != stamp.Length) throw Failure("corrupt_record");
                    omitted = true;
                }
            }
            var entries = value is null or AgentRawEvent { BackendEventType: "local.sessionSummary" or "local.sessionState" or "codealta.sessionHeader" or "codealta.sessionState" }
                ? Array.Empty<AgentSessionHistoryEntry>() : [new AgentSessionHistoryEntry(start, value) { SourceEnd = end }];
            page = new(entries, start > bom ? new(sessionId, stamp.Length, stamp.LastWriteUtcTicks, start) : null, omitted);
        }
        token.ThrowIfCancellationRequested();
        CheckStamp(stream, stamp, getStamp);
        return page with { Revision = new(sessionId, stamp.Length, stamp.LastWriteUtcTicks) };
    }

    internal static async Task<AgentHistorySourceChunk> ReadSourceAsync(Stream stream, AgentHistoryRevision revision,
        long start, long end, long offset, Func<Stamp> getStamp, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.SessionId);
        if (start < 0 || end <= start || end > revision.Length || end - start > TimelineRecordBytes
            || offset < start || offset >= end || revision.LastWriteUtcTicks < 0 || revision.LastWriteUtcTicks > DateTime.MaxValue.Ticks)
            throw Failure("invalid_cursor");
        var stamp = new Stamp(revision.Length, revision.LastWriteUtcTicks);
        CheckStamp(stream, stamp, getStamp);
        var buffer = new byte[SourceChunkBytes + 4];
        // Ranges are selected journal bytes, never paths or authority to mutate a record.
        // Require physical boundaries (with the UTF-8 BOM exception) before opening a range.
        if (start != 0)
        {
            stream.Position = start - 1;
            await FillAsync(stream, buffer.AsMemory(0, 1), token).ConfigureAwait(false);
            if (buffer[0] != (byte)'\n')
            {
                stream.Position = 0;
                await FillAsync(stream, buffer.AsMemory(0, 3), token).ConfigureAwait(false);
                if (start != 3 || buffer[0] != 0xef || buffer[1] != 0xbb || buffer[2] != 0xbf) throw Failure("invalid_cursor");
            }
        }
        if (end != stamp.Length)
        {
            stream.Position = end - 1;
            await FillAsync(stream, buffer.AsMemory(0, 1), token).ConfigureAwait(false);
            if (buffer[0] != (byte)'\n') throw Failure("invalid_cursor");
        }
        stream.Position = offset;
        var count = (int)Math.Min(buffer.Length, end - offset);
        if (await FillAsync(stream, buffer.AsMemory(0, count), token).ConfigureAwait(false) != count) throw Failure("history_changed");
        var used = Math.Min(SourceChunkBytes, count);
        // Include only complete UTF-8 scalars; the next request starts at the first unconsumed byte.
        if (used < count) while (used > 0 && (buffer[used] & 0xc0) == 0x80) used--;
        string text;
        try { text = StrictUtf8.GetString(buffer, 0, used); }
        catch (DecoderFallbackException) { throw Failure("unsupported_format"); }
        token.ThrowIfCancellationRequested();
        CheckStamp(stream, stamp, getStamp);
        return new(text, offset + used < end ? offset + used : null);
    }

    private static void CheckStamp(Stream stream, Stamp expected, Func<Stamp> getStamp)
    {
        if (stream.Length != expected.Length || getStamp() != expected) throw Failure("history_changed");
    }
}
