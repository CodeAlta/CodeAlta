using System.Globalization;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;

namespace CodeAlta.Server.Identity;

/// <summary>What a signed request carries: its method, path, the four headers and the hash of its body.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="PathAndQuery">The path with its query, as received.</param>
/// <param name="DeviceId">The <see cref="FleetSignedRequest.DeviceHeader"/>, or <see langword="null"/>.</param>
/// <param name="Timestamp">The <see cref="FleetSignedRequest.TimestampHeader"/>, or <see langword="null"/>.</param>
/// <param name="Nonce">The <see cref="FleetSignedRequest.NonceHeader"/>, or <see langword="null"/>.</param>
/// <param name="Signature">The <see cref="FleetSignedRequest.SignatureHeader"/>, or <see langword="null"/>.</param>
/// <param name="BodyHash">The hash of the body as received (<see cref="FleetSignedRequest.HashBody"/>).</param>
public sealed record SignedRequestData(string Method, string PathAndQuery, string? DeviceId, string? Timestamp, string? Nonce, string? Signature, string BodyHash)
{
    /// <summary>Reads what a request carries.</summary>
    /// <param name="request">The request.</param>
    /// <param name="bodyHash">The hash of its body.</param>
    /// <returns>The data to verify.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static SignedRequestData From(HttpRequest request, string bodyHash)
    {
        ArgumentNullException.ThrowIfNull(request);
        static string? Header(HttpRequest request, string name) => request.Headers.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
        return new SignedRequestData(request.Method, string.Concat(request.PathBase.Value, request.Path.Value, request.QueryString.Value),
            Header(request, FleetSignedRequest.DeviceHeader), Header(request, FleetSignedRequest.TimestampHeader), Header(request, FleetSignedRequest.NonceHeader),
            Header(request, FleetSignedRequest.SignatureHeader), bodyHash);
    }
}

/// <summary>
/// Checks the proof of possession of a device key on a REST call: the signature of the request with the key of a
/// device of the table, a time close to the server's, and a nonce never seen in that window. There is no bearer
/// secret: what proves a device is its key, on every request.
/// </summary>
public sealed class RequestAuthenticator
{
    private readonly ICoordinatorStore _store;
    private readonly NonceCache _nonces;
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxSkew;

    /// <summary>Initializes a new instance of the <see cref="RequestAuthenticator"/> class.</summary>
    /// <param name="store">The store of the devices.</param>
    /// <param name="nonces">The nonces seen.</param>
    /// <param name="time">The clock.</param>
    /// <param name="maxSkew">How far the time of a request may be from the server's.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public RequestAuthenticator(ICoordinatorStore store, NonceCache nonces, TimeProvider time, TimeSpan maxSkew)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(nonces);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _nonces = nonces;
        _time = time;
        _maxSkew = maxSkew;
    }

    /// <summary>Authenticates a request of an approved device.</summary>
    /// <param name="request">What the request carries.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The device.</returns>
    /// <exception cref="CoordinatorException">The request is not signed, its signature or time is wrong, its nonce was seen, or its device is unknown, pending or revoked.</exception>
    public async ValueTask<DeviceRecord> AuthenticateAsync(SignedRequestData request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timestamp = CheckForm(request);
        var device = await _store.GetDeviceAsync(request.DeviceId!, cancellationToken).ConfigureAwait(false);
        // An unknown device and a wrong signature answer the same, so that the answer does not tell which ids exist.
        if (device is null || !FleetDevicePublicKey.TryParse(device.PublicKey, out var key)) throw SignatureInvalid();
        CheckSignature(request, timestamp, key);
        switch (device.Status)
        {
            case DeviceStatuses.Pending:
                throw CoordinatorException.Forbidden(FleetErrorCodes.PendingApproval, "The device waits for its approval by a controller device.", FleetErrorHints.WaitForApproval);
            case DeviceStatuses.Revoked:
                throw CoordinatorException.Forbidden(FleetErrorCodes.Revoked, "The device was revoked.");
        }

        return device;
    }

    /// <summary>
    /// Checks a request signed by a key that the server does not know yet (a setup, an enrollment): the key comes in
    /// the body, and the device id of the headers must be the one derived from it.
    /// </summary>
    /// <param name="request">What the request carries.</param>
    /// <param name="key">The key of the body.</param>
    /// <exception cref="CoordinatorException">The request is not signed by that key, its time is wrong, or its nonce was seen.</exception>
    public void VerifyWithKey(SignedRequestData request, FleetDevicePublicKey key)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(key);
        var timestamp = CheckForm(request);
        if (!string.Equals(request.DeviceId, key.DeviceId, StringComparison.Ordinal)) throw SignatureInvalid();
        CheckSignature(request, timestamp, key);
    }

    private long CheckForm(SignedRequestData request)
    {
        if (string.IsNullOrEmpty(request.DeviceId) || request.DeviceId.Length > 64 || string.IsNullOrEmpty(request.Signature)
            || !FleetSignedRequest.IsValidNonce(request.Nonce)
            || !long.TryParse(request.Timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            throw CoordinatorException.Unauthenticated(CoordinatorErrorCodes.SignatureMissing,
                $"Sign the request with the device key: {FleetSignedRequest.DeviceHeader}, {FleetSignedRequest.TimestampHeader}, {FleetSignedRequest.NonceHeader} and {FleetSignedRequest.SignatureHeader}.");
        }

        var skew = Math.Abs(_time.GetUtcNow().ToUnixTimeMilliseconds() - timestamp);
        if (skew > _maxSkew.TotalMilliseconds)
        {
            throw CoordinatorException.Unauthenticated(CoordinatorErrorCodes.ClockSkew,
                $"The time of the request is {TimeSpan.FromMilliseconds(skew).TotalSeconds:0} seconds from the server's: set the clock of the device.");
        }

        return timestamp;
    }

    private void CheckSignature(SignedRequestData request, long timestamp, FleetDevicePublicKey key)
    {
        byte[] signed;
        try
        {
            signed = FleetSignedRequest.GetSignedBytes(request.Method, request.PathAndQuery, timestamp, request.Nonce!, request.BodyHash, request.DeviceId!);
        }
        catch (ArgumentException)
        {
            throw SignatureInvalid();
        }

        if (!key.Verify(signed, request.Signature)) throw SignatureInvalid();

        // The nonce is recorded once the signature holds: a forger cannot use up the nonces of a device.
        switch (_nonces.TryRecord(request.DeviceId!, request.Nonce!))
        {
            case NonceResult.Replayed:
                throw CoordinatorException.Unauthenticated(CoordinatorErrorCodes.Replayed, "This request was already received: sign each request with a new nonce.");
            case NonceResult.Full:
                throw new CoordinatorException(StatusCodes.Status503ServiceUnavailable, FleetErrorCodes.ServerBusy, "The server is busy: retry later.", FleetErrorHints.Retry);
        }
    }

    private static CoordinatorException SignatureInvalid()
        => CoordinatorException.Unauthenticated(CoordinatorErrorCodes.SignatureInvalid, "The signature of the request does not verify with the key of its device.");
}
