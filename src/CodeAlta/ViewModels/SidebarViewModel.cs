using CodeAlta.Presentation.Sidebar;
using XenoAtom.Terminal.UI;

namespace CodeAlta.ViewModels;

public sealed partial class SidebarViewModel
{
    public SidebarViewModel()
    {
        DraftThreadTitle = string.Empty;
    }

    [Bindable]
    public partial string? DraftThreadTitle { get; set; }

    internal SidebarTreeProjection? Projection { get; set; }
}
