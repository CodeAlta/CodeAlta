namespace CodeAlta.Server.Storage;

/// <summary>A budget as the server keeps it: the signed document of its owner and what the server read of it.</summary>
/// <param name="Id">The budget.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="OwnerPersonId">The person who owns it.</param>
/// <param name="Number">Its version.</param>
/// <param name="Removed">Whether it is taken away.</param>
/// <param name="Payload">The signed JSON text.</param>
/// <param name="Signature">Its signature.</param>
/// <param name="SignerDeviceId">The controller device that signed it.</param>
/// <param name="UpdatedAt">When the server received this version.</param>
public sealed record BudgetRecord(string Id, string OrganizationId, string OwnerPersonId, long Number, bool Removed, string Payload, string Signature, string SignerDeviceId,
    DateTimeOffset UpdatedAt);

/// <summary>What the statistics facts of a budget count: whose machines, which scope, which measure, which quarter hours.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="OwnerPersonId">The owner of the budget: only the facts of the machines of that person count.</param>
/// <param name="ScopeKind">The scope (<see cref="CodeAlta.Fleet.Protocol.FleetBudgetScopeKinds"/>).</param>
/// <param name="ScopeId">Its id; null for the person.</param>
/// <param name="Measure">The measure (<see cref="CodeAlta.Fleet.Protocol.FleetBudgetMeasures"/>).</param>
/// <param name="FromQuarter">The first quarter hour.</param>
/// <param name="ToQuarter">The quarter hour after the last.</param>
public sealed record BudgetUsageQuery(string OrganizationId, string OwnerPersonId, string ScopeKind, string? ScopeId, string Measure, long FromQuarter, long ToQuarter);

/// <summary>What a machine asks of an enforced budget, and what the server gives it.</summary>
/// <param name="BudgetId">The budget.</param>
/// <param name="PeriodStart">The period, its first quarter hour.</param>
/// <param name="DeviceId">The machine.</param>
/// <param name="Limit">The limit of the budget.</param>
/// <param name="Spent">What the machine says it spent in the period.</param>
/// <param name="Needed">What it needs to hold beyond what it spent.</param>
/// <param name="Share">The least the server gives at once, so that a machine asks seldom and spends offline.</param>
/// <param name="Usage">What the facts count of the scope, by machine, read in the same transaction.</param>
public sealed record BudgetAllowanceAsk(string BudgetId, long PeriodStart, string DeviceId, long Limit, long Spent, long Needed, long Share, BudgetUsageQuery Usage);

/// <summary>A run that waits for a reached budget.</summary>
/// <param name="Id">The hold.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="BudgetId">The budget.</param>
/// <param name="Number">The version of the budget the machine verified.</param>
/// <param name="PeriodStart">The period, its first quarter hour.</param>
/// <param name="MachineId">The machine of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="Nonce">The nonce of the machine.</param>
/// <param name="Title">What the run is.</param>
/// <param name="Status">Where it stands (<see cref="CodeAlta.Fleet.Protocol.Api.FleetBudgetHoldStates"/>).</param>
/// <param name="RequestedAt">When the machine asked.</param>
/// <param name="ContinuePayload">The signed Continue anyway, once given.</param>
/// <param name="ContinueSignature">Its signature.</param>
/// <param name="DecidedByDeviceId">The controller device that continued or stopped it.</param>
/// <param name="DecidedAt">When.</param>
public sealed record BudgetHoldRecord(string Id, string OrganizationId, string BudgetId, long Number, long PeriodStart, string MachineId, string RunId, string Nonce, string? Title,
    string Status, DateTimeOffset RequestedAt, string? ContinuePayload, string? ContinueSignature, string? DecidedByDeviceId, DateTimeOffset? DecidedAt);

/// <summary>A notification of a person, made once per problem (its <paramref name="DedupeKey"/>).</summary>
/// <param name="Id">The notification.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="PersonId">The person it is for.</param>
/// <param name="DedupeKey">What makes it once: a budget, its period and threshold; a machine and its problem.</param>
/// <param name="Kind">What it is about (<see cref="CodeAlta.Fleet.Protocol.Api.FleetNotificationKinds"/>).</param>
/// <param name="Severity"><c>info</c>, <c>warning</c> or <c>error</c>.</param>
/// <param name="BudgetId">The budget, for a budget.</param>
/// <param name="MachineId">The machine, for a problem of a machine.</param>
/// <param name="Problem">The kind of the problem of a machine.</param>
/// <param name="Subject">What the problem is about.</param>
/// <param name="Detail">A short fact; never content.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="ResolvedAt">When the problem ended.</param>
/// <param name="ReadAt">When the person dismissed it.</param>
public sealed record NotificationRecord(string Id, string OrganizationId, string PersonId, string DedupeKey, string Kind, string Severity, string? BudgetId, string? MachineId,
    string? Problem, string? Subject, string? Detail, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ReadAt);

/// <summary>A problem a machine reports, as the server keeps it while it lasts.</summary>
/// <param name="DeviceId">The machine.</param>
/// <param name="Kind">What it is (<see cref="CodeAlta.Fleet.Protocol.Api.FleetHealthKinds"/>).</param>
/// <param name="Subject">What it is about; empty for the machine.</param>
/// <param name="Detail">A short fact.</param>
/// <param name="FirstSeen">When the machine first reported it, since it last did not.</param>
/// <param name="LastSeen">When it last reported it.</param>
/// <param name="NotificationId">The notification it made once it lasted; null before.</param>
public sealed record MachineHealthRecord(string DeviceId, string Kind, string Subject, string? Detail, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string? NotificationId);

