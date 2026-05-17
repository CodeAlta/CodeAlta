using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;

namespace CodeAlta.ViewModels;

public sealed partial class NavigatorSettingsDialogViewModel
{
    public NavigatorSettingsDialogViewModel()
    {
        SortMode = NavigatorProjectSortMode.Name;
        RecentThreadsPerProject = 3;
        ThemeSchemeName = string.Empty;
    }

    [Bindable]
    public partial NavigatorProjectSortMode SortMode { get; set; }

    [Bindable]
    public partial int RecentThreadsPerProject { get; set; }

    [Bindable]
    public partial string ThemeSchemeName { get; set; }
}
