using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The key of a provider of a team or of the organization (<c>doc/fleet/orchestration.md</c>, credentials of unattended
/// runs): a device of someone who answers for that scope seals it with a key made for this version of the provider, and wraps
/// that key to each machine of the scope, signed by itself. A machine opens its copy only when the signer answers for the
/// owner of the provider, as the signed access document it verified says; the server never reads the key.
/// </summary>
public static class FleetProviderKeys
{
    private const string ContentDomain = "codealta-provider-content/1";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly FleetGrantKeys.Purpose Providers = new("codealta-provider-key/1", "codealta-provider-key-sign/1");

    /// <summary>Seals the key of a provider for the machines of its scope.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="version">Its version, which a new key or definition makes.</param>
    /// <param name="apiKey">The key of the account.</param>
    /// <param name="recipients">The machines of the scope, from the device list this device verified.</param>
    /// <param name="listSequence">The entry of the device list this device read.</param>
    /// <param name="signer">The controller device of someone who answers for the scope.</param>
    /// <returns>The sealed key, and the copy of its sealing key for each machine.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An id or the key is empty, or no machine receives it.</exception>
    public static (string Sealed, IReadOnlyList<(string DeviceId, string Wrapped)> Wraps) Seal(string organizationId, string providerId, long version, string apiKey,
        IReadOnlyList<FleetDevicePublicKey> recipients, long listSequence, DeviceIdentity signer)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(signer);
        if (recipients.Count == 0) throw new ArgumentException("A provider gives its key to at least one machine.", nameof(recipients));
        var key = FleetMetadataKey.Generate();
        var plain = Encoding.UTF8.GetBytes(apiKey);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipher = new byte[plain.Length + TagLength];
        using (var aes = new AesGcm(key.Material, TagLength))
            aes.Encrypt(nonce, plain, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), Associated(key.Id, organizationId, providerId, version));
        CryptographicOperations.ZeroMemory(plain);
        var sealedText = Encoding.UTF8.GetString(FleetSealedJson.Write(writer =>
        {
            writer.WriteString("alg", FleetMetadataKey.SealAlgorithm);
            writer.WriteString("kid", key.Id);
            writer.WriteString("iv", Base64Url.EncodeToString(nonce));
            writer.WriteString("ct", Base64Url.EncodeToString(cipher));
        }));
        var subject = Subject(version);
        var wraps = new List<(string, string)>(recipients.Count);
        foreach (var recipient in recipients)
            wraps.Add((recipient.DeviceId, FleetGrantKeys.Wrap(key, recipient, organizationId, providerId, subject, Math.Max(1, listSequence), signer, Providers)));
        return (sealedText, wraps);
    }

    /// <summary>Opens the key of a provider on a machine it was sealed for, once its copy holds.</summary>
    /// <param name="sealedText">The sealed key.</param>
    /// <param name="wrapped">This machine's copy of its sealing key.</param>
    /// <param name="organizationId">The organization.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="version">Its version.</param>
    /// <param name="ownerKind">The kind of its owner: a team or the organization.</param>
    /// <param name="ownerId">Its owner.</param>
    /// <param name="identity">The key of this machine.</param>
    /// <param name="list">The device list as this machine verified it.</param>
    /// <param name="access">The access document as this machine verified it.</param>
    /// <returns>The key of the account.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">The copy is not for this machine, provider and version, is not signed by someone who answers for its owner, or does not open.</exception>
    public static string Open(string sealedText, string wrapped, string organizationId, string providerId, long version, string ownerKind, string ownerId, DeviceIdentity identity,
        FleetDeviceListState list, FleetAccessState access)
    {
        ArgumentNullException.ThrowIfNull(sealedText);
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(access);
        var key = FleetGrantKeys.Unwrap(wrapped, organizationId, providerId, Subject(version), identity, list, signer => access.Answers(ownerKind, ownerId, signer.OwnerId), Providers);
        try
        {
            using var document = JsonDocument.Parse(sealedText);
            var root = document.RootElement;
            if (FleetSealedJson.Text(root, "alg") != FleetMetadataKey.SealAlgorithm || FleetSealedJson.Text(root, "kid") != key.Id)
                throw new InvalidDataException("The key of the provider is not sealed with the key of its copy.");
            var nonce = Base64Url.DecodeFromChars(FleetSealedJson.Text(root, "iv") ?? string.Empty);
            var cipher = Base64Url.DecodeFromChars(FleetSealedJson.Text(root, "ct") ?? string.Empty);
            if (nonce.Length != NonceLength || cipher.Length <= TagLength) throw new InvalidDataException("The key of the provider does not read.");
            var plain = new byte[cipher.Length - TagLength];
            try
            {
                using (var aes = new AesGcm(key.Material, TagLength))
                    aes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain, Associated(key.Id, organizationId, providerId, version));
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            throw new InvalidDataException("The key of the provider does not open with its copy.", exception);
        }
    }

    private static string Subject(long version) => "version:" + version.ToString(CultureInfo.InvariantCulture);

    private static byte[] Associated(string keyId, string organizationId, string providerId, long version)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{ContentDomain}\n{FleetMetadataKey.SealAlgorithm}\n{keyId}\n{organizationId}\n{providerId}\n{version}"));
}
