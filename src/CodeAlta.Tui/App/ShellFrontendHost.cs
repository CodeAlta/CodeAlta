using System.Runtime.ExceptionServices;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.App;

internal interface IShellFrontendHostLifecycle
{
    void PrepareForRun();

    Visual GetRoot();

    TerminalLoopResult Tick(CancellationToken cancellationToken);

    ValueTask DisposeFrontendAsync();

    IAsyncDisposable? OwnedServices { get; }
}

internal sealed class ShellFrontendHost : IAsyncDisposable
{
    private readonly IShellFrontendHostLifecycle _lifecycle;

    public ShellFrontendHost(IShellFrontendHostLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        _lifecycle = lifecycle;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _lifecycle.PrepareForRun();
        var root = _lifecycle.GetRoot();
        await Terminal.RunAsync(
            root,
            () => Tick(cancellationToken),
            cancellationToken);
    }

    public TerminalLoopResult Tick(CancellationToken cancellationToken)
        => _lifecycle.Tick(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        Exception? frontendFailure = null;
        try
        {
            await _lifecycle.DisposeFrontendAsync();
        }
        catch (Exception ex)
        {
            frontendFailure = ex;
        }

        // A failed draft acknowledgement must not abandon runtime/provider/plugin ownership.
        try
        {
            if (_lifecycle.OwnedServices is { } ownedServices)
            {
                await ownedServices.DisposeAsync();
            }
        }
        catch (Exception ex) when (frontendFailure is not null)
        {
            throw new AggregateException(frontendFailure, ex);
        }

        if (frontendFailure is not null)
        {
            ExceptionDispatchInfo.Throw(frontendFailure);
        }
    }
}
