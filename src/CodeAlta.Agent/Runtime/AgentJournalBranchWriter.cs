using System.Buffers;
using System.Text;

namespace CodeAlta.Agent.Runtime;

// Copies the records of one journal into the journal of another session. No filesystem acquisition or locking here.
internal static class AgentJournalBranchWriter
{
    /// <summary>The largest record whose attachment paths are rewritten; a larger one keeps them.</summary>
    internal const int MaxEditedRecordBytes = 16 * 1024 * 1024;

    private const int ReadBytes = 256 * 1024;

    /// <summary>Where the attachments of the two sessions are, as the folders of their journals name them.</summary>
    /// <param name="SourceShard">The date folders of the source journal: year, month and day.</param>
    /// <param name="TargetShard">The date folders of the target journal: year, month and day.</param>
    internal sealed record AttachmentMove((string Year, string Month, string Day) SourceShard, (string Year, string Month, string Day) TargetShard);

    /// <summary>What a copy did.</summary>
    internal sealed class Result
    {
        /// <summary>Gets the number of records written.</summary>
        internal long Records { get; set; }

        /// <summary>Gets the number of metadata snapshots left out.</summary>
        internal long Snapshots { get; set; }

        /// <summary>Gets the number of bytes written.</summary>
        internal long Bytes { get; set; }

