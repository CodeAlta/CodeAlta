using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>The paths and events of the budgets, their holds and the notifications of the REST API.</summary>
public static class FleetBudgetMethods
{
    /// <summary>The event a server sends a device when its budgets, allowances or holds changed: it reads them again.</summary>
    public const string Changed = "budgets.changed";

    /// <summary>The event a server sends a controller device when its person has a new notification: it reads them again.</summary>
    public const string NotificationsChanged = "notifications.changed";
}

/// <summary>Where a budget stands in its period.</summary>
public static class FleetBudgetStates
{
    /// <summary>Less than 80% of its limit is used.</summary>
    public const string Ok = "ok";

    /// <summary>80% of its limit or more is used.</summary>
    public const string Warning = "warning";

    /// <summary>Its limit is used: new runs in its scope wait or stop.</summary>
    public const string Reached = "reached";
}

/// <summary>The consumption of a budget in its current period, from the statistics facts the machines published.</summary>
/// <param name="PeriodStart">When the period started.</param>
/// <param name="PeriodEnd">When the next one starts.</param>
/// <param name="Used">What the scope used in the period: tokens, or millionths of a US dollar.</param>
/// <param name="Limit">The limit of the budget.</param>
/// <param name="Granted">For an enforced budget, what the machines hold or used of it (each the larger); 0 for an advisory one.</param>
/// <param name="State">Where it stands (<see cref="FleetBudgetStates"/>).</param>
public sealed record FleetBudgetUsageDto(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, long Used, long Limit, long Granted, string State);

/// <summary>A budget as the server holds it.</summary>
/// <param name="Id">The budget.</param>
/// <param name="Signed">The budget as its owner's controller device signed it.</param>
/// <param name="Number">Its version.</param>
/// <param name="Removed">Whether it is taken away.</param>
/// <param name="UpdatedAt">When the server received this version.</param>
/// <param name="Usage">Its consumption in the current period; null for a budget taken away.</param>
public sealed record FleetBudgetDto(string Id, FleetSignedBudget Signed, long Number, bool Removed, DateTimeOffset UpdatedAt, FleetBudgetUsageDto? Usage);

/// <summary>Budgets.</summary>
/// <param name="Budgets">The budgets.</param>
public sealed record FleetBudgetListDto(IReadOnlyList<FleetBudgetDto> Budgets);

/// <summary>The body of <c>PUT /api/v1/budgets/{id}</c>: a budget, or a newer version of it, its owner's controller device signed.</summary>
/// <param name="Budget">The signed budget.</param>
public sealed record PutFleetBudgetRequest(FleetSignedBudget Budget);

/// <summary>A budget that limits the calling machine, with what the machine holds of it.</summary>
/// <param name="Id">The budget.</param>
/// <param name="Signed">The budget as signed, which the machine verifies.</param>
/// <param name="Usage">Its consumption in the current period, the whole scope.</param>
/// <param name="Allowance">What the server gave this machine in the period (enforced budgets).</param>
/// <param name="MachineUsed">What the facts of this machine count in the period.</param>
public sealed record FleetMachineBudgetDto(string Id, FleetSignedBudget Signed, FleetBudgetUsageDto Usage, long Allowance, long MachineUsed);

/// <summary>The budgets of the calling machine.</summary>
/// <param name="Budgets">The budgets.</param>
public sealed record FleetMachineBudgetListDto(IReadOnlyList<FleetMachineBudgetDto> Budgets);

/// <summary>The body of <c>POST /api/v1/budgets/{id}/allowance</c>: a machine asks more of an enforced budget.</summary>
/// <param name="Number">The version of the budget the machine verified.</param>
/// <param name="Spent">What the machine spent in the period, as it counts it.</param>
/// <param name="Needed">What it needs to hold beyond what it spent, to start a run.</param>
public sealed record FleetAllowanceRequest(long Number, long Spent, long Needed);

