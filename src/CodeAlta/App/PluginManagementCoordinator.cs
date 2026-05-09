using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal sealed class PluginManagementCoordinator
{
    private readonly PluginManagementService _service;
    private readonly Func<string, CancellationToken, Task> _openFileAsync;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;

    public PluginManagementCoordinator(
        PluginManagementService service,
        Func<string, CancellationToken, Task> openFileAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(openFileAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        _service = service;
        _openFileAsync = openFileAsync;
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;
    }

    public void Open()
        => new PluginManagementDialog(_service, _openFileAsync, _getBounds, _getFocusTarget).Show();
}
