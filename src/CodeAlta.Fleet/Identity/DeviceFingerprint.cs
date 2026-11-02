using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CodeAlta.Fleet.Identity;

/// <summary>
/// The fingerprint of a device key: the SHA-256 of its SubjectPublicKeyInfo. Pairing pins it, the trusted devices list
/// it, and TLS checks it instead of a certificate chain.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> is the canonical form, base64url without padding (43 characters), used in pairing payloads and
/// files. <see cref="ToDisplayString"/> and <see cref="ToShortWords()"/> are for people to compare.
/// </remarks>
public readonly struct DeviceFingerprint : IEquatable<DeviceFingerprint>
{
    /// <summary>The length of a fingerprint in bytes.</summary>
    public const int Length = 32;

    private const int CanonicalLength = 43;
    private const int DisplayBytes = 20;
    private const int DefaultWordCount = 6;

    private readonly byte[]? _bytes;

    private DeviceFingerprint(byte[] bytes) => _bytes = bytes;

    /// <summary>Gets whether this is the default value, which names no key.</summary>
    public bool IsEmpty => _bytes is null;

    /// <summary>Computes the fingerprint of a public key given as a DER SubjectPublicKeyInfo.</summary>
    /// <param name="subjectPublicKeyInfo">The DER encoding of the public key.</param>
    /// <returns>The fingerprint.</returns>
    /// <exception cref="ArgumentException">The key is empty.</exception>
    public static DeviceFingerprint FromSubjectPublicKeyInfo(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        if (subjectPublicKeyInfo.IsEmpty)
        {
            throw new ArgumentException("A public key is required.", nameof(subjectPublicKeyInfo));
        }

        return new DeviceFingerprint(SHA256.HashData(subjectPublicKeyInfo));
    }

    /// <summary>Computes the fingerprint of the public key of a certificate.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The fingerprint of its public key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is <see langword="null"/>.</exception>
    public static DeviceFingerprint FromCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return FromSubjectPublicKeyInfo(certificate.PublicKey.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Makes a fingerprint from its 32 bytes.</summary>
    /// <param name="bytes">The SHA-256 value.</param>
    /// <returns>The fingerprint.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not 32 bytes long.</exception>
    public static DeviceFingerprint FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"A fingerprint is {Length} bytes long.", nameof(bytes));
        }

        return new DeviceFingerprint(bytes.ToArray());
    }

    /// <summary>Reads the canonical form written by <see cref="ToString"/>.</summary>
    /// <param name="text">The text to read.</param>
    /// <param name="fingerprint">The fingerprint read, or the default value.</param>
    /// <returns>Whether <paramref name="text"/> is a canonical fingerprint.</returns>
    public static bool TryParse(string? text, out DeviceFingerprint fingerprint)
    {
        fingerprint = default;
        if (text is not { Length: CanonicalLength } || !Base64Url.IsValid(text))
        {
            return false;
        }

        var bytes = Base64Url.DecodeFromChars(text);
        if (bytes.Length != Length)
        {
            return false;
        }

        fingerprint = new DeviceFingerprint(bytes);
        // One encoding only: the unused low bits of the last character must be zero.
        return string.Equals(fingerprint.ToString(), text, StringComparison.Ordinal);
    }

    /// <summary>Reads the canonical form written by <see cref="ToString"/>.</summary>
    /// <param name="text">The text to read.</param>
    /// <returns>The fingerprint.</returns>
    /// <exception cref="FormatException"><paramref name="text"/> is not a canonical fingerprint.</exception>
    public static DeviceFingerprint Parse(string text)
        => TryParse(text, out var fingerprint) ? fingerprint : throw new FormatException("The text is not a device fingerprint.");

    /// <summary>Gets the 32 bytes of the fingerprint, empty for the default value.</summary>
    /// <returns>The bytes.</returns>
    public ReadOnlySpan<byte> AsSpan() => _bytes;

    /// <summary>Gets the canonical form: base64url without padding.</summary>
    /// <returns>The canonical text, empty for the default value.</returns>
    public override string ToString() => _bytes is null ? string.Empty : Base64Url.EncodeToString(_bytes);

    /// <summary>
    /// Gets the form people read: the first 160 bits in base32, in eight groups of four characters
    /// (<c>ABCD EFGH IJKL MNOP QRST UVWX YZ23 4567</c>).
    /// </summary>
    /// <returns>The text to show, empty for the default value.</returns>
    public string ToDisplayString()
    {
        if (_bytes is null)
        {
            return string.Empty;
        }

        var encoded = Base32.Encode(_bytes.AsSpan(0, DisplayBytes));
        var builder = new StringBuilder(encoded.Length + encoded.Length / 4);
        for (var index = 0; index < encoded.Length; index += 4)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(encoded, index, Math.Min(4, encoded.Length - index));
        }

        return builder.ToString();
    }

    /// <summary>Gets six words that stand for this fingerprint, for two people to compare aloud.</summary>
    /// <returns>The words separated by spaces, empty for the default value.</returns>
    public string ToShortWords() => _bytes is null ? string.Empty : PairingWords.FromBytes(_bytes.AsSpan(0, DefaultWordCount));

    /// <summary>
    /// Gets the words both devices of a pairing show: they depend on the two fingerprints and not on their order, so
    /// both screens show the same words when each holds the key of the other.
    /// </summary>
    /// <param name="first">The fingerprint of one device.</param>
    /// <param name="second">The fingerprint of the other device.</param>
    /// <returns>Six words separated by spaces.</returns>
    /// <exception cref="ArgumentException">One of the fingerprints is the default value.</exception>
    public static string ComparisonWords(DeviceFingerprint first, DeviceFingerprint second)
    {
        if (first.IsEmpty || second.IsEmpty)
        {
            throw new ArgumentException("Both fingerprints are required.");
        }

        var (low, high) = first.AsSpan().SequenceCompareTo(second.AsSpan()) <= 0 ? (first, second) : (second, first);
        var context = "CodeAlta pairing words v1\0"u8;
        Span<byte> input = stackalloc byte[context.Length + 2 * Length];
        context.CopyTo(input);
        low.AsSpan().CopyTo(input[context.Length..]);
        high.AsSpan().CopyTo(input[(context.Length + Length)..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return PairingWords.FromBytes(hash[..DefaultWordCount]);
    }

    /// <inheritdoc />
    public bool Equals(DeviceFingerprint other) => AsSpan().SequenceEqual(other.AsSpan());

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DeviceFingerprint other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _bytes is null ? 0 : BitConverter.ToInt32(_bytes, 0);

    /// <summary>Compares two fingerprints.</summary>
    /// <param name="left">The first fingerprint.</param>
    /// <param name="right">The second fingerprint.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(DeviceFingerprint left, DeviceFingerprint right) => left.Equals(right);

    /// <summary>Compares two fingerprints.</summary>
    /// <param name="left">The first fingerprint.</param>
    /// <param name="right">The second fingerprint.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(DeviceFingerprint left, DeviceFingerprint right) => !left.Equals(right);
}
