using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The devices of the organization of one server link, as this device last verified them: the signed device list read
/// from the server and replayed here with the rules every device applies (<see cref="FleetDeviceListVerifier"/>),
/// anchored in the folder of the link. The first read pins the first entry; every accepted read moves a checkpoint
/// (the sequence and the hash of the last entry) that a later list must extend. A list with another first entry, a
/// shorter one (which could bring back a removed device) or one that goes another way after the checkpoint is refused:
/// a server can neither swap the organization under a device nor undo an entry it verified.
/// </summary>
/// <remarks>
/// This is the one verifier of a server's device list for a device: what it trusts on a relayed channel, the entries
/// it signs (<see cref="FleetServerClient.DeviceList"/>) and the devices it gives a metadata key to
/// (<see cref="FleetMetadataKeyring"/>) all come from it.
/// </remarks>
public sealed class FleetOrganizationDevices
{
    /// <summary>The name of the file of the pin in the folder of a link.</summary>
    public const string PinFileName = "device-list.json";

    private readonly string? _pinPath;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private FleetDeviceListState? _current;
    private FleetDeviceListPin? _checkpoint;

    /// <summary>Initializes a new instance of the <see cref="FleetOrganizationDevices"/> class.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="identity">The key of this device, which signs the reads.</param>
    /// <param name="pinPath">The file of the pin and the checkpoint; null keeps them in memory only.</param>
    /// <param name="handler">The HTTP handler, for tests; null for the usual one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="identity"/> is null.</exception>
    public FleetOrganizationDevices(Uri server, DeviceIdentity identity, string? pinPath = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(identity);
        ServerAddress = server;
        Identity = identity;
        _pinPath = pinPath;
        Handler = handler;
    }

    /// <summary>Gets the address of the server.</summary>
    public Uri ServerAddress { get; }

    /// <summary>Gets the list as last verified; null before the first read.</summary>
    public FleetDeviceListState? Current => Volatile.Read(ref _current);

    /// <summary>Gets the trust a node gives the members of the organization on a relayed channel, read live from <see cref="Current"/>.</summary>
    public IDeviceTrustPolicy Trust => new FleetOrganizationTrust(() => Current, Identity.DeviceId);

    /// <summary>Gets the key of this device.</summary>
    internal DeviceIdentity Identity { get; }

    /// <summary>Gets the HTTP handler of the calls, for tests; null for the usual one.</summary>
    internal HttpMessageHandler? Handler { get; }

    /// <summary>Gets the pin of the server's own certificate (<see cref="FleetServerCertificates"/>), or null for one that validates as usual.</summary>
    public string? CertificatePin { get; init; }

    /// <summary>
    /// Creates a client of the server, signed by this device, that checks the pin of its certificate when it has one and reads
    /// the device list through this anchor: the one way a node or a desktop makes a client of a link it holds.
    /// </summary>
    /// <returns>The client; the caller disposes it.</returns>
    public FleetServerClient CreateClient() => new(ServerAddress, Identity, Handler, null, CertificatePin) { DeviceList = this };

    /// <summary>
    /// Reads the device list from the server again, verifies it, and checks it against the anchor: the same first entry,
    /// and the entries verified before, unchanged, at its start. The checkpoint then moves to its last entry.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verified list.</returns>
    /// <exception cref="FleetDeviceListException">The list does not verify, or it does not extend the list this device verified before.</exception>
    /// <exception cref="FleetServerException">The server refused the read.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetDeviceListState> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var anchor = Newer(await ReadPinAsync(cancellationToken).ConfigureAwait(false), _checkpoint);
            using var client = CreateClient();
            var organization = anchor?.Organization ?? (await client.GetOrganizationAsync(cancellationToken).ConfigureAwait(false)).Id;
            var entries = await client.GetDeviceListAsync(cancellationToken).ConfigureAwait(false);
            var list = FleetServerClient.VerifyDeviceList(entries, organization);
            if (anchor is not null) Extends(anchor, entries, list);
            var accepted = new FleetDeviceListPin(list.FirstHash!, list.OrganizationId ?? organization, list.Sequence, list.LastHash);
            WritePin(accepted);
            Volatile.Write(ref _current, list);
            return list;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>
    /// Reads the name the server gives a member of the verified list, to show it beside the id that list verifies: the
    /// server's word, which no signature covers, for display only.
    /// </summary>
    /// <param name="deviceId">The device, a member of <see cref="Current"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The name, on one line of at most 64 characters; null when the device is not a member, the server does not
    /// answer, or it answers for another key.</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is empty.</exception>
    public async Task<string?> ReadNameAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        if (Current is not { } list || !list.Members.ContainsKey(deviceId)) return null;
        DeviceDto device;
        try
        {
            using var client = CreateClient();
            device = await client.GetDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (device.Id != deviceId || !FleetDevicePublicKey.TryParse(device.PublicKey, out var key) || key.DeviceId != deviceId) return null;
        var name = new string([.. (device.Name ?? string.Empty).Trim().Select(static c => char.IsControl(c) ? ' ' : c)]);
        return name.Length == 0 ? null : name.Length > 64 ? name[..64] : name;
    }

