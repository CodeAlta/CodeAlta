using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodeAlta.Fleet.Identity;

/// <summary>
/// The proof that a client holds its device key, bound to one TLS channel: an ECDSA P-256 signature over a nonce of the
/// server and the channel binding of the connection (the SHA-256 of the server's certificate, as
/// <c>tls-server-end-point</c> of RFC 5929).
/// </summary>
/// <remarks>
/// The client pins the server's key, so the certificate it saw is the certificate of the device it meant to reach. A
/// device that receives a proof cannot replay it to another one: its certificate, hence the binding, differs. The
/// <c>hello</c> of the fleet protocol carries the same proof.
/// </remarks>
public static class DeviceProof
{
    /// <summary>The length of a challenge nonce in bytes.</summary>
    public const int NonceLength = 32;

    /// <summary>The length of a channel binding in bytes.</summary>
    public const int ChannelBindingLength = 32;

    /// <summary>The length of a signature in bytes: r and s of P-256.</summary>
    public const int SignatureLength = 64;

    private static ReadOnlySpan<byte> Context => "CodeAlta fleet device proof v1\0"u8;

    /// <summary>Computes the channel binding of a connection from the certificate of its server.</summary>
    /// <param name="serverCertificate">The certificate the server presented.</param>
    /// <returns>The SHA-256 of the certificate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serverCertificate"/> is <see langword="null"/>.</exception>
    public static byte[] ComputeChannelBinding(X509Certificate serverCertificate)
    {
        ArgumentNullException.ThrowIfNull(serverCertificate);
        return SHA256.HashData(serverCertificate.GetRawCertData());
    }

    /// <summary>Signs a challenge with a device key.</summary>
    /// <param name="identity">The device.</param>
    /// <param name="channelBinding">The channel binding of the connection.</param>
    /// <param name="nonce">The nonce the server sent.</param>
    /// <returns>The signature.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The binding or the nonce does not have its length.</exception>
    public static byte[] Sign(DeviceIdentity identity, ReadOnlySpan<byte> channelBinding, ReadOnlySpan<byte> nonce)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var message = BuildMessage(identity.Fingerprint, channelBinding, nonce);
        return identity.SignData(message);
    }

    /// <summary>Checks a proof.</summary>
    /// <param name="subjectPublicKeyInfo">The public key the client presented, as a DER SubjectPublicKeyInfo.</param>
    /// <param name="channelBinding">The channel binding of the connection, as the server computes it.</param>
    /// <param name="nonce">The nonce the server sent.</param>
    /// <param name="signature">The signature the client sent.</param>
    /// <param name="fingerprint">The fingerprint of the key when the proof is valid, else the default value.</param>
    /// <returns>Whether the key is a P-256 key and the signature is valid.</returns>
    public static bool Verify(
        ReadOnlySpan<byte> subjectPublicKeyInfo,
        ReadOnlySpan<byte> channelBinding,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> signature,
        out DeviceFingerprint fingerprint)
    {
        fingerprint = default;
        if (subjectPublicKeyInfo.IsEmpty
            || channelBinding.Length != ChannelBindingLength
            || nonce.Length != NonceLength
            || signature.Length != SignatureLength)
        {
            return false;
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read);
            if (read != subjectPublicKeyInfo.Length || !DeviceIdentity.IsP256(key))
            {
                return false;
            }
        }
        catch (CryptographicException)
        {
            return false;
        }

        var candidate = DeviceFingerprint.FromSubjectPublicKeyInfo(subjectPublicKeyInfo);
        var message = BuildMessage(candidate, channelBinding, nonce);
        if (!key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return false;
        }

        fingerprint = candidate;
        return true;
    }

    private static byte[] BuildMessage(DeviceFingerprint client, ReadOnlySpan<byte> channelBinding, ReadOnlySpan<byte> nonce)
    {
        if (channelBinding.Length != ChannelBindingLength)
        {
            throw new ArgumentException($"A channel binding is {ChannelBindingLength} bytes long.", nameof(channelBinding));
        }

        if (nonce.Length != NonceLength)
        {
            throw new ArgumentException($"A nonce is {NonceLength} bytes long.", nameof(nonce));
        }

        var message = new byte[Context.Length + ChannelBindingLength + NonceLength + DeviceFingerprint.Length];
        var span = message.AsSpan();
        Context.CopyTo(span);
        span = span[Context.Length..];
        channelBinding.CopyTo(span);
        span = span[ChannelBindingLength..];
        nonce.CopyTo(span);
        client.AsSpan().CopyTo(span[NonceLength..]);
        return message;
    }
}
