using System.Buffers;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

// Reads the usage records of an already opened journal in one pass. No filesystem acquisition or locking here: a
// record is known by the start of its line (see AgentJournalRecordText), and a usage record alone is kept whole.
internal static class AgentJournalUsageReader
{
    private const int ReadBytes = 128 * 1024;

    /// <summary>Reads the usage of the requests to the model that the journal records in <c>[0, length)</c>.</summary>
    /// <param name="stream">The opened journal.</param>
    /// <param name="length">The journal length to read up to.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>
    /// The usage records that report an operation, in journal order. A final record without a line end is one that is
    /// being written: it is not listed.
    /// </returns>
    /// <remarks>
    /// The scan costs the reading of the file and holds one read buffer, whatever the size of the other records: of
    /// them only the first <see cref="AgentJournalRecordText.HeadBytes"/> bytes are looked at. A usage record is small;
    /// one that cannot be parsed or exceeds <see cref="AgentJournalHistoryReader.TimelineRecordBytes"/> fails the scan,
    /// because what it reports is then unknown and a list without it would be taken for the whole usage.
    /// </remarks>
    /// <exception cref="AgentSessionHistoryException">
    /// The journal is not UTF-8 (<c>unsupported_format</c>), or a usage record is too large (<c>record_too_large</c>)
    /// or is not one the serializer wrote (<c>corrupt_record</c>).
    /// </exception>
    /// <exception cref="OperationCanceledException">The scan is canceled.</exception>
    internal static Task<List<AgentSessionUsageUpdate>> ReadAsync(Stream stream, long length, CancellationToken cancellationToken)
        => ReadAsync(stream, length, null, cancellationToken);

    /// <summary>Reads the usage that the journal records in <c>[0, length)</c> from a time on.</summary>
    /// <param name="stream">The opened journal.</param>
    /// <param name="length">The journal length to read up to.</param>
    /// <param name="countedFrom">
    /// The time from which the records of the journal are those of its session; null when they all are. The older
    /// ones are those a fork or a rewind copied from its source: they are parsed like the others and not listed.
    /// </param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The usage records of the session that report an operation, in journal order.</returns>
    /// <exception cref="AgentSessionHistoryException">As the scan of the whole journal.</exception>
    /// <exception cref="OperationCanceledException">The scan is canceled.</exception>
    internal static async Task<List<AgentSessionUsageUpdate>> ReadAsync(Stream stream, long length, DateTimeOffset? countedFrom, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var end = Math.Min(length, stream.Length);
        var scan = new Scan(countedFrom);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBytes);
        try
        {
            stream.Position = 0;
            var position = 0L;
            while (position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // The journal became shorter after its length was captured; what was read is all there is.
                    break;
                }

                // A byte order mark of UTF-16 or UTF-32: only the tolerant text reader decodes such a journal. The
                // mark of UTF-8 starts the first line, which is the header and never a usage record.
                if (position == 0 && ((read >= 2 && ((buffer[0] == 0xfe && buffer[1] == 0xff) || (buffer[0] == 0xff && buffer[1] == 0xfe)))
                    || (read >= 4 && buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0xfe && buffer[3] == 0xff)))
                {
                    throw new AgentSessionHistoryException("unsupported_format");
                }

                scan.Consume(buffer.AsSpan(0, read));
                position += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return scan.Found;
    }

    private enum LineState
    {
        // No byte of the line was read yet.
        Start,

        // The start of the line is being collected: its kind is not known yet.
        Undecided,

        // A usage record: all of it is collected.
        Usage,

        // Another record: nothing of it is kept.
        Skipped,
    }

    private sealed class Scan(DateTimeOffset? countedFrom)
    {
        // The line that a read cuts: its start until its kind is known, then all of it for a usage record alone.
        private byte[] _line = new byte[AgentJournalRecordText.HeadBytes];
        private int _lineLength;
        private LineState _state;

        internal List<AgentSessionUsageUpdate> Found { get; } = [];

        internal void Consume(ReadOnlySpan<byte> buffer)
        {
            var index = 0;
            while (index < buffer.Length)
            {
                var newline = buffer[index..].IndexOf((byte)'\n');
                var part = newline < 0 ? buffer[index..] : buffer.Slice(index, newline);
                if (_state == LineState.Start && newline >= 0)
                {
                    // The usual case: the whole line is in the buffer, and nothing is copied.
                    if (AgentJournalRecordText.Classify(part[..Math.Min(part.Length, AgentJournalRecordText.HeadBytes)]) == AgentJournalRecordKind.UsageUpdated)
                    {
                        Record(part);
                    }
                }
                else if (_state != LineState.Skipped)
                {
                    Append(part, complete: newline >= 0);
                }

                if (newline < 0)
                {
                    return;
                }

                _state = LineState.Start;
                _lineLength = 0;
                index += newline + 1;
            }
        }

        private void Append(ReadOnlySpan<byte> part, bool complete)
        {
            if (_state != LineState.Usage)
            {
                var head = Math.Min(AgentJournalRecordText.HeadBytes - _lineLength, part.Length);
                part[..head].CopyTo(_line.AsSpan(_lineLength));
                _lineLength += head;
                part = part[head..];
                if (!complete && _lineLength < AgentJournalRecordText.HeadBytes)
                {
                    _state = LineState.Undecided;
                    return;
                }

                if (AgentJournalRecordText.Classify(_line.AsSpan(0, _lineLength)) != AgentJournalRecordKind.UsageUpdated)
                {
                    _state = LineState.Skipped;
                    return;
                }

                _state = LineState.Usage;
            }

            if (part.Length > AgentJournalHistoryReader.TimelineRecordBytes - _lineLength)
            {
                throw new AgentSessionHistoryException("record_too_large");
            }

            if (_line.Length - _lineLength < part.Length)
            {
                Array.Resize(ref _line, Math.Max(_lineLength + part.Length, Math.Min(_line.Length * 2, AgentJournalHistoryReader.TimelineRecordBytes)));
            }

            part.CopyTo(_line.AsSpan(_lineLength));
            _lineLength += part.Length;
            if (complete)
            {
                Record(_line.AsSpan(0, _lineLength));
            }
        }

        private void Record(ReadOnlySpan<byte> line)
        {
            // Journals written on Windows end their lines with CR LF.
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            AgentEvent? value;
            try
            {
                value = JsonSerializer.Deserialize(line, AgentJsonSerializerContext.Default.AgentEvent);
            }
            catch (Exception error) when (error is JsonException or NotSupportedException)
            {
                throw new AgentSessionHistoryException("corrupt_record");
            }

            if (value is not AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.UsageUpdated } update)
            {
                throw new AgentSessionHistoryException("corrupt_record");
            }

            // A usage record without an operation only reports the context window or the rate limits.
            if (update.Usage?.LastOperation is { } operation && !(update.Timestamp < countedFrom))
            {
                Found.Add(new(update.RunId?.Value, operation));
            }
        }
    }
}
