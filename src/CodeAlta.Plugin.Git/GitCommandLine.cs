using System.Diagnostics;

namespace CodeAlta.Plugin.Git;

/// <summary>Runs <c>git</c> and the provider CLIs (<c>gh</c>, <c>glab</c>, <c>az</c>) for the Git backend.</summary>
internal static class GitCommandLine
{
    public static async Task<GitCommandLineResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // The desktop has no console to share: without this each command opens a console window,
                // which takes the foreground from the application's own window.
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new GitCommandLineResult(process.ExitCode, workingDirectory, stdout, stderr);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new GitCommandLineResult(-1, workingDirectory, string.Empty, fileName + " timed out.");
        }
    }

    /// <summary>Runs a located CLI with its leading arguments followed by <paramref name="arguments"/>.</summary>
    public static Task<GitCommandLineResult> RunAsync(GitCliCommand command, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        => RunAsync(command.FileName, [.. command.LeadingArguments, .. arguments], workingDirectory, timeout, cancellationToken);

    /// <summary>Reads the URL of one remote, or null when git is missing, fails or reports nothing.</summary>
    public static async Task<string?> TryGetGitRemoteUrlAsync(string workingDirectory, string remoteName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync("git", ["-C", workingDirectory, "remote", "get-url", remoteName], workingDirectory, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return null;
            }

            var remoteUrl = result.Stdout.Trim();
            return string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Lists the remote names of a repository; empty when git is missing or fails.</summary>
    public static async Task<IReadOnlyList<string>> GetGitRemoteNamesAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync("git", ["-C", workingDirectory, "remote"], workingDirectory, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return [];
            }

            return result.Stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static remoteName => !string.IsNullOrWhiteSpace(remoteName))
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Asks a provider CLI to print a token (<c>gh auth token</c>, <c>glab config get token</c>,
    /// <c>az account get-access-token</c>); null when the CLI is missing, signed out or fails.
    /// </summary>
    public static async Task<string?> TryReadTokenAsync(string cliName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            if (GitCliLocator.Find(cliName) is not { } command)
            {
                return null;
            }

            var result = await RunAsync(command, arguments, Environment.CurrentDirectory, timeout, cancellationToken).ConfigureAwait(false);
            var token = result.ExitCode == 0 ? result.Stdout.Trim() : string.Empty;
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}

internal sealed record GitCommandLineResult(int ExitCode, string WorkingDirectory, string Stdout, string Stderr);

/// <summary>How to start a provider CLI: the executable, and the arguments that come before the caller's.</summary>
internal sealed record GitCliCommand(string FileName, IReadOnlyList<string> LeadingArguments);

/// <summary>Finds the provider CLIs on the <c>PATH</c> without starting them.</summary>
internal static class GitCliLocator
{
    /// <summary>Finds a CLI by its bare name (<c>gh</c>, <c>glab</c>, <c>az</c>) on the process <c>PATH</c>.</summary>
    public static GitCliCommand? Find(string name)
        => Find(name, Environment.GetEnvironmentVariable("PATH"));

    /// <summary>Finds a CLI by its bare name in the directories of <paramref name="searchPath"/>.</summary>
    public static GitCliCommand? Find(string name, string? searchPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return null;
        }

        // On Windows a bare name is not a program (the Azure CLI ships a shell script named `az` beside az.cmd).
        ReadOnlySpan<string> extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [string.Empty];
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(directory, name + extension));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    break; // A malformed PATH entry names no program.
                }

                if (File.Exists(candidate))
                {
                    return extension is ".cmd" or ".bat" ? FromBatchFile(candidate) : new GitCliCommand(candidate, []);
                }
            }
        }

        return null;
    }

    // A batch file runs through cmd.exe, which parses its command line again: an argument array cannot be
    // handed to it intact. The Azure CLI's az.cmd only starts the Python installed beside it, so that
    // Python is started directly; any other batch file is not used.
    private static GitCliCommand? FromBatchFile(string path)
    {
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), "az", StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(path) is not { } directory)
        {
            return null;
        }

        var python = Path.GetFullPath(Path.Combine(directory, "..", "python.exe"));
        return File.Exists(python) ? new GitCliCommand(python, ["-IBm", "azure.cli"]) : null;
    }
}
