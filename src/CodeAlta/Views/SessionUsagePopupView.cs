using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Views;

internal sealed class SessionUsagePopupView
{
    private readonly Func<Visual> _buildContent;

    public SessionUsagePopupView(Func<Visual> buildContent)
    {
        ArgumentNullException.ThrowIfNull(buildContent);
        _buildContent = buildContent;

        Popup = new Popup
        {
            MatchAnchorWidth = false,
            CloseOnTab = false,
        };
        Popup.Closed((_, _) => IsOpen = false);
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