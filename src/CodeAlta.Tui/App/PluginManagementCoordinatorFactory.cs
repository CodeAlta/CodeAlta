using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.App;

internal static class PluginManagementCoordinatorFactory
{
    public static Func<Task> Create(
        CatalogOptions catalogOptions,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<Visual?> getDialogAnchor,
        Func<string, CancellationToken, Task> openFileAsync,
        Func<IReadOnlyList<CodeAlta.Plugins.PluginRuntimeDiagnostic>>? getRuntimeDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getDialogAnchor);
        ArgumentNullException.ThrowIfNull(openFileAsync);
        var coordinator = new PluginManagementCoordinator(
            new PluginManagementService(catalogOptions, getSelectedProject, getRuntimeDiagnostics),
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
