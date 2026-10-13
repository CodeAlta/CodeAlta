namespace CodeAlta.Catalog.Bookmarks;

/// <summary>The two kinds of bookmarks.</summary>
public static class BookmarkKinds
{
    /// <summary>A bookmark of a whole session.</summary>
    public const string Session = "session";

    /// <summary>A bookmark of one message of a session.</summary>
    public const string Message = "message";
}

/// <summary>Who wrote a bookmarked message.</summary>
public static class BookmarkRoles
{
    /// <summary>A prompt of the user.</summary>
    public const string User = "user";

    /// <summary>A message of the assistant.</summary>
    public const string Assistant = "assistant";

    /// <summary>Gets the role a text names, in any case.</summary>
    /// <param name="value">A text such as <c>user</c> or <c>Assistant</c>.</param>
    /// <returns><see cref="User"/> or <see cref="Assistant"/>; <see langword="null"/> for anything else.</returns>
    public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        User => User,
        Assistant => Assistant,
        _ => null,
    };
}

/// <summary>
/// A session, or one message of a session, that the user or an agent marked to find it again.
/// </summary>
/// <param name="Id">The id of the bookmark.</param>
/// <param name="Kind"><see cref="BookmarkKinds.Session"/> or <see cref="BookmarkKinds.Message"/>.</param>
/// <param name="SessionId">The session.</param>
/// <param name="SessionTitle">The title of the session when it was last seen.</param>
/// <param name="ProjectId">The project of the session when it was last seen; <see langword="null"/> for a session of no project.</param>
/// <param name="Role"><see cref="BookmarkRoles.User"/> or <see cref="BookmarkRoles.Assistant"/>, for a message.</param>
/// <param name="Label">The name the user gave the bookmark; <see langword="null"/> when it has none.</param>
/// <param name="Excerpt">The beginning of the message, on one line; <see langword="null"/> for a session.</param>
/// <param name="RunId">The run of the message, as its journal writes it; <see langword="null"/> for a session, or for a message that names no run.</param>
/// <param name="ContentId">The content identifier of the message, as its journal writes it; <see langword="null"/> for a session.</param>
/// <param name="Offset">
/// The byte offset the journal held the message at when it was last found. It is where to look first, never what
/// names the message: the run and the content identifier do.
/// </param>
/// <param name="MessageAt">When the message was written.</param>
/// <param name="CreatedAt">When the bookmark was added.</param>
/// <param name="CreatedBy">The session whose agent added the bookmark; <see langword="null"/> when the user did.</param>
public sealed record Bookmark(
    string Id,
    string Kind,
    string SessionId,
    string SessionTitle,
    string? ProjectId,
    string? Role,
    string? Label,
    string? Excerpt,
    string? RunId,
    string? ContentId,
    long? Offset,
    DateTimeOffset? MessageAt,
    DateTimeOffset CreatedAt,
    string? CreatedBy);

/// <summary>The session a bookmark is added on, as the host that adds it sees it.</summary>
/// <param name="SessionId">The session, which the host found to exist.</param>
/// <param name="Title">The title of the session.</param>
/// <param name="ProjectId">The project of the session; <see langword="null"/> for a session of no project.</param>
public sealed record BookmarkTarget(string SessionId, string Title, string? ProjectId);

/// <summary>How a request about a bookmark ended.</summary>
public enum BookmarkStatus
{
    /// <summary>The request was carried out.</summary>
    Ok,

    /// <summary>There is no such bookmark.</summary>
    NotFound,

    /// <summary>A value of the request is not one a bookmark can have.</summary>
    Invalid,

    /// <summary>The session of the bookmark has no journal any more.</summary>
    SessionMissing,

    /// <summary>What was named is not a completed message of the user or of the assistant.</summary>
    NotBookmarkable,

    /// <summary>The session no longer holds the message: it was rewound past it.</summary>
    Gone,

    /// <summary>There are as many bookmarks as are kept.</summary>
    Limit,
}

/// <summary>The outcome of adding, naming or removing a bookmark.</summary>
/// <param name="Status">How the request ended.</param>
/// <param name="Bookmark">The bookmark as it is after the request, or the one that was removed.</param>
public sealed record BookmarkResult(BookmarkStatus Status, Bookmark? Bookmark)
{
    /// <summary>Gets a value indicating whether the request added a bookmark: false when what it names was bookmarked already.</summary>
    public bool Created { get; init; }
}

/// <summary>Where a bookmark leads now.</summary>
/// <param name="Status">
/// <see cref="BookmarkStatus.Ok"/>, <see cref="BookmarkStatus.NotFound"/>, <see cref="BookmarkStatus.SessionMissing"/> or
/// <see cref="BookmarkStatus.Gone"/>.
/// </param>
/// <param name="Bookmark">The bookmark.</param>
/// <param name="Offset">The offset the journal holds the message at now; <see langword="null"/> for a bookmark of a session.</param>
public sealed record BookmarkLocation(BookmarkStatus Status, Bookmark? Bookmark, long? Offset);

/// <summary>A bookmark with the text of its message, read from the journal of its session.</summary>
/// <param name="Status">As for <see cref="BookmarkLocation"/>.</param>
/// <param name="Bookmark">The bookmark.</param>
/// <param name="Offset">The offset the journal holds the message at now.</param>
/// <param name="Text">The text of the message, or its beginning; <see langword="null"/> for a session, or when the message was not found.</param>
/// <param name="Truncated">Whether the message is longer than <paramref name="Text"/>.</param>
public sealed record BookmarkText(BookmarkStatus Status, Bookmark? Bookmark, long? Offset, string? Text, bool Truncated);

/// <summary>A completed message of the user or of the assistant, as the journal of a session holds it.</summary>
/// <param name="RunId">The run of the message; <see langword="null"/> when it names none.</param>
/// <param name="ContentId">The content identifier of the message.</param>
/// <param name="Role"><see cref="BookmarkRoles.User"/> or <see cref="BookmarkRoles.Assistant"/>.</param>
/// <param name="Timestamp">When the message was written.</param>
/// <param name="Offset">The byte offset of its record in the journal.</param>
/// <param name="Text">The text of the message.</param>
public sealed record BookmarkMessage(string? RunId, string ContentId, string Role, DateTimeOffset Timestamp, long Offset, string Text);

/// <summary>What a journal answered when it was asked for a message.</summary>
public enum BookmarkJournalStatus
{
    /// <summary>The journal holds the message.</summary>
    Found,

    /// <summary>The journal does not hold such a message.</summary>
    NotFound,

    /// <summary>The session has no journal.</summary>
    SessionMissing,
}

/// <summary>The answer of a journal that was asked for a message.</summary>
/// <param name="Status">What the journal answered.</param>
/// <param name="Message">The message, when it was found.</param>
public sealed record BookmarkJournalRead(BookmarkJournalStatus Status, BookmarkMessage? Message);
