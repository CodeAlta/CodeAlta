using System.Collections.Immutable;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The metadata keys of the spaces of this device's person at one server (<c>doc/fleet/security-and-privacy.md</c>,
/// keys): one key per space, with its epochs. It reads the copies wrapped to this device and takes those a controller
/// device of its person signed (<see cref="FleetKeyWrap.Unwrap(string, string, int, string?, DeviceIdentity, FleetDeviceListState)"/>);
/// on a controller device, it makes the key of a space this device links to the server when the person has none,
/// makes a new epoch of a space once a device was removed from the list after its key was given, and wraps the keys it
/// holds to the person's devices that lack them. The devices and their keys come from the device list as the anchor of
/// the link verified it (<see cref="FleetOrganizationDevices"/>), never from the server's tables.
/// </summary>
/// <remarks>
/// <para>
/// The server knows a space by an opaque id; which space it is on the devices (its id there, such as <c>default</c> or
/// <c>work</c>) is sealed with the key itself, so the devices of a person agree on the space of a key without the server
/// learning it. Two spaces of the person with the same id on the devices are one space: the first one made wins.
/// </para>
/// <para>
/// A key given before the last removal of the list may be held by the removed device: it seals nothing more, and the
/// space has no key that seals until the new epoch reaches this device, never an older key. The keys of the person
/// made before spaces had keys (without a space) still open what they sealed, and seal nothing new.
/// </para>
/// <para>One caller of <see cref="RefreshAsync(IReadOnlyCollection{string}, CancellationToken)"/> at a time: the loop of a <see cref="FleetConnector"/>; the keys are read from any thread.</para>
/// </remarks>
public sealed class FleetMetadataKeyring
{
    private readonly FleetOrganizationDevices _devices;
    private readonly DeviceIdentity _identity;
    private Snapshot _snapshot = Snapshot.Empty;

    /// <summary>Initializes a new instance of the <see cref="FleetMetadataKeyring"/> class.</summary>
    /// <param name="devices">The anchored device list of the server, whose key and HTTP handler the calls use too.</param>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is <see langword="null"/>.</exception>
    public FleetMetadataKeyring(FleetOrganizationDevices devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        _identity = devices.Identity;
    }

    /// <summary>Gets the anchored device list the keyring wraps by.</summary>
    public FleetOrganizationDevices Devices => _devices;

    /// <summary>
    /// Gets or sets the signed access document of the server as this device verified it, read at each call
    /// (<c>doc/fleet/access-control.md</c>, ownership over time): a machine given to another person takes the keys of its new owner
    /// and seals with them only, never with those of the person the device list names; a controller device wraps its person's keys
    /// to the machines the document gives them, never to those it gives another person, and makes a new epoch of a key such a
    /// machine holds. Null reads the device list alone; the connector of the link sets it to its access document.
    /// </summary>
    public Func<FleetAccessState?>? Access { get; set; }

    /// <summary>
    /// Gets the keys that seal, by the id of their space on this device; a space waits without one before its key reaches
    /// this device or while a removal waits for its new epoch. A key given before the last removal the anchored list
    /// accepted is never among them, from the moment the list accepted it: also when the keys were not read again since,
    /// or the next epoch could not be made. Nor is a key of another person than the one of this device, a machine given away.
    /// </summary>
    public IReadOnlyDictionary<string, FleetMetadataKey> Current => Sealing(Volatile.Read(ref _snapshot).Current, _devices.Current, Access?.Invoke());

    /// <summary>Gets every key this device opened: those that seal, older epochs and the keys of the person, which still open what they sealed.</summary>
    public IReadOnlyCollection<FleetMetadataKey> Held => Volatile.Read(ref _snapshot).Held.Values.ToArray();

    /// <summary>Gets the key that seals the metadata of a space.</summary>
    /// <param name="space">The id of the space on this device; the default space when null.</param>
    /// <returns>The key, or null when the space has none that seals now.</returns>
    public FleetMetadataKey? CurrentFor(string? space) => Current.GetValueOrDefault(Normalize(space));

    /// <summary>Opens sealed metadata with the key it names, when this device holds it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sealedText">The sealed text, as the server gave it.</param>
    /// <param name="metadata">The metadata, or <see langword="null"/>.</param>
    /// <returns>Whether it opened.</returns>
    public bool TryOpen(string sessionId, string? sealedText, out FleetSessionMetadata? metadata)
    {
        metadata = null;
        return FleetMetadataKey.KeyIdOf(sealedText) is { } keyId && Volatile.Read(ref _snapshot).Held.TryGetValue(keyId, out var key)
            && key.TryOpen(sessionId, sealedText, out metadata);
    }

