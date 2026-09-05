using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace CodeAlta.Tui.Views;

internal sealed class AppStartupHook : Visual
{
    private readonly Action _onAttached;
    private bool _started;

    public AppStartupHook(Action onAttached)
    {
        ArgumentNullException.ThrowIfNull(onAttached);

        _onAttached = onAttached;
        IsVisible = false;
        IsEnabled = false;
    }

    protected override void OnAttachedToApp(TerminalApp app)
    {
        base.OnAttachedToApp(app);

        if (_started)
        {
            return;
        }

        _started = true;
        _onAttached();
    }

    protected override SizeHints MeasureCore(in LayoutConstraints constraints)
        => SizeHints.Fixed(constraints.Clamp(new Size(0, 0)));

    protected override void ArrangeCore(in Rectangle finalRect)
    {
        _ = finalRect;
    }

    protected override void RenderOverride(CellBuffer buffer)
    {
        _ = buffer;
    }
}
