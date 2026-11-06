using CodeAlta.Catalog;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode permission-mode</c>.</summary>
internal sealed class NodePermissionModeCommandOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the words: the mode to set, or none to show it.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// The permission mode the sessions of the machine of a profile run in when nothing else chooses it
/// (<see cref="FleetSessionDefaults"/>), for its owner on that machine: <c>altanode permission-mode</c> shows it,
/// <c>altanode permission-mode bypass|ask</c> sets it. A node that runs reads it again for each session and each request.
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
internal sealed class NodePermissionModeCommands(TextWriter output, TextWriter error)
{
    public async ValueTask<int> RunAsync(NodePermissionModeCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var fleetRoot = new CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot }.FleetRoot;
        string? mode = null;
        if (options.Words is [var word] && !FleetSessionDefaults.TryParsePermissionMode(word, out mode) || options.Words.Count > 1)
            return await UsageAsync("permission-mode takes bypass or ask, or nothing to show the mode.").ConfigureAwait(false);
        try
        {
            var defaults = mode is null ? FleetSessionDefaults.Read(fleetRoot) : FleetSessionDefaults.Write(fleetRoot, new FleetSessionDefaultsInfo(mode));
            await output.WriteLineAsync(defaults.PermissionMode == SessionPermissionModes.Bypass
                ? "The sessions of this machine bypass permissions: they run commands and change files without asking, unless they have a mode of their own."
                : "The sessions of this machine ask first: a command or a file change waits for a person, unless they have a mode of their own.").ConfigureAwait(false);
            return NodeExitCodes.Success;
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
