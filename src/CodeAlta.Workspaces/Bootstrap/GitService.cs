using System.Diagnostics;

namespace CodeAlta.Workspaces.Bootstrap;

/// <summary>
/// Shells out to the <c>git</c> CLI for repository operations.
/// </summary>
public sealed class GitService
{
    /// <summary>
    /// Ensures the provided directory is a git repository by running <c>git init</c> when needed.
    /// </summary>
    /// <param name="path">Repository path.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The git command result.</returns>
    public Task<GitCommandResult> InitAsync(
        string path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        Directory.CreateDirectory(path);
        return RunAsync(path, "init", progress, cancellationToken);
    }

    /// <summary>
    /// Clones a repository into the provided directory.
    /// </summary>
    /// <param name="repoUrl">Repository URL.</param>
    /// <param name="destinationPath">Destination path.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The git command result.</returns>
    public Task<GitCommandResult> CloneAsync(
        string repoUrl,
        string destinationPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            throw new ArgumentException("Repository URL is required.", nameof(repoUrl));
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination path is required.", nameof(destinationPath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath)) ?? ".");
        return RunAsync(
            Environment.CurrentDirectory,
            $"clone \"{repoUrl}\" \"{Path.GetFullPath(destinationPath)}\"",
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Pulls changes for the current branch.
    /// </summary>
    public Task<GitCommandResult> PullAsync(
        string repoPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(repoPath, "pull --ff-only", progress, cancellationToken);
    }

    /// <summary>
    /// Returns porcelain status output.
    /// </summary>
    public Task<GitCommandResult> StatusAsync(
        string repoPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(repoPath, "status --porcelain", progress, cancellationToken);
    }

    /// <summary>
    /// Stages all file changes.
    /// </summary>
    public Task<GitCommandResult> AddAllAsync(
        string repoPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(repoPath, "add -A", progress, cancellationToken);
    }

    /// <summary>
    /// Creates a commit with the provided message.
    /// </summary>
    public Task<GitCommandResult> CommitAsync(
        string repoPath,
        string message,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Commit message is required.", nameof(message));
        }

        return RunAsync(repoPath, $"commit -m \"{message.Replace("\"", "\\\"", StringComparison.Ordinal)}\"", progress, cancellationToken);
    }

    /// <summary>
    /// Pushes current branch to its upstream remote.
    /// </summary>
    public Task<GitCommandResult> PushAsync(
        string repoPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(repoPath, "push", progress, cancellationToken);
    }

    /// <summary>
    /// Adds or updates the <c>origin</c> remote.
    /// </summary>
    public async Task<GitCommandResult> SetOriginAsync(
        string repoPath,
        string remoteUrl,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            throw new ArgumentException("Remote URL is required.", nameof(remoteUrl));
        }

        var result = await RunAsync(repoPath, $"remote set-url origin \"{remoteUrl}\"", progress, cancellationToken)
            .ConfigureAwait(false);
        if (result.Success)
        {
            return result;
        }

        // If origin does not exist, add it.
        return await RunAsync(repoPath, $"remote add origin \"{remoteUrl}\"", progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        string arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Working directory is required.", nameof(workingDirectory));
        }

        var normalizedWorkingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(normalizedWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory '{normalizedWorkingDirectory}' was not found.");
        }

        var processStartInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = normalizedWorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process
        {
            StartInfo = processStartInfo,
            EnableRaisingEvents = false,
        };

        progress?.Report($"git {arguments}");
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var commandLine = $"git {arguments}";
        var result = new GitCommandResult
        {
            CommandLine = commandLine,
            WorkingDirectory = normalizedWorkingDirectory,
            ExitCode = process.ExitCode,
            StandardOutput = output,
            StandardError = error,
        };

        if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
        {
            progress?.Report(result.CombinedOutput.Trim());
        }

        return result;
    }
}

