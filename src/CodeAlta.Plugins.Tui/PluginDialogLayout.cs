using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Plugins.Tui;

/// <summary>
/// Provides layout helpers for plugin-owned dialogs.
/// </summary>
public static class PluginDialogLayout
{
    /// <summary>
    /// Resolves the application bounds that should be used as the sizing reference for a dialog anchored to a visual.
    /// </summary>
    /// <param name="anchor">The visual that owns or opens the dialog, or <see langword="null" /> when no anchor is available.</param>
    /// <returns>
    /// The root application bounds when available, the anchor bounds as a fallback, or <see langword="null" /> when no anchor is available.
    /// </returns>
    public static Rectangle? ResolveDialogBounds(Visual? anchor)
    {
        if (anchor is null)
        {
            return null;
        }

        return anchor.App?.Root.GetAbsoluteBounds() ?? anchor.GetAbsoluteBounds();
    }

    /// <summary>
    /// Resolves a responsive dialog size from the available dialog bounds and requested minimum dimensions.
    /// </summary>
    /// <param name="bounds">The available dialog bounds, or <see langword="null" /> to use the minimum dimensions.</param>
    /// <param name="minWidth">The minimum dialog width.</param>
    /// <param name="minHeight">The minimum dialog height.</param>
    /// <param name="widthFactor">The fraction of the available width to use, in the (0, 1] range.</param>
    /// <param name="heightFactor">The fraction of the available height to use, in the (0, 1] range.</param>
    /// <returns>The resolved dialog size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minWidth" /> or <paramref name="minHeight" /> is less than or equal to zero,
    /// or when <paramref name="widthFactor" /> or <paramref name="heightFactor" /> is outside the (0, 1] range.
    /// </exception>
    public static Size ResolveResponsiveSize(
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

    /// <summary>
    /// Applies responsive dimensions to a dialog while preserving the default centered placement behavior.
    /// </summary>
    /// <param name="dialog">The dialog to update.</param>
    /// <param name="bounds">The available dialog bounds, or <see langword="null" /> to use the minimum dimensions.</param>
    /// <param name="minWidth">The minimum dialog width.</param>
    /// <param name="minHeight">The minimum dialog height.</param>
    /// <param name="widthFactor">The fraction of the available width to use, in the (0, 1] range.</param>
    /// <param name="heightFactor">The fraction of the available height to use, in the (0, 1] range.</param>
    /// <returns>The updated <paramref name="dialog" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dialog" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minWidth" /> or <paramref name="minHeight" /> is less than or equal to zero,
    /// or when <paramref name="widthFactor" /> or <paramref name="heightFactor" /> is outside the (0, 1] range.
    /// </exception>
    public static Dialog ApplyResponsiveSize(
        Dialog dialog,
        Rectangle? bounds,
        int minWidth,
        int minHeight,
        double widthFactor = 0.8,
        double heightFactor = 0.8)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var size = ResolveResponsiveSize(bounds, minWidth, minHeight, widthFactor, heightFactor);
        dialog.MinWidth = minWidth;
        dialog.MinHeight = minHeight;
        return dialog
            .Width(size.Width)
            .Height(size.Height);
    }

    /// <summary>
    /// Applies responsive dimensions to a dialog from bounds resolved at layout time while preserving the default centered placement behavior.
    /// </summary>
    /// <param name="dialog">The dialog to update.</param>
    /// <param name="getBounds">A delegate that returns the available dialog bounds, or <see langword="null" /> to use the dialog's application bounds.</param>
    /// <param name="minWidth">The minimum dialog width.</param>
    /// <param name="minHeight">The minimum dialog height.</param>
    /// <param name="widthFactor">The fraction of the available width to use, in the (0, 1] range.</param>
    /// <param name="heightFactor">The fraction of the available height to use, in the (0, 1] range.</param>
    /// <returns>The updated <paramref name="dialog" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dialog" /> or <paramref name="getBounds" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minWidth" /> or <paramref name="minHeight" /> is less than or equal to zero,
    /// or when <paramref name="widthFactor" /> or <paramref name="heightFactor" /> is outside the (0, 1] range.
    /// </exception>
    public static Dialog ApplyResponsiveSize(
        Dialog dialog,
        Func<Rectangle?> getBounds,
        int minWidth,
        int minHeight,
        double widthFactor = 0.8,
        double heightFactor = 0.8)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(getBounds);

        _ = ResolveResponsiveSize(bounds: null, minWidth, minHeight, widthFactor, heightFactor);
        dialog.MinWidth = minWidth;
        dialog.MinHeight = minHeight;
        return dialog
            .Width(() => ResolveResponsiveSize(ResolveBounds(getBounds, dialog), minWidth, minHeight, widthFactor, heightFactor).Width)
            .Height(() => ResolveResponsiveSize(ResolveBounds(getBounds, dialog), minWidth, minHeight, widthFactor, heightFactor).Height);
    }

    private static Rectangle? ResolveBounds(Func<Rectangle?> getBounds, Dialog dialog)
        => getBounds() ?? dialog.App?.Root.GetAbsoluteBounds() ?? dialog.Parent?.GetAbsoluteBounds();
}