        /// <summary>Gets the names of the attachment files the written records name.</summary>
        internal HashSet<string> AttachmentFiles { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Copies the records in <c>[start, end)</c> of a journal as records of another session.</summary>
    /// <param name="source">The opened source journal.</param>
    /// <param name="start">Where to start, at the start of a record.</param>
    /// <param name="end">Where to stop, at the start of a record or at the end of the journal.</param>
    /// <param name="output">Where the records are written.</param>
    /// <param name="sourceSessionId">The session the records belong to.</param>
    /// <param name="targetSessionId">The session they are written for.</param>
    /// <param name="attachments">Where the attachments of both sessions are; null to leave the paths of attachments as they are.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>What was written.</returns>
    /// <remarks>
    /// The bytes of a record are copied as they are, apart from two things: its session identifier, which is the one
    /// among the fields every record ends with, and the paths of the attachments of the source session, which name
    /// the folder of the target session afterwards. Nothing else of a record is read, so what the model is sent again
    /// from the copy is what it was sent from the source. The four metadata snapshots are left out: the last one of a
    /// journal counts, and a summary inherits its lineage from the ones before it, so those of another session must
    /// not precede the ones the caller writes for the target. Blank lines are left out too.
    /// </remarks>
    /// <exception cref="InvalidDataException">A record does not end with the fields every record has.</exception>
    /// <exception cref="OperationCanceledException">The copy is canceled.</exception>
    internal static async Task<Result> CopyAsync(Stream source, long start, long end, Stream output, string sourceSessionId,
        string targetSessionId, AttachmentMove? attachments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSessionId);
        var copy = new Copy(sourceSessionId, targetSessionId, attachments);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBytes);
        try
        {
            source.Position = start;
            var position = start;
            var filled = 0;
            while (position + filled < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (filled == buffer.Length)
                {
                    if (buffer.Length >= MaxEditedRecordBytes)
                    {
                        // One record is larger than what is edited in memory: it is passed through, and only the
                        // fields it ends with are changed.
                        var kept = copy.BeginLarge(buffer.AsSpan(0, filled));
                        buffer.AsSpan(filled - kept, kept).CopyTo(buffer);
                        position += filled - kept;
                        filled = kept;
                    }
                    else
                    {
                        var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(buffer.Length * 2L, MaxEditedRecordBytes));
                        buffer.AsSpan(0, filled).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = larger;
                    }
                }

                var read = await source.ReadAsync(buffer.AsMemory(filled, (int)Math.Min(buffer.Length - filled, end - position - filled)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // The journal became shorter after the range was chosen; what was read is all there is.
                    end = position + filled;
                }

                filled += read;
                var consumed = copy.Consume(buffer.AsSpan(0, filled), atEnd: position + filled >= end);
                await copy.FlushAsync(output, cancellationToken).ConfigureAwait(false);
                buffer.AsSpan(consumed, filled - consumed).CopyTo(buffer);
                position += consumed;
                filled -= consumed;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return copy.Result;
    }

    private sealed class Copy
    {
        private readonly byte[] _sourceId;
        private readonly byte[] _targetId;
        private readonly byte[]? _needle;
        private readonly byte[]? _sourceShard;
        private readonly byte[]? _targetShard;
        private readonly ArrayBufferWriter<byte> _pending = new(ReadBytes);

        // A record larger than the buffer: what was read of it has been written, apart from its last bytes.
        private bool _large;
        private bool _largeSkipped;

        internal Copy(string sourceSessionId, string targetSessionId, AttachmentMove? attachments)
        {
            _sourceId = Encoding.UTF8.GetBytes(sourceSessionId);
            _targetId = Encoding.UTF8.GetBytes(targetSessionId);
            if (attachments is not null)
            {
                _needle = Encoding.UTF8.GetBytes(sourceSessionId + ".attachments");
                _sourceShard = Encoding.ASCII.GetBytes(attachments.SourceShard.Year + attachments.SourceShard.Month + attachments.SourceShard.Day);
                _targetShard = Encoding.ASCII.GetBytes(attachments.TargetShard.Year + attachments.TargetShard.Month + attachments.TargetShard.Day);
            }
        }

        internal Result Result { get; } = new();

        // Writes what was prepared since the last flush.
        internal async ValueTask FlushAsync(Stream output, CancellationToken cancellationToken)
        {
            if (_pending.WrittenCount == 0)
            {
                return;
            }

            await output.WriteAsync(_pending.WrittenMemory, cancellationToken).ConfigureAwait(false);
            Result.Bytes += _pending.WrittenCount;
            _pending.ResetWrittenCount();
        }

        // Prepares every complete record of the buffer and returns the number of bytes it consumed.
        internal int Consume(ReadOnlySpan<byte> buffer, bool atEnd)
        {
            var consumed = 0;
            while (consumed < buffer.Length)
            {
                var rest = buffer[consumed..];
                var newline = rest.IndexOf((byte)'\n');
                if (newline < 0 && !atEnd)
                {
                    break;
                }

                // The last record of the range may have no line end.
                var length = newline < 0 ? rest.Length : newline;
                var line = rest[..length];
                var terminator = newline < 0 ? default : rest.Slice(newline, 1);
                if (length > 0 && line[^1] == (byte)'\r' && newline >= 0)
                {
                    line = line[..^1];
                    terminator = rest.Slice(newline - 1, 2);
                }

                if (_large)
                {
                    EndLarge(line, terminator);
                }
                else
                {
                    Write(line, terminator);
                }

                consumed += newline < 0 ? length : newline + 1;
            }

            return consumed;
        }

        // Starts a record that does not fit the buffer, or continues it. Returns the number of bytes to keep: the
        // ones that may hold the fields the record ends with.
        internal int BeginLarge(ReadOnlySpan<byte> buffer)
        {
            if (!_large)
            {
                _large = true;
                var kind = AgentJournalRecordText.Classify(buffer[..AgentJournalRecordText.HeadBytes]);
                _largeSkipped = AgentJournalRecordText.IsMetadataSnapshot(kind);
            }

            var kept = Math.Min(buffer.Length, AgentJournalRecordText.TailBytes + 1);
            if (!_largeSkipped)
            {
                _pending.Write(buffer[..^kept]);
            }

            return kept;
        }

        private void EndLarge(ReadOnlySpan<byte> line, ReadOnlySpan<byte> terminator)
        {
            _large = false;
            if (_largeSkipped)
            {
                Result.Snapshots++;
                return;
            }

            // The line here is what was kept of the record and what was read after it: its end.
            if (!TryWriteRecord(line))
            {
                throw new InvalidDataException("A journal record does not end with the session it belongs to.");
            }

            _pending.Write(terminator.IsEmpty ? "\n"u8 : terminator);
            Result.Records++;
        }

        private void Write(ReadOnlySpan<byte> line, ReadOnlySpan<byte> terminator)
        {
            if (line.IsEmpty)
            {
                return;
            }

            var kind = AgentJournalRecordText.Classify(line[..Math.Min(line.Length, AgentJournalRecordText.HeadBytes)]);
            if (AgentJournalRecordText.IsMetadataSnapshot(kind))
            {
                Result.Snapshots++;
                return;
            }

            if (!TryWriteRecord(line))
            {
                WriteParsed(line);
            }

            _pending.Write(terminator.IsEmpty ? "\n"u8 : terminator);
            Result.Records++;
        }

        // Writes a record whose end is the fields every record ends with: the session among them is replaced, and
        // what precedes it is searched for paths of attachments.
        private bool TryWriteRecord(ReadOnlySpan<byte> line)
        {
            var tail = line[^Math.Min(line.Length, AgentJournalRecordText.TailBytes)..];
            if (!AgentJournalRecordText.TryFindSessionId(tail, out var range))
            {
                return false;
            }

            var (start, length) = range.GetOffsetAndLength(tail.Length);
            start += line.Length - tail.Length;
            WriteBody(line[..start]);
            // A record that already names another session keeps it: it was not this session's to begin with.
            var value = line.Slice(start, length);
            _pending.Write(value.SequenceEqual(_sourceId) ? _targetId : value);
            _pending.Write(line[(start + length)..]);
            return true;
        }

        // A record that an older version wrote with its fields in another order: its session is found by reading it
        // as JSON, and the record is written as it is around that one value.
        private void WriteParsed(ReadOnlySpan<byte> line)
        {
            var start = -1;
            var length = 0;
            try
            {
                var reader = new System.Text.Json.Utf8JsonReader(line);
                if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
                {
                    throw new InvalidDataException("A journal record is not a JSON object.");
                }

                while (reader.Read() && reader.CurrentDepth >= 1)
                {
                    if (reader.CurrentDepth != 1 || reader.TokenType != System.Text.Json.JsonTokenType.PropertyName)
                    {
                        continue;
                    }

                    var identity = reader.ValueTextEquals("sessionId"u8);
                    if (!reader.Read())
                    {
                        break;
                    }

                    if (identity && reader.TokenType == System.Text.Json.JsonTokenType.String && !reader.ValueIsEscaped)
                    {
                        // The token starts at its opening quote.
                        start = (int)reader.TokenStartIndex + 1;
                        length = reader.ValueSpan.Length;
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }
            catch (System.Text.Json.JsonException error)
            {
                throw new InvalidDataException("A journal record is not valid JSON.", error);
            }

            if (start < 0 || !line.Slice(start, length).SequenceEqual(_sourceId))
            {
                // Without a session, or with another one: nothing to change but the paths of attachments.
                WriteBody(line);
                return;
            }

            WriteBody(line[..start]);
            _pending.Write(_targetId);
            WriteBody(line[(start + length)..]);
        }

        // Writes a part of a record, with the paths of the attachments of the source moved to the target.
        private void WriteBody(ReadOnlySpan<byte> body)
        {
            if (_needle is null)
            {
                _pending.Write(body);
                return;
            }

            while (true)
            {
                var index = body.IndexOf(_needle);
                if (index < 0)
                {
                    _pending.Write(body);
                    return;
                }

                var after = body[(index + _needle.Length)..];
                if (TryReadShard(body[..index], out var shardStart))
                {
                    _pending.Write(body[..shardStart]);
                    WriteShard(body[shardStart..index]);
                    _pending.Write(_targetId);
                    _pending.Write(".attachments"u8);
                    AddFile(after);
                }
                else
                {
                    // The name of the folder alone, in some text: not a path of this store.
                    _pending.Write(body[..(index + _needle.Length)]);
                }

                body = after;
            }
        }

        // Looks for the date folders of the source journal before the folder of its attachments, with the separator
        // JSON writes them with: a slash, an escaped slash or an escaped backslash.
        private bool TryReadShard(ReadOnlySpan<byte> before, out int start)
        {
            start = 0;
            var position = before.Length;
            ReadOnlySpan<int> digits = [2, 2, 4];
            var shard = _sourceShard!;
            var shardEnd = shard.Length;
            foreach (var count in digits)
            {
                var separator = SeparatorLength(before[..position]);
                if (separator == 0 || position - separator < count)
                {
                    return false;
                }

                position -= separator;
                if (!before.Slice(position - count, count).SequenceEqual(shard.AsSpan(shardEnd - count, count)))
                {
                    return false;
                }

                position -= count;
                shardEnd -= count;
            }

            start = position;
            return true;
        }

        // Year, separator, month, separator, day, separator: the digits change, the separators stay.
        private void WriteShard(ReadOnlySpan<byte> shard)
        {
            var target = _targetShard!;
            var position = 0;
            var written = 0;
            ReadOnlySpan<int> digits = [4, 2, 2];
            foreach (var count in digits)
            {
                _pending.Write(target.AsSpan(written, count));
                written += count;
                position += count;
                var separator = shard[position] == (byte)'/' ? 1 : 2;
                _pending.Write(shard.Slice(position, separator));
                position += separator;
            }
        }

        // The length of the separator a span ends with: "/", "\/" or "\\".
        private static int SeparatorLength(ReadOnlySpan<byte> text)
        {
            if (text.Length >= 2 && text[^2] == (byte)'\\' && text[^1] is (byte)'\\' or (byte)'/')
            {
                return 2;
            }

            return text.Length >= 1 && text[^1] == (byte)'/' ? 1 : 0;
        }

        private void AddFile(ReadOnlySpan<byte> after)
        {
            var separator = after.StartsWith("\\\\"u8) || after.StartsWith("\\/"u8) ? 2 : after.StartsWith("/"u8) ? 1 : 0;
            if (separator == 0)
            {
                return;
            }

            var name = after[separator..];
            var length = 0;
            while (length < name.Length && (char.IsAsciiLetterOrDigit((char)name[length]) || name[length] is (byte)'.' or (byte)'-' or (byte)'_'))
            {
                length++;
            }

            if (length > 0)
            {
                Result.AttachmentFiles.Add(Encoding.ASCII.GetString(name[..length]));
            }
        }
    }
}
