using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Storage;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Services;

/// <summary>
/// The automations a person gives the machines of the organization (<c>doc/fleet/orchestration.md</c>): a controller device
/// of that person signs each version for one of the person's machines, sealed for the machine and the person's controller
/// devices, and sends it with <c>PUT /api/v1/automations/{id}</c>; the server checks the signature and the facts it reads,
/// keeps the version, and delivers it as signed to its machine, now or when it connects
/// (<see cref="FleetAutomationMethods.Assigned"/>). The machine verifies it against its own anchored device list before it
/// takes it: the server alone cannot make it run anything. The machines claim their due times here and report their runs.
/// </summary>
internal sealed class FleetAutomationService
{
    /// <summary>The most runs kept per automation.</summary>
    internal const int RunsKept = 200;

    private const int MaxRunsPerReport = 100;
    private const int MaxMessageLength = 500;
    private const int MaxRunIdLength = 128;
    private const int MaxOccurrenceLength = 256;
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxLease = TimeSpan.FromHours(1);

    private readonly ICoordinatorStore _store;
    private readonly IAutomationStore _automations;
    private readonly PresenceRegistry _presence;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="FleetAutomationService"/> class.</summary>
    /// <param name="store">The store of the devices and the audit.</param>
    /// <param name="automations">The store of the automations.</param>
    /// <param name="presence">The devices online, to deliver a version to its machine at once.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The log.</param>
    public FleetAutomationService(ICoordinatorStore store, IAutomationStore automations, PresenceRegistry presence, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(automations);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        (_store, _automations, _presence, _time, _logger) = (store, automations, presence, time, logger);
    }

