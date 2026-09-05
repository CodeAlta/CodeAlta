using System.Collections.Concurrent;

namespace CodeAlta.Tui.App;

internal sealed class DeferredUiActionQueue
{
    private readonly ConcurrentQueue<Action> _pendingActions = new();

    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _pendingActions.Enqueue(action);
    }

    public int Drain(int maxActions = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActions);

        var drainedActions = 0;
        while (drainedActions < maxActions && _pendingActions.TryDequeue(out var action))
        {
            drainedActions++;
            action();
        }

        return drainedActions;
    }
}
