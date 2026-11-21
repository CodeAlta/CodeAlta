using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CodeAlta.Agent;

/// <summary>The Antigravity CLI of Google (<c>agy</c>), which runs as an ACP agent (<c>agy acp</c>).</summary>
public static class AntigravityCli
{
    /// <summary>
    /// Gets whether a command line starts the ACP server of the CLI: its executable (<c>agy</c>, or
    /// <c>antigravity</c> as its archive names it) with the argument <c>acp</c>.
    /// </summary>
    /// <param name="command">The executable: a name on <c>PATH</c> or a path, with the separators of any system.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>True for the ACP server of the CLI.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool IsAcpServer(string command, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(arguments);
        var name = Path.GetFileNameWithoutExtension(command[(command.LastIndexOfAny(['/', '\\']) + 1)..]);
        return (name.Equals("agy", StringComparison.OrdinalIgnoreCase) || name.Equals("antigravity", StringComparison.OrdinalIgnoreCase)) &&
               arguments.Contains("acp", StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Reads what a Google AI plan has left for Antigravity by asking the Antigravity CLI, which is the one that knows
/// the account: for each group of models, the five-hour window and the week. A short-lived process answers
/// (<c>agy --print /usage --output-format json</c>) and exits; no model is called, and CodeAlta never reads the
/// credentials of the CLI.
/// </summary>
public static class AntigravityAccountUsage
{
    private const long FiveHourMinutes = 5 * 60;
    private const long SevenDayMinutes = 7 * 24 * 60;
    private const int MaximumLimits = 16;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Starts the CLI, asks it for the usage of its account, and waits for it to exit.</summary>
    /// <param name="executable">The executable of the CLI: a name on <c>PATH</c> or a path.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The usage; that the CLI is missing or not signed in; or not available when its account has no limits of a plan.</returns>
    /// <exception cref="ArgumentException"><paramref name="executable"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">The CLI did not answer, or answered with something else than its usage.</exception>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public static async Task<AgentSubscriptionUsageReading> ReadAsync(string executable, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (Path.IsPathFullyQualified(executable) && !File.Exists(executable))
        {
            return AgentSubscriptionUsageReading.NoTool;
        }

        // The process works in the profile of the user, as the readings of the other CLIs do: /usage opens no conversation.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(home) ? Environment.CurrentDirectory : home,
        };
        foreach (var argument in (string[])["--print", "/usage", "--output-format", "json"])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return AgentSubscriptionUsageReading.NoTool;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        string output;
        string error;
        try
        {
            process.StandardInput.Close();
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            error = await errorTask.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"The Antigravity CLI at '{executable}' did not answer.");
        }

        return Parse(output, error, DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException($"The Antigravity CLI at '{executable}' did not answer with its usage (exit code {process.ExitCode}).");
    }

    /// <summary>
    /// Reads the usage of what the CLI wrote, or <see langword="null"/> when it is not an answer to <c>/usage</c>. The
    /// shape of the answer is not a contract: what is missing is left out.
    /// </summary>
    internal static AgentSubscriptionUsageReading? Parse(string output, string error, DateTimeOffset now)
    {
        // A CLI that is not signed in says so in words, on either stream, and writes no usage.
        if ((output + "\n" + error).Contains("sign in", StringComparison.OrdinalIgnoreCase) && !output.Contains("\"groups\"", StringComparison.Ordinal))
        {
            return AgentSubscriptionUsageReading.NotSignedIn;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.Object ||
                !command.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var limits = new List<AgentSubscriptionLimit>();
            if (data.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    // The shorter window first, as the other providers give theirs.
                    var name = Text(group, "name");
                    limits.AddRange(buckets.EnumerateArray()
                        .Select(bucket => Window(bucket, name))
                        .OfType<AgentSubscriptionLimit>()
                        .OrderBy(static limit => limit.WindowMinutes ?? long.MaxValue));
                }
            }

            return limits.Count == 0
                ? AgentSubscriptionUsageReading.NotAvailable
                : new(AgentSubscriptionUsageReading.Ok, new AgentSubscriptionUsage(null, [.. limits.Take(MaximumLimits)], now));
        }
    }

    // A bucket says what is left of it (remaining_fraction, from 0 to 1) and when it starts over; a window nothing was
    // used of yet has not started, and comes with the first instant of Unix time.
    private static AgentSubscriptionLimit? Window(JsonElement bucket, string? group)
    {
        if (bucket.ValueKind != JsonValueKind.Object || Text(bucket, "id") is not { } id ||
            !bucket.TryGetProperty("remaining_fraction", out var remaining) || remaining.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        long? minutes = Text(bucket, "window") switch
        {
            "5h" => FiveHourMinutes,
            "weekly" => SevenDayMinutes,
            _ => null,
        };
        DateTimeOffset? resetsAt = Text(bucket, "reset_time") is { } text &&
                                   DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) &&
                                   at > DateTimeOffset.UnixEpoch
            ? at
            : null;
        return new AgentSubscriptionLimit(id, group, UsedPercent: Math.Clamp((1 - remaining.GetDouble()) * 100, 0, 100), ResetsAt: resetsAt, WindowMinutes: minutes);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It ended by itself.
        }
    }
}
