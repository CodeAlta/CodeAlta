using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.SessionMoves;

/// <summary>What the capture of the git payload of a session found.</summary>
/// <param name="Status">
/// <c>ok</c>, or why no payload was made: <c>not_repository</c>, <c>no_remote</c>, <c>no_commit</c>,
/// <c>payload_too_large</c>, <c>git_unavailable</c> or <c>git_failed</c>.
/// </param>
/// <param name="Repository">The repository and its payload, when <paramref name="Status"/> is <c>ok</c>.</param>
/// <param name="Message">What git or the limit said, when something failed.</param>
public sealed record GitPayloadCapture(string Status, SessionMoveRepository? Repository, string? Message = null);

/// <summary>What the target made of a git payload.</summary>
/// <param name="Status">
/// <c>ok</c>, or why the worktree is not ready: <c>not_repository</c>, <c>git_prerequisites_missing</c>,
/// <c>head_missing</c>, <c>worktree_failed</c>, <c>patch_failed</c>, <c>tree_mismatch</c>, <c>git_unavailable</c> or
/// <c>git_failed</c>.
/// </param>
/// <param name="Message">What git said, when something failed.</param>
/// <param name="WorktreeRoot">The root of the worktree that was prepared.</param>
/// <param name="WorkFolder">The folder of the project in it, where the session works.</param>
/// <param name="Branch">The branch checked out there; <see langword="null"/> for a detached HEAD.</param>
/// <param name="CreatedBranch">Whether the branch was created for the move (and goes when the move is undone).</param>
public sealed record GitPayloadPreparation(string Status, string? Message = null, string? WorktreeRoot = null, string? WorkFolder = null,
    string? Branch = null, bool CreatedBranch = false)
{
    /// <summary>Gets whether the worktree is ready.</summary>
    public bool Succeeded => Status == GitWorktreeService.Ok;
}

/// <summary>What a move of a session would ship and leave behind, read without writing (<see cref="GitSessionPayload.PreviewAsync"/>).</summary>
/// <param name="Status"><c>ok</c>, or why the session cannot move: <c>not_repository</c>, <c>no_remote</c>, <c>git_unavailable</c>, <c>git_failed</c>.</param>
public sealed record GitPayloadPreview(string Status)
{
    /// <summary>Gets what git said of a failure.</summary>
    public string? Message { get; init; }

    /// <summary>Gets the repository key the target finds its project by.</summary>
    public string? RepositoryKey { get; init; }

    /// <summary>Gets the folder of the session in the repository, with forward slashes; <c>.</c> for its root.</summary>
    public string RelativeFolder { get; init; } = ".";

    /// <summary>Gets the branch checked out; null for a detached HEAD.</summary>
    public string? Branch { get; init; }

    /// <summary>Gets the URL of the remote the key comes from, which a target without the repository could clone.</summary>
    public string? RemoteUrl { get; init; }

    /// <summary>Gets how many commits no remote has.</summary>
    public int UnpushedCommits { get; init; }

    /// <summary>Gets whether the working tree has changes.</summary>
    public bool Dirty { get; init; }

    /// <summary>Gets the changed files that look like secrets, which stay behind.</summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];
}

/// <summary>
/// The git payload of a moved session: what the source ships of its repository beyond what the remote has, and how the
/// target makes a worktree of it.
/// </summary>
/// <remarks>
/// <para>
/// The source ships nothing when HEAD is on a remote and the tree is clean. Otherwise it ships the commits no remote has
/// (<c>git bundle create … HEAD --not --remotes</c>) and the changes of the working tree, untracked files included, as a
/// binary diff from HEAD. The diff is made from an index of its own (<c>GIT_INDEX_FILE</c>), so the checkout of the
/// session is not touched: the files git ignores stay behind, and so do the files whose names look like secrets
/// (<c>.env</c>, <c>*.pem</c>, <c>*.key</c>, <c>id_*</c>, <c>*.tfvars</c>) and the submodules; the manifest lists them.
/// The tree that index writes is the one the target must get.
/// </para>
/// <para>
/// The target fetches the commits into the repository of its project, adds a worktree on the branch at HEAD, applies
/// the diff to an index of its own, checks that it writes the expected tree, and only then writes the changed files
/// into the worktree (<c>git restore --source=&lt;tree&gt; --worktree</c>): its index stays at HEAD, so the changes are
/// changes there too, as they were on the source.
/// </para>
/// </remarks>
public static class GitSessionPayload
{
    /// <summary>The size of a payload when nothing else is said: 64 MiB.</summary>
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;

