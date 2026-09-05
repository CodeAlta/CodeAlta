using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tui.Views;

internal interface IDirectoryPathDialogService
{
    Rectangle? GetDialogBounds();

    Visual? GetDialogFocusTarget();

    Visual? GetSubmitFocusTarget();

    IEnumerable<ProjectDescriptor>? GetProjects();

    Task OpenFolderAsync(string path, bool trustFolder);
}

internal sealed class DirectoryPathDialogService : IDirectoryPathDialogService
{
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Func<Visual?> _getDialogFocusTarget;
    private readonly Func<Visual?> _getSubmitFocusTarget;
    private readonly Func<IEnumerable<ProjectDescriptor>>? _getProjects;
    private readonly Func<string, bool, Task> _openFolderAsync;

    public DirectoryPathDialogService(
        Func<Rectangle?> getDialogBounds,
        Func<Visual?> getDialogFocusTarget,
        Func<string, bool, Task> openFolderAsync,
        Func<Visual?>? getSubmitFocusTarget = null,
        Func<IEnumerable<ProjectDescriptor>>? getProjects = null)
    {
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(getDialogFocusTarget);
        ArgumentNullException.ThrowIfNull(openFolderAsync);

        _getDialogBounds = getDialogBounds;
        _getDialogFocusTarget = getDialogFocusTarget;
        _getSubmitFocusTarget = getSubmitFocusTarget ?? getDialogFocusTarget;
        _getProjects = getProjects;
        _openFolderAsync = openFolderAsync;
    }

    public Rectangle? GetDialogBounds() => _getDialogBounds();

    public Visual? GetDialogFocusTarget() => _getDialogFocusTarget();

    public Visual? GetSubmitFocusTarget() => _getSubmitFocusTarget();

    public IEnumerable<ProjectDescriptor>? GetProjects() => _getProjects?.Invoke();

    public Task OpenFolderAsync(string path, bool trustFolder) => _openFolderAsync(path, trustFolder);
}
