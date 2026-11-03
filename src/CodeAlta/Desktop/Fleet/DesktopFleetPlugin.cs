using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Fleet;

/// <summary>
/// The console of the fleet, as a plugin of the window (<c>doc/fleet/console.md</c>): its first canvas, Fleet, shows the
/// machines of the organization of the server this computer is linked to, with their presence and the sessions they
/// publish, live from the directory of the control connection; without a server, the machines paired with this computer.
/// </summary>
/// <remarks>
/// The plugin declares the canvas and the command that opens it; the canvas is drawn by a module of the application's own
/// build (<c>src/CodeAlta/frontend/src/fleet/canvas</c>), which asks the <c>fleetServer</c> service of the window for what
/// it shows. It holds no state of its own.
/// </remarks>
[Plugin(PluginId, DisplayName = "Fleet", Description = Summary)]
internal sealed class DesktopFleetPlugin : PluginBase
{
    /// <summary>The id of the plugin.</summary>
    internal const string PluginId = "fleet";

    /// <summary>The id of the canvas: <c>alta canvas open fleet</c>.</summary>
    internal const string CanvasId = "fleet";

    /// <summary>The name of the module of the application's build that draws the canvas.</summary>
    internal const string AppModule = "fleet";

    private const string Summary = "The console of the fleet in CodeAlta Desktop: the machines of the organization of a CodeAlta Server, or the machines paired with this computer, and their sessions.";

    /// <summary>The definition a window lists among its built-in plugins.</summary>
    internal static BuiltInPluginDefinition Definition()
        => new()
        {
            Id = PluginId, DisplayName = "Fleet", Description = Summary,
            PluginType = typeof(DesktopFleetPlugin), Factory = static () => new DesktopFleetPlugin(),
        };

    /// <inheritdoc />
    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = CanvasId,
            Title = "Fleet",
            Description = "The machines of the organization of your CodeAlta Server, whether they are online, and the sessions they publish: state, machine, provider and model, times.",
            Icon = "remote",
            Scope = PluginCanvasScope.Application,
            Open = static (_, _) => new ValueTask<PluginCanvasView>(new PluginCanvasView { Script = PluginScript.App(AppModule) }),
            Describe = static (_, _) => new ValueTask<string?>(
                "# Fleet\n\nThe machines of the organization of the CodeAlta Server this computer is linked to (Settings > Fleet > Server), whether each is online, "
                + "and the sessions they publish: their state, machine, provider and model, and times. Titles and content are never published. "
                + "A session of this computer or of a paired machine opens in a tab; the others are shown only."),
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell(CanvasId, "Opens the Fleet canvas: the machines of your CodeAlta Server and the sessions they publish.", OpenAsync) with
        {
            Label = "Fleet",
            SearchText = "fleet server machines nodes console sessions organization",
        };
    }

    private async ValueTask<PluginCommandResult> OpenAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        var result = await Services.Canvases.OpenAsync(CanvasId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Requested ? PluginCommandResult.Handled : PluginCommandResult.Message("The Fleet canvas cannot be shown here.");
    }
}
