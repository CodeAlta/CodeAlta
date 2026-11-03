using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>A member of the signed device list of an organization, as this device verified it.</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="Kind">Its class (<see cref="FleetDeviceKinds"/>).</param>
/// <param name="Fingerprint">The fingerprint of its key, as the signed entry names it.</param>
/// <param name="OwnerId">The person it belongs to, or null when the list does not say.</param>
public sealed record FleetOrganizationDevice(string DeviceId, string Kind, DeviceFingerprint Fingerprint, string? OwnerId);

/// <summary>
/// The device list of an organization once this device replayed it (<c>doc/fleet/security-and-privacy.md</c>, the
/// device list): every entry follows the one before it, the first adds a controller device signed by itself, and every
/// later one is signed by a controller device that is a member at that point. The server signs nothing, so it cannot add
/// a member by itself; this view is what a device trusts, never the server's table of devices.
/// </summary>
public sealed class FleetVerifiedDeviceList
{
    private const int MaxPayloadLength = 8192;

    private FleetVerifiedDeviceList(string? organizationId, string rootHash, long count, ImmutableDictionary<string, FleetOrganizationDevice> members,
        ImmutableHashSet<DeviceFingerprint> removed)
    {
        OrganizationId = organizationId;
        RootHash = rootHash;
        Count = count;
        Members = members;
        Removed = removed;
    }

    /// <summary>Gets the organization the entries name; null when the list holds its first entry only.</summary>
    public string? OrganizationId { get; }

    /// <summary>Gets the hash of the first entry: the root a device pins, so that a server cannot swap the whole list.</summary>
    public string RootHash { get; }

    /// <summary>Gets how many entries were replayed.</summary>
    public long Count { get; }

    /// <summary>Gets the members, by device id.</summary>
    public ImmutableDictionary<string, FleetOrganizationDevice> Members { get; }

    /// <summary>Gets the keys of the devices the list removed: revoked, refused on every path.</summary>
    public ImmutableHashSet<DeviceFingerprint> Removed { get; }

    /// <summary>Finds a member by its key.</summary>
    /// <param name="fingerprint">The fingerprint of the key.</param>
    /// <returns>The member, or null.</returns>
    public FleetOrganizationDevice? FindByFingerprint(DeviceFingerprint fingerprint)
        => fingerprint.IsEmpty ? null : Members.Values.FirstOrDefault(member => member.Fingerprint == fingerprint);

    /// <summary>Replays the entries of a device list, from its first one.</summary>
    /// <param name="entries">The entries, by sequence, as <c>GET /api/v1/device-list</c> gives them.</param>
    /// <returns>The verified list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    /// <exception cref="InvalidDataException">The list is empty, or an entry does not hold; the message names it.</exception>
    public static FleetVerifiedDeviceList Verify(IReadOnlyList<DeviceListEntryDto> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) throw new InvalidDataException("The server has no device list.");
        var members = new Dictionary<string, (FleetDeviceListEntryPayload Entry, byte[] Key, DeviceFingerprint Fingerprint)>(StringComparer.Ordinal);
        var removed = ImmutableHashSet.CreateBuilder<DeviceFingerprint>();
        string? previous = null;
        string? organization = null;
        string root = "";
        string? firstDevice = null;
        string? firstOwner = null;
        long expected = 0;
        foreach (var dto in entries)
        {
            expected++;
            var signed = dto.Entry ?? throw Broken(expected);
            if (dto.Sequence != expected || signed.Payload is not { Length: > 0 and <= MaxPayloadLength }
                || !FleetDeviceListEntries.TryParsePayload(signed.Payload, out var payload) || payload!.Version != FleetDeviceListEntryPayload.CurrentVersion
                || payload.Sequence != expected || !string.Equals(payload.Previous, previous, StringComparison.Ordinal))
                throw Broken(expected);

            var hash = FleetDeviceListEntries.Hash(signed);
            if (expected == 1)
            {
                // The first entry: a controller device adds itself.
                if (payload.Operation != FleetDeviceListOperations.Add || payload.Kind != FleetDeviceKinds.Controller || payload.SignerDeviceId != payload.DeviceId
                    || !TryReadKey(payload.PublicKey, out var self, out var selfFingerprint) || DeviceIdentity.DeviceIdOf(selfFingerprint) != payload.DeviceId
                    || !Verify(self, signed))
                    throw Broken(expected);
                members[payload.DeviceId] = (payload, self, selfFingerprint);
                root = hash;
                firstDevice = payload.DeviceId;
            }
            else
            {
                if (payload.OrganizationId is null || organization is not null && !string.Equals(payload.OrganizationId, organization, StringComparison.Ordinal))
                    throw Broken(expected);
                organization ??= payload.OrganizationId;
                if (!members.TryGetValue(payload.SignerDeviceId, out var signer) || signer.Entry.Kind != FleetDeviceKinds.Controller || !Verify(signer.Key, signed))
                    throw Broken(expected);
                // The first device names no owner in its own entry: it is the person of the entries it signs, the owner who set the server up.
                if (payload.SignerDeviceId == firstDevice && payload.Operation == FleetDeviceListOperations.Add) firstOwner ??= payload.OwnerId;
                switch (payload.Operation)
                {
                    case FleetDeviceListOperations.Add when !members.ContainsKey(payload.DeviceId) && FleetDeviceKinds.IsKnown(payload.Kind)
                        && TryReadKey(payload.PublicKey, out var added, out var addedFingerprint) && DeviceIdentity.DeviceIdOf(addedFingerprint) == payload.DeviceId:
                        members[payload.DeviceId] = (payload, added, addedFingerprint);
                        removed.Remove(addedFingerprint);
                        break;
                    case FleetDeviceListOperations.Remove when members.Remove(payload.DeviceId, out var gone):
                        removed.Add(gone.Fingerprint);
                        break;
                    default:
                        throw Broken(expected);
                }
            }

            previous = hash;
        }

