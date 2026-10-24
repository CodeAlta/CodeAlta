using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>A point of a session to fork it at or to rewind it to, read from one revision of its journal.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Revision">The journal the point was read from.</param>
/// <param name="CutOffset">The byte offset of the point: what precedes it is kept.</param>
/// <param name="Guard">The bytes that end at <paramref name="CutOffset"/>, which a later commit finds again in the journal.</param>
/// <param name="Turns">Every turn of the journal.</param>
/// <param name="KeptTurnCount">The number of turns before the point.</param>
/// <param name="Summary">The summary of the session as it is now.</param>
/// <param name="State">The state of the session at the point: the last one written before it.</param>
/// <param name="CurrentState">The state of the session as it is now: the last one of the journal.</param>
internal sealed record AgentSessionBranchPoint(
    string SessionId,
    AgentHistoryRevision Revision,
    long CutOffset,
    byte[] Guard,
    IReadOnlyList<AgentSessionTurn> Turns,
    int KeptTurnCount,
    AgentSessionSummary Summary,
    AgentSessionState State,
    AgentSessionState CurrentState)
{
    /// <summary>Gets the first turn after the point; null when the point is after the last turn.</summary>
    internal AgentSessionTurn? NextTurn => KeptTurnCount < Turns.Count ? Turns[KeptTurnCount] : null;

    /// <summary>Gets the exclusive byte offset of the end of the last complete record of the journal.</summary>
    internal required long EndOffset { get; init; }
}

public sealed partial class FileSystemAgentSessionStore
{
    private const int BranchGuardBytes = 256;

