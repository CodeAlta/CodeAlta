using XenoAtom.Terminal.UI.Threading;

namespace CodeAlta.Tui.Threading;

internal static class UiDispatch
{
    public static void Invoke(IUiDispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
    }

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

    public static Task InvokeAsync(IUiDispatcher dispatcher, Func<Task> action, bool allowInline = false)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        return allowInline || dispatcher.CheckAccess()
            ? action()
            : dispatcher.InvokeAsync(action).Unwrap();
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

    /// <summary>
    /// Runs a function on the UI thread of <paramref name="dispatcher"/>, or here when there is none: a caller that builds
    /// visuals for no running application (a test, a tree built before the run) has no UI thread to go to.
    /// </summary>
    public static T InvokeOrInline<TState, T>(IUiDispatcher? dispatcher, Func<TState, T> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(action);

        return dispatcher is null ? action(state) : Invoke(dispatcher, action, state);
    }

    /// <summary>
    /// Runs an action on the thread of the application that owns <paramref name="owner"/>, or here when no running
    /// application owns it: an object nobody owns is used from any thread.
    /// </summary>
    public static void InvokeOnOwner<TState>(DispatcherObject owner, Action<TState> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(action);

        if (owner.CheckAccess())
        {
            action(state);
            return;
        }

        owner.Dispatcher.InvokeAsync(() => action(state)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Queues an action for a later turn of the loop of the application that runs on the calling thread. For the handlers
    /// of that application only: the dispatcher of another thread belongs to no application.
    /// </summary>
    public static void PostCurrentDeferred(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Dispatcher.Current.Post(action);
    }
}
