namespace CodeAlta.Orchestration.Runtime;

// The sole runtime publication seam. Display storage does not replace the original effects stream:
// original event instances still reach that lossy route without DTO/JSON/exception reconstruction.
internal sealed class SessionRuntimeEventPublisher(int capacity = BoundedRuntimeEventStream<SessionRuntimeEvent>.DefaultCapacity)
{
    private readonly object _gate = new();
    private readonly BoundedRuntimeEventStream<SessionRuntimeEvent> _events = new(capacity);
    private bool _closed;

    internal RuntimeDisplayProjection Display { get; } = new();
    internal long DroppedCount => _events.DroppedCount;

    internal bool TryPublish(SessionRuntimeEvent runtimeEvent)
    {
        lock (_gate)
        {
            if (_closed) return false;
            Display.Commit(runtimeEvent);
            return _events.TryPublish(runtimeEvent);
        }
    }

    internal IAsyncEnumerable<SessionRuntimeEvent> ReadAllAsync(CancellationToken cancellationToken = default)
        => _events.ReadAllAsync(cancellationToken);

    internal void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Display.Complete();
            _events.Complete();
        }
    }
}
