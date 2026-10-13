using System.Buffers;
using System.Text;

namespace CodeAlta.Agent.Runtime;

// Reads the turns of an already opened journal. No filesystem acquisition, locking or caching here.
internal static class AgentJournalTurnReader
{
    private const int ReadBytes = 128 * 1024;

    // What is kept of the start of a record: enough for the text of a prompt, whose every character can take six bytes.
    private const int HeadCapacity = 32 * 1024;

    private static ReadOnlySpan<byte> ContentIdKey => ",\"contentId\":\""u8;

    private static ReadOnlySpan<byte> ContentKey => ",\"content\":\""u8;

    private static ReadOnlySpan<byte> SourceSessionKey => ",\"source_session_id\":\""u8;

    private static ReadOnlySpan<byte> AskKey => ",\"ask_id\":\""u8;

    /// <summary>Where a metadata snapshot is in the journal; <see cref="Length"/> is 0 when there is none.</summary>
    internal readonly record struct Snapshot(long Offset, int Length)
    {
        internal bool Exists => Length > 0;
    }

    /// <summary>A point the journal can be cut at, with the snapshots in force there: the last ones before it.</summary>
    /// <param name="Offset">The byte offset of the point, at the start of a record.</param>
    /// <param name="Summary">The last summary snapshot before the point.</param>
    /// <param name="State">The last state snapshot before the point.</param>
    /// <param name="ViewState">The last view-state snapshot before the point.</param>
    internal readonly record struct Boundary(long Offset, Snapshot Summary, Snapshot State, Snapshot ViewState);

    /// <summary>The outcome of a scan.</summary>
    /// <param name="Supported">False for a journal that is not UTF-8, which only the tolerant text reader decodes.</param>
    /// <param name="Turns">The turns in journal order.</param>
    /// <param name="Boundaries">One boundary per turn, at its start, then the boundary after the last complete record.</param>
    internal sealed record Result(bool Supported, IReadOnlyList<AgentSessionTurn> Turns, IReadOnlyList<Boundary> Boundaries)
    {
        /// <summary>Gets the boundary after the last complete record.</summary>
        internal Boundary End => Boundaries[^1];
    }

    /// <summary>Reads the turns in <c>[0, length)</c>.</summary>
    /// <param name="stream">The opened journal.</param>
    /// <param name="length">The journal length to read up to.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <remarks>
    /// A record is read by its two ends only (see <see cref="AgentJournalRecordText"/>), so the scan costs the reading
    /// of the file and holds no more than one read buffer, whatever the size of a record. A final record without a
    /// line end is one that is being written: it belongs to no turn and lies after the last boundary.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The scan is canceled.</exception>
    internal static async Task<Result> ScanAsync(Stream stream, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var end = Math.Min(length, stream.Length);
        var start = 0L;
        var bom = new byte[4];
        stream.Position = 0;
        var count = await FillAsync(stream, bom.AsMemory(0, (int)Math.Min(bom.Length, end)), cancellationToken).ConfigureAwait(false);
        if ((count >= 2 && ((bom[0] == 0xfe && bom[1] == 0xff) || (bom[0] == 0xff && bom[1] == 0xfe))) ||
            (count == 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xfe && bom[3] == 0xff))
        {
            return new(false, [], [default]);
        }

        if (count >= 3 && bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf)
        {
            start = 3;
        }

        var state = new ScanState(start);
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
                    // The journal became shorter after its length was captured; what was read is all there is.
                    break;
                }

