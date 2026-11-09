using System.Globalization;
using CodeAlta.ModelServers;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode llama</c>.</summary>
internal sealed class NodeLlamaCommandOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the words: the subcommand.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// <c>altanode llama status</c>: the llama.cpp side of the running node, for its owner on the machine: its GPUs, its
/// builds and their update policy, the models on its disk and their downloads, and its servers with what they load.
/// CodeAlta Desktop changes them, on the Model servers page.
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
internal sealed class NodeLlamaCommands(TextWriter output, TextWriter error)
{
    /// <summary>Gets what stops the command.</summary>
    public CancellationToken Stop { get; init; }

    public async ValueTask<int> RunAsync(NodeLlamaCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Words is not ([] or ["status"]))
            return await UsageAsync($"'{string.Join(' ', options.Words)}' is not a command of llama: status.").ConfigureAwait(false);
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var control = NodeLocalControl.Read(profile);
        if (!control.NodeListens)
        {
            await error.WriteLineAsync("altanode: no node runs on this profile; its model servers run with it (altanode run, or altanode install).").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var (client, identity) = await control.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using (identity)
            await using (client.ConfigureAwait(false))
            {
                var status = await client.GetModelServersAsync(timeout.Token).ConfigureAwait(false);
                foreach (var line in Lines(status, TimeZoneInfo.Local)) await output.WriteLineAsync(line).ConfigureAwait(false);
            }

            return NodeExitCodes.Success;
        }
        catch (Fleet.Protocol.FleetException exception)
        {
            await error.WriteLineAsync("altanode: the node runs no model servers (" + exception.Message + ").").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !Stop.IsCancellationRequested)
        {
            await error.WriteLineAsync("altanode: the node did not answer: " + NodeClientCommands.Describe(exception)).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
    }

    /// <summary>The lines of the status of a model host.</summary>
    /// <param name="status">The status.</param>
    /// <param name="zone">The time zone of the times shown.</param>
    /// <returns>The lines.</returns>
    internal static IEnumerable<string> Lines(ModelHostStatus status, TimeZoneInfo zone)
    {
        string Size(long bytes) => (bytes / 1e9).ToString(bytes >= 100e9 ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB";
        string Time(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var gpus = status.Hardware.Gpus.Where(static gpu => !gpu.Integrated).Select(gpu => gpu.Name + (gpu.MemoryBytes is { } memory ? $" ({Size(memory)})" : "")).ToList();
        yield return $"GPUs: {(gpus.Count > 0 ? string.Join(", ", gpus) : "none for llama.cpp")}.";
        foreach (var line in NodeServiceCommands.ModelServersLines(status).Take(1)) yield return line.Replace("Model servers:", "Servers:", StringComparison.Ordinal);
        var policy = status.Settings.Updates;
        yield return "Updates: " + (policy.Schedule == "manual" ? "when you click Update" : $"{policy.Schedule} at {policy.Time}{(policy.Schedule == "weekly" ? " on " + policy.Day : "")}")
            + $", {(policy.Channel == "pinned" ? "staying on " + policy.PinnedBuild : policy.Channel == "every" ? "every build" : "stable builds")}"
            + (status.Planned is { } planned ? $"; planned {Time(planned.At)}{(planned.Build is { } build ? " to " + build : "")}." : ".");
        if (status.History.FirstOrDefault() is { } last)
            yield return $"Last update: {Time(last.At)}, {last.Backend} {last.From ?? "none"} -> {last.To}: {last.Result}{(last.Reason is { } reason ? $" ({reason})" : "")}.";
        yield return $"Models: {status.Models.Count}, {Size(status.ModelsBytes)} in {status.ModelsFolder}; {Size(status.FreeBytes)} free.";
        foreach (var download in status.Downloads.Where(static download => download.State is "queued" or "downloading" or "checking" or "paused" or "failed"))
            yield return $"  {download.Model}: {download.State}" + (download.Total > 0 ? $", {Size(download.Done)} of {Size(download.Total)}" : "") + (download.Error is { } problem ? $" ({problem})" : "") + ".";
        foreach (var line in NodeServiceCommands.ModelServersLines(status).Skip(1)) yield return line;
        foreach (var task in status.Tasks.Where(static task => task.EndedAt is null)) yield return $"Running: {task.Title}{(task.Message is { } message ? " - " + message : "")}.";
        foreach (var warning in status.Warnings) yield return "Warning: " + warning;
    }

    private async ValueTask<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
