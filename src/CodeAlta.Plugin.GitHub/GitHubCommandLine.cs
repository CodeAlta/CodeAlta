using System.Diagnostics;

namespace CodeAlta.Plugin.GitHub;

/// <summary>Runs the <c>git</c> and <c>gh</c> executables for the GitHub backend.</summary>
internal static class GitHubCommandLine
{
    public static async Task<GitHubCommandLineResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
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
            return new GitHubCommandLineResult(process.ExitCode, workingDirectory, stdout, stderr);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new GitHubCommandLineResult(-1, workingDirectory, string.Empty, fileName + " timed out.");
        }
    }

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

    /// <summary>Asks the GitHub CLI for its token, or null when it is missing, signed out or fails.</summary>
    public static async Task<string?> TryGetGhAuthTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync("gh", ["auth", "token"], Environment.CurrentDirectory, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
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

internal sealed record GitHubCommandLineResult(int ExitCode, string WorkingDirectory, string Stdout, string Stderr);
