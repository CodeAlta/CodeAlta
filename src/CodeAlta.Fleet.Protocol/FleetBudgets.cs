using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>What a budget limits (<c>doc/fleet/observability.md</c>, budgets and alerts), as the statistics facts count it.</summary>
public static class FleetBudgetMeasures
{
    /// <summary>Tokens: the input and output tokens of the model requests.</summary>
    public const string Tokens = "tokens";

    /// <summary>The estimated cost, in millionths of a US dollar, from the prices of the model catalog.</summary>
    public const string Cost = "cost";

    /// <summary>Gets whether a text names a measure.</summary>
    /// <param name="measure">The text.</param>
    /// <returns>Whether it does.</returns>
    public static bool IsKnown([NotNullWhen(true)] string? measure) => measure is Tokens or Cost;
}

/// <summary>The periods of a budget: its limit starts again at the start of each, in the time zone of the budget.</summary>
public static class FleetBudgetPeriods
{
    /// <summary>A day, from midnight.</summary>
    public const string Day = "day";

    /// <summary>A week, from Monday at midnight.</summary>
    public const string Week = "week";

    /// <summary>A month, from its first day at midnight.</summary>
    public const string Month = "month";

    /// <summary>Gets whether a text names a period.</summary>
    /// <param name="period">The text.</param>
    /// <returns>Whether it does.</returns>
    public static bool IsKnown([NotNullWhen(true)] string? period) => period is Day or Week or Month;
}

/// <summary>How a budget holds: it warns from the facts, or it gives each machine a share it spends before a run starts.</summary>
public static class FleetBudgetModes
{
    /// <summary>Warns from the facts as they arrive: machines working at once or offline can pass it before anyone is told.</summary>
    public const string Advisory = "advisory";

    /// <summary>Split into allowances the server gives each machine: a run starts only when its machine holds enough.</summary>
    public const string Enforced = "enforced";

    /// <summary>Gets whether a text names a mode.</summary>
    /// <param name="mode">The text.</param>
    /// <returns>Whether it does.</returns>
    public static bool IsKnown([NotNullWhen(true)] string? mode) => mode is Advisory or Enforced;
}

/// <summary>What a new run in the scope of a budget does once the budget is reached.</summary>
public static class FleetBudgetReachedActions
{
    /// <summary>The run waits until a person continues it anyway (or stops it).</summary>
    public const string Hold = "hold";

    /// <summary>The run does not start.</summary>
    public const string Stop = "stop";

    /// <summary>Gets whether a text names an action.</summary>
    /// <param name="action">The text.</param>
    /// <returns>Whether it does.</returns>
    public static bool IsKnown([NotNullWhen(true)] string? action) => action is Hold or Stop;
}

/// <summary>Whose spending a budget limits (its <c>chargeScope</c>, <c>doc/fleet/access-control.md</c>).</summary>
public static class FleetBudgetScopeKinds
{
    /// <summary>Every machine of the person who owns the budget; names no id.</summary>
    public const string Person = "person";

    /// <summary>One machine, by its device id.</summary>
    public const string Machine = "machine";

    /// <summary>One project, by its blind index (<c>FleetProjectKeys</c>), which the server cannot name.</summary>
    public const string Project = "project";

    /// <summary>One space, by the opaque id the server knows it by.</summary>
    public const string Space = "space";

    /// <summary>A team (with the roles of M4).</summary>
    public const string Team = "team";

    /// <summary>The organization (with the roles of M4).</summary>
    public const string Organization = "organization";

    /// <summary>An automation (with the charge scopes of M4).</summary>
    public const string Automation = "automation";

    /// <summary>Gets whether a text names a scope, available now or with the roles to come.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it does.</returns>
    public static bool IsKnown([NotNullWhen(true)] string? kind) => kind is Person or Machine or Project or Space or Team or Organization or Automation;

    /// <summary>Gets whether a scope can be limited now: a person's own spending, a machine, a project, a space.</summary>
    /// <param name="kind">The scope.</param>
    /// <returns>Whether it can.</returns>
    public static bool IsAvailable(string? kind) => kind is Person or Machine or Project or Space;

