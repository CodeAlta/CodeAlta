using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The proof of possession of a device key on a call to the server's REST API: the device signs the method, the path,
/// a timestamp, a nonce and the hash of the body of each request (in the manner of DPoP, RFC 9449), so that no bearer
/// secret exists to steal and a captured request cannot be replayed or changed.
/// </summary>
/// <remarks>
/// <para>
/// A request carries four headers: <see cref="DeviceHeader"/> (the device id), <see cref="TimestampHeader"/> (Unix time
/// in milliseconds), <see cref="NonceHeader"/> (a random value, base64url) and <see cref="SignatureHeader"/> (the
/// ECDSA P-256 signature of <see cref="GetSignedBytes"/>, as the 64 bytes of r and s in base64url).
/// </para>
/// <para>
/// The path is the one the client sent, with its query; the host is not signed, because proxies in front of the
/// server (Tailscale Serve, Caddy, a load balancer) may change it. Both sides build the same bytes with
/// <see cref="GetSignedBytes"/>, so the code of the device keys signs and verifies them without knowing HTTP.
/// </para>
/// </remarks>
public static class FleetSignedRequest
{
    /// <summary>The header that names the device.</summary>
    public const string DeviceHeader = "Alta-Device";

    /// <summary>The header of the time of the request, in Unix milliseconds.</summary>
    public const string TimestampHeader = "Alta-Timestamp";

    /// <summary>The header of the nonce of the request.</summary>
    public const string NonceHeader = "Alta-Nonce";

    /// <summary>The header of the signature.</summary>
    public const string SignatureHeader = "Alta-Signature";

    /// <summary>The fewest characters of a nonce: 16 random bytes in base64url.</summary>
    public const int MinNonceLength = 22;

    /// <summary>The most characters of a nonce.</summary>
    public const int MaxNonceLength = 128;

    private const string Domain = "codealta-fleet-request/1";

    /// <summary>Gets the bytes the device signs for one request.</summary>
    /// <param name="method">The HTTP method, such as <c>POST</c>; compared in upper case.</param>
    /// <param name="pathAndQuery">The path of the request with its query, such as <c>/api/v1/devices?limit=10</c>.</param>
    /// <param name="timestampMilliseconds">The time of the request, in Unix milliseconds.</param>
    /// <param name="nonce">The nonce of the request.</param>
    /// <param name="bodyHash">The hash of the body (<see cref="HashBody"/>).</param>
    /// <param name="deviceId">The id of the device that signs.</param>
    /// <returns>The UTF-8 of the domain and the fields, one per line.</returns>
    /// <exception cref="ArgumentException">A text argument is null or empty, or holds a line break.</exception>
    public static byte[] GetSignedBytes(string method, string pathAndQuery, long timestampMilliseconds, string nonce, string bodyHash, string deviceId)
    {
        RequireLine(method, nameof(method));
        RequireLine(pathAndQuery, nameof(pathAndQuery));
        RequireLine(nonce, nameof(nonce));
        RequireLine(bodyHash, nameof(bodyHash));
        RequireLine(deviceId, nameof(deviceId));
        return Encoding.UTF8.GetBytes(
            $"{Domain}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{timestampMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{nonce}\n{bodyHash}\n{deviceId}");
    }

    /// <summary>Hashes the body of a request: the base64url of its SHA-256, the hash of no bytes for a request without one.</summary>
    /// <param name="body">The bytes of the body, exactly as sent.</param>
    /// <returns>The hash, 43 characters of base64url.</returns>
    public static string HashBody(ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, hash);
        return Base64Url.EncodeToString(hash);
    }

    /// <summary>Makes a new nonce: 16 random bytes in base64url.</summary>
    /// <returns>The nonce.</returns>
    public static string CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>Gets whether a text can be a nonce: base64url, between <see cref="MinNonceLength"/> and <see cref="MaxNonceLength"/> characters.</summary>
    /// <param name="nonce">The text.</param>
    /// <returns>Whether it has the form of a nonce.</returns>
    public static bool IsValidNonce(string? nonce)
        => nonce is { Length: >= MinNonceLength and <= MaxNonceLength } && Base64Url.IsValid(nonce);

    private static void RequireLine(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        if (value.AsSpan().IndexOfAny('\n', '\r') >= 0) throw new ArgumentException("A signed field cannot hold a line break.", name);
    }
}
