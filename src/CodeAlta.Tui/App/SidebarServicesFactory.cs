using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.App;

internal static class SidebarServicesFactory
{
    public static (NavigatorActionCoordinator NavigatorActions, SidebarCoordinator Sidebar) Create(
        SidebarViewModel viewModel,
        CatalogOptions catalogOptions,
        CodeAltaShellController shellController,
        ShellSessionStateCoordinator sessionStateCoordinator,
        IAltaNotesService notesService,
        Func<string?, string> resolveProviderDisplayName,
        Func<Visual?> getPromptFocusTarget,
        Action refreshCatalogAndSessionWorkspace,
        Action<string, bool, StatusTone> setStatus,
        Action setReadyStatusForCurrentSelection)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(shellController);
        ArgumentNullException.ThrowIfNull(sessionStateCoordinator);
        ArgumentNullException.ThrowIfNull(notesService);
        ArgumentNullException.ThrowIfNull(resolveProviderDisplayName);
        ArgumentNullException.ThrowIfNull(getPromptFocusTarget);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndSessionWorkspace);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(setReadyStatusForCurrentSelection);

        SidebarCoordinator? sidebar = null;
        var navigatorActions = new NavigatorActionCoordinator(
            shellController,
            sessionStateCoordinator,
            resolveProviderDisplayName,
            () => GetSidebarDialogBounds(sidebar),
            () => GetSidebarFocusTarget(sidebar),
            getPromptFocusTarget,
            setStatus,
            setReadyStatusForCurrentSelection);
        var navigatorSettings = new NavigatorSettingsCoordinator(
            sessionStateCoordinator,
            () => GetSidebarDialogBounds(sidebar),
            () => GetSidebarFocusTarget(sidebar),
            refreshCatalogAndSessionWorkspace,
            setStatus);
        var applicationLogs = new ApplicationLogsCoordinator(
            () => GetSidebarDialogBounds(sidebar),
            () => GetSidebarFocusTarget(sidebar));
        var persistenceFeedback = new ViewStatePersistenceFeedback(setStatus);
        sidebar = new SidebarCoordinator(
            viewModel,
            catalogOptions,
            shellController,
            () => _ = ToggleSortModeAsync(sessionStateCoordinator, refreshCatalogAndSessionWorkspace, persistenceFeedback),
            navigatorSettings.Open,
            navigatorActions.RenameProjectDisplayNameAsync,
            new SidebarRowCommandDispatcher(navigatorActions),
            applicationLogs.Open,
            notesService);
        return (navigatorActions, sidebar);
    }

    internal static async Task ToggleSortModeAsync(
        ShellSessionStateCoordinator sessionStateCoordinator,
        Action refreshCatalogAndSessionWorkspace,
        ViewStatePersistenceFeedback persistenceFeedback)
    {
        var settings = sessionStateCoordinator.GetNavigatorSettingsSnapshot();
        settings.SortMode = settings.SortMode == NavigatorProjectSortMode.Name
            ? NavigatorProjectSortMode.Date
            : NavigatorProjectSortMode.Name;
        var result = await sessionStateCoordinator.SaveNavigatorSettingsAsync(settings);
        if (persistenceFeedback.Report(result))
        {
            refreshCatalogAndSessionWorkspace();
        }
    }

    private static Rectangle? GetSidebarDialogBounds(SidebarCoordinator? sidebar)
        => DialogBoundsResolver.ResolveAppBounds(GetSidebarFocusTarget(sidebar));

    private static Visual? GetSidebarFocusTarget(SidebarCoordinator? sidebar)
        => sidebar?.View.Tree;
}
