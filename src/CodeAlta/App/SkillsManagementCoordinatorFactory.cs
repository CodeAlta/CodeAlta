using CodeAlta.Catalog;
using CodeAlta.Models;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App;

internal static class SkillsManagementCoordinatorFactory
{
    public static Func<Task> Create(CodeAltaOwnedServices? ownedServices, CatalogOptions catalogOptions, Func<ProjectDescriptor?> getSelectedProject, Func<Visual?> getDialogAnchor, Func<string, CancellationToken, Task> openFileAsync, Func<string, CancellationToken, Task> activateSkillAsync, Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getDialogAnchor);
        ArgumentNullException.ThrowIfNull(openFileAsync);
        ArgumentNullException.ThrowIfNull(activateSkillAsync);
        ArgumentNullException.ThrowIfNull(setStatus);
        if (ownedServices is null)
        {
            return () =>
            {
                setStatus("Skills management is unavailable in this app instance.", false, StatusTone.Warning);
                return Task.CompletedTask;
            };
        }

        var coordinator = new SkillsManagementCoordinator(
            new SkillsManagementService(ownedServices.SkillCatalog, catalogOptions, getSelectedProject),
            openFileAsync,
            activateSkillAsync,
            () => DialogBoundsResolver.ResolveAppBounds(getDialogAnchor()),
            getDialogAnchor);
        return () =>
        {
            coordinator.Open();
            return Task.CompletedTask;
        };
    }
}
