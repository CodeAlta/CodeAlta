using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog.Bookmarks;

/// <summary>
/// The bookmarks of one CodeAlta instance: the sessions, and the messages of sessions, that the user or an agent
/// marked to find them again. They are kept in the application database, and a message is found again in the
/// journal of its session by its run and its content identifier.
/// </summary>
/// <remarks>
/// The service does not know which sessions exist: the host that adds a bookmark names the session it found, and
/// decides what a session that is gone means. Every method can be called from any thread.
/// </remarks>
public sealed class BookmarkService
{
    /// <summary>The most bookmarks that are kept.</summary>
    public const int MaximumBookmarks = 2000;

    /// <summary>The most characters of the name of a bookmark.</summary>
    public const int MaximumLabelLength = 200;

    /// <summary>The most characters of the excerpt of a message.</summary>
    public const int MaximumExcerptLength = 280;

    /// <summary>The most characters of the title of a session that is kept with a bookmark.</summary>
    public const int MaximumTitleLength = 256;

    private const int MaximumIdentifierLength = 256;

    // Providers report content identifiers of several hundred characters: the timeline shows them in a compact form.
    private const int MaximumContentIdentifierLength = 4096;

    private readonly BookmarkStore _store;
    private readonly IBookmarkJournal _journal;
    private readonly TimeProvider _clock;
    private readonly int _maximum;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The bookmarks as the database holds them, read once and again when the file of the database was replaced.
    private List<Bookmark>? _bookmarks;
    private int _generation = -1;
    private IReadOnlyCollection<string>? _sessionIds;

    /// <summary>Initializes a new instance of the <see cref="BookmarkService"/> class.</summary>
    /// <param name="database">The application database of the instance.</param>
    /// <param name="journal">What reads the bookmarked messages from the journals of the sessions.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public BookmarkService(IApplicationDatabase database, IBookmarkJournal journal)
        : this(database, journal, TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="BookmarkService"/> class with a clock.</summary>
    /// <param name="database">The application database of the instance.</param>
    /// <param name="journal">What reads the bookmarked messages from the journals of the sessions.</param>
    /// <param name="clock">The clock that dates the bookmarks.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public BookmarkService(IApplicationDatabase database, IBookmarkJournal journal, TimeProvider clock)
        : this(database, journal, clock, MaximumBookmarks)
    {
    }

    // The most bookmarks is a parameter for the tests of the limit.
    internal BookmarkService(IApplicationDatabase database, IBookmarkJournal journal, TimeProvider clock, int maximum)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(clock);
        _store = new BookmarkStore(database);
        _journal = journal;
        _clock = clock;
        _maximum = maximum;
    }

    /// <summary>Raised after the bookmarks changed, on the thread that changed them.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the sessions that have a bookmark, of the session or of one of its messages, as they were last read;
    /// <see langword="null"/> until the bookmarks were read once.
    /// </summary>
    public IReadOnlyCollection<string>? BookmarkedSessionIds => Volatile.Read(ref _sessionIds);

    /// <summary>Lists every bookmark, the newest first.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bookmarks.</returns>
    /// <exception cref="ApplicationDatabaseNewerVersionException">The bookmarks were written by a newer build.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be read.</exception>
    public async ValueTask<IReadOnlyList<Bookmark>> ListAsync(CancellationToken cancellationToken = default)
        => await RunAsync(static (bookmarks, _) => ValueTask.FromResult<(IReadOnlyList<Bookmark>, bool)>((bookmarks.ToArray(), false)), cancellationToken).ConfigureAwait(false);

    /// <summary>Gets one bookmark.</summary>
    /// <param name="id">The id of the bookmark.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bookmark; <see langword="null"/> when there is none with this id.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be read.</exception>
    public async ValueTask<Bookmark?> GetAsync(string id, CancellationToken cancellationToken = default)
        => await RunAsync((bookmarks, _) => ValueTask.FromResult<(Bookmark?, bool)>((Find(bookmarks, id), false)), cancellationToken).ConfigureAwait(false);

