using System.Text;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>A noncreating runtime observation released only after bounded persisted scope verification.</summary>
/// <param name="Status">Explicit verification outcome; only ok contains state.</param>
/// <param name="State">Point-in-time actor facts, not liveness or permission.</param>
public sealed record OwnedRuntimeObservation(string Status, SessionRuntimeCurrentState? State);

public sealed partial class SessionRuntimeService
{
    // The project catalog answers of observations, kept while the catalog files they come from are unchanged.
    private readonly ObservedProjectOwnership _observedOwnership = new();

    /// <summary>Verifies a saved session's exact header/project scope, then observes its existing actor without activation.</summary>
    /// <remarks>Reads one bounded header, never history. Archived projects refuse. Catalog/header reads and actor observations
    /// are not atomic against external writers or other actors. Absence is unknown/not attached, never idle/completed.
    /// The project catalog check gives the answer of <see cref="ProjectCatalog.ReadBoundedOwnershipAsync"/>, which is
    /// scanned again only when the catalog files it reads changed (see <c>ObservedProjectOwnership</c>).</remarks>
    /// <param name="sessionId">Exact saved identity.</param><param name="createdAt">Exact saved header creation time.</param>
    /// <param name="scope">Global or project scope.</param><param name="projectId">Exact project identity, otherwise null.</param>
    /// <param name="projectPath">Expected catalog path, never a root for arbitrary journal access.</param>
    /// <param name="cancellationToken">Cancels reads and caller waiting.</param>
    /// <returns>Verified observation or explicit refusal.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closed.</exception>
    public async Task<OwnedRuntimeObservation> ObserveOwnedStateAsync(string sessionId, DateTimeOffset createdAt, string scope,
        string? projectId, string? projectPath, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => ObserveOwnedStateBodyAsync(sessionId, createdAt, scope, projectId, projectPath, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<OwnedRuntimeObservation> ObserveOwnedStateBodyAsync(string id, DateTimeOffset createdAt, string scope,
        string? projectId, string? projectPath, CancellationToken token)
    {
        if (scope is not ("global" or "project") || scope == "global" && (projectId is not null || projectPath is not null)
            || scope == "project" && (projectId is null || projectPath is null)) return new("invalid_scope", null);
        var persisted = await _sessionViewCatalog.JournalStore.ReadBoundedHeaderAsync(id, createdAt, token).ConfigureAwait(false);
        if (persisted.Status != BoundedSessionHeaderStatus.Found) return new("metadata_unavailable", null);
        var header = persisted.Header!;
        if (header.SchemaVersion != 1 || header.SessionId != id || header.CreatedAt != createdAt
            || (scope == "global" ? header.Kind != SessionViewKind.GlobalSession || header.ProjectRef is not null
                || header.WorkingDirectory != _projectCatalog.Options.GlobalRoot
                : header.Kind != SessionViewKind.ProjectSession || header.WorkingDirectory != projectPath
                    || await HeaderProjectIdAsync(header, projectId, token).ConfigureAwait(false) != projectId))
            return new("scope_mismatch", null);
        var headerProjectId = scope == "project" ? projectId : header.ProjectRef;
        if (scope == "project")
        {
            var ownership = await _observedOwnership.ReadAsync(_projectCatalog.Options.ProjectsRoot,
                cancellation => _projectCatalog.ReadBoundedOwnershipAsync(projectId, projectPath, cancellation), projectId!, projectPath!, token).ConfigureAwait(false);
            if (ownership.Status != ProjectOwnershipStatus.Match) return new("project_unverified", null);
            if (ownership.Archived != false) return new("archived_project", null);
        }
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessionActors.TryGet(id, out var actor)) return new("ok", new(_runtimeInstanceId, id, false, null));
        return await actor.QueryAsync(_ =>
        {
            lock (_identityGate)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_sessionActors.TryGet(id, out var current) || !ReferenceEquals(current, actor))
                    return ValueTask.FromResult(new OwnedRuntimeObservation("stale_attachment", null));
                if (_entries.TryGetValue(id, out var entry) && (entry.Kind != header.Kind || entry.ProjectId != headerProjectId
                    || entry.WorkingDirectory != header.WorkingDirectory || entry.CreatedAt != header.CreatedAt
                    || entry.ProviderId.Value != header.ProviderId || entry.ProviderKey != header.ProviderKey))
                    return ValueTask.FromResult(new OwnedRuntimeObservation("scope_mismatch", null));
                return ValueTask.FromResult(new OwnedRuntimeObservation("ok", CaptureCurrentState(id)));
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    // The project the header of a project session names, as the runtime binds the session (by its folder) and the lists
    // show it: the one it records, or, when that is no project of the catalog, the project of the catalog in its folder
    // (ProjectCatalog.ResolveSessionProject). The catalog is read only when the header records another project than the
    // one expected, which only a session written before its folder was a project of the catalog does.
    private async ValueTask<string?> HeaderProjectIdAsync(SessionViewJournalHeader header, string? expectedProjectId, CancellationToken cancellationToken)
    {
        if (header.ProjectRef == expectedProjectId) return header.ProjectRef;
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        return ProjectCatalog.ResolveSessionProject(header.ProjectRef, header.WorkingDirectory, projects)?.Id ?? header.ProjectRef;
    }
}

/// <summary>
/// The answers of the bounded project catalog check for the observations of sessions, which ask it for the same few
/// projects every few seconds. An answer is given again while the catalog files the check reads are the same: the flat
/// <c>*.md</c> sources and the <c>readme.md</c> of each folder of the catalog folder, listed in the same order, each
/// with the same attributes, length, last write and creation times, under ancestors with the same attributes. It is
/// kept only when the files were the same before and after the scan that gave it, and at most <see cref="MaximumAge"/>:
/// a source rewritten without changing any of these is seen again by then.
/// </summary>
/// <remarks>The sources described here are those <see cref="ProjectCatalog.ReadBoundedOwnershipAsync"/> reads; a source
/// added to that scan must be described here too.</remarks>
internal sealed class ObservedProjectOwnership
{
    /// <summary>The longest time an answer is given again without a new scan.</summary>
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    // More requested projects than an observation batch names: beyond it the answers start again from none.
    private const int MaximumAnswers = 256;
    private readonly object _gate = new();
    private readonly Func<long> _milliseconds;
    private readonly Dictionary<(string ProjectId, string ProjectPath), (ProjectOwnershipResult Result, long At)> _answers = [];
    private string? _sources;

    internal ObservedProjectOwnership() : this(static () => Environment.TickCount64) { }

    // Test seam: the clock of the answer ages, in milliseconds.
    internal ObservedProjectOwnership(Func<long> milliseconds) => _milliseconds = milliseconds;

    /// <summary>Gives the answer of <paramref name="scan"/> for a project, scanning only when the sources changed.</summary>
    /// <param name="root">The folder of the project catalog that the scan reads.</param>
    /// <param name="scan">The bounded catalog check of this project.</param>
    /// <param name="projectId">The requested project identity.</param>
    /// <param name="projectPath">The requested project path.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What the scan answers for the catalog files as they are.</returns>
    /// <exception cref="OperationCanceledException">The scan was canceled.</exception>
    internal async Task<ProjectOwnershipResult> ReadAsync(string root, Func<CancellationToken, Task<ProjectOwnershipResult>> scan,
        string projectId, string projectPath, CancellationToken cancellationToken)
    {
        var key = (projectId, projectPath);
        var now = _milliseconds();
        var before = Describe(root);
        if (before is not null)
        {
            lock (_gate)
            {
                if (before == _sources && _answers.TryGetValue(key, out var known) && now - known.At < MaximumAge.TotalMilliseconds)
                    return known.Result;
            }
        }
        var result = await scan(cancellationToken).ConfigureAwait(false);
        if (before is not null && before == Describe(root))
        {
            lock (_gate)
            {
                if (before != _sources || _answers.Count >= MaximumAnswers)
                {
                    _answers.Clear();
                    _sources = before;
                }
                _answers[key] = (result, now);
            }
        }
        return result;
    }

    // The catalog files the scan reads, described by their attributes and stamps without being read; null when they
    // cannot be described, which leaves the answer to the scan.
    internal static string? Describe(string root)
    {
        if (root.Length > 4096 || !Path.IsPathFullyQualified(root)) return null;
        var text = new StringBuilder();
        try
        {
            // Every ancestor, as the scan checks them from the root of the file system down to the catalog folder: only
            // their attributes count, not their times, which change with every file created beside them.
            FileAttributes? folder = null;
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                var attributes = Describe(text, directory.FullName, directory.FullName, source: false);
                folder ??= attributes ?? 0;
            }
            if (folder is not { } found || !found.HasFlag(FileAttributes.Directory)) return text.ToString();
            var entries = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
            {
                // The scan stops at the entry over its limit: what is listed after it does not change its answer.
                if (++entries > ProjectCatalog.MaximumOwnershipEntries + 1) break;
                var name = Path.GetFileName(entry);
                // A flat source is a *.md file; any other entry counts by its attributes, and a folder by its readme.md.
                var flat = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                if (Describe(text, entry, name, flat) is { } attributes && attributes.HasFlag(FileAttributes.Directory)
                    && !attributes.HasFlag(FileAttributes.ReparsePoint))
                    Describe(text, Path.Combine(entry, "readme.md"), name + "/readme.md", source: true);
            }
            return text.ToString();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // One line for a path, from one status read: its attributes and, for a file the scan may read, its length, last
    // write and creation times; or that it is missing (null).
    private static FileAttributes? Describe(StringBuilder text, string path, string name, bool source)
    {
        var info = new FileInfo(path);
        var attributes = info.Attributes;
        text.Append(name).Append('|');
        if ((int)attributes == -1)
        {
            text.Append("missing\n");
            return null;
        }
        text.Append((int)attributes);
        if (source && !attributes.HasFlag(FileAttributes.Directory))
            text.Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|').Append(info.CreationTimeUtc.Ticks);
        text.Append('\n');
        return attributes;
    }
}
