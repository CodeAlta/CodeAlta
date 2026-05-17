using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal sealed class NavigatorSettingsCoordinator : INavigatorSettingsDialogService
{
    private readonly ShellThreadStateCoordinator _threadStateCoordinator;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Action _refreshCatalogAndThreadWorkspace;
    private readonly Action<string, bool, StatusTone> _setStatus;

    public NavigatorSettingsCoordinator(
        ShellThreadStateCoordinator threadStateCoordinator,
        Func<Rectangle?> getDialogBounds,
        Func<Visual?> getFocusTarget,
        Action refreshCatalogAndThreadWorkspace,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(threadStateCoordinator);
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(setStatus);

        _threadStateCoordinator = threadStateCoordinator;
        _getDialogBounds = getDialogBounds;
        _getFocusTarget = getFocusTarget;
        _refreshCatalogAndThreadWorkspace = refreshCatalogAndThreadWorkspace;
        _setStatus = setStatus;
    }

    public void Open()
    {
        new NavigatorSettingsDialog(
            _threadStateCoordinator.GetNavigatorSettingsSnapshot(),
            this)
            .Show();
    }

    private async Task SaveAsync(NavigatorSettings settings)
    {
        try
        {
            await _threadStateCoordinator.SaveNavigatorSettingsAsync(settings);
            _refreshCatalogAndThreadWorkspace();
        }
        catch (Exception ex)
        {
            _setStatus($"Failed to save workspace settings: {ex.Message}", false, StatusTone.Error);
        }
    }

    Rectangle? INavigatorSettingsDialogService.GetDialogBounds()
        => _getDialogBounds();

    Visual? INavigatorSettingsDialogService.GetDialogFocusTarget()
        => _getFocusTarget();

    void INavigatorSettingsDialogService.PreviewNavigatorTheme(string? themeSchemeName)
        => _threadStateCoordinator.PreviewNavigatorTheme(themeSchemeName);

    void INavigatorSettingsDialogService.ClearNavigatorThemePreview()
        => _threadStateCoordinator.ClearNavigatorThemePreview();

    Task INavigatorSettingsDialogService.SaveNavigatorSettingsAsync(NavigatorSettings settings)
        => SaveAsync(settings);
}