    /// <summary>
    /// Pins the device list of a link to a first entry this device learned elsewhere than from the server (the anchor a
    /// recovery key sealed), before its first read: the server then cannot answer a list of its own making.
    /// </summary>
    /// <param name="pinPath">The file of the pin in the folder of the link.</param>
    /// <param name="firstHash">The hash of the first entry.</param>
    /// <param name="organizationId">The organization of the list.</param>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    /// <exception cref="FleetDeviceListException">The link pinned another first entry already.</exception>
    /// <exception cref="IOException">The pin cannot be written.</exception>
    public static void Anchor(string pinPath, string firstHash, string organizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pinPath);
        ArgumentException.ThrowIfNullOrEmpty(firstHash);
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        FleetDeviceListPin? held = null;
        try { held = JsonSerializer.Deserialize(File.ReadAllBytes(pinPath), FleetOrganizationJson.Default.FleetDeviceListPin); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException) { }
        if (held is { Root.Length: > 0 })
        {
            if (!string.Equals(held.Root, firstHash, StringComparison.Ordinal)) throw new FleetDeviceListException(1, "the link pinned another first entry already");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pinPath))!);
        FleetFiles.WriteAtomically(pinPath, JsonSerializer.SerializeToUtf8Bytes(new FleetDeviceListPin(firstHash, organizationId), FleetOrganizationJson.Default.FleetDeviceListPin));
    }

    // A later list keeps the first entry pinned and every entry up to the checkpoint, unchanged: a removal it verified stays.
    private static void Extends(FleetDeviceListPin anchor, IReadOnlyList<DeviceListEntryDto> entries, FleetDeviceListState list)
    {
        if (!string.Equals(anchor.Root, list.FirstHash, StringComparison.Ordinal))
            throw new FleetDeviceListException(1, "it starts with another device than the one this device trusted for this server");
        if (anchor.Sequence <= 0) return;
        if (entries.Count < anchor.Sequence)
            throw new FleetDeviceListException(entries.Count + 1, $"it is shorter than the list of {anchor.Sequence} entries this device verified: entries were taken away");
        if (!string.Equals(entries[(int)anchor.Sequence - 1].Hash, anchor.Tip, StringComparison.Ordinal))
            throw new FleetDeviceListException(anchor.Sequence, "it goes another way than the list this device verified before");
    }

    private static FleetDeviceListPin? Newer(FleetDeviceListPin? stored, FleetDeviceListPin? held)
        => stored is null ? held : held is null || stored.Sequence >= held.Sequence ? stored : held;

    // A pin that cannot be read now (another instance replaces it) is read again; one that stays unreadable refuses
    // the list rather than trust it from its first entry again.
    private async Task<FleetDeviceListPin?> ReadPinAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return ReadPin();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && exception is not (FileNotFoundException or DirectoryNotFoundException))
            {
                if (attempt >= 4) throw new FleetDeviceListException(0, $"the anchor of the device list of this server cannot be read ({exception.Message})");
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
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

    // The checkpoint only moves forward, also when another instance of the same link wrote it meanwhile.
    private void WritePin(FleetDeviceListPin pin)
    {
        _checkpoint = pin;
        FleetDeviceListPin? stored;
        try { stored = _pinPath is null ? null : ReadPin(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        if (_pinPath is null || stored is not null && stored.Sequence >= pin.Sequence) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pinPath)!);
            FleetFiles.WriteAtomically(_pinPath, JsonSerializer.SerializeToUtf8Bytes(pin, FleetOrganizationJson.Default.FleetDeviceListPin));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Held in memory meanwhile; the next accepted read writes it.
        }
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
/// The trust a node gives, on a relayed channel, to the members of the signed device list of its organization, read with the
/// signed access document (<c>doc/fleet/access-control.md</c>, what the node decides): on a personal machine, the owner's own
/// controller devices get the permissions of a paired device; on a team machine, the controller devices of the team's members
/// view, drive and start their own sessions, and its maintainers also manage it, whoever the device list names as its owner;
/// any other member is known but may do nothing; a device the list removed, or of a person the document suspended, is refused.
/// </summary>
/// <param name="list">Gives the list as last verified; null refuses everyone.</param>
/// <param name="selfDeviceId">The device of this node, never trusted through the list.</param>
/// <param name="access">Gives the access document as last verified; null or none reads every machine as personal and suspends nobody.</param>
public sealed class FleetOrganizationTrust(Func<FleetDeviceListState?> list, string selfDeviceId, Func<FleetAccessState?>? access = null) : IDeviceTrustPolicy
{
    /// <summary>What the members of the team of a team machine may do there: their own sessions.</summary>
    public const DevicePermissions TeamMemberPermissions = DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start;

    /// <summary>What its maintainers may do there: their own sessions, the automations of the team, and manage the machine.</summary>
    public const DevicePermissions TeamMaintainerPermissions = TeamMemberPermissions | DevicePermissions.Automate | DevicePermissions.Manage;

    /// <inheritdoc />
    public TrustedDevice? FindTrusted(DeviceFingerprint fingerprint)
    {
        if (list() is not { } current || current.FindByFingerprint(fingerprint) is not { } member || member.DeviceId == selfDeviceId) return null;
        var document = access?.Invoke();
        if (document is not null && document.IsSuspended(member.OwnerId)) return null;
        if (member.Kind == FleetDeviceKinds.Controller && document?.TeamOf(selfDeviceId) is { } team)
        {
            var permissions = FleetAccessState.RoleIn(team, member.OwnerId) switch
            {
                FleetTeamRoles.Maintainer => TeamMaintainerPermissions,
                FleetTeamRoles.Member => TeamMemberPermissions,
                _ => DevicePermissions.None,
            };
            return new TrustedDevice(member.DeviceId, member.DeviceId, fingerprint, permissions, DateTimeOffset.UnixEpoch, null)
            {
                PersonId = member.OwnerId,
                OwnSessionsOnly = true,
            };
        }

        var owner = current.Members.TryGetValue(selfDeviceId, out var self) ? self.OwnerId : null;
        var own = owner is not null && member.OwnerId == owner && member.Kind == FleetDeviceKinds.Controller;
        // Another person's controller device gets nothing from the organization here; the people the machine names get what
        // its owner gave them, on their own sessions (FleetNamedPeopleTrust), and a first try asks the owner.
        var other = !own && member.Kind == FleetDeviceKinds.Controller && member.OwnerId is not null;
        return new TrustedDevice(member.DeviceId, member.DeviceId, fingerprint, own ? FleetPairingCoordinator.DefaultPermissions : DevicePermissions.None,
            DateTimeOffset.UnixEpoch, null)
        {
            PersonId = other ? member.OwnerId : null,
            OwnSessionsOnly = other,
        };
    }

    /// <inheritdoc />
    public bool IsRevoked(DeviceFingerprint fingerprint)
    {
        if (list() is not { } current) return false;
        if (current.IsRemoved(fingerprint)) return true;
        return access?.Invoke() is { } document && current.FindByFingerprint(fingerprint) is { } member && document.IsSuspended(member.OwnerId);
    }
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

/// <summary>The anchor of a device list in the folder of a link: its pin and its checkpoint.</summary>
/// <param name="Root">The hash of its first entry.</param>
/// <param name="Organization">The organization it named, which later lists must name too.</param>
/// <param name="Sequence">The sequence of the last entry accepted; 0 for a pin of an older version, without a checkpoint.</param>
/// <param name="Tip">The hash of that entry, which a later list holds at the same place.</param>
internal sealed record FleetDeviceListPin(string Root, string? Organization, long Sequence = 0, string? Tip = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FleetDeviceListPin))]
internal sealed partial class FleetOrganizationJson : JsonSerializerContext;