        var view = members.Values.Select(member => new FleetOrganizationDevice(member.Entry.DeviceId, member.Entry.Kind!, member.Fingerprint,
                member.Entry.OwnerId ?? (member.Entry.Sequence == 1 ? firstOwner : null)))
            .ToImmutableDictionary(static member => member.DeviceId, StringComparer.Ordinal);
        return new FleetVerifiedDeviceList(organization, root, expected, view, removed.ToImmutable());
    }

    private static InvalidDataException Broken(long sequence) => new($"The device list of the server does not verify: entry {sequence} does not hold.");

    private static bool TryReadKey(string? text, out byte[] key, out DeviceFingerprint fingerprint)
    {
        key = [];
        fingerprint = default;
        if (string.IsNullOrEmpty(text) || text.Length > 1024) return false;
        try
        {
            key = Convert.FromBase64String(text);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out var read);
            if (read != key.Length || ecdsa.KeySize != 256) return false;
            fingerprint = DeviceFingerprint.FromSubjectPublicKeyInfo(key);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static bool Verify(byte[] key, FleetSignedDeviceListEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Signature) || entry.Signature.Length > 128 || !Base64Url.IsValid(entry.Signature)) return false;
        var signature = Base64Url.DecodeFromChars(entry.Signature);
        if (signature.Length != 64) return false;
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key, out _);
        return ecdsa.VerifyData(FleetDeviceListEntries.GetSignedBytes(entry.Payload), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}

/// <summary>
/// The devices of the organization of one server link, as this device last verified them: the signed device list read
/// from the server, replayed here, with its first entry pinned in the folder of the link the first time it was read. A
/// later list with another first entry is refused: a server cannot swap the organization under a device.
/// </summary>
public sealed class FleetOrganizationDevices
{
    /// <summary>The name of the file of the pin in the folder of a link.</summary>
    public const string PinFileName = "device-list.json";

    private readonly Uri _server;
    private readonly DeviceIdentity _identity;
    private readonly string? _pinPath;
    private readonly HttpMessageHandler? _handler;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private FleetVerifiedDeviceList? _current;

    /// <summary>Initializes a new instance of the <see cref="FleetOrganizationDevices"/> class.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="identity">The key of this device, which signs the reads.</param>
    /// <param name="pinPath">The file of the pin of the first entry; null pins in memory only.</param>
    /// <param name="handler">The HTTP handler, for tests; null for the usual one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="identity"/> is null.</exception>
    public FleetOrganizationDevices(Uri server, DeviceIdentity identity, string? pinPath = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(identity);
        _server = server;
        _identity = identity;
        _pinPath = pinPath;
        _handler = handler;
    }

