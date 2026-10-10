using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodeAlta.Catalog.Worktrees;

/// <summary>What git answered to one command.</summary>
/// <param name="ExitCode">The exit code of git, <see cref="NotStarted"/> or <see cref="TimedOut"/>.</param>
/// <param name="Output">What git printed.</param>
/// <param name="Error">What git printed as errors.</param>
public sealed record GitRun(int ExitCode, string Output, string Error)
{
    /// <summary>The exit code of a run that did not start: git is not installed.</summary>
    public const int NotStarted = -1;

    /// <summary>The exit code of a run that was stopped because it took too long.</summary>
    public const int TimedOut = -2;

    /// <summary>Gets whether git ran and reported no error.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs git in a folder and returns what it answered; it never throws for a failure of git.</summary>
/// <param name="folder">The folder git runs in.</param>
/// <param name="arguments">The arguments, each one as it is.</param>
/// <param name="timeout">How long git may run.</param>
/// <param name="cancellationToken">Stops the run.</param>
/// <returns>The answer of git.</returns>
public delegate Task<GitRun> GitRunner(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

/// <summary>A checkout of a repository: its main one, or a linked worktree.</summary>
/// <param name="Path">The folder of the checkout.</param>
/// <param name="Name">The name of that folder.</param>
/// <param name="Branch">The branch it is on; null when it is on a commit without a branch.</param>
/// <param name="Head">The commit it is on, when git names one.</param>
/// <param name="Main">Whether it is the checkout the repository lives in, which cannot be removed.</param>
/// <param name="Locked">Whether git keeps it from being removed.</param>
/// <param name="Missing">Whether its folder is gone while the repository still lists it.</param>
public sealed record GitWorktree(string Path, string Name, string? Branch, string? Head, bool Main, bool Locked, bool Missing);

/// <summary>A branch of a repository.</summary>
/// <param name="Name">The name: <c>main</c>, or <c>origin/main</c> for a branch of a remote.</param>
/// <param name="Current">Whether the checkout that was asked about is on it.</param>
/// <param name="Remote">Whether it is a branch of a remote that has no local branch yet.</param>
/// <param name="Worktree">The checkout that is on it, when one is; a branch is used by one checkout at a time.</param>
public sealed record GitBranch(string Name, bool Current, bool Remote, string? Worktree);

/// <summary>The outcome of a change to a checkout.</summary>
/// <param name="Status">
/// <see cref="GitWorktreeService.Ok"/>, or why nothing was done: <c>git_unavailable</c>, <c>not_repository</c>,
/// <c>not_worktree</c>, <c>main</c>, <c>dirty</c>, <c>invalid</c>, <c>not_found</c>, <c>timeout</c> or <c>failed</c>.
/// </param>
/// <param name="Message">What git said, when it refused.</param>
/// <param name="BranchKept">After a removal: the branch of the worktree that was kept because it holds commits no other branch has.</param>
public sealed record GitWorktreeOutcome(string Status, string? Message = null, string? BranchKept = null)
{
    /// <summary>Gets whether the change was made.</summary>
    public bool Succeeded => Status == GitWorktreeService.Ok;

    /// <summary>Gets the branch of a removed worktree that was deleted with it; null when none was.</summary>
    public string? BranchDeleted { get; init; }
}

/// <summary>A worktree that was created, or why none was.</summary>
/// <param name="Status">
/// <see cref="GitWorktreeService.Ok"/>, or <c>git_unavailable</c>, <c>not_repository</c>, <c>no_commit</c>,
/// <c>invalid</c>, <c>timeout</c> or <c>failed</c>.
/// </param>
/// <param name="Message">What git said, when it refused.</param>
/// <param name="Root">The folder of the new checkout.</param>
/// <param name="Folder">The folder of the project inside the new checkout: where a session of the project works.</param>
/// <param name="Name">The name of the worktree.</param>
/// <param name="Branch">The branch that was created for it.</param>
public sealed record GitWorktreeCreation(string Status, string? Message = null, string? Root = null, string? Folder = null, string? Name = null, string? Branch = null)
{
    /// <summary>Gets whether the worktree was created.</summary>
    public bool Succeeded => Status == GitWorktreeService.Ok;
}

/// <summary>
/// Creates, lists and removes the git worktrees of a project, and moves a checkout to another branch.
/// </summary>
/// <remarks>
/// <para>
/// A worktree is a second checkout of a repository, in a folder of its own and on a branch of its own: a session
/// that works there leaves the folder of the project as it is. A new worktree gets a name from
/// <see cref="WorktreeNames"/> and the branch <c>alta/&lt;name&gt;</c>, and starts from a commit: what is not
/// committed in the folder of the project is not in it.
/// </para>
/// <para>
/// Nothing here decides whether a checkout is in use: the caller asks its sessions before it removes a worktree
/// or changes a branch.
/// </para>
/// </remarks>
public sealed class GitWorktreeService
{
    /// <summary>The status of a change that was made.</summary>
    public const string Ok = "ok";

    /// <summary>What the branches of the worktrees created here start with.</summary>
    public const string BranchPrefix = "alta/";

    /// <summary>How long a command that reads may run.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a command that checks files out or removes them may run.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(9);

    private const int MaximumOutputCharacters = 1024 * 1024;
    private const int MaximumMessageLength = 600;
    private const int MaximumBranches = 400;
    private const string IgnoreFileName = ".gitignore";
    private const string Heads = "refs/heads/";

    private readonly CatalogOptions _options;
    private readonly CodeAltaConfigStore _config;
    private readonly GitRunner _run;
    private readonly Random _random;
    // One change at a time: two creations must not pick the same name.
    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <summary>Creates the service.</summary>
    /// <param name="options">The catalog roots; the global location is under its global root.</param>
    /// <param name="config">The configuration the location is read from.</param>
    /// <param name="run">Runs git; the <c>git</c> found on the path when null.</param>
    /// <param name="random">Picks the names; a shared source when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="config"/> is null.</exception>
    public GitWorktreeService(CatalogOptions options, CodeAltaConfigStore config, GitRunner? run = null, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(config);
        _options = options;
        _config = config;
        _run = run ?? RunGitAsync;
        _random = random ?? Random.Shared;
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Gets the folder of the global location: the worktrees of every project are in a folder of it.</summary>
    public string GlobalFolder => _options.WorktreesRoot;

    /// <summary>Reads where new worktrees go. A configuration file that cannot be read gives the global location.</summary>
    /// <returns>The user's choice.</returns>
    public WorktreeSettings ReadSettings()
    {
        try
        {
            return WorktreeSettings.Read(_config.LoadGlobal().Worktrees);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new(WorktreeLocation.Global, null);
        }
    }

    /// <summary>Gets the folder the worktrees of a project are created in, for a choice of location.</summary>
    /// <param name="settings">The choice of location.</param>
    /// <param name="project">The project.</param>
    /// <param name="repositoryRoot">The root of the checkout the project is in.</param>
    /// <returns>The folder; each worktree is a folder inside it.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public string ParentFolder(WorktreeSettings settings, ProjectDescriptor project, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return settings.Location switch
        {
            WorktreeLocation.Project => Path.Combine(repositoryRoot, ".alta", "worktrees"),
            WorktreeLocation.Custom when settings.Folder is { Length: > 0 } folder => Path.Combine(folder, FolderNameOf(project, repositoryRoot)),
            _ => Path.Combine(_options.WorktreesRoot, FolderNameOf(project, repositoryRoot)),
        };
    }

    /// <summary>
    /// Creates a worktree for a project, on a new branch that starts from a commit, and returns the folder a
    /// session of the project works in there.
    /// </summary>
    /// <param name="project">The project; its folder is in a git repository.</param>
    /// <param name="baseReference">The branch or commit the worktree starts from; the commit the folder of the project is on when null.</param>
    /// <param name="cancellationToken">Stops waiting; a checkout that started is stopped with it.</param>
    /// <returns>The worktree, or why none was created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<GitWorktreeCreation> CreateAsync(ProjectDescriptor project, string? baseReference = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (baseReference is not null && !IsReferenceName(baseReference))
        {
            return new("invalid");
        }

        string projectFolder;
        try
        {
            projectFolder = Normalize(project.ProjectPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new("not_repository");
        }

        if (!Directory.Exists(projectFolder))
        {
            return new("not_repository");
        }

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var top = await _run(projectFolder, ["rev-parse", "--show-toplevel"], ReadTimeout, cancellationToken).ConfigureAwait(false);
            if (!top.Succeeded || FirstLine(top.Output) is not { } rootLine)
            {
                return new(top.ExitCode == GitRun.NotStarted ? "git_unavailable" : "not_repository", Message(top));
            }

            var root = Normalize(rootLine);
            // The folder of the project inside its checkout: the session works in the same folder of the new one.
            var below = Path.GetRelativePath(root, projectFolder);
            if (below.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(below))
            {
                below = ".";
            }

            var commit = await _run(root, ["rev-parse", "--verify", "--quiet", (baseReference ?? "HEAD") + "^{commit}"], ReadTimeout, cancellationToken).ConfigureAwait(false);
            if (!commit.Succeeded || FirstLine(commit.Output) is not { } start)
            {
                return new(baseReference is null ? "no_commit" : "invalid", Message(commit));
            }

            var settings = ReadSettings();
            var parent = ParentFolder(settings, project, root);
            Directory.CreateDirectory(parent);
            if (settings.Location == WorktreeLocation.Project)
            {
                EnsureIgnored(parent);
            }

            var branches = await _run(root, ["for-each-ref", "--format=%(refname:short)", "refs/heads/" + BranchPrefix], ReadTimeout, cancellationToken).ConfigureAwait(false);
            var taken = new HashSet<string>(Lines(branches.Output), StringComparer.OrdinalIgnoreCase);
            var name = WorktreeNames.PickFree(_random, candidate => taken.Contains(BranchPrefix + candidate) || Path.Exists(Path.Combine(parent, candidate)));
            var path = Path.Combine(parent, name);
            var branch = BranchPrefix + name;
            // Not tracking: a worktree that starts from a branch of a remote must not push to that branch.
            var add = await _run(root, ["worktree", "add", "--no-track", "-b", branch, path, start], WriteTimeout, cancellationToken).ConfigureAwait(false);
            if (!add.Succeeded)
            {
                return new(StatusOf(add), Message(add));
            }

            var folder = below == "." ? path : Path.Combine(path, below);
            Directory.CreateDirectory(folder);
            return new(Ok, null, path, folder, name, branch);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new("failed", Clean(exception.Message));
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Lists the checkouts of the repository a folder is in, the main one first.</summary>
    /// <param name="folder">A folder of the repository.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The checkouts, or null when the folder is in no repository or git cannot answer.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<IReadOnlyList<GitWorktree>?> ListAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var run = await _run(folder, ["worktree", "list", "--porcelain"], ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            return null;
        }

        var worktrees = ParseWorktrees(run.Output);
        for (var index = 0; index < worktrees.Count; index++)
        {
            if (!worktrees[index].Missing && !Directory.Exists(worktrees[index].Path))
            {
                worktrees[index] = worktrees[index] with { Missing = true };
            }
        }

        return worktrees;
    }

    /// <summary>
    /// Removes a worktree of the repository a folder is in. Without <paramref name="force"/> a worktree that
    /// holds changes that are not committed is left as it is, with the status <c>dirty</c>. The branch of a
    /// worktree created here is deleted with it, unless it holds commits that no other branch has.
    /// </summary>
    /// <param name="folder">A folder of the repository, usually the folder of the project.</param>
    /// <param name="worktree">The folder of the worktree, or a folder inside it.</param>
    /// <param name="force">Removes the worktree with the changes that are not committed, which are lost.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public Task<GitWorktreeOutcome> RemoveAsync(string folder, string worktree, bool force, CancellationToken cancellationToken = default)
        => RemoveAsync(folder, worktree, force, deleteBranch: true, cancellationToken);

    /// <summary>
    /// Removes a worktree of the repository a folder is in, and says what becomes of its branch. Without
    /// <paramref name="force"/> a worktree that holds changes that are not committed is left as it is, with the
    /// status <c>dirty</c>. A worktree whose folder is gone is forgotten by its name: the other worktrees whose
    /// folder is gone stay listed, also when git refuses to forget the one that was asked, which then fails.
    /// </summary>
    /// <param name="folder">A folder of the repository, usually the folder of the project.</param>
    /// <param name="worktree">The folder of the worktree, or a folder inside it.</param>
    /// <param name="force">Removes the worktree with the changes that are not committed, which are lost.</param>
    /// <param name="deleteBranch">
    /// Deletes the branch of a worktree created here with it, unless it holds commits that no other branch has;
    /// the branch is left as it is when false.
    /// </param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The outcome, with the branch that was deleted or the one that was kept for its commits.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<GitWorktreeOutcome> RemoveAsync(string folder, string worktree, bool force, bool deleteBranch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ListAsync(folder, cancellationToken).ConfigureAwait(false) is not { Count: > 0 } worktrees)
            {
                return new("not_repository");
            }

            if (Find(worktrees, worktree) is not { } target)
            {
                return new("not_worktree");
            }

            if (target.Main)
            {
                return new("main");
            }

            // Never from inside the folder that goes away.
            var home = worktrees[0].Path;
            if (target.Missing)
            {
                // By its name, and never with a prune, which forgets every worktree whose folder is gone and not
                // only this one. What git refuses to forget this way, such as a folder that lost its `.git`,
                // stays listed, and what git said is the answer.
                var forget = await _run(home, ["worktree", "remove", target.Path], ReadTimeout, cancellationToken).ConfigureAwait(false);
                if (!forget.Succeeded)
                {
                    return new(StatusOf(forget), Message(forget));
                }
            }
            else
            {
                if (!force)
                {
                    var status = await _run(target.Path, ["status", "--porcelain", "--untracked-files=normal"], ReadTimeout, cancellationToken).ConfigureAwait(false);
                    if (!status.Succeeded)
                    {
                        return new(StatusOf(status), Message(status));
                    }

                    if (FirstLine(status.Output) is not null)
                    {
                        return new("dirty");
                    }
                }

                IReadOnlyList<string> arguments = force ? ["worktree", "remove", "--force", target.Path] : ["worktree", "remove", target.Path];
                var remove = await _run(home, arguments, WriteTimeout, cancellationToken).ConfigureAwait(false);
                if (!remove.Succeeded)
                {
                    return new(StatusOf(remove), Message(remove));
                }
            }

            string? kept = null, deleted = null;
            if (deleteBranch && target.Branch is { } branch && branch.StartsWith(BranchPrefix, StringComparison.Ordinal))
            {
                // Only a branch whose commits are all in another one goes: no commit is lost with a worktree.
                var delete = await _run(home, ["branch", "-d", branch], ReadTimeout, cancellationToken).ConfigureAwait(false);
                if (delete.Succeeded)
                {
                    deleted = branch;
                }
                else
                {
                    kept = branch;
                }
            }

            // The folders that only held it go with the last worktree: the one of the project, then the one of
            // the catalog. A folder the user chose is theirs, and stays.
            var parent = Path.GetDirectoryName(target.Path);
            RemoveWhenEmpty(parent);
            if (parent is not null && Path.GetDirectoryName(parent) is { } above && Normalize(above).Equals(Normalize(_options.WorktreesRoot), PathComparison))
            {
                RemoveWhenEmpty(above);
            }

            return new(Ok, null, kept) { BranchDeleted = deleted };
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// Lists the branches a checkout can move to, the ones with the newest commits first: the local branches,
    /// then the branches of the remotes that have no local branch yet.
    /// </summary>
    /// <param name="checkout">The folder of the checkout.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The branches, or null when the folder is in no repository or git cannot answer.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<IReadOnlyList<GitBranch>?> ListBranchesAsync(string checkout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkout);
        if (!Directory.Exists(checkout))
        {
            return null;
        }

        var run = await _run(checkout,
            ["for-each-ref", "--sort=-committerdate", "--count=" + MaximumBranches.ToString(CultureInfo.InvariantCulture),
                "--format=%(refname)%09%(HEAD)%09%(worktreepath)", "refs/heads", "refs/remotes"],
            ReadTimeout, cancellationToken).ConfigureAwait(false);
        return run.Succeeded ? ParseBranches(run.Output) : null;
    }

