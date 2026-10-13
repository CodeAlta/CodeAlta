namespace CodeAlta.Catalog.Bookmarks;

/// <summary>
/// Reads the messages bookmarks point to from the journals of the sessions. A message is named by its run and its
/// content identifier; the byte offset of its record only says where it was.
/// </summary>
public interface IBookmarkJournal
{
    /// <summary>Reads the message the journal of a session holds at an offset.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="offset">The byte offset of a record of its journal.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The message; <see cref="BookmarkJournalStatus.NotFound"/> when the offset is not the start of a completed
    /// message of the user or of the assistant.
    /// </returns>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    ValueTask<BookmarkJournalRead> ReadAsync(string sessionId, long offset, CancellationToken cancellationToken = default);

    /// <summary>Finds a message in the journal of a session by what names it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="runId">The run of the message; <see langword="null"/> for a message that names none.</param>
    /// <param name="contentId">The content identifier of the message.</param>
    /// <param name="role"><see cref="BookmarkRoles.User"/> or <see cref="BookmarkRoles.Assistant"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The message with the offset its journal holds it at now.</returns>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    ValueTask<BookmarkJournalRead> FindAsync(string sessionId, string? runId, string contentId, string role, CancellationToken cancellationToken = default);

    /// <summary>Finds the message of a turn of a session: its prompt, or the last message of the assistant.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="turn">The number of the turn, from 1, or the identifier of its run.</param>
    /// <param name="role"><see cref="BookmarkRoles.User"/> for the prompt of the turn, <see cref="BookmarkRoles.Assistant"/> for its last message of the assistant.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The message; <see cref="BookmarkJournalStatus.NotFound"/> when the session has no such turn, or the turn no such message.</returns>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    ValueTask<BookmarkJournalRead> FindInTurnAsync(string sessionId, string turn, string role, CancellationToken cancellationToken = default);
}
