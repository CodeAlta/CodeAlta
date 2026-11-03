using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Control;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// What machines publish over their control connection: their inventory and the facts of their sessions. Only the
/// allow-list is kept (<c>doc/fleet/security-and-privacy.md</c>, what the server sees): the contracts carry no title,
/// project or path, and the sealed metadata stays opaque text.
/// </summary>
public sealed class PublicationService
{
    /// <summary>The most facts in one batch.</summary>
    public const int MaxFactsPerBatch = 500;

    /// <summary>The largest sealed metadata of a session, in characters.</summary>
    public const int MaxSealedMetadataLength = 64 * 1024;

    private const int MaxIdLength = 128;
    private const int MaxTextLength = 128;
    private const int MaxListItems = 64;

    private readonly ICoordinatorStore _store;
    private readonly DirectoryHub _directory;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="PublicationService"/> class.</summary>
    /// <param name="store">The store.</param>
    /// <param name="directory">Where changes are announced.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PublicationService(ICoordinatorStore store, DirectoryHub directory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _directory = directory;
        _time = time;
    }

    /// <summary>Keeps the inventory of a machine and announces it when it changed.</summary>
    /// <param name="device">The device of the machine.</param>
    /// <param name="publication">The publication.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The acknowledged sequence.</returns>
    /// <exception cref="CoordinatorException">The inventory does not read (<see cref="CoordinatorErrorCodes.PublicationInvalid"/>).</exception>
    public async Task<FleetPublishAck> PublishInventoryAsync(DeviceRecord device, FleetInventoryPublish? publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (publication?.Inventory is not { } inventory || publication.Sequence <= 0) throw Invalid("inventory.publish needs a sequence from 1 and an inventory.");
        // Written again from the contract: what the device sent beyond it is not kept.
        var kept = new FleetMachineInventory(Text(inventory.Name, "name", required: true)!, Text(inventory.Os, "os", required: true)!,
            Text(inventory.Architecture, "architecture"), Text(inventory.AppVersion, "appVersion"), List(inventory.Capabilities, "capabilities"),
            List(inventory.Labels, "labels"), List(inventory.Providers, "providers"));
        var now = _time.GetUtcNow();
        var machine = new MachineRecord(device.Id, device.OrganizationId, JsonSerializer.Serialize(kept, FleetServerJsonContext.Default.FleetMachineInventory),
            publication.Sequence, now);
        var result = await _store.PublishInventoryAsync(machine, cancellationToken).ConfigureAwait(false);
        if (result.Applied)
        {
            _directory.Publish(device.OrganizationId, new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Machine, device.Id,
                Machine: new FleetDirectoryMachine(device.Id, device.Kind, kept, now)));
        }

        return new FleetPublishAck(result.Ack);
    }

    /// <summary>Keeps the facts of the sessions of a machine and announces those that changed.</summary>
    /// <param name="device">The device of the machine.</param>
    /// <param name="publication">The publication.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The acknowledged sequence and what happened to the facts.</returns>
    /// <exception cref="CoordinatorException">A fact does not read (<see cref="CoordinatorErrorCodes.PublicationInvalid"/>).</exception>
    public async Task<FleetFactsPublishResult> PublishFactsAsync(DeviceRecord device, FleetFactsPublish? publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (publication?.Facts is not { } facts || publication.Sequence <= 0) throw Invalid("facts.publish needs a sequence from 1 and facts.");
        if (facts.Count > MaxFactsPerBatch) throw Invalid($"facts.publish takes at most {MaxFactsPerBatch} facts.");
        var now = _time.GetUtcNow();
        var records = new List<SessionRecord>(facts.Count);
        foreach (var fact in facts)
        {
            if (fact is null) throw Invalid("A fact cannot be null.");
            if (fact.Epoch < 0 || fact.Version < 0) throw Invalid("The epoch and the version of a fact cannot be negative.");
            if (fact.SealedMetadata is { Length: > MaxSealedMetadataLength }) throw Invalid($"The sealed metadata of a session is at most {MaxSealedMetadataLength} characters.");
            var counts = fact.Counts ?? new FleetSessionCounts();
            if (counts.Turns < 0 || counts.ToolCalls < 0 || counts.InputTokens < 0 || counts.OutputTokens < 0) throw Invalid("The counts of a session cannot be negative.");
            records.Add(new SessionRecord(Text(fact.SessionId, "sessionId", required: true, MaxIdLength)!, device.OrganizationId, device.Id, fact.Epoch, fact.Version,
                Text(fact.State, "state", required: true)!, fact.CreatedAt, fact.UpdatedAt, Text(fact.ProviderType, "providerType"), Text(fact.Model, "model"),
                counts.Turns, counts.ToolCalls, counts.InputTokens, counts.OutputTokens, fact.SealedMetadata, now));
        }

        var result = await _store.PublishSessionFactsAsync(device.Id, publication.Sequence, records, cancellationToken).ConfigureAwait(false);
        int applied = 0, unchanged = 0;
        var stale = new List<string>();
        foreach (var (record, outcome) in result.Outcomes)
        {
            switch (outcome)
            {
                case SessionFactOutcome.Applied:
                    applied++;
                    _directory.Publish(device.OrganizationId, new FleetDirectoryChange(FleetServerMethods.ChangeKinds.Session, record.Id, Session: record.ToDirectory()));
                    break;
                case SessionFactOutcome.Unchanged:
                    unchanged++;
                    break;
                default:
                    stale.Add(record.Id);
                    break;
            }
        }

        return new FleetFactsPublishResult(result.Ack, applied, unchanged, stale);
    }

    private static string? Text(string? value, string field, bool required = false, int maxLength = MaxTextLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (required) throw Invalid($"{field} is required.");
            return null;
        }

        if (value.Length > maxLength || value.Any(char.IsControl)) throw Invalid($"{field} is at most {maxLength} characters, without control characters.");
        return value;
    }

    private static IReadOnlyList<string>? List(IReadOnlyList<string>? values, string field)
    {
        if (values is null) return null;
        if (values.Count > MaxListItems) throw Invalid($"{field} has at most {MaxListItems} items.");
        return values.Select(value => Text(value, field, required: true)!).ToArray();
    }

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.PublicationInvalid, message);
}
