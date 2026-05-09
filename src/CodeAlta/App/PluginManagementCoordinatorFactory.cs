using CodeAlta.Catalog;
using CodeAlta.Models;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App;

internal static class PluginManagementCoordinatorFactory
{
    public static Func<Task> Create(
        CatalogOptions catalogOptions,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<Visual?> getDialogAnchor,
        Func<string, CancellationToken, Task> openFileAsync)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getDialogAnchor);
        ArgumentNullException.ThrowIfNull(openFileAsync);
        var coordinator = new PluginManagementCoordinator(
            new PluginManagementService(catalogOptions, getSelectedProject),
            openFileAsync,
            () => DialogBoundsResolver.ResolveAppBounds(getDialogAnchor()),
            getDialogAnchor);
        return () =>
        {
            coordinator.Open();
            return Task.CompletedTask;
        };
    }
}