    /// <summary>Gets whether a scope names an id.</summary>
    /// <param name="kind">The scope.</param>
    /// <returns>Whether it does.</returns>
    public static bool HasId(string? kind) => kind is not Person and not Organization;
}

/// <summary>
/// A budget of a person (<c>doc/fleet/observability.md</c>, budgets and alerts): a limit of tokens or of estimated cost per
/// period for a scope, signed by a controller device of that person as policies are, so that a server can neither loosen it
/// nor make one up. The allowances of an enforced budget are the server's bookkeeping, within the limit the machines verified.
/// </summary>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization of the server.</param>
/// <param name="PersonId">The person who owns the budget.</param>
/// <param name="BudgetId">The id of the budget.</param>
/// <param name="Name">Its name, for people.</param>
/// <param name="ScopeKind">Whose spending it limits (<see cref="FleetBudgetScopeKinds"/>).</param>
/// <param name="ScopeId">The machine, project key or space; null for the person.</param>
/// <param name="Measure">What it limits (<see cref="FleetBudgetMeasures"/>).</param>
/// <param name="Limit">The limit per period: tokens, or millionths of a US dollar.</param>
/// <param name="Period">The period (<see cref="FleetBudgetPeriods"/>).</param>
/// <param name="TimeZone">The time zone its periods start in, an IANA or Windows id; <c>UTC</c> by default.</param>
/// <param name="Mode">Advisory or enforced (<see cref="FleetBudgetModes"/>).</param>
/// <param name="Reached">What a new run does once it is reached (<see cref="FleetBudgetReachedActions"/>).</param>
/// <param name="Number">The version of the budget: a machine never goes back to an older one.</param>
/// <param name="Removed">Whether the budget is taken away.</param>
/// <param name="SignerDeviceId">The controller device that signs it.</param>
/// <param name="Time">When it was signed.</param>
public sealed record FleetBudgetDefinition(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("person")] string PersonId,
    [property: JsonPropertyName("id")] string BudgetId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("scope")] string ScopeKind,
    [property: JsonPropertyName("scopeId")] string? ScopeId,
    [property: JsonPropertyName("measure")] string Measure,
    [property: JsonPropertyName("limit")] long Limit,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("zone")] string TimeZone,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("reached")] string Reached,
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("removed")] bool Removed,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>A budget as it travels: the exact JSON text the signer signed, and the signature.</summary>
/// <param name="Payload">The JSON of a <see cref="FleetBudgetDefinition"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetBudgetDocuments.GetSignedBytes"/>, in base64url.</param>
public sealed record FleetSignedBudget(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>
/// <b>Continue anyway</b> for one run that waits for a reached budget, signed by a controller device of the owner of the
/// budget: bound to the budget and its version, the period, the machine, the run and the nonce the machine chose, and valid
/// for a while. The machine takes it once. Continuing is a looser rule than holding, so it is signed; stopping is stricter
/// and needs no signature.
/// </summary>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="HoldId">The hold the machine asked, at the server.</param>
/// <param name="BudgetId">The budget.</param>
/// <param name="BudgetNumber">The version of the budget.</param>
/// <param name="PeriodStart">The start of the period, a quarter hour since the Unix epoch.</param>
/// <param name="MachineId">The machine of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="Nonce">The nonce of the machine.</param>
/// <param name="PersonId">The person who continues it.</param>
/// <param name="SignerDeviceId">The controller device of that person that signs.</param>
/// <param name="Time">When it was signed.</param>
/// <param name="Expires">Until when the run may start with it.</param>
public sealed record FleetBudgetContinue(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("hold")] string HoldId,
    [property: JsonPropertyName("budget")] string BudgetId,
    [property: JsonPropertyName("number")] long BudgetNumber,
    [property: JsonPropertyName("period")] long PeriodStart,
    [property: JsonPropertyName("machine")] string MachineId,
    [property: JsonPropertyName("run")] string RunId,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("person")] string PersonId,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("expires")] DateTimeOffset Expires)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>A <see cref="FleetBudgetContinue"/> as it travels: the exact JSON text the signer signed, and the signature.</summary>
