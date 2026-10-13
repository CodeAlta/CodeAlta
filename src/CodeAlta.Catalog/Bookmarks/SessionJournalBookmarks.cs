using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Bookmarks;

/// <summary>Reads the messages bookmarks point to from the journals of a session store.</summary>
public sealed class SessionJournalBookmarks : IBookmarkJournal
{
    private readonly FileSystemAgentSessionStore _store;

    /// <summary>Initializes a new instance of the <see cref="SessionJournalBookmarks"/> class.</summary>
    /// <param name="store">The store of the sessions, which shares the journal locks of its host.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public SessionJournalBookmarks(FileSystemAgentSessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<BookmarkJournalRead> ReadAsync(string sessionId, long offset, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        AgentEvent? record;
        try
        {
            record = await _store.ReadHistoryRecordAsync(sessionId, offset, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentSessionHistoryException exception)
        {
            // An offset that is no record boundary, or a record that cannot be read, is not a message.
            return new(exception.Code == "missing_session" ? BookmarkJournalStatus.SessionMissing : BookmarkJournalStatus.NotFound, null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(BookmarkJournalStatus.SessionMissing, null);
        }

        return MessageOf(record, offset) is { } message ? new(BookmarkJournalStatus.Found, message) : new(BookmarkJournalStatus.NotFound, null);
    }

    /// <inheritdoc />
    public async ValueTask<BookmarkJournalRead> FindAsync(string sessionId, string? runId, string contentId, string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        return await FindCoreAsync(sessionId, runId, KindOf(role), contentId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<BookmarkJournalRead> FindInTurnAsync(string sessionId, string turn, string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn);
        var kind = KindOf(role);
        AgentSessionTurns turns;
        try
        {
            turns = await _store.ReadTurnsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentSessionHistoryException exception) when (exception.Code == "missing_session")
        {
            return new(BookmarkJournalStatus.SessionMissing, null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(BookmarkJournalStatus.SessionMissing, null);
        }

        // A number names a turn by its position; anything else is the identifier of a run.
        var number = int.TryParse(turn, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var found = turns.Turns.FirstOrDefault(candidate => number > 0 ? candidate.Number == number : string.Equals(candidate.RunId, turn, StringComparison.Ordinal));
        if (found is null)
        {
            return new(BookmarkJournalStatus.NotFound, null);
        }

        if (kind == AgentContentKind.Assistant)
        {
            return await FindCoreAsync(sessionId, found.RunId, kind, contentId: null, cancellationToken).ConfigureAwait(false);
        }

        if (found.PromptOffset is not { } offset)
        {
            return new(BookmarkJournalStatus.NotFound, null);
        }

        var prompt = await ReadAsync(sessionId, offset, cancellationToken).ConfigureAwait(false);
        return prompt.Message is { Role: not BookmarkRoles.User } ? new(BookmarkJournalStatus.NotFound, null) : prompt;
    }

    private async ValueTask<BookmarkJournalRead> FindCoreAsync(string sessionId, string? runId, AgentContentKind kind, string? contentId, CancellationToken cancellationToken)
    {
        AgentSessionHistoryEntry? entry;
        try
        {
            entry = await _store.FindContentRecordAsync(sessionId, runId, kind, contentId, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentSessionHistoryException exception) when (exception.Code == "missing_session")
        {
            return new(BookmarkJournalStatus.SessionMissing, null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(BookmarkJournalStatus.SessionMissing, null);
        }

        return entry is not null && MessageOf(entry.Event, entry.Offset) is { } message
            ? new(BookmarkJournalStatus.Found, message)
            : new(BookmarkJournalStatus.NotFound, null);
    }

    private static AgentContentKind KindOf(string role) => role switch
    {
        BookmarkRoles.User => AgentContentKind.User,
        BookmarkRoles.Assistant => AgentContentKind.Assistant,
        _ => throw new ArgumentException("The role is 'user' or 'assistant'.", nameof(role)),
    };

    private static BookmarkMessage? MessageOf(AgentEvent? record, long offset)
        => record is AgentContentCompletedEvent { Kind: AgentContentKind.User or AgentContentKind.Assistant, ContentId.Length: > 0 } message
            ? new(message.RunId?.Value, message.ContentId, message.Kind == AgentContentKind.User ? BookmarkRoles.User : BookmarkRoles.Assistant,
                message.Timestamp, offset, message.Content ?? string.Empty)
            : null;
}
