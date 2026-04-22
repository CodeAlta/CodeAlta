using CodeAlta.App;
using CodeAlta.Models;

namespace CodeAlta.Views;

internal sealed class InitialCatalogStateCoordinator
{
    private readonly Func<CancellationToken, Task<ShellThreadStateCoordinator.InitialCatalogState>> _loadInitialCatalogStateAsync;
    private readonly Action<ShellThreadStateCoordinator.InitialCatalogState> _applyInitialCatalogState;
    private readonly Action _refreshCatalogAndThreadWorkspace;
    private readonly Action _focusPromptEditor;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private Task<ShellThreadStateCoordinator.InitialCatalogState>? _initialCatalogStateTask;
    private bool _initialCatalogStateResolved;

    public InitialCatalogStateCoordinator(
        Func<CancellationToken, Task<ShellThreadStateCoordinator.InitialCatalogState>> loadInitialCatalogStateAsync,
        Action<ShellThreadStateCoordinator.InitialCatalogState> applyInitialCatalogState,
        Action refreshCatalogAndThreadWorkspace,
        Action focusPromptEditor,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(loadInitialCatalogStateAsync);
        ArgumentNullException.ThrowIfNull(applyInitialCatalogState);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(focusPromptEditor);
        ArgumentNullException.ThrowIfNull(setStatus);

        _loadInitialCatalogStateAsync = loadInitialCatalogStateAsync;
        _applyInitialCatalogState = applyInitialCatalogState;
        _refreshCatalogAndThreadWorkspace = refreshCatalogAndThreadWorkspace;
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
            _refreshCatalogAndThreadWorkspace();
            _focusPromptEditor();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _setStatus($"Failed to load saved state: {ex.Message}", false, StatusTone.Error);
        }

        _initialCatalogStateResolved = true;
        return true;
    }
}
