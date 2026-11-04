using System.Buffers.Text;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The pin of a server's own certificate (<c>altaserver run --tls self</c>): <c>sha256:</c> and the base64url of the
/// SHA-256 of its public key (SubjectPublicKeyInfo). A client given the pin trusts that key only, whatever the chain, the
/// name or the dates of the certificate, as the fleet pins the keys of nodes; without a pin it validates as usual.
/// </summary>
public static class FleetServerCertificates
{
    /// <summary>The prefix of a pin.</summary>
    public const string PinPrefix = "sha256:";

    /// <summary>Gets the pin of a certificate.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>Its pin.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is null.</exception>
    public static string Pin(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return PinPrefix + Base64Url.EncodeToString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Gets whether a text is a pin.</summary>
    /// <param name="pin">The text.</param>
    /// <returns>Whether it is <c>sha256:</c> and 43 characters of base64url.</returns>
    public static bool IsPin(string? pin)
        => pin is { Length: 50 } && pin.StartsWith(PinPrefix, StringComparison.Ordinal) && pin.AsSpan(PinPrefix.Length).IndexOfAnyExcept(Base64UrlCharacters) < 0;

    /// <summary>Gets whether a certificate a server presented is the one a pin names.</summary>
    /// <param name="certificate">The certificate, or null.</param>
    /// <param name="pin">The pin.</param>
    /// <returns>Whether its key is the pinned one.</returns>
    public static bool Matches(X509Certificate? certificate, string pin)
    {
        if (certificate is null || !IsPin(pin)) return false;
        string presented;
        if (certificate is X509Certificate2 full)
        {
            presented = Pin(full);
        }
        else
        {
            using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            presented = Pin(loaded);
        }

        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(presented), System.Text.Encoding.ASCII.GetBytes(pin));
    }

    /// <summary>Gets the HTTP handler of a pinned server: its certificate is checked against the pin only.</summary>
    /// <param name="pin">The pin.</param>
    /// <returns>The handler; the caller disposes it.</returns>
    /// <exception cref="ArgumentException"><paramref name="pin"/> is not a pin.</exception>
    public static SocketsHttpHandler CreateHandler(string pin)
    {
        if (!IsPin(pin)) throw new ArgumentException($"'{pin}' is not the pin of a certificate.", nameof(pin));
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, pin) },
        };
    }

    /// <summary>Configures a WebSocket to a pinned server (the control connection, a relay); nothing without a pin.</summary>
    /// <param name="options">The options of the socket.</param>
    /// <param name="pin">The pin, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static void Configure(ClientWebSocketOptions options, string? pin)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (pin is null) return;
        options.RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, pin);
    }

    private static ReadOnlySpan<char> Base64UrlCharacters => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
}
