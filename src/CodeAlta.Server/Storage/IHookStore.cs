namespace CodeAlta.Server.Storage;

/// <summary>A hook of the server, for one machine of one person.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="HookId">Its id, part of its address.</param>
/// <param name="OwnerPersonId">The person who made it, who owns the machine.</param>
/// <param name="MachineId">The machine its events go to.</param>
/// <param name="Source">What pushes to it: <c>incident_io</c> or <c>webhook</c>.</param>
/// <param name="Label">Its name.</param>
/// <param name="CreatedByDeviceId">The controller device that made it.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="LastReceivedAt">When it last received an event.</param>
public sealed record HookRecord(string OrganizationId, string HookId, string OwnerPersonId, string MachineId, string Source, string Label, string CreatedByDeviceId,
    DateTimeOffset CreatedAt, DateTimeOffset? LastReceivedAt)
{
    /// <summary>Gets how many of its events wait for the machine, when listed.</summary>
    public int Pending { get; init; }
}

/// <summary>An event a hook received.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="DeliveryId">The id of the delivery.</param>
/// <param name="HookId">The hook.</param>
/// <param name="MachineId">The machine it goes to.</param>
/// <param name="EventKey">What makes a repeated push the same event.</param>
/// <param name="ReceivedAt">When it came.</param>
/// <param name="ExpiresAt">Until when it waits for its machine.</param>
/// <param name="State">Its state (<see cref="CodeAlta.Fleet.Protocol.Api.HookEventStates"/>).</param>
/// <param name="Message">What the machine said of it.</param>
/// <param name="AnsweredAt">When the machine answered.</param>
/// <param name="Headers">Its headers as a JSON object, while it waits.</param>
/// <param name="Body">Its body, while it waits.</param>
public sealed record HookEventRecord(string OrganizationId, string DeliveryId, string HookId, string MachineId, string EventKey, DateTimeOffset ReceivedAt, DateTimeOffset ExpiresAt,
    string State, string? Message, DateTimeOffset? AnsweredAt, string? Headers, byte[]? Body);

/// <summary>The hooks of the server and the events they received (migration 0015).</summary>
public interface IHookStore
{
    /// <summary>Stores a new hook, with its audit record.</summary>
    /// <param name="hook">The hook.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once it is stored.</returns>
    ValueTask CreateHookAsync(HookRecord hook, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Gets a hook by its id.</summary>
    /// <param name="hookId">The hook.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The hook, or null.</returns>
    ValueTask<HookRecord?> GetHookAsync(string hookId, CancellationToken cancellationToken = default);

    /// <summary>Lists the hooks of a person, with how many of their events wait.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="ownerPersonId">The person.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The hooks.</returns>
    ValueTask<IReadOnlyList<HookRecord>> ListHooksAsync(string organizationId, string ownerPersonId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a hook and its events, with its audit record.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="hookId">The hook.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether there was such a hook.</returns>
    ValueTask<bool> DeleteHookAsync(string organizationId, string hookId, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Records an event a hook received, unless the same event was received before.</summary>
    /// <param name="hookEvent">The event.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The delivery id of the event recorded, or of the same one received before, and whether it was a repeat.</returns>
    ValueTask<(string DeliveryId, bool Duplicate)> AddHookEventAsync(HookEventRecord hookEvent, CancellationToken cancellationToken = default);

    /// <summary>Lists the events that wait for a machine and have not expired, the oldest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="now">The time.</param>
    /// <param name="limit">The most events.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The events, with their headers and bodies.</returns>
    ValueTask<IReadOnlyList<HookEventRecord>> ListPendingHookEventsAsync(string organizationId, string machineId, DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

    /// <summary>Records what a machine did with an event it was given, and forgets its headers and body.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="machineId">The machine, which must be the one of the event.</param>
    /// <param name="deliveryId">The delivery.</param>
    /// <param name="state">Its new state.</param>
    /// <param name="message">What the machine said.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the machine had such a pending event.</returns>
    ValueTask<bool> AnswerHookEventAsync(string organizationId, string machineId, string deliveryId, string state, string? message, DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the latest events of a hook, without their content, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="hookId">The hook.</param>
    /// <param name="limit">The most events.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The events.</returns>
    ValueTask<IReadOnlyList<HookEventRecord>> ListHookEventsAsync(string organizationId, string hookId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires the events whose machine did not take them in time (their content goes), and forgets the facts of events older
    /// than a moment.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="forgetBefore">Events received before this are forgotten.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many events expired.</returns>
    ValueTask<int> ExpireHookEventsAsync(DateTimeOffset now, DateTimeOffset forgetBefore, CancellationToken cancellationToken = default);
}
