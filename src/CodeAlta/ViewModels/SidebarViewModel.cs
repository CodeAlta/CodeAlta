using CodeAlta.Catalog;
using CodeAlta.Presentation.Sidebar;
using XenoAtom.Terminal.UI;

namespace CodeAlta.ViewModels;

public sealed partial class SidebarViewModel
{
    public SidebarViewModel()
    {
        SortMode = NavigatorProjectSortMode.Name;
    }

    [Bindable]
    public partial NavigatorProjectSortMode SortMode { get; set; }

    internal SidebarTreeProjection? Projection { get; set; }
}
