using System.Text;
using CodeAlta.Catalog.Worktrees;
using XenoAtom.Logging;

namespace CodeAlta.Catalog.Reviews;

/// <summary>Reads the sides of the files a review is about, and writes snapshots of a work tree.</summary>
public interface IReviewTextSource
{
    /// <summary>Reads one side of a file as text.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="target">One of <see cref="ReviewTargets"/>.</param>
    /// <param name="side">One of <see cref="ReviewSides"/>.</param>
    /// <param name="path">The file, as a thread names it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The text, or null when the side does not exist, is too large or is not text.</returns>
    Task<string?> ReadAsync(ReviewScope scope, string target, string side, string path, CancellationToken cancellationToken);

    /// <summary>Writes the work tree of a checkout as a git tree, without changing its index, its files or its references.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The id of the tree, or null when git could not write it.</returns>
    Task<string?> SnapshotAsync(ReviewScope scope, CancellationToken cancellationToken);

    /// <summary>Lists the files that differ between two snapshots of a checkout.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="from">The first tree.</param>
    /// <param name="to">The second tree.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The paths, relative to the root of the checkout with <c>/</c>; empty when they cannot be listed.</returns>
    Task<IReadOnlyList<string>> ChangedPathsAsync(ReviewScope scope, string from, string to, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>
/// Reads the sides of files with git: the new side from the work tree, the old side from the last commit (the
/// changes) or from the commit the branch left its base at (the branch).
/// </summary>
public sealed class GitReviewTextSource : IReviewTextSource
{
    /// <summary>The largest side read, in bytes; a larger file has no text.</summary>
    public const int MaximumBytes = 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly GitRunner _run;
    private readonly Func<ReviewScope, CancellationToken, Task<string?>>? _branchBase;

    /// <summary>Creates the source over the <c>git</c> found on the path.</summary>
    public GitReviewTextSource()
        : this(branchBase: null)
    {
    }

    /// <summary>Creates the source over the <c>git</c> found on the path, with the base of a branch found by the caller.</summary>
    /// <param name="branchBase">
    /// Finds the commit the branch of a checkout left its base at, as the changes view finds it; null finds the
    /// merge base with the upstream branch, else with <c>origin/HEAD</c>, <c>main</c> or <c>master</c>.
    /// </param>
    public GitReviewTextSource(Func<ReviewScope, CancellationToken, Task<string?>>? branchBase)
    {
        _run = GitWorktreeService.RunGitAsync;
        _branchBase = branchBase;
    }

    /// <inheritdoc />
    public async Task<string?> ReadAsync(ReviewScope scope, string target, string side, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!ReviewService.IsValidPath(path))
        {
            return null;
        }

        var root = ReviewTargets.IsDiff(target) ? GitWorktreeService.FindCheckoutRoot(scope.Folder) ?? scope.Folder : scope.Folder;
        if (side == ReviewSides.New)
        {
            return ReadFile(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        }

        var commit = target == ReviewTargets.Branch ? await BaseAsync(scope, cancellationToken).ConfigureAwait(false) : "HEAD";
        if (commit is null)
        {
            return null;
        }

        var run = await _run(root, ["show", commit + ":" + path], Timeout, cancellationToken).ConfigureAwait(false);
        return run.Succeeded && run.Output.Length <= MaximumBytes && !run.Output.Contains('\0') ? run.Output : null;
    }

    /// <inheritdoc />
    public Task<string?> SnapshotAsync(ReviewScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var root = GitWorktreeService.FindCheckoutRoot(scope.Folder);
        return root is null ? Task.FromResult<string?>(null) : GitSnapshot.WriteTreeAsync(root, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ChangedPathsAsync(ReviewScope scope, string from, string to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!IsTree(from) || !IsTree(to) || GitWorktreeService.FindCheckoutRoot(scope.Folder) is not { } root)
        {
            return [];
        }

        var run = await _run(root, ["diff-tree", "-r", "--name-only", "--no-renames", "-z", from, to], Timeout, cancellationToken).ConfigureAwait(false);
        return run.Succeeded ? run.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
    }

    private static bool IsTree(string? id) => id is { Length: >= 40 and <= 64 } && id.All(Uri.IsHexDigit);

    // Where the branch left its base, as the changes view of the desktop finds it: the upstream branch, else the main
    // branch of `origin` or of the repository that is not the branch itself; then their merge base with HEAD.
    private async Task<string?> BaseAsync(ReviewScope scope, CancellationToken cancellationToken)
    {
        if (_branchBase is not null)
        {
            return await _branchBase(scope, cancellationToken).ConfigureAwait(false);
        }

        var upstream = await _run(scope.Folder, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], Timeout, cancellationToken).ConfigureAwait(false);
        var reference = upstream.Succeeded ? upstream.Output.Trim() : null;
        if (string.IsNullOrEmpty(reference))
        {
            var branch = await _run(scope.Folder, ["rev-parse", "--abbrev-ref", "HEAD"], Timeout, cancellationToken).ConfigureAwait(false);
            var current = branch.Succeeded ? branch.Output.Trim() : null;
            var names = await _run(scope.Folder, ["for-each-ref", "--format=%(refname:short)", "refs/remotes/origin/main", "refs/remotes/origin/master",
                "refs/heads/main", "refs/heads/master"], Timeout, cancellationToken).ConfigureAwait(false);
            reference = names.Succeeded
                ? names.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(name => !string.Equals(name, current, StringComparison.Ordinal))
                : null;
        }

        if (string.IsNullOrEmpty(reference) || reference.StartsWith('-') || !GitWorktreeService.IsReferenceName(reference))
        {
            return null;
        }

        var run = await _run(scope.Folder, ["merge-base", "HEAD", reference], Timeout, cancellationToken).ConfigureAwait(false);
        return run.Succeeded && run.Output.Trim() is { Length: >= 40 } commit && commit.All(Uri.IsHexDigit) ? commit : null;
    }

    private static string? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan().Contains((byte)0))
            {
                return null;
            }

            return new UTF8Encoding(false, false).GetString(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Writes the work tree of a checkout as a git tree, through an index of its own.</summary>
public static class GitSnapshot
{
    private const int MaximumDiagnosticLength = 512;
    private const string UnwritableIndexError = "unable to write new index file";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Writes tracked files and unignored new files as a tree of the repository, through a copy of the index.
    /// The real index, files and references are not changed. Git writes new content objects in the repository,
    /// where its garbage collection removes them later. If git cannot replace the copy, it creates another
    /// temporary index from the same tracked entries and writes the tree through that index.
    /// </summary>
    /// <param name="checkoutRoot">The root folder of the checkout.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The id of the tree, or null when git could not write it.</returns>
    /// <exception cref="ArgumentException"><paramref name="checkoutRoot"/> is blank.</exception>
    public static async Task<string?> WriteTreeAsync(string checkoutRoot, CancellationToken cancellationToken)
        => (await WriteTreeWithDiagnosticAsync(checkoutRoot, cancellationToken).ConfigureAwait(false)).Tree;

    // indexPath: gives the path of each temporary index; a test gives one that git cannot replace.
    internal static async Task<(string? Tree, string? Diagnostic)> WriteTreeWithDiagnosticAsync(
        string checkoutRoot, CancellationToken cancellationToken, Func<string>? indexPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutRoot);
        indexPath ??= static () => Path.Combine(Path.GetTempPath(), "alta-review-" + Guid.NewGuid().ToString("N") + ".index");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var stage = "git rev-parse";
        string? index = null;
        string? fallbackIndex = null;
        SnapshotFailure? firstFailure = null;

        Task<GitRun> Git(string[] arguments)
        {
            stage = $"git {arguments[0]}";
            return GitWorktreeService.RunGitAsync(checkoutRoot, arguments, Timeout, environment, cancellationToken);
        }

        async Task<(string? Tree, SnapshotFailure? Failure)> WriteTreeAsync()
        {
            var added = await Git(["add", "-A", "--", "."]).ConfigureAwait(false);
            if (!added.Succeeded)
            {
                return (null, GitFailure(stage, added));
            }

            var tree = await Git(["write-tree"]).ConfigureAwait(false);
            if (!tree.Succeeded)
            {
                return (null, GitFailure(stage, tree));
            }

            return ObjectId(tree.Output) is { } id ? (id, null) : (null, new(stage, "git did not return a tree id"));
        }

        try
        {
            var location = await Git(["rev-parse", "--git-path", "index"]).ConfigureAwait(false);
            if (!location.Succeeded)
            {
                return Finish(null, GitFailure(stage, location));
            }
            if (location.Output.Trim() is not { Length: > 0 } relative)
            {
                return Finish(null, new SnapshotFailure(stage, "git did not return an index path"));
            }

            stage = "copy index";
            var real = Path.GetFullPath(relative, checkoutRoot);
            index = indexPath();
            if (File.Exists(real))
            {
                File.Copy(real, index, overwrite: true);
            }

            environment["GIT_INDEX_FILE"] = index;
            var (tree, failure) = await WriteTreeAsync().ConfigureAwait(false);
            if (failure is not { IndexUnwritable: true })
            {
                return Finish(tree, failure);
            }

            firstFailure = failure;
            stage = "prepare fallback index";
            fallbackIndex = indexPath();
            var emptyTree = await Git(["mktree"]).ConfigureAwait(false);
            if (!emptyTree.Succeeded)
            {
                return Finish(null, firstFailure, GitFailure(stage, emptyTree));
            }
            if (ObjectId(emptyTree.Output) is not { } emptyId)
            {
                return Finish(null, firstFailure, new SnapshotFailure(stage, "git did not return an empty tree id"));
            }

            // A two-tree reset with identical trees carries stage-0 entries forward, but drops conflict stages.
            // Save them so add still knows about conflicted paths, including those under ignore rules.
            // Git's quoted index-info format preserves path bytes through the text runner.
            stage = "git ls-files";
            var unmerged = await GitWorktreeService.RunGitAsync(checkoutRoot, ["-c", "core.quotepath=on", "ls-files", "--unmerged"],
                Timeout, environment, cancellationToken).ConfigureAwait(false);
            if (!unmerged.Succeeded)
            {
                return Finish(null, firstFailure, GitFailure(stage, unmerged));
            }
            if (unmerged.Output.Length >= GitWorktreeService.MaximumOutputCharacters)
            {
                return Finish(null, firstFailure, new SnapshotFailure(stage, "unmerged index entries exceeded the output limit"));
            }

            // Git renames its lock to the fresh output, not over the file a filter is holding. Starting from HEAD
            // instead would lose staged additions (and restore staged removals) under an ignore rule.
            var seed = await Git(["read-tree", "--reset", "-i", "--no-sparse-checkout", $"--index-output={fallbackIndex}", emptyId, emptyId]).ConfigureAwait(false);
            if (!seed.Succeeded)
            {
                return Finish(null, firstFailure, GitFailure(stage, seed));
            }

            environment["GIT_INDEX_FILE"] = fallbackIndex;
            if (unmerged.Output.Length > 0)
            {
                stage = "git update-index";
                var restored = await GitWorktreeService.RunGitAsync(checkoutRoot, ["update-index", "--index-info"],
                    Timeout, environment, unmerged.Output, cancellationToken).ConfigureAwait(false);
                if (!restored.Succeeded)
                {
                    return Finish(null, firstFailure, GitFailure(stage, restored));
                }
            }

            var (retried, fallbackFailure) = await WriteTreeAsync().ConfigureAwait(false);
            return Finish(retried, firstFailure, fallbackFailure);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var failure = new SnapshotFailure(stage, $"{exception.GetType().Name} (0x{exception.HResult:X8})", BoundDiagnostic(exception.Message));
            return firstFailure is null ? Finish(null, failure) : Finish(null, firstFailure, failure);
        }
        finally
        {
            if (index is not null)
            {
                DeleteTemporaryIndex(index);
            }
            if (fallbackIndex is not null)
            {
                DeleteTemporaryIndex(fallbackIndex);
            }
        }
    }

    private static string? ObjectId(string output)
        => output.Trim() is { Length: 40 or 64 } id && id.All(Uri.IsHexDigit) ? id : null;

    private static void DeleteTemporaryIndex(string index)
    {
        try
        {
            File.Delete(index);
            File.Delete(index + ".lock");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary file left behind is removed with the other temporary files.
        }
    }

    private static SnapshotFailure GitFailure(string stage, GitRun run)
    {
        var unwritable = IsUnwritableIndex(run);
        var reason = run.ExitCode switch
        {
            GitRun.NotStarted => "git could not start",
            GitRun.TimedOut => "git timed out",
            _ when unwritable => UnwritableIndexError,
            _ when run.Error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) => "permission denied",
            _ when run.Error.Contains("File exists", StringComparison.OrdinalIgnoreCase) => "file already exists",
            _ => $"git exited with code {run.ExitCode}",
        };
        return new SnapshotFailure(stage, reason, BoundDiagnostic(run.Error), unwritable);
    }

    private static string? BoundDiagnostic(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.Length switch
        {
            0 => null,
            > MaximumDiagnosticLength => string.Concat(trimmed[..(MaximumDiagnosticLength - 1)], "…"),
            _ => trimmed.ToString(),
        };
    }

    private static (string? Tree, string? Diagnostic) Finish(string? tree, SnapshotFailure? failure, SnapshotFailure? fallback = null)
    {
        if (failure is null)
        {
            return (tree, null);
        }

        // Git's stderr and exception messages can name private paths or include output from a configured filter.
        // Keep them for test assertions only; even the application log gets only the stage and a safe reason.
        if (LogManager.IsInitialized)
        {
            var logger = LogManager.GetLogger("CodeAlta.Reviews");
            if (tree is null)
            {
                var summary = fallback is null ? failure.Summary : $"{failure.Summary}; fallback: {fallback.Summary}";
                logger.Warn($"Could not write a review snapshot: {summary}.");
            }
            else
            {
                logger.Debug($"Wrote a review snapshot through a fresh index after {failure.Summary}.");
            }
        }

        return (tree, fallback is null ? failure.Diagnostic : $"{failure.Diagnostic}; fallback: {fallback.Diagnostic}");
    }

    private sealed record SnapshotFailure(string Stage, string Reason, string? Error = null, bool IndexUnwritable = false)
    {
        public string Summary => $"{Stage}: {Reason}";
        public string Diagnostic => Error is null ? Summary : $"{Summary}: {Error}";
    }

    // What git (run with LC_ALL=C) says when it cannot put the index it wrote in place of the one it read: "Unable" up to
    // git 2.39, "unable" after.
    internal static bool IsUnwritableIndex(GitRun run)
        => run.Error.Contains(UnwritableIndexError, StringComparison.OrdinalIgnoreCase);
}
