using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Views;

internal static class ResponsiveDialogSize
{
    public static Size Resolve(
        Rectangle? bounds,
        int minWidth,
        int minHeight,
        double widthFactor = 0.8,
        double heightFactor = 0.8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minHeight);

        if (widthFactor is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(widthFactor), widthFactor, "Dialog width factor must be in the (0, 1] range.");
        }

        if (heightFactor is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(heightFactor), heightFactor, "Dialog height factor must be in the (0, 1] range.");
        }

        var availableWidth = Math.Max(minWidth, bounds?.Width ?? minWidth);
        var availableHeight = Math.Max(minHeight, bounds?.Height ?? minHeight);
        var width = Math.Max(minWidth, (int)Math.Round(availableWidth * widthFactor, MidpointRounding.AwayFromZero));
        var height = Math.Max(minHeight, (int)Math.Round(availableHeight * heightFactor, MidpointRounding.AwayFromZero));
        return new Size(width, height);
    }

    public static Dialog Apply(
        Dialog dialog,
        Rectangle? bounds,
        int minWidth,
        int minHeight,
        double widthFactor = 0.8,
        double heightFactor = 0.8)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var size = Resolve(bounds, minWidth, minHeight, widthFactor, heightFactor);
        return dialog
            .Width(size.Width)
            .Height(size.Height);
    }
}