    /// <summary>Gets the list as last verified; null before the first read.</summary>
    public FleetVerifiedDeviceList? Current => Volatile.Read(ref _current);

    /// <summary>Gets the trust a node gives the members of the organization on a relayed channel, read live from <see cref="Current"/>.</summary>
    public IDeviceTrustPolicy Trust => new FleetOrganizationTrust(() => Current, _identity.DeviceId);

    /// <summary>Reads the device list from the server again and verifies it.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verified list.</returns>
    /// <exception cref="InvalidDataException">The list does not verify, or its first entry is not the one pinned.</exception>
    /// <exception cref="FleetServerException">The server refused the read.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetVerifiedDeviceList> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = new FleetServerClient(_server, _identity, _handler);
            var list = FleetVerifiedDeviceList.Verify(await client.GetDeviceListAsync(cancellationToken).ConfigureAwait(false));
            var pinned = ReadPin() ?? Current?.RootHash;
            if (pinned is not null && !string.Equals(pinned, list.RootHash, StringComparison.Ordinal))
                throw new InvalidDataException("The device list of the server starts with another device than the one this device trusted: it is refused.");
            if (pinned is null) WritePin(list);
            Volatile.Write(ref _current, list);
            return list;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private string? ReadPin()
    {
        if (_pinPath is null) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(_pinPath), FleetOrganizationJson.Default.FleetDeviceListPin)?.Root;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private void WritePin(FleetVerifiedDeviceList list)
    {
        if (_pinPath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_pinPath)!);
        FleetFiles.WriteAtomically(_pinPath, JsonSerializer.SerializeToUtf8Bytes(new FleetDeviceListPin(list.RootHash, list.OrganizationId),
            FleetOrganizationJson.Default.FleetDeviceListPin));
    }
}

/// <summary>
/// The trust a node gives, on a relayed channel, to the members of the signed device list of its organization: the
/// person's own controller devices get the permissions of a paired device, any other member is known but may do nothing
/// in this step, and a device the list removed is refused.
/// </summary>
/// <param name="list">Gives the list as last verified; null refuses everyone.</param>
/// <param name="selfDeviceId">The device of this node, never trusted through the list.</param>
public sealed class FleetOrganizationTrust(Func<FleetVerifiedDeviceList?> list, string selfDeviceId) : IDeviceTrustPolicy
{
    /// <inheritdoc />
    public TrustedDevice? FindTrusted(DeviceFingerprint fingerprint)
    {
        if (list() is not { } current || current.FindByFingerprint(fingerprint) is not { } member || member.DeviceId == selfDeviceId) return null;
        var owner = current.Members.TryGetValue(selfDeviceId, out var self) ? self.OwnerId : null;
        var own = owner is not null && member.OwnerId == owner && member.Kind == FleetDeviceKinds.Controller;
        return new TrustedDevice(member.DeviceId, member.DeviceId, fingerprint, own ? FleetPairingCoordinator.DefaultPermissions : DevicePermissions.None,
            DateTimeOffset.UnixEpoch, null);
    }

    /// <inheritdoc />
    public bool IsRevoked(DeviceFingerprint fingerprint) => list() is { } current && current.Removed.Contains(fingerprint);
}

/// <summary>A paired trust and an organization's, together: a paired device keeps its permissions; a key either one revoked is refused.</summary>
/// <param name="paired">The devices paired with this node.</param>
/// <param name="organization">The members of the organization.</param>
public sealed class FleetCombinedTrust(IDeviceTrustPolicy paired, IDeviceTrustPolicy organization) : IDeviceTrustPolicy
{
    /// <inheritdoc />
    public TrustedDevice? FindTrusted(DeviceFingerprint fingerprint)
        => IsRevoked(fingerprint) ? null : paired.FindTrusted(fingerprint) ?? organization.FindTrusted(fingerprint);

    /// <inheritdoc />
    public bool IsRevoked(DeviceFingerprint fingerprint) => paired.IsRevoked(fingerprint) || organization.IsRevoked(fingerprint);
}

/// <summary>The pin of a device list in the folder of a link.</summary>
/// <param name="Root">The hash of its first entry.</param>
/// <param name="Organization">The organization it named, for people who read the file.</param>
internal sealed record FleetDeviceListPin(string Root, string? Organization);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FleetDeviceListPin))]
internal sealed partial class FleetOrganizationJson : JsonSerializerContext;