/// <summary>The allowance of a machine in an enforced budget, for the current period.</summary>
/// <param name="BudgetId">The budget.</param>
/// <param name="PeriodStart">The start of the period, a quarter hour since the Unix epoch.</param>
/// <param name="Granted">Everything the server gave this machine in the period.</param>
/// <param name="Exhausted">Whether the budget had nothing more to give: it is reached.</param>
public sealed record FleetAllowanceDto(string BudgetId, long PeriodStart, long Granted, bool Exhausted);

/// <summary>The states of a hold: a run that waits for a reached budget.</summary>
public static class FleetBudgetHoldStates
{
    /// <summary>The run waits for a person.</summary>
    public const string Waiting = "waiting";

    /// <summary>A person continued it anyway: the signed Continue anyway is held.</summary>
    public const string Continued = "continued";

    /// <summary>A person stopped it: it does not start.</summary>
    public const string Stopped = "stopped";
}

/// <summary>The body of <c>POST /api/v1/budgets/{id}/holds</c>: a machine says that a run waits for a reached budget.</summary>
/// <param name="RunId">The run, which the Continue anyway is bound to.</param>
/// <param name="Nonce">A random value of the machine, which the Continue anyway is bound to too.</param>
/// <param name="Number">The version of the budget the machine verified.</param>
/// <param name="PeriodStart">The period the run waits in, a quarter hour since the Unix epoch.</param>
/// <param name="Title">What the run is, as the machine names it for people.</param>
public sealed record RequestFleetBudgetHoldRequest(string RunId, string Nonce, long Number, long PeriodStart, string? Title);

/// <summary>A run that waits for a reached budget, as the server holds it.</summary>
/// <param name="Id">The hold.</param>
/// <param name="BudgetId">The budget.</param>
/// <param name="BudgetName">Its name.</param>
/// <param name="Number">The version of the budget the machine verified.</param>
/// <param name="PeriodStart">The period, a quarter hour since the Unix epoch.</param>
/// <param name="MachineId">The machine of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="Nonce">The nonce of the machine.</param>
/// <param name="Title">What the run is.</param>
/// <param name="Status">Where it stands (<see cref="FleetBudgetHoldStates"/>).</param>
/// <param name="RequestedAt">When the machine asked.</param>
/// <param name="Continue">The signed Continue anyway, once given.</param>
/// <param name="DecidedAt">When a person continued or stopped it.</param>
public sealed record FleetBudgetHoldDto(string Id, string BudgetId, string BudgetName, long Number, long PeriodStart, string MachineId, string RunId, string Nonce,
    string? Title, string Status, DateTimeOffset RequestedAt, FleetSignedBudgetContinue? Continue, DateTimeOffset? DecidedAt);

/// <summary>Holds.</summary>
/// <param name="Holds">The holds.</param>
public sealed record FleetBudgetHoldListDto(IReadOnlyList<FleetBudgetHoldDto> Holds);

/// <summary>The body of <c>POST /api/v1/budget-holds/{id}/continue</c>: the Continue anyway a controller device of the owner signed.</summary>
/// <param name="Continue">The signed Continue anyway.</param>
public sealed record ContinueFleetBudgetHoldRequest(FleetSignedBudgetContinue Continue);

/// <summary>The kinds of the notifications of a person.</summary>
public static class FleetNotificationKinds
{
    /// <summary>A budget passed 80% of its limit in its period.</summary>
    public const string BudgetWarning = "budget.warning";

    /// <summary>A budget reached its limit in its period.</summary>
    public const string BudgetReached = "budget.reached";

    /// <summary>A machine has a problem that lasts (<see cref="FleetHealthKinds"/>).</summary>
    public const string Health = "health";
}

