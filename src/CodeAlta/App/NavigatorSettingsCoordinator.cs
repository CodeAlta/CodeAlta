using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal sealed class NavigatorSettingsCoordinator : INavigatorSettingsDialogService
{
    private readonly ShellSessionStateCoordinator _sessionStateCoordinator;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Action _refreshCatalogAndSessionWorkspace;
    private readonly Action<string, bool, StatusTone> _setStatus;

    public NavigatorSettingsCoordinator(
        ShellSessionStateCoordinator sessionStateCoordinator,
        Func<Rectangle?> getDialogBounds,
        Func<Visual?> getFocusTarget,
        Action refreshCatalogAndSessionWorkspace,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(sessionStateCoordinator);
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndSessionWorkspace);
        ArgumentNullException.ThrowIfNull(setStatus);

        _sessionStateCoordinator = sessionStateCoordinator;
        _getDialogBounds = getDialogBounds;
        _getFocusTarget = getFocusTarget;
        _refreshCatalogAndSessionWorkspace = refreshCatalogAndSessionWorkspace;
        _setStatus = setStatus;
    }

    public void Open()
    {
        new NavigatorSettingsDialog(
            _sessionStateCoordinator.GetNavigatorSettingsSnapshot(),
            this)
            .Show();
    }

    private async Task SaveAsync(NavigatorSettings settings)
    {
        try
        {
            await _sessionStateCoordinator.SaveNavigatorSettingsAsync(settings);
            _refreshCatalogAndSessionWorkspace();
        }
        catch (Exception ex)
        {
            _setStatus(SR.T("Failed to save workspace settings: {0}", ex.Message), false, StatusTone.Error);
        }
    }

    Rectangle? INavigatorSettingsDialogService.GetDialogBounds()
        => _getDialogBounds();

    Visual? INavigatorSettingsDialogService.GetDialogFocusTarget()
        => _getFocusTarget();

    void INavigatorSettingsDialogService.PreviewNavigatorTheme(string? themeSchemeName)
        => _sessionStateCoordinator.PreviewNavigatorTheme(themeSchemeName);

    void INavigatorSettingsDialogService.ClearNavigatorThemePreview()
        => _sessionStateCoordinator.ClearNavigatorThemePreview();

    Task INavigatorSettingsDialogService.SaveNavigatorSettingsAsync(NavigatorSettings settings)
        => SaveAsync(settings);
}
