using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Reads the version of a Claude Code executable by asking it (<c>claude --version</c>), to tell whether a CLI the user
/// installed is the version CodeAlta is tested with (<see cref="ClaudeCodePinnedCli.Version"/>).
/// </summary>
public static partial class ClaudeCodeCliVersion
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Reads the version in what <c>claude --version</c> writes, such as <c>2.1.292 (Claude Code)</c>.</summary>
    /// <param name="output">What the executable wrote.</param>
    /// <returns>The version, such as <c>2.1.292</c>, or null when the output has none.</returns>
    public static string? Parse(string? output)
        => output is not null && VersionPattern().Match(output) is { Success: true } match ? match.Value : null;

    /// <summary>Starts an executable with <c>--version</c> and reads its version.</summary>
    /// <param name="executable">The full path of the executable.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The version, or null when the executable did not start, did not answer in ten seconds or wrote none.</returns>
    /// <exception cref="ArgumentException"><paramref name="executable"/> is null or blank.</exception>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public static async Task<string?> ReadAsync(string executable, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--version");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return Parse(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (process is not null)
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
                    // The process ended on its own.
                }

                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Tells the user that the Claude Code they installed is not the version CodeAlta is tested with.
    /// </summary>
    /// <param name="installedVersion">The version of the executable that runs, or null when it is not known.</param>
    /// <returns>The warning, or null when the versions are the same or the version is not known.</returns>
    public static string? DescribeDifference(string? installedVersion)
        => installedVersion is null || string.Equals(installedVersion, ClaudeCodePinnedCli.Version, StringComparison.Ordinal)
            ? null
            : $"This Claude Code is version {installedVersion}; CodeAlta is tested with {ClaudeCodePinnedCli.Version}. " +
              "Download that version in Settings > Providers to run it instead.";

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
