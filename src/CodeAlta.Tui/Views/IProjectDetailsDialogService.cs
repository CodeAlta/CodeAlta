using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.Views;

internal interface IProjectDetailsDialogService
{
    Rectangle? GetDialogBounds();

    Visual? GetDialogFocusTarget();

    Task SaveProjectAsync(ProjectDescriptor project);
}
