using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop;

/// <summary>
/// The landing page of the window, as a plugin of the application: a canvas that the page draws itself, and the command
/// that opens it.
/// </summary>
/// <remarks>
/// <para>
/// The plugin declares the canvas and nothing else: the tab is drawn by a module of the application's own build
/// (<c>src/CodeAlta/frontend/src/landing</c>), which reads the projects, the sessions and the providers from the services the
/// window already has, and the cards of plugins from <see cref="Rpc.PluginUiService.LandingCardsAsync"/>. Being a canvas, the page is
/// listed with the others, is closed and restored as a tab, and is opened by <c>/landing</c>, <c>alta landing open</c> and <c>alta canvas</c>.
/// </para>
/// <para>It holds no state: what the user chose for the page (opening it at start-up, its animation) is kept by the window.</para>
/// </remarks>
[Plugin(AltaLanding.PluginId, DisplayName = "Landing page", Description = "The landing page of CodeAlta Desktop: recent projects and sessions, the documentation, the statistics and the cards of plugins.")]
internal sealed class DesktopLandingPlugin : PluginBase
{
    /// <summary>The name of the module of the application's build that draws the page.</summary>
    internal const string AppModule = "landing";

    /// <summary>The definition a host lists among its built-in plugins.</summary>
    internal static BuiltInPluginDefinition Definition()
        => new()
        {
            Id = AltaLanding.PluginId, DisplayName = "Landing page",
            Description = "The landing page of CodeAlta Desktop: recent projects and sessions, the documentation, the statistics and the cards of plugins.",
            PluginType = typeof(DesktopLandingPlugin), Factory = static () => new DesktopLandingPlugin(),
        };

    /// <inheritdoc />
    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = AltaLanding.CanvasId,
            Title = "Welcome",
            Description = "The landing page: recent projects and sessions of the space, the documentation, the statistics and the cards that plugins pin.",
            Icon = "house",
            Scope = PluginCanvasScope.Application,
            // Before the other canvases of the page that lists them.
            Order = -1000,
            Open = static (_, _) => new ValueTask<PluginCanvasView>(new PluginCanvasView { Script = PluginScript.App(AppModule) }),
            Describe = static (_, _) => new ValueTask<string?>(
                "# Welcome\n\nThe landing page of CodeAlta Desktop. It shows the recent projects and sessions of the space, links to the documentation and to the settings of the providers, "
                + "and the cards that plugins pin (the Statistics overview among them). It holds nothing of its own: read projects with `alta project list` and sessions with `alta session list`."),
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell(AltaLanding.CanvasId, "Opens the landing page: recent projects and sessions, the documentation and the statistics.", OpenAsync) with
        {
            Label = "Welcome",
            SearchText = "landing welcome home start page recent",
        };
    }

    private async ValueTask<PluginCommandResult> OpenAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        var result = await Services.Canvases.OpenAsync(AltaLanding.CanvasId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Requested ? PluginCommandResult.Handled : PluginCommandResult.Message("The landing page cannot be shown here.");
    }
}
