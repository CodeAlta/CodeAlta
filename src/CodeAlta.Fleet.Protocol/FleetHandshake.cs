using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodeAlta.Fleet.Protocol;

/// <summary>The options of the side that challenges: a server, or a node that accepts a channel.</summary>
public sealed class FleetHandshakeServerOptions
{
    /// <summary>Gets the protocol versions this side supports.</summary>
    public required FleetProtocolRange Protocol { get; init; }

    /// <summary>Gets the limits announced to the client.</summary>
    public FleetConnectionPolicy Policy { get; init; } = FleetConnectionPolicy.CreateDefault();

    /// <summary>Gets the features announced in the welcome.</summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>Gets the binding of the channel (from TLS) that the proof signs, or empty when there is none.</summary>
    public ReadOnlyMemory<byte> ChannelBinding { get; init; }

    /// <summary>Gets how long the client has to answer the challenge.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the clock of the challenge's time.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets what gives the cursors of the welcome for an accepted client, or <see langword="null"/> for none.</summary>
    public Func<FleetHello, CancellationToken, ValueTask<JsonElement?>>? ResolveCursorsAsync { get; init; }
}

/// <summary>The options of the side that answers a challenge: a device that connects.</summary>
public sealed class FleetHandshakeClientOptions
{
    /// <summary>Gets the protocol versions this side supports.</summary>
    public required FleetProtocolRange Protocol { get; init; }

    /// <summary>Gets what the client is.</summary>
    public required FleetClientInfo Client { get; init; }

    /// <summary>Gets the cursors the client resumes from, or <see langword="null"/> for none.</summary>
    public JsonElement? ResumeCursors { get; init; }

    /// <summary>Gets the binding of the channel (from TLS) that the proof signs, or empty when there is none.</summary>
    public ReadOnlyMemory<byte> ChannelBinding { get; init; }

    /// <summary>Gets how long the server has to send its challenge, then its welcome.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A client accepted by <see cref="FleetHandshake.AcceptAsync"/>.</summary>
/// <param name="Hello">The hello of the client: what it is, its device, what it resumes from.</param>
/// <param name="Welcome">The welcome sent to it: the negotiated version, the features, the policy.</param>
public sealed record FleetAcceptedClient(FleetHello Hello, FleetWelcome Welcome);

/// <summary>
/// The first frames of every connection: <c>hello.challenge</c>, <c>hello</c>, then <c>welcome</c> or <c>error</c>.
/// </summary>
/// <remarks>
/// The handshake runs on the transport before a <see cref="FleetConnection"/> starts on it; the connection then takes
/// its limits from <see cref="FleetConnectionOptions.FromPolicy"/> of the welcome's policy. On a refusal, the side
/// that refuses sends the error and closes its side; both sides throw <see cref="FleetHandshakeException"/>.
/// </remarks>
public static class FleetHandshake
{
    // A frame of the handshake is small: this bounds what a side reads before the peer is authenticated.
    private const int MaxHandshakeFrameBytes = 64 * 1024;
    private const int NonceBytes = 32;

    /// <summary>Challenges a client and accepts it or refuses it.</summary>
    /// <param name="transport">The transport of the new connection.</param>
    /// <param name="options">The versions, the policy and the features of this side.</param>
    /// <param name="verifier">Checks the device proof of the client.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The accepted client.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The protocol range of <paramref name="options"/> is not valid.</exception>
    /// <exception cref="FleetHandshakeException">The client was refused (<see cref="FleetErrorCodes.UpdateRequired"/>, a refused proof, a frame out of place).</exception>
    /// <exception cref="TimeoutException">The client did not answer within <see cref="FleetHandshakeServerOptions.Timeout"/>.</exception>
    /// <exception cref="IOException">The transport failed or closed.</exception>
    public static async Task<FleetAcceptedClient> AcceptAsync(IFleetTransport transport, FleetHandshakeServerOptions options, IFleetHandshakeVerifier verifier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(verifier);
        if (!options.Protocol.IsValid) throw new ArgumentException("The protocol range is not valid.", nameof(options));

        using var timeout = CreateTimeout(options.Timeout, cancellationToken);
        var token = timeout.Token;
        try
        {
            var challenge = new FleetHelloChallenge(CreateNonce(), options.TimeProvider.GetUtcNow(), options.Protocol, options.Policy);
            await SendAsync(transport, challenge, token).ConfigureAwait(false);

            var frame = await ReceiveAsync(transport, token).ConfigureAwait(false);
            if (frame is not FleetHello hello || hello.Protocol is null || hello.Client is null || hello.Device is null || string.IsNullOrEmpty(hello.Device.Id))
            {
                throw await RefuseAsync(transport, new FleetError(FleetErrorCodes.ProtocolError, null, "The first frame of a client must be a hello."), token).ConfigureAwait(false);
            }

            if (!FleetProtocolRange.TryNegotiate(options.Protocol, hello.Protocol, out var version))
            {
                var error = new FleetError(FleetErrorCodes.UpdateRequired, FleetErrorHints.Update,
                    $"This side speaks the fleet protocol {options.Protocol.Min} to {options.Protocol.Max}; the client speaks {hello.Protocol.Min} to {hello.Protocol.Max}.");
                throw await RefuseAsync(transport, error, token).ConfigureAwait(false);
            }

            var context = new FleetProofContext(challenge.Nonce, challenge.Time, hello.Device.Id, options.ChannelBinding);
            var verification = await verifier.VerifyAsync(hello, context, token).ConfigureAwait(false);
            if (!verification.IsAccepted)
            {
                throw await RefuseAsync(transport, verification.Error!, token).ConfigureAwait(false);
            }

            var cursors = options.ResolveCursorsAsync is null ? null : await options.ResolveCursorsAsync(hello, token).ConfigureAwait(false);
            var welcome = new FleetWelcome(version, options.Features, options.Policy, cursors);
            await SendAsync(transport, welcome, token).ConfigureAwait(false);
            return new FleetAcceptedClient(hello, welcome);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            transport.Abort();
            throw new TimeoutException("The client did not complete the fleet handshake in time.", exception);
        }
    }

