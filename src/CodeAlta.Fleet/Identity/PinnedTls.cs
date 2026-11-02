using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodeAlta.Fleet.Identity;

/// <summary>
/// TLS between two devices over any stream (a socket, a WebSocket, a relayed stream): the server presents its device
/// certificate, the client checks it against a pinned fingerprint only, then the client proves its device key.
/// </summary>
/// <remarks>
/// <para>
/// The client proves its key with a signed challenge bound to the TLS channel (<see cref="DeviceProof"/>), right after
/// the handshake, not with a client certificate (mutual TLS). Mutual TLS was set aside because:
/// SslStream needs a client key it can hand to the platform (SChannel refuses a key that lives only in memory, macOS
/// wants a keychain identity), so every connection would copy the device key into a key store; TLS 1.3 on Windows asks for
/// the client certificate after the handshake in ways that differ across platforms; and the fleet's control connection
/// reaches the server through proxies and tunnels that end TLS, where only an application-level proof survives. One proof
/// for every path is what the protocol's <c>hello</c> carries (doc/fleet/protocol.md, Connections and Handshake).
/// </para>
/// <para>
/// The exchange after the handshake, inside TLS: the server sends <c>"CAFP"</c>, a version byte and a 32-byte nonce; the
/// client sends a version byte, the length (16 bits, big endian) and the bytes of its SubjectPublicKeyInfo, the length
/// (one byte) and the bytes of its signature; the server answers one status byte, 0 when the client is accepted.
/// </para>
/// <para>
/// TLS 1.3 is used where the platform offers it; TLS 1.2 is allowed for macOS, whose SslStream stops there. Both are
/// forward secret with the ECDHE suites a P-256 certificate implies.
/// </para>
/// </remarks>
public static class PinnedTls
{
    private const SslProtocols SupportedProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;
    private const string TargetHost = "codealta-device";
    private const byte ExchangeVersion = 1;
    private const int MaxSubjectPublicKeyInfoLength = 512;
    private const int ChallengeLength = 4 + 1 + DeviceProof.NonceLength;

    private static ReadOnlySpan<byte> ChallengeMagic => "CAFP"u8;

    private enum Status : byte
    {
        Accepted = 0,
        InvalidProof = 1,
        NotTrusted = 2,
        Revoked = 3,
        ProtocolError = 4,
    }

    /// <summary>Accepts a connection from another device.</summary>
    /// <param name="transport">The stream to the client.</param>
    /// <param name="options">The certificate of this device and the devices it trusts.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The connection, with the client's key checked.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="DeviceAuthenticationException">The client did not prove a key this device accepts.</exception>
    /// <exception cref="AuthenticationException">The TLS handshake failed.</exception>
    /// <exception cref="IOException">The transport failed.</exception>
    public static async Task<PinnedTlsConnection> AuthenticateAsServerAsync(
        Stream transport,
        PinnedTlsServerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        var stream = new SslStream(transport, options.LeaveInnerStreamOpen);
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(options.Certificate, additionalCertificates: null, offline: true),
                ClientCertificateRequired = false,
                EnabledSslProtocols = SupportedProtocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);

            var challenge = new byte[ChallengeLength];
            ChallengeMagic.CopyTo(challenge);
            challenge[ChallengeMagic.Length] = ExchangeVersion;
            var nonce = challenge.AsMemory(ChallengeMagic.Length + 1);
            RandomNumberGenerator.Fill(nonce.Span);
            await stream.WriteAsync(challenge, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var header = new byte[3];
            await ReadProofPartAsync(stream, header, cancellationToken).ConfigureAwait(false);
            var keyLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
            if (header[0] != ExchangeVersion || keyLength is 0 or > MaxSubjectPublicKeyInfoLength)
            {
                await RefuseAsync(stream, Status.ProtocolError, cancellationToken).ConfigureAwait(false);
                throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ProtocolError, "The client sent a proof this device does not read.");
            }

