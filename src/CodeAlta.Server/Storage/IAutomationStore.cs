namespace CodeAlta.Server.Storage;

/// <summary>The states of the delivery of an automation version to its machine.</summary>
public static class AutomationDeliveryStates
{
    /// <summary>The machine has not taken it yet: it is sent when the machine connects.</summary>
    public const string Pending = "pending";

    /// <summary>The machine holds it.</summary>
    public const string Applied = "applied";

    /// <summary>The machine refused it (its signature, its number, its content): it is not sent again.</summary>
    public const string Refused = "refused";
}

/// <summary>An automation version the server holds for a machine.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="AutomationId">The automation.</param>
/// <param name="MachineId">The machine it is for.</param>
/// <param name="OwnerPersonId">The person whose controller device signed it, who owns the machine.</param>
/// <param name="Number">The number of the version.</param>
/// <param name="Removed">Whether it takes the automation away.</param>
/// <param name="Enabled">Whether its triggers start it.</param>
/// <param name="Triggers">The kinds of its triggers, as JSON text.</param>
/// <param name="Payload">The JSON text that was signed.</param>
/// <param name="Signature">The signature.</param>
/// <param name="SignerDeviceId">The controller device that signed it.</param>
/// <param name="UpdatedAt">When it was stored.</param>
/// <param name="DeliveryState">Where its delivery stands (<see cref="AutomationDeliveryStates"/>).</param>
/// <param name="DeliveryMessage">Why the machine refused it; null otherwise.</param>
public sealed record AutomationRecord(string OrganizationId, string AutomationId, string MachineId, string OwnerPersonId, long Number, bool Removed, bool Enabled,
    string Triggers, string Payload, string Signature, string SignerDeviceId, DateTimeOffset UpdatedAt, string DeliveryState, string? DeliveryMessage)
{
    /// <summary>Gets when a schedule of it is next due on its machine, as the machine last reported it.</summary>
    public DateTimeOffset? NextDue { get; init; }
}

/// <summary>A run of an automation a machine reported, or a due time it claimed.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="RunId">The run.</param>
/// <param name="AutomationId">The automation.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Number">The version that ran.</param>
/// <param name="Trigger">What started it.</param>
/// <param name="Status">Its status; <c>claimed</c> until the machine reports it.</param>
/// <param name="Occurrence">The due time it took; null for a run by hand or by an event.</param>
/// <param name="StartedAt">When it started or was claimed.</param>
/// <param name="EndedAt">When it ended.</param>
/// <param name="LeaseUntil">Until when its machine is known to be at work on it.</param>
/// <param name="SessionId">The session it started on its machine.</param>
/// <param name="Message">Why it failed or was skipped.</param>
/// <param name="UpdatedAt">When the server last wrote it.</param>
public sealed record AutomationRunRecord(string OrganizationId, string RunId, string AutomationId, string MachineId, long Number, string Trigger, string Status,
    string? Occurrence, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, DateTimeOffset? LeaseUntil, string? SessionId, string? Message, DateTimeOffset UpdatedAt)
{
    /// <summary>The status of a due time claimed and not reported yet.</summary>
    public const string Claimed = "claimed";

    /// <summary>Gets the input tokens of its session, the prompt cache included; null when unknown.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Gets the output tokens of its session; null when unknown.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Gets the cost the provider reported for its session, in <see cref="CostUnit"/>; null when none was.</summary>
    public double? Cost { get; init; }

    /// <summary>Gets the unit of <see cref="Cost"/>: <c>usd</c>, or the one the provider named.</summary>
    public string? CostUnit { get; init; }
}

/// <summary>What a claim of a due time found.</summary>
/// <param name="Granted">Whether the claim is this run's (taken now, or taken before by the same run).</param>
/// <param name="Holder">The run that holds it, when another one does.</param>
public sealed record AutomationClaimOutcome(bool Granted, AutomationRunRecord? Holder);

/// <summary>The automations of the organization and their runs, as the store keeps them.</summary>
public interface IAutomationStore
{
    /// <summary>Stores a version for its machine when it is newer than the one held for that machine, audited in the same transaction.</summary>
    /// <param name="automation">The version, pending delivery.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="false"/> when the version held for that machine has the same number or a later one: nothing was written.</returns>
    ValueTask<bool> PutAutomationAsync(AutomationRecord automation, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Gets the version held for an automation and a machine.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The version, or <see langword="null"/>.</returns>
    ValueTask<AutomationRecord?> GetAutomationAsync(string organizationId, string automationId, string machineId, CancellationToken cancellationToken = default);

    /// <summary>Lists the versions of the automations of a person, by automation and machine.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="ownerPersonId">The person.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The versions.</returns>
    ValueTask<IReadOnlyList<AutomationRecord>> ListAutomationsAsync(string organizationId, string ownerPersonId, CancellationToken cancellationToken = default);

    /// <summary>Lists who holds each automation of the organization that is not removed: its id and the person of its versions, once per pair.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The automations and their people, by automation.</returns>
    ValueTask<IReadOnlyList<(string AutomationId, string PersonId)>> ListAutomationHoldersAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Lists the versions a machine has not taken yet.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The versions, by automation.</returns>
    ValueTask<IReadOnlyList<AutomationRecord>> ListPendingAutomationsAsync(string organizationId, string machineId, CancellationToken cancellationToken = default);

    /// <summary>Records what a machine did with a version, when it is still the one held for it.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="number">The number of the version.</param>
    /// <param name="state"><see cref="AutomationDeliveryStates.Applied"/> or <see cref="AutomationDeliveryStates.Refused"/>.</param>
    /// <param name="message">Why it was refused.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the version held was that one.</returns>
    ValueTask<bool> AcknowledgeAutomationAsync(string organizationId, string automationId, string machineId, long number, string state, string? message,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets the version held for an automation and a machine, audited in the same transaction.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether one was held.</returns>
    ValueTask<bool> DeleteAutomationAsync(string organizationId, string automationId, string machineId, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a due time of an automation for a run: granted when nobody claimed it, or when this run did already, whose
    /// lease is then renewed while it has not ended; a due time claimed by another run, on any machine, stays its.
    /// </summary>
    /// <param name="claim">The run that claims, with its occurrence and lease.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the claim found.</returns>
    ValueTask<AutomationClaimOutcome> ClaimAutomationOccurrenceAsync(AutomationRunRecord claim, CancellationToken cancellationToken = default);

    /// <summary>Writes the runs a machine reported: new ones are added, known ones of that machine updated, those of another machine left alone.</summary>
    /// <param name="runs">The runs.</param>
    /// <param name="keep">How many runs are kept per automation; older ones are forgotten.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many were written.</returns>
    ValueTask<int> RecordAutomationRunsAsync(IReadOnlyList<AutomationRunRecord> runs, int keep, CancellationToken cancellationToken = default);

    /// <summary>Records when the schedules of automations are next due on their machine, for the versions held for it.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="machineId">The machine that reports them.</param>
    /// <param name="schedules">The automations and their next due times.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once they are written.</returns>
    ValueTask SetAutomationNextDueAsync(string organizationId, string machineId, IReadOnlyList<(string AutomationId, DateTimeOffset? NextDue)> schedules,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the runs of an automation, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="limit">The most runs.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The runs.</returns>
    ValueTask<IReadOnlyList<AutomationRunRecord>> ListAutomationRunsAsync(string organizationId, string automationId, int limit, CancellationToken cancellationToken = default);
}
