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
/// A checkout a session is at work in is neither removed nor moved to another branch: the answer is
/// <c>in_use</c>. A worktree is named by its folder, and only a folder of the project's own repository is
/// accepted.
/// </remarks>
[NeoRpcService("worktrees", Version = 1)]
internal sealed class WorktreesService
{
    private const int MaximumPathLength = 4096;
    private const int AbbreviatedCommitLength = 7;

    private readonly GitWorktreeService? _worktrees;
    private readonly ProjectCatalog? _projects;
    private readonly CodeAltaConfigStore? _config;
    private readonly Func<IReadOnlyList<SessionWorkFolder>>? _busy;
    private readonly string? _epoch;

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

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
        if (target.Main || target.Path.Equals(GitWorktreeService.FindCheckoutRoot(root), PathComparison)) return new("main");
        if (BusyCheckouts().Contains(target.Path)) return new("in_use");
        // Not the caller's token: a removal that started is not left half done because a page went away.
        var outcome = await _worktrees.RemoveAsync(root, target.Path, request.Force, CancellationToken.None).ConfigureAwait(false);
        return new(outcome.Status, outcome.Message, outcome.BranchKept);
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
    private HashSet<string> BusyCheckouts()
    {
        var checkouts = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var session in _busy!())
        {
            if (GitWorktreeService.FindCheckoutRoot(session.Folder) is { } checkout) checkouts.Add(checkout);
        }

        return checkouts;
    }

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
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
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
/// <c>ok</c>, or why nothing changed: <c>in_use</c>, <c>dirty</c>, <c>main</c>, <c>not_worktree</c>,
/// <c>not_found</c>, <c>not_repository</c>, <c>git_unavailable</c>, <c>timeout</c>, <c>failed</c>, or a refusal of the request.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="Message">What git said, when it refused.</param>
/// <param name="BranchKept">The branch of a removed worktree that was kept because it holds commits of its own.</param>
internal sealed record WorktreeChangeResponse(string Status, string? Message = null, string? BranchKept = null);

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