    /// <summary>
    /// Moves a checkout to a branch: a local branch, a branch of a remote (a local branch that follows it is
    /// created), or a new branch that starts where the checkout is. Git refuses when changes that are not
    /// committed would be overwritten, or when another checkout is on the branch.
    /// </summary>
    /// <param name="checkout">The folder of the checkout.</param>
    /// <param name="branch">The branch, as <see cref="ListBranchesAsync"/> names it, or the name of the new one.</param>
    /// <param name="create">Creates the branch.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<GitWorktreeOutcome> SwitchAsync(string checkout, string branch, bool create, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkout);
        if (!IsReferenceName(branch))
        {
            return new("invalid");
        }

        if (!Directory.Exists(checkout))
        {
            return new("not_repository");
        }

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<string> arguments = ["switch", "-c", branch];
            if (!create)
            {
                var local = await _run(checkout, ["show-ref", "--verify", "--quiet", "refs/heads/" + branch], ReadTimeout, cancellationToken).ConfigureAwait(false);
                if (local.ExitCode is GitRun.NotStarted or GitRun.TimedOut)
                {
                    return new(StatusOf(local), Message(local));
                }

                if (local.Succeeded)
                {
                    arguments = ["switch", branch];
                }
                else if ((await _run(checkout, ["show-ref", "--verify", "--quiet", "refs/remotes/" + branch], ReadTimeout, cancellationToken).ConfigureAwait(false)).Succeeded)
                {
                    // A local branch of the same name that follows the branch of the remote.
                    arguments = ["switch", "--track", branch];
                }
                else
                {
                    return new("not_found");
                }
            }

