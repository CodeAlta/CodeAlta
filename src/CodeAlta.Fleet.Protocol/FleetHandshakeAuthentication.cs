using System.Text;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// What a device proof signs: the nonce of the challenge, the channel binding and the device id.
/// </summary>
/// <remarks>
/// Both sides build the same bytes with <see cref="GetSignedBytes"/>, so that the code of the device keys signs and
/// verifies them without knowing the protocol.
/// </remarks>
public sealed class FleetProofContext
{
    private const string Domain = "codealta-fleet-proof/1";

    /// <summary>Initializes a new instance of the <see cref="FleetProofContext"/> class.</summary>
    /// <param name="nonce">The nonce of the challenge.</param>
    /// <param name="time">The time of the challenge.</param>
    /// <param name="deviceId">The id of the device that proves itself.</param>
    /// <param name="channelBinding">The binding of the channel (from TLS), or empty when there is none.</param>
    /// <exception cref="ArgumentException"><paramref name="nonce"/> or <paramref name="deviceId"/> is null or empty.</exception>
    public FleetProofContext(string nonce, DateTimeOffset time, string deviceId, ReadOnlyMemory<byte> channelBinding)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        Nonce = nonce;
        Time = time;
        DeviceId = deviceId;
        ChannelBinding = channelBinding;
    }

    /// <summary>Gets the nonce of the challenge.</summary>
    public string Nonce { get; }

    /// <summary>Gets the time of the challenge, for a verifier that checks clock skew; the proof does not sign it.</summary>
    public DateTimeOffset Time { get; }

    /// <summary>Gets the id of the device that proves itself.</summary>
    public string DeviceId { get; }

    /// <summary>Gets the binding of the channel, or empty when there is none.</summary>
    public ReadOnlyMemory<byte> ChannelBinding { get; }

    /// <summary>Gets the bytes the proof signs.</summary>
    /// <returns>The UTF-8 of the domain, the nonce, the device id and the base64 of the channel binding, one per line.</returns>
    public byte[] GetSignedBytes()
        => Encoding.UTF8.GetBytes($"{Domain}\n{Nonce}\n{DeviceId}\n{Convert.ToBase64String(ChannelBinding.Span)}");
}

/// <summary>
/// The client side of the device proof: the device keys sign the challenge.
/// </summary>
public interface IFleetHandshakeSigner
{
    /// <summary>Gets the id of the device.</summary>
    string DeviceId { get; }

    /// <summary>Signs a challenge with the device key.</summary>
    /// <param name="context">What to sign (<see cref="FleetProofContext.GetSignedBytes"/>).</param>
    /// <param name="cancellationToken">Cancels the signature.</param>
    /// <returns>The proof, as text (such as the base64url of the signature).</returns>
    ValueTask<string> SignAsync(FleetProofContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The server side of the device proof: checks the signature and the standing of the device.
/// </summary>
public interface IFleetHandshakeVerifier
{
    /// <summary>Checks the proof of a client.</summary>
    /// <param name="hello">The hello of the client.</param>
    /// <param name="context">What the proof should sign.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Whether the client is accepted, or the error it is refused with.</returns>
    ValueTask<FleetProofVerification> VerifyAsync(FleetHello hello, FleetProofContext context, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="IFleetHandshakeVerifier.VerifyAsync"/>.</summary>
public sealed class FleetProofVerification
{
    private FleetProofVerification(FleetError? error) => Error = error;

    /// <summary>Gets whether the client is accepted.</summary>
    public bool IsAccepted => Error is null;

    /// <summary>Gets the error a refused client gets, or <see langword="null"/> when it is accepted.</summary>
    public FleetError? Error { get; }

    /// <summary>Accepts the client.</summary>
    /// <returns>An acceptance.</returns>
    public static FleetProofVerification Accept() => new(null);

    /// <summary>Refuses the client, such as with <see cref="FleetErrorCodes.Unauthenticated"/>,
    /// <see cref="FleetErrorCodes.PendingApproval"/> or <see cref="FleetErrorCodes.Revoked"/>.</summary>
    /// <param name="error">The error the client gets.</param>
    /// <returns>A refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static FleetProofVerification Reject(FleetError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new FleetProofVerification(error);
    }
}
