using XenoAtom.Terminal.UI.Threading;

namespace CodeAlta.App;

internal sealed class TerminalUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public TerminalUiDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public bool CheckAccess()
        => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.Post(action);
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcher.InvokeAsync(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcher.InvokeAsync(action);
    }
}