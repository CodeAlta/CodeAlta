using System.Buffers;

namespace CodeAlta.Fleet.Protocol;

/// <summary>The kind of a frame a transport receives.</summary>
public enum FleetFrameType
{
    /// <summary>The peer closed its side: no frame follows.</summary>
    Closed,

    /// <summary>A text frame: the UTF-8 JSON of a <see cref="FleetMessage"/>.</summary>
    Text,

    /// <summary>A binary frame (bundle chunks, replica segments), for the channels that carry them.</summary>
    Binary,
}

/// <summary>
/// A bidirectional message transport: whole frames, in order, in both directions.
/// </summary>
/// <remarks>
/// A transport is driven by one reader and one writer at a time (a <see cref="FleetConnection"/> has one reader loop
/// and one writer loop): <see cref="ReceiveAsync"/> is never called concurrently with itself, nor
/// <see cref="SendAsync"/> or <see cref="CloseAsync"/> with each other. <see cref="Abort"/> may be called from any
/// thread at any time.
/// </remarks>
public interface IFleetTransport : IAsyncDisposable
{
    /// <summary>Sends one whole frame.</summary>
    /// <param name="frame">The content of the frame.</param>
    /// <param name="type">The kind of frame: <see cref="FleetFrameType.Text"/> or <see cref="FleetFrameType.Binary"/>.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the frame is handed to the transport.</returns>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, FleetFrameType type, CancellationToken cancellationToken);

    /// <summary>Receives the next whole frame.</summary>
    /// <param name="destination">The buffer the content of the frame is written to.</param>
    /// <param name="maxFrameBytes">The largest frame accepted.</param>
    /// <param name="cancellationToken">Cancels the receive.</param>
    /// <returns>The kind of the frame, or <see cref="FleetFrameType.Closed"/> when the peer closed its side.</returns>
    /// <exception cref="FleetException">The frame is larger than <paramref name="maxFrameBytes"/> (<see cref="FleetErrorCodes.FrameTooLarge"/>).</exception>
    /// <exception cref="IOException">The transport failed or was aborted.</exception>
    ValueTask<FleetFrameType> ReceiveAsync(IBufferWriter<byte> destination, int maxFrameBytes, CancellationToken cancellationToken);

    /// <summary>Closes this side cleanly: the peer receives <see cref="FleetFrameType.Closed"/> after the frames sent before.</summary>
    /// <param name="description">Why the connection closes, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the close.</param>
    /// <returns>A task that completes when the close is sent.</returns>
    ValueTask CloseAsync(string? description, CancellationToken cancellationToken);

    /// <summary>Ends the transport at once: pending and later sends and receives fail.</summary>
    void Abort();
}
