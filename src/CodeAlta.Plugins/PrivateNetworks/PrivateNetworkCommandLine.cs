using System.Diagnostics;

using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.PrivateNetworks;

/// <summary>Runs the command lines the providers use (<c>tailscale</c>, <c>devtunnel</c>): found on the path or where their installers put them.</summary>
internal static class PrivateNetworkCommandLine
{
    /// <summary>The executable of a command line: on the path first, then in the folders its installers use.</summary>
    /// <param name="name">Its name, without the <c>.exe</c> of Windows.</param>
    /// <param name="usual">The full paths its installers use, for a CodeAlta started before the path was changed.</param>
    internal static string? Find(string name, IEnumerable<string> usual)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), file);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A folder of the path that is no path.
            }
        }

        return usual.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Runs a command without a window and waits for its end, <paramref name="patience"/> at most. Its output is read to the end whatever
    /// happens: a command that waits is stopped after a while, and what it printed still says why.
    /// </summary>
    /// <returns>Its exit code and its output, its errors after it; -1 and <see cref="CommandLineResult.TimedOut"/> when it did not end in time.</returns>
    /// <exception cref="InvalidOperationException">The command did not start.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    internal static async Task<CommandLineResult> RunAsync(string path, IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken)
    {
        using var process = Process.Start(Info(path, args)) ?? throw new InvalidOperationException($"{Path.GetFileName(path)} did not start.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var ended = await WaitAsync(process, patience, cancellationToken).ConfigureAwait(false);
        var text = await output.ConfigureAwait(false) + await errors.ConfigureAwait(false);
        return new(ended ? process.ExitCode : -1, text, !ended);
    }

    /// <summary>How a command starts: its output and its errors redirected, without a window.</summary>
    internal static ProcessStartInfo Info(string path, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    // Waits for the end of a process; one that does not end in time is killed. A cancellation of the caller is thrown again.
    private static async Task<bool> WaitAsync(Process process, TimeSpan patience, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(patience);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }

            if (cancellationToken.IsCancellationRequested) throw;
            return false;
        }
    }
}
