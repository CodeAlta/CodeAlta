namespace CodeAlta.Agent.Runtime;

public sealed partial class FileSystemAgentSessionStore
{
    /// <summary>
    /// Reads the system prompt record that starts at a journal offset, as a page entry reported it, with the
    /// system prompt record that precedes it in the journal: the prompt it replaced.
    /// </summary>
    /// <param name="sessionId">An existing session identifier, never a path.</param>
    /// <param name="offset">Starting byte offset of the record.</param>
    /// <param name="cancellationToken">Cancels lookup, gate admission or reading.</param>
    /// <returns>The record and, unless it is the first prompt of the session, the one before it.</returns>
    /// <remarks>
    /// The record before is found by one pass over the journal up to <paramref name="offset"/>, which knows the
    /// other records by the start of their line and never loads them, whatever their size. No revision is checked,
    /// because a journal only grows. The journal gate orders the read after admitted writes and is not held while
    /// reading.
    /// </remarks>
    /// <exception cref="ArgumentException">The session identifier is blank.</exception>
    /// <exception cref="AgentSessionHistoryException">
    /// The session is missing (<c>missing_session</c>), its journal is outside the sessions root
    /// (<c>outside_root</c>), the offset does not start a system prompt record (<c>invalid_cursor</c>), or the
    /// record is larger than 8 MiB (<c>record_too_large</c>) or not readable (<c>unsupported_format</c>,
    /// <c>corrupt_record</c>).
    /// </exception>
    /// <exception cref="IOException">Journal access fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<AgentSystemPromptRecord> ReadSystemPromptAsync(string sessionId, long offset, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var path = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentSessionHistoryException("missing_session");
        return await AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path, async (contained, token) =>
        {
            // The gate only orders this read after admitted writes: the record and those before it do not change.
            _ = await _journalFile.WithPathLockAsync(contained, () => Task.FromResult(GetFileStamp(contained)), token).ConfigureAwait(false)
                ?? throw new AgentSessionHistoryException("missing_session");
            await using var stream = await OpenReadStreamAsync(contained, token).ConfigureAwait(false);
            return await AgentJournalHistoryReader.ReadSystemPromptAsync(stream, offset, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
