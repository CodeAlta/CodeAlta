using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal sealed class SkillsManagementCoordinator
{
    private readonly SkillsManagementService _service;
    private readonly Func<string, CancellationToken, Task> _openFileAsync;
    private readonly Func<string, CancellationToken, Task> _activateSkillAsync;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;

    public SkillsManagementCoordinator(
        SkillsManagementService service,
        Func<string, CancellationToken, Task> openFileAsync,
        Func<string, CancellationToken, Task> activateSkillAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(openFileAsync);
        ArgumentNullException.ThrowIfNull(activateSkillAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _service = service;
        _openFileAsync = openFileAsync;
        _activateSkillAsync = activateSkillAsync;
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;
    }

    public void Open()
        => new SkillsManagementDialog(_service, _openFileAsync, _activateSkillAsync, _getBounds, _getFocusTarget).Show();
}