    /// <summary>Reads the keys again for one space and gives the key that seals it.</summary>
    /// <param name="space">The id of the space on this device; the default space when null.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The key, or null when the space has none that seals yet.</returns>
    /// <exception cref="FleetDeviceListException">The device list does not verify, or does not extend the one this device verified: nothing is read or wrapped.</exception>
    /// <exception cref="FleetServerException">The server refused a call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetMetadataKey?> RefreshAsync(string? space, CancellationToken cancellationToken)
    {
        var current = await RefreshAsync([Normalize(space)], cancellationToken).ConfigureAwait(false);
        return current.GetValueOrDefault(Normalize(space));
    }

    /// <summary>
    /// Reads the keys again: the copies of this device, and on a controller device, makes the key of a space this device
    /// links when the person has none, a new epoch after a removal, and wraps the keys it holds to the person's devices that lack them.
    /// </summary>
    /// <param name="spaces">The ids of the spaces on this device that publish to the server: only those get a key that seals.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The keys that seal, by the id of their space on this device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="spaces"/> is <see langword="null"/>.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify, or does not extend the one this device verified: nothing is read or wrapped.</exception>
    /// <exception cref="FleetServerException">The server refused a call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyDictionary<string, FleetMetadataKey>> RefreshAsync(IReadOnlyCollection<string> spaces, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spaces);
        var wanted = spaces.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        // The list first: nothing is read or wrapped through a server whose list does not extend the anchored one.
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var access = Access?.Invoke();
        // A removal it accepted stops the older keys sealing now, before the calls that follow can fail (or a server delay them).
        var accepted = Volatile.Read(ref _snapshot);
        if (Sealing(accepted.Current, list, access) is var sealing && !ReferenceEquals(sealing, accepted.Current)) Volatile.Write(ref _snapshot, accepted with { Current = sealing });
        using var client = _devices.CreateClient();
        var rows = await client.ListMetadataKeysAsync(cancellationToken).ConfigureAwait(false);
        var opened = Open(rows, list, access);
        var known = ReadSpaces(rows, opened);
        var me = _identity.DeviceId;
        if (list.IsController(me))
        {
            // The devices of the person: its own, and the machines the signed access document gives them, never one it gives another person.
            var context = new Making(client, list, list.Members[me], [.. list.Members.Values.Where(member => member.DeviceId == me
                || list.Members[me].OwnerId is { } owner && FleetAccessState.PersonOf(access, member) == owner)], access);
            foreach (var space in known.Where(static space => space.Local is not null).ToArray())
            {
                if (await KeepAsync(context, space, opened, cancellationToken).ConfigureAwait(false) is { } made)
                {
                    opened[made.Id] = made;
                    known[known.IndexOf(space)] = space with { Made = made };
                }
            }

            foreach (var local in wanted.Order(StringComparer.Ordinal))
            {
                if (known.Any(space => space.Local == local) || !MayMakeSpace(known, context)) continue;
                if (await MakeAsync(context, null, local, 1, cancellationToken).ConfigureAwait(false) is { } made)
                {
                    opened[made.Id] = made;
                    known.Add(new KnownSpace(made.Grant!.SpaceId!, local, [], DateTimeOffset.UtcNow) { Made = made });
                }
            }

            await WrapLegacyAsync(context, rows, opened, cancellationToken).ConfigureAwait(false);
        }

        var previous = Volatile.Read(ref _snapshot);
        var person = PersonOfSelf(list, access);
        var current = ImmutableDictionary.CreateBuilder<string, FleetMetadataKey>(StringComparer.Ordinal);
        foreach (var local in wanted)
        {
            // The first space made for this local space wins: the devices of the person converge on it.
            var space = known.Where(candidate => candidate.Local == local).OrderBy(static candidate => candidate.CreatedAt).ThenBy(static candidate => candidate.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (space is null || Newest(space, opened) is not { } key || IsStale(key, list) || !IsOfPerson(key, person)) continue;
            // Never back to an older epoch of the same space.
            if (previous.Current.TryGetValue(local, out var held) && held.Grant!.SpaceId == key.Grant!.SpaceId && held.Grant.Epoch > key.Grant.Epoch && !IsStale(held, list)
                && IsOfPerson(held, person))
                key = held;
            current[local] = key;
        }

        var all = previous.Held.ToBuilder();
        foreach (var (id, key) in opened) all[id] = key;
        var snapshot = new Snapshot(current.ToImmutable(), all.ToImmutable());
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot.Current;
    }

