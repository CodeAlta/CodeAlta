using CodeAlta.Plugins.Abstractions;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tui.App;

internal static class ShellPluginFooterComposer
{
    public static Visual Compose(Visual sessionCommandBar, PluginHostBridge? pluginHostBridge)
    {
        ArgumentNullException.ThrowIfNull(sessionCommandBar);
        if (pluginHostBridge is null)
        {
            return sessionCommandBar;
        }

        var footerStatus = ComposeStatusItems(pluginHostBridge, PluginUiRegion.SessionFooter);
        var visuals = new[] { footerStatus }
            .Where(static visual => visual is not null)
            .Cast<Visual>()
            .Concat(pluginHostBridge.CreateVisuals(PluginUiRegion.CommandBar))
            .Concat(pluginHostBridge.CreateVisuals(PluginUiRegion.SessionFooter))
            .ToArray();
        if (visuals.Length == 0)
        {
            return sessionCommandBar;
        }

        return new VStack(visuals.Append(sessionCommandBar).ToArray())
        {
            HorizontalAlignment = Align.Stretch,
        };
    }

    public static Visual? ComposeStatusItems(PluginHostBridge? pluginHostBridge, PluginUiRegion region, string? sessionId = null)
    {
        if (pluginHostBridge is null)
        {
            return null;
        }

        var items = pluginHostBridge.GetStatusItems(region, sessionId);
        if (items.Count == 0)
        {
            return null;
        }

        return new Markup(string.Join("  ", items.Select(FormatStatusItem)))
        {
            Wrap = false,
        };
    }

    public static Visual? ComposeRegion(PluginHostBridge? pluginHostBridge, PluginUiRegion region, string? sessionId = null)
    {
        if (pluginHostBridge is null)
        {
            return null;
        }

        var visuals = new[] { ComposeStatusItems(pluginHostBridge, region, sessionId) }
            .Where(static visual => visual is not null)
            .Cast<Visual>()
            .Concat(pluginHostBridge.CreateVisuals(region, sessionId))
            .ToArray();
        return visuals.Length switch
        {
            0 => null,
            1 => visuals[0],
            _ => new HStack(visuals) { Spacing = 2 },
        };
    }

    private static string FormatStatusItem(PluginStatusItem item)
    {
        var tone = item.Tone switch
        {
            PluginStatusTone.Success => "success",
            PluginStatusTone.Warning => "warning",
            PluginStatusTone.Error => "error",
            PluginStatusTone.Muted => "muted",
            _ => "primary",
        };
        var icon = string.IsNullOrWhiteSpace(item.IconMarkup) ? string.Empty : item.IconMarkup + " ";
        return $"[{tone}]{icon}{AnsiMarkup.Escape(item.Label)}[/][dim]: {AnsiMarkup.Escape(item.Text)}[/]";
    }
}
