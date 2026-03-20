using XenoAtom.Terminal.UI.Threading;

namespace CodeAlta.App;

internal static class UiDispatch
{
    public static void Post(IUiDispatcher dispatcher, Action action, bool allowInline = false)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        if (allowInline || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Post(action);
    }

    public static T Invoke<T>(IUiDispatcher dispatcher, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        return dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
    }

    public static T Invoke<TState, T>(IUiDispatcher dispatcher, Func<TState, T> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        return dispatcher.CheckAccess()
            ? action(state)
            : dispatcher.InvokeAsync(() => action(state)).GetAwaiter().GetResult();
    }

    public static void PostCurrent(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher.Current;
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Post(action);
    }

    public static void PostCurrentDeferred(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Dispatcher.Current.Post(action);
    }

    public static T InvokeCurrent<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher.Current;
        return dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
    }

    public static T InvokeCurrent<TState, T>(Func<TState, T> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Dispatcher.Current;
        return dispatcher.CheckAccess()
            ? action(state)
            : dispatcher.InvokeAsync(() => action(state)).GetAwaiter().GetResult();
    }
}