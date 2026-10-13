using CodeAlta.Catalog;
using CodeAlta.Catalog.Bookmarks;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta bookmark` commands: the sessions, and the messages of sessions, that the user or an agent marked to
// find them again. They are kept in the database of the instance.
internal sealed partial class BuiltInAltaCommandContributor
{
    /// <summary>The most characters of the text of a message one <c>show</c> returns.</summary>
    internal const int MaximumBookmarkText = 16 * 1024;

    /// <summary>The number of bookmarks one <c>list</c> returns when it names no limit.</summary>
    internal const int DefaultBookmarkLimit = 50;

    private static readonly AltaCommandPolicy[] BookmarkPolicies =
    [
        Read("bookmark list"),
        Read("bookmark show"),
        Mutating("bookmark add"),
        Mutating("bookmark rename"),
        Mutating("bookmark remove"),
    ];

    private static Command CreateBookmarkCommand(AltaCommandContext context)
    {
        var group = Group("bookmark", "Use the bookmarks: sessions, and messages of sessions, marked to find them again.");
        group.Add(CreateBookmarkListCommand(context));
        group.Add(CreateBookmarkShowCommand(context));
        group.Add(CreateBookmarkAddCommand(context));
        group.Add(CreateBookmarkRenameCommand(context));
        group.Add(CreateBookmarkRemoveCommand(context));
        AddHelpText(
            group,
            "A bookmark marks a whole session, or one message of a session: a prompt of the user or a message of the assistant. The user adds bookmarks in the window and finds them in its Bookmarks tab; these commands read them, and add one when the user asks for it.",
            "A bookmarked message is named by its turn and its content id, and is found again after the session was renamed, moved to another provider or rewound to a later turn. Deleting a session removes its bookmarks; a bookmark whose session is gone otherwise stays, with `sessionMissing`; one whose message was removed by a rewind is `gone` in `alta bookmark show`.",
            "A bookmarked session is not removed by the automatic cleanup of old sessions.",
            "Examples: `alta bookmark list`; `alta bookmark show <bookmark-id>`; `alta bookmark add --turn 3 --role assistant --label \"The migration plan\"`.");
        return group;
    }

    private static Command CreateBookmarkListCommand(AltaCommandContext context)
    {
        string? session = null;
        string? project = null;
        var all = false;
        var limit = DefaultBookmarkLimit;
        var command = Leaf("list", "List the bookmarks, the newest first: id, kind, session, and for a message its role and the beginning of its text.");
        command.Add("session=", "The bookmarks of one session: the one of the session and those of its messages.", value => session = value);
        command.Add("project=", "The bookmarks of the sessions of one project: its id, slug or path.", value => project = value);
        command.Add("all", "The bookmarks of every project and of the sessions of no project.", value => all = value is not null);
        command.Add("limit=", $"Maximum bookmarks to return. Defaults to {DefaultBookmarkLimit}.", (int value) => limit = value);
        command.Add(async (_, _) => await HandleBookmarkListAsync(context, session, project, all, limit).ConfigureAwait(false));
        AddHelpText(
            command,
            "Without an option, the bookmarks of the project of the calling session are listed, and all of them for a caller of no project.",
            "Each `alta.bookmark` record has the `id` the other commands take. `kind` is `session` or `message`; `role` is `user` or `assistant`; `excerpt` is the beginning of the message on one line; `label` is the name the user gave the bookmark.",
            "Examples: `alta bookmark list`; `alta bookmark list --session <session-id>`; `alta bookmark list --all --limit 20`.");
        return command;
    }

    private static Command CreateBookmarkShowCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("show", "Show one bookmark, with the text of its message read from the session.");
        command.Add("<bookmark-id>", "Bookmark id from `alta bookmark list`.", value => id = value);
        command.Add(async (_, _) => await HandleBookmarkShowAsync(context, id).ConfigureAwait(false));
        AddHelpText(
            command,
            $"`status` is `ok`, `session_missing` (the session no longer exists) or `gone` (the session was rewound past the message); `found` says the same in one word. `text` is the message, at most {MaximumBookmarkText} characters: `truncated` says when it is longer.",
            "`offset` is where the journal of the session holds the message now, and `runId` the turn it belongs to: `alta session turns <session-id>` lists the turns.");
        return command;
    }

    private static Command CreateBookmarkAddCommand(AltaCommandContext context)
    {
        var options = new BookmarkAddOptions();
        var command = Leaf("add", "Bookmark a session, or one message of a session.");
        command.Add("session=", "The session. Defaults to the calling session.", value => options.Session = value);
        command.Add("turn=", "Bookmark a message of this turn: its number or its run id, from `alta session turns`.", value => options.Turn = value);
        command.Add("role=", "With --turn: `user` for the prompt of the turn (default), `assistant` for its last message of the assistant.", value => options.Role = value);
        command.Add("offset=", "Bookmark the message whose record starts at this byte offset of the journal of the session.", (long value) => options.Offset = value);
        command.Add("label=", $"A name for the bookmark, at most {BookmarkService.MaximumLabelLength} characters.", value => options.Label = value);
        command.Add(async (_, _) => await HandleBookmarkAddAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "Without --turn and --offset, the session itself is bookmarked.",
            "To bookmark a message, name its turn: `alta session turns <session-id>` lists the turns with their number (`turn`), their `runId` and the beginning of their prompt. `--turn 3` bookmarks the prompt of the third turn, `--turn 3 --role assistant` the answer the assistant ended that turn with.",
            "--offset is for a caller that knows the offset of a record, such as the `offset` of another bookmark; the record must be a completed message of the user or of the assistant.",
            "Adding what is bookmarked already returns the bookmark there is, with `created` false. Bookmark only what the user asks you to bookmark.",
            "Examples: `alta bookmark add`; `alta bookmark add --turn 3 --role assistant --label \"The migration plan\"`; `alta bookmark add --session <session-id> --turn 01JD...`.");
        return command;
    }

    private static Command CreateBookmarkRenameCommand(AltaCommandContext context)
    {
        string? id = null;
        string? label = null;
        var command = Leaf("rename", "Give a bookmark a name, or take its name away.");
        command.Add("<bookmark-id>", "Bookmark id from `alta bookmark list`.", value => id = value);
        command.Add("label=", $"The name, at most {BookmarkService.MaximumLabelLength} characters. An empty name removes it. Required.", value => label = value);
        command.Add(async (_, _) => await HandleBookmarkRenameAsync(context, id, label).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta bookmark rename <bookmark-id> --label \"The migration plan\"`; `alta bookmark rename <bookmark-id> --label \"\"`.");
        return command;
    }

    private static Command CreateBookmarkRemoveCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("remove", "Remove a bookmark. The session and its messages are not touched.");
        command.Add("<bookmark-id>", "Bookmark id from `alta bookmark list`.", value => id = value);
        command.Add(async (_, _) => await HandleBookmarkRemoveAsync(context, id).ConfigureAwait(false));
        AddHelpText(command, "Remove only a bookmark the user asks you to remove.");
        return command;
    }

    private static bool TryGetBookmarks(AltaCommandContext context, out BookmarkService bookmarks)
    {
        if (context.Services.Get<BookmarkService>() is { } found)
        {
            bookmarks = found;
            return true;
        }

        bookmarks = null!;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'BookmarkService' is unavailable.");
        return false;
    }

    private static async ValueTask<int> HandleBookmarkListAsync(AltaCommandContext context, string? sessionRef, string? projectRef, bool all, int limit)
    {
        const string Command = "alta bookmark list";
        if (!TryGetBookmarks(context, out var bookmarks))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var session = NormalizeOptionalText(sessionRef);
        var named = NormalizeOptionalText(projectRef);
        if (all && (session is not null || named is not null) || session is not null && named is not null)
        {
            return UsageError(context, "usage.scopeConflict", "Use one of --session, --project and --all.", Command);
        }

        if (limit <= 0)
        {
            return UsageError(context, "usage.invalidLimit", "--limit must be positive.", Command);
        }

        // The project that is listed: the one that is named, else the one of the caller, which a caller of no project does not have.
        var projectId = all || session is not null ? null : NormalizeOptionalText(context.Caller.SourceProjectId);
        if (named is not null)
        {
            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            if (await ResolveProjectAsync(catalog, named, context, includeArchived: true).ConfigureAwait(false) is not { } project)
            {
                return NotFound(context, "project.notFound", $"Project '{named}' was not found.");
            }

            projectId = project.Id;
        }

        var view = await BookmarkViewAsync(context).ConfigureAwait(false);
        var selected = new List<Bookmark>();
        foreach (var bookmark in await bookmarks.ListAsync(context.CancellationToken).ConfigureAwait(false))
        {
            if (session is not null && !string.Equals(bookmark.SessionId, session, StringComparison.OrdinalIgnoreCase)
                || projectId is not null && !string.Equals(view.ProjectOf(bookmark), projectId, StringComparison.Ordinal))
            {
                continue;
            }

            selected.Add(bookmark);
        }

        foreach (var bookmark in selected.Take(limit))
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, view.Record(context, "alta.bookmark", bookmark));
        }

        WriteSummary(context, "alta.bookmark.summary", Math.Min(selected.Count, limit), truncated: selected.Count > limit);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleBookmarkShowAsync(AltaCommandContext context, string? id)
    {
        if (!TryGetBookmarks(context, out var bookmarks))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } bookmarkId)
        {
            return UsageError(context, "usage.missingId", "A bookmark id is required.", "alta bookmark show");
        }

        if (await bookmarks.GetAsync(bookmarkId, context.CancellationToken).ConfigureAwait(false) is not { } bookmark)
        {
            return BookmarkNotFound(context, bookmarkId);
        }

        var view = await BookmarkViewAsync(context).ConfigureAwait(false);
        BookmarkText? text = null;
        if (bookmark.Kind == BookmarkKinds.Message)
        {
            // The journal of the session is read: the session is not retired meanwhile.
            context.ClaimSessionWork(bookmark.SessionId);
            text = await bookmarks.ReadTextAsync(bookmark.Id, MaximumBookmarkText, context.CancellationToken).ConfigureAwait(false);
            if (text.Status == BookmarkStatus.NotFound)
            {
                return BookmarkNotFound(context, bookmarkId);
            }
        }

        // For a message the journal says whether its session is there; for a session, the sessions the host lists.
        var missing = text is null ? view.Missing(bookmark) == true : text.Status == BookmarkStatus.SessionMissing;
        var status = missing ? "session_missing" : text?.Status == BookmarkStatus.Gone ? "gone" : "ok";
        var record = view.Record(context, "alta.bookmark", text?.Bookmark ?? bookmark);
        AltaJsonlWriter.WriteRecord(context.Stdout, record with
        {
            SessionMissing = text is null ? record.SessionMissing : missing,
            Status = status,
            Found = status == "ok",
            Offset = text?.Offset ?? (status == "ok" ? bookmark.Offset : null),
            Text = text?.Text,
            Truncated = text?.Truncated == true ? true : null,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleBookmarkAddAsync(AltaCommandContext context, BookmarkAddOptions options)
    {
        const string Command = "alta bookmark add";
        if (!TryGetBookmarks(context, out var bookmarks))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (FirstNonEmpty(options.Session, context.Caller.SourceSessionId) is not { } sessionId)
        {
            return UsageError(context, "usage.missingSession", "--session is required outside a session caller.", Command);
        }

        var turn = NormalizeOptionalText(options.Turn);
        var role = NormalizeOptionalText(options.Role);
        if (turn is not null && options.Offset is not null)
        {
            return UsageError(context, "usage.targetConflict", "Use either --turn or --offset, not both.", Command);
        }

        if (role is not null && turn is null)
        {
            return UsageError(context, "usage.missingTurn", "--role names a message of a turn: give --turn too.", Command);
        }

        if (role is not null && BookmarkRoles.Parse(role) is null)
        {
            return UsageError(context, "usage.invalidRole", "--role is `user` or `assistant`.", Command);
        }

        if (options.Offset is < 0)
        {
            return UsageError(context, "usage.invalidOffset", "--offset is a byte offset, from 0.", Command);
        }

        if (!BookmarkService.TryNormalizeLabel(options.Label, out var label))
        {
            return UsageError(context, "usage.invalidLabel", $"--label is at most {BookmarkService.MaximumLabelLength} characters on one line.", Command);
        }

        sessionId = sessionId.Trim();
        // The session is not retired while it is looked at, nor before its bookmark protects it.
        context.ClaimSessionWork(sessionId);
        var resolved = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (resolved.ExitCode != AltaExitCodes.Success)
        {
            return resolved.ExitCode;
        }

        var session = resolved.Info!.Session;
        var target = new BookmarkTarget(session.SessionId, session.Title ?? string.Empty, NormalizeOptionalText(session.ProjectRef));
        var createdBy = NormalizeOptionalText(context.Caller.SourceSessionId);
        var result = turn is not null
            ? await bookmarks.AddTurnMessageAsync(target, turn, BookmarkRoles.Parse(role) ?? BookmarkRoles.User, label, createdBy, context.CancellationToken).ConfigureAwait(false)
            : options.Offset is { } offset
                ? await bookmarks.AddMessageAsync(target, offset, label, createdBy, context.CancellationToken).ConfigureAwait(false)
                : await bookmarks.AddSessionAsync(target, label, createdBy, context.CancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case BookmarkStatus.Ok when result.Bookmark is not null:
                break;
            case BookmarkStatus.SessionMissing:
                return NotFound(context, "session.notFound", $"Session '{sessionId}' has no journal.");
            case BookmarkStatus.NotBookmarkable:
                return NotFound(context, "bookmark.notBookmarkable", turn is not null
                    ? $"Session '{sessionId}' has no such message: the turn '{turn}' does not exist, or has no message of that role. List the turns with `alta session turns {sessionId}`."
                    : "The offset is not the start of a completed message of the user or of the assistant. Name the message by its turn with --turn.");
            case BookmarkStatus.Limit:
                return PermissionDenied(context, "bookmark.limit", $"There are already {BookmarkService.MaximumBookmarks} bookmarks. Remove some first.");
            default:
                return UsageError(context, "usage.invalidBookmark", "The session, the turn or the label is not one a bookmark can have.", Command);
        }

        var view = await BookmarkViewAsync(context, session).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, view.Record(context, "alta.bookmark.added", result.Bookmark) with { Created = result.Created });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleBookmarkRenameAsync(AltaCommandContext context, string? id, string? label)
    {
        const string Command = "alta bookmark rename";
        if (!TryGetBookmarks(context, out var bookmarks))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } bookmarkId)
        {
            return UsageError(context, "usage.missingId", "A bookmark id is required.", Command);
        }

        if (label is null)
        {
            return UsageError(context, "usage.missingLabel", "--label is required: the name of the bookmark, or an empty text to remove it.", Command);
        }

        if (!BookmarkService.TryNormalizeLabel(label, out var name))
        {
            return UsageError(context, "usage.invalidLabel", $"--label is at most {BookmarkService.MaximumLabelLength} characters on one line.", Command);
        }

        var result = await bookmarks.RenameAsync(bookmarkId, name, context.CancellationToken).ConfigureAwait(false);
        if (result.Bookmark is not { } bookmark)
        {
            return BookmarkNotFound(context, bookmarkId);
        }

        var view = await BookmarkViewAsync(context).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, view.Record(context, "alta.bookmark.changed", bookmark));
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleBookmarkRemoveAsync(AltaCommandContext context, string? id)
    {
        if (!TryGetBookmarks(context, out var bookmarks))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } bookmarkId)
        {
            return UsageError(context, "usage.missingId", "A bookmark id is required.", "alta bookmark remove");
        }

        var result = await bookmarks.RemoveAsync(bookmarkId, context.CancellationToken).ConfigureAwait(false);
        if (result.Bookmark is not { } bookmark)
        {
            return BookmarkNotFound(context, bookmarkId);
        }

        var view = await BookmarkViewAsync(context).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, view.Record(context, "alta.bookmark.removed", bookmark));
        return AltaExitCodes.Success;
    }

    private static int BookmarkNotFound(AltaCommandContext context, string id)
        => NotFound(context, "bookmark.notFound", $"There is no bookmark '{id}'. List them with `alta bookmark list`.");

    // What the host knows of the sessions of the bookmarks: which exist, and the title and the project of each as
    // they are now. A host that cannot list its sessions says of none that it is gone.
    private static async Task<BookmarkView> BookmarkViewAsync(AltaCommandContext context, SessionViewDescriptor? known = null)
    {
        var sessions = new Dictionary<string, SessionViewDescriptor>(StringComparer.OrdinalIgnoreCase);
        var listed = false;
        if (known is not null)
        {
            sessions[known.SessionId] = known;
        }
        else if ((context.Services.Get<IAltaSessionQueryService>() is not null || context.Services.Get<SessionRuntimeService>() is not null
                || context.Services.Get<SessionViewCatalog>() is not null)
            && await LoadSessionInfosAsync(context).ConfigureAwait(false) is { } infos)
        {
            listed = true;
            foreach (var info in infos)
            {
                sessions[info.Session.SessionId] = info.Session;
            }
        }

        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        if (context.Services.Get<ProjectCatalog>() is { } catalog)
        {
            foreach (var project in await catalog.LoadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                projects[project.Id] = project.Slug;
            }
        }

        return new(sessions, listed, projects);
    }

    private sealed class BookmarkView(Dictionary<string, SessionViewDescriptor> sessions, bool listed, Dictionary<string, string> projects)
    {
        // Null when the host did not list its sessions: nothing is known of a session that is not among those it has.
        internal bool? Missing(Bookmark bookmark) => sessions.ContainsKey(bookmark.SessionId) ? false : listed ? true : null;

        internal string? ProjectOf(Bookmark bookmark)
            => sessions.TryGetValue(bookmark.SessionId, out var session) ? NormalizeOptionalText(session.ProjectRef) : bookmark.ProjectId;

        internal BookmarkRecord Record(AltaCommandContext context, string type, Bookmark bookmark)
        {
            var projectId = ProjectOf(bookmark);
            var title = sessions.TryGetValue(bookmark.SessionId, out var session) && !string.IsNullOrWhiteSpace(session.Title) ? session.Title : bookmark.SessionTitle;
            return new(type, 1, context.CorrelationId, bookmark.Id, bookmark.Kind, bookmark.SessionId, title, Missing(bookmark), projectId,
                projectId is not null && projects.TryGetValue(projectId, out var slug) ? slug : null, bookmark.Role, bookmark.Label, bookmark.Excerpt,
                bookmark.RunId, bookmark.ContentId, bookmark.Offset, bookmark.MessageAt, bookmark.CreatedAt, bookmark.CreatedBy);
        }
    }

    // The names are those of the JSONL record, in camel case.
    private sealed record BookmarkRecord(string Type, int Version, string CorrelationId, string Id, string Kind, string SessionId, string SessionTitle,
        bool? SessionMissing, string? ProjectId, string? Project, string? Role, string? Label, string? Excerpt, string? RunId, string? ContentId,
        long? Offset, DateTimeOffset? MessageAt, DateTimeOffset CreatedAt, string? CreatedBy)
    {
        public bool? Created { get; init; }

        public string? Status { get; init; }

        public bool? Found { get; init; }

        public string? Text { get; init; }

        public bool? Truncated { get; init; }
    }

    private sealed class BookmarkAddOptions
    {
        public string? Session { get; set; }

        public string? Turn { get; set; }

        public string? Role { get; set; }

        public long? Offset { get; set; }

        public string? Label { get; set; }
    }
}