    /// <summary>How many ignored entries the manifest lists.</summary>
    public const int MaximumListedIgnored = 100;

    private const string CommitsFileName = "commits.bundle";
    private const string PatchFileName = "worktree.patch";

    /// <summary>Gets whether the name of a file looks like a secret, which a move leaves behind unless asked.</summary>
    /// <param name="path">The path of the file, with either separator.</param>
    /// <returns><see langword="true"/> for <c>.env</c>, <c>.env.*</c>, <c>*.pem</c>, <c>*.key</c>, <c>id_*</c> and <c>*.tfvars</c>.</returns>
    public static bool LooksSecret(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = path.Replace('\\', '/').TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal)
            || name.EndsWith(".pem", StringComparison.Ordinal) || name.EndsWith(".key", StringComparison.Ordinal)
            || name.StartsWith("id_", StringComparison.Ordinal)
            || name.EndsWith(".tfvars", StringComparison.Ordinal) || name.EndsWith(".tfvars.json", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads, without writing anything, what a move of a session working in a folder would ship and leave behind: its
    /// repository key, the folder of the project in it, the branch, the URL a target could clone, and the changed files
    /// that look like secrets.
    /// </summary>
    /// <param name="workFolder">The folder the session works in.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The preview; its status says why there is none (<c>not_repository</c>, <c>no_remote</c>, <c>git_unavailable</c>...).</returns>
    /// <exception cref="ArgumentException"><paramref name="workFolder"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<GitPayloadPreview> PreviewAsync(string workFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workFolder);
        if (!Directory.Exists(workFolder)) return new("not_repository");
        var top = await GitAsync(workFolder, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        if (!top.Succeeded || FirstLine(top.Output) is not { } rootLine) return new(StatusOf(top, "not_repository"));
        var root = Path.GetFullPath(rootLine);
        var key = await RepositoryKey.ReadAsync(root, cancellationToken).ConfigureAwait(false);
        if (key is null) return new("no_remote") { Message = "The repository has no remote: another machine cannot find it." };
        var relative = Path.GetRelativePath(root, Path.GetFullPath(workFolder)).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) relative = ".";
        var branch = await GitAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken).ConfigureAwait(false);
        var remote = await GitAsync(root, ["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false);
        if (!remote.Succeeded)
        {
            var remotes = await GitAsync(root, ["remote"], cancellationToken).ConfigureAwait(false);
            if (remotes.Succeeded && FirstLine(remotes.Output) is { } first) remote = await GitAsync(root, ["remote", "get-url", first], cancellationToken).ConfigureAwait(false);
        }

        var unpushed = await GitAsync(root, ["rev-list", "--count", "HEAD", "--not", "--remotes"], cancellationToken).ConfigureAwait(false);
        var status = await GitAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--ignore-submodules=none"],
            cancellationToken).ConfigureAwait(false);
        if (!status.Succeeded) return new(StatusOf(status, "git_failed")) { Message = Clean(status) };
        var changed = ParseStatus(status.Output);
        return new(GitWorktreeService.Ok)
        {
            RepositoryKey = key,
            RelativeFolder = relative,
            Branch = branch.Succeeded ? FirstLine(branch.Output) : null,
            RemoteUrl = remote.Succeeded ? FirstLine(remote.Output) : null,
            UnpushedCommits = unpushed.Succeeded && int.TryParse(FirstLine(unpushed.Output), out var count) ? count : 0,
            Dirty = changed.Count > 0,
            Secrets = [.. changed.Where(path => !path.EndsWith('/') && LooksSecret(path))],
        };
    }

