using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// What a machine installs with the key of a provider of a scope: its owner, its name, its type, its endpoint and its models.
/// The copy of the key is signed over it, so that a server that keeps a genuine copy cannot send the key, or the sessions of
/// the machine, to another endpoint.
/// </summary>
/// <param name="OwnerKind">A team or the organization.</param>
/// <param name="OwnerId">Its owner.</param>
/// <param name="Name">The name people see.</param>
/// <param name="Type">The type of provider.</param>
/// <param name="Endpoint">Its endpoint; null for the vendor's own.</param>
/// <param name="Models">The models it offers, in order: the first is its default.</param>
public sealed record FleetProviderDefinition(string OwnerKind, string OwnerId, string Name, string Type, string? Endpoint, IReadOnlyList<string> Models)
{
    private const string Domain = "codealta-provider-definition/1";

    /// <summary>Gets the definition of a provider as a server describes it.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>Its definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public static FleetProviderDefinition Of(Protocol.Api.ScopeProviderDto provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new(provider.OwnerKind, provider.OwnerId, provider.Name, provider.Type, provider.Endpoint, provider.Models ?? []);
    }

    /// <summary>Gets the hash of the definition: a JSON array of its fields in a fixed order, under a domain of its own.</summary>
    /// <returns>The hash, base64url.</returns>
    public string Digest()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(Domain);
            writer.WriteStringValue(OwnerKind);
            writer.WriteStringValue(OwnerId);
            writer.WriteStringValue(Name);
            writer.WriteStringValue(Type);
            if (Endpoint is null) writer.WriteNullValue();
            else writer.WriteStringValue(Endpoint);
            writer.WriteStartArray();
            foreach (var model in Models ?? []) writer.WriteStringValue(model);
            writer.WriteEndArray();
            writer.WriteEndArray();
        }

        return Base64Url.EncodeToString(SHA256.HashData(buffer.WrittenSpan));
    }
}

/// <summary>
/// The key of a provider of a team or of the organization (<c>doc/fleet/orchestration.md</c>, credentials of unattended
/// runs): a device of someone who answers for that scope seals it with a key made for this version of the provider, and wraps
/// that key to each machine of the scope, signing each copy over the version and the whole definition of the provider. A
/// machine opens its copy only when the signer answers for the owner of the provider, as the signed access document it verified
/// says, and only for the definition it installs; the server never reads the key.
/// </summary>
public static class FleetProviderKeys
{
    private const string ContentDomain = "codealta-provider-content/1";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly FleetGrantKeys.Purpose Providers = new("codealta-provider-key/1", "codealta-provider-key-sign/1");

