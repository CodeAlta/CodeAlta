using System.Buffers.Text;
using System.Globalization;
using System.Net.WebSockets;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>Where a session channel goes: straight to the node, or through the relay of a server.</summary>
public enum FleetChannelPath
{
    /// <summary>A TCP connection to an address of the node (paired machines, the LAN, a tailnet).</summary>
    Direct,

    /// <summary>Through the relay of a CodeAlta Server, which pipes the TLS of the channel without reading it.</summary>
    Relayed,
}

/// <summary>
/// Opens the side of a relay this device holds a ticket of: a WebSocket to <c>/fleet/v1/relay/{ticket}</c>, its upgrade
/// signed with the device key, given as a stream of bytes. The session channel runs its whole stack on it
/// (<see cref="Channels.FleetChannelStack"/>): TLS between the two devices, then the fleet handshake.
/// </summary>
public static class FleetRelayStreams
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Gets the address of a side of a relay: <c>ws(s)://&lt;server&gt;/fleet/v1/relay/{ticket}</c>.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="ticket">The ticket of the side.</param>
    /// <returns>The WebSocket address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="ticket"/> is empty.</exception>
    public static Uri Address(Uri server, string ticket)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(ticket);
        var builder = new UriBuilder(server) { Scheme = server.Scheme == "https" ? "wss" : "ws" };
        builder.Path = builder.Path.TrimEnd('/') + FleetServerMethods.RelayPath + "/" + Uri.EscapeDataString(ticket);
        return builder.Uri;
    }

    /// <summary>Opens the side of a relay.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="ticket">The ticket of this side.</param>
    /// <param name="identity">The key of this device, which signs the upgrade.</param>
    /// <param name="time">The clock of the signature; null for the system's.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The stream of the relay; disposing it closes the relay.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="WebSocketException">The server refused the side (a ticket used or expired, a signature it does not take) or does not answer.</exception>
    public static async Task<Stream> ConnectAsync(Uri server, FleetRelayTicket ticket, DeviceIdentity identity, TimeProvider? time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(identity);
        var address = Address(server, ticket.Ticket);
        var socket = new ClientWebSocket();
        try
        {
            var now = (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
            var nonce = FleetSignedRequest.CreateNonce();
            var signed = FleetSignedRequest.GetSignedBytes("GET", address.PathAndQuery, now, nonce, FleetSignedRequest.HashBody([]), identity.DeviceId);
            socket.Options.SetRequestHeader(FleetSignedRequest.DeviceHeader, identity.DeviceId);
            socket.Options.SetRequestHeader(FleetSignedRequest.TimestampHeader, now.ToString(CultureInfo.InvariantCulture));
            socket.Options.SetRequestHeader(FleetSignedRequest.NonceHeader, nonce);
            socket.Options.SetRequestHeader(FleetSignedRequest.SignatureHeader, Base64Url.EncodeToString(identity.SignData(signed)));
            // The channel inside has its own heartbeat; the relay is a pipe.
            socket.Options.KeepAliveInterval = TimeSpan.Zero;
            await socket.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
            return WebSocketStream.Create(socket, WebSocketMessageType.Binary, CloseTimeout);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
