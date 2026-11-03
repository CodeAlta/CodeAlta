using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The devices of the organization of one server link, as this device last verified them: the signed device list read
/// from the server and replayed here with the rules every device applies (<see cref="FleetDeviceListVerifier"/>), with
/// its first entry pinned in the folder of the link the first time it was read. A later list with another first entry
/// is refused: a server cannot swap the organization under a device.
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
    private FleetDeviceListState? _current;

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
    public FleetDeviceListState? Current => Volatile.Read(ref _current);

    /// <summary>Gets the trust a node gives the members of the organization on a relayed channel, read live from <see cref="Current"/>.</summary>
    public IDeviceTrustPolicy Trust => new FleetOrganizationTrust(() => Current, _identity.DeviceId);

    /// <summary>Reads the device list from the server again, verifies it, and checks its first entry against the pin.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verified list.</returns>
    /// <exception cref="FleetDeviceListException">The list does not verify, or its first entry is not the one pinned.</exception>
    /// <exception cref="FleetServerException">The server refused the read.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetDeviceListState> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pin = ReadPin();
            using var client = new FleetServerClient(_server, _identity, _handler);
            var list = await client.ReadVerifiedDeviceListAsync(pin?.Organization ?? Current?.OrganizationId, cancellationToken).ConfigureAwait(false);
            var pinned = pin?.Root ?? Current?.FirstHash;
            if (pinned is not null && !string.Equals(pinned, list.FirstHash, StringComparison.Ordinal))
                throw new FleetDeviceListException(1, "it starts with another device than the one this device trusted for this server");
            if (pin is null) WritePin(list);
            Volatile.Write(ref _current, list);
            return list;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private FleetDeviceListPin? ReadPin()
    {
        if (_pinPath is null) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(_pinPath), FleetOrganizationJson.Default.FleetDeviceListPin) is { Root.Length: > 0 } pin ? pin : null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private void WritePin(FleetDeviceListState list)
    {
        if (_pinPath is null || list.FirstHash is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_pinPath)!);
        FleetFiles.WriteAtomically(_pinPath, JsonSerializer.SerializeToUtf8Bytes(new FleetDeviceListPin(list.FirstHash, list.OrganizationId),
            FleetOrganizationJson.Default.FleetDeviceListPin));
    }
}

/// <summary>Reads the members of a verified device list by the fingerprint of their key, as a channel authenticates them.</summary>
public static class FleetDeviceListMembers
{
    /// <summary>Gets the fingerprint of the key of a member.</summary>
    /// <param name="member">The member.</param>
    /// <returns>The fingerprint of its key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> is null.</exception>
    public static DeviceFingerprint Fingerprint(this FleetDeviceListMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return DeviceFingerprint.Parse(member.Key.Fingerprint);
    }

    /// <summary>Finds a member by its key.</summary>
    /// <param name="list">The verified list.</param>
    /// <param name="fingerprint">The fingerprint of the key.</param>
    /// <returns>The member, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
    public static FleetDeviceListMember? FindByFingerprint(this FleetDeviceListState list, DeviceFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (fingerprint.IsEmpty || !list.Members.TryGetValue(DeviceIdentity.DeviceIdOf(fingerprint), out var member)) return null;
        return string.Equals(member.Key.Fingerprint, fingerprint.ToString(), StringComparison.Ordinal) ? member : null;
    }

    /// <summary>Gets whether the list removed the device of a key: revoked, refused on every path.</summary>
    /// <param name="list">The verified list.</param>
    /// <param name="fingerprint">The fingerprint of the key.</param>
    /// <returns>Whether the device of that key was removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
    public static bool IsRemoved(this FleetDeviceListState list, DeviceFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(list);
        return !fingerprint.IsEmpty && list.Removed.Contains(DeviceIdentity.DeviceIdOf(fingerprint));
    }
}

/// <summary>
/// The trust a node gives, on a relayed channel, to the members of the signed device list of its organization: the
/// person's own controller devices get the permissions of a paired device, any other member is known but may do nothing
/// in this step, and a device the list removed is refused.
/// </summary>
/// <param name="list">Gives the list as last verified; null refuses everyone.</param>
/// <param name="selfDeviceId">The device of this node, never trusted through the list.</param>
public sealed class FleetOrganizationTrust(Func<FleetDeviceListState?> list, string selfDeviceId) : IDeviceTrustPolicy
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
    public bool IsRevoked(DeviceFingerprint fingerprint) => list() is { } current && current.IsRemoved(fingerprint);
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
/// <param name="Organization">The organization it named, which later lists must name too.</param>
internal sealed record FleetDeviceListPin(string Root, string? Organization);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FleetDeviceListPin))]
internal sealed partial class FleetOrganizationJson : JsonSerializerContext;
