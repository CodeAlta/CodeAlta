using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Search;

/// <summary>
/// Search, as a plugin of the window: it tells the index when a prompt is written and when a turn ends, so that both
/// are found within seconds instead of at the next pass.
/// </summary>
/// <remarks>The index and its queries are the window's (<see cref="DesktopSearch"/>); the plugin holds no state.</remarks>
[Plugin(PluginId, DisplayName = "Search", Description = Summary)]
internal sealed class DesktopSearchPlugin : PluginBase
{
    /// <summary>The id of the plugin.</summary>
    internal const string PluginId = "search";

    private const string Summary = "Local search of CodeAlta Desktop: the prompts and final answers of the sessions, the user guide, the skills, the tools and the plugins, for the search window and alta search.";

    private readonly DesktopSearchRelay _relay;

    private DesktopSearchPlugin(DesktopSearchRelay relay) => _relay = relay;

    /// <summary>The definition a window lists among its built-in plugins.</summary>
    /// <param name="relay">Where the events of the sessions go.</param>
    internal static BuiltInPluginDefinition Definition(DesktopSearchRelay relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        return new()
        {
            Id = PluginId, DisplayName = "Search", Description = Summary,
            PluginType = typeof(DesktopSearchPlugin), Factory = () => new DesktopSearchPlugin(relay),
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
    {
        // One line, the same from turn to turn while search is on: provider prompt caches survive.
        yield return Prompt.Dynamic(
            PluginPromptChannel.Developer,
            (_, _) => new ValueTask<string?>(_relay.Enabled() ? Guidance : null),
            title: "Search",
            kind: PluginPromptPartKind.ToolGuidance,
            order: 55);
    }

    /// <summary>What the instructions of a session say about search while it is on.</summary>
    internal const string Guidance = "Search: `alta search <need>` finds earlier conversations, plans and tasks, skills, tools, MCP servers and the user guide on this computer; " +
        "`--kind skill,tool,mcp,doc` keeps to what can be used, and `alta search show <ref>` reads a hit whole.";

    /// <inheritdoc />
    public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // A signal, nothing more: the index reads the journal a moment later, and the callback returns at once.
        _relay.Forward(context.Event);
        return ValueTask.CompletedTask;
    }
}
