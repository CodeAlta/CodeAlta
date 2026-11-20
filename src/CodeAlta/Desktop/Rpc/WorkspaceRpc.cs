using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>Persisted catalog browsing for one explicitly admitted task-owned copy.</summary>
/// <remarks>
/// Listing can create/rebuild data/alta.sqlite3 and SQLite sidecars. It never falls back to header scans.
/// AgentSessionCatalog starts its shared load using Task.Run and CancellationToken.None: canceling a waiter
/// does not stop that load. NeoAstra stops waiting for invocations after five seconds during teardown.
/// Consequently catalog/cache work can outlive bridge teardown; this service adds no lifetime guarantee.
/// Row/string/wire limits bound the response, not the underlying whole-catalog scan or shared snapshot load.
/// </remarks>
[NeoRpcService("workspace", Version = 1)]
internal sealed partial class WorkspaceService
{
    /// <summary>The most projects a snapshot lists: room for the sub-projects of monorepos.</summary>
    internal const int MaximumProjects = 1000;

    private readonly Func<WorkspaceRequest, CancellationToken, Task<WorkspaceSnapshot>>? _read;

    internal WorkspaceService(CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        _readHistory = reads.ReadHistoryPageAsync;
        _readHistoryTail = reads.ReadHistoryTailPageAsync;
        _readTimeline = reads.ReadTimelinePageAsync;
        _readTimelineTail = reads.ReadTimelineTailAsync;
        _readHistorySource = reads.ReadHistorySourceAsync;
        _readAllSessions = reads.ReadSnapshotAsync;
        _read = ProjectedSnapshot(reads.ReadSnapshotAsync);
    }

    // The snapshot of the page from the one of a host: the sessions whose folder exists, with their worktrees.
    // The spaces are read only for a window that shows one of them: what it asks for is counted and chosen in that space.
    private static Func<WorkspaceRequest, CancellationToken, Task<WorkspaceSnapshot>> ProjectedSnapshot(Func<CancellationToken, Task<CodeAlta.Orchestration.Runtime.OwnedWorkspaceSnapshot>> read,
        Func<CancellationToken, Task<IReadOnlyList<SpaceDescriptor>>>? readSpaces = null)
        => async (wanted, token) =>
        {
            var actual = read(token);
            var snapshot = await actual.ConfigureAwait(false);
            var (projects, sessions) = WithExistingFolders(snapshot.Projects, snapshot.Sessions, Directory.Exists);
            var spaces = readSpaces is not null && wanted.Space is not null ? await readSpaces(token).ConfigureAwait(false) : null;
            return WithRepositories(ProjectSnapshot(projects, sessions, snapshot.SessionHeaders, DescribeWorktreesOnce(), wanted, spaces),
                    static path => CodeAlta.Catalog.Worktrees.GitWorktreeService.FindCheckoutRoot(path) is not null)
                with { UnreadableProjectFiles = UnreadableFiles(snapshot.UnreadableProjects) };
        };

    // Marks the secure projects, whose sessions are confidential, which the window shows with a padlock.
    internal static WorkspaceSnapshot WithPrivacy(WorkspaceSnapshot snapshot, CodeAltaConfigStore config)
        => snapshot with
        {
            Projects = [.. snapshot.Projects.Select(project => project.MachineId is null && config.IsProjectPseudonymized(project.Path)
                ? project with { Secure = true } : project)],
        };

    // Marks the projects whose folder is in no git repository: the window offers them no worktree and no branch.
    internal static WorkspaceSnapshot WithRepositories(WorkspaceSnapshot snapshot, Func<string, bool> inRepository)
        => snapshot with
        {
            Projects = [.. snapshot.Projects.Select(project => project.MachineId is null && !project.Archived && !inRepository(project.Path)
                ? project with { NoRepository = true } : project)],
        };

    // The project files the snapshot skipped, for the window to say so; a few, each path bounded as the others.
    internal static string[]? UnreadableFiles(IReadOnlyList<UnreadableProjectFile> unreadable)
        => unreadable.Count == 0 ? null
            : [.. unreadable.Select(static file => file.SourcePath).Where(static path => path.Length <= 4096 && !path.Any(char.IsControl))
                .Order(StringComparer.Ordinal).Take(20)];

    internal WorkspaceService(string? catalogRoot)
    {
        if (catalogRoot is null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRoot);
        var options = new CatalogOptions { GlobalRoot = catalogRoot };
        var projects = new ProjectCatalog(options);
        var journals = new SessionViewJournalStore(options);
        var store = journals.CreateSessionStore();
        IAgentSessionCatalog sessions = new AgentSessionCatalog(store);
        _readHistory = store.ReadHistoryPageAsync;
        _readHistoryTail = store.ReadHistoryTailPageAsync;
        _readTimeline = store.ReadTimelinePageAsync;
        _readHistorySource = store.ReadHistorySourceAsync;
        _read = (wanted, cancellationToken) => ReadAsync(projects.LoadAsync,
            token => sessions.ListSessionsAsync(filter: null, cancellationToken: token), existingFoldersOnly: true, wanted, cancellationToken);
    }

