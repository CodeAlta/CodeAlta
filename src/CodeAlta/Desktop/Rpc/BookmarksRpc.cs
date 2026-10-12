using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Bookmarks;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the bookmarks: the sessions and the messages of sessions the user marked to find them
/// again. It lists them, adds one on a session or on a message of its timeline, names one, removes one, says
/// where a bookmarked message is in its journal now, and is told when anything of this changes.
/// </summary>
[NeoRpcService("bookmarks", Version = 1)]
internal sealed class BookmarksService
{
    /// <summary>The most bytes of the bookmarks one answer holds, below the size of a response.</summary>
    internal const int MaximumListBytes = 700 * 1024;

    private readonly BookmarkService? _bookmarks;
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, BookmarkTarget>>>? _sessions;
    private readonly ISessionRetentionAdmission? _retention;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal BookmarksService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="bookmarks">The bookmarks of the instance.</param>
    /// <param name="sessions">
    /// Says which of some sessions exist, each with its title and its project as the window lists them. A session
    /// that is not in the answer does not exist; a failure says nothing about any session.
    /// </param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="retention">Where a session is claimed while a bookmark is added on it, so that it is not retired meanwhile.</param>
    internal BookmarksService(BookmarkService bookmarks,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, BookmarkTarget>>> sessions, string epoch,
        ISessionRetentionAdmission? retention = null)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_bookmarks, _sessions, _epoch, _retention) = (bookmarks, sessions, epoch, retention);
    }

    /// <summary>
    /// Says which sessions exist the way the window does: from the sessions the host lists, each with the title of
    /// the list of sessions and the project its journal names.
    /// </summary>
    /// <param name="reads">The reads of the workspace the window lists its sessions from.</param>
    /// <param name="journals">The journals, for the header of a session the window does not list any more.</param>
    internal static Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, BookmarkTarget>>> Sessions(
        OwnedSessionWorkspace reads, SessionViewJournalStore journals)
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(journals);
        return async (ids, cancellationToken) =>
        {
            var found = new Dictionary<string, BookmarkTarget>(StringComparer.Ordinal);
            if (ids.Count == 0) return found;
            var snapshot = await reads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            var listed = snapshot.Sessions.Where(session => wanted.Contains(session.SessionId)).ToList();
            var headers = new Dictionary<string, SessionViewJournalHeader>(snapshot.SessionHeaders, StringComparer.Ordinal);
            // The snapshot has the headers of the sessions the window lists: a bookmark can be of an older one.
            var unread = listed.Where(session => !headers.ContainsKey(session.SessionId)).ToList();
            if (unread.Count > 0)
            {
                foreach (var (id, header) in await OwnedSessionWorkspace.ReadSessionHeadersAsync(unread, journals, cancellationToken).ConfigureAwait(false))
                    headers[id] = header;
            }

            foreach (var session in listed)
            {
                SessionViewKind? kind = null;
                ProjectDescriptor? project = null;
                if (headers.TryGetValue(session.SessionId, out var header) && header.SessionId == session.SessionId && header.WorkingDirectory == session.WorkspacePath)
                {
                    if (header.Kind == SessionViewKind.GlobalSession && header.ProjectRef is null) kind = SessionViewKind.GlobalSession;
                    else if (header.Kind == SessionViewKind.ProjectSession && header.ProjectRef is { } reference)
                    {
                        project = snapshot.Projects.FirstOrDefault(candidate => candidate.Id == reference && candidate.ProjectPath == session.WorkspacePath);
                        if (project is not null) kind = SessionViewKind.ProjectSession;
                    }
                }

                found[session.SessionId] = new(session.SessionId, SessionRuntimeService.ListedTitle(session, kind, project), project?.Id);
            }

            return found;
        };
    }

    /// <summary>Every bookmark, the newest first.</summary>
    [NeoRpcMethod("list")]
    public async Task<BookmarksListResponse> ListAsync(BookmarksRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, [], false);
        IReadOnlyList<Bookmark> bookmarks;
        IReadOnlyDictionary<string, BookmarkTarget> sessions;
        try
        {
            bookmarks = await _bookmarks!.ListAsync(cancellationToken).ConfigureAwait(false);
            // Sessions that cannot be listed say nothing about a bookmark: none is shown as one whose session is gone.
            sessions = await _sessions!([.. bookmarks.Select(static bookmark => bookmark.SessionId).Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return new("read_failed", [], false);
        }

        var rows = new List<BookmarkRow>(bookmarks.Count);
        var remaining = MaximumListBytes;
        foreach (var bookmark in bookmarks)
        {
            var row = Row(bookmark, sessions.GetValueOrDefault(bookmark.SessionId), known: true);
            var cost = Cost(row);
            if (cost > remaining) break;
            remaining -= cost;
            rows.Add(row);
        }

        try
        {
            // What a session is called now is what its bookmarks say once it is gone.
            await _bookmarks.RememberSessionsAsync([.. sessions.Values], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            // The list is the one that was read.
        }

        return new("ok", rows, rows.Count < bookmarks.Count);
    }

    /// <summary>Bookmarks a session, or the message of a session that its journal holds at an offset. Adding what is bookmarked answers with the bookmark there is.</summary>
    [NeoRpcMethod("add")]
    public async Task<BookmarkResponse> AddAsync(BookmarkAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        long? offset = null;
        if (!Identifier(request.SessionId, 256) || request.Offset is not null && (offset = Decimal(request.Offset)) is null
            || !BookmarkService.TryNormalizeLabel(request.Label, out var label)) return new("invalid_request", null);
        // A session that is being retired is as good as gone; one that is claimed is not retired before its bookmark protects it.
        using var claim = _retention?.TryProtectSession(request.SessionId);
        if (_retention is not null && claim is null) return new("not_found", null);
        try
        {
            var sessions = await _sessions!([request.SessionId], cancellationToken).ConfigureAwait(false);
            if (!sessions.TryGetValue(request.SessionId, out var session)) return new("not_found", null);
            var result = offset is { } at
                ? await _bookmarks!.AddMessageAsync(session, at, label, createdBy: null, cancellationToken).ConfigureAwait(false)
                : await _bookmarks!.AddSessionAsync(session, label, createdBy: null, cancellationToken).ConfigureAwait(false);
            return new(Status(result.Status), result.Bookmark is null ? null : Row(result.Bookmark, session, known: true));
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return new("write_failed", null);
        }
    }

    /// <summary>Gives a bookmark the name the user chose; an empty name gives it back the one of what it marks.</summary>
    [NeoRpcMethod("rename")]
    public async Task<BookmarkResponse> RenameAsync(BookmarkRenameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!Identifier(request.Id, 64) || !BookmarkService.TryNormalizeLabel(request.Label, out var label)) return new("invalid_request", null);
        BookmarkResult result;
        try
        {
            result = await _bookmarks!.RenameAsync(request.Id, label, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return new("write_failed", null);
        }

        return new(Status(result.Status), result.Bookmark is null ? null : await RowAsync(result.Bookmark, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Removes a bookmark.</summary>
    [NeoRpcMethod("remove")]
    public async Task<BookmarkResponse> RemoveAsync(BookmarkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!Identifier(request.Id, 64)) return new("invalid_request", null);
        BookmarkResult result;
        try
        {
            result = await _bookmarks!.RemoveAsync(request.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return new("write_failed", null);
        }

        return new(Status(result.Status), result.Bookmark is null ? null : await RowAsync(result.Bookmark, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Where a bookmark leads now: its session, and for a message the offset its journal holds it at.</summary>
    [NeoRpcMethod("resolve")]
    public async Task<BookmarkResolveResponse> ResolveAsync(BookmarkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null);
        if (!Identifier(request.Id, 64)) return new("invalid_request", null, null, null);
        try
        {
            if (await _bookmarks!.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } bookmark) return new("not_found", null, null, null);
            var sessions = await _sessions!([bookmark.SessionId], cancellationToken).ConfigureAwait(false);
            if (!sessions.TryGetValue(bookmark.SessionId, out var session)) return new("session_missing", bookmark.SessionId, bookmark.ProjectId, null);
            if (bookmark.Kind == BookmarkKinds.Session) return new("ok", session.SessionId, session.ProjectId, null);
            var location = await _bookmarks.ResolveAsync(bookmark.Id, cancellationToken).ConfigureAwait(false);
            return location.Status switch
            {
                BookmarkStatus.Ok => new("ok", session.SessionId, session.ProjectId, location.Offset?.ToString(CultureInfo.InvariantCulture)),
                // Removed while its message was looked for.
                BookmarkStatus.NotFound => new("not_found", null, null, null),
                BookmarkStatus.SessionMissing => new("session_missing", bookmark.SessionId, bookmark.ProjectId, null),
                _ => new("gone", session.SessionId, session.ProjectId, null),
            };
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            // Neither the session nor its message is known to be gone.
            return new("read_failed", null, null, null);
        }
    }

    /// <summary>Tells when the bookmarks changed: the page then lists again.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<BookmarksEvent> Watch(BookmarksRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.BookmarksEvent);
    }

    internal async IAsyncEnumerable<BookmarksEvent> WatchAsync(BookmarksRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null) yield break;
        // A page that does not read misses nothing: one pending notice says everything that changed.
        var notices = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        void Notify() => notices.Writer.TryWrite(0);
        _bookmarks!.Changed += Notify;
        try
        {
            var revision = 0;
            yield return new BookmarksEvent(revision);
            await foreach (var _ in notices.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return new BookmarksEvent(++revision);
        }
        finally
        {
            _bookmarks.Changed -= Notify;
        }
    }

    private string? Refuse(string? expectedEpoch)
        => _bookmarks is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    // The bookmark as it is after a request that changed it: its session is looked for, and is not said to be gone
    // when that fails.
    private async Task<BookmarkRow> RowAsync(Bookmark bookmark, CancellationToken cancellationToken)
    {
        try
        {
            var sessions = await _sessions!([bookmark.SessionId], cancellationToken).ConfigureAwait(false);
            return Row(bookmark, sessions.GetValueOrDefault(bookmark.SessionId), known: true);
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return Row(bookmark, null, known: false);
        }
    }

    // The identifiers are those of the entries of the timeline: the run as the journal writes it, the content
    // identifier in the compact form a long one has there.
    private static BookmarkRow Row(Bookmark bookmark, BookmarkTarget? session, bool known)
        => new(bookmark.Id, bookmark.Kind, bookmark.SessionId, session is null ? bookmark.ProjectId : session.ProjectId,
            Bounded(session?.Title ?? bookmark.SessionTitle, BookmarkService.MaximumTitleLength), known && session is null,
            bookmark.Role, bookmark.Label, bookmark.Excerpt, bookmark.RunId,
            bookmark.ContentId is null ? null : RuntimeDisplayProjection.CompactIdentifier(bookmark.ContentId),
            bookmark.Offset?.ToString(CultureInfo.InvariantCulture), bookmark.MessageAt, bookmark.CreatedAt, bookmark.CreatedBy);

    private static string Status(BookmarkStatus status) => status switch
    {
        BookmarkStatus.Ok => "ok",
        BookmarkStatus.NotFound or BookmarkStatus.SessionMissing => "not_found",
        BookmarkStatus.NotBookmarkable => "not_bookmarkable",
        BookmarkStatus.Limit => "limit",
        _ => "invalid_request",
    };

    // Anything but the cancellation of the request is the failure of what was asked, which the answer names.
    private static bool Failure(Exception exception, CancellationToken cancellationToken)
        => exception is not OutOfMemoryException && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static bool Identifier(string? value, int maximum)
        => value is { Length: > 0 } && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);

    private static long? Decimal(string value)
        => value.Length is >= 1 and <= 19 && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string Bounded(string value, int maximum)
        => value.Length <= maximum ? value : value[..(char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum)];

    // The bytes a row takes at most in a response: the names of its properties, and its texts as JSON writes them.
    private static int Cost(BookmarkRow row)
        => 512 + Json(row.Id) + Json(row.SessionId) + Json(row.ProjectId) + Json(row.SessionTitle) + Json(row.Label) + Json(row.Excerpt)
            + Json(row.RunId) + Json(row.ContentId) + Json(row.Offset) + Json(row.CreatedBy);

    private static int Json(string? value)
    {
        var bytes = 0;
        foreach (var character in value ?? string.Empty)
            bytes += character is >= ' ' and < (char)0x7f and not ('"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`') ? 1 : 6;
        return bytes;
    }
}

internal sealed record BookmarksRequest(string? ExpectedEpoch);

/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c> or <c>read_failed</c>.</param>
/// <param name="Bookmarks">The bookmarks, the newest first.</param>
/// <param name="Truncated">There are more bookmarks than the answer holds.</param>
internal sealed record BookmarksListResponse(string Status, IReadOnlyList<BookmarkRow> Bookmarks, bool Truncated);

/// <param name="Id">The id of the bookmark.</param>
/// <param name="Kind"><c>session</c> for a whole session, <c>message</c> for one message of its timeline.</param>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectId">The project of the session; null for a chat.</param>
/// <param name="SessionTitle">The title of the session as it is now, or as it was when the session no longer exists.</param>
/// <param name="SessionMissing">The session no longer exists.</param>
/// <param name="Role"><c>user</c> or <c>assistant</c>, for a message.</param>
/// <param name="Label">The name the user gave the bookmark; null when it has none.</param>
/// <param name="Excerpt">The beginning of the message, as plain text on one line; null for a session.</param>
/// <param name="RunId">The turn of the message.</param>
/// <param name="ContentId">The id of the message, as the entries of the timeline carry it.</param>
/// <param name="Offset">The offset the journal held the message at when it was last found: where to look first, not an identity.</param>
/// <param name="MessageAt">When the message was written.</param>
/// <param name="CreatedAt">When the bookmark was added.</param>
/// <param name="CreatedBy">The session whose agent added the bookmark; null when the user did.</param>
internal sealed record BookmarkRow(string Id, string Kind, string SessionId, string? ProjectId, string SessionTitle, bool SessionMissing,
    string? Role, string? Label, string? Excerpt, string? RunId, string? ContentId, string? Offset, DateTimeOffset? MessageAt,
    DateTimeOffset CreatedAt, string? CreatedBy);

/// <param name="SessionId">The session to bookmark, or the session of the message.</param>
/// <param name="Offset">The offset of the message in the journal of the session, as the timeline keys its row; null to bookmark the session.</param>
/// <param name="Label">The name of the bookmark; null to give it none.</param>
internal sealed record BookmarkAddRequest(string? ExpectedEpoch, string SessionId, string? Offset, string? Label);

internal sealed record BookmarkRenameRequest(string? ExpectedEpoch, string Id, string? Label);

internal sealed record BookmarkRequest(string? ExpectedEpoch, string Id);

/// <param name="Status">
/// <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>, <c>not_found</c> (no such bookmark, or no such session to
/// bookmark), <c>not_bookmarkable</c> (the offset is not one of a message of the user or of the assistant), <c>limit</c> (there are as
/// many bookmarks as are kept) or <c>write_failed</c>.
/// </param>
/// <param name="Bookmark">The bookmark as it is after the request; the one that was removed for a removal.</param>
internal sealed record BookmarkResponse(string Status, BookmarkRow? Bookmark);

/// <param name="Status">
/// <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>, <c>not_found</c> (no such bookmark), <c>session_missing</c>
/// (its session no longer exists), <c>gone</c> (the session no longer holds the message: it was rewound past it) or <c>read_failed</c>
/// (the sessions or the journal could not be read: nothing is known to be gone).
/// </param>
/// <param name="SessionId">The session to open.</param>
/// <param name="ProjectId">The project of the session; null for a chat.</param>
/// <param name="Offset">The offset the journal holds the message at now; null for a bookmark of a session.</param>
internal sealed record BookmarkResolveResponse(string Status, string? SessionId, string? ProjectId, string? Offset);

internal sealed record BookmarksEvent(int Revision);
