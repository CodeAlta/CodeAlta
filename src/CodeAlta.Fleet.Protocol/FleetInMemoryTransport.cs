using System.Buffers;
using System.Threading.Channels;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// One end of an in-memory duplex pair, for tests and for hosts of one process that talk as machines would.
/// </summary>
/// <remarks>
/// Each direction is a bounded queue of frames: a sender waits when its peer does not read, as over a socket.
/// </remarks>
public sealed class FleetInMemoryTransport : IFleetTransport
{
    /// <summary>The default number of frames each direction holds before a sender waits.</summary>
    public const int DefaultCapacity = 64;

    private readonly Channel<Frame> _inbound;
    private readonly Channel<Frame> _outbound;
    private int _aborted;

    private FleetInMemoryTransport(Channel<Frame> inbound, Channel<Frame> outbound)
    {
        _inbound = inbound;
        _outbound = outbound;
    }

    /// <summary>Creates two connected ends.</summary>
    /// <param name="capacity">The number of frames each direction holds before a sender waits.</param>
    /// <returns>The two ends: what one sends, the other receives.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public static (FleetInMemoryTransport First, FleetInMemoryTransport Second) CreatePair(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        var options = new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait };
        var firstToSecond = Channel.CreateBounded<Frame>(options);
        var secondToFirst = Channel.CreateBounded<Frame>(options);
        return (new FleetInMemoryTransport(secondToFirst, firstToSecond), new FleetInMemoryTransport(firstToSecond, secondToFirst));
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, FleetFrameType type, CancellationToken cancellationToken)
    {
        if (type == FleetFrameType.Closed) throw new ArgumentOutOfRangeException(nameof(type), type, "A closed frame is sent by CloseAsync.");
        ThrowIfAborted();
        try
        {
            await _outbound.Writer.WriteAsync(new Frame(frame.ToArray(), type), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new IOException("The in-memory transport is closed.", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<FleetFrameType> ReceiveAsync(IBufferWriter<byte> destination, int maxFrameBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        ThrowIfAborted();
        Frame frame;
        try
        {
            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return FleetFrameType.Closed;
            if (!_inbound.Reader.TryRead(out frame)) return FleetFrameType.Closed;
        }
        catch (ChannelClosedException exception)
        {
            throw new IOException("The peer aborted the in-memory transport.", exception);
        }

        if (frame.Bytes.Length > maxFrameBytes)
        {
            throw new FleetException(FleetErrorCodes.FrameTooLarge, null, $"A frame of {frame.Bytes.Length} bytes exceeds the limit of {maxFrameBytes} bytes.");
        }

        destination.Write(frame.Bytes);
        return frame.Type;
    }

    /// <inheritdoc />
    public ValueTask CloseAsync(string? description, CancellationToken cancellationToken)
    {
        ThrowIfAborted();
        _outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
        var aborted = new IOException("The in-memory transport was aborted.");
        // Both directions fail: the peer's receive and send throw, as on a reset socket.
        _outbound.Writer.TryComplete(aborted);
        _inbound.Writer.TryComplete(aborted);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Abort();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfAborted()
    {
        if (Volatile.Read(ref _aborted) != 0) throw new IOException("The in-memory transport was aborted.");
    }

    private readonly record struct Frame(byte[] Bytes, FleetFrameType Type);
}
