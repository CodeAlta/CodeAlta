using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>Persisted catalog browsing for one explicitly admitted task-owned copy.</summary>
/// <remarks>
/// Listing can create/rebuild cache/cache.sqlite3 and SQLite sidecars. It never falls back to header scans.
/// AgentSessionCatalog starts its shared load using Task.Run and CancellationToken.None: canceling a waiter
/// does not stop that load. NeoAstra stops waiting for invocations after five seconds during teardown.
/// Consequently catalog/cache work can outlive bridge teardown; this service adds no lifetime guarantee.
/// Row/string/wire limits bound the response, not the underlying whole-catalog scan or shared snapshot load.
/// </remarks>
[NeoRpcService("workspace", Version = 1)]
internal sealed partial class WorkspaceService
{
    private readonly Func<CancellationToken, Task<WorkspaceSnapshot>>? _read;

    internal WorkspaceService(CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        _readHistory = reads.ReadHistoryPageAsync;
        _readHistoryTail = reads.ReadHistoryTailPageAsync;
        _readTimeline = reads.ReadTimelinePageAsync;
        _readHistorySource = reads.ReadHistorySourceAsync;
        _read = async token =>
        {
            var actual = reads.ReadSnapshotAsync(token);
            var snapshot = await actual.ConfigureAwait(false);
            var (projects, sessions) = WithExistingFolders(snapshot.Projects, snapshot.Sessions, Directory.Exists);
            return ProjectSnapshot(projects, sessions, snapshot.SessionHeaders);
        };
    }

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
        _read = cancellationToken => ReadAsync(projects.LoadAsync,
            token => sessions.ListSessionsAsync(filter: null, cancellationToken: token), journals, cancellationToken);
    }

    [NeoRpcMethod("snapshot")]
    public Task<WorkspaceSnapshot> SnapshotAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _read is null
            ? Task.FromResult(new WorkspaceSnapshot(false, [], [], false, false, false))
            : _read(cancellationToken);
    }

    // The actual RPC route uses this seam; fixtures supply only completed/faulted/canceled literal reads.
    internal static Task<WorkspaceSnapshot> ReadAsync(
        Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> loadProjects,
        Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> loadSessions,
        CancellationToken cancellationToken)
        => ReadAsync(loadProjects, loadSessions, null, cancellationToken);

    private static Task<WorkspaceSnapshot> ReadAsync(
        Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> loadProjects,
        Func<CancellationToken, IAsyncEnumerable<AgentSessionMetadata>> loadSessions,
        SessionViewJournalStore? journals,
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
            if (journals is null) return ProjectSnapshot(projects, sessions);
            // Unreadable scope evidence cannot suppress an otherwise visible session.
            var headers = await CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace.ReadSessionHeadersAsync(sessions, journals, cancellationToken).ConfigureAwait(false);
            var shown = WithExistingFolders(projects, sessions, Directory.Exists);
            return ProjectSnapshot(shown.Projects, shown.Sessions, headers);
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

    internal static WorkspaceSnapshot ProjectSnapshot(
        IReadOnlyList<ProjectDescriptor> projects, IReadOnlyList<AgentSessionMetadata> sessions,
        IReadOnlyDictionary<string, SessionViewJournalHeader>? headers = null,
        Func<string, (string Root, string Name, bool Missing)>? worktree = null)
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
        var displayedSessions = new List<WorkspaceSession>();
        var shortened = false;
        // Conservative JSON bound: six bytes per UTF-16 unit, 512 bytes per row for property names,
        // booleans/timestamps/punctuation, and 2048 reserved for the snapshot/envelope. Each session
        // additionally reserves 64 bytes for createdAt (33 ISO characters, quotes/property/comma,
        // including a possibly escaped '+' offset). Keep the overall cap below 1 MiB RPC default.
        var remaining = 700 * 1024 - 2048;
        foreach (var project in projects.OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Id, StringComparer.Ordinal).ThenBy(value => value.ProjectPath, StringComparer.Ordinal))
        {
            if (displayedProjects.Count == 200) break;
            var name = DisplayText(string.IsNullOrWhiteSpace(project.DisplayName) ? project.Id : project.DisplayName, ref shortened);
            var cost = 512 + 6 * (project.Id.Length + name.Length + project.ProjectPath.Length);
            if (cost > remaining) break;
            remaining -= cost;
            displayedProjects.Add(new WorkspaceProject(project.Id, name, project.ProjectPath, project.Archived));
        }
        foreach (var session in sessions.OrderByDescending(value => value.UpdatedAt)
                     .ThenByDescending(value => value.SessionId, StringComparer.Ordinal))
        {
            if (displayedSessions.Count == 500) break;
            var parent = session.ParentSessionId ?? session.ViewState?.ParentSessionId;
            if (string.IsNullOrWhiteSpace(parent)) parent = null;
            var lineageIssue = parent is not null && !ValidLineageId(parent) ? "invalid_parent" : null;
            if (lineageIssue is not null) parent = null;
            string? scopeKind = null;
            string? projectId = null;
            string? automationId = null;
            // The worktree the session works in, as the session records it: it is shown also once its folder is
            // gone, until the session continues in the folder of its project.
            string? worktreePath = null, worktreeRoot = null, worktreeName = null;
            var worktreeMissing = false;
            if (session.WorktreePath is { Length: > 0 and <= 4096 } recorded && !string.IsNullOrWhiteSpace(recorded) && !recorded.Any(char.IsControl))
            {
                (worktreeRoot, worktreeName, worktreeMissing) = (worktree ?? DescribeWorktree)(recorded);
                worktreePath = recorded;
            }
            if (headers?.TryGetValue(session.SessionId, out var header) == true && header.SessionId == session.SessionId
                && header.CreatedAt == session.CreatedAt && header.WorkingDirectory == session.WorkspacePath)
            {
                // The automation that started the session, as the session itself records it.
                if (header.CreatedBy is { Kind: AltaActorProvenance.AutomationKind, AutomationId: { Length: 36 } automation }) automationId = automation;
                if (header.Kind == SessionViewKind.GlobalSession && header.ProjectRef is null)
                    scopeKind = "global";
                else if (header.Kind == SessionViewKind.ProjectSession && header.ProjectRef is { } reference
                    && projects.Any(project => project.Id == reference && project.ProjectPath == session.WorkspacePath))
                {
                    scopeKind = "project";
                    projectId = reference;
                }
            }
            var persistedTitle = (session.Details as RawApiSessionMetadataDetails)?.Title;
            var hasTitle = !string.IsNullOrWhiteSpace(persistedTitle);
            // A session that was never named keeps the text it was created with as its saved title, while its summary
            // follows the conversation: it is listed by the first line of that summary, as the terminal UI does.
            var neverNamed = hasTitle && IsCreationTitle(persistedTitle!, scopeKind,
                projects.FirstOrDefault(project => project.Id == projectId)?.DisplayName);
            var sourceTitle = hasTitle && !neverNamed ? persistedTitle!
                : FirstLine(session.Summary) ?? (hasTitle ? persistedTitle! : session.SessionId);
            var title = DisplayText(sourceTitle, ref shortened);
            var fullTitle = DisplayTextBounded(sourceTitle, 4096, ref shortened);
            var cost = 512 + 64 + 6 * (session.SessionId.Length + title.Length + fullTitle.Length + (parent?.Length ?? 0)
                + (session.WorkspacePath?.Length ?? 0) + (session.ProviderKey?.Length ?? 0) + (projectId?.Length ?? 0) + (automationId?.Length ?? 0)
                + (worktreePath?.Length ?? 0) + (worktreeRoot?.Length ?? 0) + (worktreeName?.Length ?? 0));
            if (cost > remaining) break;
            remaining -= cost;
            displayedSessions.Add(new WorkspaceSession(session.SessionId, title, session.WorkspacePath, session.ProviderKey, session.UpdatedAt,
                fullTitle, sourceTitle.Length > 4096, parent, scopeKind, projectId, lineageIssue,
                session.CreatedAt.Year > 1 ? session.CreatedAt : null, session.ViewState?.MessageCount is >= 0 ? session.ViewState.MessageCount : null,
                automationId, worktreePath, worktreeRoot, worktreeName, worktreeMissing));
        }
        return new WorkspaceSnapshot(true, displayedProjects.ToArray(), displayedSessions.ToArray(),
            displayedProjects.Count < projects.Count, displayedSessions.Count < sessions.Count, shortened);
    }

    // The titles a session is created with, as SessionRuntimeService names it: the project or "Global Session", or the
    // first line of the summary it is created with. A saved title that is one of them was never chosen by a person.
    private static bool IsCreationTitle(string title, string? scopeKind, string? projectName) => scopeKind switch
    {
        "project" => projectName is not null && (title == projectName || title == $"Project session for {projectName}."),
        "global" => title is "Global Session" or "Global overview and coordination session.",
        _ => false,
    };

    private static string? FirstLine(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Split(['\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim() is { Length: > 0 } line ? line : null;

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

internal sealed record WorkspaceRequest;
internal sealed record WorkspaceSnapshot(bool Configured, WorkspaceProject[] Projects, WorkspaceSession[] Sessions,
    bool ProjectsTruncated, bool SessionsTruncated, bool DisplayTextTruncated);
internal sealed record WorkspaceProject(string Id, string Name, string Path, bool Archived);
internal sealed record WorkspaceSession(string Id, string Title, string? WorkspacePath, string? ProviderKey, DateTimeOffset UpdatedAt,
    string FullTitle, bool FullTitleTruncated, string? ParentSessionId, string? ScopeKind, string? ProjectId, string? LineageIssue,
    DateTimeOffset? CreatedAt, int? MessageCount = null, string? AutomationId = null,
    string? WorktreePath = null, string? WorktreeRoot = null, string? WorktreeName = null, bool WorktreeMissing = false);
