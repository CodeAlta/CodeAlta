using System.Text;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;

namespace CodeAlta.Tui.Presentation.Controls;

internal sealed class AnchoredOverlayView : ContentVisual
{
    private Rectangle? _anchorRect;
    private Rectangle _popupRect;

    public AnchoredOverlayView()
    {
        HorizontalAlignment = Align.Stretch;
        VerticalAlignment = Align.Stretch;
        IsHitTestVisible = false;
        IsVisible = false;
    }

    public PopupPlacement Placement { get; set; } = PopupPlacement.Below;

    public int OffsetX { get; set; }

    public int OffsetY { get; set; }

    public bool IsOpen { get; private set; }

    public void Show(Visual content, Rectangle anchorRect)
    {
        ArgumentNullException.ThrowIfNull(content);

        Content = content;
        _anchorRect = anchorRect;
        IsOpen = true;
        IsVisible = true;
        IsHitTestVisible = true;
    }

    public void UpdateAnchor(Rectangle anchorRect)
    {
        if (!IsOpen)
        {
            return;
        }

        _anchorRect = anchorRect;
    }

    public void Close()
    {
        IsOpen = false;
        IsVisible = false;
        IsHitTestVisible = false;
        _anchorRect = null;
        _popupRect = default;
        Content = null;
    }

    protected override SizeHints MeasureCore(in LayoutConstraints constraints)
    {
        if (IsOpen && Content is { } content)
        {
            var maxWidth = constraints.MaxWidth;
            var maxHeight = !constraints.IsHeightBounded
                ? int.MaxValue
                : constraints.MaxHeight;
            content.Measure(new LayoutConstraints(0, maxWidth, 0, maxHeight));
        }

        return SizeHints.Flex(
            min: Size.Zero,
            natural: Size.Zero,
            max: new Size(int.MaxValue, int.MaxValue),
            growX: 1,
            growY: 1,
            shrinkX: 1,
            shrinkY: 1);
    }

    protected override void ArrangeCore(in Rectangle finalRect)
    {
        if (!IsOpen || Content is not { } content || _anchorRect is not Rectangle anchorRect)
        {
            _popupRect = default;
            return;
        }

        var slot = finalRect;
        var desired = content.DesiredSize;
        var overlayAbsoluteBounds = GetAbsoluteBounds();
        var anchorBounds = new Rectangle(
            anchorRect.X - overlayAbsoluteBounds.X,
            anchorRect.Y - overlayAbsoluteBounds.Y,
            anchorRect.Width,
            anchorRect.Height);

        var desiredWidth = Math.Clamp(desired.Width, 1, slot.Width);
        var desiredHeight = Math.Clamp(desired.Height, 1, slot.Height);

        var x = slot.X + Math.Max(0, (slot.Width - desiredWidth) / 2);
        var y = slot.Y + Math.Max(0, (slot.Height - desiredHeight) / 2);

        var belowY = anchorBounds.Y + anchorBounds.Height;
        var aboveY = anchorBounds.Y - desiredHeight;
        var rightX = anchorBounds.X + anchorBounds.Width;
        var leftX = anchorBounds.X - desiredWidth;

        switch (Placement)
        {
            case PopupPlacement.Above:
                x = anchorBounds.X;
                y = aboveY;
                if (y < slot.Y && belowY + desiredHeight <= slot.Bottom)
                {
                    y = belowY;
                }

                break;

            case PopupPlacement.Right:
                x = rightX;
                y = anchorBounds.Y;
                if (x + desiredWidth > slot.Right && leftX >= slot.X)
                {
                    x = leftX;
                }

                break;

            case PopupPlacement.Left:
                x = leftX;
                y = anchorBounds.Y;
                if (x < slot.X && rightX + desiredWidth <= slot.Right)
                {
                    x = rightX;
                }

                break;

            case PopupPlacement.Below:
            default:
                x = anchorBounds.X;
                y = belowY;
                if (y + desiredHeight > slot.Bottom && aboveY >= slot.Y)
                {
                    y = aboveY;
                }

                break;
        }

        x += OffsetX;
        y += OffsetY;

        x = Math.Clamp(x, slot.X, Math.Max(slot.X, slot.Right - desiredWidth));
        y = Math.Clamp(y, slot.Y, Math.Max(slot.Y, slot.Bottom - desiredHeight));

        _popupRect = new Rectangle(x, y, desiredWidth, desiredHeight);
        content.Arrange(_popupRect);
    }
}
