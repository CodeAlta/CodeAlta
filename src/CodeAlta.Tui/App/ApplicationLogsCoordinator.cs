using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.App;

internal sealed class ApplicationLogsCoordinator
{
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;

    public ApplicationLogsCoordinator(
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;
    }

    public void Open()
        => new ApplicationLogsDialog(
            CodeAltaLogging.GetUiLogBuffer(),
            _getBounds,
            _getFocusTarget)
            .Show();
}