    /// <summary>Seals the key of a provider for the machines of its scope, bound to its definition.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="version">Its version, which a new key or definition makes.</param>
    /// <param name="definition">What the machines install with the key, as the server will describe it.</param>
    /// <param name="apiKey">The key of the account.</param>
    /// <param name="recipients">The machines of the scope, from the device list this device verified.</param>
    /// <param name="listSequence">The entry of the device list this device read.</param>
    /// <param name="signer">The controller device of someone who answers for the scope.</param>
    /// <returns>The sealed key, and the copy of its sealing key for each machine.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An id or the key is empty, or no machine receives it.</exception>
    public static (string Sealed, IReadOnlyList<(string DeviceId, string Wrapped)> Wraps) Seal(string organizationId, string providerId, long version,
        FleetProviderDefinition definition, string apiKey, IReadOnlyList<FleetDevicePublicKey> recipients, long listSequence, DeviceIdentity signer)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        ArgumentNullException.ThrowIfNull(definition);
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
        var subject = Subject(version, definition);
        var wraps = new List<(string, string)>(recipients.Count);
        foreach (var recipient in recipients)
            wraps.Add((recipient.DeviceId, FleetGrantKeys.Wrap(key, recipient, organizationId, providerId, subject, Math.Max(1, listSequence), signer, Providers)));
        return (sealedText, wraps);
    }

    /// <summary>Opens the key of a provider on a machine it was sealed for, once its copy holds for the definition the machine installs.</summary>
    /// <param name="sealedText">The sealed key.</param>
    /// <param name="wrapped">This machine's copy of its sealing key.</param>
    /// <param name="organizationId">The organization.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="version">Its version.</param>
    /// <param name="definition">What the machine installs with the key: its owner, name, type, endpoint and models.</param>
    /// <param name="identity">The key of this machine.</param>
    /// <param name="list">The device list as this machine verified it.</param>
    /// <param name="access">The access document as this machine verified it.</param>
    /// <returns>The key of the account.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The copy is not for this machine, provider, version and definition, is not signed by someone who answers for its owner, or does not open.
    /// </exception>
    public static string Open(string sealedText, string wrapped, string organizationId, string providerId, long version, FleetProviderDefinition definition,
        DeviceIdentity identity, FleetDeviceListState list, FleetAccessState access)
        => Open(sealedText, wrapped, organizationId, providerId, version, definition, identity, list, access, signedNow: false);

    /// <summary>
    /// Opens the key of a provider as
    /// <see cref="Open(string, string, string, string, long, FleetProviderDefinition, DeviceIdentity, FleetDeviceListState, FleetAccessState)"/>
    /// does; for a version the machine takes for the first time, also only when the device that signed the copy is still a
    /// controller device of the device list as this machine holds it now, of the same person: a device removed since cannot give a
    /// new version against the entry of the list it signed at. A copy of a version already installed holds against that entry.
    /// </summary>
    /// <param name="sealedText">The sealed key.</param>
    /// <param name="wrapped">This machine's copy of its sealing key.</param>
    /// <param name="organizationId">The organization.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="version">Its version.</param>
    /// <param name="definition">What the machine installs with the key: its owner, name, type, endpoint and models.</param>
    /// <param name="identity">The key of this machine.</param>
    /// <param name="list">The device list as this machine verified it.</param>
    /// <param name="access">The access document as this machine verified it.</param>
    /// <param name="signedNow">Whether the signer must hold its device now: true for a version this machine never installed.</param>
    /// <returns>The key of the account.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The copy is not for this machine, provider, version and definition, is not signed by someone who answers for its owner, or does not open.
    /// </exception>
    public static string Open(string sealedText, string wrapped, string organizationId, string providerId, long version, FleetProviderDefinition definition,
        DeviceIdentity identity, FleetDeviceListState list, FleetAccessState access, bool signedNow)
    {
        ArgumentNullException.ThrowIfNull(sealedText);
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(access);
        var key = FleetGrantKeys.Unwrap(wrapped, organizationId, providerId, Subject(version, definition), identity, list,
            signer => access.Answers(definition.OwnerKind, definition.OwnerId, signer.OwnerId) && (!signedNow || SignsNow(list, signer)), Providers);
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

    // Whether the device that signed a copy is a controller device of the list as it is now, not removed, of the same person.
    private static bool SignsNow(FleetDeviceListState list, FleetDeviceListMember signer)
        => !list.Removed.Contains(signer.DeviceId) && list.Members.TryGetValue(signer.DeviceId, out var now) && now.Kind == FleetDeviceKinds.Controller
            && string.Equals(now.OwnerId, signer.OwnerId, StringComparison.Ordinal);

    // What the signature of a copy binds besides the provider: its version and the hash of its definition.
    private static string Subject(long version, FleetProviderDefinition definition)
        => "version:" + version.ToString(CultureInfo.InvariantCulture) + ";definition:" + definition.Digest();

    private static byte[] Associated(string keyId, string organizationId, string providerId, long version)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{ContentDomain}\n{FleetMetadataKey.SealAlgorithm}\n{keyId}\n{organizationId}\n{providerId}\n{version}"));
}
