using System.Text;
using CodeAlta.Catalog.Worktrees;

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

    private async Task<string?> BaseAsync(ReviewScope scope, CancellationToken cancellationToken)
    {
        if (_branchBase is not null)
        {
            return await _branchBase(scope, cancellationToken).ConfigureAwait(false);
        }

        foreach (var candidate in (string[])["@{upstream}", "origin/HEAD", "main", "master"])
        {
            var run = await _run(scope.Folder, ["merge-base", "HEAD", candidate], Timeout, cancellationToken).ConfigureAwait(false);
            if (run.Succeeded && run.Output.Trim() is { Length: >= 40 } commit)
            {
                return commit;
            }
        }

        return null;
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
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Writes every file of the work tree that git does not ignore as a tree of the repository. The index of the
    /// checkout is copied to a temporary file first, so that only the files changed since are hashed; the real
    /// index, the files and the references are not changed. Git writes the objects of new contents in the
    /// repository, where its garbage collection removes them later.
    /// </summary>
    /// <param name="checkoutRoot">The root folder of the checkout.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The id of the tree, or null when git could not write it.</returns>
    /// <exception cref="ArgumentException"><paramref name="checkoutRoot"/> is blank.</exception>
    public static async Task<string?> WriteTreeAsync(string checkoutRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutRoot);
        var index = Path.Combine(Path.GetTempPath(), "alta-review-" + Guid.NewGuid().ToString("N") + ".index");
        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = index };
            Task<GitRun> Git(IReadOnlyList<string> arguments, bool own)
                => GitWorktreeService.RunGitAsync(checkoutRoot, arguments, Timeout, own ? environment : null, cancellationToken);

            var location = await Git(["rev-parse", "--git-path", "index"], own: false).ConfigureAwait(false);
            if (location.Succeeded && location.Output.Trim() is { Length: > 0 } relative)
            {
                var real = Path.GetFullPath(relative, checkoutRoot);
                if (File.Exists(real))
                {
                    File.Copy(real, index, overwrite: true);
                }
            }

            if (!(await Git(["add", "-A", "--", "."], own: true).ConfigureAwait(false)).Succeeded)
            {
                return null;
            }

            var tree = await Git(["write-tree"], own: true).ConfigureAwait(false);
            return tree.Succeeded && tree.Output.Trim() is { Length: >= 40 } id && id.All(Uri.IsHexDigit) ? id : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
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
    }
}
