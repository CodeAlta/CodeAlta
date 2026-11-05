using System.Buffers.Text;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The recovery key of a person at a server (<c>doc/fleet/security-and-privacy.md</c>, keys): 256 random bits the person
/// keeps as 32 words, shown once when it is made, never stored by a device nor sent to the server. It derives a P-256 key
/// that is a member of the device list (<see cref="Protocol.FleetDeviceKinds.Recovery"/>): the person's controller devices
/// wrap their keys to it, and when every device is lost, it signs a new device in and gives it those keys.
/// </summary>
/// <remarks>
/// It also seals the anchor of the device list (the organization and the hash of its first entry), which the server keeps:
/// a device that recovers pins the list by it, so that a server cannot answer another list of its own making.
/// </remarks>
public sealed class FleetRecoveryKey : IDisposable
{
    /// <summary>The length of the secret, in bytes: one word each.</summary>
    public const int Length = 32;

    private const string IdentityDomain = "codealta-recovery-key/1:p256";
    private const string AnchorDomain = "codealta-recovery-anchor/1";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    // The order of P-256: a private scalar is from 1 to this, excluded.
    private static ReadOnlySpan<byte> Order =>
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xBC, 0xE6, 0xFA, 0xAD, 0xA7, 0x17, 0x9E, 0x84, 0xF3, 0xB9, 0xCA, 0xC2, 0xFC, 0x63, 0x25, 0x51,
    ];

    private static readonly FrozenDictionary<string, byte> WordValues =
        PairingWords.Words.Select(static (word, index) => (word, (byte)index)).ToFrozenDictionary(static pair => pair.word, static pair => pair.Item2, StringComparer.OrdinalIgnoreCase);

    private readonly byte[] _secret;

    private FleetRecoveryKey(byte[] secret) => _secret = secret;

    /// <summary>Gets the words of the key, separated by spaces: what the person writes down.</summary>
    public string Words => PairingWords.FromBytes(_secret);

    /// <summary>Makes a new recovery key.</summary>
    /// <returns>The key; dispose it once its words were shown.</returns>
    public static FleetRecoveryKey Generate() => new(RandomNumberGenerator.GetBytes(Length));

    /// <summary>Reads the words of a recovery key, as the person typed them: in any case, separated by spaces, commas, dashes or line breaks.</summary>
    /// <param name="words">The words.</param>
    /// <param name="key">The key, or <see langword="null"/>.</param>
    /// <returns>Whether the text is 32 words of the list.</returns>
    public static bool TryParse(string? words, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FleetRecoveryKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(words) || words.Length > 1024) return false;
        var parts = words.Split([' ', '\t', '\r', '\n', ',', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != Length) return false;
        var secret = new byte[Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!WordValues.TryGetValue(parts[index], out var value)) return false;
            secret[index] = value;
        }

        key = new FleetRecoveryKey(secret);
        return true;
    }

    /// <summary>
    /// Finds the first word, as the person typed them, that is no word of the list of recovery keys: what a typo makes, said by
    /// its position so that the person checks that word without the words being shown again.
    /// </summary>
    /// <param name="words">The words, separated as <see cref="TryParse"/> takes them.</param>
    /// <returns>The position of that word, from 1; 0 when every word is of the list (or there are none).</returns>
    public static int UnknownWordPosition(string? words)
    {
        if (string.IsNullOrWhiteSpace(words) || words.Length > 1024) return 0;
        var parts = words.Split([' ', '\t', '\r', '\n', ',', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            if (!WordValues.ContainsKey(parts[index])) return index + 1;
        }

        return 0;
    }

    /// <summary>Gets the key of the recovery key in the device list: the same for the same words, on any device.</summary>
    /// <returns>The identity, in memory only; dispose it when done.</returns>
    public DeviceIdentity CreateIdentity()
    {
        Span<byte> scalar = stackalloc byte[32];
        try
        {
            // A scalar out of range is so unlikely that the next counter is enough to stay on the same words.
            for (var counter = 0; ; counter++)
            {
                HKDF.DeriveKey(HashAlgorithmName.SHA256, _secret, scalar, salt: [], info: Encoding.UTF8.GetBytes($"{IdentityDomain}:{counter}"));
                if (scalar.IndexOfAnyExcept((byte)0) >= 0 && scalar.SequenceCompareTo(Order) < 0) return DeviceIdentity.FromPrivateKey(scalar);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>Seals the anchor of the device list with the recovery key, for the server to keep.</summary>
    /// <param name="organizationId">The organization of the list.</param>
    /// <param name="firstHash">The hash of its first entry.</param>
    /// <returns>The sealed text.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    public string SealAnchor(string organizationId, string firstHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(firstHash);
        var plain = FleetSealedJson.Write(writer =>
        {
            writer.WriteString("org", organizationId);
            writer.WriteString("root", firstHash);
        });
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedBytes = new byte[plain.Length + TagLength];
        var key = AnchorKey();
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), Encoding.UTF8.GetBytes(AnchorDomain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", FleetMetadataKey.SealAlgorithm);
            writer.WriteString("iv", Base64Url.EncodeToString(nonce));
            writer.WriteString("ct", Base64Url.EncodeToString(sealedBytes));
        }));
    }

    /// <summary>Opens the anchor this recovery key sealed.</summary>
    /// <param name="sealedText">The sealed text, as the server kept it.</param>
    /// <param name="organizationId">The organization of the list, or <see langword="null"/>.</param>
    /// <param name="firstHash">The hash of its first entry, or <see langword="null"/>.</param>
    /// <returns>Whether it opened: another recovery key or a changed text do not.</returns>
    public bool TryOpenAnchor(string? sealedText, out string? organizationId, out string? firstHash)
    {
        organizationId = null;
        firstHash = null;
        if (string.IsNullOrEmpty(sealedText) || sealedText.Length > 4096) return false;
        var key = AnchorKey();
        try
        {
            using var document = JsonDocument.Parse(sealedText);
            var root = document.RootElement;
            if (FleetSealedJson.Text(root, "alg") != FleetMetadataKey.SealAlgorithm) return false;
            var nonce = FleetSealedJson.Bytes(root, "iv");
            var sealedBytes = FleetSealedJson.Bytes(root, "ct");
            if (nonce is not { Length: NonceLength } || sealedBytes is not { Length: > TagLength }) return false;
            var plain = new byte[sealedBytes.Length - TagLength];
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(nonce, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes(AnchorDomain));
            }

            using var opened = JsonDocument.Parse(plain);
            organizationId = FleetSealedJson.Text(opened.RootElement, "org");
            firstHash = FleetSealedJson.Text(opened.RootElement, "root");
            return !string.IsNullOrEmpty(organizationId) && !string.IsNullOrEmpty(firstHash);
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    public void Dispose() => CryptographicOperations.ZeroMemory(_secret);

    private byte[] AnchorKey() => HKDF.DeriveKey(HashAlgorithmName.SHA256, _secret, 32, salt: [], info: Encoding.UTF8.GetBytes(AnchorDomain));
}
