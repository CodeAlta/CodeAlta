using CodeAlta.Catalog;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode labels</c>.</summary>
internal sealed class NodeLabelCommandOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the words: the subcommand and its labels.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// The labels of the machine of a profile (<see cref="FleetMachineLabels"/>), for its owner on that machine:
/// <c>altanode labels list</c>, <c>add</c>, <c>remove</c> and <c>set</c>. A node that runs publishes them at its next
/// publication, and the rules of environments read them at the next run.
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
internal sealed class NodeLabelCommands(TextWriter output, TextWriter error)
{
    public async ValueTask<int> RunAsync(NodeLabelCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var fleetRoot = new CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot }.FleetRoot;
        var (command, labels) = options.Words is [var first, .. var rest] ? (first, rest) : ("list", []);
        try
        {
            var held = FleetMachineLabels.Read(fleetRoot);
            IReadOnlyList<string> kept;
            switch (command)
            {
                case "list" when labels.Count == 0:
                    kept = held;
                    break;
                case "add" when labels.Count > 0:
                    kept = FleetMachineLabels.Write(fleetRoot, [.. held, .. labels]);
                    break;
                case "remove" when labels.Count > 0:
                    kept = FleetMachineLabels.Write(fleetRoot, held.Where(label => !labels.Contains(label, StringComparer.OrdinalIgnoreCase)));
                    break;
                case "set":
                    kept = FleetMachineLabels.Write(fleetRoot, labels);
                    break;
                case "list" or "add" or "remove":
                    return await UsageAsync(command == "list" ? "labels list takes no argument." : $"labels {command} takes one label or more.").ConfigureAwait(false);
                default:
                    return await UsageAsync($"'{command}' is not a command of labels: list, add, remove or set.").ConfigureAwait(false);
            }

            await output.WriteLineAsync(kept.Count == 0 ? "This machine has no label." : string.Join(Environment.NewLine, kept)).ConfigureAwait(false);
            return NodeExitCodes.Success;
        }
        catch (ArgumentException exception)
        {
            return await UsageAsync(exception.Message.Split(" (Parameter", 2)[0]).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("altanode: " + exception.Message).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
    }

    private async Task<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