    /// <summary>Bookmarks a session. A session that is bookmarked already answers with the bookmark it has.</summary>
    /// <param name="target">The session, which the caller found to exist.</param>
    /// <param name="label">The name of the bookmark; <see langword="null"/> or empty for none.</param>
    /// <param name="createdBy">The session whose agent adds the bookmark; <see langword="null"/> when the user does.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// <see cref="BookmarkStatus.Ok"/> with the bookmark, <see cref="BookmarkStatus.Invalid"/> for a value a bookmark
    /// cannot have, or <see cref="BookmarkStatus.Limit"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be written.</exception>
    public async ValueTask<BookmarkResult> AddSessionAsync(BookmarkTarget target, string? label, string? createdBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ValidTarget(target) || !TryNormalizeLabel(label, out var name) || !ValidCreator(createdBy))
        {
            return new(BookmarkStatus.Invalid, null);
        }

        return await AddAsync(target, name, createdBy, message: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bookmarks the message the journal of a session holds at an offset. A message that is bookmarked already
    /// answers with the bookmark it has.
    /// </summary>
    /// <param name="target">The session of the message, which the caller found to exist.</param>
    /// <param name="offset">The byte offset of the record of the message in the journal of the session.</param>
    /// <param name="label">The name of the bookmark; <see langword="null"/> or empty for none.</param>
    /// <param name="createdBy">The session whose agent adds the bookmark; <see langword="null"/> when the user does.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// <see cref="BookmarkStatus.Ok"/> with the bookmark, <see cref="BookmarkStatus.NotBookmarkable"/> when the offset
    /// is not one of a completed message of the user or of the assistant, <see cref="BookmarkStatus.SessionMissing"/>
    /// when the session has no journal, <see cref="BookmarkStatus.Invalid"/> or <see cref="BookmarkStatus.Limit"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be written.</exception>
    public async ValueTask<BookmarkResult> AddMessageAsync(BookmarkTarget target, long offset, string? label, string? createdBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (offset < 0 || !ValidTarget(target) || !TryNormalizeLabel(label, out var name) || !ValidCreator(createdBy))
        {
            return new(BookmarkStatus.Invalid, null);
        }

        var read = await _journal.ReadAsync(target.SessionId, offset, cancellationToken).ConfigureAwait(false);
        return await AddReadAsync(target, name, createdBy, read, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bookmarks a message of a turn of a session: its prompt, or the last message of the assistant. A message that
    /// is bookmarked already answers with the bookmark it has.
    /// </summary>
    /// <param name="target">The session of the message, which the caller found to exist.</param>
    /// <param name="turn">The number of the turn, from 1, or the identifier of its run.</param>
    /// <param name="role"><see cref="BookmarkRoles.User"/> for the prompt of the turn, <see cref="BookmarkRoles.Assistant"/> for its last message of the assistant.</param>
    /// <param name="label">The name of the bookmark; <see langword="null"/> or empty for none.</param>
    /// <param name="createdBy">The session whose agent adds the bookmark; <see langword="null"/> when the user does.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>As <see cref="AddMessageAsync"/>; <see cref="BookmarkStatus.NotBookmarkable"/> when the session has no such turn, or the turn no such message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be written.</exception>
    public async ValueTask<BookmarkResult> AddTurnMessageAsync(BookmarkTarget target, string turn, string role, string? label, string? createdBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ValidIdentifier(turn) || BookmarkRoles.Parse(role) is not { } wanted || !ValidTarget(target) || !TryNormalizeLabel(label, out var name) || !ValidCreator(createdBy))
        {
            return new(BookmarkStatus.Invalid, null);
        }

        var read = await _journal.FindInTurnAsync(target.SessionId, turn.Trim(), wanted, cancellationToken).ConfigureAwait(false);
        return await AddReadAsync(target, name, createdBy, read, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives a bookmark the name the user chose, or takes its name away.</summary>
    /// <param name="id">The id of the bookmark.</param>
    /// <param name="label">The name; <see langword="null"/> or empty for none.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see cref="BookmarkStatus.Ok"/> with the bookmark, <see cref="BookmarkStatus.NotFound"/> or <see cref="BookmarkStatus.Invalid"/>.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be written.</exception>
    public async ValueTask<BookmarkResult> RenameAsync(string id, string? label, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeLabel(label, out var name))
        {
            return new(BookmarkStatus.Invalid, null);
        }

        return await RunAsync<BookmarkResult>(async (bookmarks, token) =>
        {
            var index = IndexOf(bookmarks, id);
            if (index < 0)
            {
                return (new(BookmarkStatus.NotFound, null), false);
            }

            var bookmark = bookmarks[index];
            if (string.Equals(bookmark.Label, name, StringComparison.Ordinal))
            {
                return (new(BookmarkStatus.Ok, bookmark), false);
            }

            await _store.UpdateLabelAsync(bookmark.Id, name, token).ConfigureAwait(false);
            bookmarks[index] = bookmark = bookmark with { Label = name };
            return (new(BookmarkStatus.Ok, bookmark), true);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a bookmark.</summary>
    /// <param name="id">The id of the bookmark.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see cref="BookmarkStatus.Ok"/> with the bookmark that was removed, or <see cref="BookmarkStatus.NotFound"/>.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be written.</exception>
    public async ValueTask<BookmarkResult> RemoveAsync(string id, CancellationToken cancellationToken = default)
        => await RunAsync<BookmarkResult>(async (bookmarks, token) =>
        {
            var index = IndexOf(bookmarks, id);
            if (index < 0)
            {
                return (new(BookmarkStatus.NotFound, null), false);
            }

            var bookmark = bookmarks[index];
            await _store.DeleteAsync(bookmark.Id, token).ConfigureAwait(false);
            bookmarks.RemoveAt(index);
            return (new(BookmarkStatus.Ok, bookmark), true);
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>Removes the bookmarks of a session that was deleted: the one of the session and those of its messages.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The number of bookmarks that were removed.</returns>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be written.</exception>
    public async ValueTask<int> RemoveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => await RunAsync<int>(async (bookmarks, token) =>
        {
            if (!bookmarks.Exists(bookmark => SameSession(bookmark.SessionId, sessionId)))
            {
                return (0, false);
            }

            await _store.DeleteSessionAsync(sessionId, token).ConfigureAwait(false);
            return (bookmarks.RemoveAll(bookmark => SameSession(bookmark.SessionId, sessionId)), true);
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Removes the bookmarks of a session that was deleted, without being waited for: it is what a host does when it
    /// is told of the deletion. When the removal fails the bookmarks stay, as those of a session that no longer exists.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    public void ForgetSession(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _ = ForgetSessionAsync(sessionId);
        }
    }

    private async Task ForgetSessionAsync(string sessionId)
    {
        try
        {
            await RemoveSessionAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The bookmarks stay: the host lists them as those of a session that is gone.
        }
    }

    /// <summary>
    /// Keeps the title and the project of the sessions of the bookmarks as the host sees them now, so that a bookmark
    /// whose session is gone later still says what it was. It is not a change of the bookmarks: nothing is raised.
    /// </summary>
    /// <param name="sessions">The sessions that exist, among those of the bookmarks.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the bookmarks were written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is <see langword="null"/>.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be written.</exception>
    public async ValueTask RememberSessionsAsync(IReadOnlyCollection<BookmarkTarget> sessions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
        {
            return;
        }

        await RunAsync<int>(async (bookmarks, token) =>
        {
            var seen = new Dictionary<string, BookmarkTarget>(StringComparer.Ordinal);
            foreach (var session in sessions)
            {
                if (ValidTarget(session))
                {
                    seen[session.SessionId] = session;
                }
            }

            var changed = new List<(int Index, Bookmark Bookmark)>();
            for (var index = 0; index < bookmarks.Count; index++)
            {
                var bookmark = bookmarks[index];
                if (!seen.TryGetValue(bookmark.SessionId, out var session))
                {
                    continue;
                }

                var title = Title(session.Title);
                if (!string.Equals(title, bookmark.SessionTitle, StringComparison.Ordinal) || !string.Equals(session.ProjectId, bookmark.ProjectId, StringComparison.Ordinal))
                {
                    changed.Add((index, bookmark with { SessionTitle = title, ProjectId = session.ProjectId }));
                }
            }

            if (changed.Count > 0)
            {
                await _store.UpdateSessionsAsync([.. changed.Select(static item => item.Bookmark)], token).ConfigureAwait(false);
                foreach (var (index, bookmark) in changed)
                {
                    bookmarks[index] = bookmark;
                }
            }

            return (changed.Count, false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Says where a bookmark leads now. A message is looked for at the offset it was last found at, then among the
    /// records of its run: the offset of a record moves when the first line of its journal is replaced. The offset
    /// it is found at is kept for the next time.
    /// </summary>
    /// <param name="id">The id of the bookmark.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// <see cref="BookmarkStatus.Ok"/> with the offset of a message (none for a session),
    /// <see cref="BookmarkStatus.NotFound"/> when there is no such bookmark, <see cref="BookmarkStatus.SessionMissing"/>
    /// when the session of a message has no journal, or <see cref="BookmarkStatus.Gone"/> when its journal no longer
    /// holds the message. For a bookmark of a session the caller says whether the session exists.
    /// </returns>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be read.</exception>
    public async ValueTask<BookmarkLocation> ResolveAsync(string id, CancellationToken cancellationToken = default)
    {
        var located = await LocateAsync(id, cancellationToken).ConfigureAwait(false);
        return new(located.Status, located.Bookmark, located.Message?.Offset);
    }

    /// <summary>Reads the text of the message of a bookmark from the journal of its session.</summary>
    /// <param name="id">The id of the bookmark.</param>
    /// <param name="maximumCharacters">The most characters of the text to return.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The bookmark with the text of its message; the statuses are those of <see cref="ResolveAsync"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumCharacters"/> is negative.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">The database cannot be read.</exception>
    public async ValueTask<BookmarkText> ReadTextAsync(string id, int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        var located = await LocateAsync(id, cancellationToken).ConfigureAwait(false);
        if (located.Message is not { } message)
        {
            return new(located.Status, located.Bookmark, null, null, false);
        }

        var text = message.Text;
        var truncated = text.Length > maximumCharacters;
        if (truncated)
        {
            // Half of a surrogate pair is not text.
            text = text[..(maximumCharacters > 0 && char.IsHighSurrogate(text[maximumCharacters - 1]) ? maximumCharacters - 1 : maximumCharacters)];
        }

        return new(located.Status, located.Bookmark, message.Offset, text, truncated);
    }

    /// <summary>Makes the name of a bookmark from what was typed: trimmed, and nothing for an empty one.</summary>
    /// <param name="label">What was typed.</param>
    /// <param name="normalized">The name; <see langword="null"/> for none.</param>
    /// <returns>
    /// <see langword="false"/> when the name is longer than <see cref="MaximumLabelLength"/> or holds a control
    /// character or half of a surrogate pair.
    /// </returns>
    public static bool TryNormalizeLabel(string? label, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        return normalized is null || normalized.Length <= MaximumLabelLength && WellFormed(normalized);
    }

    /// <summary>Makes the excerpt of a message: its beginning on one line, with every run of white space as one space.</summary>
    /// <param name="text">The text of the message.</param>
    /// <returns>At most <see cref="MaximumExcerptLength"/> characters, never half of a surrogate pair.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string MakeExcerpt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var excerpt = new StringBuilder(Math.Min(text.Length, MaximumExcerptLength));
        var space = false;
        for (var index = 0; index < text.Length && excerpt.Length < MaximumExcerptLength; index++)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                space = excerpt.Length > 0;
                continue;
            }

            var pair = char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
            if (char.IsSurrogate(character) && !pair)
            {
                continue;
            }

            if (excerpt.Length + (space ? 1 : 0) + (pair ? 2 : 1) > MaximumExcerptLength)
            {
                break;
            }

            if (space)
            {
                excerpt.Append(' ');
                space = false;
            }

            excerpt.Append(character);
            if (pair)
            {
                excerpt.Append(text[++index]);
            }
        }

        return excerpt.ToString();
    }

    private async ValueTask<BookmarkResult> AddReadAsync(BookmarkTarget target, string? label, string? createdBy, BookmarkJournalRead read, CancellationToken cancellationToken)
    {
        if (read.Status == BookmarkJournalStatus.SessionMissing)
        {
            return new(BookmarkStatus.SessionMissing, null);
        }

        // A message whose identifiers cannot be kept cannot be found again.
        if (read.Message is not { } message || !ValidIdentifier(message.ContentId, MaximumContentIdentifierLength) || message.RunId is not null && !ValidIdentifier(message.RunId))
        {
            return new(BookmarkStatus.NotBookmarkable, null);
        }

        return await AddAsync(target, label, createdBy, message, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BookmarkResult> AddAsync(BookmarkTarget target, string? label, string? createdBy, BookmarkMessage? message, CancellationToken cancellationToken)
        => await RunAsync<BookmarkResult>(async (bookmarks, token) =>
        {
            var existing = bookmarks.Find(bookmark => SameSession(bookmark.SessionId, target.SessionId) && (message is null
                ? bookmark.Kind == BookmarkKinds.Session
                : bookmark.Kind == BookmarkKinds.Message && SameMessage(bookmark, message)));
            if (existing is not null)
            {
                return (new(BookmarkStatus.Ok, existing), false);
            }

            if (bookmarks.Count >= _maximum)
            {
                return (new(BookmarkStatus.Limit, null), false);
            }

            var bookmark = new Bookmark(
                Guid.CreateVersion7(_clock.GetUtcNow()).ToString("D"),
                message is null ? BookmarkKinds.Session : BookmarkKinds.Message,
                target.SessionId,
                Title(target.Title),
                target.ProjectId,
                message?.Role,
                label,
                message is null ? null : MakeExcerpt(message.Text),
                message?.RunId,
                message?.ContentId,
                message?.Offset,
                message?.Timestamp,
                _clock.GetUtcNow(),
                string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim());
            await _store.InsertAsync(bookmark, token).ConfigureAwait(false);
            bookmarks.Add(bookmark);
            bookmarks.Sort(NewestFirst);
            return (new(BookmarkStatus.Ok, bookmark) { Created = true }, true);
        }, cancellationToken).ConfigureAwait(false);

    private async ValueTask<(BookmarkStatus Status, Bookmark? Bookmark, BookmarkMessage? Message)> LocateAsync(string id, CancellationToken cancellationToken)
    {
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } bookmark)
        {
            return (BookmarkStatus.NotFound, null, null);
        }

        if (bookmark.Kind != BookmarkKinds.Message || bookmark.ContentId is not { } contentId || bookmark.Role is not { } role)
        {
            return (BookmarkStatus.Ok, bookmark, null);
        }

        // The journal is read without the bookmarks held: a long journal does not make a list wait.
        BookmarkMessage? message = null;
        if (bookmark.Offset is { } hint)
        {
            var read = await _journal.ReadAsync(bookmark.SessionId, hint, cancellationToken).ConfigureAwait(false);
            if (read.Status == BookmarkJournalStatus.SessionMissing)
            {
                return (BookmarkStatus.SessionMissing, bookmark, null);
            }

            if (read.Message is { } there && SameMessage(bookmark, there))
            {
                message = there;
            }
        }

        if (message is null)
        {
            var found = await _journal.FindAsync(bookmark.SessionId, bookmark.RunId, contentId, role, cancellationToken).ConfigureAwait(false);
            if (found.Status != BookmarkJournalStatus.Found || found.Message is not { } elsewhere)
            {
                return (found.Status == BookmarkJournalStatus.SessionMissing ? BookmarkStatus.SessionMissing : BookmarkStatus.Gone, bookmark, null);
            }

            message = elsewhere;
            var moved = elsewhere.Offset;
            // Where it is now is where to look first the next time. The bookmark did not change for who lists it.
            bookmark = await RunAsync<Bookmark>(async (bookmarks, token) =>
            {
                var index = IndexOf(bookmarks, bookmark.Id);
                if (index < 0 || bookmarks[index].Offset == moved)
                {
                    return (index < 0 ? bookmark : bookmarks[index], false);
                }

                await _store.UpdateOffsetAsync(bookmark.Id, moved, token).ConfigureAwait(false);
                return (bookmarks[index] = bookmarks[index] with { Offset = moved }, false);
            }, cancellationToken).ConfigureAwait(false);
        }

        return (BookmarkStatus.Ok, bookmark, message);
    }

    // Runs one request on the bookmarks, one at a time, and tells that they changed once they are released.
    private async ValueTask<T> RunAsync<T>(Func<List<Bookmark>, CancellationToken, ValueTask<(T Result, bool Changed)>> request, CancellationToken cancellationToken)
    {
        T result;
        bool changed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var replaced = false;
            for (var attempt = 0; ; attempt++)
            {
                replaced |= await LoadAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    (result, changed) = await request(_bookmarks!, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (SqliteException) when (attempt == 0 && _generation != _store.Generation)
                {
                    // The file of the database was replaced between the read and the write: the request is run
                    // once more, on the bookmarks of the file that is there now.
                }
                catch
                {
                    // What the database holds after a write that failed is not known: it is read again the next time.
                    _generation = -1;
                    throw;
                }
            }

            changed |= replaced;
            if (changed)
            {
                Volatile.Write(ref _sessionIds, SessionsOf(_bookmarks!));
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return result;
    }

    // Reads the bookmarks when they were never read, or when the file of the database was replaced since: it then
    // came back as a copy, older, or empty. Returns whether bookmarks that were read before were read again.
    private async ValueTask<bool> LoadAsync(CancellationToken cancellationToken)
    {
        // The database says that its file was replaced once it is used: the version of the table is read from it,
        // which also makes the table in a file that does not have it.
        await _store.MigrateAsync(cancellationToken).ConfigureAwait(false);
        if (_bookmarks is not null && _generation == _store.Generation)
        {
            return false;
        }

        var replaced = _bookmarks is not null;
        for (var attempt = 0; ; attempt++)
        {
            // Opening the database can itself replace its file: what was read then belongs to the file before.
            var generation = _store.Generation;
            await _store.MigrateAsync(cancellationToken).ConfigureAwait(false);
            var bookmarks = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (generation != _store.Generation && attempt < 3)
            {
                continue;
            }

            bookmarks.Sort(NewestFirst);
            _bookmarks = bookmarks;
            _generation = generation;
            Volatile.Write(ref _sessionIds, SessionsOf(bookmarks));
            return replaced;
        }
    }

    // The order of a list: the bookmark added last comes first.
    private static int NewestFirst(Bookmark left, Bookmark right)
    {
        var order = right.CreatedAt.CompareTo(left.CreatedAt);
        return order != 0 ? order : string.CompareOrdinal(right.Id, left.Id);
    }

    private static string[] SessionsOf(List<Bookmark> bookmarks)
        => [.. bookmarks.Select(static bookmark => bookmark.SessionId).Distinct(StringComparer.Ordinal)];

    private static Bookmark? Find(List<Bookmark> bookmarks, string id) => IndexOf(bookmarks, id) is >= 0 and var index ? bookmarks[index] : null;

    private static int IndexOf(List<Bookmark> bookmarks, string id)
        => string.IsNullOrEmpty(id) ? -1 : bookmarks.FindIndex(bookmark => string.Equals(bookmark.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool SameSession(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    // What names a message: its run, its content identifier and who wrote it. Never its offset.
    private static bool SameMessage(Bookmark bookmark, BookmarkMessage message)
        => string.Equals(bookmark.RunId, message.RunId, StringComparison.Ordinal)
            && string.Equals(bookmark.ContentId, message.ContentId, StringComparison.Ordinal)
            && string.Equals(bookmark.Role, message.Role, StringComparison.Ordinal);

    private static bool ValidTarget(BookmarkTarget target)
        => ValidIdentifier(target.SessionId) && target.SessionId == target.SessionId.Trim() && target.Title is not null
            && (target.ProjectId is null || ValidIdentifier(target.ProjectId));

    private static bool ValidCreator(string? createdBy) => string.IsNullOrWhiteSpace(createdBy) || ValidIdentifier(createdBy);

    private static bool ValidIdentifier(string? value, int maximum = MaximumIdentifierLength)
        => value is { Length: > 0 } && value.Length <= maximum && !string.IsNullOrWhiteSpace(value) && WellFormed(value);

    // The title on one line, cut where a character is whole.
    private static string Title(string title)
    {
        var text = new StringBuilder(Math.Min(title.Length, MaximumTitleLength));
        foreach (var character in title.Trim())
        {
            if (text.Length == MaximumTitleLength)
            {
                break;
            }

            text.Append(char.IsControl(character) ? ' ' : character);
        }

        if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
        {
            text.Length--;
        }

        return text.ToString();
    }

    private static bool WellFormed(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character))
            {
                return false;
            }

            if (!char.IsSurrogate(character))
            {
                continue;
            }

            if (!char.IsHighSurrogate(character) || ++index == value.Length || !char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
