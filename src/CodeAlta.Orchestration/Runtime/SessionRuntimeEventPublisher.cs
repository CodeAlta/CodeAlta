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

    // Told after each publication, outside the gate: what waits for the sessions to settle looks at them again.
    internal Action? Published { get; set; }

    internal bool TryPublish(SessionRuntimeEvent runtimeEvent)
    {
        bool published;
        lock (_gate)
        {
            if (_closed) return false;
            Display.Commit(runtimeEvent);
            ToolOutput.Commit(runtimeEvent);
            published = _events.TryPublish(runtimeEvent);
        }

        Published?.Invoke();
        return published;
    }

    // Publishes an event to the reader alone, after every event published before it: the projections never see it.
    internal bool TryPublishToReader(SessionRuntimeEvent runtimeEvent)
    {
        lock (_gate)
        {
            return !_closed && _events.TryPublish(runtimeEvent);
        }
    }

    // Publishes an event to the reader alone, after every event published before it, waiting for room in a full stream: what
    // must not be dropped (the marker of a flush). False when the stream is closed.
    internal ValueTask<bool> PublishToReaderAsync(SessionRuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closed) return ValueTask.FromResult(false);
        }

        // Outside the gate: the publications never wait for it. A completion meanwhile ends the wait with false.
        return _events.PublishAsync(runtimeEvent, cancellationToken);
    }

    internal async IAsyncEnumerable<SessionRuntimeEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var stamped in ReadStampedAsync(cancellationToken).ConfigureAwait(false))
            yield return stamped.Event;
    }

    // The events with the number of events the stream had dropped when each was accepted.
    internal async IAsyncEnumerable<BoundedRuntimeEventStream<SessionRuntimeEvent>.StampedEvent> ReadStampedAsync(
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
            await foreach (var stamped in _events.ReadStampedAsync(cancellationToken).ConfigureAwait(false))
                yield return stamped;
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