/// <param name="Payload">The JSON text, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetBudgetDocuments.GetContinueSignedBytes"/>, in base64url.</param>
public sealed record FleetSignedBudgetContinue(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>What a run is, as a budget of its machine sees it: the machine, its owner, the space and the project of the session.</summary>
/// <param name="MachineId">The device of the machine that runs it.</param>
/// <param name="MachineOwnerId">The person who owns the machine.</param>
/// <param name="SpaceKey">The opaque id of the space of the session at the server; null when unknown.</param>
/// <param name="ProjectKey">The blind index of the project of the session; null for none or unknown.</param>
public sealed record FleetBudgetRunScope(string MachineId, string? MachineOwnerId, string? SpaceKey, string? ProjectKey);

/// <summary>Writes, reads and verifies the signed budgets and their Continue anyway; computes their periods.</summary>
public static class FleetBudgetDocuments
{
    /// <summary>The longest a Continue anyway stays valid.</summary>
    public static readonly TimeSpan MaxContinueLifetime = TimeSpan.FromDays(1);

    /// <summary>How far in the future of the machine a Continue anyway may be signed.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(10);

    /// <summary>The share of its limit that a machine must hold before a run of an enforced budget starts, in hundredths.</summary>
    public const int RunReservePercent = 1;

    /// <summary>The most budgets of a person.</summary>
    public const int MaxBudgets = 64;

    private const string BudgetDomain = "codealta-budget/1\n";
    private const string ContinueDomain = "codealta-budget-continue/1\n";
    private const int MaxPayloadLength = 8 * 1024;
    private const int MaxIdLength = 128;

    /// <summary>Gets whether a text can be the name of a budget: 1 to 64 characters, without control characters.</summary>
    /// <param name="name">The text.</param>
    /// <returns>Whether it can.</returns>
    public static bool IsName([NotNullWhen(true)] string? name) => name is { Length: > 0 and <= 64 } && !name.Any(char.IsControl) && name.Trim().Length == name.Length;

    /// <summary>Writes the JSON text of a budget, the text a signer signs.</summary>
    /// <param name="definition">The budget.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    public static string Serialize(FleetBudgetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, FleetBudgetJsonContext.Default.FleetBudgetDefinition);
    }

    /// <summary>Writes the JSON text of a Continue anyway, the text a signer signs.</summary>
    /// <param name="decision">The Continue anyway.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is null.</exception>
    public static string Serialize(FleetBudgetContinue decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return JsonSerializer.Serialize(decision, FleetBudgetJsonContext.Default.FleetBudgetContinue);
    }

    /// <summary>Gets the bytes a signer signs for a budget.</summary>
    /// <param name="payload">The JSON text of the budget.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(BudgetDomain + payload);
    }

    /// <summary>Gets the bytes a signer signs for a Continue anyway: under a domain of its own, so that a budget never reads as one.</summary>
    /// <param name="payload">The JSON text of the Continue anyway.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetContinueSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(ContinueDomain + payload);
    }

    /// <summary>Reads the JSON text of a budget and checks what it says, apart from its signature.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="definition">The budget read, or null.</param>
    /// <param name="reason">Why it does not hold, or null.</param>
    /// <returns>Whether it reads and holds.</returns>
    public static bool TryRead(string? payload, [NotNullWhen(true)] out FleetBudgetDefinition? definition, [NotNullWhen(false)] out string? reason)
    {
        definition = null;
        reason = "the budget does not read";
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return false;
        FleetBudgetDefinition? read;
        try { read = JsonSerializer.Deserialize(payload, FleetBudgetJsonContext.Default.FleetBudgetDefinition); }
        catch (JsonException) { return false; }
        if (read is null) return false;
        if (read.Version != FleetBudgetDefinition.CurrentVersion)
            return Refuse($"version {read.Version.ToString(CultureInfo.InvariantCulture)} of the budget is not known", out reason);
        if (!IsId(read.OrganizationId) || !IsId(read.PersonId) || !IsId(read.BudgetId) || !IsId(read.SignerDeviceId) || !IsName(read.Name) || read.Number < 1)
            return Refuse("a budget names its organization, person, id, name and signer, and a version from 1", out reason);
        if (!FleetBudgetScopeKinds.IsKnown(read.ScopeKind) || (FleetBudgetScopeKinds.HasId(read.ScopeKind) ? !IsId(read.ScopeId) : read.ScopeId is not null))
            return Refuse("a budget limits a person, a machine, a project, a space, a team, the organization or an automation, by its id but the person's", out reason);
        if (!FleetBudgetMeasures.IsKnown(read.Measure) || read.Limit < 1 || !FleetBudgetPeriods.IsKnown(read.Period) || !FleetBudgetModes.IsKnown(read.Mode)
            || !FleetBudgetReachedActions.IsKnown(read.Reached))
            return Refuse("a budget limits tokens or a cost, from 1, per day, week or month, advisory or enforced, and holds or stops the runs once reached", out reason);
        if (FindZone(read.TimeZone) is null) return Refuse($"the time zone '{read.TimeZone}' is not known", out reason);
        (definition, reason) = (read, null);
        return true;
    }

    /// <summary>Reads the JSON text of a Continue anyway and checks what it says, apart from its signature.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="decision">The Continue anyway read, or null.</param>
    /// <param name="reason">Why it does not hold, or null.</param>
    /// <returns>Whether it reads and holds.</returns>
    public static bool TryReadContinue(string? payload, [NotNullWhen(true)] out FleetBudgetContinue? decision, [NotNullWhen(false)] out string? reason)
    {
        decision = null;
        reason = "the Continue anyway does not read";
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return false;
        FleetBudgetContinue? read;
        try { read = JsonSerializer.Deserialize(payload, FleetBudgetJsonContext.Default.FleetBudgetContinue); }
        catch (JsonException) { return false; }
        if (read is null) return false;
        if (read.Version != FleetBudgetContinue.CurrentVersion)
            return Refuse($"version {read.Version.ToString(CultureInfo.InvariantCulture)} of the Continue anyway is not known", out reason);
        if (!IsId(read.OrganizationId) || !IsId(read.HoldId) || !IsId(read.BudgetId) || !IsId(read.MachineId) || !IsId(read.RunId) || !IsId(read.Nonce)
            || !IsId(read.PersonId) || !IsId(read.SignerDeviceId) || read.BudgetNumber < 1)
            return Refuse("a Continue anyway names its organization, hold, budget, machine, run, nonce, person and signer", out reason);
        if (read.Expires <= read.Time || read.Expires - read.Time > MaxContinueLifetime)
            return Refuse("a Continue anyway is valid for at most a day after it is signed", out reason);
        (decision, reason) = (read, null);
        return true;
    }

    /// <summary>
    /// Verifies a budget for a machine: it reads and holds, is of the organization of the machine's server and of the person
    /// who owns the machine, and is signed by a controller device of that person in the device list the machine verified.
    /// </summary>
    /// <param name="signed">The signed budget.</param>
    /// <param name="devices">The device list as the machine verified it.</param>
    /// <param name="machineId">The device of the machine that verifies.</param>
    /// <param name="definition">The budget, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public static bool TryVerify(FleetSignedBudget? signed, FleetDeviceListState devices, string? machineId,
        [NotNullWhen(true)] out FleetBudgetDefinition? definition, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(devices);
        definition = null;
        if (signed is null || string.IsNullOrEmpty(signed.Signature)) return Refuse("the budget does not read", out reason);
        if (!TryRead(signed.Payload, out var read, out reason)) return false;
        if (devices.OrganizationId is { } organization && organization != read.OrganizationId) return Refuse("the budget is of another organization", out reason);
        if (string.IsNullOrEmpty(machineId) || !devices.Members.TryGetValue(machineId, out var self) || self.OwnerId is null || self.OwnerId != read.PersonId)
            return Refuse("the budget is of another person than the owner of this machine", out reason);
        if (!IsSigner(devices, read.SignerDeviceId, read.PersonId, out var signer))
            return Refuse("the budget is not signed by a controller device of its person", out reason);
        if (!signer.Key.Verify(GetSignedBytes(signed.Payload), signed.Signature))
            return Refuse("the signature of the budget does not verify with the key the device list holds for its signer", out reason);
        definition = read;
        return true;
    }

    /// <summary>
    /// Verifies a Continue anyway for a machine: it reads and holds, names the budget and the version the machine verified, the
    /// period, this machine, this run and this nonce; it is valid at <paramref name="now"/>; and it is signed by a controller
    /// device of the owner of the budget.
    /// </summary>
    /// <param name="signed">The signed Continue anyway.</param>
    /// <param name="budget">The budget the machine verified (<see cref="TryVerify"/>).</param>
    /// <param name="devices">The device list as the machine verified it.</param>
    /// <param name="machineId">The device of the machine.</param>
    /// <param name="runId">The run that waits.</param>
    /// <param name="nonce">The nonce the machine chose for it.</param>
    /// <param name="periodStart">The period the run waits in.</param>
    /// <param name="now">The time of the machine.</param>
    /// <param name="decision">The Continue anyway, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="budget"/> or <paramref name="devices"/> is null.</exception>
    public static bool TryVerifyContinue(FleetSignedBudgetContinue? signed, FleetBudgetDefinition budget, FleetDeviceListState devices, string machineId, string runId,
        string nonce, long periodStart, DateTimeOffset now, [NotNullWhen(true)] out FleetBudgetContinue? decision, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(devices);
        decision = null;
        if (signed is null || string.IsNullOrEmpty(signed.Signature)) return Refuse("the Continue anyway does not read", out reason);
        if (!TryReadContinue(signed.Payload, out var read, out reason)) return false;
        if (read.OrganizationId != budget.OrganizationId || read.BudgetId != budget.BudgetId || read.BudgetNumber != budget.Number || read.PeriodStart != periodStart)
            return Refuse("the Continue anyway is for another budget, another version of it or another period", out reason);
        if (read.MachineId != machineId || read.RunId != runId || read.Nonce != nonce)
            return Refuse("the Continue anyway is for another machine or another run", out reason);
        if (read.Time - now > MaxSkew) return Refuse("the Continue anyway is signed in the future", out reason);
        if (read.Expires <= now) return Refuse("the Continue anyway expired", out reason);
        if (read.PersonId != budget.PersonId) return Refuse("the Continue anyway is not given by the owner of the budget", out reason);
        if (!IsSigner(devices, read.SignerDeviceId, read.PersonId, out var signer))
            return Refuse("the Continue anyway is not signed by a controller device of the owner of the budget", out reason);
        if (!signer.Key.Verify(GetContinueSignedBytes(signed.Payload), signed.Signature))
            return Refuse("the signature of the Continue anyway does not verify with the key the device list holds for its signer", out reason);
        decision = read;
        return true;
    }

    /// <summary>Gets whether a budget limits a run: its scope is the owner of the machine, the machine, the space or the project of the session.</summary>
    /// <param name="budget">The budget.</param>
    /// <param name="run">The run.</param>
    /// <returns>Whether it does.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool Covers(FleetBudgetDefinition budget, FleetBudgetRunScope run)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(run);
        if (budget.Removed) return false;
        return budget.ScopeKind switch
        {
            FleetBudgetScopeKinds.Person => run.MachineOwnerId is not null && run.MachineOwnerId == budget.PersonId,
            FleetBudgetScopeKinds.Machine => budget.ScopeId == run.MachineId,
            FleetBudgetScopeKinds.Space => run.SpaceKey is not null && budget.ScopeId == run.SpaceKey,
            FleetBudgetScopeKinds.Project => run.ProjectKey is not null && budget.ScopeId == run.ProjectKey,
            _ => false,
        };
    }

    /// <summary>Gets the period of a budget that holds a time, in the time zone of the budget.</summary>
    /// <param name="budget">The budget.</param>
    /// <param name="now">The time.</param>
    /// <returns>The start and the end of the period, as times and as quarter hours (<see cref="FleetStatisticsFamilies.QuarterOf"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="budget"/> is null.</exception>
    /// <exception cref="ArgumentException">The period or the time zone of the budget is not known.</exception>
    public static FleetBudgetPeriod PeriodOf(FleetBudgetDefinition budget, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return PeriodOf(budget.Period, budget.TimeZone, now);
    }

    /// <summary>Gets the period that holds a time, in a time zone.</summary>
    /// <param name="period">The period (<see cref="FleetBudgetPeriods"/>).</param>
    /// <param name="timeZone">The time zone, an IANA or Windows id.</param>
    /// <param name="now">The time.</param>
    /// <returns>The start and the end of the period.</returns>
    /// <exception cref="ArgumentException">The period or the time zone is not known.</exception>
    public static FleetBudgetPeriod PeriodOf(string period, string? timeZone, DateTimeOffset now)
    {
        if (!FleetBudgetPeriods.IsKnown(period)) throw new ArgumentException($"'{period}' is not a period of a budget.", nameof(period));
        var zone = FindZone(timeZone) ?? throw new ArgumentException($"'{timeZone}' is not a known time zone.", nameof(timeZone));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var (first, next) = period switch
        {
            FleetBudgetPeriods.Day => (today, today.AddDays(1)),
            FleetBudgetPeriods.Week => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7))),
            _ => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1)),
        };
        var start = Midnight(first, zone);
        var end = Midnight(next, zone);
        return new FleetBudgetPeriod(start, end, FleetStatisticsFamilies.QuarterOf(start), FleetStatisticsFamilies.QuarterOf(end));
    }

    /// <summary>Gets what a machine must hold of an enforced budget before a run starts: a hundredth of its limit, at least 1.</summary>
    /// <param name="budget">The budget.</param>
    /// <returns>The amount.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="budget"/> is null.</exception>
    public static long RunReserve(FleetBudgetDefinition budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return Math.Max(1, budget.Limit / 100 * RunReservePercent);
    }

    /// <summary>Gets the measure of a statistics row that a budget counts: tokens in and out of a usage row, the dollars of a cost row.</summary>
    /// <param name="measure">What the budget limits.</param>
    /// <param name="row">The row.</param>
    /// <returns>The amount the row counts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    public static long Count(string measure, FleetStatisticsRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (measure == FleetBudgetMeasures.Tokens && row.Family == FleetStatisticsFamilies.Usage)
            return row.Measures.GetValueOrDefault("input_tokens") + row.Measures.GetValueOrDefault("output_tokens");
        if (measure == FleetBudgetMeasures.Cost && row.Family == FleetStatisticsFamilies.Cost && (row.Keys is not { Count: > 0 } keys || keys[0] is "" or "usd"))
            return row.Measures.GetValueOrDefault("total_micro");
        return 0;
    }

    /// <summary>Finds a time zone by an IANA or Windows id; <c>UTC</c> when none is named.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The time zone, or null when it is not known.</returns>
    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id == "UTC") return TimeZoneInfo.Utc;
        if (id.Length > 64) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    // The start of a local day: midnight, or the first quarter hour that exists when a change of time skips midnight.
    private static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(15);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    // A controller device of the person, still in the list.
    private static bool IsSigner(FleetDeviceListState devices, string deviceId, string personId, [NotNullWhen(true)] out FleetDeviceListMember? signer)
        => devices.Members.TryGetValue(deviceId, out signer) && signer.Kind == FleetDeviceKinds.Controller && !devices.Removed.Contains(deviceId)
            && signer.OwnerId is not null && signer.OwnerId == personId;

    private static bool IsId(string? text) => !string.IsNullOrEmpty(text) && text.Length <= MaxIdLength && !text.Any(char.IsControl);

    private static bool Refuse(string why, out string reason)
    {
        reason = why;
        return false;
    }
}

/// <summary>A period of a budget.</summary>
/// <param name="Start">When it starts.</param>
/// <param name="End">When the next one starts.</param>
/// <param name="StartQuarter">Its first quarter hour.</param>
/// <param name="EndQuarter">The first quarter hour of the next one.</param>
public sealed record FleetBudgetPeriod(DateTimeOffset Start, DateTimeOffset End, long StartQuarter, long EndQuarter);

/// <summary>The source-generated contracts of the signed budgets and their Continue anyway.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = false)]
[JsonSerializable(typeof(FleetBudgetDefinition))]
[JsonSerializable(typeof(FleetSignedBudget))]
[JsonSerializable(typeof(FleetBudgetContinue))]
[JsonSerializable(typeof(FleetSignedBudgetContinue))]
public sealed partial class FleetBudgetJsonContext : JsonSerializerContext;