    [NeoRpcMethod("snapshot")]
    public Task<WorkspaceSnapshot> SnapshotAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _read is null
            ? Task.FromResult(new WorkspaceSnapshot(false, [], [], false, false, false))
            : ServeAsync(_read(request, cancellationToken));

        async Task<WorkspaceSnapshot> ServeAsync(Task<WorkspaceSnapshot> read)
        {
            var snapshot = await read.ConfigureAwait(false);
            Served?.Invoke();
            return snapshot;
        }
    }

    /// <summary>Told each time the page is served a workspace snapshot: the first one ends the start-up screen.</summary>
    internal Action? Served { get; set; }

    // The actual RPC route uses this seam; fixtures supply only completed/faulted/canceled literal reads.
    internal static Task<WorkspaceSnapshot> ReadAsync(
        Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> loadProjects,
        Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> loadSessions,
        CancellationToken cancellationToken)
        => ReadAsync(loadProjects, loadSessions, existingFoldersOnly: false, wanted: null, cancellationToken);

    // The scope of each session comes from the journal header the session list records: no journal is opened.
    private static Task<WorkspaceSnapshot> ReadAsync(
        Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> loadProjects,
        Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> loadSessions,
        bool existingFoldersOnly,
        WorkspaceRequest? wanted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loadProjects);
        ArgumentNullException.ThrowIfNull(loadSessions);
        return ReadCoreAsync();

        async Task<WorkspaceSnapshot> ReadCoreAsync()
        {
            var projects = await loadProjects(cancellationToken).ConfigureAwait(false);
            var sessions = new List<AgentSessionMetadata>();
            await foreach (var session in loadSessions(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                sessions.Add(session);
            }
            var headers = CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace.ReadSessionHeaders(sessions);
            if (!existingFoldersOnly) return ProjectSnapshot(projects, sessions, headers, wanted: wanted);
            var shown = WithExistingFolders(projects, sessions, Directory.Exists);
            return ProjectSnapshot(shown.Projects, shown.Sessions, headers, wanted: wanted);
        }
    }

    /// <summary>
    /// Leaves out what points to a folder that is no longer there: a project whose folder is gone, and a
    /// session recorded in such a folder. Nothing is removed from the catalog, so both are back with the folder.
    /// </summary>
    internal static (IReadOnlyList<ProjectDescriptor> Projects, IReadOnlyList<AgentSessionMetadata> Sessions) WithExistingFolders(
        IReadOnlyList<ProjectDescriptor> projects, IReadOnlyList<AgentSessionMetadata> sessions, Func<string, bool> folderExists)
    {
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Exists(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true; // Nothing to look for: the row is kept as it is.
            if (!known.TryGetValue(path, out var exists)) known[path] = exists = folderExists(path);
            return exists;
        }
        return (projects.Where(project => Exists(project.ProjectPath)).ToArray(), sessions.Where(session => Exists(session.WorkspacePath)).ToArray());
    }

    /// <summary>
    /// Looks at the folder a session records as its worktree: the checkout it is in, the name of that checkout,
    /// and whether the folder is gone.
    /// </summary>
    internal static (string Root, string Name, bool Missing) DescribeWorktree(string folder)
    {
        var missing = !Directory.Exists(folder);
        // A folder that is gone is in no checkout any more: the nearest one above it would be another one.
        var root = (missing ? null : CodeAlta.Catalog.Worktrees.GitWorktreeService.FindCheckoutRoot(folder)) ?? Path.TrimEndingDirectorySeparator(folder);
        return (root, Path.GetFileName(root) is { Length: > 0 } name ? name : root, missing);
    }

    /// <summary>
    /// Describes each worktree folder once for one snapshot: the sessions that work in the same worktree share what was
    /// found. Nothing is kept for the next snapshot, which looks at the folders again.
    /// </summary>
    internal static Func<string, (string Root, string Name, bool Missing)> DescribeWorktreesOnce(Func<string, (string Root, string Name, bool Missing)>? describe = null)
    {
        var described = new Dictionary<string, (string Root, string Name, bool Missing)>(StringComparer.Ordinal);
        return folder => described.TryGetValue(folder, out var known) ? known : described[folder] = (describe ?? DescribeWorktree)(folder);
    }

    internal static WorkspaceSnapshot ProjectSnapshot(
        IReadOnlyList<ProjectDescriptor> projects, IReadOnlyList<AgentSessionMetadata> sessions,
        IReadOnlyDictionary<string, SessionViewJournalHeader>? headers = null,
        Func<string, (string Root, string Name, bool Missing)>? worktree = null, WorkspaceRequest? wanted = null,
        IReadOnlyList<SpaceDescriptor>? spaces = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessions);
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        // Validate even omitted identities: never silently trim, merge or alias an oversized identifier.
        foreach (var project in projects)
        {
            ValidateIdentity(project.Id, 256, required: true);
            ValidateIdentity(project.ProjectPath, 4096, required: true);
            if (!projectIds.Add(project.Id)) throw new InvalidDataException("Duplicate project identity.");
        }
        foreach (var session in sessions)
        {
            ValidateIdentity(session.SessionId, 256, required: true);
            ValidateIdentity(session.WorkspacePath, 4096, required: false);
            ValidateIdentity(session.ProviderKey, 256, required: false);
            if (!sessionIds.Add(session.SessionId)) throw new InvalidDataException("Duplicate session identity.");
        }

        var displayedProjects = new List<WorkspaceProject>();
        var shortened = false;
        // Conservative JSON bound: six bytes per UTF-16 unit, 512 bytes per row for property names,
        // booleans/timestamps/punctuation, and 2048 reserved for the snapshot/envelope. Each session
        // additionally reserves 64 bytes for createdAt (33 ISO characters, quotes/property/comma,
        // including a possibly escaped '+' offset). Keep the overall cap below 1 MiB RPC default. The projects take
        // at most a third of it, so that the many sub-projects of a monorepo leave the sessions most of the room. A project
        // row has 32 bytes more for the count of the sessions its list leaves out.
        var remaining = 700 * 1024 - 2048;
        var projectsRemaining = 240 * 1024;
        var names = projects.GroupBy(static project => project.Id, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.First().DisplayName, StringComparer.Ordinal);
        // The projects of their own first: sub-projects come after them, so that a large map never pushes them out.
        // Sub-projects the user hid come last: the window leaves them out, and they must not take the room of those it shows.
        foreach (var project in projects.OrderBy(static value => value.IsSubProject).ThenBy(static value => value.IsSubProject && value.Archived)
                     .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Id, StringComparer.Ordinal).ThenBy(value => value.ProjectPath, StringComparer.Ordinal))
        {
            if (displayedProjects.Count == MaximumProjects) break;
            var name = DisplayText(string.IsNullOrWhiteSpace(project.DisplayName) ? project.Id : project.DisplayName, ref shortened);
            var subPath = project.SubPath is { Length: <= 1024 } sub ? sub : null;
            var parentId = project.ParentId is { Length: <= 256 } parent && subPath is not null ? parent : null;
            var repositoryName = parentId is not null && names.TryGetValue(parentId, out var repository) && !string.IsNullOrWhiteSpace(repository)
                ? DisplayText(repository, ref shortened) : null;
            var cost = 224 + 6 * (project.Id.Length + name.Length + project.ProjectPath.Length + (parentId?.Length ?? 0) + (subPath?.Length ?? 0) + (repositoryName?.Length ?? 0));
            if (cost > remaining || cost > projectsRemaining) break;
            remaining -= cost;
            projectsRemaining -= cost;
            displayedProjects.Add(new WorkspaceProject(project.Id, name, project.ProjectPath, project.Archived) { ParentId = parentId, SubPath = subPath, RepositoryName = repositoryName });
        }
        // The sessions in the order the lists show them: the archived ones after the others, then the newest first.
        var ordered = sessions.OrderBy(static value => value.ViewState?.Archived == true).ThenByDescending(value => value.UpdatedAt)
            .ThenByDescending(value => value.SessionId, StringComparer.Ordinal).ToArray();
        var scopes = new SessionScope[ordered.Length];
        for (var position = 0; position < ordered.Length; position++) scopes[position] = ScopeOf(ordered[position], headers, projects);
        var shownProjects = displayedProjects.Select(static project => project.Id).ToHashSet(StringComparer.Ordinal);
        // The list a session is in: its project when the window shows it, the chats otherwise.
        string? ListOf(int position) => scopes[position].ProjectId is { } id && shownProjects.Contains(id) ? id : null;
        var rows = new List<(int Position, WorkspaceSession Session)>();
        var kept = new HashSet<int>();
        var projectsById = projects.ToDictionary(static project => project.Id, StringComparer.Ordinal);
        string SourceTitle(int position) => CodeAlta.Orchestration.Runtime.SessionRuntimeService.ListedTitle(ordered[position],
            scopes[position].ScopeKind switch { "global" => SessionViewKind.GlobalSession, "project" => SessionViewKind.ProjectSession, _ => (SessionViewKind?)null },
            scopes[position].ProjectId is { } scopeProject ? projectsById.GetValueOrDefault(scopeProject) : null);
        // The space the window shows, when it shows one: the rows it asks for, and those it is told are left out, are the ones
        // it lists (see InSpace). Without it, every session is.
        var shownSpace = spaces is not null && wanted?.Space is { } asked && spaces.Any(space => space.Id == asked) ? asked : null;
        bool Shown(int position) => shownSpace is null || InSpace(scopes[position],
            scopes[position].ProjectId is { } owner ? projectsById.GetValueOrDefault(owner) : null, shownSpace, spaces!);
        // What the window asks for comes first, whatever its age; then what every list shows at first, which is the same
        // whatever was asked for: a list that is extended, or a search, does not change what the other lists show.
        var wantedCut = false;
        var first = FairOrder(ordered, ListOf).Take(MaximumSessions).ToArray();
        foreach (var (index, ofList) in WantedOrder(wanted, ordered, ListOf, SourceTitle,
                     position => scopes[position].ProjectId is { } named ? projectsById.GetValueOrDefault(named)?.DisplayName : null, first.ToHashSet().Contains, Shown))
        {
            // No row fits any more: the smallest one takes more than what is left.
            if (remaining < 512 + 64) { wantedCut = true; break; }
            // A row that has no room does not keep a smaller one out. Only a list that is cut is said: its rows are counted.
            if (!kept.Contains(index) && !Keep(index)) wantedCut |= ofList;
        }
        foreach (var index in first)
        {
            if (!kept.Contains(index) && !Keep(index)) break;
        }

        // Adds the row of a session; false when the snapshot has no room for it.
        bool Keep(int index)
        {
            var session = ordered[index];
            var (scopeKind, projectId, automationId, recordedSpace) = scopes[index];
            var parent = session.ParentSessionId ?? session.ViewState?.ParentSessionId;
            if (string.IsNullOrWhiteSpace(parent)) parent = null;
            var lineageIssue = parent is not null && !ValidLineageId(parent) ? "invalid_parent" : null;
            if (lineageIssue is not null) parent = null;
            // The worktree the session works in, as the session records it: it is shown also once its folder is
            // gone, until the session continues in the folder of its project.
            string? worktreePath = null, worktreeRoot = null, worktreeName = null;
            var worktreeMissing = false;
            if (session.WorktreePath is { Length: > 0 and <= 4096 } recorded && !string.IsNullOrWhiteSpace(recorded) && !recorded.Any(char.IsControl))
            {
                (worktreeRoot, worktreeName, worktreeMissing) = (worktree ?? DescribeWorktree)(recorded);
                worktreePath = recorded;
            }
            // A session that was never named keeps the text it was created with as its saved title, while its summary
            // follows the conversation: it is listed by the first line of that summary, as the terminal UI does. The
            // deletion of a session is confirmed with the same title.
            var sourceTitle = SourceTitle(index);
            var title = DisplayText(sourceTitle, ref shortened);
            var fullTitle = DisplayTextBounded(sourceTitle, 4096, ref shortened);
            var spaceId = string.IsNullOrWhiteSpace(recordedSpace) ? null
                : SpaceDescriptor.IsValidId(recordedSpace.Trim()) ? recordedSpace.Trim() : SpaceDescriptor.DefaultId;
            string? forkedFrom = null, forkedTitle = null, forkKind = null;
            var forkTurns = 0;
            if (session.ForkedFrom is { } origin && ValidLineageId(origin.SessionId))
            {
                forkedFrom = origin.SessionId;
                forkedTitle = string.IsNullOrWhiteSpace(origin.Title) ? null : DisplayText(origin.Title, ref shortened);
                forkKind = origin.Kind == CodeAlta.Agent.Runtime.AgentSessionForkKind.Rewind ? "rewind" : "fork";
                forkTurns = Math.Max(0, origin.TurnCount);
            }

            var cost = 512 + 64 + WireLength(session.SessionId) + WireLength(title) + WireLength(fullTitle) + WireLength(parent)
                + WireLength(session.WorkspacePath) + WireLength(session.ProviderKey) + WireLength(projectId) + WireLength(automationId)
                + WireLength(worktreePath) + WireLength(worktreeRoot) + WireLength(worktreeName)
                + WireLength(forkedFrom) + WireLength(forkedTitle) + WireLength(spaceId) + 6 * 24
                + (session.ViewState?.Cloud is null ? 0 : 6 * 900);
            if (cost > remaining) return false;
            remaining -= cost;
            kept.Add(index);
            rows.Add((index, new WorkspaceSession(session.SessionId, title, session.WorkspacePath, session.ProviderKey, session.UpdatedAt,
                fullTitle, sourceTitle.Length > 4096, parent, scopeKind, projectId, lineageIssue,
                session.CreatedAt.Year > 1 ? session.CreatedAt : null, session.ViewState?.MessageCount is >= 0 ? session.ViewState.MessageCount : null,
                automationId, worktreePath, worktreeRoot, worktreeName, worktreeMissing,
                forkedFrom, forkedTitle, forkKind, forkTurns, Math.Max(0, session.RewindCount), spaceId,
                session.ViewState?.Archived == true, session.ViewState?.Archived == true ? session.ViewState.ArchivedAt : null)
            {
                ProviderConversation = (session.Details as RawApiSessionMetadataDetails)?.ProviderSessionId is { Length: > 0 } ? true : null,
                Cloud = WorkspaceSessionCloud.Of(session.ViewState?.Cloud),
            }));
            return true;
        }

        // The rows are sent in the order the lists show them, whatever the order they were kept in.
        rows.Sort(static (left, right) => left.Position.CompareTo(right.Position));
        var displayedSessions = rows.ConvertAll(static row => row.Session);

        // What each list leaves out, so that the window says it and asks for it: a list that stops is never taken for
        // sessions that are gone. The rows of a list are counted, as the window counts them: not the sub-agents under them.
        var positions = PositionsById(ordered);
        var omitted = new Dictionary<string, int>(StringComparer.Ordinal);
        var omittedChats = 0;
        for (var position = 0; position < ordered.Length; position++)
        {
            if (kept.Contains(position) || ordered[position].ViewState?.Archived == true || !Shown(position)) continue;
            if (ParentInList(ordered, positions, ListOf, position) >= 0) continue;
            if (ListOf(position) is { } id) omitted[id] = omitted.GetValueOrDefault(id) + 1;
            else omittedChats++;
        }
        for (var position = 0; position < displayedProjects.Count; position++)
        {
            if (omitted.TryGetValue(displayedProjects[position].Id, out var count)) displayedProjects[position] = displayedProjects[position] with { OmittedSessions = count };
        }
        return new WorkspaceSnapshot(true, displayedProjects.ToArray(), displayedSessions.ToArray(),
            displayedProjects.Count < projects.Count, displayedSessions.Count < sessions.Count, shortened)
        {
            OmittedChatSessions = omittedChats > 0 ? omittedChats : null,
            WantedTruncated = wantedCut ? true : null,
        };
    }

    /// <summary>
    /// Whether a window that shows one space lists a session, as the window decides it: the session is in that space (see
    /// <see cref="SpaceCatalog.SessionSpaces"/>). A session that records no scope is listed by its folder, which the window
    /// looks at: it counts as listed, so that it is never kept out of what the window can ask for.
    /// </summary>
    internal static bool InSpace(SessionScope scope, ProjectDescriptor? project, string space, IReadOnlyList<SpaceDescriptor> spaces)
        => scope.ScopeKind is null || SpaceCatalog.SessionSpaces(scope.RecordedSpace, project, spaces).Contains(space, StringComparer.Ordinal);

    // The bytes a text takes at most in the JSON of a snapshot: a character the serializer may escape takes six.
    private static int WireLength(string? value)
    {
        if (value is null) return 0;
        var length = 0;
        foreach (var character in value)
            length += character is >= ' ' and < (char)0x7F and not ('"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`') ? 1 : 6;
        return length;
    }

    /// <summary>The most sessions a request names, lists it extends, and texts it searches.</summary>
    internal const int MaximumWantedSessions = 256, MaximumWantedLists = 64, MaximumWantedSearches = 4;

    /// <summary>The most sessions one search of every list adds to a snapshot, the newest first.</summary>
    internal const int MaximumFoundSessions = 200;

    /// <summary>The most sessions a search of one list adds: the saved sessions of a project show all those it finds.</summary>
    internal const int MaximumFoundInList = 2000;

    private static Dictionary<string, int> PositionsById(IReadOnlyList<AgentSessionMetadata> ordered)
    {
        var positions = new Dictionary<string, int>(ordered.Count, StringComparer.Ordinal);
        for (var position = 0; position < ordered.Count; position++) positions[ordered[position].SessionId] = position;
        return positions;
    }

    // The position of the session a session is listed under: its parent, when it is a live session of the same list;
    // -1 for a row of the list itself.
    private static int ParentInList(IReadOnlyList<AgentSessionMetadata> ordered, Dictionary<string, int> positions, Func<int, string?> listOf, int position)
    {
        var parent = ordered[position].ParentSessionId ?? ordered[position].ViewState?.ParentSessionId;
        return !string.IsNullOrWhiteSpace(parent) && positions.TryGetValue(parent, out var above) && above != position
            && ordered[above].ViewState?.Archived != true && listOf(above) == listOf(position) ? above : -1;
    }

    /// <summary>
    /// The sessions a window asks for beyond what every list shows at first, as positions in <paramref name="ordered"/>, the
    /// most wanted first: the sessions it names (those of its tabs, one it opens) with the sessions they are under; the
    /// sessions a text it searches finds, the newest first and at most <see cref="MaximumFoundSessions"/> more than those
    /// listed at first (<see cref="MaximumFoundInList"/> for a search of one list); the first rows of the lists it extends, each with its sub-agents; then the archived sessions, when
    /// it shows them. A text finds a session that has each of its words in its title, its identifier or the name of its
    /// project, and one of them at least in its title or its identifier.
    /// </summary>
    /// <param name="wanted">The request, or null.</param>
    /// <param name="ordered">The sessions: the archived ones last, then the newest first.</param>
    /// <param name="listOf">The list of a session: its project, or null for the chats.</param>
    /// <param name="title">The title a session is listed with.</param>
    /// <param name="projectName">The name of the project of a session, or null.</param>
    /// <param name="listedAtFirst">Whether a session is among those listed whatever is asked for: a search does not count them.</param>
    /// <param name="shown">Whether the window lists a session: the rows of a list and what a search finds are among those.</param>
    /// <returns>The positions, each with whether it is a row of a list the window extends.</returns>
    internal static List<(int Position, bool OfList)> WantedOrder(WorkspaceRequest? wanted, IReadOnlyList<AgentSessionMetadata> ordered, Func<int, string?> listOf,
        Func<int, string> title, Func<int, string?> projectName, Func<int, bool> listedAtFirst, Func<int, bool> shown)
    {
        var order = new List<(int Position, bool OfList)>();
        if (wanted is null || ordered.Count == 0) return order;
        var positions = PositionsById(ordered);
        var seen = new HashSet<int>();
        void Add(int position, bool ofList = false) { if (seen.Add(position)) order.Add((position, ofList)); }

        foreach (var id in (wanted.Sessions ?? []).Take(MaximumWantedSessions))
        {
            if (id is null || !positions.TryGetValue(id, out var position)) continue;
            // With the sessions it is under, so that the list shows it in its place. A lineage that loops ends where it does.
            for (var depth = 0; position >= 0 && depth < 64 && seen.Add(position); depth++)
            {
                order.Add((position, false));
                position = ordered[position].ViewState?.Archived == true ? -1 : ParentInList(ordered, positions, listOf, position);
            }
        }

        AddFound(wanted, ordered, listOf, title, projectName, listedAtFirst, shown, position => Add(position));

        var lists = (wanted.Lists ?? []).Where(static list => list is { Count: > 0 }).Take(MaximumWantedLists).ToArray();
        if (lists.Length > 0)
        {
            var live = 0;
            while (live < ordered.Count && ordered[live].ViewState?.Archived != true) live++;
            // The row each live session is under, itself for a row: found once for every list asked for.
            var rows = new int[live];
            Array.Fill(rows, -1);
            int RowOf(int position)
            {
                var path = new List<int>();
                var current = position;
                while (rows[current] < 0 && !path.Contains(current))
                {
                    path.Add(current);
                    var above = ParentInList(ordered, positions, listOf, current);
                    if (above < 0) break;
                    current = above;
                }
                // A lineage that loops has no row above it: the session it came back to is the row.
                var row = rows[current] >= 0 ? rows[current] : current;
                foreach (var member in path) rows[member] = row;
                return row;
            }
            foreach (var list in lists)
            {
                // The rows of a list are in the order of what was done last under them: the first session of each tree.
                var chosen = new HashSet<int>();
                for (var position = 0; position < live && chosen.Count < list.Count; position++)
                    if (listOf(position) == list.ProjectId && shown(position)) chosen.Add(RowOf(position));
                for (var position = 0; position < live; position++)
                    if (listOf(position) == list.ProjectId && chosen.Contains(RowOf(position))) Add(position, ofList: true);
            }
        }

        // The archived sessions, which no list shows: the newest first, as they are ordered.
        if (wanted.Archived)
        {
            for (var position = 0; position < ordered.Count; position++)
                if (ordered[position].ViewState?.Archived == true) Add(position);
        }
        return order;
    }

    // What a search of the window finds, before the rows of the lists: a session that is looked for is not kept out by them.
    private static void AddFound(WorkspaceRequest wanted, IReadOnlyList<AgentSessionMetadata> ordered, Func<int, string?> listOf, Func<int, string> title,
        Func<int, string?> projectName, Func<int, bool> listedAtFirst, Func<int, bool> shown, Action<int> add)
    {
        foreach (var search in (wanted.Searches ?? []).Take(MaximumWantedSearches))
        {
            if (search?.Text is not { Length: > 0 and <= 256 } query) continue;
            var words = query.Split((char[]?)null, 16, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) continue;
            var found = 0;
            var most = search.Scoped ? MaximumFoundInList : MaximumFoundSessions;
            for (var position = 0; position < ordered.Count && found < most; position++)
            {
                if (search.Scoped && listOf(position) != search.ProjectId || !shown(position)) continue;
                string? text = null, project = null;
                var (all, own) = (true, false);
                foreach (var word in words)
                {
                    if (ordered[position].SessionId.Contains(word, StringComparison.OrdinalIgnoreCase)
                        || (text ??= title(position)).Contains(word, StringComparison.OrdinalIgnoreCase)) { own = true; continue; }
                    if ((project ??= projectName(position) ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
                    all = false;
                    break;
                }
                if (!all || !own) continue;
                // One that is listed at first is asked for too, in case the snapshot has no room for all of those; it is not counted.
                if (!listedAtFirst(position)) found++;
                add(position);
            }
        }
    }

    /// <summary>The most sessions a snapshot lists.</summary>
    internal const int MaximumSessions = 500;

    // The newest sessions of the whole workspace that are always listed: what the window shows as recent is exact.
    private const int NewestAlwaysListed = 50;

    // What a session records of where it belongs: its kind of scope, its project, the automation that started it, its space.
    internal readonly record struct SessionScope(string? ScopeKind, string? ProjectId, string? AutomationId, string? RecordedSpace);

    internal static SessionScope ScopeOf(AgentSessionMetadata session, IReadOnlyDictionary<string, SessionViewJournalHeader>? headers, IReadOnlyList<ProjectDescriptor> projects)
    {
        string? scopeKind = null;
        string? projectId = null;
        string? automationId = null;
        // The space the session records: a value that is no identifier names no space, which is the default one.
        var recordedSpace = session.SpaceId;
        if (headers?.TryGetValue(session.SessionId, out var header) == true && header.SessionId == session.SessionId
            && header.CreatedAt == session.CreatedAt && header.WorkingDirectory == session.WorkspacePath)
        {
            // The automation that started the session, as the session itself records it.
            if (header.CreatedBy is { Kind: AltaActorProvenance.AutomationKind, AutomationId: { Length: 36 } automation }) automationId = automation;
            recordedSpace = header.SpaceId ?? recordedSpace;
            if (header.Kind == SessionViewKind.GlobalSession && header.ProjectRef is null)
                scopeKind = "global";
            // The project it records, or the project added since for its folder when it records none of the catalog.
            else if (header.Kind == SessionViewKind.ProjectSession
                && ProjectCatalog.ResolveSessionProject(header.ProjectRef, session.WorkspacePath, projects) is { } project)
            {
                scopeKind = "project";
                projectId = project.Id;
            }
        }
        return new SessionScope(scopeKind, projectId, automationId, recordedSpace);
    }

    /// <summary>
    /// The order in which the sessions are kept while there is room, as positions in <paramref name="ordered"/> (the archived
    /// ones last, then the newest first). The newest of the workspace come first, then every list (a project, or the chats)
    /// gets its next newest session in turn: a project that has many recent sessions takes the room of its own older ones,
    /// never the sessions of another project. The archived sessions follow, the newest first.
    /// </summary>
    internal static IEnumerable<int> FairOrder(IReadOnlyList<AgentSessionMetadata> ordered, Func<int, string?> listOf)
    {
        var live = 0;
        while (live < ordered.Count && ordered[live].ViewState?.Archived != true) live++;
        var newest = Math.Min(NewestAlwaysListed, live);
        for (var position = 0; position < newest; position++) yield return position;

        // The lists by their newest session, each with its sessions from the newest.
        var lists = new List<List<int>>();
        var byList = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        List<int>? chats = null;
        for (var position = 0; position < live; position++)
        {
            List<int>? list;
            if (listOf(position) is { } id)
            {
                if (!byList.TryGetValue(id, out list)) lists.Add(byList[id] = list = []);
            }
            else if ((list = chats) is null)
            {
                lists.Add(chats = list = []);
            }

            list.Add(position);
        }

        for (var depth = 0; ; depth++)
        {
            var any = false;
            foreach (var list in lists)
            {
                if (depth >= list.Count) continue;
                any = true;
                if (list[depth] >= newest) yield return list[depth];
            }

            if (!any) break;
        }

        for (var position = live; position < ordered.Count; position++) yield return position;
    }

    private static void ValidateIdentity(string? value, int maximumLength, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Missing catalog identity.");
        if (value is null) return;
        if (value.Length > maximumLength) throw new InvalidDataException("Catalog identity exceeds the desktop wire limit.");
        ValidateUnicode(value);
    }

    private static string DisplayText(string value, ref bool shortened) => DisplayTextBounded(value, 256, ref shortened);

    private static string DisplayTextBounded(string value, int maximum, ref bool shortened)
    {
        ValidateUnicode(value);
        if (value.Length <= maximum) return value;
        shortened = true;
        var length = char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum;
        return value[..length];
    }

    private static bool ValidLineageId(string value)
    {
        if (value.Length is < 1 or > 256 || value != value.Trim() || string.IsNullOrWhiteSpace(value)) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i])) return false;
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    private static void ValidateUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[i + 1]))
                throw new InvalidDataException("Catalog text contains an invalid Unicode scalar.");
            i++;
        }
    }
}

