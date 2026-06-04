using CodeAlta.App;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Views;

internal sealed class ProviderDialogCoordinator
{
    private readonly IModelProviderDialogService _modelProviders;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private ModelProvidersDialog? _dialog;

    public ProviderDialogCoordinator(
        ProviderFrontendCoordinator providerUi,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(providerUi);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _modelProviders = new ModelProviderDialogService(providerUi);
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;
    }

    public Task OpenAsync()
    {
        (_dialog ??= new ModelProvidersDialog(
            _modelProviders,
            _getBounds,
            _getFocusTarget)).Show();
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_dialog?.IsOpen == true)
        {
            _dialog.Refresh();
            return;
        }

        await _modelProviders.RefreshConfigurationAsync(cancellationToken);
    }
}
