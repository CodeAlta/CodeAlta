using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The events of the end of a journal, from one of its records on.
/// </summary>
/// <param name="Events">The events, in journal order, from the record at <paramref name="StartOffset"/>.</param>
/// <param name="StartOffset">The byte offset of the first record read: the events before it were not read.</param>
/// <param name="Length">The length of the journal the events were read from.</param>
internal sealed record AgentJournalTail(IReadOnlyList<AgentEvent> Events, long StartOffset, long Length);

public sealed partial class FileSystemAgentSessionStore
{
    private const string CompactionCheckpointEventType = "local.compactionCheckpoint";

    // How many events of history this store read from journals, whatever the reader.
    private long _historyEventsRead;

    /// <summary>
    /// Gets how many events of history this store has read from journals since it was created: by a read of a whole
    /// history, of its end from a compaction checkpoint (the events it returns), or of its start before an offset.
    /// </summary>
    internal long HistoryEventsRead => Interlocked.Read(ref _historyEventsRead);

    /// <summary>
    /// Reads the events of a journal from the record of a compaction checkpoint to its end, page by page from the end:
    /// the records before the checkpoint are not read.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="checkpointContentId">The content of the checkpoint, as the state of the session names it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The events from the last record of that checkpoint on; <see langword="null"/> when the journal has no such
    /// record, or changed during the read.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> or <paramref name="checkpointContentId"/> is empty.</exception>
    /// <exception cref="AgentSessionHistoryException">The journal cannot be read by pages (format, record size, missing session).</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    internal async Task<AgentJournalTail?> ReadEventsFromCheckpointAsync(string sessionId, string checkpointContentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointContentId);
        // The pages come from the end, the newest first.
        var pages = new List<AgentSessionHistoryPage>();
        AgentSessionHistoryCursor? cursor = null;
        AgentHistoryRevision? revision = null;
        while (true)
        {
            var page = await ReadTimelinePageAsync(sessionId, cursor, cancellationToken).ConfigureAwait(false);
            if (page.Revision is not { } read || (revision is not null && (read.Length != revision.Length || read.LastWriteUtcTicks != revision.LastWriteUtcTicks)))
            {
                return null;
            }

            revision ??= read;
            pages.Add(page);
            for (var index = page.Entries.Count - 1; index >= 0; index--)
            {
                if (!IsCheckpoint(page.Entries[index].Event, checkpointContentId)) continue;
                var events = new List<AgentEvent>();
                for (var entry = index; entry < page.Entries.Count; entry++) events.Add(page.Entries[entry].Event);
                for (var newer = pages.Count - 2; newer >= 0; newer--)
                {
                    foreach (var entry in pages[newer].Entries) events.Add(entry.Event);
                }

                // The records of the page of the checkpoint that come before it were parsed with it, not kept.
                Interlocked.Add(ref _historyEventsRead, events.Count);
                return new AgentJournalTail(events, page.Entries[index].Offset, revision.Length);
            }

            if (page.Next is not { } next)
            {
                return null;
            }

            cursor = next;
        }
    }

    /// <summary>
    /// Reads the events of the records of a journal that start before an offset, as a read of the whole history reads
    /// them: what the journal holds from that offset on is not read.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="end">The offset of the first record not to read, such as the length the journal had at a moment.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The events, in journal order; none when the session has no journal.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="end"/> is negative.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="JsonException">A record before the offset cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    internal async Task<IReadOnlyList<AgentEvent>> ReadEventsBeforeAsync(string sessionId, long end, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(end);
        var path = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (path is null || end == 0)
        {
            return [];
        }

        return await _journalFile.WithPathLockAsync(path, async () =>
        {
            var history = new List<AgentEvent>();
            await using var file = await OpenReadStreamAsync(path, cancellationToken).ConfigureAwait(false);
            await using var stream = new JournalPrefixStream(file, end);
            await foreach (var @event in ReadJournalEventsAsync(stream, tolerateIncompleteTail: true, cancellationToken).ConfigureAwait(false))
            {
                if (@event is AgentRawEvent { BackendEventType: SessionSummaryEventType or SessionStateEventType or CodeAltaSessionHeaderEventType or CodeAltaSessionStateEventType })
                {
                    continue;
                }

                history.Add(@event);
                Interlocked.Increment(ref _historyEventsRead);
            }

            return (IReadOnlyList<AgentEvent>)history;
        }, cancellationToken).ConfigureAwait(false);
    }

    // Keeps in memory the metadata (summary, state, space) of a journal as its row in the projection cache holds it, as a
    // read of the journal would keep it: valid while the journal has the stamp of the row. An append then advances it
    // (AdvanceMetadataProjection) and the row is written again without reading the journal.
    private void SeedMetadataProjection(AgentSessionCacheProjection row)
    {
        var stamp = GetFileStamp(row.JournalPath);
        if (stamp is not { } current ||
            current.Length != row.Stamp.Length ||
            current.LastWriteTimeUtc.Ticks != row.Stamp.LastWriteTimeUtc.Ticks)
        {
            return;
        }

        var snapshots = new MetadataSnapshots(row.Summary, row.State, row.SpaceId);
        var seeded = new CachedSessionProjection(current, snapshots.ToProjection(), snapshots);
        _metadataProjectionCache.AddOrUpdate(Path.GetFullPath(row.JournalPath), seeded,
            (_, existing) => existing.Stamp == current ? existing : seeded);
    }

    // Before the first append of this store to a journal: the metadata of the journal comes from its row in the
    // projection cache when the row is the projection of the journal as it is, instead of a read of the journal after
    // the append. A journal is named by its session.
    private async Task SeedMetadataProjectionFromCacheAsync(string path, CancellationToken cancellationToken)
    {
        if (_projectionCache is null || _metadataProjectionCache.ContainsKey(Path.GetFullPath(path)) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var row = await _projectionCache.GetSessionAsync(Path.GetFileNameWithoutExtension(path), CreateCacheProjectionContext(), cancellationToken)
                .ConfigureAwait(false);
            if (row is not null && string.Equals(Path.GetFullPath(row.JournalPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                SeedMetadataProjection(row);
            }
        }
        catch (Exception exception) when (exception is AgentSessionCacheLockedException or IOException or JsonException)
        {
            // Without the row, the journal is read after the append, as before.
        }
    }

    private static bool IsCheckpoint(AgentEvent @event, string contentId)
    {
        if (@event is not AgentRawEvent { BackendEventType: CompactionCheckpointEventType } raw) return false;
        try
        {
            return string.Equals(raw.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint)?.ContentId, contentId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The first bytes of a journal: a read ends at the given length, whatever the journal holds after it.
    private sealed class JournalPrefixStream(Stream inner, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = inner.Read(buffer[..Limit(buffer.Length)]);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken).ConfigureAwait(false);
            _position += count;
            return count;
        }

        private int Limit(int requested) => (int)Math.Min(requested, Math.Max(0, length - _position));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
