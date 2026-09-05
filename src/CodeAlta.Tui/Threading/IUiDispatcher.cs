namespace CodeAlta.Tui.Threading;

internal interface IUiDispatcher
{
    bool CheckAccess();

    void VerifyAccess()
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException("The current operation must run on the frontend UI dispatcher.");
        }
    }

    void Post(Action action);

    void PostDeferred(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Post(action);
    }

    void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (CheckAccess())
        {
            action();
            return;
        }

        InvokeAsync(action).GetAwaiter().GetResult();
    }

    T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return CheckAccess()
            ? action()
            : InvokeAsync(action).GetAwaiter().GetResult();
    }

    Task InvokeAsync(Action action);

    Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var task = InvokeAsync(action);
        return cancellationToken.CanBeCanceled
            ? task.WaitAsync(cancellationToken)
            : task;
    }

    Task<T> InvokeAsync<T>(Func<T> action);

    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var task = InvokeAsync(action);
        return cancellationToken.CanBeCanceled
            ? task.WaitAsync(cancellationToken)
            : task;
    }
}