            var key = new byte[keyLength];
            await ReadProofPartAsync(stream, key, cancellationToken).ConfigureAwait(false);
            var signatureLength = new byte[1];
            await ReadProofPartAsync(stream, signatureLength, cancellationToken).ConfigureAwait(false);
            if (signatureLength[0] != DeviceProof.SignatureLength)
            {
                await RefuseAsync(stream, Status.ProtocolError, cancellationToken).ConfigureAwait(false);
                throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ProtocolError, "The client sent a proof this device does not read.");
            }

            var signature = new byte[DeviceProof.SignatureLength];
            await ReadProofPartAsync(stream, signature, cancellationToken).ConfigureAwait(false);

            var binding = DeviceProof.ComputeChannelBinding(options.Certificate);
            if (!DeviceProof.Verify(key, binding, nonce.Span, signature, out var fingerprint))
            {
                await RefuseAsync(stream, Status.InvalidProof, cancellationToken).ConfigureAwait(false);
                throw new DeviceAuthenticationException(DeviceAuthenticationFailure.InvalidProof, "The client did not prove its device key.");
            }

            if (options.TrustPolicy.IsRevoked(fingerprint))
            {
                await RefuseAsync(stream, Status.Revoked, cancellationToken).ConfigureAwait(false);
                throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ClientRevoked, "The client's device was revoked.");
            }

            var device = options.TrustPolicy.FindTrusted(fingerprint);
            if (device is null && !options.AllowUnpairedClients)
            {
                await RefuseAsync(stream, Status.NotTrusted, cancellationToken).ConfigureAwait(false);
                throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ClientNotTrusted, "The client's device is not paired with this one.");
            }

            await stream.WriteAsync(new[] { (byte)Status.Accepted }, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new PinnedTlsConnection(stream, fingerprint, device);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Connects to another device.</summary>
    /// <param name="transport">The stream to the server.</param>
    /// <param name="options">This device and the pin of the server.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The connection, with the server's key checked and this device's key proved.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The pinned fingerprint is the default value.</exception>
    /// <exception cref="DeviceAuthenticationException">The server's key is not the pinned or a trusted one, or the server refused this device.</exception>
    /// <exception cref="AuthenticationException">The TLS handshake failed.</exception>
    /// <exception cref="IOException">The transport failed.</exception>
    public static async Task<PinnedTlsConnection> AuthenticateAsClientAsync(
        Stream transport,
        PinnedTlsClientOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Identity);
        if (options.ServerFingerprint.IsEmpty)
        {
            throw new ArgumentException("A pinned server fingerprint is required.", nameof(options));
        }

        DeviceAuthenticationFailure? rejection = null;
        TrustedDevice? server = null;
        var stream = new SslStream(transport, options.LeaveInnerStreamOpen);
        try
        {
            try
            {
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = TargetHost,
                    EnabledSslProtocols = SupportedProtocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    // The pin replaces the chain, the name and the dates: the certificate is only a carrier of the key.
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        rejection = CheckServer(certificate, options, out server);
                        return rejection is null;
                    },
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthenticationException exception) when (rejection is { } failure)
            {
                throw new DeviceAuthenticationException(failure, DescribeServerRejection(failure), exception);
            }

            var serverCertificate = stream.RemoteCertificate
                ?? throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ServerKeyMismatch, "The server presented no certificate.");
            var challenge = new byte[ChallengeLength];
            await ReadProofPartAsync(stream, challenge, cancellationToken).ConfigureAwait(false);
            if (!challenge.AsSpan(0, ChallengeMagic.Length).SequenceEqual(ChallengeMagic) || challenge[ChallengeMagic.Length] != ExchangeVersion)
            {
                throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ProtocolError, "The server sent a challenge this device does not read.");
            }

            var nonce = challenge.AsSpan(ChallengeMagic.Length + 1);
            var signature = DeviceProof.Sign(options.Identity, DeviceProof.ComputeChannelBinding(serverCertificate), nonce);
            var key = options.Identity.ExportSubjectPublicKeyInfo();
            var proof = new byte[3 + key.Length + 1 + signature.Length];
            proof[0] = ExchangeVersion;
            BinaryPrimitives.WriteUInt16BigEndian(proof.AsSpan(1), checked((ushort)key.Length));
            key.CopyTo(proof, 3);
            proof[3 + key.Length] = checked((byte)signature.Length);
            signature.CopyTo(proof, 4 + key.Length);
            await stream.WriteAsync(proof, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var status = new byte[1];
            await ReadProofPartAsync(stream, status, cancellationToken).ConfigureAwait(false);
            return (Status)status[0] switch
            {
                Status.Accepted => new PinnedTlsConnection(stream, options.ServerFingerprint, server),
                Status.InvalidProof => throw new DeviceAuthenticationException(DeviceAuthenticationFailure.InvalidProof, "The server did not accept the proof of this device's key."),
                Status.NotTrusted => throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ClientNotTrusted, "The server does not trust this device."),
                Status.Revoked => throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ClientRevoked, "The server revoked this device."),
                _ => throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ProtocolError, "The server refused the exchange."),
            };
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static DeviceAuthenticationFailure? CheckServer(X509Certificate? certificate, PinnedTlsClientOptions options, out TrustedDevice? server)
    {
        server = null;
        if (certificate is null)
        {
            return DeviceAuthenticationFailure.ServerKeyMismatch;
        }

        DeviceFingerprint presented;
        if (certificate is X509Certificate2 certificate2)
        {
            presented = DeviceFingerprint.FromCertificate(certificate2);
        }
        else
        {
            using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            presented = DeviceFingerprint.FromCertificate(loaded);
        }

        if (!CryptographicOperations.FixedTimeEquals(presented.AsSpan(), options.ServerFingerprint.AsSpan()))
        {
            return DeviceAuthenticationFailure.ServerKeyMismatch;
        }

        if (options.TrustPolicy is { } trust)
        {
            if (trust.IsRevoked(presented))
            {
                return DeviceAuthenticationFailure.ServerRevoked;
            }

            server = trust.FindTrusted(presented);
            if (server is null)
            {
                return DeviceAuthenticationFailure.ServerNotTrusted;
            }
        }

        return null;
    }

    private static string DescribeServerRejection(DeviceAuthenticationFailure failure) => failure switch
    {
        DeviceAuthenticationFailure.ServerRevoked => "The server's device was revoked.",
        DeviceAuthenticationFailure.ServerNotTrusted => "The server's device is not paired with this one.",
        _ => "The server presented another key than the one paired with this device.",
    };

    private static async Task ReadProofPartAsync(SslStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new DeviceAuthenticationException(DeviceAuthenticationFailure.ProtocolError, "The other device closed the connection during the key exchange.", exception);
        }
    }

    private static async Task RefuseAsync(SslStream stream, Status status, CancellationToken cancellationToken)
    {
        try
        {
            await stream.WriteAsync(new[] { (byte)status }, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The client may be gone already; the refusal stands either way.
        }
    }
}
