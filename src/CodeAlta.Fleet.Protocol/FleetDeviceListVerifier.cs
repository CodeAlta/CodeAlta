using System.Collections.Immutable;

namespace CodeAlta.Fleet.Protocol;

/// <summary>A device of a verified device list.</summary>
/// <param name="DeviceId">The id of the device, derived from its key.</param>
/// <param name="Kind">Its class (<see cref="FleetDeviceKinds"/>).</param>
/// <param name="Key">Its public key, as the entry that added it says.</param>
/// <param name="OwnerId">The person it belongs to; null for the first device until it signs an entry for its person.</param>
public sealed record FleetDeviceListMember(string DeviceId, string Kind, FleetDevicePublicKey Key, string? OwnerId);

/// <summary>The time of a device in a verified device list: the entry that added it and, once it left, the one that removed it.</summary>
/// <param name="Member">The device, as the list last held it.</param>
/// <param name="AddedAt">The sequence of the entry that added it.</param>
/// <param name="RemovedAt">The sequence of the entry that removed it; null while it is a member.</param>
public sealed record FleetDeviceListTenure(FleetDeviceListMember Member, long AddedAt, long? RemovedAt);

/// <summary>A device list as a verifier replayed it: its members after its last entry, and where the next entry goes.</summary>
/// <param name="OrganizationId">The organization the entries after the first name; null while the list has one entry.</param>
/// <param name="Sequence">The sequence of the last entry, 0 for an empty list.</param>
/// <param name="LastHash">The hash of the last entry, which the next one names; null for an empty list.</param>
/// <param name="Members">The devices in the list, by id.</param>
/// <param name="Removed">The devices removed from the list, which never join it again.</param>
public sealed record FleetDeviceListState(
    string? OrganizationId,
    long Sequence,
    string? LastHash,
    ImmutableDictionary<string, FleetDeviceListMember> Members,
    ImmutableHashSet<string> Removed)
{
    /// <summary>Gets the state of a list without entries.</summary>
    public static FleetDeviceListState Empty { get; } = new(null, 0, null, ImmutableDictionary.Create<string, FleetDeviceListMember>(StringComparer.Ordinal),
        ImmutableHashSet.Create<string>(StringComparer.Ordinal));

    /// <summary>Gets the device the first entry added: the first controller device, the owner's at setup.</summary>
    public string? FirstDeviceId { get; init; }

    /// <summary>Gets the hash of the first entry: the root a device pins per server, so that a server cannot swap the whole list.</summary>
    public string? FirstHash { get; init; }

    /// <summary>
    /// Gets the sequence of the last entry that removed a device, 0 when none did: a key given to the devices of the
    /// list before it may be held by the removed device, so it seals nothing new (a new epoch replaces it).
    /// </summary>
    public long LastRemovalSequence { get; init; }

    /// <summary>
    /// Gets every device the list ever held, members and removed devices, by id, with the entries that added and removed
    /// it: what a device was by an earlier entry of the list, as the list itself authenticates it.
    /// </summary>
    public ImmutableDictionary<string, FleetDeviceListTenure> Tenures { get; init; } = ImmutableDictionary.Create<string, FleetDeviceListTenure>(StringComparer.Ordinal);

    /// <summary>
    /// Gets a device as the list held it at an entry from <paramref name="sequence"/> on: a member now, or one that a later
    /// entry than that one removed. A device that left by that entry, or never was in the list, gives null.
    /// </summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="sequence">The sequence of an entry of the list.</param>
    /// <returns>The device, or null.</returns>
    public FleetDeviceListMember? MemberFrom(string deviceId, long sequence)
    {
        if (Tenures.TryGetValue(deviceId, out var tenure)) return tenure.RemovedAt is { } removed && removed <= sequence ? null : tenure.Member;
        // A state made without its history (by hand) knows its members only.
        return Members.GetValueOrDefault(deviceId);
    }

    /// <summary>Gets whether a device is a controller device of the list: one that may sign entries.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>Whether it is a member of class <see cref="FleetDeviceKinds.Controller"/>.</returns>
    public bool IsController(string deviceId) => Members.TryGetValue(deviceId, out var member) && member.Kind == FleetDeviceKinds.Controller;
}

