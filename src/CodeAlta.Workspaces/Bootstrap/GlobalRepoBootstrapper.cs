namespace CodeAlta.Workspaces.Bootstrap;

/// <summary>
/// Ensures the global CodeAlta repository exists on disk.
/// </summary>
public sealed class GlobalRepoBootstrapper
{
    private readonly GitService _git;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalRepoBootstrapper"/> class.
    /// </summary>
    /// <param name="git">Git service.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="git"/> is <see langword="null"/>.</exception>
    public GlobalRepoBootstrapper(GitService git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <summary>
    /// Ensures the global repo exists and is initialized.
    /// </summary>
    /// <param name="globalRepoRoot">Global repository root directory.</param>
    /// <param name="remoteUrl">Optional remote URL to clone or set as origin.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Bootstrap result.</returns>
    public async Task<GlobalRepoBootstrapResult> EnsureAsync(
        string globalRepoRoot,
        string? remoteUrl = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(globalRepoRoot))
        {
            throw new ArgumentException("Global repo root is required.", nameof(globalRepoRoot));
        }

        var normalizedRoot = Path.GetFullPath(globalRepoRoot);
        var createdDirectory = false;
        if (!Directory.Exists(normalizedRoot))
        {
            Directory.CreateDirectory(normalizedRoot);
            createdDirectory = true;
        }

        var dotGit = Path.Combine(normalizedRoot, ".git");
        var initialized = false;
        var cloned = false;

        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
        {
            if (!string.IsNullOrWhiteSpace(remoteUrl) && Directory.EnumerateFileSystemEntries(normalizedRoot).Any() is false)
            {
                var cloneResult = await _git.CloneAsync(remoteUrl, normalizedRoot, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (cloneResult.Success)
                {
                    cloned = true;
                }
            }

            if (!cloned)
            {
                var initResult = await _git.InitAsync(normalizedRoot, progress, cancellationToken).ConfigureAwait(false);
                initialized = initResult.Success;
            }
        }

        if (!string.IsNullOrWhiteSpace(remoteUrl) && (Directory.Exists(Path.Combine(normalizedRoot, ".git"))))
        {
            _ = await _git.SetOriginAsync(normalizedRoot, remoteUrl, progress, cancellationToken).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.Combine(normalizedRoot, "workspaces"));
        Directory.CreateDirectory(Path.Combine(normalizedRoot, "machines"));

        return new GlobalRepoBootstrapResult
        {
            GlobalRepoRoot = normalizedRoot,
            CreatedDirectory = createdDirectory,
            InitializedRepository = initialized,
            ClonedRepository = cloned,
            OriginRemoteUrl = string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl,
        };
    }
}

