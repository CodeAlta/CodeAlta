using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Views;

internal interface INavigatorSettingsDialogService
{
    Rectangle? GetDialogBounds();

    Visual? GetDialogFocusTarget();

    void PreviewNavigatorTheme(string? themeSchemeName);

    void ClearNavigatorThemePreview();

    Task SaveNavigatorSettingsAsync(NavigatorSettings settings);
}
