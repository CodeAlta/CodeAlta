using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal static class SidebarServicesFactory
{
    public static (NavigatorActionCoordinator NavigatorActions, SidebarCoordinator Sidebar) Create(
        SidebarViewModel viewModel,
        CatalogOptions catalogOptions,
        CodeAltaShellController shellController,
        ShellThreadStateCoordinator threadStateCoordinator,
        Action refreshCatalogAndThreadWorkspace,
        Action<string, bool, StatusTone> setStatus,
        Action setReadyStatusForCurrentSelection)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(shellController);
        ArgumentNullException.ThrowIfNull(threadStateCoordinator);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(setReadyStatusForCurrentSelection);

        SidebarCoordinator? sidebar = null;
        var navigatorActions = new NavigatorActionCoordinator(
            shellController,
            threadStateCoordinator,
            () => GetSidebarDialogBounds(sidebar),
            () => GetSidebarFocusTarget(sidebar),
            setStatus,
            setReadyStatusForCurrentSelection);
        var navigatorSettings = new NavigatorSettingsCoordinator(
            threadStateCoordinator,
            () => GetSidebarDialogBounds(sidebar),
            () => GetSidebarFocusTarget(sidebar),
            refreshCatalogAndThreadWorkspace,
            setStatus);
        sidebar = new SidebarCoordinator(
            viewModel,
            catalogOptions,
            shellController,
            () => _ = ToggleSortModeAsync(threadStateCoordinator, refreshCatalogAndThreadWorkspace),
            navigatorSettings.Open,
            navigatorActions.RenameProjectDisplayNameAsync,
            navigatorActions.ConfirmDeleteThread,
            navigatorActions.ConfirmDeleteProject,
            navigatorActions.OpenProjectThreads,
            navigatorActions.OpenProjectDetails,
            navigatorActions.OpenFolder);
        return (navigatorActions, sidebar);
    }

    private static async Task ToggleSortModeAsync(
        ShellThreadStateCoordinator threadStateCoordinator,
        Action refreshCatalogAndThreadWorkspace)
    {
        var settings = threadStateCoordinator.GetNavigatorSettingsSnapshot();
        settings.SortMode = settings.SortMode == NavigatorProjectSortMode.Name
            ? NavigatorProjectSortMode.Date
            : NavigatorProjectSortMode.Name;
        await threadStateCoordinator.SaveNavigatorSettingsAsync(settings);
        refreshCatalogAndThreadWorkspace();
    }

    private static Rectangle? GetSidebarDialogBounds(SidebarCoordinator? sidebar)
        => DialogBoundsResolver.ResolveAppBounds(GetSidebarFocusTarget(sidebar));

    private static Visual? GetSidebarFocusTarget(SidebarCoordinator? sidebar)
        => sidebar?.View.Tree;
}
