using System.Net.WebSockets;
using System.Runtime.InteropServices;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Channels;

/// <summary>The constants of the session channel between two devices (<c>doc/fleet/protocol.md</c>, "Connections").</summary>
public static class FleetChannelProtocol
{
    /// <summary>The port a node listens on for direct channels, by default.</summary>
    public const int DefaultPort = 2585;

    /// <summary>The versions of the fleet protocol the session channel speaks.</summary>
    public static FleetProtocolRange Versions { get; } = new(1, 1);

    /// <summary>The kind of client a desktop, a TUI or <c>altanode</c> announces when it connects to a node.</summary>
    public const string ViewerClientKind = "viewer";

    /// <summary>The kind of client <c>altanode pair</c> announces to the node it runs beside.</summary>
    public const string ControlClientKind = "control";

    /// <summary>
    /// The proof a client puts in its hello. The device is proved by <see cref="PinnedTls"/> before the WebSocket starts
    /// (a signature bound to the TLS channel), so the hello repeats nothing: its device id is checked against the one TLS
    /// authenticated.
    /// </summary>
    public const string TlsProof = "tls";

    /// <summary>Gets the operating system of this process, as a client or a node describes itself.</summary>
    public static string OperatingSystemName
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : RuntimeInformation.OSDescription;
}

/// <summary>
/// A session channel once its device is authenticated and its handshake done: the connection, not started yet, and who is
/// at the other end. The caller registers its handlers on <see cref="Connection"/>, then starts it.
/// </summary>
public sealed class FleetChannel : IAsyncDisposable
{
    internal FleetChannel(FleetConnection connection, string remoteDeviceId, DeviceFingerprint remoteFingerprint, TrustedDevice? remoteDevice,
        FleetClientInfo? client, FleetWelcome welcome)
    {
        Connection = connection;
        RemoteDeviceId = remoteDeviceId;
        RemoteFingerprint = remoteFingerprint;
        RemoteDevice = remoteDevice;
        Client = client;
        Welcome = welcome;
    }

    /// <summary>Gets the connection of the channel. It owns the transport: disposing it closes the channel.</summary>
    public FleetConnection Connection { get; }

    /// <summary>Gets the id of the device at the other end, as TLS authenticated it.</summary>
    public string RemoteDeviceId { get; }

    /// <summary>Gets the fingerprint of the key of the device at the other end.</summary>
    public DeviceFingerprint RemoteFingerprint { get; }

    /// <summary>Gets the trusted device at the other end when this side knew it at the time of the handshake; null for an unpaired client.</summary>
    public TrustedDevice? RemoteDevice { get; }

    /// <summary>Gets what the client said it is (on the node's side); null on the client's side.</summary>
    public FleetClientInfo? Client { get; }

