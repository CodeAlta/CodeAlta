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
/// The rules of the signed device list (<c>doc/fleet/security-and-privacy.md</c>, the device list): the first entry
/// adds a controller device signed by itself; every later entry follows the one before it (sequence and hash) and is
/// signed by a controller device that is a member at that point. The server checks each entry it is given and can
/// replay the whole list; devices apply the same rules, so a server cannot add a member by itself.
/// </summary>
public static class DeviceListChain
{
    /// <summary>Reads a signed entry and checks its signature with the key of its signer.</summary>
    /// <param name="entry">The signed entry.</param>
    /// <param name="signerKey">The key of the device that should have signed it.</param>
    /// <returns>What the entry says.</returns>
    /// <exception cref="CoordinatorException">The entry is missing or does not read (<see cref="CoordinatorErrorCodes.EntryInvalid"/>), or its signature does not verify (<see cref="CoordinatorErrorCodes.EntrySignatureInvalid"/>).</exception>
    public static FleetDeviceListEntryPayload ReadSigned(FleetSignedDeviceListEntry? entry, DevicePublicKey signerKey)
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

    /// <summary>Replays a whole device list from its first entry.</summary>
    /// <param name="entries">The entries, by sequence.</param>
    /// <returns>Whether the list holds, and its members.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    public static DeviceListVerification Verify(IReadOnlyList<DeviceListEntryRecord> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var members = new Dictionary<string, (string Kind, DevicePublicKey Key)>(StringComparer.Ordinal);
        string? previousHash = null;
        string? organization = null;
        long count = 0;
        foreach (var record in entries)
        {
            count++;
            var signed = new FleetSignedDeviceListEntry(record.Payload, record.Signature);
            if (!Holds(record, signed, count, previousHash, ref organization, members)) return Result(false, count, record.Sequence, members);
            previousHash = FleetDeviceListEntries.Hash(signed);
        }

        return Result(true, count, null, members);
    }

    private static bool Holds(DeviceListEntryRecord record, FleetSignedDeviceListEntry signed, long expected, string? previousHash, ref string? organization,
        Dictionary<string, (string Kind, DevicePublicKey Key)> members)
    {
        if (record.Sequence != expected || !string.Equals(record.Hash, FleetDeviceListEntries.Hash(signed), StringComparison.Ordinal)) return false;
        if (!FleetDeviceListEntries.TryParsePayload(record.Payload, out var payload) || payload!.Sequence != expected
            || !string.Equals(payload.Previous, previousHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (expected == 1)
        {
            // The first entry: a controller device adds itself.
            if (payload.Operation != FleetDeviceListOperations.Add || payload.Kind != FleetDeviceKinds.Controller || payload.SignerDeviceId != payload.DeviceId
                || !DevicePublicKey.TryParse(payload.PublicKey, out var self) || self.DeviceId != payload.DeviceId
                || !self.Verify(FleetDeviceListEntries.GetSignedBytes(record.Payload), record.Signature))
            {
                return false;
            }

            organization = record.OrganizationId;
            members[payload.DeviceId] = (payload.Kind, self);
            return true;
        }

        if (!string.Equals(payload.OrganizationId, organization, StringComparison.Ordinal)) return false;
        if (!members.TryGetValue(payload.SignerDeviceId, out var signer) || signer.Kind != FleetDeviceKinds.Controller) return false;
        if (!signer.Key.Verify(FleetDeviceListEntries.GetSignedBytes(record.Payload), record.Signature)) return false;
        switch (payload.Operation)
        {
            case FleetDeviceListOperations.Add when !members.ContainsKey(payload.DeviceId) && FleetDeviceKinds.IsKnown(payload.Kind)
                && DevicePublicKey.TryParse(payload.PublicKey, out var added) && added.DeviceId == payload.DeviceId:
                members[payload.DeviceId] = (payload.Kind!, added);
                return true;
            case FleetDeviceListOperations.Remove when members.Remove(payload.DeviceId):
                return true;
            default:
                return false;
        }
    }

    private static DeviceListVerification Result(bool valid, long count, long? brokenAt, Dictionary<string, (string Kind, DevicePublicKey Key)> members)
        => new(valid, count, brokenAt, members.ToDictionary(static pair => pair.Key, static pair => pair.Value.Kind, StringComparer.Ordinal));
}