/// <summary>
/// What a window asks of the sessions beyond what every list shows at first, so that an old session is never out of reach:
/// all of it is optional, and what is beyond a bound is left out.
/// </summary>
internal sealed record WorkspaceRequest
{
    /// <summary>The sessions the window shows whatever their age: those of its tabs, and one it is opening.</summary>
    public string[]? Sessions { get; init; }

    /// <summary>The lists the window shows more rows of than the snapshot has at first.</summary>
    public WorkspaceWantedList[]? Lists { get; init; }

    /// <summary>What the window searches the titles of the sessions for: the sessions found are listed.</summary>
    public WorkspaceWantedSearch[]? Searches { get; init; }

    /// <summary>Whether the window shows the archived sessions, which are otherwise listed only while there is room.</summary>
    public bool Archived { get; init; }

    /// <summary>
    /// The space the window shows, or null when it shows the sessions of every space: the rows of the lists, what a search
    /// finds and the rows said to be left out are those of that space.
    /// </summary>
    public string? Space { get; init; }
}

/// <summary>A text a window searches the sessions for.</summary>
/// <param name="Text">The words: a session found has each of them in its title or its identifier, whatever the case.</param>
/// <param name="Scoped">Whether one list is searched; every list otherwise.</param>
/// <param name="ProjectId">The project of that list, or null for the chats.</param>
internal sealed record WorkspaceWantedSearch(string Text, bool Scoped, string? ProjectId);

