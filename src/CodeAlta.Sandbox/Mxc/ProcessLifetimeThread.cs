using System.Collections.Concurrent;

namespace CodeAlta.Sandbox.Mxc;

/// <summary>
/// A thread that runs blocking operations one after the other and never ends, for what must not depend on the life of
/// the thread that does it.
/// </summary>
/// <remarks>
/// On Linux, mxc 1.0.1 starts <c>bwrap --die-with-parent</c> on the calling thread, and Linux kills the sandbox when that
/// thread ends, not when the process does (microsoft/mxc#1525). A thread of the thread pool ends after 20 seconds without
/// work, which killed sandboxes at random (CodeAlta#226). This thread lives as long as the process: its sandboxes still
/// die with CodeAlta, and only with it.
/// </remarks>
internal sealed class ProcessLifetimeThread
{
    private readonly BlockingCollection<Action> _operations = new();
    private readonly Lazy<Thread> _thread;

    /// <summary>Initializes a new instance of the <see cref="ProcessLifetimeThread"/> class; its thread starts with the first operation.</summary>
    /// <param name="name">The name of the thread.</param>
    public ProcessLifetimeThread(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _thread = new(() =>
        {
            var thread = new Thread(Run) { IsBackground = true, Name = name };
            thread.Start();
            return thread;
        });
    }

    /// <summary>Runs an operation on the thread.</summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="disposeLateResult">Releases a result that comes after the wait was canceled.</param>
    /// <param name="cancellationToken">Cancels the wait, and the operation while it has not started.</param>
    /// <returns>The result of the operation.</returns>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    public async Task<T> RunAsync<T>(Func<T> operation, Action<T> disposeLateResult, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(disposeLateResult);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _thread.Value;
        _operations.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(operation());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, CancellationToken.None);

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = completion.Task.ContinueWith(static (task, state) =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    try
                    {
                        ((Action<T>)state!)(task.Result);
                    }
                    catch
                    {
                        // Nobody waits for that result any more: releasing it is all that is left to do.
                    }
                }
                else if (task.IsFaulted)
                {
                    _ = task.Exception;
                }
            }, disposeLateResult, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private void Run()
    {
        foreach (var operation in _operations.GetConsumingEnumerable())
            operation();
    }
}