/// <summary>The result of checking a device list.</summary>
/// <param name="IsValid">Whether every entry holds.</param>
/// <param name="Count">How many entries were read, the one that broke the chain included.</param>
/// <param name="BrokenAt">The position (from 1) of the first entry that does not hold, or <see langword="null"/>.</param>
/// <param name="Reason">Why that entry does not hold, for people, or <see langword="null"/>.</param>
/// <param name="State">The list after the last entry that holds.</param>
public sealed record FleetDeviceListVerification(bool IsValid, long Count, long? BrokenAt, string? Reason, FleetDeviceListState State);

/// <summary>A device list that does not verify: a device refuses to sign after it or to trust a key it holds.</summary>
public sealed class FleetDeviceListException : Exception
{
    /// <summary>The stable code of the refusal.</summary>
    public const string Code = "device_list.broken";

    /// <summary>Initializes a new instance of the <see cref="FleetDeviceListException"/> class.</summary>
    /// <param name="brokenAt">The position of the entry that does not hold.</param>
    /// <param name="reason">Why it does not hold.</param>
    public FleetDeviceListException(long brokenAt, string reason)
        : base($"The device list of the server does not verify at entry {brokenAt}: {reason}. Nothing was signed or trusted after it.")
    {
        BrokenAt = brokenAt;
        Reason = reason;
    }

    /// <summary>Gets the position of the entry that does not hold.</summary>
    public long BrokenAt { get; }

    /// <summary>Gets why it does not hold.</summary>
    public string Reason { get; }
}

/// <summary>
/// The rules of the signed device list (<c>doc/fleet/security-and-privacy.md</c>, the device list), the same for the
/// server, which checks each entry before it keeps it, and for every device, which replays the list before it signs
/// after it or trusts a key of it, so that a server cannot add a member by itself.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each entry follows the one before it: the next sequence and the hash of the previous entry.</item>
/// <item>The first entry adds a controller device signed by its own key; its person is the one named by the first entry it signs.</item>
/// <item>Every later entry names the organization of the list and is signed by a controller device that is a member at
/// that point: an execution node never signs an entry.</item>
/// <item>A controller adds devices of its own person only (team machines and their signers come later); a removed
/// device never joins again; the last controller device is never removed.</item>
/// <item>A person has at most one recovery key in the list; it signs only the entry that adds a controller device of its
/// person (when every device is lost), never a removal.</item>
/// </list>
/// </remarks>
public static class FleetDeviceListVerifier
{
    /// <summary>The largest text of an entry.</summary>
    public const int MaxPayloadLength = 8192;

    /// <summary>Replays a device list from its first entry.</summary>
    /// <param name="entries">The signed entries, in order.</param>
    /// <param name="organizationId">The organization the list must name; null to take the one its second entry names.</param>
    /// <returns>Whether the list holds, where it breaks, and the list up to there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    public static FleetDeviceListVerification Verify(IEnumerable<FleetSignedDeviceListEntry> entries, string? organizationId = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var state = FleetDeviceListState.Empty;
        long count = 0;
        foreach (var entry in entries)
        {
            count++;
            if (!TryAppend(state, entry, organizationId, out var next, out var reason)) return new(false, count, count, reason, state);
            state = next;
        }