/// <summary>
/// The budgets of the organization, their allowances and holds, the notifications of its people and the health of its
/// machines (<c>doc/fleet/observability.md</c>), as the store keeps them.
/// </summary>
public interface IBudgetStore
{
    /// <summary>Lists budgets.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="ownerPersonId">The person whose budgets; null for every budget of the organization.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The budgets.</returns>
    ValueTask<IReadOnlyList<BudgetRecord>> ListBudgetsAsync(string organizationId, string? ownerPersonId, CancellationToken cancellationToken = default);

    /// <summary>Gets a budget.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="id">The budget.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The budget, or null.</returns>
    ValueTask<BudgetRecord?> GetBudgetAsync(string organizationId, string id, CancellationToken cancellationToken = default);

    /// <summary>Stores a budget, or a newer version of the one of its owner, audited in the same transaction.</summary>
    /// <param name="budget">The budget.</param>
    /// <param name="audit">The audit entry.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it was stored: false when the one held is as new, or of another person.</returns>
    ValueTask<bool> PutBudgetAsync(BudgetRecord budget, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Sums what the statistics facts count of a budget, by machine.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sum of each machine that counts something.</returns>
    ValueTask<IReadOnlyDictionary<string, long>> SumBudgetUsageAsync(BudgetUsageQuery query, CancellationToken cancellationToken = default);

    /// <summary>Lists what the server gave the machines of a budget in a period.</summary>
    /// <param name="budgetId">The budget.</param>
    /// <param name="periodStart">The period.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The allowance of each machine.</returns>
    ValueTask<IReadOnlyDictionary<string, long>> ListAllowancesAsync(string budgetId, long periodStart, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a machine more of an enforced budget, in one transaction with the allowances and the facts of the period, so that
    /// what all machines hold or used never passes the limit (<see cref="Services.BudgetAllowances.Grant"/>).
    /// </summary>
    /// <param name="ask">What the machine asks.</param>
    /// <param name="now">The time.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the machine holds now in the period, and whether the budget had not enough to give.</returns>
    ValueTask<(long Granted, bool Exhausted)> GrantAllowanceAsync(BudgetAllowanceAsk ask, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Records a run that waits for a budget; the hold of that budget, machine and run is kept when there is one.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="audit">The audit entry.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The hold held for that run.</returns>
    ValueTask<BudgetHoldRecord> RequestBudgetHoldAsync(BudgetHoldRecord hold, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Gets a hold.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="id">The hold.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The hold, or null.</returns>
    ValueTask<BudgetHoldRecord?> GetBudgetHoldAsync(string organizationId, string id, CancellationToken cancellationToken = default);

    /// <summary>Lists the holds of budgets, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="budgetIds">The budgets.</param>
    /// <param name="limit">The most holds.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The holds.</returns>
    ValueTask<IReadOnlyList<BudgetHoldRecord>> ListBudgetHoldsAsync(string organizationId, IReadOnlyCollection<string> budgetIds, int limit, CancellationToken cancellationToken = default);

    /// <summary>Decides a hold that waits: continued with a signed Continue anyway, or stopped; audited in the same transaction.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="id">The hold.</param>
    /// <param name="status">The decision (<see cref="CodeAlta.Fleet.Protocol.Api.FleetBudgetHoldStates"/>).</param>
    /// <param name="payload">The signed Continue anyway; null to stop.</param>
    /// <param name="signature">Its signature; null to stop.</param>
    /// <param name="deviceId">The controller device that decides.</param>
    /// <param name="at">When.</param>
    /// <param name="audit">The audit entry.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the hold waited and is decided now.</returns>
    ValueTask<bool> DecideBudgetHoldAsync(string organizationId, string id, string status, string? payload, string? signature, string deviceId, DateTimeOffset at, AuditEntry audit,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a notification, unless the person has one of the same key: each problem is told once.</summary>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it was added.</returns>
    ValueTask<bool> AddNotificationAsync(NotificationRecord notification, CancellationToken cancellationToken = default);

    /// <summary>Lists the notifications of a person, the newest first.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="personId">The person.</param>
    /// <param name="limit">The most notifications.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The notifications.</returns>
    ValueTask<IReadOnlyList<NotificationRecord>> ListNotificationsAsync(string organizationId, string personId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Marks a notification of a person read.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="personId">The person.</param>
    /// <param name="id">The notification.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the person has it.</returns>
    ValueTask<bool> ReadNotificationAsync(string organizationId, string personId, string id, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Marks the notifications of problems resolved.</summary>
    /// <param name="ids">The notifications.</param>
    /// <param name="at">When the problems ended.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once they are marked.</returns>
    ValueTask ResolveNotificationsAsync(IReadOnlyCollection<string> ids, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps the problems a machine reports now: one reported again keeps when it was first seen, a new one starts now, and one
    /// no longer reported ends (of the kinds <paramref name="kinds"/> covers).
    /// </summary>
    /// <param name="deviceId">The machine.</param>
    /// <param name="problems">The problems it reports now, by kind and subject, with their detail.</param>
    /// <param name="kinds">The kinds this report speaks of: a problem of another kind is left as it is.</param>
    /// <param name="now">The time of the report.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The problems that last now, and those that ended.</returns>
    ValueTask<(IReadOnlyList<MachineHealthRecord> Current, IReadOnlyList<MachineHealthRecord> Ended)> ReportHealthAsync(string deviceId,
        IReadOnlyList<(string Kind, string Subject, string? Detail)> problems, IReadOnlyCollection<string> kinds, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Records the notification a lasting problem made.</summary>
    /// <param name="deviceId">The machine.</param>
    /// <param name="kind">The kind of the problem.</param>
    /// <param name="subject">Its subject; empty for the machine.</param>
    /// <param name="notificationId">The notification.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once it is recorded.</returns>
    ValueTask SetHealthNotificationAsync(string deviceId, string kind, string subject, string notificationId, CancellationToken cancellationToken = default);
}
