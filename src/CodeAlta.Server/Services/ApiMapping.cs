using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>Maps the records of the store to the contracts of the API and the control connection.</summary>
internal static class ApiMapping
{
    public static PersonDto ToDto(this PersonRecord person) => new(person.Id, person.DisplayName, person.Role);

    public static DeviceDto ToDto(this DeviceRecord device, bool online)
        => new(device.Id, device.Name, device.Kind, device.PublicKey, device.Fingerprint, device.Status, device.OwnerPersonId, device.EnrolledByDeviceId,
            device.CreatedAt, device.ApprovedAt, device.RevokedAt, online);

    public static SessionDto ToDto(this SessionRecord session)
        => new(session.Id, session.MachineId, session.Epoch, session.Version, session.State, session.CreatedAt, session.UpdatedAt, session.ProviderType, session.Model,
            new FleetSessionCounts(session.Turns, session.ToolCalls, session.InputTokens, session.OutputTokens), session.SealedMetadata);

    public static FleetSessionFact ToFact(this SessionRecord session)
        => new(session.Id, session.Epoch, session.Version, session.State, session.CreatedAt, session.UpdatedAt, session.ProviderType, session.Model,
            new FleetSessionCounts(session.Turns, session.ToolCalls, session.InputTokens, session.OutputTokens), session.SealedMetadata);

    public static FleetDirectorySession ToDirectory(this SessionRecord session) => new(session.MachineId, session.ToFact());

    public static FleetMachineInventory ReadInventory(this MachineRecord machine)
        => JsonSerializer.Deserialize(machine.Inventory, FleetServerJsonContext.Default.FleetMachineInventory) ?? new FleetMachineInventory(string.Empty, string.Empty);

    public static FleetDirectoryMachine ToDirectory(this MachineRecord machine, string kind) => new(machine.DeviceId, kind, machine.ReadInventory(), machine.UpdatedAt);

    public static DeviceListEntryDto ToDto(this DeviceListEntryRecord entry)
        => new(entry.Sequence, entry.Hash, new FleetSignedDeviceListEntry(entry.Payload, entry.Signature), entry.RecordedAt);

    public static AuditRecordDto ToDto(this AuditRecord record)
        => new(record.Sequence, record.RecordedAt, record.Entry.Action, record.Entry.Result, record.Entry.ActorPersonId, record.Entry.ActorDeviceId, record.Entry.Target,
            record.Entry.SourceAddress, record.Entry.RequestId, record.Entry.Details, record.PreviousHash, record.Hash);

    public static AuditEntry Audit(this RequestOrigin origin, string action, DeviceRecord? actor, string? target, string result = AuditEntry.Success, string? details = null)
        => new(action, result, actor?.OwnerPersonId, actor?.Id, target, origin.SourceAddress, origin.RequestId, details);
}
