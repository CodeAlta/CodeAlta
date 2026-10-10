namespace CodeAlta.Agent;

/// <summary>
/// One journal file of the session store: where it is, how long it is and when it last changed.
/// </summary>
/// <param name="SessionId">The identifier of the session, which is the name of its file.</param>
/// <param name="Path">The full path of the journal file.</param>
/// <param name="Length">The length of the file in bytes when it was listed.</param>
/// <param name="LastWriteUtc">The time the file last changed, in UTC, when it was listed.</param>
public sealed record SessionJournalFile(string SessionId, string Path, long Length, DateTimeOffset LastWriteUtc);

/// <summary>
/// Lists the journal files of the session store and opens them for reading, for readers that follow the journals from an offset
/// (such as the statistics of the history) and must not hold the lock of a session.
/// </summary>
/// <remarks>
/// The catalog lists <b>every</b> journal under the sessions folder of the store, whatever the session is: it is not the list the
/// Explorer shows, which hides the sessions of a project that was removed. The files are opened for reading with sharing that lets
/// the session keep writing and lets its file be replaced or deleted, and no in-process lock of the session is taken: a reader
/// sees a file that may end in a line that is not complete, and stops at the last line end it has seen.
/// </remarks>
public interface ISessionJournalCatalog
{
    /// <summary>Lists the journal files of the store, the most recently changed first.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The journal files, as they were when the folder was read.</returns>
    IAsyncEnumerable<SessionJournalFile> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the journal file of one session.</summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file; <see langword="null"/> when the store has no journal for the session.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    ValueTask<SessionJournalFile?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Opens the journal of a session for reading, positioned at an offset.</summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="offset">The offset of the first byte to read: 0 for the start, or the end of the last line that was read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A readable, seekable stream that the session can write to meanwhile; <see langword="null"/> when the journal does not exist (any more).</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    ValueTask<Stream?> OpenAsync(string sessionId, long offset = 0, CancellationToken cancellationToken = default);
}