    /// <summary>Lists the automations of the person of the calling controller device, per machine.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The automations.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<AutomationListDto> ListAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var person = RequirePersonController(actor);
        var records = await _automations.ListAutomationsAsync(actor.OrganizationId, person, cancellationToken).ConfigureAwait(false);
        return new AutomationListDto([.. records.Select(ToDto)]);
    }

    /// <summary>
    /// Stores a version the calling controller device signed for one machine of its person (or a removal), when it is
    /// newer than the one held for that machine, and delivers it to the machine.
    /// </summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The automation the path names.</param>
    /// <param name="request">The signed version.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The version held now.</returns>
    /// <exception cref="CoordinatorException">The caller, the version or its machine does not hold, or it is not newer.</exception>
    public async Task<AutomationDto> PutAsync(DeviceRecord actor, string id, PutAutomationRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var person = RequirePersonController(actor);
        if (request?.Version is not { Payload.Length: > 0, Signature.Length: > 0 } signed || signed.Payload.Length > FleetAutomationVersionDocuments.MaxPayloadLength)
            throw Invalid("The request needs a version of the automation signed by the calling device.");
        if (!FleetDevicePublicKey.TryParse(actor.PublicKey, out var key)) throw new InvalidOperationException("The key of a device in the table does not read.");
        if (!key.Verify(FleetAutomationVersionDocuments.GetSignedBytes(signed.Payload), signed.Signature))
            throw Invalid("The signature of the version does not verify with the key of the calling device.");
        if (!FleetAutomationVersionDocuments.TryParse(signed.Payload, out var version)) throw Invalid("The version does not read.");
        if (FleetAutomationVersionDocuments.Check(version) is { } reason) throw Invalid($"The version does not hold: {reason}.");
        if (!string.Equals(version.AutomationId, id, StringComparison.Ordinal)) throw Invalid("The version names another automation than the path.");
        if (!string.Equals(version.SignerDeviceId, actor.Id, StringComparison.Ordinal) || !string.Equals(version.SignerKey, actor.PublicKey, StringComparison.Ordinal))
            throw Invalid("The version must name the device that signs it, with its key.");
        if (!string.Equals(version.OrganizationId, actor.OrganizationId, StringComparison.Ordinal)) throw Invalid("The version must name the organization of the server.");
        // What an automation does is for its machine and its person: the server takes it sealed, never in clear.
        if (version.Definition is not null) throw Invalid("A version given to the server holds its definition sealed for its machine, never in clear.");
        var machine = await _store.GetDeviceAsync(version.MachineId, cancellationToken).ConfigureAwait(false);
        if (machine is null || machine.OrganizationId != actor.OrganizationId || machine.Kind != FleetDeviceKinds.Node)
            throw CoordinatorException.NotFound(CoordinatorErrorCodes.DeviceNotFound, "No machine of the organization has this id.");
        if (machine.Status != DeviceStatuses.Approved) throw CoordinatorException.Conflict(CoordinatorErrorCodes.DeviceState, "The machine is not a member of the device list.");
        if (!string.Equals(machine.OwnerPersonId, person, StringComparison.Ordinal))
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "A person gives automations to the machines they own only.");

        var record = new AutomationRecord(actor.OrganizationId, version.AutomationId, version.MachineId, person, version.Number, version.Removed, version.Enabled,
            JsonSerializer.Serialize(version.Triggers, FleetAutomationApiJsonContext.Default.IReadOnlyListString), signed.Payload, signed.Signature, actor.Id,
            _time.GetUtcNow(), AutomationDeliveryStates.Pending, null);
        var audit = origin.Audit(version.Removed ? "automation.remove" : "automation.put", actor, version.AutomationId, details: Details(version));
        if (!await _automations.PutAutomationAsync(record, audit, CancellationToken.None).ConfigureAwait(false))
        {
            var held = await _automations.GetAutomationAsync(actor.OrganizationId, version.AutomationId, version.MachineId, cancellationToken).ConfigureAwait(false);
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AutomationConflict,
                $"The server holds version {held?.Number ?? version.Number} of this automation for this machine: a version is never replaced by one that is not newer.");
        }

        _logger.LogInformation("Automation {Automation} version {Number} for {Machine} was {Action} by {Device}", version.AutomationId, version.Number, version.MachineId,
            version.Removed ? "removed" : "stored", actor.Id);
        if (_presence.Get(version.MachineId) is { } session) _ = DeliverAsync(session, [record]);
        return ToDto(record);
    }

    /// <summary>Forgets an automation for a machine once the machine took away what it held (an acknowledged removal).</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The automation.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once it is forgotten.</returns>
    /// <exception cref="CoordinatorException">It is not the caller's, or the machine has not taken it away yet.</exception>
    public async Task DeleteAsync(DeviceRecord actor, string id, string? machineId, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var person = RequirePersonController(actor);
        if (string.IsNullOrEmpty(machineId)) throw Invalid("Name the machine: ?machine=<device id>.");
        var held = await _automations.GetAutomationAsync(actor.OrganizationId, id, machineId, cancellationToken).ConfigureAwait(false);
        if (held is null || held.OwnerPersonId != person) throw CoordinatorException.NotFound(CoordinatorErrorCodes.AutomationNotFound, "No automation of yours has this id on this machine.");
        if (!held.Removed || held.DeliveryState != AutomationDeliveryStates.Applied)
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AutomationConflict,
                "The machine still holds this automation: give it a signed removal first, and forget it once the machine took it away.");
        await _automations.DeleteAutomationAsync(actor.OrganizationId, id, machineId, origin.Audit("automation.delete", actor, id), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Passes a run the calling controller device asked by hand, and signed, to the machine of an automation it gave
    /// through this server; the machine verifies the request and reports the run as the others. The server keeps nothing
    /// of it: a machine that is offline does not get it.
    /// </summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The automation the path names.</param>
    /// <param name="request">The signed run request.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The run as asked, <c>requested</c>.</returns>
    /// <exception cref="CoordinatorException">The caller or the request does not hold, the server holds no such automation for the machine, or the machine is offline.</exception>
    public async Task<AutomationRunDto> RunAsync(DeviceRecord actor, string id, RunAutomationRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var person = RequirePersonController(actor);
        if (request?.Request is not { Payload.Length: > 0, Signature.Length: > 0 } signed) throw Invalid("The request needs a run signed by the calling device.");
        if (!FleetDevicePublicKey.TryParse(actor.PublicKey, out var key)) throw new InvalidOperationException("The key of a device in the table does not read.");
        if (!FleetAutomationRunRequestDocuments.TryRead(signed.Payload, out var run, out var reason)) throw Invalid($"The run request does not hold: {reason}.");
        if (!key.Verify(FleetAutomationRunRequestDocuments.GetSignedBytes(signed.Payload), signed.Signature))
            throw Invalid("The signature of the run request does not verify with the key of the calling device.");
        if (run.AutomationId != id) throw Invalid("The run request names another automation than the path.");
        if (run.SignerDeviceId != actor.Id || run.SignerKey != actor.PublicKey) throw Invalid("The run request must name the device that signs it, with its key.");
        if (run.OrganizationId != actor.OrganizationId) throw Invalid("The run request must name the organization of the server.");
        var held = await _automations.GetAutomationAsync(actor.OrganizationId, id, run.MachineId, cancellationToken).ConfigureAwait(false);
        if (held is null || held.Removed || held.OwnerPersonId != person)
            throw CoordinatorException.NotFound(CoordinatorErrorCodes.AutomationNotFound, "No automation of yours has this id on this machine.");
        if (_presence.Get(run.MachineId) is not { } session)
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AutomationConflict, "The machine is offline: it runs this automation by its triggers, and by hand once it is online.");
        try
        {
            using var timeout = new CancellationTokenSource(PushTimeout);
            await session.Connection.SendEventAsync(new FleetEvent(FleetAutomationMethods.Run,
                JsonSerializer.SerializeToElement(signed, FleetAutomationRunRequestJsonContext.Default.FleetSignedAutomationRunRequest)), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetException or FleetConnectionClosedException or OperationCanceledException or InvalidOperationException)
        {
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.AutomationConflict, "The machine did not take the request: try again once it is online.");
        }

        _logger.LogInformation("Automation {Automation} was asked to run on {Machine} by {Device} (run {Run})", id, run.MachineId, actor.Id, run.RunId);
        await _store.AppendAuditAsync(origin.Audit("automation.run", actor, id), CancellationToken.None).ConfigureAwait(false);
        return new AutomationRunDto(run.RunId, id, run.MachineId, held.Number, "manual", "requested", run.Time, null, null, null, null, null);
    }

    /// <summary>Lists the runs of an automation of the caller's person, as its machines reported them, the newest first.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The automation.</param>
    /// <param name="limit">The most runs; null for 50.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The runs.</returns>
    /// <exception cref="CoordinatorException">No automation of the caller's person has this id.</exception>
    public async Task<AutomationRunListDto> ListRunsAsync(DeviceRecord actor, string id, int? limit, CancellationToken cancellationToken)
    {
        var person = RequirePersonController(actor);
        var records = await _automations.ListAutomationsAsync(actor.OrganizationId, person, cancellationToken).ConfigureAwait(false);
        if (!records.Any(record => record.AutomationId == id))
            throw CoordinatorException.NotFound(CoordinatorErrorCodes.AutomationNotFound, "No automation of yours has this id.");
        var runs = await _automations.ListAutomationRunsAsync(actor.OrganizationId, id, Math.Clamp(limit ?? 50, 1, RunsKept), cancellationToken).ConfigureAwait(false);
        return new AutomationRunListDto([.. runs.Select(static run => new AutomationRunDto(run.RunId, run.AutomationId, run.MachineId, run.Number, run.Trigger, run.Status,
            run.StartedAt, run.EndedAt, run.Occurrence, run.LeaseUntil, run.SessionId, run.Message)
        {
            InputTokens = run.InputTokens, OutputTokens = run.OutputTokens, Cost = run.Cost, CostUnit = run.CostUnit,
        })]);
    }

    /// <summary>Sends a machine that connects the versions it has not taken yet.</summary>
    /// <param name="session">Its control connection.</param>
    /// <returns>A task that completes once they were sent or could not be.</returns>
    public async Task SendPendingAsync(ControlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Device.Kind != FleetDeviceKinds.Node) return;
        try
        {
            var pending = await _automations.ListPendingAutomationsAsync(session.Device.OrganizationId, session.Device.Id).ConfigureAwait(false);
            if (pending.Count > 0) await DeliverAsync(session, pending).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogDebug("The automations were not sent to {Device}: {Reason}", session.Device.Id, exception.Message);
        }
    }

    /// <summary>Records what a machine did with the versions delivered to it.</summary>
    /// <param name="device">The machine.</param>
    /// <param name="acks">Its answers.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once they are recorded.</returns>
    /// <exception cref="CoordinatorException">The answers do not read.</exception>
    public async Task AcknowledgeAsync(DeviceRecord device, FleetAutomationAcks? acks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (acks?.Acks is not { Count: > 0 and <= 1000 } list || list.Any(static ack => string.IsNullOrEmpty(ack?.AutomationId) || ack.Number <= 0))
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.PublicationInvalid, "automations.ack needs one answer per version.");
        foreach (var ack in list)
        {
            var state = ack.Applied ? AutomationDeliveryStates.Applied : AutomationDeliveryStates.Refused;
            if (await _automations.AcknowledgeAutomationAsync(device.OrganizationId, ack.AutomationId, device.Id, ack.Number, state, Line(ack.Message), cancellationToken)
                    .ConfigureAwait(false) && !ack.Applied)
                _logger.LogWarning("Machine {Device} refused version {Number} of automation {Automation}: {Reason}", device.Id, ack.Number, ack.AutomationId, Line(ack.Message));
        }
    }

    /// <summary>
    /// Claims a due time of an automation for a run of a machine: granted when no run took it, on any machine; the server
    /// keeps the claim, so that the automation given to another machine later does not run that time again.
    /// </summary>
    /// <param name="device">The machine.</param>
    /// <param name="request">The claim.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the run may start.</returns>
    /// <exception cref="CoordinatorException">The claim does not read.</exception>
    public async Task<FleetAutomationClaimResult> ClaimAsync(DeviceRecord device, FleetAutomationClaimRequest? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (request is not { AutomationId.Length: > 0, Occurrence.Length: > 0 and <= MaxOccurrenceLength, RunId.Length: > 0 and <= MaxRunIdLength } || request.Number <= 0)
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.PublicationInvalid, "automations.claim needs an automation, a version, a due time and a run.");
        var held = await _automations.GetAutomationAsync(device.OrganizationId, request.AutomationId, device.Id, cancellationToken).ConfigureAwait(false);
        if (held is null || held.Removed) return new FleetAutomationClaimResult(false, "The server holds this automation for another machine, or for none.");
        var now = _time.GetUtcNow();
        var lease = request.LeaseUntil < now ? now : request.LeaseUntil > now + MaxLease ? now + MaxLease : request.LeaseUntil;
        var outcome = await _automations.ClaimAutomationOccurrenceAsync(new AutomationRunRecord(device.OrganizationId, request.RunId, request.AutomationId, device.Id,
            request.Number, "schedule", AutomationRunRecord.Claimed, request.Occurrence, now, null, lease, null, null, now), cancellationToken).ConfigureAwait(false);
        if (outcome.Granted) return new FleetAutomationClaimResult(true, null);
        var holder = outcome.Holder!;
        return new FleetAutomationClaimResult(false, holder.MachineId == device.Id
            ? "This due time was already taken by another run of this machine."
            : $"This due time was already taken by a run of another machine ({holder.MachineId}).");
    }

    /// <summary>Records the runs a machine reports, for automations the server holds for it.</summary>
    /// <param name="device">The machine.</param>
    /// <param name="facts">The runs.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once they are recorded.</returns>
    /// <exception cref="CoordinatorException">The runs do not read.</exception>
    public async Task RecordRunsAsync(DeviceRecord device, FleetAutomationRunFacts? facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (facts?.Runs is not { Count: <= MaxRunsPerReport } runs || runs.Count == 0 && facts.Schedules is not { Count: > 0 }
            || facts.Schedules is { Count: > MaxRunsPerReport } || facts.Schedules?.Any(static schedule => schedule is not { AutomationId.Length: > 0 }) == true
            || runs.Any(static run => run is not { RunId.Length: > 0 and <= MaxRunIdLength, AutomationId.Length: > 0, Trigger.Length: > 0 and <= 32, Status.Length: > 0 and <= 32 }
                || run.SessionId is { Length: > 128 }))
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.PublicationInvalid, $"automations.runs needs 1 to {MaxRunsPerReport} runs, each with its ids, trigger and status.");
        var now = _time.GetUtcNow();
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        var records = new List<AutomationRunRecord>(runs.Count);
        foreach (var run in runs)
        {
            // Only the runs of what this server gave the machine: the automations of its own profile stay with its owner.
            if (!known.TryGetValue(run.AutomationId, out var held))
            {
                held = await _automations.GetAutomationAsync(device.OrganizationId, run.AutomationId, device.Id, cancellationToken).ConfigureAwait(false) is not null;
                known[run.AutomationId] = held;
            }

            if (!held) continue;
            records.Add(new AutomationRunRecord(device.OrganizationId, run.RunId, run.AutomationId, device.Id, Math.Max(0, run.Number), run.Trigger, run.Status, null,
                run.StartedAt, run.EndedAt, run.EndedAt, run.SessionId, Line(run.Message), now)
            {
                // Facts of what the session used; a value that does not hold is dropped, not the run.
                InputTokens = run.InputTokens is >= 0 ? run.InputTokens : null, OutputTokens = run.OutputTokens is >= 0 ? run.OutputTokens : null,
                Cost = run.Cost is { } cost && double.IsFinite(cost) && cost >= 0 && IsUnit(run.CostUnit) ? cost : null,
                CostUnit = run.Cost is { } priced && double.IsFinite(priced) && priced >= 0 && IsUnit(run.CostUnit) ? run.CostUnit : null,
            });
        }

        if (records.Count > 0) await _automations.RecordAutomationRunsAsync(records, RunsKept, cancellationToken).ConfigureAwait(false);
        // When its schedules are next due, for what the server holds for the machine (the store ignores the rest).
        if (facts.Schedules is { Count: > 0 } schedules)
            await _automations.SetAutomationNextDueAsync(device.OrganizationId, device.Id, [.. schedules.Select(static schedule => (schedule.AutomationId, schedule.NextDue))],
                cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverAsync(ControlSession session, IReadOnlyList<AutomationRecord> records)
    {
        try
        {
            // One version per event: a version may be large, and a frame of the connection is bounded.
            foreach (var record in records)
            {
                using var timeout = new CancellationTokenSource(PushTimeout);
                var delivery = new FleetAutomationDelivery([new FleetSignedAutomationVersion(record.Payload, record.Signature)]);
                await session.Connection.SendEventAsync(new FleetEvent(FleetAutomationMethods.Assigned,
                    JsonSerializer.SerializeToElement(delivery, FleetAutomationServerJsonContext.Default.FleetAutomationDelivery)), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is FleetException or FleetConnectionClosedException or OperationCanceledException or InvalidOperationException)
        {
            // The machine gets it when it connects next: it stays pending until the machine answers.
            _logger.LogDebug("The automations were not delivered to {Device}: {Reason}", session.Device.Id, exception.Message);
        }
    }

    // Personal automations: a controller device of a person, who gives automations to the machines they own.
    private static string RequirePersonController(DeviceRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != FleetDeviceKinds.Controller || actor.OwnerPersonId is not { } person)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a controller device of a person gives automations to machines.");
        return person;
    }

    private static AutomationDto ToDto(AutomationRecord record)
    {
        IReadOnlyList<string> triggers;
        try { triggers = JsonSerializer.Deserialize(record.Triggers, FleetAutomationApiJsonContext.Default.IReadOnlyListString) ?? []; }
        catch (JsonException) { triggers = []; }
        return new AutomationDto(record.AutomationId, record.MachineId, record.Number, record.Removed, record.Enabled, triggers, record.SignerDeviceId, record.UpdatedAt,
            record.DeliveryState, record.DeliveryMessage, new FleetSignedAutomationVersion(record.Payload, record.Signature)) { NextDue = record.Removed ? null : record.NextDue };
    }

    private static bool IsUnit(string? unit) => unit is { Length: > 0 and <= 32 } && !unit.Any(char.IsControl);

    private static string? Line(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxMessageLength ? line : line[..MaxMessageLength];
    }

    private static string Details(FleetAutomationVersion version)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("machine", version.MachineId);
            writer.WriteNumber("number", version.Number);
            writer.WriteBoolean("enabled", version.Enabled);
            writer.WriteStartArray("triggers");
            foreach (var trigger in version.Triggers) writer.WriteStringValue(trigger);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.AutomationInvalid, message);
}