/// <summary>A list of sessions a window shows, and how many of its rows.</summary>
/// <param name="ProjectId">The project of the list, or null for the chats.</param>
/// <param name="Count">The rows wanted, from the newest: each comes with its sub-agents.</param>
internal sealed record WorkspaceWantedList(string? ProjectId, int Count);
internal sealed record WorkspaceSnapshot(bool Configured, WorkspaceProject[] Projects, WorkspaceSession[] Sessions,
    bool ProjectsTruncated, bool SessionsTruncated, bool DisplayTextTruncated)
{
    // The project files of the catalog that could not be read, so their projects are not listed; absent when there is none.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? UnreadableProjectFiles { get; init; }

    // How many rows the list of the chats has that the snapshot leaves out, older than the ones it lists; absent when none.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OmittedChatSessions { get; init; }

    // True when the snapshot has no room for every row of the lists the request extends.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WantedTruncated { get; init; }
}
internal sealed record WorkspaceProject(string Id, string Name, string Path, bool Archived)
{
    // How many rows the list of the project has that the snapshot leaves out, older than the ones it lists; absent when none.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OmittedSessions { get; init; }

    // The machine a project that stands for a paired machine groups the sessions of (its sessions of no project of this
    // catalog); absent for a project of the catalog.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachineId { get; init; }

