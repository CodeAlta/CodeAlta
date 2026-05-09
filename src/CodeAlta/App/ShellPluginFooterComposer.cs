using CodeAlta.Plugins.Abstractions;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.App;

internal static class ShellPluginFooterComposer
{
    public static Visual Compose(Visual threadCommandBar, PluginHostBridge? pluginHostBridge)
    {
        ArgumentNullException.ThrowIfNull(threadCommandBar);
        if (pluginHostBridge is null)
        {
            return threadCommandBar;
        }

        var footerStatus = ComposeStatusItems(pluginHostBridge, PluginUiRegion.ThreadFooter);
        var visuals = new[] { footerStatus }
            .Where(static visual => visual is not null)
            .Cast<Visual>()
            .Concat(pluginHostBridge.CreateVisuals(PluginUiRegion.CommandBar))
            .Concat(pluginHostBridge.CreateVisuals(PluginUiRegion.ThreadFooter))
            .ToArray();
        if (visuals.Length == 0)
        {
            return threadCommandBar;
        }

        return new VStack(visuals.Append(threadCommandBar).ToArray())
        {
            HorizontalAlignment = Align.Stretch,
        };
    }

    public static Visual? ComposeStatusItems(PluginHostBridge? pluginHostBridge, PluginUiRegion region)
    {
        if (pluginHostBridge is null)
        {
            return null;
        }

        var items = pluginHostBridge.GetStatusItems(region);
        if (items.Count == 0)
        {
            return null;
        }

        return new Markup(string.Join("  ", items.Select(FormatStatusItem)))
        {
            Wrap = false,
        };
    }

    public static Visual? ComposeRegion(PluginHostBridge? pluginHostBridge, PluginUiRegion region)
    {
        if (pluginHostBridge is null)
        {
            return null;
        }

        var visuals = new[] { ComposeStatusItems(pluginHostBridge, region) }
            .Where(static visual => visual is not null)
            .Cast<Visual>()
            .Concat(pluginHostBridge.CreateVisuals(region))
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
