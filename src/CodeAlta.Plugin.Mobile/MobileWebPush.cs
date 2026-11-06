using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// Sends a Web Push message with the cryptography of .NET alone: the payload encrypted for the browser of the phone
/// (RFC 8291, in the <c>aes128gcm</c> content encoding of RFC 8188), and the request signed with the identity of the
/// application server (VAPID, RFC 8292). Written for CodeAlta, so that what encrypts and signs for the phones depends on
/// no library outside .NET; it follows the protocol as Lib.Net.Http.WebPush (MIT, Tomasz Pęczek) implements it.
/// </summary>
internal sealed class MobileWebPush : IDisposable
{
    // One record holds the whole payload: its size, which the receiver checks, only has to exceed what it carries.
    private const int RecordSize = 4096;
    private const int TagSize = 16;

    // How long a signature of the server stays valid; RFC 8292 allows at most 24 hours.
    private static readonly TimeSpan SignatureLifetime = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly ECDsa _signer;
    private readonly string _publicKey;
    private readonly string _subject;

    /// <summary>Creates a sender.</summary>
    /// <param name="http">The client that posts to the push services; the caller owns it.</param>
    /// <param name="publicKey">The public key of the server, an uncompressed P-256 point in base64url.</param>
    /// <param name="privateKey">The private key of the server, the 32 bytes of its scalar in base64url.</param>
    /// <param name="subject">How the operator of a push service reaches the sender: an <c>https:</c> or <c>mailto:</c> address.</param>
    /// <exception cref="CryptographicException">The keys are not a P-256 key pair.</exception>
    /// <exception cref="FormatException">A key is not base64url.</exception>
    internal MobileWebPush(HttpClient http, string publicKey, string privateKey, string subject)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _publicKey = publicKey;
        _subject = subject;
        var point = Point(Base64Url.DecodeFromChars(publicKey));
        _signer = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = point, D = Base64Url.DecodeFromChars(privateKey) });
    }

    /// <summary>Posts a message to the endpoint of a subscription.</summary>
    /// <param name="endpoint">The endpoint of the subscription, already checked to be a push service.</param>
    /// <param name="p256dh">The public key of the browser, base64url.</param>
    /// <param name="auth">The authentication secret of the browser, base64url.</param>
    /// <param name="payload">What the browser receives.</param>
    /// <param name="timeToLive">How long, in seconds, the push service keeps a message the phone cannot take yet.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="MobileWebPushException">The push service refused the message.</exception>
    /// <exception cref="HttpRequestException">The push service could not be reached.</exception>
    /// <exception cref="CryptographicException">The keys of the subscription are not a P-256 point and a secret.</exception>
    internal async Task SendAsync(string endpoint, string p256dh, string auth, ReadOnlyMemory<byte> payload, int timeToLive, CancellationToken cancellationToken)
    {
        var uri = new Uri(endpoint, UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("TTL", timeToLive.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("Urgency", "high");
        request.Headers.Authorization = new AuthenticationHeaderValue("vapid", $"t={Token($"{uri.Scheme}://{uri.IdnHost}")}, k={_publicKey}");
        var content = new ByteArrayContent(Encrypt(payload.Span, Base64Url.DecodeFromChars(p256dh), Base64Url.DecodeFromChars(auth)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentEncoding.Add("aes128gcm");
        request.Content = content;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // 201 is the answer of RFC 8030; Mozilla answers 200 or 202.
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Accepted))
            throw new MobileWebPushException(response.StatusCode);
    }

    /// <summary>
    /// The body of a message for a browser (RFC 8291): a header with the salt, the record size and the public key of a key
    /// pair made for this message alone, then the payload, its delimiter of last record, encrypted with AES-128-GCM.
    /// </summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> payload, byte[] browserKey, byte[] authSecret)
    {
        if (authSecret.Length != 16) throw new CryptographicException("The authentication secret of the browser is not 16 bytes.");
        if (payload.Length + 1 + TagSize > RecordSize) throw new ArgumentException("The payload does not fit in one record.", nameof(payload));
        using var browser = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = Point(browserKey) });
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPoint = ephemeral.ExportParameters(false).Q;
        byte[] serverKey = [4, .. ephemeralPoint.X!, .. ephemeralPoint.Y!];
        var secret = ephemeral.DeriveRawSecretAgreement(browser.PublicKey);

        // "WebPush: info" 0x00 browser key, server key: the keys of both sides go into the key of the content.
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. browserKey, .. serverKey];
        var keyMaterial = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, authSecret, keyInfo);
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, keyMaterial, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, keyMaterial, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        var header = salt.Length + sizeof(uint) + 1 + serverKey.Length;
        var body = new byte[header + payload.Length + 1 + TagSize];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), RecordSize);
        body[20] = (byte)serverKey.Length;
        serverKey.CopyTo(body, 21);
        var plaintext = new byte[payload.Length + 1];
        payload.CopyTo(plaintext);
        plaintext[^1] = 2;
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, body.AsSpan(header, plaintext.Length), body.AsSpan(header + plaintext.Length, TagSize));
        return body;
    }

    // A JSON Web Token signed with ES256 that names the push service it is for, until when it holds, and who sends.
    private string Token(string audience)
    {
        var expires = DateTimeOffset.UtcNow.Add(SignatureLifetime).ToUnixTimeSeconds();
        var claims = new JsonObject { ["aud"] = audience, ["exp"] = expires, ["sub"] = _subject }.ToJsonString();
        var signed = $"{Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8)}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims))}";
        // .NET signs in the IEEE P1363 format, r then s, which is the format of JWS.
        var signature = _signer.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256);
        return $"{signed}.{Base64Url.EncodeToString(signature)}";
    }

    // An uncompressed P-256 point: 0x04, then X and Y of 32 bytes each.
    private static ECPoint Point(byte[] key)
    {
        if (key is not [4, ..] || key.Length != 65) throw new CryptographicException("The key is not an uncompressed P-256 point.");
        return new ECPoint { X = key[1..33], Y = key[33..] };
    }

    /// <inheritdoc />
    public void Dispose() => _signer.Dispose();
}

/// <summary>A push service refused a message. It holds the status alone: the endpoint and the keys of the phone stay out of it.</summary>
/// <param name="statusCode">The status the push service answered.</param>
internal sealed class MobileWebPushException(HttpStatusCode statusCode) : Exception($"The push service answered {(int)statusCode}.")
{
    /// <summary>The status the push service answered: 404 or 410 when the subscription is gone.</summary>
    internal HttpStatusCode StatusCode { get; } = statusCode;
}
