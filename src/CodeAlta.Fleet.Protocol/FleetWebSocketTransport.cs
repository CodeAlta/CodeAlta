using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// A transport over a WebSocket: JSON envelopes in text frames, binary payloads in binary frames.
/// </summary>
/// <remarks>
/// The client side comes from <see cref="ConnectAsync"/>; the server side wraps the socket an ASP.NET Core endpoint
/// accepted. The close is sent with <see cref="WebSocket.CloseOutputAsync"/> so that it never receives beside the
/// reader loop: the reader sees the peer's close as <see cref="FleetFrameType.Closed"/>.
/// </remarks>
public sealed class FleetWebSocketTransport : IFleetTransport
{
    // A close description is limited to 123 bytes of UTF-8 by RFC 6455.
    private const int MaxCloseDescriptionBytes = 123;
    private const int ReceiveChunkBytes = 16 * 1024;

    private readonly WebSocket _socket;

    /// <summary>Initializes a new instance of the <see cref="FleetWebSocketTransport"/> class over an open socket.</summary>
    /// <param name="socket">The socket, which the transport owns from now on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="socket"/> is <see langword="null"/>.</exception>
    public FleetWebSocketTransport(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    /// <summary>Gets the socket.</summary>
    public WebSocket Socket => _socket;

    /// <summary>Connects to a WebSocket endpoint, such as <c>wss://server/fleet/v1</c>.</summary>
    /// <param name="uri">The address of the endpoint.</param>
    /// <param name="configure">Configures the client socket (headers, certificates, proxy), or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The transport over the connected socket.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is <see langword="null"/>.</exception>
    /// <exception cref="WebSocketException">The connection failed.</exception>
    public static async Task<FleetWebSocketTransport> ConnectAsync(Uri uri, Action<ClientWebSocketOptions>? configure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var socket = new ClientWebSocket();
        try
        {
            configure?.Invoke(socket.Options);
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return new FleetWebSocketTransport(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, FleetFrameType type, CancellationToken cancellationToken)
    {
        var messageType = type switch
        {
            FleetFrameType.Text => WebSocketMessageType.Text,
            FleetFrameType.Binary => WebSocketMessageType.Binary,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "A closed frame is sent by CloseAsync."),
        };
        return _socket.SendAsync(frame, messageType, endOfMessage: true, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<FleetFrameType> ReceiveAsync(IBufferWriter<byte> destination, int maxFrameBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        var total = 0;
        while (true)
        {
            // One byte more than the limit allows tells a frame that is too large without buffering it.
            var memory = destination.GetMemory(Math.Min(ReceiveChunkBytes, maxFrameBytes - total + 1));
            var result = await _socket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Only a normal closure is a clean close: an aborted peer or one that refused a frame is a failure.
                if (_socket.CloseStatus == WebSocketCloseStatus.NormalClosure) return FleetFrameType.Closed;
                throw new IOException($"The peer closed the WebSocket with the status {_socket.CloseStatus?.ToString() ?? "none"}.");
            }
            total += result.Count;
            if (total > maxFrameBytes)
            {
                throw new FleetException(FleetErrorCodes.FrameTooLarge, null, $"A frame exceeds the limit of {maxFrameBytes} bytes.");
            }

            destination.Advance(result.Count);
            if (result.EndOfMessage) return result.MessageType == WebSocketMessageType.Text ? FleetFrameType.Text : FleetFrameType.Binary;
        }
    }

    /// <inheritdoc />
    public async ValueTask CloseAsync(string? description, CancellationToken cancellationToken)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, Truncate(description), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Abort() => _socket.Abort();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string? Truncate(string? description)
    {
        if (description is null || Encoding.UTF8.GetByteCount(description) <= MaxCloseDescriptionBytes) return description;
        var length = Math.Min(description.Length, MaxCloseDescriptionBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(description.AsSpan(0, length)) > MaxCloseDescriptionBytes) length--;
        // Never end between the two halves of a surrogate pair.
        if (length > 0 && char.IsHighSurrogate(description[length - 1])) length--;
        return description[..length];
    }
}
