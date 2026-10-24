using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

public sealed partial class FileSystemAgentSessionStore
{
    // A journal of at most this many bytes that the index has no row of yet is indexed from its start by the append that
    // follows: a session whose first records (its header) were written without the index.
    private const int IndexOnAppendMaxBytes = 64 * 1024;

    // How many times a read of the index starts again when another writer went on meanwhile.
    private const int IndexReadAttempts = 3;

    private IAgentSessionJournalIndex? JournalIndex => _projectionCache?.JournalIndex;

    // The index keys a session by the name of its journal file.
    private static string IndexKey(string path) => Path.GetFileNameWithoutExtension(path);

    private static bool IsIndexOf(AgentSessionJournalIndexRow row, string fullPath, long length, long lastWriteUtcTicks)
        => row.FormatVersion == AgentSessionJournalIndexRow.CurrentFormatVersion
            && row.Length == length && row.LastWriteUtcTicks == lastWriteUtcTicks
            && string.Equals(row.JournalPath, fullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Brings the index of a journal to the journal as it is now and returns its row: a row that covers the length and
    /// the last-write time of the journal is returned without reading it; otherwise the journal is read from where the
    /// row ends when it only grew, or from its start, and the row is written back.
    /// </summary>
    /// <returns>The row; null when the journal does not exist.</returns>
    private async Task<AgentSessionJournalIndexRow?> EnsureIndexedAsync(IAgentSessionJournalIndex index, string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var key = IndexKey(fullPath);
        AgentSessionJournalIndexRow? indexed = null;
        for (var attempt = 0; attempt < IndexReadAttempts; attempt++)
        {
            if (GetFileStamp(fullPath) is not { } stamp)
            {
                return null;
            }

            var row = await index.ReadRowAsync(key, cancellationToken).ConfigureAwait(false);
            if (row is not null && IsIndexOf(row, fullPath, stamp.Length, stamp.LastWriteTimeUtc.Ticks))
            {
                return row;
            }

            indexed = await IndexJournalAsync(index, fullPath, row, cancellationToken).ConfigureAwait(false);
            if (indexed is null)
            {
                return null;
            }

            if (GetFileStamp(fullPath) is { } after && IsIndexOf(indexed, fullPath, after.Length, after.LastWriteTimeUtc.Ticks))
            {
                return indexed;
            }
        }

        // A journal that keeps growing: the row of the journal as it was read is right for that revision.
        return indexed;
    }

    // Reads the journal, from where a row of it ends when the journal only grew since, else from its start, and writes
    // the row back. The journal gate only orders the read after admitted writes; it is not held while reading.
    private async Task<AgentSessionJournalIndexRow?> IndexJournalAsync(IAgentSessionJournalIndex index, string fullPath,
        AgentSessionJournalIndexRow? row, CancellationToken cancellationToken)
    {
        var key = IndexKey(fullPath);
        var stamp = await _journalFile.WithPathLockAsync(fullPath, () => Task.FromResult(GetFileStamp(fullPath)), cancellationToken).ConfigureAwait(false);
        if (stamp is null)
        {
            return null;
        }

        var (length, ticks) = (stamp.Value.Length, stamp.Value.LastWriteTimeUtc.Ticks);
        await using var stream = await OpenReadStreamAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (row is { Supported: true, FormatVersion: AgentSessionJournalIndexRow.CurrentFormatVersion }
            && string.Equals(row.JournalPath, fullPath, StringComparison.OrdinalIgnoreCase)
            && length > row.Length && row.EndOffset <= row.Length
            && await HasIndexGuardAsync(stream, row, cancellationToken).ConfigureAwait(false)
            && await index.ReadTurnsAsync(key, cancellationToken).ConfigureAwait(false) is { } known
            && known.Row.Length == row.Length && known.Row.LastWriteUtcTicks == row.LastWriteUtcTicks)
        {
            // The journal grew: only what follows the end of the row is read.
            var resumed = new AgentJournalIndexer(known.Row, known.Turns);
            await AgentJournalTurnReader.ContinueAsync(stream, resumed, row.EndOffset, length, cancellationToken).ConfigureAwait(false);
            var grown = resumed.ToRow(key, fullPath, length, ticks);
            await index.WriteAsync(new(grown, resumed.ToIndexTurns(changedOnly: true), resumed.Usage, Replace: false,
                new AgentSessionCacheFileStamp(new DateTime(row.LastWriteUtcTicks, DateTimeKind.Utc), row.Length)), cancellationToken).ConfigureAwait(false);
            return grown;
        }

        var indexer = await AgentJournalTurnReader.IndexAsync(stream, length, cancellationToken).ConfigureAwait(false);
        var replaced = indexer?.ToRow(key, fullPath, length, ticks) ?? new AgentSessionJournalIndexRow
        {
            SessionId = key,
            JournalPath = fullPath,
            Length = length,
            LastWriteUtcTicks = ticks,
            EndOffset = 0,
            Guard = [],
            Supported = false,
        };
        await index.WriteAsync(new(replaced, indexer?.ToIndexTurns(changedOnly: false) ?? [], indexer?.Usage ?? [], Replace: true, Expected: null),
            cancellationToken).ConfigureAwait(false);
        return replaced;
    }

    private static async Task<bool> HasIndexGuardAsync(Stream stream, AgentSessionJournalIndexRow row, CancellationToken cancellationToken)
    {
        if (row.Guard.Length > row.EndOffset)
        {
            return false;
        }

        var guard = new byte[row.Guard.Length];
        stream.Position = row.EndOffset - guard.Length;
        return await stream.ReadAtLeastAsync(guard, guard.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) == guard.Length
            && guard.AsSpan().SequenceEqual(row.Guard);
    }

    // Indexes the journal of a session again from its start, after the store replaced it (a rewind, a provider
    // selection) or wrote it whole (a fork): the offsets of its records changed.
    private async Task ReindexAsync(string path, CancellationToken cancellationToken)
    {
        if (JournalIndex is not { } index)
        {
            return;
        }

        await IndexJournalAsync(index, Path.GetFullPath(path), row: null, cancellationToken).ConfigureAwait(false);
    }

    private Task RemoveIndexAsync(string path, CancellationToken cancellationToken)
        => JournalIndex is { } index ? index.RemoveAsync(IndexKey(path), cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Brings the index of a journal past records just appended to it, from the bytes that were written and without
    /// reading the journal: when the index covers the journal as it was before the append, and the append is the only
    /// change since. Otherwise nothing is written, and the next read of the index reads the journal from where it ends.
    /// </summary>
    /// <param name="path">The journal.</param>
    /// <param name="append">The append, with the bytes it wrote.</param>
    /// <param name="cancellationToken">Stops the write before it starts.</param>
    /// <returns>A task that completes once the index is written.</returns>
    /// <exception cref="AgentSessionCacheLockedException">The database of the index is locked.</exception>
    internal async Task AdvanceIndexAsync(string path, JournalAppend append, CancellationToken cancellationToken)
    {
        if (JournalIndex is not { } index || append.After is not { } after || append.Bytes.IsEmpty)
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var key = IndexKey(fullPath);
        var beforeLength = append.Before?.Length ?? 0;
        if (after.Length != beforeLength + append.Bytes.Length)
        {
            // Another writer appended meanwhile, or the journal was replaced.
            return;
        }

        AgentJournalIndexer indexer;
        var replace = true;
        AgentSessionCacheFileStamp? expected = null;
        var written = append.Bytes;
        var position = beforeLength;
        if (beforeLength == 0)
        {
            // A new journal: all of it is what was written, after the byte order mark of UTF-8 when it starts with one.
            var bom = written.Span is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
            indexer = new AgentJournalIndexer(bom, written[..bom].ToArray());
            written = written[bom..];
            position = bom;
        }
        else if (await index.ReadRowAsync(key, cancellationToken).ConfigureAwait(false) is { } row)
        {
            if (append.Before is not { } before || !IsIndexOf(row, fullPath, before.Length, before.LastWriteTimeUtc.Ticks)
                || row.EndOffset != row.Length || !row.Supported)
            {
                return;
            }

            var runs = AgentJournalIndexer.CollectRunIds(append.Bytes.Span);
            if (row.LastRunId is { } last)
            {
                runs.Add(last);
            }

            var turns = await index.ReadTurnsOfRunsAsync(key, runs, cancellationToken).ConfigureAwait(false);
            indexer = new AgentJournalIndexer(row, turns);
            replace = false;
            expected = new AgentSessionCacheFileStamp(before.LastWriteTimeUtc, before.Length);
        }
        else if (beforeLength <= IndexOnAppendMaxBytes)
        {
            await using var stream = await OpenReadStreamAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (await AgentJournalTurnReader.IndexAsync(stream, beforeLength, cancellationToken).ConfigureAwait(false) is not { } head
                || head.EndOffset != beforeLength)
            {
                return;
            }

            indexer = head;
        }
        else
        {
            return;
        }

        indexer.Consume(written.Span, position);
        await index.WriteAsync(new(indexer.ToRow(key, fullPath, after.Length, after.LastWriteTimeUtc.Ticks),
            indexer.ToIndexTurns(changedOnly: !replace), indexer.Usage, replace, expected), cancellationToken).ConfigureAwait(false);
    }

    // The turns of a session from the index, brought to its journal as it is now; null without an index or for a journal
    // the index cannot read (another encoding).
    private async Task<TurnScan?> ScanIndexedTurnsAsync(IAgentSessionJournalIndex index, string sessionId, string contained,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < IndexReadAttempts; attempt++)
        {
            var row = await EnsureIndexedAsync(index, contained, cancellationToken).ConfigureAwait(false)
                ?? throw new AgentSessionHistoryException("missing_session");
            if (!row.Supported)
            {
                throw new AgentSessionHistoryException("unsupported_format");
            }

            var read = await index.ReadTurnsAsync(IndexKey(contained), cancellationToken).ConfigureAwait(false);
            if (read is null || read.Row.Length != row.Length || read.Row.LastWriteUtcTicks != row.LastWriteUtcTicks)
            {
                // Another writer went on meanwhile, or the index could not keep the row.
                continue;
            }

            var turns = new AgentSessionTurn[read.Turns.Count];
            var boundaries = new AgentJournalTurnReader.Boundary[read.Turns.Count + 1];
            for (var number = 0; number < turns.Length; number++)
            {
                var turn = read.Turns[number];
                turns[number] = turn.Turn;
                boundaries[number] = new(turn.Turn.StartOffset, Snapshot(turn.Summary), Snapshot(turn.State), Snapshot(turn.ViewState));
            }

            boundaries[^1] = new(row.EndOffset, Snapshot(row.Summary), Snapshot(row.State), Snapshot(row.ViewState));
            return new TurnScan(contained, new AgentHistoryRevision(sessionId, row.Length, row.LastWriteUtcTicks),
                new AgentJournalTurnReader.Result(true, turns, boundaries) { HistoryCount = row.HistoryCount });
        }

        return null;

        static AgentJournalTurnReader.Snapshot Snapshot(AgentJournalRecordPosition position) => new(position.Offset, position.Length);
    }

    /// <summary>
    /// Reads the events of the history that a range of the journal of a session holds, from a revision of it that a read
    /// of its turns gave: blank lines and the four metadata snapshots are left out, as the history pages leave them out.
    /// </summary>
    /// <param name="revision">The revision the range was read from (<see cref="AgentSessionTurns.Revision"/>).</param>
    /// <param name="start">The start of a record, such as the start of a turn, or 0.</param>
    /// <param name="end">The end of a record, such as <see cref="AgentSessionTurns.EndOffset"/>.</param>
    /// <param name="cancellationToken">Cancels lookup or reading.</param>
    /// <returns>The events in journal order.</returns>
    /// <remarks>Only the range is read. The journal only grows, but for a replacement, which fails the read.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="revision"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within the revision.</exception>
    /// <exception cref="AgentSessionHistoryException">The session is missing or its journal was replaced (<c>history_changed</c>).</exception>
    /// <exception cref="JsonException">A record of the range is malformed.</exception>
    /// <exception cref="IOException">Journal access fails.</exception>
    internal async Task<IReadOnlyList<AgentEvent>> ReadHistoryRangeAsync(AgentHistoryRevision revision, long start, long end,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (start < 0 || end < start || end > revision.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "The range is not within the revision of the journal.");
        }

        var path = await TryGetSessionFilePathAsync(revision.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentSessionHistoryException("missing_session");
        return await AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path, async (contained, token) =>
        {
            void CheckRevision()
            {
                // A journal only grows: one that is shorter, or as long but written since, was replaced.
                if (GetFileStamp(contained) is not { } stamp || stamp.Length < revision.Length
                    || (stamp.Length == revision.Length && stamp.LastWriteTimeUtc.Ticks != revision.LastWriteUtcTicks))
                {
                    throw new AgentSessionHistoryException("history_changed");
                }
            }

            CheckRevision();
            var events = new List<AgentEvent>();
            await using (var stream = await OpenReadStreamAsync(contained, token).ConfigureAwait(false))
            {
                var bytes = new byte[end - start];
                stream.Position = start;
                await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                var span = (ReadOnlySpan<byte>)bytes;
                if (start == 0 && span is [0xef, 0xbb, 0xbf, ..])
                {
                    span = span[3..];
                }

                while (!span.IsEmpty)
                {
                    token.ThrowIfCancellationRequested();
                    var newline = span.IndexOf((byte)'\n');
                    var line = newline < 0 ? span : span[..newline];
                    span = newline < 0 ? default : span[(newline + 1)..];
                    if (line.IndexOfAnyExcept(" \t\r"u8) < 0)
                    {
                        continue;
                    }

                    var value = JsonSerializer.Deserialize(line, AgentJsonSerializerContext.Default.AgentEvent)
                        ?? throw new JsonException("Journal line deserialized to null.");
                    if (value is not AgentRawEvent { BackendEventType: SessionSummaryEventType or SessionStateEventType or CodeAltaSessionHeaderEventType or CodeAltaSessionStateEventType })
                    {
                        events.Add(value);
                    }
                }
            }

            CheckRevision();
            return (IReadOnlyList<AgentEvent>)events;
        }, cancellationToken).ConfigureAwait(false);
    }

