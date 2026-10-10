using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

/// <summary>Names the landing page of the desktop application: a canvas of a plugin of the application.</summary>
public static class AltaLanding
{
    /// <summary>The identifier of the built-in plugin that provides the landing page.</summary>
    public const string PluginId = "landing";

    /// <summary>The runtime key of that plugin.</summary>
    public const string PluginKey = "builtin:" + PluginId;

    /// <summary>The identifier of the canvas of the landing page, and the name of the command that opens it.</summary>
    public const string CanvasId = "landing";
}

/// <summary>
/// The <c>alta landing</c> commands: the landing page of the window, which is a canvas of the application. A host that shows
/// canvases has them; the page is opened as any canvas is.
/// </summary>
internal sealed partial class BuiltInAltaCommandContributor
{
    // It changes nothing but what the window shows.
    private static readonly AltaCommandPolicy[] LandingPolicies =
    [
        Read("landing open"),
    ];

    private static Command CreateLandingCommand(AltaCommandContext context)
    {
        var group = Group("landing", "Show the landing page of the window: recent projects and sessions, the documentation, the statistics and the cards of plugins.");
        string? space = null;
        var open = Leaf("open", "Open the landing page in a tab, or bring its tab to the front.");
        open.Add("space=", "The space to open the tab in. Defaults to the space the window shows.", value => space = value);
        open.Add(async (_, _) => await HandleLandingOpenAsync(context, space).ConfigureAwait(false));
        group.Add(open);
        AddHelpText(
            group,
            "The landing page is the user's start page: open it when the user asks for it, not to report your own work.",
            "It is the canvas `" + AltaLanding.PluginKey + "/" + AltaLanding.CanvasId + "`: `alta canvas describe`, `close` and `list --open` apply to it.",
            "Example: `alta landing open`.");
        return group;
    }

    private static async ValueTask<int> HandleLandingOpenAsync(AltaCommandContext context, string? spaceRef)
    {
        const string CommandPath = "alta landing open";
        if (!TryGetCanvasView(context, out var view))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var canvas = view.List().FirstOrDefault(static item => string.Equals(item.PluginKey, AltaLanding.PluginKey, StringComparison.Ordinal)
            && string.Equals(item.Id, AltaLanding.CanvasId, StringComparison.Ordinal));
        if (canvas is null)
        {
            return Unsupported(context, "landing.unavailable", "The landing page is not available: its plugin is turned off or did not start.");
        }

        var (target, targetExit) = await ResolveCanvasContextAsync(context, canvas, null, null, CommandPath, required: true).ConfigureAwait(false);
        if (target is null)
        {
            return targetExit;
        }

        var (spaceId, spaceName, spaceExit) = await ResolveCanvasSpaceAsync(context, canvas, target, spaceRef, CommandPath).ConfigureAwait(false);
        if (spaceExit != AltaExitCodes.Success)
        {
            return spaceExit;
        }

        if (!view.HasWindow)
        {
            return CanvasWindowUnavailable(context);
        }

        var opened = await view.OpenAsync(CanvasTargetOf(canvas, target, spaceId, null), null, focus: true, context.CancellationToken).ConfigureAwait(false);
        switch (opened.Status)
        {
            case "requested":
                break;
            case "unavailable":
                return CanvasWindowUnavailable(context);
            default:
                return Unsupported(context, "landing.unavailable", "The landing page is not available: its plugin is turned off or did not start.");
        }

        // As `alta canvas open` writes it: what the host does not have (a space, on a host without spaces) is left out of the record.
        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.landing.opened",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["instanceId"] = opened.InstanceId,
            ["spaceId"] = opened.SpaceId,
            ["space"] = spaceName,
            ["shown"] = opened.Shown,
        });
        return AltaExitCodes.Success;
    }
}
