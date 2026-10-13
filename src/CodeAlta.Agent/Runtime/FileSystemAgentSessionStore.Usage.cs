namespace CodeAlta.Agent.Runtime;

public sealed partial class FileSystemAgentSessionStore
{
    /// <summary>
    /// Reads the usage of every request to the model that the journal of a session records: its
    /// <see cref="AgentSessionUpdateKind.UsageUpdated"/> records that report an operation.
    /// </summary>
    /// <param name="sessionId">An existing session identifier, never a path.</param>
    /// <param name="cancellationToken">Cancels lookup, gate admission or reading.</param>
    /// <returns>The usage updates in journal order; empty when the journal records none.</returns>
    /// <remarks>
    /// The journal is read once, from its start to the length it has when the read starts: what a run appends in the
    /// meantime is not read, and no revision is checked, because a journal only grows. The other records are known
    /// by the start of their line and are never loaded, whatever their size. The journal gate orders the read after
    /// admitted writes and is not held while reading.
    /// <para>
    /// The list is all the usage or nothing: a usage record that cannot be read fails the read, so that a sum of the
    /// list is never a part taken for the whole.
    /// </para>
    /// <para>
    /// A session made by a fork or a rewind starts with records copied from its source, which are the usage of the
    /// source: they are left out, as the statistics leave them out (<see cref="AgentSessionForkOrigin.CountedFrom"/>).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The session identifier is blank.</exception>
    /// <exception cref="AgentSessionHistoryException">
    /// The session is missing (<c>missing_session</c>), its journal is outside the sessions root
    /// (<c>outside_root</c>) or is not UTF-8 (<c>unsupported_format</c>), or a usage record is larger than 8 MiB
    /// (<c>record_too_large</c>) or is not one the serializer wrote (<c>corrupt_record</c>).
    /// </exception>
    /// <exception cref="IOException">Journal access fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<IReadOnlyList<AgentSessionUsageUpdate>> ReadUsageUpdatesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var path = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentSessionHistoryException("missing_session");
        var countedFrom = (await GetSessionSummaryAsync(sessionId, cancellationToken).ConfigureAwait(false))?.ForkedFrom?.CountedFrom;
        return await AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path, async (contained, token) =>
        {
            // The gate only orders this read after admitted writes: the scan stops at the length captured here.
            var stamp = await _journalFile.WithPathLockAsync(contained, () => Task.FromResult(GetFileStamp(contained)), token).ConfigureAwait(false)
                ?? throw new AgentSessionHistoryException("missing_session");
            await using var stream = await OpenReadStreamAsync(contained, token).ConfigureAwait(false);
            return await AgentJournalUsageReader.ReadAsync(stream, stamp.Length, countedFrom, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
