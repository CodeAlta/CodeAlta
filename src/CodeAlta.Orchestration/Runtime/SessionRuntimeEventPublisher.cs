using System.Runtime.CompilerServices;

namespace CodeAlta.Orchestration.Runtime;

// The sole runtime publication seam. Display storage does not replace the original effects stream:
// original event instances still reach that lossy route without DTO/JSON/exception reconstruction.
internal sealed class SessionRuntimeEventPublisher(int capacity = BoundedRuntimeEventStream<SessionRuntimeEvent>.DefaultCapacity)
{
    private readonly object _gate = new();
    private readonly BoundedRuntimeEventStream<SessionRuntimeEvent> _events = new(capacity);
    private bool _closed;
    private bool _readerActive;

    internal RuntimeDisplayProjection Display { get; } = new();
    internal RuntimeToolOutputProjection ToolOutput { get; } = new();
    internal long DroppedCount => _events.DroppedCount;

    internal bool TryPublish(SessionRuntimeEvent runtimeEvent)
    {
        lock (_gate)
        {
            if (_closed) return false;
            Display.Commit(runtimeEvent);
            ToolOutput.Commit(runtimeEvent);
            return _events.TryPublish(runtimeEvent);
        }
    }

    internal async IAsyncEnumerable<SessionRuntimeEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_readerActive) throw new InvalidOperationException("Only one original runtime event reader may be active.");
            _readerActive = true;
        }

        try
        {
            // Retain the channel's buffered cancellation semantics; a per-item token check here
            // could consume and then discard an original event instead of delivering it.
            await foreach (var runtimeEvent in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return runtimeEvent;
        }
        finally
        {
            // Cancel/Complete alone cannot release a reader suspended at yield. The inner
            // enumeration must actually terminate/dispose before a successor can consume.
            lock (_gate) _readerActive = false;
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Display.Complete();
            ToolOutput.Complete();
            _events.Complete();
        }
    }
}
