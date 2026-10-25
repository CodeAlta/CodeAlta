using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Provides a bounded runtime event stream with a documented newest-event drop policy under pressure.
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class BoundedRuntimeEventStream<TEvent>
{
    /// <summary>The default event stream capacity.</summary>
    public const int DefaultCapacity = 1024;

    // Each event with the number of events dropped before it was accepted: a reader tells from two of them whether events
    // were lost between them.
    private readonly Channel<StampedEvent> _channel;
    private long _droppedCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="BoundedRuntimeEventStream{TEvent}"/> class.
    /// </summary>
    /// <param name="capacity">The bounded channel capacity.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is less than one.</exception>
    public BoundedRuntimeEventStream(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Event stream capacity must be at least one.");
        }

        _channel = Channel.CreateBounded<StampedEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>Gets the approximate number of events dropped because the stream was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    /// Attempts to publish an event without blocking runtime progress.
    /// </summary>
    /// <param name="runtimeEvent">The event to publish.</param>
    /// <returns><see langword="true"/> when the event was accepted; otherwise, <see langword="false"/> when the stream is complete.</returns>
    public bool TryPublish(TEvent runtimeEvent)
    {
        if (_channel.Writer.TryWrite(new StampedEvent(runtimeEvent, DroppedCount)))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    // Publishes an event that must not be dropped (the marker of a flush): waits for room, after every event accepted before
    // it. Never for runtime progress, which TryPublish never blocks. False when the stream is complete.
    internal async ValueTask<bool> PublishAsync(TEvent runtimeEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(new StampedEvent(runtimeEvent, DroppedCount), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Streams events until the stream is completed or cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async event sequence.</returns>
    public async IAsyncEnumerable<TEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var stamped in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return stamped.Event;
    }

    // Streams the events with the number of events dropped before each was accepted.
    internal IAsyncEnumerable<StampedEvent> ReadStampedAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Completes the stream.
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();

    // An event, and how many events the stream had dropped when it was accepted.
    internal readonly record struct StampedEvent(TEvent Event, long DroppedBefore);
}
