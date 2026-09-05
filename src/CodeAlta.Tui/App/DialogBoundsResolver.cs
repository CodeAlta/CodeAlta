using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.App;

internal static class DialogBoundsResolver
{
    public static Rectangle? ResolveAppBounds(Visual? focusTarget)
        => PluginDialogLayout.ResolveDialogBounds(focusTarget);
}
