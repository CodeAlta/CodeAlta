namespace CodeAlta.Catalog;

/// <summary>
/// An asynchronous lock that is granted in the order it was asked for. A waiter that is canceled leaves the queue
/// and never gets the lock.
/// </summary>
internal sealed class AsyncFifoLock
{
    private readonly object _gate = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    private bool _held;

    public async ValueTask<Releaser> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (_gate)
        {
            if (!_held)
            {
                _held = true;
                return new Releaser(this);
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(waiter);
        }

        await using var registration = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (node.List is null)
                {
                    return; // The lock was handed over already: the caller releases it.
                }

                _waiters.Remove(node);
            }

            waiter.TrySetCanceled(cancellationToken);
        }).ConfigureAwait(false);
        await waiter.Task.ConfigureAwait(false);
        var releaser = new Releaser(this);
        if (cancellationToken.IsCancellationRequested)
        {
            // Canceled just as the lock was handed over: a canceled caller never runs under it.
            releaser.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return releaser;
    }

    private void Release()
    {
        TaskCompletionSource? next = null;
        lock (_gate)
        {
            if (_waiters.First is { } first)
            {
                _waiters.RemoveFirst();
                next = first.Value; // The lock stays held: it passes to the first waiter.
            }
            else
            {
                _held = false;
            }
        }

        next?.TrySetResult();
    }

    internal readonly struct Releaser(AsyncFifoLock owner) : IDisposable
    {
        public void Dispose() => owner.Release();
    }
}
