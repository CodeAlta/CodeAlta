using System.Collections.Immutable;

namespace CodeAlta.Fleet.Protocol;

/// <summary>A device of a verified device list.</summary>
/// <param name="DeviceId">The id of the device, derived from its key.</param>
/// <param name="Kind">Its class (<see cref="FleetDeviceKinds"/>).</param>
/// <param name="Key">Its public key, as the entry that added it says.</param>
/// <param name="OwnerId">The person it belongs to; null for the first device until it signs an entry for its person.</param>
public sealed record FleetDeviceListMember(string DeviceId, string Kind, FleetDevicePublicKey Key, string? OwnerId);

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
            next = state with
            {
                Sequence = 1,
                LastHash = hash,
                Members = state.Members.SetItem(payload.DeviceId, new FleetDeviceListMember(payload.DeviceId, FleetDeviceKinds.Controller, self, payload.OwnerId)),
            };
            return null;
        }

        var organization = organizationId ?? state.OrganizationId ?? payload.OrganizationId;
        if (string.IsNullOrEmpty(payload.OrganizationId) || !string.Equals(payload.OrganizationId, organization, StringComparison.Ordinal))
            return "it names another organization";
        if (!state.Members.TryGetValue(payload.SignerDeviceId, out var signer)) return "its signer is not a member of the list";
        if (signer.Kind != FleetDeviceKinds.Controller) return "its signer is not a controller device";
        if (!signer.Key.Verify(signed, entry.Signature)) return "its signature does not verify with the key of its signer";

        var members = state.Members;
        var removed = state.Removed;
        switch (payload.Operation)
        {
            case FleetDeviceListOperations.Add:
                if (members.ContainsKey(payload.DeviceId) || removed.Contains(payload.DeviceId)) return "it adds a device that is or was in the list";
                if (!FleetDeviceKinds.IsKnown(payload.Kind)) return "it adds a device of no known class";
                if (!FleetDevicePublicKey.TryParse(payload.PublicKey, out var added) || added.DeviceId != payload.DeviceId) return "the added device's key does not give its id";
                if (string.IsNullOrEmpty(payload.OwnerId)) return "it adds a device without its person";
                // The person of the first device is the one its first entry names.
                if (signer.OwnerId is null) members = members.SetItem(signer.DeviceId, signer = signer with { OwnerId = payload.OwnerId });
                if (!string.Equals(signer.OwnerId, payload.OwnerId, StringComparison.Ordinal)) return "a controller device adds devices of its own person only";
                members = members.SetItem(payload.DeviceId, new FleetDeviceListMember(payload.DeviceId, payload.Kind!, added, payload.OwnerId));
                break;
            case FleetDeviceListOperations.Remove:
                if (!members.TryGetValue(payload.DeviceId, out var gone)) return "it removes a device that is not in the list";
                if (gone.Kind == FleetDeviceKinds.Controller && members.Values.Count(static member => member.Kind == FleetDeviceKinds.Controller) <= 1)
                    return "it removes the last controller device";
                members = members.Remove(payload.DeviceId);
                removed = removed.Add(payload.DeviceId);
                break;
            default:
                return $"its operation '{payload.Operation}' is unknown";
        }

        next = new FleetDeviceListState(organization, payload.Sequence, hash, members, removed);
        return null;
    }
}
