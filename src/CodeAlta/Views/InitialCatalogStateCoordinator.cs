using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Models;

namespace CodeAlta.Views;

internal sealed class InitialCatalogStateCoordinator
{
    private readonly Func<CancellationToken, Task<ShellSessionStateCoordinator.InitialCatalogState>> _loadInitialCatalogStateAsync;
    private readonly Action<ShellSessionStateCoordinator.InitialCatalogState> _applyInitialCatalogState;
    private readonly Action _publishStartupCatalogProjectionReady;
    private readonly Action _focusPromptEditor;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private Task<ShellSessionStateCoordinator.InitialCatalogState>? _initialCatalogStateTask;
    private bool _initialCatalogStateResolved;

    public InitialCatalogStateCoordinator(
        Func<CancellationToken, Task<ShellSessionStateCoordinator.InitialCatalogState>> loadInitialCatalogStateAsync,
        Action<ShellSessionStateCoordinator.InitialCatalogState> applyInitialCatalogState,
        Action publishStartupCatalogProjectionReady,
        Action focusPromptEditor,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(loadInitialCatalogStateAsync);
        ArgumentNullException.ThrowIfNull(applyInitialCatalogState);
        ArgumentNullException.ThrowIfNull(publishStartupCatalogProjectionReady);
        ArgumentNullException.ThrowIfNull(focusPromptEditor);
        ArgumentNullException.ThrowIfNull(setStatus);

        _loadInitialCatalogStateAsync = loadInitialCatalogStateAsync;
        _applyInitialCatalogState = applyInitialCatalogState;
        _publishStartupCatalogProjectionReady = publishStartupCatalogProjectionReady;
        _focusPromptEditor = focusPromptEditor;
        _setStatus = setStatus;
    }

    public void EnsureStarted(CancellationToken cancellationToken)
        => _initialCatalogStateTask ??= _loadInitialCatalogStateAsync(cancellationToken);

    public bool TryResolve(CancellationToken cancellationToken)
    {
        if (_initialCatalogStateResolved)
        {
            return true;
        }

        var task = _initialCatalogStateTask;
        if (task is null || !task.IsCompleted)
        {
            return false;
        }

        try
        {
            _applyInitialCatalogState(task.GetAwaiter().GetResult());
            _publishStartupCatalogProjectionReady();
            _focusPromptEditor();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _setStatus(SR.T("Failed to load saved state: {0}", ex.Message), false, StatusTone.Error);
        }

        _initialCatalogStateResolved = true;
        return true;
    }
}
