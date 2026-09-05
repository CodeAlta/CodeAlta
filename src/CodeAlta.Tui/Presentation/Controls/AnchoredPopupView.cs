using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tui.Presentation.Controls;

internal sealed class AnchoredPopupView
{
    private readonly Func<Visual> _buildContent;
    private readonly Action? _onClosed;

    public AnchoredPopupView(Func<Visual> buildContent, Action? onClosed = null)
    {
        ArgumentNullException.ThrowIfNull(buildContent);
        _buildContent = buildContent;
        _onClosed = onClosed;

        Popup = new Popup
        {
            MatchAnchorWidth = false,
            CloseOnTab = false,
        };
        Popup.Closed((_, _) =>
        {
            IsOpen = false;
            _onClosed?.Invoke();
        });
    }

    public Popup Popup { get; }

    public bool IsOpen { get; private set; }

    public void Show(Visual anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        Popup.Anchor = anchor;
        Popup.Placement = PopupPlacement.Above;
        Popup.OffsetY = 0;
        Popup.Content = _buildContent();
        Popup.Show();
        IsOpen = true;
    }

    public void Close()
    {
        Popup.Close();
        IsOpen = false;
    }
}