    /// <summary>Reads the turns of a session.</summary>
    /// <param name="sessionId">An existing session identifier, never a path.</param>
    /// <param name="cancellationToken">Cancels lookup, gate admission or reading.</param>
    /// <returns>The turns in journal order, with the journal revision they were read from.</returns>
    /// <remarks>
    /// A store with a session index (<see cref="IAgentSessionProjectionCache.JournalIndex"/>) reads them from the index,
    /// which every append through the store keeps up to date: the journal is not read. A journal the index does not
    /// cover as it is (written by something else) is read from where the index ends, or from its start when it was
    /// replaced, and the index written back. Without an index, the whole journal is read, by the two ends of each record
    /// only: the cost is the reading of the file, whatever the size of a record. The journal gate orders the read after
    /// admitted writes and is not held while reading, so a session that runs is not delayed; what it writes meanwhile is
    /// not in the result.
    /// </remarks>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="AgentSessionHistoryException">The session is missing, its journal is outside the sessions root or is not UTF-8.</exception>
    /// <exception cref="IOException">Journal access fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<AgentSessionTurns> ReadTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var scan = await ScanTurnsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new(scan.Revision, scan.Result.Turns, scan.Result.End.Offset) { HistoryCount = scan.Result.HistoryCount };
    }

    /// <summary>Resolves a point of a session: the start of a turn, or the end of what is complete.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="beforeRunId">The run of the turn the point precedes; null for the point after the last turn.</param>
    /// <param name="activeRunId">The run that is in progress, which no point lies after; null when the session is idle.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The point, with what a fork or a rewind writes from it.</returns>
    /// <exception cref="AgentSessionHistoryException">
    /// The session or its summary is missing (<c>missing_session</c>), the turn is not in the journal (<c>turn_not_found</c>),
    /// or it starts after the run in progress (<c>turn_running</c>).
    /// </exception>
    internal async Task<AgentSessionBranchPoint> ReadBranchPointAsync(string sessionId, string? beforeRunId, string? activeRunId,
        CancellationToken cancellationToken)
    {
        var scan = await ScanTurnsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var turns = scan.Result.Turns;
        var kept = turns.Count;
        // A turn that runs is never part of what is kept: its records are still being written.
        for (var index = 0; index < turns.Count && activeRunId is not null; index++)
        {
            if (string.Equals(turns[index].RunId, activeRunId, StringComparison.Ordinal))
            {
                kept = index;
                break;
            }
        }

        if (beforeRunId is not null)
        {
            var found = -1;
            for (var index = 0; index < turns.Count && found < 0; index++)
            {
                if (string.Equals(turns[index].RunId, beforeRunId, StringComparison.Ordinal))
                {
                    found = index;
                }
            }

            if (found < 0) throw new AgentSessionHistoryException("turn_not_found");
            if (found > kept) throw new AgentSessionHistoryException("turn_running");
            kept = found;
        }

        var boundary = scan.Result.Boundaries[kept];
        var summary = await GetSessionSummaryAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentSessionHistoryException("missing_session");
        await using var stream = await OpenReadStreamAsync(scan.Path, cancellationToken).ConfigureAwait(false);
        var initial = new AgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = summary.ProtocolFamily,
            ProviderKey = summary.ProviderKey,
            UpdatedAt = summary.CreatedAt,
        };
        var state = await ReadStateSnapshotAsync(stream, boundary.State, cancellationToken).ConfigureAwait(false) ?? initial;
        var end = scan.Result.End;
        var current = end.State == boundary.State
            ? state
            : await ReadStateSnapshotAsync(stream, end.State, cancellationToken).ConfigureAwait(false) ?? initial;
        var guard = new byte[(int)Math.Min(BranchGuardBytes, boundary.Offset)];
        stream.Position = boundary.Offset - guard.Length;
        await stream.ReadExactlyAsync(guard, cancellationToken).ConfigureAwait(false);
        return new(sessionId, scan.Revision, boundary.Offset, guard, turns, kept, summary, state, current) { EndOffset = end.Offset };
    }

    /// <summary>Writes the journal of a new session that starts with the records of another one up to a point.</summary>
    /// <param name="point">The point of the source session.</param>
    /// <param name="summary">The summary of the new session: its identifier and its creation time decide where its journal is.</param>
    /// <param name="state">The state of the new session.</param>
    /// <param name="header">The first line of the new journal; null for none.</param>
    /// <param name="additionalSnapshots">Records written after the summary and the state.</param>
    /// <param name="includeRemainder">
    /// True to copy the records after the point as well, up to the last complete one: the new session then holds what a
    /// rewind to the point removes from the source.
    /// </param>
    /// <param name="cancellationToken">Cancels the copy; nothing of the new session remains then.</param>
    /// <returns>The number of records copied.</returns>
    /// <remarks>
    /// The source is read without its gate: its records up to the point never change while it only grows, and the
    /// bytes that end at the point are compared with the ones the point was read with. The journal is written beside
    /// its final place and renamed, which is the commit point; the attachments its records name are copied before.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The source changed before the point, or the new session exists.</exception>
    /// <exception cref="InvalidDataException">A record of the source cannot be given to another session.</exception>
    /// <exception cref="IOException">Reading or writing fails.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    internal async Task<long> CommitForkAsync(AgentSessionBranchPoint point, AgentSessionSummary summary, AgentSessionState state,
        string? header, IReadOnlyList<string> additionalSnapshots, bool includeRemainder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(additionalSnapshots);
        if (string.Equals(summary.SessionId, point.SessionId, StringComparison.OrdinalIgnoreCase) || summary.SessionId != state.SessionId)
            throw new ArgumentException("A fork needs a session identifier of its own.", nameof(summary));
        var sourcePath = await TryGetSessionFilePathAsync(point.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The session to fork no longer exists.");
        var targetPath = _layout.GetSessionFilePath(summary.SessionId, summary.CreatedAt);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"Path '{targetPath}' did not resolve to a parent directory.");
        Directory.CreateDirectory(targetDirectory);
        var temporary = targetPath + ".fork-" + Guid.NewGuid().ToString("N");
        var targetAttachments = Path.ChangeExtension(targetPath, ".attachments");
        var createdAttachments = false;
        AgentJournalBranchWriter.Result result;
        try
        {
            await using (var source = await OpenReadStreamAsync(sourcePath, cancellationToken).ConfigureAwait(false))
            {
                var end = includeRemainder ? point.EndOffset : point.CutOffset;
                if (source.Length < end || !await HasGuardAsync(source, point, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The session changed before it could be forked.");
                var probe = new byte[3];
                source.Position = 0;
                var start = await source.ReadAtLeastAsync(probe, probe.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) == 3
                    && probe[0] == 0xef && probe[1] == 0xbb && probe[2] == 0xbf ? 3 : 0;
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, useAsync: true);
                if (header is not null)
                {
                    await output.WriteAsync(Utf8WithoutBom.GetBytes(header + "\n"), cancellationToken).ConfigureAwait(false);
                }

                result = await AgentJournalBranchWriter.CopyAsync(source, start, end, output, point.SessionId, summary.SessionId,
                    AttachmentMoveOf(sourcePath, targetPath), cancellationToken).ConfigureAwait(false);
                var snapshots = new List<string>(additionalSnapshots.Count + 2)
                {
                    new AgentRawEvent(summary.ProviderId, summary.SessionId, summary.UpdatedAt, SessionSummaryEventType,
                        JsonSerializer.SerializeToElement(summary, AgentJsonSerializerContext.Default.AgentSessionSummary)).ToJson(),
                    new AgentRawEvent(summary.ProviderId, summary.SessionId, state.UpdatedAt, SessionStateEventType,
                        JsonSerializer.SerializeToElement(state, AgentJsonSerializerContext.Default.AgentSessionState)).ToJson(),
                };
                snapshots.AddRange(additionalSnapshots);
                foreach (var snapshot in snapshots)
                {
                    await output.WriteAsync(Utf8WithoutBom.GetBytes(snapshot + "\n"), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            // A copy of a file is a clone where the filesystem has them (APFS, ReFS, Btrfs): it takes no room.
            var sourceAttachments = Path.ChangeExtension(sourcePath, ".attachments");
            foreach (var name in result.AttachmentFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = Path.Combine(sourceAttachments, name);
                if (!File.Exists(file)) continue;
                if (!createdAttachments)
                {
                    Directory.CreateDirectory(targetAttachments);
                    createdAttachments = true;
                }

                File.Copy(file, Path.Combine(targetAttachments, name), overwrite: false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _journalFile.WithPathLockAsync(targetPath, () =>
            {
                // Nothing fallible follows the rename: the new session exists from here on.
                File.Move(temporary, targetPath, overwrite: false);
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (createdAttachments && !File.Exists(targetPath) && Directory.Exists(targetAttachments)) Directory.Delete(targetAttachments, recursive: true);
            throw;
        }

        _sessionFiles[summary.SessionId] = targetPath;
        await UpsertCacheFromFileAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
        await ReindexAsync(targetPath, CancellationToken.None).ConfigureAwait(false);
        return result.Records;
    }

    /// <summary>Removes from the journal of a session what follows a point, and writes its summary and state again.</summary>
    /// <param name="point">The point of the session.</param>
    /// <param name="summary">The summary of the session after the rewind.</param>
    /// <param name="state">The state of the session after the rewind.</param>
    /// <param name="header">The first line of the journal after the rewind; null to keep the one it has.</param>
    /// <param name="additionalSnapshots">Records written after the summary and the state.</param>
    /// <param name="cancellationToken">Cancels before the commit point.</param>
    /// <remarks>
    /// The records before the point are copied to a file beside the journal, which is completed and then renamed over
    /// the journal: no byte of the journal changes before that rename, and a failure before it leaves the session as it
    /// was. A reader that follows a journal from an offset it remembers recognizes a journal by its first line: the
    /// caller gives a first line that differs from the one before, so that such a reader starts again instead of
    /// reading on from an offset that is no longer the start of a record. The caller holds the idle-session mutation
    /// admission.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The session changed since the point was read.</exception>
    /// <exception cref="InvalidDataException">The first line to replace is oversized.</exception>
    /// <exception cref="IOException">Preparing or replacing the journal failed.</exception>
    /// <exception cref="OperationCanceledException">Canceled before the commit point.</exception>
    internal async Task CommitRewindAsync(AgentSessionBranchPoint point, AgentSessionSummary summary, AgentSessionState state,
        string? header, IReadOnlyList<string> additionalSnapshots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(additionalSnapshots);
        if (summary.SessionId != point.SessionId || state.SessionId != point.SessionId)
            throw new ArgumentException("A rewind keeps the session identifier.", nameof(summary));
        var path = await TryGetSessionFilePathAsync(point.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The session to rewind no longer exists.");
        await _journalFile.WithPathLockAsync(path, async () =>
        {
            var temporary = path + ".rewind-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 1, useAsync: true))
                {
                    if (GetFileStamp(path) is not { } stamp || stamp.Length != point.Revision.Length
                        || stamp.LastWriteTimeUtc.Ticks != point.Revision.LastWriteUtcTicks
                        || source.Length != point.Revision.Length || !await HasGuardAsync(source, point, cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("The session changed before it could be rewound.");
                    await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, useAsync: true);
                    var start = 0L;
                    if (header is not null)
                    {
                        start = await FindFirstLineEndAsync(source, point.CutOffset, cancellationToken).ConfigureAwait(false);
                        await output.WriteAsync(Utf8WithoutBom.GetBytes(header + "\n"), cancellationToken).ConfigureAwait(false);
                    }

                    await CopyRangeAsync(source, start, point.CutOffset, output, cancellationToken).ConfigureAwait(false);
                    var snapshots = new List<string>(additionalSnapshots.Count + 2)
                    {
                        new AgentRawEvent(summary.ProviderId, summary.SessionId, summary.UpdatedAt, SessionSummaryEventType,
                            JsonSerializer.SerializeToElement(summary, AgentJsonSerializerContext.Default.AgentSessionSummary)).ToJson(),
                        new AgentRawEvent(summary.ProviderId, summary.SessionId, state.UpdatedAt, SessionStateEventType,
                            JsonSerializer.SerializeToElement(state, AgentJsonSerializerContext.Default.AgentSessionState)).ToJson(),
                    };
                    snapshots.AddRange(additionalSnapshots);
                    foreach (var snapshot in snapshots)
                    {
                        await output.WriteAsync(Utf8WithoutBom.GetBytes(snapshot + "\n"), cancellationToken).ConfigureAwait(false);
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                // The read handle is closed: a replacement on Windows needs it to be. The shared journal gate is still
                // held; a path that something else changed meanwhile is refused immediately before the rename.
                if (GetFileStamp(path) is not { } before || before.Length != point.Revision.Length
                    || before.LastWriteTimeUtc.Ticks != point.Revision.LastWriteUtcTicks)
                    throw new InvalidOperationException("The session changed before journal replacement.");
                File.Move(temporary, path, overwrite: true);
                // Nothing fallible follows the durable decision; caches reproject from the changed stamp.
                _journalFile.ForgetLatestNotes(path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellationToken).ConfigureAwait(false);
        InvalidateMetadataProjectionCache(path);
        await UpsertCacheFromFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        await ReindexAsync(path, CancellationToken.None).ConfigureAwait(false);
    }

    // The offset after the first line of a journal, which a new first line replaces.
    private static async Task<long> FindFirstLineEndAsync(Stream source, long limit, CancellationToken cancellationToken)
    {
        const int MaximumHeaderBytes = 128 * 1024;
        var buffer = new byte[4096];
        source.Position = 0;
        var position = 0L;
        while (position < limit)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - position)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var newline = buffer.AsSpan(0, read).IndexOf((byte)'\n');
            if (newline >= 0) return position + newline + 1;
            position += read;
            if (position > MaximumHeaderBytes) throw new InvalidDataException("Session header is oversized.");
        }

        // The point is within the first line: nothing of the journal but that line is kept.
        return limit;
    }

    private static async Task CopyRangeAsync(Stream source, long start, long end, Stream output, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            source.Position = start;
            var remaining = end - start;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new InvalidOperationException("The session changed before it could be rewound.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<TurnScan> ScanTurnsAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var path = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentSessionHistoryException("missing_session");
        return await AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path, async (contained, token) =>
        {
            // The index gives the turns without reading the journal, once it covers the journal as it is.
            if (JournalIndex is { } index && await ScanIndexedTurnsAsync(index, sessionId, contained, token).ConfigureAwait(false) is { } indexed)
            {
                return indexed;
            }

            // The gate only orders this read after admitted writes: the scan stops at the length captured here.
            var stamp = await _journalFile.WithPathLockAsync(contained, () => Task.FromResult(GetFileStamp(contained)), token).ConfigureAwait(false)
                ?? throw new AgentSessionHistoryException("missing_session");
            await using var stream = await OpenReadStreamAsync(contained, token).ConfigureAwait(false);
            var result = await AgentJournalTurnReader.ScanAsync(stream, stamp.Length, token).ConfigureAwait(false);
            if (!result.Supported) throw new AgentSessionHistoryException("unsupported_format");
            return new TurnScan(contained, new AgentHistoryRevision(sessionId, stamp.Length, stamp.LastWriteTimeUtc.Ticks), result);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentSessionState?> ReadStateSnapshotAsync(Stream stream, AgentJournalTurnReader.Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Exists)
        {
            return null;
        }

        var bytes = new byte[snapshot.Length];
        stream.Position = snapshot.Offset;
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(bytes.AsSpan().TrimEnd("\r\n"u8), AgentJsonSerializerContext.Default.AgentEvent) is AgentRawEvent raw
                ? raw.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentSessionState)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> HasGuardAsync(Stream stream, AgentSessionBranchPoint point, CancellationToken cancellationToken)
    {
        var guard = new byte[point.Guard.Length];
        stream.Position = point.CutOffset - guard.Length;
        return await stream.ReadAtLeastAsync(guard, guard.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) == guard.Length
            && guard.AsSpan().SequenceEqual(point.Guard);
    }

    // Null for a journal that is not in the date folders the layout gives: its records then keep their paths.
    private static AgentJournalBranchWriter.AttachmentMove? AttachmentMoveOf(string sourcePath, string targetPath)
        => ShardOf(sourcePath) is { } source && ShardOf(targetPath) is { } target ? new(source, target) : null;

    // The date folders of a journal: year, month and day.
    private static (string Year, string Month, string Day)? ShardOf(string journalPath)
    {
        var day = Path.GetDirectoryName(journalPath);
        var month = Path.GetDirectoryName(day);
        var year = Path.GetDirectoryName(month);
        (string Year, string Month, string Day) shard = (Path.GetFileName(year) ?? string.Empty, Path.GetFileName(month) ?? string.Empty, Path.GetFileName(day) ?? string.Empty);
        return shard.Year.Length == 4 && shard.Month.Length == 2 && shard.Day.Length == 2
            && (shard.Year + shard.Month + shard.Day).All(char.IsAsciiDigit) ? shard : null;
    }

    private sealed record TurnScan(string Path, AgentHistoryRevision Revision, AgentJournalTurnReader.Result Result);
}