            var run = await _run(checkout, arguments, WriteTimeout, cancellationToken).ConfigureAwait(false);
            return run.Succeeded ? new(Ok) : new(StatusOf(run), Message(run));
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Finds the checkout a folder is in among a list: the one whose folder holds it.</summary>
    /// <param name="worktrees">The checkouts of a repository.</param>
    /// <param name="folder">A folder.</param>
    /// <returns>The checkout with the longest folder that holds <paramref name="folder"/>, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="worktrees"/> is null.</exception>
    public static GitWorktree? Find(IReadOnlyList<GitWorktree> worktrees, string folder)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        GitWorktree? found = null;
        foreach (var worktree in worktrees)
        {
            if (IsWithin(folder, worktree.Path) && (found is null || worktree.Path.Length > found.Path.Length))
            {
                found = worktree;
            }
        }

        return found;
    }

    /// <summary>Answers whether a folder is another folder or inside it.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="root">The folder it may be in.</param>
    /// <returns>True when <paramref name="folder"/> is <paramref name="root"/> or below it.</returns>
    public static bool IsWithin(string? folder, string? root)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var (inner, outer) = (Normalize(folder), Normalize(root));
            return inner.Equals(outer, PathComparison)
                || (inner.Length > outer.Length && inner.StartsWith(outer, PathComparison)
                    && (Path.EndsInDirectorySeparator(outer) || inner[outer.Length] == Path.DirectorySeparatorChar));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the root of the checkout a folder is in: the nearest folder at or above it that holds <c>.git</c>,
    /// a folder in a repository and a file in a linked worktree. No process is started.
    /// </summary>
    /// <param name="folder">A folder.</param>
    /// <returns>The root of its checkout, or null when it is in none.</returns>
    public static string? FindCheckoutRoot(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            for (var current = Normalize(folder); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                var git = Path.Combine(current, ".git");
                if (File.Exists(git) || Directory.Exists(git))
                {
                    return current;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is in no checkout that can be shown.
        }

        return null;
    }

    /// <summary>
    /// Finds the folder git keeps the repository of a checkout in, which every checkout of one repository
    /// shares: two folders are checkouts of the same repository when they have the same one. No process is
    /// started.
    /// </summary>
    /// <param name="folder">A folder of a checkout.</param>
    /// <returns>The shared git folder, or null when the folder is in no checkout.</returns>
    public static string? FindCommonDirectory(string? folder)
    {
        if (FindCheckoutRoot(folder) is not { } root)
        {
            return null;
        }

        try
        {
            var git = Path.Combine(root, ".git");
            if (!Directory.Exists(git))
            {
                // A linked worktree: `.git` is a file that names its own folder inside the repository.
                const string prefix = "gitdir:";
                var pointer = ReadStart(git);
                if (pointer is null || !pointer.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }

                git = Path.GetFullPath(pointer[prefix.Length..].Trim(), root);
                if (!Directory.Exists(git))
                {
                    return null;
                }
            }

            var common = ReadStart(Path.Combine(git, "commondir"));
            return Normalize(common is null ? git : Path.GetFullPath(common, git));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Answers whether two folders are in checkouts of the same repository.</summary>
    /// <param name="first">A folder.</param>
    /// <param name="second">Another folder.</param>
    /// <returns>True when both are in a checkout and the two checkouts share their repository.</returns>
    public static bool SameRepository(string? first, string? second)
        => FindCommonDirectory(first) is { } one && FindCommonDirectory(second) is { } other && one.Equals(other, PathComparison);

    /// <summary>
    /// Answers whether a text can name a branch or a commit in a command: it holds no space and no character
    /// git forbids in a name, and it does not start with a hyphen, which would make it an option.
    /// </summary>
    /// <param name="name">The text.</param>
    /// <returns>True when the text is safe to hand to git as a name.</returns>
    public static bool IsReferenceName(string? name)
    {
        if (name is not { Length: > 0 and <= 200 } || name[0] is '-' or '/' or '.' || name[^1] is '/' or '.')
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character <= ' ' || character == '\u007f' || character is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            {
                return false;
            }
        }

        return !name.Contains("..", StringComparison.Ordinal) && !name.Contains("@{", StringComparison.Ordinal)
            && !name.Contains("//", StringComparison.Ordinal) && !name.Contains("/.", StringComparison.Ordinal)
            && !name.EndsWith(".lock", StringComparison.Ordinal) && name != "@";
    }

    /// <summary>Reads what <c>git worktree list --porcelain</c> printed.</summary>
    /// <param name="porcelain">The output of git.</param>
    /// <returns>The checkouts in the order of git: the main one first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="porcelain"/> is null.</exception>
    public static List<GitWorktree> ParseWorktrees(string porcelain)
    {
        ArgumentNullException.ThrowIfNull(porcelain);
        var worktrees = new List<GitWorktree>();
        string? path = null, branch = null, head = null;
        var (locked, missing, bare) = (false, false, false);

        void Close()
        {
            if (path is not null && !bare)
            {
                var folder = Normalize(path);
                worktrees.Add(new(folder, Path.GetFileName(folder) is { Length: > 0 } name ? name : folder, branch, head, worktrees.Count == 0, locked, missing));
            }

            (path, branch, head, locked, missing, bare) = (null, null, null, false, false, false);
        }

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Close();
                continue;
            }

            var space = line.IndexOf(' ');
            var (key, value) = space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..]);
            switch (key)
            {
                case "worktree":
                    Close();
                    path = value;
                    break;
                case "HEAD":
                    head = value;
                    break;
                case "branch":
                    branch = value.StartsWith(Heads, StringComparison.Ordinal) ? value[Heads.Length..] : value;
                    break;
                case "bare":
                    bare = true;
                    break;
                case "locked":
                    locked = true;
                    break;
                case "prunable":
                    missing = true;
                    break;
            }
        }

        Close();
        // The first checkout git lists is the main one, also when a bare repository was skipped before it.
        return worktrees;
    }

    /// <summary>Reads the branches <see cref="ListBranchesAsync"/> asked git for.</summary>
    /// <param name="output">Lines of a reference, the mark of the current branch and the checkout that is on it, separated by tabs.</param>
    /// <returns>The local branches, then the branches of remotes that have no local branch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    public static List<GitBranch> ParseBranches(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        const string remotes = "refs/remotes/";
        var local = new List<GitBranch>();
        var remote = new List<GitBranch>();
        foreach (var line in Lines(output))
        {
            var fields = line.Split('\t', 3);
            var reference = fields[0];
            var current = fields.Length > 1 && fields[1] == "*";
            var worktree = fields.Length > 2 && fields[2].Length > 0 ? Normalize(fields[2]) : null;
            if (reference.StartsWith(Heads, StringComparison.Ordinal))
            {
                local.Add(new(reference[Heads.Length..], current, false, worktree));
            }
            else if (reference.StartsWith(remotes, StringComparison.Ordinal) && !reference.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                remote.Add(new(reference[remotes.Length..], false, true, null));
            }
        }

        var names = new HashSet<string>(local.Select(static branch => branch.Name), StringComparer.Ordinal);
        // `origin/main` is offered only while there is no `main`.
        local.AddRange(remote.Where(branch => branch.Name.IndexOf('/') is var slash && slash > 0 && !names.Contains(branch.Name[(slash + 1)..])));
        return local;
    }

    /// <summary>
    /// Runs the <c>git</c> found on the path in a folder, without a shell or a prompt, and returns what it
    /// printed. It is stopped when it runs longer than <paramref name="timeout"/>.
    /// </summary>
    /// <param name="folder">The folder git runs in.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="timeout">How long git may run.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>The answer; <see cref="GitRun.NotStarted"/> when git is not installed.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled; git and what it started were stopped.</exception>
    public static async Task<GitRun> RunGitAsync(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in (ReadOnlySpan<string>)["-C", folder, "-c", "core.quotepath=off", "--no-pager"])
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";
        // The folder decides the repository, not variables inherited from the process that started the host.
        foreach (var inherited in (ReadOnlySpan<string>)["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR"])
        {
            start.Environment.Remove(inherited);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return new(GitRun.NotStarted, string.Empty, string.Empty);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return new(GitRun.NotStarted, string.Empty, string.Empty);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        // A pipe read cannot always be canceled: stopping the process ends it.
        using var stop = limit.Token.Register(static state => Kill((Process)state!), process);
        try
        {
            process.StandardInput.Close();
            var output = ReadAsync(process.StandardOutput);
            var error = ReadAsync(process.StandardError);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(GitRun.TimedOut, string.Empty, "git did not answer in time.");
        }
        finally
        {
            Kill(process);
        }
    }

    // What is read of an output is bounded; the rest is drained so that git never waits on a full pipe.
    private static async Task<string> ReadAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                if (text.Length < MaximumOutputCharacters)
                {
                    text.Append(buffer, 0, Math.Min(read, MaximumOutputCharacters - text.Length));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process was stopped: what was read is what there is.
        }

        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    // Git ignores everything in a folder that holds this file, the file included: the worktrees inside a
    // repository never show up as its changes, and no tracked file is touched.
    private static void EnsureIgnored(string folder)
    {
        var path = Path.Combine(folder, IgnoreFileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "# Worktrees created by CodeAlta. Git ignores this folder.\n*\n");
        }
    }

    // The folder that held the worktree goes with the last one; a folder that holds only the ignore file is empty.
    private static void RemoveWhenEmpty(string? folder)
    {
        try
        {
            if (folder is null || !Directory.Exists(folder))
            {
                return;
            }

            var entries = Directory.GetFileSystemEntries(folder);
            if (entries.Length == 1 && string.Equals(Path.GetFileName(entries[0]), IgnoreFileName, StringComparison.Ordinal))
            {
                File.Delete(entries[0]);
                entries = [];
            }

            if (entries.Length == 0)
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind harms nothing.
        }
    }

    // A project is named by its slug; one that has none, by the folder of its checkout.
    private static string FolderNameOf(ProjectDescriptor project, string repositoryRoot)
    {
        var name = string.IsNullOrWhiteSpace(project.Slug) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(repositoryRoot)) : project.Slug.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(character => Array.IndexOf(invalid, character) >= 0 || character is ' ' ? '-' : character)]).Trim('-', '.');
        return cleaned.Length == 0 ? "project" : cleaned;
    }

    private static string StatusOf(GitRun run)
        => run.ExitCode switch
        {
            GitRun.NotStarted => "git_unavailable",
            GitRun.TimedOut => "timeout",
            _ => "failed",
        };

    private static string? Message(GitRun run) => run.Succeeded ? null : Clean(run.Error.Length > 0 ? run.Error : run.Output);

    // What git said, on one line and without what a terminal would interpret.
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaximumMessageLength));
        var space = false;
        foreach (var character in text.Trim())
        {
            if (builder.Length >= MaximumMessageLength)
            {
                builder.Append('…');
                break;
            }

            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? FirstLine(string text) => Lines(text).FirstOrDefault();

    private static IEnumerable<string> Lines(string text)
        => text.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // The first line of a small file, or null when there is no such file.
    private static string? ReadStart(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[4096];
        var text = Encoding.UTF8.GetString(buffer, 0, stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false));
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        return (end < 0 ? text : text[..end]).Trim();
    }
}
