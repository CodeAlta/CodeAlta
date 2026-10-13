using System.Buffers;

namespace CodeAlta.Agent.Runtime;

// Finds the completed messages of one kind in a range of an already opened journal. No filesystem acquisition,
// locking or parsing here: a record is known by the two ends of its line (see AgentJournalRecordText).
internal static class AgentJournalContentReader
{
    private const int ReadBytes = 128 * 1024;

    /// <summary>Lists the offsets of the completed messages of a kind that a run wrote in <c>[start, end)</c>.</summary>
    /// <param name="stream">The opened journal.</param>
    /// <param name="start">The offset of the first record to look at.</param>
    /// <param name="end">The exclusive end of the range, after the line end of a record.</param>
    /// <param name="kind"><see cref="AgentJournalRecordKind.UserContent"/> or <see cref="AgentJournalRecordKind.AssistantContent"/>.</param>
    /// <param name="runId">The run the records name, as the journal writes it; empty for the records that name none.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The offsets in journal order. A record without a line end is one that is being written: it is not listed.</returns>
    /// <exception cref="OperationCanceledException">The scan is canceled.</exception>
    internal static async Task<List<long>> FindAsync(Stream stream, long start, long end, AgentJournalRecordKind kind,
        ReadOnlyMemory<byte> runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var scan = new Scan(kind, runId);
        end = Math.Min(end, stream.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBytes);
        try
        {
            stream.Position = start;
            var position = start;
            while (position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                scan.Consume(buffer.AsSpan(0, read), position);
                position += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return scan.Found;
    }

    private sealed class Scan(AgentJournalRecordKind kind, ReadOnlyMemory<byte> runId)
    {
        // The start and the end of the line being read: what lies between them is not kept.
        private readonly byte[] _head = new byte[AgentJournalRecordText.HeadBytes];
        private readonly byte[] _tail = new byte[AgentJournalRecordText.TailBytes];
        private int _headLength;
        private int _tailLength;
        private long _lineOffset;
        private bool _inLine;

        internal List<long> Found { get; } = [];

        internal void Consume(ReadOnlySpan<byte> buffer, long position)
        {
            var index = 0;
            while (index < buffer.Length)
            {
                if (!_inLine)
                {
                    _inLine = true;
                    _lineOffset = position + index;
                    _headLength = 0;
                    _tailLength = 0;
                }

                var newline = buffer[index..].IndexOf((byte)'\n');
                if (newline < 0)
                {
                    Append(buffer[index..]);
                    return;
                }

                Append(buffer.Slice(index, newline));
                _inLine = false;
                Record();
                index += newline + 1;
            }
        }

        private void Append(ReadOnlySpan<byte> part)
        {
            var room = Math.Min(_head.Length - _headLength, part.Length);
            part[..room].CopyTo(_head.AsSpan(_headLength));
            _headLength += room;
            if (part.Length >= _tail.Length)
            {
                part[^_tail.Length..].CopyTo(_tail);
                _tailLength = _tail.Length;
                return;
            }

            var keep = Math.Min(_tailLength, _tail.Length - part.Length);
            _tail.AsSpan(_tailLength - keep, keep).CopyTo(_tail);
            part.CopyTo(_tail.AsSpan(keep));
            _tailLength = keep + part.Length;
        }

        private void Record()
        {
            var tail = _tail.AsSpan(0, _tailLength);
            // Journals written on Windows end their lines with CR LF.
            if (tail.Length > 0 && tail[^1] == (byte)'\r')
            {
                tail = tail[..^1];
            }

            if (tail.IsEmpty || AgentJournalRecordText.Classify(_head.AsSpan(0, _headLength)) != kind
                || !AgentJournalRecordText.TryReadEnvelope(tail, out _, out var run) || !run.SequenceEqual(runId.Span))
            {
                return;
            }

            Found.Add(_lineOffset);
        }
    }
}