    /// <summary>Gets the welcome of the handshake: the negotiated version, the features and the policy.</summary>
    public FleetWelcome Welcome { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

/// <summary>What the node's side of a session channel needs: its key, who it trusts, and whether unpaired clients may come to pair.</summary>
public sealed class FleetChannelServerOptions
{
    /// <summary>Gets the TLS certificate of the node's device key (<see cref="DeviceIdentity.CreateTlsCertificate"/>).</summary>
    public required System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; init; }

    /// <summary>Gets the devices the node trusts and those it revoked.</summary>
    public required IDeviceTrustPolicy TrustPolicy { get; init; }

    /// <summary>Gets what tells, for each new channel, whether a client the node does not know may come in to redeem a pairing offer.</summary>
    public Func<bool> AllowUnpairedClients { get; init; } = static () => false;

    /// <summary>Gets the fingerprint of the node's own key: a client that proves it is the owner's local control (<c>altanode pair</c>).</summary>
    public DeviceFingerprint OwnFingerprint { get; init; }

    /// <summary>Gets the features the node announces in its welcome.</summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>Gets the limits the node announces.</summary>
    public FleetConnectionPolicy Policy { get; init; } = FleetConnectionPolicy.CreateDefault();

    /// <summary>Gets how long a client has for TLS, its device proof and the handshake.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>What the client's side of a session channel needs: its key, the node's pinned key, and what it is.</summary>
public sealed class FleetChannelClientOptions
{
    /// <summary>Gets the key of this device.</summary>
    public required DeviceIdentity Identity { get; init; }

    /// <summary>Gets the fingerprint of the node's key, from pairing: the channel is refused when the node presents another key.</summary>
    public required DeviceFingerprint NodeFingerprint { get; init; }

    /// <summary>Gets what this client is.</summary>
    public FleetClientInfo Client { get; init; } = new(FleetChannelProtocol.ViewerClientKind, "1", FleetChannelProtocol.OperatingSystemName);

    /// <summary>Gets how long the node has for TLS, the device proof and the handshake.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The stack of a session channel over any stream: TLS with pinned device keys (<see cref="PinnedTls"/>), a WebSocket made
/// from the TLS stream, the fleet handshake, then a <see cref="FleetConnection"/>. A stream in, a channel out: the stream
/// is a TCP connection for a direct channel, and will be a relayed stream later, with the same stack inside.
/// </summary>
public static class FleetChannelStack
{
    /// <summary>Runs the node's side of the stack on a new stream.</summary>
    /// <param name="stream">The stream from the client. The channel owns it from now on; it is disposed when the stack fails.</param>
    /// <param name="options">The key, the trust and the policy of the node.</param>
    /// <param name="cancellationToken">Cancels the stack.</param>
    /// <returns>The authenticated channel, not started.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DeviceAuthenticationException">The client's device was refused (unknown, revoked, or its proof invalid).</exception>
    /// <exception cref="FleetHandshakeException">The handshake failed.</exception>
    /// <exception cref="TimeoutException">The client did not complete the stack within <see cref="FleetChannelServerOptions.HandshakeTimeout"/>.</exception>
    /// <exception cref="IOException">The stream failed or closed.</exception>
    public static async Task<FleetChannel> AcceptAsync(Stream stream, FleetChannelServerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HandshakeTimeout);
        PinnedTlsConnection? tls = null;
        WebSocket? socket = null;
        try
        {
            var trust = options.OwnFingerprint.IsEmpty ? options.TrustPolicy : new OwnerAwareTrustPolicy(options.TrustPolicy, options.OwnFingerprint);
            tls = await PinnedTls.AuthenticateAsServerAsync(stream, new PinnedTlsServerOptions
            {
                Certificate = options.Certificate,
                TrustPolicy = trust,
                AllowUnpairedClients = options.AllowUnpairedClients(),
            }, timeout.Token).ConfigureAwait(false);

            socket = WebSocket.CreateFromStream(tls.Stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
            var transport = new FleetWebSocketTransport(socket);
            var accepted = await FleetHandshake.AcceptAsync(transport, new FleetHandshakeServerOptions
            {
                Protocol = FleetChannelProtocol.Versions,
                Policy = options.Policy,
                Features = options.Features,
                Timeout = options.HandshakeTimeout,
            }, new TlsBoundDeviceVerifier(tls.RemoteDeviceId), timeout.Token).ConfigureAwait(false);

            var device = tls.RemoteFingerprint == options.OwnFingerprint ? null : tls.RemoteDevice;
            var connection = new FleetConnection(transport, FleetConnectionOptions.FromPolicy(accepted.Welcome.Policy));
            return new FleetChannel(connection, tls.RemoteDeviceId, tls.RemoteFingerprint, device, accepted.Hello.Client, accepted.Welcome);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Dispose(socket, tls, stream);
            throw new TimeoutException("The client did not open its fleet channel in time.", exception);
        }
        catch
        {
            Dispose(socket, tls, stream);
            throw;
        }
    }

    /// <summary>Runs the client's side of the stack on a new stream to a node.</summary>
    /// <param name="stream">The stream to the node. The channel owns it from now on; it is disposed when the stack fails.</param>
    /// <param name="options">The key of this device and the pinned key of the node.</param>
    /// <param name="cancellationToken">Cancels the stack.</param>
    /// <returns>The authenticated channel, not started.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DeviceAuthenticationException">The node presented another key, or refused this device (not paired, revoked).</exception>
    /// <exception cref="FleetHandshakeException">The node refused the handshake.</exception>
    /// <exception cref="TimeoutException">The node did not complete the stack within <see cref="FleetChannelClientOptions.HandshakeTimeout"/>.</exception>
    /// <exception cref="IOException">The stream failed or closed.</exception>
    public static async Task<FleetChannel> ConnectAsync(Stream stream, FleetChannelClientOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HandshakeTimeout);
        PinnedTlsConnection? tls = null;
        WebSocket? socket = null;
        try
        {
            tls = await PinnedTls.AuthenticateAsClientAsync(stream, new PinnedTlsClientOptions
            {
                Identity = options.Identity,
                ServerFingerprint = options.NodeFingerprint,
            }, timeout.Token).ConfigureAwait(false);

            socket = WebSocket.CreateFromStream(tls.Stream, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
            var transport = new FleetWebSocketTransport(socket);
            var welcome = await FleetHandshake.ConnectAsync(transport, new FleetHandshakeClientOptions
            {
                Protocol = FleetChannelProtocol.Versions,
                Client = options.Client,
                Timeout = options.HandshakeTimeout,
            }, new TlsBoundDeviceSigner(options.Identity.DeviceId), timeout.Token).ConfigureAwait(false);

            var connection = new FleetConnection(transport, FleetConnectionOptions.FromPolicy(welcome.Policy));
            return new FleetChannel(connection, tls.RemoteDeviceId, tls.RemoteFingerprint, tls.RemoteDevice, null, welcome);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Dispose(socket, tls, stream);
            throw new TimeoutException("The node did not open the fleet channel in time.", exception);
        }
        catch
        {
            Dispose(socket, tls, stream);
            throw;
        }
    }

    private static void Dispose(WebSocket? socket, PinnedTlsConnection? tls, Stream stream)
    {
        socket?.Dispose();
        tls?.Dispose();
        stream.Dispose();
    }

    // The device is authenticated by PinnedTls before the WebSocket starts: the client signed a nonce of the node bound to
    // the TLS channel with its device key, and the node checked the key against its trusted devices. The hello comes inside
    // that channel, so it cannot come from anyone else; repeating a signature there would prove nothing more. What the
    // verifier checks is that the hello names the device TLS authenticated, so that no layer above ever reads another id.
    private sealed class TlsBoundDeviceVerifier(string authenticatedDeviceId) : IFleetHandshakeVerifier
    {
        public ValueTask<FleetProofVerification> VerifyAsync(FleetHello hello, FleetProofContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(string.Equals(hello.Device.Id, authenticatedDeviceId, StringComparison.Ordinal)
                ? FleetProofVerification.Accept()
                : FleetProofVerification.Reject(new FleetError(FleetErrorCodes.Unauthenticated, FleetErrorHints.Reauthenticate,
                    "The hello names another device than the one TLS authenticated.")));
    }

    // See TlsBoundDeviceVerifier: the proof of the key was made in the TLS exchange.
    private sealed class TlsBoundDeviceSigner(string deviceId) : IFleetHandshakeSigner
    {
        public string DeviceId { get; } = deviceId;

        public ValueTask<string> SignAsync(FleetProofContext context, CancellationToken cancellationToken) => ValueTask.FromResult(FleetChannelProtocol.TlsProof);
    }

    // The node's own key is the owner's local control (altanode pair beside the node): it is admitted as known, never as
    // a trusted device; what it may do is decided above, from where it connects.
    private sealed class OwnerAwareTrustPolicy(IDeviceTrustPolicy inner, DeviceFingerprint own) : IDeviceTrustPolicy
    {
        private readonly TrustedDevice _owner = new(DeviceIdentity.DeviceIdOf(own), "owner", own, DevicePermissions.None, DateTimeOffset.UnixEpoch, null);

        public TrustedDevice? FindTrusted(DeviceFingerprint fingerprint) => fingerprint == own ? _owner : inner.FindTrusted(fingerprint);

        public bool IsRevoked(DeviceFingerprint fingerprint) => fingerprint != own && inner.IsRevoked(fingerprint);
    }
}
