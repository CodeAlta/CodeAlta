using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.App;

internal interface IShellFrontendHostLifecycle
{
    void PrepareForRun();

    Visual GetRoot();

    TerminalLoopResult Tick(CancellationToken cancellationToken);

    ValueTask DisposeFrontendAsync();
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
        => await _lifecycle.DisposeFrontendAsync();
}