        return new(true, count, null, null, state);
    }

    /// <summary>Replays a device list and throws when it does not hold.</summary>
    /// <param name="entries">The signed entries, in order.</param>
    /// <param name="organizationId">The organization the list must name; null to take the one its second entry names.</param>
    /// <returns>The list after its last entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="FleetDeviceListException">An entry does not hold, or the list is empty.</exception>
    public static FleetDeviceListState VerifyOrThrow(IEnumerable<FleetSignedDeviceListEntry> entries, string? organizationId = null)
    {
        var verification = Verify(entries, organizationId);
        if (!verification.IsValid) throw new FleetDeviceListException(verification.BrokenAt!.Value, verification.Reason!);
        if (verification.Count == 0) throw new FleetDeviceListException(1, "the list is empty");
        return verification.State;
    }

    /// <summary>Checks one entry after a list and gives the list with it.</summary>
    /// <param name="state">The list before the entry.</param>
    /// <param name="entry">The signed entry.</param>
    /// <param name="organizationId">The organization the list must name; null to take the one the list names.</param>
    /// <param name="next">The list with the entry, or <paramref name="state"/> when it does not hold.</param>
    /// <param name="reason">Why the entry does not hold, or <see langword="null"/>.</param>
    /// <returns>Whether the entry holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    public static bool TryAppend(FleetDeviceListState state, FleetSignedDeviceListEntry? entry, string? organizationId, out FleetDeviceListState next,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(state);
        next = state;
        reason = Check(state, entry, organizationId, ref next);
        return reason is null;
    }

    private static string? Check(FleetDeviceListState state, FleetSignedDeviceListEntry? entry, string? organizationId, ref FleetDeviceListState next)
    {
        if (entry is null || string.IsNullOrEmpty(entry.Payload) || entry.Payload.Length > MaxPayloadLength
            || !FleetDeviceListEntries.TryParsePayload(entry.Payload, out var payload) || payload!.Version != FleetDeviceListEntryPayload.CurrentVersion)
        {
            return "the entry does not read as an entry of version 1";
        }

        if (payload.Sequence != state.Sequence + 1) return $"its sequence is {payload.Sequence}, not {state.Sequence + 1}";
        if (!string.Equals(payload.Previous, state.LastHash, StringComparison.Ordinal)) return "it does not name the hash of the entry before it";
        var signed = FleetDeviceListEntries.GetSignedBytes(entry.Payload);
        var hash = FleetDeviceListEntries.Hash(entry);
        if (state.Sequence == 0)
        {
            // The first entry: a controller device adds itself.
            if (payload.Operation != FleetDeviceListOperations.Add || payload.Kind != FleetDeviceKinds.Controller || payload.SignerDeviceId != payload.DeviceId)
                return "the first entry must add a controller device signed by itself";
            if (!FleetDevicePublicKey.TryParse(payload.PublicKey, out var self) || self.DeviceId != payload.DeviceId) return "the first device's key does not give its id";
            if (!self.Verify(signed, entry.Signature)) return "the signature of the first device does not verify";
            var first = new FleetDeviceListMember(payload.DeviceId, FleetDeviceKinds.Controller, self, payload.OwnerId);
            next = state with
            {
                FirstDeviceId = payload.DeviceId,
                FirstHash = hash,
                Sequence = 1,
                LastHash = hash,
                Members = state.Members.SetItem(payload.DeviceId, first),
                Tenures = state.Tenures.SetItem(payload.DeviceId, new FleetDeviceListTenure(first, 1, null)),
            };
            return null;
        }

        var organization = organizationId ?? state.OrganizationId ?? payload.OrganizationId;
        if (string.IsNullOrEmpty(payload.OrganizationId) || !string.Equals(payload.OrganizationId, organization, StringComparison.Ordinal))
            return "it names another organization";
        if (!state.Members.TryGetValue(payload.SignerDeviceId, out var signer)) return "its signer is not a member of the list";
        if (signer.Kind is not (FleetDeviceKinds.Controller or FleetDeviceKinds.Recovery)) return "its signer is not a controller device";
        if (!signer.Key.Verify(signed, entry.Signature)) return "its signature does not verify with the key of its signer";

        var members = state.Members;
        var removed = state.Removed;
        var tenures = state.Tenures;
        switch (payload.Operation)
        {
            case FleetDeviceListOperations.Add:
                if (members.ContainsKey(payload.DeviceId) || removed.Contains(payload.DeviceId)) return "it adds a device that is or was in the list";
                if (!FleetDeviceKinds.IsMember(payload.Kind)) return "it adds a device of no known class";
                if (!FleetDevicePublicKey.TryParse(payload.PublicKey, out var added) || added.DeviceId != payload.DeviceId) return "the added device's key does not give its id";
                if (string.IsNullOrEmpty(payload.OwnerId)) return "it adds a device without its person";
                // A recovery key signs in a new controller device of its person, when every device is lost, and nothing else.
                if (signer.Kind == FleetDeviceKinds.Recovery && payload.Kind != FleetDeviceKinds.Controller) return "a recovery key adds controller devices only";
                // The person of the first device is the one its first entry names.
                if (signer.OwnerId is null)
                {
                    members = members.SetItem(signer.DeviceId, signer = signer with { OwnerId = payload.OwnerId });
                    tenures = tenures.SetItem(signer.DeviceId, tenures.TryGetValue(signer.DeviceId, out var tenure) ? tenure with { Member = signer } : new(signer, 1, null));
                }

                if (!string.Equals(signer.OwnerId, payload.OwnerId, StringComparison.Ordinal) && !AddsFirstDeviceOfPerson(state, members, signer, payload))
                    return "a controller device adds devices of its own person only";
                if (payload.Kind == FleetDeviceKinds.Recovery && members.Values.Any(member => member.Kind == FleetDeviceKinds.Recovery && member.OwnerId == payload.OwnerId))
                    return "the person has a recovery key already: the old one leaves the list first";
                var joined = new FleetDeviceListMember(payload.DeviceId, payload.Kind!, added, payload.OwnerId);
                members = members.SetItem(payload.DeviceId, joined);
                tenures = tenures.SetItem(payload.DeviceId, new FleetDeviceListTenure(joined, payload.Sequence, null));
                break;
            case FleetDeviceListOperations.Remove:
                if (signer.Kind == FleetDeviceKinds.Recovery) return "a recovery key removes no device";
                if (!members.TryGetValue(payload.DeviceId, out var gone)) return "it removes a device that is not in the list";
                if (gone.Kind == FleetDeviceKinds.Controller && members.Values.Count(static member => member.Kind == FleetDeviceKinds.Controller) <= 1)
                    return "it removes the last controller device";
                members = members.Remove(payload.DeviceId);
                removed = removed.Add(payload.DeviceId);
                tenures = tenures.SetItem(payload.DeviceId, tenures.TryGetValue(payload.DeviceId, out var held)
                    ? held with { RemovedAt = payload.Sequence }
                    : new FleetDeviceListTenure(gone, 0, payload.Sequence));
                break;
            default:
                return $"its operation '{payload.Operation}' is unknown";
        }

        next = new FleetDeviceListState(organization, payload.Sequence, hash, members, removed)
        {
            FirstDeviceId = state.FirstDeviceId,
            FirstHash = state.FirstHash,
            LastRemovalSequence = payload.Operation == FleetDeviceListOperations.Remove ? payload.Sequence : state.LastRemovalSequence,
            Tenures = tenures,
        };
        return null;
    }

    // The owner signs people in (doc/fleet/identity.md, a new device): a controller device of the person of the first
    // entry (the owner who set the server up) adds the first controller device of another person, one who holds no
    // member of the list, such as a person whom company sign-in created. That device then signs the person's next ones.
    private static bool AddsFirstDeviceOfPerson(FleetDeviceListState state, ImmutableDictionary<string, FleetDeviceListMember> members, FleetDeviceListMember signer,
        FleetDeviceListEntryPayload payload)
    {
        if (signer.Kind != FleetDeviceKinds.Controller || payload.Kind != FleetDeviceKinds.Controller || string.IsNullOrEmpty(payload.OwnerId)) return false;
        var owner = state.FirstDeviceId is { } first && state.Tenures.TryGetValue(first, out var tenure) ? tenure.Member.OwnerId
            : state.FirstDeviceId is { } id && members.TryGetValue(id, out var member) ? member.OwnerId : null;
        return owner is not null && string.Equals(signer.OwnerId, owner, StringComparison.Ordinal)
            && !members.Values.Any(other => string.Equals(other.OwnerId, payload.OwnerId, StringComparison.Ordinal));
    }
}
