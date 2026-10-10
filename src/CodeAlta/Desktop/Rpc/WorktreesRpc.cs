using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the git worktrees of a project: it lists the checkouts of the project's repository,
/// removes a worktree, lists the branches a checkout can move to and moves it, and reads and writes where new
/// worktrees are placed.
/// </summary>
/// <remarks>
/// <para>
/// A checkout a session is at work in is neither removed nor moved to another branch: the answer is
/// <c>in_use</c>. A worktree is named by its folder, and only a folder of the project's own repository is
/// accepted.
/// </para>
/// <para>
/// The window of the worktrees reads an inventory: every checkout git lists, whether a session of the catalog
/// records it or not, with the sessions that do. It removes several worktrees in one request, each one checked
/// again right before it goes, and opens the code editor on a checkout. Whether a checkout is in use is only
/// what the running sessions say: what a session recorded in the past never protects or releases a checkout.
/// </para>
/// </remarks>
[NeoRpcService("worktrees", Version = 1)]
internal sealed class WorktreesService
{
    /// <summary>The worktrees one request removes at most.</summary>
    internal const int MaximumRemovals = 256;

    /// <summary>The checkouts an inventory lists at most; it says when git lists more.</summary>
    internal const int MaximumCheckouts = 512;

    /// <summary>The sessions an inventory names for one checkout: the ones at work, then the most recent.</summary>
    internal const int MaximumSessionsPerCheckout = 5;

    private const int MaximumPathLength = 4096;
    private const int MaximumTitleLength = 200;
    private const int MaximumIdLength = 256;
    private const int AbbreviatedCommitLength = 7;

