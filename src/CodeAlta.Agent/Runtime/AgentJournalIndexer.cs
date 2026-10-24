using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

// Indexes the records of a journal as they are given, from its start or from where an earlier indexing ended: the turns
// and the snapshots in force at their start, the last notes record, the usage records and the number of events of the
// history. A record is read by its two ends only, but for a notes or usage record, which is kept whole. No filesystem
// access, locking or caching here.
internal sealed class AgentJournalIndexer
{
    // What is kept of the start of a record: enough for the text of a prompt, whose every character can take six bytes.
    internal const int HeadCapacity = 32 * 1024;
    internal const int GuardBytes = 256;

    private static ReadOnlySpan<byte> ContentIdKey => ",\"contentId\":\""u8;
    private static ReadOnlySpan<byte> ContentKey => ",\"content\":\""u8;
    private static ReadOnlySpan<byte> SourceSessionKey => ",\"source_session_id\":\""u8;
    private static ReadOnlySpan<byte> AskKey => ",\"ask_id\":\""u8;
    private static ReadOnlySpan<byte> ProvenanceKey => ",\"provenance\":\""u8;
    private static ReadOnlySpan<byte> ThreadRecord => "codealta.thread"u8;

    private readonly List<Turn> _turns = [];
    private readonly Dictionary<string, Turn> _byRunId = new(StringComparer.Ordinal);
    private readonly List<AgentSessionJournalIndexUsage> _usage = [];

    // A record that the read buffer cuts: its start and its end are kept, not what lies between them, but for a record
    // that is kept whole.
    private readonly byte[] _head = new byte[HeadCapacity];
    private readonly byte[] _tail = new byte[AgentJournalRecordText.TailBytes];
    private int _headLength;
    private int _tailLength;
    private long _lineOffset;
    private long _lineLength;
    private bool _inLine;
    private bool? _keepWhole;
    private ArrayBufferWriter<byte>? _whole;
    private bool _wholeTooLarge;

    // The last bytes that were given, ending at _windowEnd: the guard of the end offset is taken from them.
    private byte[] _window;

    private AgentJournalTurnReader.Snapshot _summary;
    private AgentJournalTurnReader.Snapshot _state;
    private AgentJournalTurnReader.Snapshot _viewState;
    private Turn? _current;
    private byte[] _currentRunId = [];
    private Turn? _last;
    private int _turnCount;
    private long _endOffset;
    private byte[] _guard;
    private long _historyCount;
    private string? _notesJson;
    private long? _notesOffset;
    private long? _malformedOffset;
    private string? _usageError;

    // An indexing of a journal from its start; start is past the byte order mark, which is given as the first window.
    internal AgentJournalIndexer(long start, byte[] bom)
    {
        _endOffset = start;
        _window = bom;
        _guard = bom;
    }

    // An indexing that goes on from a row, with the turns of it that the records to come may continue: the last one
    // and those of their runs. A run that is not among them starts a new turn.
    internal AgentJournalIndexer(AgentSessionJournalIndexRow row, IEnumerable<AgentSessionJournalIndexTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(turns);
        _endOffset = row.EndOffset;
        _window = row.Guard;
        _guard = row.Guard;
        _summary = new(row.Summary.Offset, row.Summary.Length);
        _state = new(row.State.Offset, row.State.Length);
        _viewState = new(row.ViewState.Offset, row.ViewState.Length);
        _turnCount = row.TurnCount;
        _historyCount = row.HistoryCount;
        _notesJson = row.NotesJson;
        _notesOffset = row.NotesOffset;
        _malformedOffset = row.MalformedOffset;
        _usageError = row.UsageError;
        foreach (var known in turns.OrderBy(static turn => turn.Turn.Number))
        {
            var turn = Turn.From(known);
            if (_byRunId.TryAdd(turn.RunId, turn))
            {
                _turns.Add(turn);
                if (turn.Number == _turnCount) _last = turn;
            }
        }
    }

    internal long EndOffset => _endOffset;

    internal AgentJournalTurnReader.Snapshot Summary => _summary;

    internal AgentJournalTurnReader.Snapshot State => _state;

    // Every turn this indexing knows, in journal order: all of them for an indexing from the start.
    internal IReadOnlyList<Turn> Turns => _turns;

    internal IReadOnlyList<AgentSessionJournalIndexUsage> Usage => _usage;

