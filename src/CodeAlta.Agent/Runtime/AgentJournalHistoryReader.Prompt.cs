using System.Buffers;

namespace CodeAlta.Agent.Runtime;

internal static partial class AgentJournalHistoryReader
{
    private const int PromptScanBytes = 128 * 1024;

    /// <summary>Reads the system prompt record that starts at an offset, with the one it replaced.</summary>
    /// <param name="stream">The opened journal.</param>
    /// <param name="offset">Starting byte offset of the record, as a page entry reported it.</param>
    /// <param name="token">Cancels the read and the scan.</param>
    /// <returns>The record and, unless it is the first prompt of the session, the prompt record before it.</returns>
    /// <remarks>
    /// The record before is found by one forward pass over <c>[0, offset)</c> that looks at the start of each line
    /// and holds one read buffer: the other records are never loaded, whatever their size. A previous record that
    /// cannot be read leaves the prompt without one, rather than failing the read of the prompt itself.
    /// </remarks>
    /// <exception cref="AgentSessionHistoryException">
    /// The offset does not start a system prompt record (<c>invalid_cursor</c>), or the record is too large
    /// (<c>record_too_large</c>) or not readable (<c>unsupported_format</c>, <c>corrupt_record</c>).
    /// </exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    internal static async Task<AgentSystemPromptRecord> ReadSystemPromptAsync(Stream stream, long offset, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        token.ThrowIfCancellationRequested();
        if (offset < 0 || offset >= stream.Length) throw Failure("invalid_cursor");
        // The start of the line says what the record is: another record is refused before any of it is loaded.
        var head = new byte[AgentJournalRecordText.HeadBytes];
        stream.Position = offset;
        var count = await FillAsync(stream, head, token).ConfigureAwait(false);
        if (AgentJournalRecordText.Classify(head.AsSpan(0, count)) != AgentJournalRecordKind.SystemPrompt) throw Failure("invalid_cursor");
        if (await ReadRecordAsync(stream, offset, token).ConfigureAwait(false) is not AgentSystemPromptEvent prompt) throw Failure("invalid_cursor");
        // The first prompt of a session replaced none, whatever a fork or a rewind copied before it.
        if (string.Equals(prompt.Change.Kind, "initial", StringComparison.OrdinalIgnoreCase)) return new(prompt, null, null);
        var previousOffset = await FindPreviousSystemPromptAsync(stream, offset, token).ConfigureAwait(false);
        if (previousOffset < 0) return new(prompt, null, null);
        try
        {
            return await ReadRecordAsync(stream, previousOffset, token).ConfigureAwait(false) is AgentSystemPromptEvent previous
                ? new(prompt, previous, previousOffset) : new(prompt, null, null);
        }
        catch (AgentSessionHistoryException) { return new(prompt, null, null); }
    }

    /// <summary>Finds the last system prompt record that starts before an offset.</summary>
    /// <param name="stream">The opened journal.</param>
    /// <param name="offset">The exclusive end of the scan, after the line end of a record.</param>
    /// <param name="token">Cancels the scan.</param>
    /// <returns>The starting offset of that record, or -1 when the journal has none before <paramref name="offset"/>.</returns>
    /// <exception cref="OperationCanceledException">The scan is canceled.</exception>
    internal static async Task<long> FindPreviousSystemPromptAsync(Stream stream, long offset, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        token.ThrowIfCancellationRequested();
        var end = Math.Min(offset, stream.Length);
        var found = -1L;
        // The start of the line being read: the rest of it is passed over.
        var head = new byte[AgentJournalRecordText.HeadBytes];
        var headLength = 0;
        var lineOffset = 0L;
        var buffer = ArrayPool<byte>.Shared.Rent(PromptScanBytes);
        try
        {
            stream.Position = 0;
            var position = 0L;
            while (position < end)
            {
                token.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position)), token).ConfigureAwait(false);
                // The journal became shorter after its length was captured; what was read is all there is.
                if (read == 0) break;
                var index = 0;
                while (index < read)
                {
                    var newline = buffer.AsSpan(index, read - index).IndexOf((byte)'\n');
                    var part = buffer.AsSpan(index, newline < 0 ? read - index : newline);
                    var room = Math.Min(head.Length - headLength, part.Length);
                    part[..room].CopyTo(head.AsSpan(headLength));
                    headLength += room;
                    if (newline < 0) break;
                    if (AgentJournalRecordText.Classify(head.AsSpan(0, headLength)) == AgentJournalRecordKind.SystemPrompt) found = lineOffset;
                    index += newline + 1;
                    lineOffset = position + index;
                    headLength = 0;
                }

                position += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return found;
    }
}