    /// <summary>
    /// Captures the repository a session works in and its work in progress into the <c>git</c> folder of a bundle.
    /// </summary>
    /// <param name="workFolder">The folder the session works in.</param>
    /// <param name="bundleFolder">The folder of the bundle; the payload goes in its <c>git</c> folder.</param>
    /// <param name="maximumBytes">The largest payload allowed.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>What was captured.</returns>
    /// <exception cref="ArgumentException">A folder is blank.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<GitPayloadCapture> CaptureAsync(string workFolder, string bundleFolder, long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFolder);
        if (!Directory.Exists(workFolder)) return new("not_repository", null);
        var top = await GitAsync(workFolder, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        if (!top.Succeeded || FirstLine(top.Output) is not { } rootLine) return new(StatusOf(top, "not_repository"), null, Clean(top));
        var root = Path.GetFullPath(rootLine);
        var key = await RepositoryKey.ReadAsync(root, cancellationToken).ConfigureAwait(false);
        if (key is null) return new("no_remote", null, "The repository has no remote: another machine cannot find it.");
        var head = await GitAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], cancellationToken).ConfigureAwait(false);
        if (!head.Succeeded || FirstLine(head.Output) is not { } headId) return new("no_commit", null, Clean(head));
        var branch = await GitAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken).ConfigureAwait(false);
        var upstream = await GitAsync(root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], cancellationToken).ConfigureAwait(false);
        var common = GitWorktreeService.FindCommonDirectory(root);
        var linked = File.Exists(Path.Combine(root, ".git"));
        var relative = Path.GetRelativePath(root, Path.GetFullPath(workFolder)).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) relative = ".";

        var gitFolder = Path.Combine(bundleFolder, SessionMoveManifest.GitFolder);
        Directory.CreateDirectory(gitFolder);
        // The commits no remote has.
        var unpushed = await GitAsync(root, ["rev-list", "--count", "HEAD", "--not", "--remotes"], cancellationToken).ConfigureAwait(false);
        if (!unpushed.Succeeded || !int.TryParse(FirstLine(unpushed.Output), out var commitCount)) return new(StatusOf(unpushed, "git_failed"), null, Clean(unpushed));
        string? commitsFile = null;
        long bytes = 0;
        if (commitCount > 0)
        {
            var path = Path.Combine(gitFolder, CommitsFileName);
            var bundle = await GitAsync(root, ["bundle", "create", path, "HEAD", "--not", "--remotes"], cancellationToken, write: true).ConfigureAwait(false);
            if (!bundle.Succeeded) return new(StatusOf(bundle, "git_failed"), null, Clean(bundle));
            commitsFile = SessionMoveManifest.GitFolder + "/" + CommitsFileName;
            bytes += new FileInfo(path).Length;
        }

        // The changes of the working tree, untracked files included; what git ignores is not listed by status.
        var status = await GitAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--ignore-submodules=none"],
            cancellationToken).ConfigureAwait(false);
        if (!status.Succeeded) return new(StatusOf(status, "git_failed"), null, Clean(status));
        var changed = ParseStatus(status.Output);
        var leftBehind = new List<SessionMoveLeftBehind>();
        var shipped = new List<string>();
        foreach (var path in changed)
        {
            var full = Path.Combine(root, path.TrimEnd('/'));
            if (path.EndsWith('/') || Directory.Exists(full)) leftBehind.Add(new(path.TrimEnd('/'), SessionMoveLeftBehind.Submodule));
            else if (LooksSecret(path)) leftBehind.Add(new(path, SessionMoveLeftBehind.Secret));
            else shipped.Add(path);
        }

        var ignored = await GitAsync(root, ["ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z"], cancellationToken).ConfigureAwait(false);
        var ignoredEntries = ignored.Succeeded ? ignored.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
        leftBehind.AddRange(ignoredEntries.Take(MaximumListedIgnored).Select(static entry => new SessionMoveLeftBehind(entry.TrimEnd('/'), SessionMoveLeftBehind.Ignored)));

        string tree;
        string? patchFile = null;
        if (shipped.Count == 0)
        {
            var headTree = await GitAsync(root, ["rev-parse", "HEAD^{tree}"], cancellationToken).ConfigureAwait(false);
            if (!headTree.Succeeded || FirstLine(headTree.Output) is not { } headTreeId) return new(StatusOf(headTree, "git_failed"), null, Clean(headTree));
            tree = headTreeId;
        }
        else
        {
            using var scratch = new Scratch();
            var environment = scratch.Environment;
            await File.WriteAllTextAsync(scratch.PathspecFile, string.Join('\0', shipped) + "\0", cancellationToken).ConfigureAwait(false);
            var path = Path.Combine(gitFolder, PatchFileName);
            GitRun step;
            if (!(step = await GitAsync(root, ["read-tree", "HEAD"], cancellationToken, environment, write: true).ConfigureAwait(false)).Succeeded
                || !(step = await GitAsync(root, ["add", "-A", "--pathspec-from-file=" + scratch.PathspecFile, "--pathspec-file-nul"], cancellationToken, environment, write: true).ConfigureAwait(false)).Succeeded)
                return new(StatusOf(step, "git_failed"), null, Clean(step));
            var written = await GitAsync(root, ["write-tree"], cancellationToken, environment, write: true).ConfigureAwait(false);
            if (!written.Succeeded || FirstLine(written.Output) is not { } writtenTree) return new(StatusOf(written, "git_failed"), null, Clean(written));
            tree = writtenTree;
            var diff = await GitAsync(root, ["diff", "--cached", "--binary", "--full-index", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-color",
                "--no-relative", "--src-prefix=a/", "--dst-prefix=b/", "--output=" + path, "HEAD"], cancellationToken, environment, write: true).ConfigureAwait(false);
            if (!diff.Succeeded) return new(StatusOf(diff, "git_failed"), null, Clean(diff));
            if (new FileInfo(path).Length > 0)
            {
                patchFile = SessionMoveManifest.GitFolder + "/" + PatchFileName;
                bytes += new FileInfo(path).Length;
            }
            else
            {
                File.Delete(path);
            }
        }

        if (bytes > maximumBytes)
            return new("payload_too_large", null, $"The work in progress takes {bytes} bytes, more than the {maximumBytes} a move ships.");
        if (commitsFile is null && patchFile is null && Directory.Exists(gitFolder) && !Directory.EnumerateFileSystemEntries(gitFolder).Any())
            Directory.Delete(gitFolder);

        return new(GitWorktreeService.Ok, new SessionMoveRepository
        {
            Key = key,
            RelativeFolder = relative,
            SourceRoot = root,
            Worktree = linked && common is not null,
            Branch = branch.Succeeded ? FirstLine(branch.Output) : null,
            Head = headId,
            Upstream = upstream.Succeeded ? FirstLine(upstream.Output) : null,
            Dirty = changed.Count > 0,
            Payload = new SessionMoveGitPayload
            {
                UnpushedCommits = commitCount,
                CommitsFile = commitsFile,
                PatchFile = patchFile,
                Tree = tree,
                Bytes = bytes,
                LeftBehind = leftBehind,
                IgnoredCount = ignoredEntries.Length,
            },
        });
    }

    /// <summary>
    /// Makes a worktree of the repository of a project for a moved session: on its branch at its HEAD, with its work in
    /// progress, checked against the tree of the manifest. What was made is taken away again when a step fails.
    /// </summary>
    /// <param name="projectFolder">The folder of the project on this machine.</param>
    /// <param name="repository">The repository of the manifest.</param>
    /// <param name="bundleFolder">The folder of the bundle.</param>
    /// <param name="worktreePath">The folder of the new worktree; it must not exist.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>What was prepared.</returns>
    /// <exception cref="ArgumentException">A folder is blank.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<GitPayloadPreparation> PrepareAsync(string projectFolder, SessionMoveRepository repository, string bundleFolder,
        string worktreePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!Directory.Exists(projectFolder)) return new("not_repository");
        var top = await GitAsync(projectFolder, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        if (!top.Succeeded || FirstLine(top.Output) is not { } rootLine) return new(StatusOf(top, "not_repository"), Clean(top));
        var root = Path.GetFullPath(rootLine);
        var payload = repository.Payload;

        // The commits: from the bundle, or from the remote that has them.
        if (payload.CommitsFile is { } commits)
        {
            var bundle = ResolveInBundle(bundleFolder, commits);
            if (!(await GitAsync(root, ["bundle", "verify", "--quiet", bundle], cancellationToken).ConfigureAwait(false)).Succeeded)
            {
                await FetchRemotesAsync(root, cancellationToken).ConfigureAwait(false);
                var verify = await GitAsync(root, ["bundle", "verify", "--quiet", bundle], cancellationToken).ConfigureAwait(false);
                if (!verify.Succeeded) return new(StatusOf(verify, "git_prerequisites_missing"), Clean(verify));
            }

            var fetch = await GitAsync(root, ["fetch", "--quiet", "--no-tags", bundle, "HEAD"], cancellationToken, write: true).ConfigureAwait(false);
            if (!fetch.Succeeded) return new(StatusOf(fetch, "git_failed"), Clean(fetch));
        }

        if (!await HasCommitAsync(root, repository.Head, cancellationToken).ConfigureAwait(false))
        {
            await FetchRemotesAsync(root, cancellationToken).ConfigureAwait(false);
            if (!await HasCommitAsync(root, repository.Head, cancellationToken).ConfigureAwait(false))
                return new("head_missing", $"The commit {repository.Head} is neither in the bundle nor on a remote of this repository.");
        }

        // The branch: its own name when that name is free here or only behind HEAD; another name otherwise, so that a
        // branch of this machine is never moved back or taken from the checkout that has it.
        var (branch, created, addArguments) = await ChooseBranchAsync(root, repository, worktreePath, cancellationToken).ConfigureAwait(false);
        var add = await GitAsync(root, addArguments, cancellationToken, write: true).ConfigureAwait(false);
        if (!add.Succeeded) return new(StatusOf(add, "worktree_failed"), Clean(add));
        try
        {
            if (created && repository.Upstream is { } upstream)
                await GitAsync(worktreePath, ["branch", "--set-upstream-to=" + upstream], cancellationToken, write: true).ConfigureAwait(false);
            if (payload.PatchFile is { } patch)
            {
                var applied = await ApplyPatchAsync(worktreePath, ResolveInBundle(bundleFolder, patch), payload.Tree, cancellationToken).ConfigureAwait(false);
                if (applied is not null)
                {
                    await RemoveAsync(root, worktreePath, created ? branch : null, CancellationToken.None).ConfigureAwait(false);
                    return applied;
                }
            }
            else
            {
                var tree = await GitAsync(worktreePath, ["rev-parse", "HEAD^{tree}"], cancellationToken).ConfigureAwait(false);
                if (FirstLine(tree.Output) != payload.Tree)
                {
                    await RemoveAsync(root, worktreePath, created ? branch : null, CancellationToken.None).ConfigureAwait(false);
                    return new("tree_mismatch", $"The worktree has the tree {FirstLine(tree.Output)}, not {payload.Tree}.");
                }
            }
        }
        catch
        {
            await RemoveAsync(root, worktreePath, created ? branch : null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var folder = repository.RelativeFolder is "." or "" ? worktreePath : Path.Combine(worktreePath, repository.RelativeFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);
        return new(GitWorktreeService.Ok, null, worktreePath, folder, branch, created);
    }

    /// <summary>Takes away a worktree a move prepared, with the branch it created.</summary>
    /// <param name="projectFolder">The folder of the project.</param>
    /// <param name="worktreePath">The worktree.</param>
    /// <param name="createdBranch">The branch the move created; <see langword="null"/> when it created none.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>A task that completes once git ended.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task RemoveAsync(string projectFolder, string worktreePath, string? createdBranch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (Directory.Exists(projectFolder))
        {
            await GitAsync(projectFolder, ["worktree", "remove", "--force", worktreePath], cancellationToken, write: true).ConfigureAwait(false);
            if (createdBranch is not null) await GitAsync(projectFolder, ["branch", "-D", createdBranch], cancellationToken, write: true).ConfigureAwait(false);
        }

        if (Directory.Exists(worktreePath))
        {
            try
            {
                Directory.Delete(worktreePath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for git (worktree prune) or the person to take away.
            }
        }
    }

    // Null when the patch applied and the tree is the expected one; otherwise why not.
    private static async Task<GitPayloadPreparation?> ApplyPatchAsync(string worktree, string patch, string expectedTree, CancellationToken cancellationToken)
    {
        using var scratch = new Scratch();
        var environment = scratch.Environment;
        GitRun step;
        if (!(step = await GitAsync(worktree, ["read-tree", "HEAD"], cancellationToken, environment, write: true).ConfigureAwait(false)).Succeeded)
            return new(StatusOf(step, "git_failed"), Clean(step));
        if (!(step = await GitAsync(worktree, ["apply", "--cached", "--binary", "--whitespace=nowarn", patch], cancellationToken, environment, write: true).ConfigureAwait(false)).Succeeded)
            return new(StatusOf(step, "patch_failed"), Clean(step));
        var written = await GitAsync(worktree, ["write-tree"], cancellationToken, environment, write: true).ConfigureAwait(false);
        if (!written.Succeeded) return new(StatusOf(written, "git_failed"), Clean(written));
        if (FirstLine(written.Output) is not { } tree || tree != expectedTree)
            return new("tree_mismatch", $"The changes give the tree {FirstLine(written.Output)}, not {expectedTree}.");

        // The files the changes touch, written from that tree; the index of the worktree stays at HEAD.
        var names = await GitAsync(worktree, ["diff", "--name-only", "-z", "--no-renames", "HEAD", tree], cancellationToken).ConfigureAwait(false);
        if (!names.Succeeded) return new(StatusOf(names, "git_failed"), Clean(names));
        var paths = names.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0) return null;
        await File.WriteAllTextAsync(scratch.PathspecFile, string.Join('\0', paths) + "\0", cancellationToken).ConfigureAwait(false);
        var restore = await GitAsync(worktree, ["restore", "--source=" + tree, "--worktree", "--pathspec-from-file=" + scratch.PathspecFile, "--pathspec-file-nul"],
            cancellationToken, new Dictionary<string, string> { ["GIT_LITERAL_PATHSPECS"] = "1" }, write: true).ConfigureAwait(false);
        return restore.Succeeded ? null : new(StatusOf(restore, "patch_failed"), Clean(restore));
    }

    private static async Task<(string? Branch, bool Created, string[] Arguments)> ChooseBranchAsync(string root, SessionMoveRepository repository,
        string worktreePath, CancellationToken cancellationToken)
    {
        if (repository.Branch is not { Length: > 0 } wanted || !GitWorktreeService.IsReferenceName(wanted))
            return (null, false, ["worktree", "add", "--detach", worktreePath, repository.Head]);
        var listed = await GitAsync(root, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        var checkedOut = listed.Succeeded
            ? GitWorktreeService.ParseWorktrees(listed.Output).Select(static worktree => worktree.Branch).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        for (var attempt = 1; attempt <= 50; attempt++)
        {
            var name = attempt == 1 ? wanted : attempt == 2 ? wanted + "-moved" : $"{wanted}-moved-{attempt - 1}";
            var tip = await GitAsync(root, ["rev-parse", "--verify", "--quiet", "refs/heads/" + name], cancellationToken).ConfigureAwait(false);
            if (!tip.Succeeded) return (name, true, ["worktree", "add", "-b", name, worktreePath, repository.Head]);
            if (checkedOut.Contains(name)) continue;
            var tipId = FirstLine(tip.Output);
            if (tipId == repository.Head) return (name, false, ["worktree", "add", worktreePath, name]);
            // A branch of this machine that is only behind HEAD goes forward; one that went elsewhere is kept.
            var behind = await GitAsync(root, ["merge-base", "--is-ancestor", tipId!, repository.Head], cancellationToken).ConfigureAwait(false);
            if (behind.Succeeded) return (name, false, ["worktree", "add", "-B", name, worktreePath, repository.Head]);
        }

        return (null, false, ["worktree", "add", "--detach", worktreePath, repository.Head]);
    }

    private static async Task<bool> HasCommitAsync(string root, string commit, CancellationToken cancellationToken)
        => (await GitAsync(root, ["cat-file", "-e", commit + "^{commit}"], cancellationToken).ConfigureAwait(false)).Succeeded;

    private static Task<GitRun> FetchRemotesAsync(string root, CancellationToken cancellationToken)
        => GitAsync(root, ["fetch", "--all", "--quiet", "--no-tags"], cancellationToken, write: true);

    // A path of the manifest inside the bundle folder; a path that leaves it is refused.
    private static string ResolveInBundle(string bundleFolder, string relative)
    {
        var root = Path.GetFullPath(bundleFolder);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!GitWorktreeService.IsWithin(full, root)) throw new ArgumentException($"'{relative}' is not a path of the bundle.", nameof(relative));
        return full;
    }

    // The paths of `git status --porcelain=v1 -z --no-renames`: "XY path", separated by NUL.
    private static List<string> ParseStatus(string output)
    {
        var paths = new List<string>();
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Length > 3) paths.Add(entry[3..]);
        }

        return paths;
    }

    private static Task<GitRun> GitAsync(string folder, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null, bool write = false)
        => GitWorktreeService.RunGitAsync(folder, arguments, write ? GitWorktreeService.WriteTimeout : GitWorktreeService.ReadTimeout, environment, cancellationToken);

    private static string StatusOf(GitRun run, string failed) => run.ExitCode == GitRun.NotStarted ? "git_unavailable" : failed;

    private static string? Clean(GitRun run)
        => run.Succeeded ? null : (run.Error.Length > 0 ? run.Error : run.Output).Trim() is { Length: > 0 } text ? text : null;

    private static string? FirstLine(string output)
        => output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    // An index and a list of paths of its own, removed with it.
    private sealed class Scratch : IDisposable
    {
        private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-move-" + Guid.NewGuid().ToString("N"))).FullName;

        public string PathspecFile => Path.Combine(_folder, "paths");

        public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
        {
            ["GIT_INDEX_FILE"] = Path.Combine(_folder, "index"),
            ["GIT_LITERAL_PATHSPECS"] = "1",
        };

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A temporary file left behind.
            }
        }
    }
}
