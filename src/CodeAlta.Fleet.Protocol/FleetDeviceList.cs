using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>The classes of devices (<c>doc/fleet/security-and-privacy.md</c>, the device list).</summary>
public static class FleetDeviceKinds
{
    /// <summary>A person's desktop or phone: it may sign other devices of its person into the device list.</summary>
    public const string Controller = "controller";

    /// <summary>A node that runs sessions: it acts only as itself and never signs another device.</summary>
    public const string Node = "node";

    /// <summary>
    /// The recovery key of a person (<c>doc/fleet/security-and-privacy.md</c>, keys): a key the person keeps as words,
    /// with no device behind it. It holds a copy of the person's keys, and signs a new controller device of its person
    /// into the list when every device is lost; it signs nothing else, and is not a controller device.
    /// </summary>
    public const string Recovery = "recovery";

    /// <summary>Gets whether a text is a kind of device that enrolls: a controller device or a node.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it is <see cref="Controller"/> or <see cref="Node"/>.</returns>
    public static bool IsKnown(string? kind) => kind is Controller or Node;

    /// <summary>Gets whether a text is a kind of member of a device list: a device that enrolls, or a recovery key.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it is <see cref="Controller"/>, <see cref="Node"/> or <see cref="Recovery"/>.</returns>
    public static bool IsMember(string? kind) => kind is Controller or Node or Recovery;
}

/// <summary>The operations of an entry of the device list.</summary>
public static class FleetDeviceListOperations
{
    /// <summary>A device joins the list.</summary>
    public const string Add = "add";

    /// <summary>A device leaves the list: its revocation.</summary>
    public const string Remove = "remove";
}

/// <summary>
/// What an entry of the device list says, signed by a controller device:
/// <c>{ "v", "org", "seq", "prev", "op", "device", "key", "kind", "owner", "signer", "time" }</c>, and <c>"sowner"</c> when the signer names its own person.
/// </summary>
/// <remarks>
/// Entries form a chain: each names the hash of the one before it (<see cref="FleetDeviceListEntries.Hash"/>) and the next
/// sequence. The first entry of a server, made at its setup, adds the owner's first controller device and is signed by
/// that device itself; its <see cref="OrganizationId"/>, <see cref="OwnerId"/> and <see cref="Previous"/> are
/// <see langword="null"/>. Devices accept a member only with a valid chain, so the server, which signs nothing, cannot
/// add a device by itself.
/// </remarks>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization of the list, or <see langword="null"/> for the first entry.</param>
/// <param name="Sequence">The position of the entry, from 1.</param>
/// <param name="Previous">The hash of the entry before, or <see langword="null"/> for the first one.</param>
/// <param name="Operation">What the entry does (<see cref="FleetDeviceListOperations"/>).</param>
/// <param name="DeviceId">The device the entry adds or removes.</param>
/// <param name="PublicKey">The public key of an added device, as the base64 of its DER SubjectPublicKeyInfo.</param>
/// <param name="Kind">The class of an added device (<see cref="FleetDeviceKinds"/>).</param>
/// <param name="OwnerId">The person who owns an added device, or <see langword="null"/> for the first entry.</param>
/// <param name="SignerDeviceId">The controller device that signs the entry.</param>
/// <param name="Time">When the signer made the entry; for display, never for ordering.</param>
/// <param name="SignerOwnerId">
/// The person of the signer, which the owner's first device names while the list does not know it yet (its first entry,
/// made before the person existed, names none): it then adds the first device of another person without being taken for
/// that person's. Omitted otherwise; an older verifier ignores it.
/// </param>
public sealed record FleetDeviceListEntryPayload(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string? OrganizationId,
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("prev")] string? Previous,
    [property: JsonPropertyName("op")] string Operation,
    [property: JsonPropertyName("device")] string DeviceId,
    [property: JsonPropertyName("key")] string? PublicKey,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("owner")] string? OwnerId,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("sowner"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SignerOwnerId = null)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>
/// An entry of the device list as it travels and is stored: the exact JSON text the signer signed, and the signature.
/// </summary>
/// <param name="Payload">The JSON of a <see cref="FleetDeviceListEntryPayload"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetDeviceListEntries.GetSignedBytes"/>, r and s in base64url.</param>
public sealed record FleetSignedDeviceListEntry(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>Writes, reads, signs and chains the entries of the device list.</summary>
public static class FleetDeviceListEntries
{
    private const string SignDomain = "codealta-device-list-entry/1\n";
    private const string HashDomain = "codealta-device-list-hash/1\n";

    /// <summary>Writes the JSON text of an entry, the text a signer signs.</summary>
    /// <param name="payload">The entry.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    public static string SerializePayload(FleetDeviceListEntryPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, FleetDeviceListJsonContext.Default.FleetDeviceListEntryPayload);
    }

    /// <summary>Reads the JSON text of an entry.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="entry">The entry read, or <see langword="null"/>.</param>
    /// <returns>Whether the text is an entry with its required fields.</returns>
    public static bool TryParsePayload(string? payload, out FleetDeviceListEntryPayload? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(payload)) return false;
        try
        {
            entry = JsonSerializer.Deserialize(payload, FleetDeviceListJsonContext.Default.FleetDeviceListEntryPayload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (entry is null || string.IsNullOrEmpty(entry.Operation) || string.IsNullOrEmpty(entry.DeviceId) || string.IsNullOrEmpty(entry.SignerDeviceId))
        {
            entry = null;
            return false;
        }

        return true;
    }

    /// <summary>Gets the bytes a signer signs for an entry.</summary>
    /// <param name="payload">The JSON text of the entry.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(SignDomain + payload);
    }

    /// <summary>Gets the hash of a signed entry, which the next entry names as its previous one.</summary>
    /// <param name="entry">The signed entry.</param>
    /// <returns>The base64url of the SHA-256 of the entry's text and signature.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    public static string Hash(FleetSignedDeviceListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes($"{HashDomain}{entry.Payload}\n{entry.Signature}")));
    }
}

/// <summary>The source-generated contracts of the device list.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(FleetDeviceListEntryPayload))]
[JsonSerializable(typeof(FleetSignedDeviceListEntry))]
public sealed partial class FleetDeviceListJsonContext : JsonSerializerContext;