    /// <summary>Answers the challenge of a server or a node and waits for its welcome.</summary>
    /// <param name="transport">The transport of the new connection.</param>
    /// <param name="options">The versions and the identity of this side.</param>
    /// <param name="signer">Signs the challenge with the device key.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The welcome: the negotiated version, the features, the policy and the cursors.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The protocol range of <paramref name="options"/> is not valid.</exception>
    /// <exception cref="FleetHandshakeException">The server refused the client (its error), or broke the protocol.</exception>
    /// <exception cref="TimeoutException">The server did not answer within <see cref="FleetHandshakeClientOptions.Timeout"/>.</exception>
    /// <exception cref="IOException">The transport failed or closed.</exception>
    public static async Task<FleetWelcome> ConnectAsync(IFleetTransport transport, FleetHandshakeClientOptions options, IFleetHandshakeSigner signer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(options.Client);
        if (!options.Protocol.IsValid) throw new ArgumentException("The protocol range is not valid.", nameof(options));

        using var timeout = CreateTimeout(options.Timeout, cancellationToken);
        var token = timeout.Token;
        try
        {
            var frame = await ReceiveAsync(transport, token).ConfigureAwait(false);
            if (frame is FleetHandshakeError refused) throw new FleetHandshakeException(refused.Error);
            if (frame is not FleetHelloChallenge challenge || string.IsNullOrEmpty(challenge.Nonce) || challenge.Protocol is null || challenge.Policy is null)
            {
                transport.Abort();
                throw new FleetHandshakeException(new FleetError(FleetErrorCodes.ProtocolError, null, "The first frame of a server must be a hello.challenge."));
            }

            var context = new FleetProofContext(challenge.Nonce, challenge.Time, signer.DeviceId, options.ChannelBinding);
            var proof = await signer.SignAsync(context, token).ConfigureAwait(false);
            var resume = options.ResumeCursors is null ? null : new FleetResume(options.ResumeCursors);
            await SendAsync(transport, new FleetHello(options.Protocol, options.Client, new FleetDeviceProof(signer.DeviceId, proof), resume), token).ConfigureAwait(false);

            frame = await ReceiveAsync(transport, token).ConfigureAwait(false);
            switch (frame)
            {
                case FleetHandshakeError error:
                    throw new FleetHandshakeException(error.Error);
                case FleetWelcome welcome when welcome.Policy is not null && welcome.Protocol >= options.Protocol.Min && welcome.Protocol <= options.Protocol.Max:
                    return welcome with { Features = welcome.Features ?? [] };
                default:
                    transport.Abort();
                    throw new FleetHandshakeException(new FleetError(FleetErrorCodes.ProtocolError, null, "The server did not welcome the client with a version it speaks."));
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            transport.Abort();
            throw new TimeoutException("The server did not complete the fleet handshake in time.", exception);
        }
    }

    private static CancellationTokenSource CreateTimeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != System.Threading.Timeout.InfiniteTimeSpan) source.CancelAfter(timeout);
        return source;
    }

    private static string CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[NonceBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task<FleetHandshakeException> RefuseAsync(IFleetTransport transport, FleetError error, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(transport, new FleetHandshakeError(error), cancellationToken).ConfigureAwait(false);
            await transport.CloseAsync(error.Code, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            // The refusal stands whether or not the peer could read it.
            transport.Abort();
        }

        return new FleetHandshakeException(error);
    }

    private static ValueTask SendAsync(IFleetTransport transport, FleetMessage message, CancellationToken cancellationToken)
        => transport.SendAsync(FleetMessageSerializer.Serialize(message), FleetFrameType.Text, cancellationToken);

    private static async Task<FleetMessage?> ReceiveAsync(IFleetTransport transport, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        var type = await transport.ReceiveAsync(buffer, MaxHandshakeFrameBytes, cancellationToken).ConfigureAwait(false);
        if (type == FleetFrameType.Closed) throw new IOException("The peer closed the connection during the fleet handshake.");
        if (type != FleetFrameType.Text) return null;
        try
        {
            return FleetMessageSerializer.Deserialize(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
