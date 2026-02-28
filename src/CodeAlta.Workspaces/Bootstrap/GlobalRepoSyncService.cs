namespace CodeAlta.Workspaces.Bootstrap;

/// <summary>
/// Syncs the global CodeAlta repository by pulling, committing local changes, and pushing.
/// </summary>
public sealed class GlobalRepoSyncService
{
    private readonly GitService _git;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalRepoSyncService"/> class.
    /// </summary>
    /// <param name="git">Git service.</param>
    public GlobalRepoSyncService(GitService git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <summary>
    /// Executes a best-effort sync for the provided repository path.
    /// </summary>
    /// <param name="repoPath">Repository path.</param>
    /// <param name="commitMessage">Commit message used when changes exist.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sync result.</returns>
    public async Task<GlobalRepoSyncResult> SyncAsync(
        string repoPath,
        string commitMessage = "CodeAlta sync",
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            throw new ArgumentException("Repository path is required.", nameof(repoPath));
        }

        var normalized = Path.GetFullPath(repoPath);

        var pulled = false;
        var pull = await _git.PullAsync(normalized, progress, cancellationToken).ConfigureAwait(false);
        pulled = pull.Success;

        var status = await _git.StatusAsync(normalized, progress, cancellationToken).ConfigureAwait(false);
        var dirty = !string.IsNullOrWhiteSpace(status.StandardOutput);
        var committed = false;
        if (dirty)
        {
            _ = await _git.AddAllAsync(normalized, progress, cancellationToken).ConfigureAwait(false);
            var commit = await _git.CommitAsync(normalized, commitMessage, progress, cancellationToken).ConfigureAwait(false);
            committed = commit.Success;
        }

        var push = await _git.PushAsync(normalized, progress, cancellationToken).ConfigureAwait(false);
        var pushed = push.Success;

        return new GlobalRepoSyncResult
        {
            CommittedChanges = committed,
            Pulled = pulled,
            Pushed = pushed,
        };
    }
}