                state.Consume(buffer.AsSpan(0, read), position);
                position += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return state.Complete();
    }

    private static async Task<int> FillAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[count..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }

        return count;
    }

    private sealed class ScanState(long start)
    {
        private readonly List<Turn> _turns = [];
        private readonly List<Boundary> _boundaries = [];
        private readonly Dictionary<string, Turn> _byRunId = new(StringComparer.Ordinal);

        // A record that the read buffer cuts: its start and its end are kept, not what lies between them.
        private readonly byte[] _head = new byte[HeadCapacity];
        private readonly byte[] _tail = new byte[AgentJournalRecordText.TailBytes];
        private int _headLength;
        private int _tailLength;
        private long _lineOffset;
        private long _lineLength;
        private bool _inLine;

        private Snapshot _summary;
        private Snapshot _state;
        private Snapshot _viewState;
        private Turn? _current;
        private byte[] _currentRunId = [];
        private long _endOffset = start;

        internal void Consume(ReadOnlySpan<byte> buffer, long position)
        {
            var index = 0;
            while (index < buffer.Length)
            {
                var newline = buffer[index..].IndexOf((byte)'\n');
                if (newline < 0)
                {
                    if (!_inLine)
                    {
                        _inLine = true;
                        _lineOffset = position + index;
                        _lineLength = 0;
                        _headLength = 0;
                        _tailLength = 0;
                    }

                    Append(buffer[index..]);
                    return;
                }

                var part = buffer.Slice(index, newline);
                var next = position + index + newline + 1;
                if (_inLine)
                {
                    Append(part);
                    _inLine = false;
                    Record(_lineOffset, _lineLength, _head.AsSpan(0, _headLength), _tail.AsSpan(0, _tailLength), next);
                }
                else
                {
                    Record(position + index, part.Length, part[..Math.Min(part.Length, HeadCapacity)],
                        part[^Math.Min(part.Length, AgentJournalRecordText.TailBytes)..], next);
                }

                index += newline + 1;
            }
        }

        internal Result Complete()
        {
            _boundaries.Add(new(_endOffset, _summary, _state, _viewState));
            var turns = new AgentSessionTurn[_turns.Count];
            for (var index = 0; index < turns.Length; index++)
            {
                turns[index] = _turns[index].ToTurn(index + 1);
            }

            return new(true, turns, _boundaries.ToArray());
        }

        private void Append(ReadOnlySpan<byte> part)
        {
            var room = Math.Min(_head.Length - _headLength, part.Length);
            part[..room].CopyTo(_head.AsSpan(_headLength));
            _headLength += room;
            _lineLength += part.Length;
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

        private void Record(long offset, long length, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, long next)
        {
            // Journals written on Windows end their lines with CR LF.
            if (tail.Length > 0 && tail[^1] == (byte)'\r')
            {
                tail = tail[..^1];
                if (head.Length == length)
                {
                    head = head[..^1];
                }
            }

            _endOffset = next;
            if (tail.IsEmpty)
            {
                return;
            }

            var kind = AgentJournalRecordText.Classify(head[..Math.Min(head.Length, AgentJournalRecordText.HeadBytes)]);
            switch (kind)
            {
                case AgentJournalRecordKind.Header:
                    return;
                case AgentJournalRecordKind.Summary:
                    _summary = AsSnapshot(offset, next);
                    return;
                case AgentJournalRecordKind.State:
                    _state = AsSnapshot(offset, next);
                    return;
                case AgentJournalRecordKind.ViewState:
                    _viewState = AsSnapshot(offset, next);
                    return;
            }

            if (!AgentJournalRecordText.TryReadEnvelope(tail, out var timestamp, out var runId))
            {
                return;
            }

            // The compaction and the remote control of a provider run between turns, under a name of their own.
            if (runId.IsEmpty || (runId.StartsWith("provider-"u8) && runId.IndexOf((byte)':') > 0))
            {
                if (kind == AgentJournalRecordKind.CompactionCompleted && _turns.Count > 0)
                {
                    _turns[^1].Compactions++;
                }

                return;
            }

            var turn = Resolve(runId, offset, timestamp);
            turn.EndOffset = next;
            switch (kind)
            {
                case AgentJournalRecordKind.UserContent when turn.PromptOffset is null:
                    turn.PromptOffset = offset;
                    ReadPrompt(turn, head, tail);
                    break;
                case AgentJournalRecordKind.UserContent:
                    turn.Steers++;
                    break;
                case AgentJournalRecordKind.AssistantContent:
                    turn.AssistantMessages++;
                    break;
                case AgentJournalRecordKind.ToolStarted:
                    turn.ToolCalls++;
                    break;
                case AgentJournalRecordKind.CompactionCompleted:
                    turn.Compactions++;
                    break;
                case AgentJournalRecordKind.Idle:
                    turn.Status = AgentSessionTurnStatus.Completed;
                    turn.EndedAt = timestamp;
                    break;
                case AgentJournalRecordKind.Error when turn.Status != AgentSessionTurnStatus.Completed:
                    turn.Status = AgentSessionTurnStatus.Failed;
                    turn.EndedAt = timestamp;
                    break;
            }
        }

        private Turn Resolve(ReadOnlySpan<byte> runId, long offset, DateTimeOffset timestamp)
        {
            if (_current is not null && runId.SequenceEqual(_currentRunId))
            {
                return _current;
            }

            var key = Encoding.UTF8.GetString(runId);
            if (!_byRunId.TryGetValue(key, out var turn))
            {
                // The first record of a run starts its turn: everything before it stays when the turn is cut.
                turn = new Turn(key, offset, timestamp);
                _byRunId.Add(key, turn);
                _turns.Add(turn);
                _boundaries.Add(new(offset, _summary, _state, _viewState));
            }

            _current = turn;
            _currentRunId = runId.ToArray();
            return turn;
        }

        private static void ReadPrompt(Turn turn, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail)
        {
            // The keys are searched in what precedes the text, where no value can hold an unescaped quote.
            var contentIndex = head.IndexOf(ContentKey);
            var before = contentIndex < 0 ? head : head[..contentIndex];
            var idIndex = before.IndexOf(ContentIdKey);
            if (idIndex >= 0)
            {
                var id = before[(idIndex + ContentIdKey.Length)..];
                var idLength = id.IndexOfAny((byte)'"', (byte)'\\');
                if (idLength > 0 && id[idLength] == (byte)'"')
                {
                    turn.PromptContentId = Encoding.UTF8.GetString(id[..idLength]);
                }
            }

            if (contentIndex >= 0)
            {
                turn.PromptPreview = AgentJournalRecordText.ReadStringStart(
                    head[(contentIndex + ContentKey.Length)..], AgentSessionTurn.PromptPreviewLength, out var complete);
                turn.PromptTruncated = !complete;
            }

            turn.SourceSessionId = AgentJournalRecordText.ReadTailString(tail, SourceSessionKey);
            turn.AskId = AgentJournalRecordText.ReadTailString(tail, AskKey);
        }

        private static Snapshot AsSnapshot(long offset, long next)
            => next - offset <= int.MaxValue ? new(offset, (int)(next - offset)) : default;
    }

    private sealed class Turn(string runId, long startOffset, DateTimeOffset startedAt)
    {
        // Set by the first record of the turn, which is the one that creates it.
        internal long EndOffset;
        internal DateTimeOffset? EndedAt;
        internal AgentSessionTurnStatus Status;
        internal long? PromptOffset;
        internal string? PromptContentId;
        internal string PromptPreview = string.Empty;
        internal bool PromptTruncated;
        internal string? SourceSessionId;
        internal string? AskId;
        internal int Steers;
        internal int AssistantMessages;
        internal int ToolCalls;
        internal int Compactions;

        internal AgentSessionTurn ToTurn(int number) => new()
        {
            Number = number,
            RunId = runId,
            StartOffset = startOffset,
            EndOffset = EndOffset,
            StartedAt = startedAt,
            EndedAt = EndedAt,
            Status = Status,
            PromptOffset = PromptOffset,
            PromptContentId = PromptContentId,
            PromptPreview = PromptPreview,
            PromptTruncated = PromptTruncated,
            SourceSessionId = SourceSessionId,
            AskId = AskId,
            SteerCount = Steers,
            AssistantMessageCount = AssistantMessages,
            ToolCallCount = ToolCalls,
            CompactionCount = Compactions,
        };
    }
}