    // The runs of the records of complete lines, as the turns take them: what an indexing that goes on needs to know.
    internal static HashSet<string> CollectRunIds(ReadOnlySpan<byte> buffer)
    {
        var runs = new HashSet<string>(StringComparer.Ordinal);
        while (!buffer.IsEmpty)
        {
            var newline = buffer.IndexOf((byte)'\n');
            if (newline < 0) break;
            var line = buffer[..newline];
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            var tail = line[^Math.Min(line.Length, AgentJournalRecordText.TailBytes)..];
            if (AgentJournalRecordText.TryReadEnvelope(tail, out _, out var runId) && IsTurnRun(runId))
            {
                runs.Add(Encoding.UTF8.GetString(runId));
            }

            buffer = buffer[(newline + 1)..];
        }

        return runs;
    }

    internal void Consume(ReadOnlySpan<byte> buffer, long position)
    {
        var lastNewline = buffer.LastIndexOf((byte)'\n');
        if (lastNewline >= 0)
        {
            _guard = Window(_window, buffer[..(lastNewline + 1)]);
        }

        _window = Window(_window, buffer);
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
                    _keepWhole = null;
                    _whole = null;
                    _wholeTooLarge = false;
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
                ReadOnlySpan<byte> whole = _keepWhole switch
                {
                    true => _wholeTooLarge || _whole is null ? default : _whole.WrittenSpan,
                    _ => _lineLength <= _headLength ? _head.AsSpan(0, _headLength) : default,
                };
                Record(_lineOffset, _lineLength, _head.AsSpan(0, _headLength), _tail.AsSpan(0, _tailLength), next, whole,
                    _keepWhole == true && _wholeTooLarge);
                _whole = null;
            }
            else
            {
                Record(position + index, part.Length, part[..Math.Min(part.Length, HeadCapacity)],
                    part[^Math.Min(part.Length, AgentJournalRecordText.TailBytes)..], next, part, tooLarge: false);
            }