    // The latest notes from the index; Found is false when the index cannot answer for this journal (it holds a record
    // that is no record, or has another encoding): the journal is then read as before.
    private async Task<(bool Found, AgentNotesEvent? Notes)> ReadIndexedNotesAsync(IAgentSessionJournalIndex index, string path,
        CancellationToken cancellationToken)
    {
        var row = await EnsureIndexedAsync(index, path, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The session journal does not exist.", Path.GetFullPath(path));
        if (!row.Supported || row.MalformedOffset is not null)
        {
            return (false, null);
        }

        return (true, ParseIndexedNotes(row.NotesJson));
    }

    // Appends notes to a journal the index covers: the records the index read are not validated again (but for the bytes
    // that end them), only those written since are, on the append handle. A journal with a record that is no record, or
    // of another encoding, has every record validated, as without the index.
    private async Task AppendIndexedNotesAsync(IAgentSessionJournalIndex index, string path, AgentNotesEvent notes, Func<Task> committed,
        CancellationToken cancellationToken)
    {
        var row = await EnsureIndexedAsync(index, path, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The session journal does not exist.", Path.GetFullPath(path));
        var prefix = row is { Supported: true, MalformedOffset: null, EndOffset: > 0 }
            ? new AgentJournalNotesReader.Prefix(row.EndOffset, row.LastWriteUtcTicks, row.Guard, ParseIndexedNotes(row.NotesJson))
            : null;
        await _journalFile.AppendNotesLineIndexedAsync(path, notes.ToJson(), Utf8WithoutBom, prefix,
            static file => new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, useAsync: true),
            async append =>
            {
                try
                {
                    await AdvanceIndexAsync(path, append, CancellationToken.None).ConfigureAwait(false);
                    await UpsertCacheFromFileAsync(path, CancellationToken.None, ownsJournalGate: true).ConfigureAwait(false);
                    await committed().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw new AgentNotesCommittedException(notes.SessionId, exception);
                }
            }, cancellationToken).ConfigureAwait(false);
    }

    private static AgentNotesEvent? ParseIndexedNotes(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var notes = JsonSerializer.Deserialize(json, AgentJsonSerializerContext.Default.AgentEvent) as AgentNotesEvent
            ?? throw new JsonException("The notes record of the index is not a notes event.");
        return notes.Markdown is null ? throw new InvalidDataException("A notes journal event has no Markdown value.") : notes;
    }
}
