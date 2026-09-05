using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Views;

internal sealed class PromptDialogCoordinator
{
    private readonly CatalogOptions _catalogOptions;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Action _onPromptsChanged;
    private readonly Action<string, StatusTone> _setStatus;

    public PromptDialogCoordinator(
        CatalogOptions catalogOptions,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<Visual?> getFocusTarget,
        Action onPromptsChanged,
        Action<string, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentNullException.ThrowIfNull(onPromptsChanged);
        ArgumentNullException.ThrowIfNull(setStatus);

        _catalogOptions = catalogOptions;
        _getSelectedProject = getSelectedProject;
        _getFocusTarget = getFocusTarget;
        _onPromptsChanged = onPromptsChanged;
        _setStatus = setStatus;
    }

    public Task OpenAsync()
    {
        new PromptManagementDialog(
            _catalogOptions,
            _getSelectedProject,
            () => DialogBoundsResolver.ResolveAppBounds(_getFocusTarget()),
            _getFocusTarget,
            _onPromptsChanged,
            _setStatus)
            .Show();
        return Task.CompletedTask;
    }
}