            index += newline + 1;
        }
    }

    // The row of what was indexed so far, for a journal of a length and a last-write time.
    internal AgentSessionJournalIndexRow ToRow(string sessionId, string journalPath, long length, long lastWriteUtcTicks)
        => new()
        {
            SessionId = sessionId,
            JournalPath = journalPath,
            Length = length,
            LastWriteUtcTicks = lastWriteUtcTicks,
            EndOffset = _endOffset,
            Guard = _guard,
            Summary = new(_summary.Offset, _summary.Length),
            State = new(_state.Offset, _state.Length),
            ViewState = new(_viewState.Offset, _viewState.Length),
            TurnCount = _turnCount,
            HistoryCount = _historyCount,
            LastRunId = _last?.RunId,
            NotesJson = _notesJson,
            NotesOffset = _notesOffset,
            MalformedOffset = _malformedOffset,
            UsageError = _usageError,
        };

    // The turns that the records given changed or added; every turn for an indexing from the start.
    internal List<AgentSessionJournalIndexTurn> ToIndexTurns(bool changedOnly)
    {
        var turns = new List<AgentSessionJournalIndexTurn>();
        foreach (var turn in _turns)
        {
            if (!changedOnly || turn.Changed)
            {
                turns.Add(turn.ToIndexTurn());
            }
        }

        return turns;
    }

    // The scan result of the turn reader: one boundary per turn, at its start, then the boundary after the last record.
    internal AgentJournalTurnReader.Result ToResult()
    {
        var turns = new AgentSessionTurn[_turns.Count];
        var boundaries = new AgentJournalTurnReader.Boundary[_turns.Count + 1];
        for (var index = 0; index < turns.Length; index++)
        {
            turns[index] = _turns[index].ToTurn();
            boundaries[index] = _turns[index].Boundary;
        }

        boundaries[^1] = new(_endOffset, _summary, _state, _viewState);
        return new(true, turns, boundaries) { HistoryCount = _historyCount };
    }

    private static byte[] Window(ReadOnlySpan<byte> before, ReadOnlySpan<byte> added)
    {
        if (added.Length >= GuardBytes)
        {
            return added[^GuardBytes..].ToArray();
        }

        var keep = Math.Min(before.Length, GuardBytes - added.Length);
        var window = new byte[keep + added.Length];
        before[^keep..].CopyTo(window);
        added.CopyTo(window.AsSpan(keep));
        return window;
    }

    private static bool IsTurnRun(ReadOnlySpan<byte> runId)
        // The compaction and the remote control of a provider run between turns, under a name of their own.
        => !runId.IsEmpty && !(runId.StartsWith("provider-"u8) && runId.IndexOf((byte)':') > 0);

    private void Append(ReadOnlySpan<byte> part)
    {
        var headBefore = _headLength;
        var room = Math.Min(_head.Length - _headLength, part.Length);
        part[..room].CopyTo(_head.AsSpan(_headLength));
        _headLength += room;
        _lineLength += part.Length;
        if (part.Length >= _tail.Length)
        {
            part[^_tail.Length..].CopyTo(_tail);
            _tailLength = _tail.Length;
        }
        else
        {
            var keep = Math.Min(_tailLength, _tail.Length - part.Length);
            _tail.AsSpan(_tailLength - keep, keep).CopyTo(_tail);
            part.CopyTo(_tail.AsSpan(keep));
            _tailLength = keep + part.Length;
        }

        if (_keepWhole is null && _headLength >= AgentJournalRecordText.HeadBytes)
        {
            // Only a notes or a usage record is kept whole, from the start of its line.
            _keepWhole = AgentJournalRecordText.Classify(_head.AsSpan(0, AgentJournalRecordText.HeadBytes))
                is AgentJournalRecordKind.Notes or AgentJournalRecordKind.UsageUpdated;
            if (_keepWhole == true)
            {
                _whole = new ArrayBufferWriter<byte>(Math.Max(256, headBefore + part.Length));
                KeepWhole(_head.AsSpan(0, headBefore));
                KeepWhole(part);
            }
        }
        else if (_keepWhole == true)
        {
            KeepWhole(part);
        }
    }

    private void KeepWhole(ReadOnlySpan<byte> part)
    {
        if (_wholeTooLarge || _whole is null)
        {
            return;
        }

        if (_whole.WrittenCount + (long)part.Length > AgentJournalHistoryReader.TimelineRecordBytes)
        {
            _wholeTooLarge = true;
            _whole = null;
            return;
        }

        _whole.Write(part);
    }

    private void Record(long offset, long length, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, long next,
        ReadOnlySpan<byte> whole, bool tooLarge)
    {
        // Journals written on Windows end their lines with CR LF.
        if (tail.Length > 0 && tail[^1] == (byte)'\r')
        {
            tail = tail[..^1];
            if (head.Length == length)
            {
                head = head[..^1];
            }

            if (whole.Length == length)
            {
                whole = whole[..^1];
            }
        }

        _endOffset = next;
        // A blank line is no record, as the readers of the history skip it.
        if (tail.IsEmpty || (length <= AgentJournalRecordText.TailBytes && tail.IndexOfAnyExcept(" \t\r"u8) < 0))
        {
            return;
        }

        var kind = AgentJournalRecordText.Classify(head[..Math.Min(head.Length, AgentJournalRecordText.HeadBytes)]);
        var hasEnvelope = AgentJournalRecordText.TryReadEnvelope(tail, out var timestamp, out var runId);
        if (kind == AgentJournalRecordKind.Unknown || tail[^1] != (byte)'}' || !hasEnvelope)
        {
            _malformedOffset ??= offset;
        }

        // The four metadata snapshots are not events of the history; those of journals written before sessions were
        // called sessions are.
        var snapshot = kind is AgentJournalRecordKind.Summary or AgentJournalRecordKind.State
            || (kind is AgentJournalRecordKind.Header or AgentJournalRecordKind.ViewState
                && head[..Math.Min(head.Length, AgentJournalRecordText.HeadBytes)].IndexOf(ThreadRecord) < 0);
        if (!snapshot)
        {
            _historyCount++;
        }

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
            case AgentJournalRecordKind.Notes when hasEnvelope && !whole.IsEmpty:
                _notesJson = Encoding.UTF8.GetString(whole);
                _notesOffset = offset;
                break;
            case AgentJournalRecordKind.UsageUpdated:
                ReadUsage(offset, whole, tooLarge);
                break;
        }

        if (!hasEnvelope)
        {
            return;
        }

        if (!IsTurnRun(runId))
        {
            if (kind == AgentJournalRecordKind.CompactionCompleted && _last is not null)
            {
                _last.Compactions++;
                _last.Changed = true;
            }

            return;
        }

        var turn = Resolve(runId, offset, timestamp);
        turn.EndOffset = next;
        turn.Changed = true;
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

    // Keeps the usage a record reports, as the reader of the usage of a journal reads it: a record that cannot be read
    // is remembered, so that a sum of the usage of the session is never one of a part of it.
    private void ReadUsage(long offset, ReadOnlySpan<byte> whole, bool tooLarge)
    {
        if (tooLarge)
        {
            _usageError ??= "record_too_large";
            return;
        }

        if (whole.IsEmpty)
        {
            _usageError ??= "corrupt_record";
            return;
        }

        AgentEvent? value;
        try
        {
            value = JsonSerializer.Deserialize(whole, AgentJsonSerializerContext.Default.AgentEvent);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            _usageError ??= "corrupt_record";
            return;
        }

        if (value is not AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.UsageUpdated } update)
        {
            _usageError ??= "corrupt_record";
            return;
        }

        // A usage record without an operation only reports the context window or the rate limits.
        if (update.Usage?.LastOperation is { } operation)
        {
            _usage.Add(new(offset, update.RunId?.Value, update.Timestamp.UtcTicks,
                JsonSerializer.Serialize(operation, AgentJsonSerializerContext.Default.AgentOperationUsageSnapshot)));
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
            turn = new Turn(++_turnCount, key, timestamp)
            {
                Boundary = new(offset, _summary, _state, _viewState),
                HistoryIndex = _historyCount - 1,
            };
            _byRunId.Add(key, turn);
            _turns.Add(turn);
            _last = turn;
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
        turn.Provenance = AgentPromptProvenance.Normalize(AgentJournalRecordText.ReadTailString(tail, ProvenanceKey));
    }

    private static AgentJournalTurnReader.Snapshot AsSnapshot(long offset, long next)
        => next - offset <= int.MaxValue ? new(offset, (int)(next - offset)) : default;

    internal sealed class Turn(int number, string runId, DateTimeOffset startedAt)
    {
        internal int Number { get; } = number;
        internal string RunId { get; } = runId;
        internal required AgentJournalTurnReader.Boundary Boundary { get; init; }
        internal required long HistoryIndex { get; init; }

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
        internal string? Provenance;
        internal int Steers;
        internal int AssistantMessages;
        internal int ToolCalls;
        internal int Compactions;

        // Whether a record given to this indexing added to the turn.
        internal bool Changed;

        internal static Turn From(AgentSessionJournalIndexTurn known)
        {
            var value = known.Turn;
            return new Turn(value.Number, value.RunId, value.StartedAt)
            {
                Boundary = new(value.StartOffset, new(known.Summary.Offset, known.Summary.Length),
                    new(known.State.Offset, known.State.Length), new(known.ViewState.Offset, known.ViewState.Length)),
                HistoryIndex = value.HistoryIndex,
                EndOffset = value.EndOffset,
                EndedAt = value.EndedAt,
                Status = value.Status,
                PromptOffset = value.PromptOffset,
                PromptContentId = value.PromptContentId,
                PromptPreview = value.PromptPreview,
                PromptTruncated = value.PromptTruncated,
                SourceSessionId = value.SourceSessionId,
                AskId = value.AskId,
                Provenance = value.Provenance,
                Steers = value.SteerCount,
                AssistantMessages = value.AssistantMessageCount,
                ToolCalls = value.ToolCallCount,
                Compactions = value.CompactionCount,
            };
        }

        internal AgentSessionTurn ToTurn() => new()
        {
            Number = Number,
            RunId = RunId,
            StartOffset = Boundary.Offset,
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
            Provenance = Provenance,
            SteerCount = Steers,
            AssistantMessageCount = AssistantMessages,
            ToolCallCount = ToolCalls,
            CompactionCount = Compactions,
            HistoryIndex = HistoryIndex,
        };

        internal AgentSessionJournalIndexTurn ToIndexTurn() => new(ToTurn(),
            new(Boundary.Summary.Offset, Boundary.Summary.Length),
            new(Boundary.State.Offset, Boundary.State.Length),
            new(Boundary.ViewState.Offset, Boundary.ViewState.Length));
    }
}
