using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The budgets of a server (doc/fleet/observability.md, budgets and alerts), for Settings > Fleet > Budgets and the Fleet
// button: each budget with its consumption, the runs that wait for one, and the notifications of the person (budgets at 80% and
// 100%, lasting problems of machines). This computer signs the budgets and each Continue anyway.
internal sealed partial class FleetServerService
{
    private const decimal MicroPerDollar = 1_000_000m;

    /// <summary>The budgets, the runs that wait and the notifications of this computer's person at a server.</summary>
    [NeoRpcMethod("budgets")]
    public async Task<FleetBudgetsOverview> BudgetsAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return FleetBudgetsOverview.Failed(refused, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return FleetBudgetsOverview.Failed("invalid_request", null);
        try
        {
            var read = await _servers!.ReadBudgetsAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            string DeviceName(string id) => read.Devices.TryGetValue(id, out var device) ? device.Name : id;
            var names = read.Budgets.ToDictionary(static pair => pair.Budget.Id, static pair => pair.Definition?.Name ?? pair.Budget.Id, StringComparer.Ordinal);
            string ScopeLabel(FleetBudgetDefinition definition) => definition.ScopeKind switch
            {
                FleetBudgetScopeKinds.Person => string.Empty,
                FleetBudgetScopeKinds.Machine => DeviceName(definition.ScopeId ?? string.Empty),
                _ when definition.ScopeId is { } id && read.ScopeNames.TryGetValue(id, out var name) => name,
                _ => definition.ScopeKind == FleetBudgetScopeKinds.Project ? "A project not on this computer" : "A space not linked here",
            };
            var budgets = read.Budgets.Where(static pair => pair.Definition is not null && !pair.Budget.Removed).Select(pair =>
            {
                var definition = pair.Definition!;
                var usage = pair.Budget.Usage;
                return new FleetBudgetView(pair.Budget.Id, definition.Name, definition.ScopeKind, definition.ScopeId, ScopeLabel(definition), definition.Measure,
                    Amount(definition.Measure, definition.Limit), Amount(definition.Measure, usage?.Used ?? 0), Amount(definition.Measure, usage?.Granted ?? 0), definition.Period,
                    definition.Mode, definition.Reached, usage?.State ?? FleetBudgetStates.Ok, usage?.PeriodEnd, (int)Math.Min(definition.Number, int.MaxValue));
            }).OrderBy(static budget => budget.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var holds = read.Holds.Select(hold => new FleetBudgetHoldView(hold.Id, hold.BudgetId, names.GetValueOrDefault(hold.BudgetId, hold.BudgetName), DeviceName(hold.MachineId),
                hold.RunId, hold.Title, hold.Status, hold.RequestedAt)).ToArray();
            var notifications = read.Notifications.Select(notification => new FleetNotificationView(notification.Id, notification.Kind, notification.Severity,
                notification.BudgetId is { } budget ? names.GetValueOrDefault(budget, notification.Subject ?? budget) : null,
                notification.MachineId is { } machine ? DeviceName(machine) : null, notification.Problem, notification.Subject, notification.Detail, notification.CreatedAt,
                notification.ResolvedAt is not null, notification.ReadAt is not null)).ToArray();
            var spaces = Spaces is null ? [] : await Spaces.LoadAsync(cancellationToken).ConfigureAwait(false);
            return new FleetBudgetsOverview("ok", null, budgets, holds, notifications,
                [.. read.Machines.Select(machine => new FleetSecretTarget(machine.DeviceId, DeviceName(machine.DeviceId)))],
                [.. spaces.Where(space => read.ScopeNames.Values.Contains(space.Name)).Select(static space => new FleetSecretTarget(space.Id, space.Name))],
                await ProjectTargetsAsync(cancellationToken).ConfigureAwait(false), TimeZoneInfo.Local.Id);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return FleetBudgetsOverview.Failed(failure.Status, failure.Message);
        }
    }

    /// <summary>Signs and stores a budget of the person: a new one, or the next version of the one with this id.</summary>
    [NeoRpcMethod("setBudget")]
    public Task<FleetSecretActionResult> SetBudgetAsync(FleetBudgetSetRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (!FleetBudgetDocuments.IsName(request!.Name?.Trim()) || !FleetBudgetScopeKinds.IsAvailable(request.ScopeKind) || !FleetBudgetMeasures.IsKnown(request.Measure)
                || !FleetBudgetPeriods.IsKnown(request.Period) || !FleetBudgetModes.IsKnown(request.Mode) || !FleetBudgetReachedActions.IsKnown(request.Reached)
                || request.Limit <= 0 || request.Limit > 1_000_000_000_000m || request.Id is { Length: 0 or > 64 })
                return new FleetSecretActionResult("invalid_request", null);
            var limit = request.Measure == FleetBudgetMeasures.Cost ? decimal.ToInt64(decimal.Round(request.Limit * MicroPerDollar)) : decimal.ToInt64(decimal.Round(request.Limit));
            if (limit < 1) return new FleetSecretActionResult("invalid_request", null);
            var scope = await _servers!.BudgetScopeAsync(request.LinkId, request.ScopeKind, request.ScopeId, cancellationToken).ConfigureAwait(false);
            var zone = FleetBudgetDocuments.FindZone(TimeZoneInfo.Local.Id) is not null ? TimeZoneInfo.Local.Id : "UTC";
            await _servers.BudgetsOf(request.LinkId).PutAsync(request.Id, new FleetBudgetSettings(request.Name!.Trim(), request.ScopeKind, scope, request.Measure, limit, request.Period,
                zone, request.Mode, request.Reached), cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Takes a budget away.</summary>
    [NeoRpcMethod("removeBudget")]
    public Task<FleetSecretActionResult> RemoveBudgetAsync(FleetSecretRefRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Id is not { Length: > 0 and <= 64 } id) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.BudgetsOf(request.LinkId).RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Continues anyway a run that waits for a reached budget: this computer signs it.</summary>
    [NeoRpcMethod("continueRun")]
    public Task<FleetSecretActionResult> ContinueRunAsync(FleetSecretRefRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Id is not { Length: > 0 and <= 64 } id) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.BudgetsOf(request.LinkId).ContinueAsync(id, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Stops a run that waits for a reached budget: it does not start.</summary>
    [NeoRpcMethod("stopRun")]
    public Task<FleetSecretActionResult> StopRunAsync(FleetSecretRefRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Id is not { Length: > 0 and <= 64 } id) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.BudgetsOf(request.LinkId).StopAsync(id, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Dismisses a notification on every device of the person.</summary>
    [NeoRpcMethod("readNotification")]
    public Task<FleetSecretActionResult> ReadNotificationAsync(FleetSecretRefRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Id is not { Length: > 0 and <= 64 } id) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.BudgetsOf(request.LinkId).ReadNotificationAsync(id, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    // Tokens as they are; a cost in dollars, from millionths.
    private static decimal Amount(string measure, long value) => measure == FleetBudgetMeasures.Cost ? value / MicroPerDollar : value;
}

/// <summary>A budget, as the page shows it.</summary>
/// <param name="Id">The budget.</param>
/// <param name="Name">Its name.</param>
/// <param name="ScopeKind"><c>person</c>, <c>machine</c>, <c>space</c> or <c>project</c>.</param>
/// <param name="ScopeId">The id of its scope at the server; null for the person.</param>
/// <param name="ScopeLabel">The name of its scope, as this computer knows it; empty for the person.</param>
/// <param name="Measure"><c>tokens</c> or <c>cost</c>.</param>
/// <param name="Limit">Its limit per period: tokens, or US dollars.</param>
/// <param name="Used">What its scope used in the period.</param>
/// <param name="Granted">For an enforced budget, what the machines hold or used of it.</param>
/// <param name="Period"><c>day</c>, <c>week</c> or <c>month</c>.</param>
/// <param name="Mode"><c>advisory</c> or <c>enforced</c>.</param>
/// <param name="Reached"><c>hold</c> or <c>stop</c>.</param>
/// <param name="State"><c>ok</c>, <c>warning</c> or <c>reached</c>.</param>
/// <param name="PeriodEnd">When its period starts again; null when unknown.</param>
/// <param name="Version">The version of the budget.</param>
internal sealed record FleetBudgetView(string Id, string Name, string ScopeKind, string? ScopeId, string ScopeLabel, string Measure, decimal Limit, decimal Used, decimal Granted,
    string Period, string Mode, string Reached, string State, DateTimeOffset? PeriodEnd, int Version);

/// <summary>A run that waits or waited for a budget.</summary>
/// <param name="Id">The hold.</param>
/// <param name="BudgetId">The budget.</param>
/// <param name="BudgetName">Its name.</param>
/// <param name="Machine">The name of the machine of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="Title">What the run is.</param>
/// <param name="Status"><c>waiting</c>, <c>continued</c> or <c>stopped</c>.</param>
/// <param name="RequestedAt">When the machine asked.</param>
internal sealed record FleetBudgetHoldView(string Id, string BudgetId, string BudgetName, string Machine, string RunId, string? Title, string Status, DateTimeOffset RequestedAt);

/// <summary>A notification of the person.</summary>
/// <param name="Id">The notification.</param>
/// <param name="Kind"><c>budget.warning</c>, <c>budget.reached</c> or <c>health</c>.</param>
/// <param name="Severity"><c>info</c>, <c>warning</c> or <c>error</c>.</param>
/// <param name="Budget">The name of the budget, for a budget.</param>
/// <param name="Machine">The name of the machine, for a problem of a machine.</param>
/// <param name="Problem">The kind of the problem of the machine.</param>
/// <param name="Subject">What the problem is about.</param>
/// <param name="Detail">A short fact, such as the share used.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="Resolved">Whether the problem ended.</param>
/// <param name="Read">Whether the person dismissed it.</param>
internal sealed record FleetNotificationView(string Id, string Kind, string Severity, string? Budget, string? Machine, string? Problem, string? Subject, string? Detail,
    DateTimeOffset CreatedAt, bool Resolved, bool Read);

/// <summary>The budgets, the runs that wait and the notifications of a server.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>unreachable</c>, <c>not_found</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Message">What the failure said.</param>
/// <param name="Budgets">The budgets.</param>
/// <param name="Holds">The runs that wait or waited, the newest first.</param>
/// <param name="Notifications">The notifications, the newest first.</param>
/// <param name="Machines">The machines of the person.</param>
/// <param name="Spaces">The spaces linked to the server.</param>
/// <param name="Projects">The projects of this computer.</param>
/// <param name="TimeZone">The time zone the periods of a new budget start in: this computer's.</param>
internal sealed record FleetBudgetsOverview(string Status, string? Message, FleetBudgetView[] Budgets, FleetBudgetHoldView[] Holds, FleetNotificationView[] Notifications,
    FleetSecretTarget[] Machines, FleetSecretTarget[] Spaces, FleetSecretTarget[] Projects, string TimeZone)
{
    /// <summary>Gets an overview of a failure.</summary>
    /// <param name="status">The status.</param>
    /// <param name="message">What it said.</param>
    /// <returns>The overview.</returns>
    public static FleetBudgetsOverview Failed(string status, string? message) => new(status, message, [], [], [], [], [], [], "UTC");
}

/// <summary>A budget to set.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Id">The budget to change; null for a new one.</param>
/// <param name="Name">Its name.</param>
/// <param name="ScopeKind"><c>person</c>, <c>machine</c>, <c>space</c> or <c>project</c>.</param>
/// <param name="ScopeId">The machine, or the space or the project on this computer; null for the person.</param>
/// <param name="Measure"><c>tokens</c> or <c>cost</c>.</param>
/// <param name="Limit">The limit per period: tokens, or US dollars.</param>
/// <param name="Period"><c>day</c>, <c>week</c> or <c>month</c>.</param>
/// <param name="Mode"><c>advisory</c> or <c>enforced</c>.</param>
/// <param name="Reached"><c>hold</c> or <c>stop</c>.</param>
internal sealed record FleetBudgetSetRequest(string ExpectedEpoch, string LinkId, string? Id, string? Name, string ScopeKind, string? ScopeId, string Measure, decimal Limit,
    string Period, string Mode, string Reached);