    // The state of that machine: "online", "connecting", "offline" or "unauthorized"; absent for a project of the catalog.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachineState { get; init; }

    // Whether the folder of the project is in no git repository: it has no worktree, no branch and no commit; absent
    // for a project in a repository, for an archived one and for one that stands for a machine.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NoRepository { get; init; }

    // The repository project whose map declares this sub-project, which the Explorer lists it under; absent for a
    // project that is no sub-project.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; init; }

    // The folder of the sub-project in its repository, such as "services/billing"; absent with ParentId.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubPath { get; init; }

    // The name of that repository, which the window shows before the name of the sub-project away from its group.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepositoryName { get; init; }

    // Whether the project is secure: its sessions are confidential ([privacy] pseudonymize of its configuration); absent
    // for a normal project.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Secure { get; init; }
}
internal sealed record WorkspaceSession(string Id, string Title, string? WorkspacePath, string? ProviderKey, DateTimeOffset UpdatedAt,
    string FullTitle, bool FullTitleTruncated, string? ParentSessionId, string? ScopeKind, string? ProjectId, string? LineageIssue,
    DateTimeOffset? CreatedAt, int? MessageCount = null, string? AutomationId = null,
    string? WorktreePath = null, string? WorktreeRoot = null, string? WorktreeName = null, bool WorktreeMissing = false,
    // Where a session that starts with the turns of another one comes from: "fork", or "rewind" for the session that
    // keeps what a rewind removed. Lineage only: the session is not a child of that one.
    string? ForkedFromSessionId = null, string? ForkedFromTitle = null, string? ForkKind = null, int ForkTurnCount = 0,
    // The number of times the session was rewound: a pane that shows it reads its conversation again when it changes.
    int RewindCount = 0,
    // The space the session records; null for a session created before sessions had a space, whose space follows
    // from its project (see SpaceCatalog.ResolveSessionSpace).
    string? SpaceId = null,
    // An archived session is left out of the Explorer and the tabs, and found in the archived sessions; ArchivedAt is
    // null for one archived before the time was kept.
    bool Archived = false, DateTimeOffset? ArchivedAt = null)
{
    // True when the provider of the session keeps a conversation of it of its own (a conversation of the Claude Code CLI,
    // which VS Code and a terminal can open); absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProviderConversation { get; init; }

    // The cloud session the session runs as, or ran as before it was brought back; absent for any other session.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceSessionCloud? Cloud { get; init; }

    // The paired machine that runs the session; absent for a session of this computer.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachineId { get; init; }

    // The name of that machine.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachineName { get; init; }

    // Where the channel to that machine goes while it is online: direct, or relayed by a server.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachineChannel { get; init; }

    // What the session of that machine did when it was listed: working, waiting or idle.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteState { get; init; }

    // The place of its run in the queue of the seats of that machine (1 for the next one); null when it waits for none.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RemoteQueuedPosition { get; init; }

    // The name of its project on that machine.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteProject { get; init; }

    // The name of its provider as that machine names it: a provider of the same key here may be another one.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachineProviderName { get; init; }
}

/// <summary>What the Explorer shows of the cloud session a session runs as, or ran as.</summary>
/// <param name="Id">The cloud session.</param>
/// <param name="Url">Its address on claude.ai, or null.</param>
/// <param name="Title">Its title, or null.</param>
/// <param name="Continued">Whether it was brought back to this computer.</param>
/// <param name="Continuing">Whether it is being brought back now.</param>
internal sealed record WorkspaceSessionCloud(string Id, string? Url, string? Title, bool Continued, bool Continuing)
{
    /// <summary>The view of the cloud state of a listing; null for a session that never ran in the cloud.</summary>
    internal static WorkspaceSessionCloud? Of(CodeAlta.Agent.AgentSessionCloud? cloud)
        => cloud is { Id.Length: > 0 and <= 80 }
            ? new(cloud.Id, cloud.Url is { Length: <= 512 } url && url.StartsWith("https://claude.ai/", StringComparison.Ordinal) ? url : null,
                cloud.Title is { Length: > 0 } title ? title.Length <= 256 ? title : title[..256] : null, cloud.Continued, cloud.Continuing)
            : null;
}
