using CodeAlta.Plugins.Abstractions;
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

        var visuals = pluginHostBridge.CreateVisuals(PluginUiRegion.CommandBar)
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
}
