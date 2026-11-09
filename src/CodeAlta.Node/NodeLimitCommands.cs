using System.Globalization;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode limits</c>.</summary>
internal sealed class NodeLimitCommandOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the words: the limit and its value.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// The limits of the machine of a profile (<see cref="FleetMachineLimits"/>), for its owner on that machine:
/// <c>altanode limits</c> shows them, <c>altanode limits sessions 3</c> and <c>altanode limits sessions off</c> change how many
/// sessions work there at once. A node that runs reads the file before each decision: the change applies to the next run.
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
internal sealed class NodeLimitCommands(TextWriter output, TextWriter error)
{
    public async ValueTask<int> RunAsync(NodeLimitCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var fleetRoot = new CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot }.FleetRoot;
        try
        {
            int? sessions;
            switch (options.Words)
            {
                case [] or ["list"]:
                    sessions = FleetMachineLimits.ReadSessions(fleetRoot);
                    break;
                case ["sessions", var value]:
                    if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)) sessions = null;
                    else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= FleetMachineLimits.MaxSessions) sessions = count;
                    else return await UsageAsync($"limits sessions takes a number from 1 to {FleetMachineLimits.MaxSessions}, or off.").ConfigureAwait(false);
                    FleetMachineLimits.WriteSessions(fleetRoot, sessions);
                    break;
                case ["sessions"]:
                    return await UsageAsync($"limits sessions takes a number from 1 to {FleetMachineLimits.MaxSessions}, or off.").ConfigureAwait(false);
                default:
                    return await UsageAsync($"'{string.Join(' ', options.Words)}' is not a command of limits: limits, or limits sessions <number>|off.").ConfigureAwait(false);
            }

            await output.WriteLineAsync(sessions is { } limit
                ? $"Sessions at work at once: {limit.ToString(CultureInfo.InvariantCulture)}; the work of other machines waits for its turn beyond it."
                : "Sessions at work at once: no limit.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }
        catch (InvalidDataException exception)
        {
            await error.WriteLineAsync("altanode: " + exception.Message).ConfigureAwait(false);
            return NodeExitCodes.Failure;
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