/// <summary>
/// A notification of a person: a budget at 80% or 100%, a lasting problem of one of their machines. Made once per problem by
/// the server; ids, numbers and the names of providers, never content.
/// </summary>
/// <param name="Id">The notification.</param>
/// <param name="Kind">What it is about (<see cref="FleetNotificationKinds"/>).</param>
/// <param name="Severity"><c>info</c>, <c>warning</c> or <c>error</c>.</param>
/// <param name="BudgetId">The budget, for a budget.</param>
/// <param name="MachineId">The machine, for a problem of a machine.</param>
/// <param name="Problem">The kind of the problem of the machine (<see cref="FleetHealthKinds"/>).</param>
/// <param name="Subject">What the problem is about, such as the provider whose sign-in expires.</param>
/// <param name="Detail">A short fact, such as a number; never content.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="ResolvedAt">When the problem ended; null while it lasts.</param>
/// <param name="ReadAt">When the person dismissed it; null while it is new.</param>
public sealed record FleetNotificationDto(string Id, string Kind, string Severity, string? BudgetId, string? MachineId, string? Problem, string? Subject, string? Detail,
    DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ReadAt);

/// <summary>Notifications, the newest first.</summary>
/// <param name="Notifications">The notifications.</param>
public sealed record FleetNotificationListDto(IReadOnlyList<FleetNotificationDto> Notifications);

/// <summary>The problems a machine reports in its heartbeat, and those the server sees for itself.</summary>
public static class FleetHealthKinds
{
    /// <summary>Offline longer than usual: seen by the server, never reported.</summary>
    public const string Offline = "offline";

    /// <summary>The sign-in of a provider expires soon, or expired; its subject is the provider.</summary>
    public const string SignIn = "sign_in";

    /// <summary>A provider's sign-in failed; its subject is the provider.</summary>
    public const string SignInFailed = "sign_in_failed";

    /// <summary>Little disk space is left; its detail is the space left, in megabytes.</summary>
    public const string DiskLow = "disk_low";

    /// <summary>The machine runs a version older than the server's: seen by the server from its inventory, never reported; its detail is its version.</summary>
    public const string VersionBehind = "version_behind";

    /// <summary>An automation fails its runs; its subject is the automation.</summary>
    public const string AutomationFailing = "automation_failing";

    /// <summary>A policy of the machine refused runs; its subject is what refused them.</summary>
    public const string PolicyRefusing = "policy_refusing";

    /// <summary>The kinds a machine reports in its heartbeat; the server sees <see cref="Offline"/> and <see cref="VersionBehind"/> for itself.</summary>
    public static IReadOnlyList<string> Reported { get; } = [SignIn, SignInFailed, DiskLow, AutomationFailing, PolicyRefusing];

    /// <summary>Gets whether a machine may report a kind (<see cref="Reported"/>).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether it may.</returns>
    public static bool IsReported(string? kind) => kind is SignIn or SignInFailed or DiskLow or AutomationFailing or PolicyRefusing;
}

/// <summary>A problem of a machine, as it reports it in its heartbeat while it lasts.</summary>
/// <param name="Kind">What it is (<see cref="FleetHealthKinds"/>).</param>
/// <param name="Subject">What it is about, such as a provider or an automation; null for the machine.</param>
/// <param name="Detail">A short fact, such as the megabytes left; never content.</param>
public sealed record FleetHealthProblem(string Kind, string? Subject = null, string? Detail = null);

/// <summary>The source-generated contracts of the budgets, holds, notifications and health of the REST API.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetBudgetDto))]
[JsonSerializable(typeof(FleetBudgetListDto))]
[JsonSerializable(typeof(PutFleetBudgetRequest))]
[JsonSerializable(typeof(FleetMachineBudgetListDto))]
[JsonSerializable(typeof(FleetAllowanceRequest))]
[JsonSerializable(typeof(FleetAllowanceDto))]
[JsonSerializable(typeof(RequestFleetBudgetHoldRequest))]
[JsonSerializable(typeof(FleetBudgetHoldDto))]
[JsonSerializable(typeof(FleetBudgetHoldListDto))]
[JsonSerializable(typeof(ContinueFleetBudgetHoldRequest))]
[JsonSerializable(typeof(FleetNotificationDto))]
[JsonSerializable(typeof(FleetNotificationListDto))]
[JsonSerializable(typeof(FleetHealthProblem))]
[JsonSerializable(typeof(ApiProblemDto))]
public sealed partial class FleetBudgetsApiJsonContext : JsonSerializerContext;