    private readonly GitWorktreeService? _worktrees;
    private readonly ProjectCatalog? _projects;
    private readonly CodeAltaConfigStore? _config;
    private readonly Func<IReadOnlyList<SessionWorkFolder>>? _busy;
    private readonly string? _epoch;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentSessionMetadata>>>? _sessions;
    private readonly DiskFolders? _folders;
    private readonly DesktopEditorView? _editor;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal WorktreesService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="worktrees">The worktrees of the host.</param>
    /// <param name="projects">The host's project catalog; it maps a project id to its folder.</param>
    /// <param name="config">The configuration the location of new worktrees is written to.</param>
    /// <param name="busy">The sessions that are at work, each with the folder it works in.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    internal WorktreesService(GitWorktreeService worktrees, ProjectCatalog projects, CodeAltaConfigStore config,
        Func<IReadOnlyList<SessionWorkFolder>> busy, string epoch)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(busy);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_worktrees, _projects, _config, _busy, _epoch) = (worktrees, projects, config, busy, epoch);
    }

    /// <summary>Creates the service for an owned host whose window manages the worktrees of a project.</summary>
    /// <param name="worktrees">The worktrees of the host.</param>
    /// <param name="projects">The host's project catalog; it maps a project id to its folder.</param>
    /// <param name="config">The configuration the location of new worktrees is written to.</param>
    /// <param name="busy">The sessions that are at work, each with the folder it works in.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="sessions">The sessions of the catalog, as it lists them: what each records says when a checkout was last used.</param>
    /// <param name="folders">The folders of the disk the code editor opens on: a worktree is one of them.</param>
    /// <param name="editor">The window's code editor.</param>
    internal WorktreesService(GitWorktreeService worktrees, ProjectCatalog projects, CodeAltaConfigStore config,
        Func<IReadOnlyList<SessionWorkFolder>> busy, string epoch,
        Func<CancellationToken, Task<IReadOnlyList<AgentSessionMetadata>>> sessions, DiskFolders folders, DesktopEditorView editor)
        : this(worktrees, projects, config, busy, epoch)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(editor);
        (_sessions, _folders, _editor) = (sessions, folders, editor);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Lists the checkouts of a project's repository, the main one first, and where a new worktree would go.</summary>
    [NeoRpcMethod("list")]
    public async Task<WorktreesListResponse> ListAsync(WorktreesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (root, refusal) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, allowArchived: true, cancellationToken).ConfigureAwait(false);
        if (root is null) return new(refusal, request.ProjectId, [], null);
        var checkout = GitWorktreeService.FindCheckoutRoot(root);
        if (checkout is null || await _worktrees!.ListAsync(root, cancellationToken).ConfigureAwait(false) is not { } worktrees)
            return new("not_repository", request.ProjectId, [], null);
        // The folder of the project inside its checkout: the same one in every other checkout.
        var below = Path.GetRelativePath(checkout, root);
        var busy = BusyCheckouts();
        var project = await _projects!.GetByIdAsync(request.ProjectId!, cancellationToken).ConfigureAwait(false);
        var parent = project is null ? null : _worktrees.ParentFolder(_worktrees.ReadSettings(), project, checkout);
        return new("ok", request.ProjectId,
            [.. worktrees.Select(worktree => new WorktreeItem(worktree.Path, worktree.Name, worktree.Branch,
                worktree.Head is { Length: >= AbbreviatedCommitLength } head ? head[..AbbreviatedCommitLength] : worktree.Head,
                // The checkout the project lives in is the one that is not removed here, also when the project is itself a worktree.
                worktree.Main || worktree.Path.Equals(checkout, PathComparison), worktree.Locked, worktree.Missing,
                below == "." ? worktree.Path : Path.Combine(worktree.Path, below), busy.Contains(worktree.Path)))],
            parent);
    }

    /// <summary>
    /// Removes a worktree of a project's repository. A worktree that holds changes that are not committed is
    /// answered with <c>dirty</c> unless the request forces it; one a session is at work in, with <c>in_use</c>.
    /// </summary>
    [NeoRpcMethod("remove", TimeoutMilliseconds = 600_000)]
    public async Task<WorktreeChangeResponse> RemoveAsync(WorktreeRemoveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (root, refusal) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, allowArchived: false, cancellationToken).ConfigureAwait(false);
        if (root is null) return new(refusal);
        if (!ValidPath(request.Path)) return new("invalid_request");
        // The folder may be gone already: the repository still lists the worktree, and that entry is what goes.
        if (await _worktrees!.ListAsync(root, cancellationToken).ConfigureAwait(false) is not { } worktrees) return new("not_repository");
        if (GitWorktreeService.Find(worktrees, request.Path!) is not { } target) return new("not_worktree");
        // A locked worktree is refused here: git would refuse it too, or leave one whose folder is gone listed without saying so.
        if (Protection(target, GitWorktreeService.FindCheckoutRoot(root), BusyCheckouts().Contains(target.Path)) is { } protection) return new(protection);
        // Not the caller's token: a removal that started is not left half done because a page went away.
        var outcome = await _worktrees.RemoveAsync(root, target.Path, request.Force, CancellationToken.None).ConfigureAwait(false);
        return new(outcome.Status, outcome.Message, outcome.BranchKept);
    }

    /// <summary>
    /// Lists every checkout git knows for a project's repository, the main one first, each with what protects it
    /// from removal and with the sessions of the catalog that record it. A checkout no session records any more
    /// is listed like the others: its last use is not known.
    /// </summary>
    [NeoRpcMethod("inventory")]
    public async Task<WorktreeInventoryResponse> InventoryAsync(WorktreesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (root, refusal) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, allowArchived: true, cancellationToken).ConfigureAwait(false);
        if (root is null) return new(refusal, request.ProjectId, [], false);
        var checkout = GitWorktreeService.FindCheckoutRoot(root);
        if (checkout is null || await _worktrees!.ListAsync(root, cancellationToken).ConfigureAwait(false) is not { } listed)
            return new("not_repository", request.ProjectId, [], false);
        // What a window can show: the first ones git lists, the main checkout among them.
        IReadOnlyList<GitWorktree> worktrees = listed.Count > MaximumCheckouts ? [.. listed.Take(MaximumCheckouts)] : listed;
        var below = Path.GetRelativePath(checkout, root);
        var working = _busy!();
        var busy = BusyCheckouts(working);
        var running = new HashSet<string>(working.Select(static session => session.SessionId), StringComparer.Ordinal);

        // What the catalog cannot say is not known: the checkouts are listed all the same.
        IReadOnlyList<AgentSessionMetadata> sessions = [];
        var sessionsKnown = _sessions is not null;
        if (_sessions is not null)
        {
            try
            {
                sessions = await _sessions(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Data.Common.DbException)
            {
                sessionsKnown = false;
            }
        }

        // The sessions of each checkout, by the folder each records: its worktree, else the folder it was created in.
        var recorded = new Dictionary<string, List<AgentSessionMetadata>>(PathComparer);
        var owners = new Dictionary<string, string?>(PathComparer);
        foreach (var session in sessions)
        {
            var folder = string.IsNullOrWhiteSpace(session.WorktreePath) ? session.WorkspacePath : session.WorktreePath;
            if (session.SessionId is not { Length: > 0 and <= MaximumIdLength } || !ValidPath(folder)) continue;
            if (!owners.TryGetValue(folder!, out var owner)) owners[folder!] = owner = Owner(worktrees, folder!);
            if (owner is null) continue;
            if (!recorded.TryGetValue(owner, out var list)) recorded[owner] = list = [];
            list.Add(session);
        }

        return new("ok", request.ProjectId,
            [.. worktrees.Select(worktree =>
            {
                var own = recorded.GetValueOrDefault(worktree.Path) ?? [];
                var project = worktree.Path.Equals(checkout, PathComparison);
                var inUse = busy.Contains(worktree.Path);
                return new WorktreeInventoryItem(worktree.Path, worktree.Name, worktree.Branch,
                    worktree.Head is { Length: >= AbbreviatedCommitLength } head ? head[..AbbreviatedCommitLength] : worktree.Head,
                    worktree.Main || project, project, worktree.Locked, worktree.Missing,
                    below == "." ? worktree.Path : Path.Combine(worktree.Path, below), inUse,
                    Protection(worktree, checkout, inUse),
                    [.. own.OrderByDescending(session => running.Contains(session.SessionId)).ThenByDescending(static session => session.UpdatedAt)
                        .ThenBy(static session => session.SessionId, StringComparer.Ordinal).Take(MaximumSessionsPerCheckout)
                        .Select(session => new WorktreeSessionItem(session.SessionId, Title(session), session.UpdatedAt, running.Contains(session.SessionId)))],
                    own.Count, own.Count == 0 ? null : own.Max(static session => session.UpdatedAt));
            })],
            sessionsKnown, listed.Count > MaximumCheckouts);
    }

    /// <summary>
    /// Removes several worktrees of a project's repository, one after the other, and answers for each. Every
    /// folder is looked up again in what git lists right before it goes: the checkout of the project, a locked
    /// worktree and one a session is at work in stay. Changes that are not committed are thrown away only for a
    /// folder the request also names in <see cref="WorktreeRemoveManyRequest.Discard"/>; the others are answered
    /// with <c>dirty</c>. A branch is deleted only when the request asks for it, and then only one whose commits
    /// are all in another branch.
    /// </summary>
    [NeoRpcMethod("removeMany", TimeoutMilliseconds = 600_000)]
    public async Task<WorktreeRemoveManyResponse> RemoveManyAsync(WorktreeRemoveManyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (root, refusal) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, allowArchived: false, cancellationToken).ConfigureAwait(false);
        if (root is null) return new(refusal, []);
        if (request.Paths is not { Length: > 0 and <= MaximumRemovals } paths || !paths.All(ValidPath)
            || request.Discard is { } named && (named.Length > paths.Length || !named.All(ValidPath)))
            return new("invalid_request", []);
        var asked = new HashSet<string>(PathComparer);
        if (!paths.All(path => asked.Add(Normalize(path)))) return new("invalid_request", []);
        var discard = new HashSet<string>((request.Discard ?? []).Select(Normalize), PathComparer);
        // Nothing is thrown away for a folder that was not asked to go.
        if (!discard.IsSubsetOf(asked)) return new("invalid_request", []);
        if (await _worktrees!.ListAsync(root, cancellationToken).ConfigureAwait(false) is not { } first) return new("not_repository", []);
        var gone = new HashSet<string>(first.Where(static worktree => worktree.Missing).Select(static worktree => worktree.Path), PathComparer);
        var checkout = GitWorktreeService.FindCheckoutRoot(root);

        var results = new List<WorktreeRemoval>(paths.Length);
        foreach (var path in paths)
        {
            // The window that asked is gone: what was not started is left alone.
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new(path, "canceled"));
                continue;
            }

            results.Add(await RemoveOneAsync(root, checkout, Normalize(path), path, gone, discard, request.DeleteMergedBranches).ConfigureAwait(false));
        }

        return new("ok", [.. results]);
    }

    // One worktree of a request: what git lists now and the sessions at work now decide, not what the window showed.
    private async Task<WorktreeRemoval> RemoveOneAsync(string root, string? checkout, string folder, string asked, HashSet<string> gone, HashSet<string> discard, bool deleteBranch)
    {
        try
        {
            // Not the caller's token: a removal that started is not left half done because a page went away.
            if (await _worktrees!.ListAsync(root, CancellationToken.None).ConfigureAwait(false) is not { } worktrees) return new(asked, "not_repository");
            if (worktrees.FirstOrDefault(worktree => worktree.Path.Equals(folder, PathComparison)) is not { } target)
                // A folder that was gone is no longer listed either: something else made git forget it meanwhile,
                // which is what was asked. Nothing here prunes.
                return new(asked, gone.Contains(folder) ? "ok" : "not_worktree");
            var busy = BusyCheckouts(_busy!()).Contains(target.Path);
            if (Protection(target, checkout, busy) is { } protection) return new(asked, protection);
            var outcome = await _worktrees.RemoveAsync(root, target.Path, discard.Contains(folder), deleteBranch, CancellationToken.None).ConfigureAwait(false);
            return new(asked, outcome.Status, outcome.Message, outcome.BranchKept, outcome.BranchDeleted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // One folder that cannot be handled does not stop the others.
            return new(asked, "failed", exception.Message is { Length: > 0 and <= 600 } message ? message : null);
        }
    }

    /// <summary>
    /// Opens the code editor on a checkout of a project's repository: the editor of the project for the checkout
    /// the project lives in, and an editor on the folder of the project in that checkout for any other one. The
    /// folder is looked up in what git lists: nothing else is opened.
    /// </summary>
    [NeoRpcMethod("openEditor")]
    public async Task<WorktreeChangeResponse> OpenEditorAsync(WorktreeOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (root, refusal) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, allowArchived: false, cancellationToken).ConfigureAwait(false);
        if (root is null) return new(refusal);
        if (_folders is null || _editor is null) return new("unavailable");
        if (!ValidPath(request.Path)) return new("invalid_request");
        if (await _worktrees!.ListAsync(root, cancellationToken).ConfigureAwait(false) is not { } worktrees) return new("not_repository");
        var folder = Normalize(request.Path!);
        if (worktrees.FirstOrDefault(worktree => worktree.Path.Equals(folder, PathComparison)) is not { } target) return new("not_worktree");
        if (target.Missing || !Directory.Exists(target.Path)) return new("worktree_missing");
        var checkout = GitWorktreeService.FindCheckoutRoot(root);
        if (target.Path.Equals(checkout, PathComparison)) return new(_editor.Open(request.ProjectId!, null, null, null) ? "ok" : "no_window");
        // The folder of the project in that checkout, when the branch it is on has it.
        var below = checkout is null ? "." : Path.GetRelativePath(checkout, root);
        var shown = below == "." ? target.Path : Path.Combine(target.Path, below);
        if (!Directory.Exists(shown)) shown = target.Path;
        // The tab is named by the worktree, whatever the folder of the project is called inside it.
        return new(_editor.OpenFolder(_folders.Give(shown) with { Name = target.Name }, null, null, null) ? "ok" : "no_window");
    }

    /// <summary>Lists the branches the checkout of a project, or one of its worktrees, can move to.</summary>
    [NeoRpcMethod("branches")]
    public async Task<WorktreeBranchesResponse> BranchesAsync(WorktreeBranchesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (folder, refusal) = await CheckoutAsync(request.ExpectedEpoch, request.ProjectId, request.Worktree, cancellationToken).ConfigureAwait(false);
        if (folder is null) return new(refusal, [], false);
        if (await _worktrees!.ListBranchesAsync(folder, cancellationToken).ConfigureAwait(false) is not { } branches) return new("not_repository", [], false);
        return new("ok",
            [.. branches.Select(static branch => new WorktreeBranch(branch.Name, branch.Current, branch.Remote, branch.Worktree,
                branch.Worktree is null ? null : Path.GetFileName(branch.Worktree)))],
            GitWorktreeService.FindCheckoutRoot(folder) is { } checkout && BusyCheckouts().Contains(checkout));
    }

    /// <summary>
    /// Moves the checkout of a project, or one of its worktrees, to a branch, or to a new branch. A checkout a
    /// session is at work in is answered with <c>in_use</c>; what git refuses comes back with its message.
    /// </summary>
    [NeoRpcMethod("switch", TimeoutMilliseconds = 600_000)]
    public async Task<WorktreeChangeResponse> SwitchAsync(WorktreeSwitchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (folder, refusal) = await CheckoutAsync(request.ExpectedEpoch, request.ProjectId, request.Worktree, cancellationToken).ConfigureAwait(false);
        if (folder is null) return new(refusal);
        if (!GitWorktreeService.IsReferenceName(request.Branch)) return new("invalid_request");
        if (GitWorktreeService.FindCheckoutRoot(folder) is not { } checkout) return new("not_repository");
        if (BusyCheckouts().Contains(checkout)) return new("in_use");
        var outcome = await _worktrees!.SwitchAsync(folder, request.Branch!, request.Create, CancellationToken.None).ConfigureAwait(false);
        return new(outcome.Status, outcome.Message);
    }

    /// <summary>Reads where new worktrees are placed.</summary>
    [NeoRpcMethod("settings")]
    public Task<WorktreeSettingsResponse> SettingsAsync(WorktreeSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(Refuse(request.ExpectedEpoch) is { } refused ? new WorktreeSettingsResponse(refused, null, null, null) : ReadSettings("ok"));
    }

    /// <summary>Writes where new worktrees are placed, in the configuration file of the user.</summary>
    [NeoRpcMethod("saveSettings")]
    public Task<WorktreeSettingsResponse> SaveSettingsAsync(WorktreeSettingsSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Task.FromResult(new WorktreeSettingsResponse(refused, null, null, null));
        var folder = string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder.Trim();
        if (request.Location is not (WorktreeSettings.GlobalName or WorktreeSettings.ProjectName or WorktreeSettings.CustomName)
            || folder is { Length: > MaximumPathLength }
            || (folder is not null && WorktreeSettings.ResolveFolder(folder) is null)
            || (request.Location == WorktreeSettings.CustomName && folder is null))
            return Task.FromResult(ReadSettings("invalid_request"));
        try
        {
            _config!.SaveGlobalWorktreeSettings(request.Location, folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Task.FromResult(ReadSettings("save_failed"));
        }

        return Task.FromResult(ReadSettings("ok"));
    }

    private WorktreeSettingsResponse ReadSettings(string status)
    {
        var settings = _worktrees!.ReadSettings();
        string? folder = null;
        try
        {
            folder = _config!.LoadGlobal().Worktrees?.Folder;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A file that cannot be read has no folder to show.
        }

        return new(status, WorktreeSettings.NameOf(settings.Location), folder, _worktrees.GlobalFolder);
    }

    // The roots of the checkouts a session is at work in. A session works in the nearest checkout that holds
    // its folder: a worktree placed inside the repository is not the repository's own checkout.
    private HashSet<string> BusyCheckouts() => BusyCheckouts(_busy!());

    private static HashSet<string> BusyCheckouts(IReadOnlyList<SessionWorkFolder> working)
    {
        var checkouts = new HashSet<string>(PathComparer);
        foreach (var session in working)
        {
            if (GitWorktreeService.FindCheckoutRoot(session.Folder) is { } checkout) checkouts.Add(checkout);
        }

        return checkouts;
    }

    // Why a checkout is not removed: it is the one of the project or the main one of the repository, a session is
    // at work in it, or git keeps it. Null for a worktree that can go.
    private static string? Protection(GitWorktree worktree, string? checkout, bool busy)
        => worktree.Main || worktree.Path.Equals(checkout, PathComparison) ? "main" : busy ? "in_use" : worktree.Locked ? "locked" : null;

    // The checkout a folder that a session records is in. A folder that is there is in the nearest checkout above
    // it; one that is gone is only in a worktree git still lists without its folder, never in the checkout around it.
    private static string? Owner(IReadOnlyList<GitWorktree> worktrees, string folder)
    {
        if (GitWorktreeService.Find(worktrees, folder) is not { } holder) return null;
        if (holder.Missing) return holder.Path;
        return Directory.Exists(folder) && GitWorktreeService.FindCheckoutRoot(folder) is { } at && at.Equals(holder.Path, PathComparison) ? holder.Path : null;
    }

    // The title a session is listed with, on one line and short enough for a row.
    private static string Title(AgentSessionMetadata session)
    {
        var title = SessionRuntimeService.ListedTitle(session, null, null);
        var text = new System.Text.StringBuilder(Math.Min(title.Length, MaximumTitleLength));
        for (var index = 0; index < title.Length && text.Length < MaximumTitleLength; index++)
        {
            var character = title[index];
            if (char.IsHighSurrogate(character) && index + 1 < title.Length && char.IsLowSurrogate(title[index + 1]))
            {
                if (text.Length + 2 > MaximumTitleLength) break;
                text.Append(character).Append(title[++index]);
            }
            else if (!char.IsSurrogate(character)) text.Append(char.IsControl(character) ? ' ' : character);
        }

        return text.ToString().Trim() is { Length: > 0 } shown ? shown : session.SessionId;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private string? Refuse(string? expectedEpoch)
        => _worktrees is null || _projects is null || _config is null || _busy is null ? "unavailable"
            : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;

    // The folder of a project a request names, or why there is none to answer for.
    private async Task<(string? Root, string Refusal)> OpenAsync(string? expectedEpoch, string? projectId, bool allowArchived, CancellationToken cancellationToken)
    {
        if (Refuse(expectedEpoch) is { } refused) return (null, refused);
        if (projectId is null) return (null, "invalid_request");
        var project = await SettingsProjectScope.ResolveAsync(_projects!, projectId, allowArchived, cancellationToken).ConfigureAwait(false);
        return project.Root is { } root ? (root, "ok") : (null, project.Status);
    }

    // The folder git runs in for a request: the folder of the project, or the folder of one of its worktrees.
    private async Task<(string? Folder, string Refusal)> CheckoutAsync(string? expectedEpoch, string? projectId, string? worktree, CancellationToken cancellationToken)
    {
        var (root, refusal) = await OpenAsync(expectedEpoch, projectId, allowArchived: false, cancellationToken).ConfigureAwait(false);
        if (root is null) return (null, refusal);
        if (worktree is null) return (root, "ok");
        if (!ValidPath(worktree)) return (null, "invalid_request");
        return Directory.Exists(worktree) && GitWorktreeService.SameRepository(root, worktree) ? (Path.GetFullPath(worktree), "ok") : (null, "not_worktree");
    }

    private static bool ValidPath(string? path)
    {
        if (path is not { Length: > 0 and <= MaximumPathLength } || path.AsSpan().ContainsAnyInRange('\0', '\u001f')) return false;
        try
        {
            // A path that cannot be made whole names no folder.
            return Path.IsPathFullyQualified(path) && Path.GetFullPath(path).Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>Asks for the checkouts of a project's repository.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
internal sealed record WorktreesRequest(string? ExpectedEpoch, string? ProjectId);

/// <summary>
/// <c>ok</c> with the checkouts, or one of <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>,
/// <c>unknown_project</c>, <c>project_unavailable</c> and <c>not_repository</c> with none.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="ProjectId">The project the values belong to.</param>
/// <param name="Worktrees">The checkouts, the main one first.</param>
/// <param name="NewFolder">The folder a new worktree of the project would be created in.</param>
internal sealed record WorktreesListResponse(string Status, string? ProjectId, WorktreeItem[] Worktrees, string? NewFolder);

/// <summary>A checkout of a project's repository.</summary>
/// <param name="Path">The folder of the checkout.</param>
/// <param name="Name">The name of that folder.</param>
/// <param name="Branch">The branch it is on; null on a commit without a branch.</param>
/// <param name="Head">The first digits of the commit it is on.</param>
/// <param name="Main">Whether it is the checkout the project lives in, which is not removed.</param>
/// <param name="Locked">Whether git keeps it from being removed.</param>
/// <param name="Missing">Whether its folder is gone.</param>
/// <param name="Folder">The folder of the project inside this checkout: what a request names to read it.</param>
/// <param name="Busy">Whether a session is at work in it.</param>
internal sealed record WorktreeItem(string Path, string Name, string? Branch, string? Head, bool Main, bool Locked, bool Missing, string Folder, bool Busy);

/// <summary>Asks to remove a worktree.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Path">The folder of the worktree.</param>
/// <param name="Force">Removes it with its changes that are not committed.</param>
internal sealed record WorktreeRemoveRequest(string? ExpectedEpoch, string? ProjectId, string? Path, bool Force = false);

/// <summary>
/// <c>ok</c>, or why nothing changed: <c>in_use</c>, <c>dirty</c>, <c>main</c>, <c>locked</c>, <c>not_worktree</c>,
/// <c>not_found</c>, <c>not_repository</c>, <c>git_unavailable</c>, <c>timeout</c>, <c>failed</c>, or a refusal of the request.
/// The code editor that was asked for a checkout also answers <c>worktree_missing</c> for a folder that is gone,
/// and <c>no_window</c> when no window is there to show it.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="Message">What git said, when it refused.</param>
/// <param name="BranchKept">The branch of a removed worktree that was kept because it holds commits of its own.</param>
internal sealed record WorktreeChangeResponse(string Status, string? Message = null, string? BranchKept = null);

/// <summary>
/// <c>ok</c> with every checkout of the repository, or one of <c>unavailable</c>, <c>stale_epoch</c>,
/// <c>invalid_request</c>, <c>unknown_project</c>, <c>project_unavailable</c> and <c>not_repository</c> with none.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="ProjectId">The project the values belong to.</param>
/// <param name="Worktrees">The checkouts, the main one first.</param>
/// <param name="SessionsKnown">Whether the catalog of sessions could be read: when it could not, no checkout names a session.</param>
/// <param name="Truncated">Whether git lists more checkouts than the answer holds.</param>
internal sealed record WorktreeInventoryResponse(string Status, string? ProjectId, WorktreeInventoryItem[] Worktrees, bool SessionsKnown, bool Truncated = false);

/// <summary>A checkout of a project's repository, as the window of the worktrees shows it.</summary>
/// <param name="Path">The folder of the checkout.</param>
/// <param name="Name">The name of that folder.</param>
/// <param name="Branch">The branch it is on; null on a commit without a branch.</param>
/// <param name="Head">The first digits of the commit it is on.</param>
/// <param name="Main">Whether it is the checkout the project lives in or the main one of the repository: neither is removed.</param>
/// <param name="Project">Whether it is the checkout the project lives in.</param>
/// <param name="Locked">Whether git keeps it from being removed.</param>
/// <param name="Missing">Whether its folder is gone while git still lists it.</param>
/// <param name="Folder">The folder of the project inside this checkout: what a request names to read its changes.</param>
/// <param name="Busy">Whether a session is at work in it right now.</param>
/// <param name="Protection">Why it is not removed: <c>main</c>, <c>in_use</c> or <c>locked</c>; null for a worktree that can go.</param>
/// <param name="Sessions">The sessions of the catalog that record it: the ones at work, then the most recent, a few at most.</param>
/// <param name="SessionCount">How many sessions of the catalog record it.</param>
/// <param name="LastUsedAt">When the most recent of those sessions was last updated; null when no session records it.</param>
internal sealed record WorktreeInventoryItem(string Path, string Name, string? Branch, string? Head, bool Main, bool Project, bool Locked, bool Missing,
    string Folder, bool Busy, string? Protection, WorktreeSessionItem[] Sessions, int SessionCount, DateTimeOffset? LastUsedAt);

/// <summary>A session of the catalog that records a checkout.</summary>
/// <param name="Id">The session.</param>
/// <param name="Title">The title it is listed with.</param>
/// <param name="UpdatedAt">When it was last updated.</param>
/// <param name="Running">Whether it is at work right now.</param>
internal sealed record WorktreeSessionItem(string Id, string Title, DateTimeOffset UpdatedAt, bool Running);

/// <summary>Asks to remove several worktrees.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Paths">The folders of the worktrees, each one once.</param>
/// <param name="Discard">The folders among them that are removed with their changes that are not committed.</param>
/// <param name="DeleteMergedBranches">Deletes the branch of a removed worktree that was created here, when all its commits are in another branch.</param>
internal sealed record WorktreeRemoveManyRequest(string? ExpectedEpoch, string? ProjectId, string[]? Paths, string[]? Discard = null, bool DeleteMergedBranches = false);

/// <summary>
/// <c>ok</c> with one result for each folder that was asked, in the order they were asked, or a refusal of the
/// whole request with none: <c>invalid_request</c>, <c>not_repository</c>, or a refusal of the project.
/// </summary>
/// <param name="Status">The outcome code of the request.</param>
/// <param name="Results">What became of each worktree.</param>
internal sealed record WorktreeRemoveManyResponse(string Status, WorktreeRemoval[] Results);

/// <summary>What became of one worktree that was asked to go.</summary>
/// <param name="Path">The folder, as it was asked.</param>
/// <param name="Status">
/// <c>ok</c>, or why it stays: <c>dirty</c>, <c>in_use</c>, <c>locked</c>, <c>main</c>, <c>not_worktree</c>,
/// <c>not_repository</c>, <c>git_unavailable</c>, <c>timeout</c>, <c>failed</c>, or <c>canceled</c> for one that
/// was not started because the window went away.
/// </param>
/// <param name="Message">What git said, when it refused.</param>
/// <param name="BranchKept">The branch that was asked to go with it and was kept because it holds commits of its own.</param>
/// <param name="BranchDeleted">The branch that was deleted with it.</param>
internal sealed record WorktreeRemoval(string Path, string Status, string? Message = null, string? BranchKept = null, string? BranchDeleted = null);

/// <summary>Asks for the code editor on a checkout.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Path">The folder of the checkout, as the inventory names it.</param>
internal sealed record WorktreeOpenRequest(string? ExpectedEpoch, string? ProjectId, string? Path);

/// <summary>Asks for the branches a checkout can move to.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Worktree">The folder of a worktree of the project; the folder of the project when null.</param>
internal sealed record WorktreeBranchesRequest(string? ExpectedEpoch, string? ProjectId, string? Worktree = null);

/// <summary>The branches of a checkout.</summary>
/// <param name="Status"><c>ok</c>, or why there is no list.</param>
/// <param name="Branches">The local branches, then the branches of remotes that have no local branch.</param>
/// <param name="Busy">Whether a session is at work in the checkout, which then stays on its branch.</param>
internal sealed record WorktreeBranchesResponse(string Status, WorktreeBranch[] Branches, bool Busy);

/// <summary>A branch a checkout can move to.</summary>
/// <param name="Name">The name: <c>main</c>, or <c>origin/main</c> for a branch of a remote.</param>
/// <param name="Current">Whether the checkout is on it.</param>
/// <param name="Remote">Whether it is a branch of a remote without a local branch.</param>
/// <param name="Worktree">The checkout that is on it, when one is.</param>
/// <param name="WorktreeName">The name of that checkout's folder.</param>
internal sealed record WorktreeBranch(string Name, bool Current, bool Remote, string? Worktree, string? WorktreeName);

/// <summary>Asks to move a checkout to a branch.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Worktree">The folder of a worktree of the project; the folder of the project when null.</param>
/// <param name="Branch">The branch, or the name of the new one.</param>
/// <param name="Create">Creates the branch where the checkout is.</param>
internal sealed record WorktreeSwitchRequest(string? ExpectedEpoch, string? ProjectId, string? Worktree, string? Branch, bool Create = false);

/// <summary>Asks where new worktrees are placed.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
internal sealed record WorktreeSettingsRequest(string? ExpectedEpoch);

/// <summary>Asks to write where new worktrees are placed.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Location"><c>global</c>, <c>project</c> or <c>custom</c>.</param>
/// <param name="Folder">The folder of the <c>custom</c> location, absolute; a leading <c>~</c> is the folder of the user.</param>
internal sealed record WorktreeSettingsSaveRequest(string? ExpectedEpoch, string? Location, string? Folder);

/// <summary>Where new worktrees are placed.</summary>
/// <param name="Status"><c>ok</c>, <c>invalid_request</c>, <c>save_failed</c>, or a refusal of the request.</param>
/// <param name="Location"><c>global</c>, <c>project</c> or <c>custom</c>.</param>
/// <param name="Folder">The folder of the <c>custom</c> location, as written.</param>
/// <param name="GlobalFolder">The folder of the <c>global</c> location.</param>
internal sealed record WorktreeSettingsResponse(string Status, string? Location, string? Folder, string? GlobalFolder);
