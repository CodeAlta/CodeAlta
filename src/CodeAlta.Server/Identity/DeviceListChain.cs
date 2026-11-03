using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Identity;

/// <summary>The result of checking a whole device list.</summary>
/// <param name="IsValid">Whether every entry holds.</param>
/// <param name="Count">How many entries were checked.</param>
/// <param name="BrokenAt">The sequence of the first entry that does not hold, or <see langword="null"/>.</param>
/// <param name="Members">The devices in the list after the last valid entry, by id, with their kind.</param>
public sealed record DeviceListVerification(bool IsValid, long Count, long? BrokenAt, IReadOnlyDictionary<string, string> Members);

/// <summary>
/// The signed device list at the server (<c>doc/fleet/security-and-privacy.md</c>, the device list): it reads the
/// entries it is given and replays the list with the rules of <see cref="FleetDeviceListVerifier"/>, the ones every
/// device applies, so that the server never keeps an entry the devices would refuse, and cannot add a member by itself.
/// </summary>
public static class DeviceListChain
{
    /// <summary>Reads a signed entry and checks its signature with the key of its signer.</summary>
    /// <param name="entry">The signed entry.</param>
    /// <param name="signerKey">The key of the device that should have signed it.</param>
    /// <returns>What the entry says.</returns>
    /// <exception cref="CoordinatorException">The entry is missing or does not read (<see cref="CoordinatorErrorCodes.EntryInvalid"/>), or its signature does not verify (<see cref="CoordinatorErrorCodes.EntrySignatureInvalid"/>).</exception>
    public static FleetDeviceListEntryPayload ReadSigned(FleetSignedDeviceListEntry? entry, FleetDevicePublicKey signerKey)
    {
        ArgumentNullException.ThrowIfNull(signerKey);
        if (entry is null || string.IsNullOrEmpty(entry.Payload) || entry.Payload.Length > 8192 || !FleetDeviceListEntries.TryParsePayload(entry.Payload, out var payload)
            || payload!.Version != FleetDeviceListEntryPayload.CurrentVersion)
        {
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.EntryInvalid, "The request needs a device-list entry of version 1, signed by the controller device that sends it.");
        }

        if (!signerKey.Verify(FleetDeviceListEntries.GetSignedBytes(entry.Payload), entry.Signature))
        {
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.EntrySignatureInvalid, "The signature of the device-list entry does not verify with the key of its signer.");
        }

        return payload;
    }

    /// <summary>Checks that an entry says what an operation needs, field by field.</summary>
    /// <param name="condition">Whether the entry holds.</param>
    /// <param name="what">What the entry must say, for the message.</param>
    /// <exception cref="CoordinatorException"><paramref name="condition"/> is false.</exception>
    public static void Require(bool condition, string what)
    {
        if (!condition) throw CoordinatorException.BadRequest(CoordinatorErrorCodes.EntryInvalid, $"The device-list entry must {what}.");
    }

    /// <summary>Replays a whole device list from its first entry, with the rules every device applies (<see cref="FleetDeviceListVerifier"/>).</summary>
    /// <param name="entries">The entries, by sequence.</param>
    /// <returns>Whether the list holds, and its members.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    public static DeviceListVerification Verify(IReadOnlyList<DeviceListEntryRecord> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var state = FleetDeviceListState.Empty;
        long count = 0;
        foreach (var record in entries)
        {
            count++;
            var signed = new FleetSignedDeviceListEntry(record.Payload, record.Signature);
            // The row agrees with the entry it keeps: its position and its hash.
            if (record.Sequence != count || !string.Equals(record.Hash, FleetDeviceListEntries.Hash(signed), StringComparison.Ordinal)
                || !FleetDeviceListVerifier.TryAppend(state, signed, count == 1 ? null : record.OrganizationId, out var next, out _))
            {
                return Result(false, count, record.Sequence, state);
            }

            state = next;
        }

        return Result(true, count, null, state);
    }

    /// <summary>Replays a device list and checks that an entry may follow it, as every device would.</summary>
    /// <param name="entries">The entries, by sequence.</param>
    /// <param name="entry">The entry to append.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="CoordinatorException">The list does not verify, or the entry would not hold after it (<see cref="CoordinatorErrorCodes.EntryInvalid"/>).</exception>
    public static void RequireAppendable(IReadOnlyList<DeviceListEntryRecord> entries, FleetSignedDeviceListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(entry);
        var replayed = FleetDeviceListVerifier.Verify(entries.Select(static record => new FleetSignedDeviceListEntry(record.Payload, record.Signature)),
            entries.Count > 0 ? entries[0].OrganizationId : null);
        if (!replayed.IsValid) throw new InvalidOperationException($"The device list of the server does not verify at entry {replayed.BrokenAt}: {replayed.Reason}.");
        if (!FleetDeviceListVerifier.TryAppend(replayed.State, entry, entries.Count > 0 ? entries[0].OrganizationId : null, out _, out var reason))
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.EntryInvalid, $"The device-list entry would not verify on the devices: {reason}.");
    }

    private static DeviceListVerification Result(bool valid, long count, long? brokenAt, FleetDeviceListState state)
        => new(valid, count, brokenAt, state.Members.ToDictionary(static pair => pair.Key, static pair => pair.Value.Kind, StringComparer.Ordinal));
}
