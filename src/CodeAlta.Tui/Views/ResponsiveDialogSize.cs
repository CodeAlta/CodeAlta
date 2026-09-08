using CodeAlta.Plugins.Tui;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.Views;

internal static class ResponsiveDialogSize
{
    public static Size Resolve(
        Rectangle? bounds,
        int minWidth,
        int minHeight,
        double widthFactor = 0.8,
        double heightFactor = 0.8)
        => PluginDialogLayout.ResolveResponsiveSize(bounds, minWidth, minHeight, widthFactor, heightFactor);

    public static Dialog Apply(
        Dialog dialog,
        Rectangle? bounds,
        int minWidth,
        int minHeight,
        double widthFactor = 0.8,
        double heightFactor = 0.8)
        => PluginDialogLayout.ApplyResponsiveSize(dialog, bounds, minWidth, minHeight, widthFactor, heightFactor);
}
