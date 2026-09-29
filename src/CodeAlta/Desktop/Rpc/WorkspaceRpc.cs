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
            return ProjectSnapshot(snapshot.Projects, snapshot.Sessions, snapshot.SessionHeaders);
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
            var headers = new Dictionary<string, SessionViewJournalHeader>(StringComparer.Ordinal);
            foreach (var session in sessions.OrderByDescending(static value => value.UpdatedAt)
                         .ThenByDescending(static value => value.SessionId, StringComparer.Ordinal).Take(500))
            {
                if (session.SessionId.Length is < 1 or > 256 || session.CreatedAt == default) continue;
                SessionViewJournalHeader? header;
                try { header = await journals.ReadHeaderAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    continue; // Unreadable scope evidence cannot suppress an otherwise visible session.
                }
                if (header is not null && header.SessionId == session.SessionId && header.CreatedAt == session.CreatedAt
                    && header.WorkingDirectory == session.WorkspacePath)
                    headers.TryAdd(session.SessionId, header);
            }
            return ProjectSnapshot(projects, sessions, headers);
        }
    }

    internal static WorkspaceSnapshot ProjectSnapshot(
        IReadOnlyList<ProjectDescriptor> projects, IReadOnlyList<AgentSessionMetadata> sessions,
        IReadOnlyDictionary<string, SessionViewJournalHeader>? headers = null)
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
            var persistedTitle = (session.Details as RawApiSessionMetadataDetails)?.Title;
            var sourceTitle = !string.IsNullOrWhiteSpace(persistedTitle) ? persistedTitle
                : !string.IsNullOrWhiteSpace(session.Summary) ? session.Summary : session.SessionId;
            var title = DisplayText(sourceTitle, ref shortened);
            var fullTitle = DisplayTextBounded(sourceTitle, 4096, ref shortened);
            var parent = session.ParentSessionId ?? session.ViewState?.ParentSessionId;
            if (string.IsNullOrWhiteSpace(parent)) parent = null;
            var lineageIssue = parent is not null && !ValidLineageId(parent) ? "invalid_parent" : null;
            if (lineageIssue is not null) parent = null;
            string? scopeKind = null;
            string? projectId = null;
            if (headers?.TryGetValue(session.SessionId, out var header) == true && header.SessionId == session.SessionId
                && header.CreatedAt == session.CreatedAt && header.WorkingDirectory == session.WorkspacePath)
            {
                if (header.Kind == SessionViewKind.GlobalSession && header.ProjectRef is null)
                    scopeKind = "global";
                else if (header.Kind == SessionViewKind.ProjectSession && header.ProjectRef is { } reference
                    && projects.Any(project => project.Id == reference && project.ProjectPath == session.WorkspacePath))
                {
                    scopeKind = "project";
                    projectId = reference;
                }
            }
            var cost = 512 + 64 + 6 * (session.SessionId.Length + title.Length + fullTitle.Length + (parent?.Length ?? 0)
                + (session.WorkspacePath?.Length ?? 0) + (session.ProviderKey?.Length ?? 0) + (projectId?.Length ?? 0));
            if (cost > remaining) break;
            remaining -= cost;
            displayedSessions.Add(new WorkspaceSession(session.SessionId, title, session.WorkspacePath, session.ProviderKey, session.UpdatedAt,
                fullTitle, sourceTitle.Length > 4096, parent, scopeKind, projectId, lineageIssue,
                session.CreatedAt.Year > 1 ? session.CreatedAt : null, session.ViewState?.MessageCount is >= 0 ? session.ViewState.MessageCount : null));
        }
        return new WorkspaceSnapshot(true, displayedProjects.ToArray(), displayedSessions.ToArray(),
            displayedProjects.Count < projects.Count, displayedSessions.Count < sessions.Count, shortened);
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

internal sealed record WorkspaceRequest;
internal sealed record WorkspaceSnapshot(bool Configured, WorkspaceProject[] Projects, WorkspaceSession[] Sessions,
    bool ProjectsTruncated, bool SessionsTruncated, bool DisplayTextTruncated);
internal sealed record WorkspaceProject(string Id, string Name, string Path, bool Archived);
internal sealed record WorkspaceSession(string Id, string Title, string? WorkspacePath, string? ProviderKey, DateTimeOffset UpdatedAt,
    string FullTitle, bool FullTitleTruncated, string? ParentSessionId, string? ScopeKind, string? ProjectId, string? LineageIssue,
    DateTimeOffset? CreatedAt, int? MessageCount = null);