    /// <summary>Forgets every key this device opened: the link is gone, or the device left the server.</summary>
    public void Forget() => Volatile.Write(ref _snapshot, Snapshot.Empty);

    // Every copy of this device whose grant holds; a copy the server made or relabelled is passed over, and so is a copy of the
    // person the device list names once the signed access document gives this machine to another person.
    private Dictionary<string, FleetMetadataKey> Open(IReadOnlyList<MetadataKeyDto> rows, FleetDeviceListState list, FleetAccessState? access)
    {
        var opened = new Dictionary<string, FleetMetadataKey>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Wrapped is not { } wrapped) continue;
            try
            {
                opened[row.Id] = FleetKeyWrap.Unwrap(wrapped, row.Id, row.Epoch, row.Space, _identity, list, access);
            }
            catch (InvalidDataException)
            {
                // Not a copy a controller device of this person gave for this space and epoch: refused.
            }
        }

        return opened;
    }

    // The spaces of the person at the server, each with the id it has on the devices when a key of it opens its descriptor.
    private static List<KnownSpace> ReadSpaces(IReadOnlyList<MetadataKeyDto> rows, Dictionary<string, FleetMetadataKey> opened)
    {
        var spaces = new List<KnownSpace>();
        foreach (var group in rows.Where(static row => row.Space is not null).GroupBy(static row => row.Space!, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(static row => row.Epoch).ToArray();
            string? local = null;
            foreach (var row in ordered.Reverse())
            {
                if (opened.TryGetValue(row.Id, out var key) && key.TryOpenSpace(group.Key, row.SealedDescriptor, out var name))
                {
                    local = name;
                    break;
                }
            }

            spaces.Add(new KnownSpace(group.Key, local, ordered, ordered.Min(static row => row.CreatedAt)));
        }

        return spaces;
    }

    // A new epoch when a device left after the key was given (its maker included), when a machine that holds it was given to
    // another person (the signed access document says so: it is no longer a device of this person), or when the copy of the
    // newest epoch for this device does not open; else the newest key goes to the person's devices that lack it.
    private async Task<FleetMetadataKey?> KeepAsync(Making context, KnownSpace space, Dictionary<string, FleetMetadataKey> opened, CancellationToken cancellationToken)
    {
        var newest = space.Rows[^1];
        var held = Newest(space, opened);
        if (held is null) return null;
        var unopenable = newest.Epoch > held.Grant!.Epoch && newest.Wrapped is not null;
        if (IsStale(held, context.List) || unopenable || newest.Id == held.Id && GivenAway(context, newest))
            return await MakeAsync(context, space.Id, space.Local!, newest.Epoch + 1, cancellationToken).ConfigureAwait(false);
        if (newest.Id != held.Id) return null;
        var missing = context.Devices.Where(device => !newest.WrappedTo.Contains(device.DeviceId)).ToArray();
        if (missing.Length > 0)
        {
            await context.Client.AddMetadataKeyWrapsAsync(newest.Id, [.. missing.Select(device => new MetadataKeyWrapDto(device.DeviceId, FleetKeyWrap.Wrap(held, device.Key, held.Grant, _identity)))],
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // The keys of the person made before spaces had keys open what they sealed: the person's new devices get them too.
    private async Task WrapLegacyAsync(Making context, IReadOnlyList<MetadataKeyDto> rows, Dictionary<string, FleetMetadataKey> opened, CancellationToken cancellationToken)
    {
        foreach (var row in rows.Where(static row => row.Space is null))
        {
            if (!opened.TryGetValue(row.Id, out var key)) continue;
            var missing = context.Devices.Where(device => !row.WrappedTo.Contains(device.DeviceId)).ToArray();
            if (missing.Length == 0) continue;
            await context.Client.AddMetadataKeyWrapsAsync(row.Id, [.. missing.Select(device => new MetadataKeyWrapDto(device.DeviceId, FleetKeyWrap.Wrap(key, device.Key, key.Grant!, _identity)))],
                cancellationToken).ConfigureAwait(false);
        }
    }

    // A new space only when this device reads every space of the person, or no controller device of the person that
    // holds the one it cannot read is left to give it: else that space may be the one sought, and its copy is on its way.
    private static bool MayMakeSpace(List<KnownSpace> known, Making context)
    {
        var owner = context.Self.OwnerId;
        foreach (var space in known.Where(static space => space.Local is null && space.Made is null))
        {
            if (space.Rows.SelectMany(static row => row.WrappedTo).Any(device => device != context.Self.DeviceId && context.List.Members.TryGetValue(device, out var member)
                    && member.Kind == FleetDeviceKinds.Controller && (owner is null || member.OwnerId == owner)))
                return false;
        }

        return true;
    }

    private async Task<FleetMetadataKey?> MakeAsync(Making context, string? spaceId, string local, int epoch, CancellationToken cancellationToken)
    {
        // The list names the person of the first device once it signed an entry; before, the server's tables do.
        var person = context.Self.OwnerId ?? (await context.Client.GetDeviceAsync(null, cancellationToken).ConfigureAwait(false)).OwnerId;
        if (person is null) return null;
        var organization = context.List.OrganizationId ?? (await context.Client.GetOrganizationAsync(cancellationToken).ConfigureAwait(false)).Id;
        spaceId ??= System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var made = FleetMetadataKey.Generate(new FleetKeyGrant(organization, person, epoch, context.List.Sequence) { SpaceId = spaceId });
        try
        {
            await context.Client.CreateSpaceKeyAsync(made.Id, epoch, spaceId, made.SealSpace(spaceId, local),
                [.. context.Devices.Select(device => new MetadataKeyWrapDto(device.DeviceId, FleetKeyWrap.Wrap(made, device.Key, made.Grant!, _identity)))],
                cancellationToken).ConfigureAwait(false);
            return made;
        }
        catch (FleetServerException exception) when (exception.Code == "metadata_key.exists")
        {
            // Another device made it first: its copy for this device is read at the next refresh.
            return null;
        }
    }

    private static FleetMetadataKey? Newest(KnownSpace space, Dictionary<string, FleetMetadataKey> opened)
        => space.Made ?? space.Rows.Reverse().Select(row => opened.GetValueOrDefault(row.Id)).FirstOrDefault(static key => key is not null);

    // A machine of the list that holds the key, not removed, which the signed access document gives another person than the one
    // of the key: its new owner reads what the key seals, so the person seals with a new epoch from now on.
    private static bool GivenAway(Making context, MetadataKeyDto row)
        => context.Self.OwnerId is { } owner && row.WrappedTo.Any(device => context.List.Members.TryGetValue(device, out var member) && member.Kind == FleetDeviceKinds.Node
            && !context.List.Removed.Contains(device) && FleetAccessState.PersonOf(context.Access, member) != owner);

    private static string Normalize(string? space) => string.IsNullOrWhiteSpace(space) ? SpaceDescriptor.DefaultId : space.Trim();

    private static bool IsStale(FleetMetadataKey key, FleetDeviceListState list) => key.Grant is not { } grant || grant.DeviceListSequence < list.LastRemovalSequence;

    // The person this device seals for: the one the signed access document names once this machine was given to another person.
    private string? PersonOfSelf(FleetDeviceListState? list, FleetAccessState? access)
        => list is not null && list.Members.TryGetValue(_identity.DeviceId, out var self) ? FleetAccessState.PersonOf(access, self) : null;

    // A key seals for this device only when it is of the person of this device; before the list names one, any key.
    private static bool IsOfPerson(FleetMetadataKey key, string? person) => person is null || key.Grant?.PersonId == person;

    // The keys that seal by a list and the access document: the same dictionary when none is stale by them.
    private ImmutableDictionary<string, FleetMetadataKey> Sealing(ImmutableDictionary<string, FleetMetadataKey> current, FleetDeviceListState? list, FleetAccessState? access)
    {
        if (list is null) return current;
        var person = PersonOfSelf(list, access);
        bool Drops(FleetMetadataKey key) => IsStale(key, list) || !IsOfPerson(key, person);
        return !current.Values.Any(Drops) ? current : current.RemoveRange([.. current.Where(pair => Drops(pair.Value)).Select(static pair => pair.Key)]);
    }

    private sealed record Snapshot(ImmutableDictionary<string, FleetMetadataKey> Current, ImmutableDictionary<string, FleetMetadataKey> Held)
    {
        public static Snapshot Empty { get; } = new(ImmutableDictionary.Create<string, FleetMetadataKey>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, FleetMetadataKey>(StringComparer.Ordinal));
    }

    // A space of the person at the server: its rows by epoch, the id it has on the devices when this device reads it, and the key this refresh made for it.
    private sealed record KnownSpace(string Id, string? Local, IReadOnlyList<MetadataKeyDto> Rows, DateTimeOffset CreatedAt)
    {
        public FleetMetadataKey? Made { get; init; }
    }

    private sealed record Making(FleetServerClient Client, FleetDeviceListState List, FleetDeviceListMember Self, IReadOnlyList<FleetDeviceListMember> Devices,
        FleetAccessState? Access);
}
