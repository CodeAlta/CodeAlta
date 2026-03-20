using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.App;

internal sealed class RuntimeEventPump : IAsyncDisposable
{
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CodeAltaShellController _shellController;
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    public RuntimeEventPump(
        WorkThreadRuntimeService runtimeService,
        CodeAltaShellController shellController)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(shellController);

        _runtimeService = runtimeService;
        _shellController = shellController;
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (_pumpTask is not null)
        {
            return;
        }

        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        _pumpTask = Task.Run(
            () => RunAsync(_pumpCts.Token),
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _pumpCts?.Cancel();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _pumpCts?.Dispose();
        _disposeCts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var runtimeEvent in _runtimeService.StreamEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                await _shellController.ApplyRuntimeEventAsync(runtimeEvent, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}