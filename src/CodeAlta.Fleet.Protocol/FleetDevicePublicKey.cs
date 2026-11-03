using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The public key of a device as a server or a peer is given it: an ECDSA P-256 key given as the base64 of its DER
/// SubjectPublicKeyInfo, its fingerprint (the SHA-256 of that encoding) and the device id derived from it, the same
/// way CodeAlta's devices derive theirs, so a device is known by the same id everywhere.
/// </summary>
public sealed class FleetDevicePublicKey
{
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private const int DeviceIdBytes = 16;
    private const int SignatureLength = 64;
    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private readonly byte[] _subjectPublicKeyInfo;

    private FleetDevicePublicKey(byte[] subjectPublicKeyInfo, string text)
    {
        _subjectPublicKeyInfo = subjectPublicKeyInfo;
        Text = text;
        var hash = SHA256.HashData(subjectPublicKeyInfo);
        Fingerprint = Base64Url.EncodeToString(hash);
        DeviceId = EncodeBase32(hash.AsSpan(0, DeviceIdBytes));
    }

    /// <summary>Gets the key as it is stored and sent: the base64 of its SubjectPublicKeyInfo.</summary>
    public string Text { get; }

    /// <summary>Gets the fingerprint of the key: base64url of its SHA-256, 43 characters.</summary>
    public string Fingerprint { get; }

    /// <summary>Gets the id of the device: the first 128 bits of the fingerprint, 26 characters of lower-case base32.</summary>
    public string DeviceId { get; }

    /// <summary>Reads a public key, which must be a P-256 key.</summary>
    /// <param name="text">The base64 of the DER SubjectPublicKeyInfo.</param>
    /// <param name="key">The key read, or <see langword="null"/>.</param>
    /// <returns>Whether the text is a P-256 public key.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out FleetDevicePublicKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024) return false;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return false;
        }

        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || !IsP256(ecdsa)) return false;
        }
        catch (CryptographicException)
        {
            return false;
        }

        key = new FleetDevicePublicKey(bytes, Convert.ToBase64String(bytes));
        return true;
    }

    /// <summary>Gets the key as its DER SubjectPublicKeyInfo, to wrap a key to it (ECDH).</summary>
    /// <returns>A copy of the encoding.</returns>
    public byte[] ExportSubjectPublicKeyInfo() => (byte[])_subjectPublicKeyInfo.Clone();

    /// <summary>Checks a signature made with the private key of this key: ECDSA P-256 over SHA-256, r and s in base64url.</summary>
    /// <param name="data">The signed bytes.</param>
    /// <param name="signature">The signature, base64url of 64 bytes.</param>
    /// <returns>Whether the signature is valid.</returns>
    public bool Verify(ReadOnlySpan<byte> data, string? signature)
    {
        if (string.IsNullOrEmpty(signature) || signature.Length > 128 || !Base64Url.IsValid(signature)) return false;
        Span<byte> bytes = stackalloc byte[96];
        if (!Base64Url.TryDecodeFromChars(signature, bytes, out var written) || written != SignatureLength) return false;
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(_subjectPublicKeyInfo, out _);
        return ecdsa.VerifyData(data, bytes[..SignatureLength], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static bool IsP256(ECDsa key)
    {
        if (key.KeySize != 256) return false;
        var curve = key.ExportParameters(includePrivateParameters: false).Curve;
        return curve.IsNamed && (curve.Oid.Value == P256Oid || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1");
    }

    // RFC 4648 base32 without padding, in lower case: the encoding of device ids.
    private static string EncodeBase32(ReadOnlySpan<byte> data)
    {
        Span<char> chars = stackalloc char[(data.Length * 8 + 4) / 5];
        int buffer = 0, bits = 0, index = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                chars[index++] = Base32Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0) chars[index++] = Base32Alphabet[(buffer << (5 - bits)) & 31];
        return new string(chars[..index]);
    }
}
