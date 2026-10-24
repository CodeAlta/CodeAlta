using System.Buffers;

namespace CodeAlta.Agent.Runtime;

// Reads the turns of an already opened journal. No filesystem acquisition, locking or caching here.
internal static class AgentJournalTurnReader
{
    private const int ReadBytes = 128 * 1024;

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

        /// <summary>Gets the number of events of the history before the end boundary.</summary>
        internal long HistoryCount { get; init; }
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
        var indexer = await IndexAsync(stream, length, cancellationToken).ConfigureAwait(false);
        return indexer is null ? new(false, [], [default]) : indexer.ToResult();
    }

    /// <summary>Indexes the records in <c>[0, length)</c>.</summary>
    /// <param name="stream">The opened journal.</param>
    /// <param name="length">The journal length to read up to.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The indexing; null for a journal that is not UTF-8.</returns>
    /// <exception cref="OperationCanceledException">The scan is canceled.</exception>
    internal static async Task<AgentJournalIndexer?> IndexAsync(Stream stream, long length, CancellationToken cancellationToken)
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
            return null;
        }

        if (count >= 3 && bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf)
        {
            start = 3;
        }

        var indexer = new AgentJournalIndexer(start, bom[..(int)start]);
        await ContinueAsync(stream, indexer, start, end, cancellationToken).ConfigureAwait(false);
        return indexer;
    }

    /// <summary>Gives an indexing the records in <c>[start, end)</c>.</summary>
    /// <param name="stream">The opened journal.</param>
    /// <param name="indexer">The indexing, which ends at <paramref name="start"/>.</param>
    /// <param name="start">The offset to read from: where the indexing ends.</param>
    /// <param name="end">The offset to read up to.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>A task that completes once read.</returns>
    /// <exception cref="OperationCanceledException">The scan is canceled.</exception>
    internal static async Task ContinueAsync(Stream stream, AgentJournalIndexer indexer, long start, long end, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(indexer);
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

                indexer.Consume(buffer.AsSpan(0, read), position);
                position += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
}
