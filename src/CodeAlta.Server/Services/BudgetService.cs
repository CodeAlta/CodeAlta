using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Services;

/// <summary>
/// The budgets of people (<c>doc/fleet/observability.md</c>, budgets and alerts). A budget is a document its owner's controller
/// device signed; the server reads its consumption from the statistics facts the machines publish, tells its owner at 80% and
/// 100% of each period once, and for an enforced budget gives each machine an allowance it spends without asking, within the
/// limit. A run that waits for a reached budget is a hold, released by a Continue anyway a controller device of the owner signs
/// (the machine verifies it), or stopped.
/// </summary>
/// <remarks>
/// The server can deny service (drop a budget, give no allowance) and sees the facts, as it does for statistics; it cannot
/// loosen a budget or continue a run: the machines verify the budgets and the Continue anyway against the device list they
/// anchored.
/// </remarks>
internal sealed class BudgetService
{
    /// <summary>The most holds listed.</summary>
    public const int MaxListed = 200;

    /// <summary>The share of the limit at which the owner is warned, in hundredths.</summary>
    public const int WarningPercent = 80;

    private readonly ICoordinatorStore _store;
    private readonly IBudgetStore _budgets;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="BudgetService"/> class.</summary>
    /// <param name="store">The store of the organization.</param>
    /// <param name="budgets">The store of the budgets.</param>
    /// <param name="notifications">The notifications of people.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The log.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public BudgetService(ICoordinatorStore store, IBudgetStore budgets, NotificationService notifications, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _budgets = budgets;
        _notifications = notifications;
        _time = time;
        _logger = logger;
    }

    /// <summary>Lists the budgets of the calling controller device's person, with their consumption in the current period.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The budgets, those taken away left out.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<FleetBudgetListDto> ListAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        RequireController(actor);
        var budgets = await _budgets.ListBudgetsAsync(actor.OrganizationId, actor.OwnerPersonId, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var listed = new List<FleetBudgetDto>(budgets.Count);
        foreach (var budget in budgets.Where(static budget => !budget.Removed))
        {
            if (!FleetBudgetDocuments.TryRead(budget.Payload, out var definition, out _)) continue;
            listed.Add(ToDto(budget, (await UsageAsync(budget, definition, now, cancellationToken).ConfigureAwait(false)).Dto));
        }

        return new FleetBudgetListDto(listed);
    }

    /// <summary>Stores a budget, or a newer version of it, that the calling controller device signed for its person.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The budget.</param>
    /// <param name="request">The signed budget.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The budget as held.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device, the budget does not hold or is not signed by the caller, or it is not newer.</exception>
    public async Task<FleetBudgetDto> PutAsync(DeviceRecord actor, string id, PutFleetBudgetRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        RequireController(actor);
        ArgumentNullException.ThrowIfNull(origin);
        if (request?.Budget is not { } signed || !FleetBudgetDocuments.TryRead(signed.Payload, out var definition, out _))
            throw Invalid("The budget does not hold.");
        if (definition.BudgetId != id || definition.OrganizationId != actor.OrganizationId || definition.PersonId != actor.OwnerPersonId
            || definition.SignerDeviceId != actor.Id || !VerifiedBy(actor, FleetBudgetDocuments.GetSignedBytes(signed.Payload), signed.Signature))
            throw Invalid("A budget is of the person of the controller device that signs it, which sends it, for this organization and this id.");
        if (!FleetBudgetScopeKinds.IsAvailable(definition.ScopeKind))
            throw Invalid($"The budgets of a {definition.ScopeKind} come with its roles: limit the person, a machine, a project or a space.");
        if (definition.ScopeKind == FleetBudgetScopeKinds.Machine)
        {
            var machine = await _store.GetDeviceAsync(definition.ScopeId!, cancellationToken).ConfigureAwait(false);
            if (machine is null || machine.OrganizationId != actor.OrganizationId || machine.OwnerPersonId != actor.OwnerPersonId)
                throw Invalid("A budget of a machine names a machine of its person.");
        }

        var held = await _budgets.ListBudgetsAsync(actor.OrganizationId, actor.OwnerPersonId, cancellationToken).ConfigureAwait(false);
        if (!definition.Removed && held.All(budget => budget.Id != id || budget.Removed) && held.Count(static budget => !budget.Removed) >= FleetBudgetDocuments.MaxBudgets)
            throw Invalid($"A person has at most {FleetBudgetDocuments.MaxBudgets} budgets.");
        var record = new BudgetRecord(id, actor.OrganizationId, actor.OwnerPersonId!, definition.Number, definition.Removed, signed.Payload, signed.Signature, actor.Id,
            _time.GetUtcNow());
        var audit = origin.Audit(definition.Removed ? "budget.remove" : "budget.set", actor, id, details: Json(("name", definition.Name), ("scope", definition.ScopeKind),
            ("measure", definition.Measure), ("limit", definition.Limit.ToString(CultureInfo.InvariantCulture)), ("period", definition.Period), ("mode", definition.Mode),
            ("reached", definition.Reached), ("number", definition.Number.ToString(CultureInfo.InvariantCulture))));
        if (!await _budgets.PutBudgetAsync(record, audit, cancellationToken).ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.BudgetConflict, "The budget held is as new, or of another person: read the budgets again.");
        // The machines of the person read their budgets again; a lower limit may be reached already.
        await _notifications.TellAsync(actor.OrganizationId, actor.OwnerPersonId!, FleetBudgetMethods.Changed, controllers: false, cancellationToken).ConfigureAwait(false);
        await _notifications.TellAsync(actor.OrganizationId, actor.OwnerPersonId!, FleetBudgetMethods.Changed, controllers: true, cancellationToken, except: actor.Id)
            .ConfigureAwait(false);
        var usage = await UsageAsync(record, definition, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (!definition.Removed) await AlertAsync(record, definition, usage, exhausted: false, cancellationToken).ConfigureAwait(false);
        return ToDto(record, definition.Removed ? null : usage.Dto);
    }

    /// <summary>Lists the budgets of the owner of the calling machine, with what the machine holds of each: the machine verifies them and keeps those that cover its runs.</summary>
    /// <param name="actor">The calling machine.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The budgets, those taken away as their signed removal: a machine keeps every budget it verified until a newer signed
    /// version, a removal included, replaces it.
    /// </returns>
    public async Task<FleetMachineBudgetListDto> ListForMachineAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is null || actor.Status != DeviceStatuses.Approved) return new FleetMachineBudgetListDto([]);
        var budgets = await _budgets.ListBudgetsAsync(actor.OrganizationId, actor.OwnerPersonId, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var listed = new List<FleetMachineBudgetDto>();
        foreach (var budget in budgets)
        {
            if (!FleetBudgetDocuments.TryRead(budget.Payload, out var definition, out _)) continue;
            if (definition.ScopeKind == FleetBudgetScopeKinds.Machine && definition.ScopeId != actor.Id) continue;
            var usage = await UsageAsync(budget, definition, now, cancellationToken).ConfigureAwait(false);
            listed.Add(new FleetMachineBudgetDto(budget.Id, new FleetSignedBudget(budget.Payload, budget.Signature), usage.Dto, usage.Granted.GetValueOrDefault(actor.Id),
                usage.Used.GetValueOrDefault(actor.Id)));
        }

        return new FleetMachineBudgetListDto(listed);
    }

    /// <summary>Gives the calling machine more of an enforced budget of its owner, within what no machine committed.</summary>
    /// <param name="actor">The calling machine.</param>
    /// <param name="id">The budget.</param>
    /// <param name="request">The version the machine verified, what it spent and what it needs.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the machine holds now in the period.</returns>
    /// <exception cref="CoordinatorException">The budget is not one of the owner of the machine, not enforced, or not the version the machine verified.</exception>
    public async Task<FleetAllowanceDto> AllowanceAsync(DeviceRecord actor, string id, FleetAllowanceRequest? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var (budget, definition) = await GetOwnAsync(actor, id, cancellationToken).ConfigureAwait(false);
        if (request is null || request.Spent < 0 || request.Needed < 0 || request.Needed > definition.Limit)
            throw Invalid("An allowance is asked with the version of the budget, what the machine spent and what it needs, at most the limit.");
        if (definition.Mode != FleetBudgetModes.Enforced) throw Invalid("Only an enforced budget gives allowances.");
        if (request.Number != budget.Number) throw CoordinatorException.Conflict(CoordinatorErrorCodes.BudgetConflict, "The budget changed: read it again.");
        var now = _time.GetUtcNow();
        var period = FleetBudgetDocuments.PeriodOf(definition, now);
        var (granted, exhausted) = await _budgets.GrantAllowanceAsync(new BudgetAllowanceAsk(budget.Id, period.StartQuarter, actor.Id, definition.Limit, request.Spent,
            request.Needed, BudgetAllowances.Share(definition.Limit), Query(budget, definition, period)), now, cancellationToken).ConfigureAwait(false);
        if (exhausted)
        {
            var usage = await UsageAsync(budget, definition, now, cancellationToken).ConfigureAwait(false);
            await AlertAsync(budget, definition, usage, exhausted: true, cancellationToken).ConfigureAwait(false);
        }

        return new FleetAllowanceDto(budget.Id, period.StartQuarter, granted, exhausted);
    }

    /// <summary>Records that a run of the calling machine waits for a reached budget of its owner; the owner's devices are told.</summary>
    /// <param name="actor">The calling machine.</param>
    /// <param name="id">The budget.</param>
    /// <param name="request">The run, its nonce, the version of the budget and the period.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The hold held for that run.</returns>
    /// <exception cref="CoordinatorException">The budget is not one of the owner of the machine, or the request does not hold.</exception>
    public async Task<FleetBudgetHoldDto> RequestHoldAsync(DeviceRecord actor, string id, RequestFleetBudgetHoldRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(origin);
        var (budget, definition) = await GetOwnAsync(actor, id, cancellationToken).ConfigureAwait(false);
        if (request is null || !IsText(request.RunId, 128) || !IsText(request.Nonce, 128) || request.Number < 1 || request.Title is { Length: > 200 } || request.Title?.Any(char.IsControl) == true)
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.BudgetHoldInvalid, "A hold names its run, its nonce, the version of the budget and its period; a title of at most 200 characters.");
        var hold = new BudgetHoldRecord(NewId(), actor.OrganizationId, budget.Id, request.Number, request.PeriodStart, actor.Id, request.RunId, request.Nonce, request.Title,
            FleetBudgetHoldStates.Waiting, _time.GetUtcNow(), null, null, null, null);
        var held = await _budgets.RequestBudgetHoldAsync(hold, origin.Audit("budget.hold", actor, hold.Id, details: Json(("budget", budget.Id), ("run", request.RunId))),
            cancellationToken).ConfigureAwait(false);
        if (held.Nonce != request.Nonce) throw CoordinatorException.Conflict(CoordinatorErrorCodes.BudgetHoldConflict, "This run asked another hold already.");
        if (held.Id == hold.Id)
            await _notifications.TellAsync(actor.OrganizationId, budget.OwnerPersonId, FleetBudgetMethods.Changed, controllers: true, cancellationToken).ConfigureAwait(false);
        return ToDto(held, definition.Name);
    }

    /// <summary>Reads a hold: its machine, or a controller device of the owner of the budget.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The hold.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The hold.</returns>
    /// <exception cref="CoordinatorException">The hold is unknown to the caller.</exception>
    public async Task<FleetBudgetHoldDto> GetHoldAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        var (hold, _, definition) = await ReadHoldAsync(actor, id, cancellationToken).ConfigureAwait(false);
        return ToDto(hold, definition.Name);
    }

    /// <summary>Lists the holds of the budgets of the calling device's person, the newest first; a machine sees its own only.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The holds.</returns>
    public async Task<FleetBudgetHoldListDto> ListHoldsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is null) return new FleetBudgetHoldListDto([]);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var budget in await _budgets.ListBudgetsAsync(actor.OrganizationId, actor.OwnerPersonId, cancellationToken).ConfigureAwait(false))
            names[budget.Id] = FleetBudgetDocuments.TryRead(budget.Payload, out var definition, out _) ? definition.Name : budget.Id;
        var holds = await _budgets.ListBudgetHoldsAsync(actor.OrganizationId, names.Keys, MaxListed, cancellationToken).ConfigureAwait(false);
        return new FleetBudgetHoldListDto([.. holds.Where(hold => actor.Kind == FleetDeviceKinds.Controller || hold.MachineId == actor.Id)
            .Select(hold => ToDto(hold, names[hold.BudgetId]))]);
    }

    /// <summary>Continues a run that waits, with the Continue anyway the calling controller device of the owner signed; the machine verifies it.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The hold.</param>
    /// <param name="request">The signed Continue anyway.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The hold, continued.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of the owner, the Continue anyway does not match the hold, or the hold is decided already.</exception>
    public async Task<FleetBudgetHoldDto> ContinueAsync(DeviceRecord actor, string id, ContinueFleetBudgetHoldRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        RequireController(actor);
        ArgumentNullException.ThrowIfNull(origin);
        var (hold, budget, definition) = await ReadHoldAsync(actor, id, cancellationToken).ConfigureAwait(false);
        if (budget.OwnerPersonId != actor.OwnerPersonId) throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only the owner of the budget continues its runs.");
        if (hold.Status != FleetBudgetHoldStates.Waiting) throw CoordinatorException.Conflict(CoordinatorErrorCodes.BudgetHoldConflict, "The run was continued or stopped already.");
        if (request?.Continue is not { } signed || !FleetBudgetDocuments.TryReadContinue(signed.Payload, out var read, out _)
            || read.HoldId != hold.Id || read.OrganizationId != actor.OrganizationId || read.BudgetId != hold.BudgetId || read.BudgetNumber != hold.Number
            || read.PeriodStart != hold.PeriodStart || read.MachineId != hold.MachineId || read.RunId != hold.RunId || read.Nonce != hold.Nonce
            || read.PersonId != actor.OwnerPersonId || read.SignerDeviceId != actor.Id
            || !VerifiedBy(actor, FleetBudgetDocuments.GetContinueSignedBytes(signed.Payload), signed.Signature))
        {
            throw CoordinatorException.BadRequest(CoordinatorErrorCodes.BudgetHoldInvalid,
                "The Continue anyway is signed by the controller device that sends it, for this hold, budget, period, machine, run and nonce.");
        }

        var now = _time.GetUtcNow();
        if (!await _budgets.DecideBudgetHoldAsync(actor.OrganizationId, id, FleetBudgetHoldStates.Continued, signed.Payload, signed.Signature, actor.Id, now,
                origin.Audit("budget.continue", actor, id, details: Json(("budget", hold.BudgetId), ("machine", hold.MachineId), ("run", hold.RunId))), cancellationToken)
            .ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.BudgetHoldConflict, "The run was continued or stopped already.");
        _notifications.Tell(hold.MachineId, FleetBudgetMethods.Changed);
        await _notifications.TellAsync(actor.OrganizationId, actor.OwnerPersonId!, FleetBudgetMethods.Changed, controllers: true, cancellationToken, except: actor.Id)
            .ConfigureAwait(false);
        return ToDto(hold with { Status = FleetBudgetHoldStates.Continued, ContinuePayload = signed.Payload, ContinueSignature = signed.Signature, DecidedByDeviceId = actor.Id,
            DecidedAt = now }, definition.Name);
    }

    /// <summary>Stops a run that waits: it does not start. Stricter than waiting, so it needs no signature.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The hold.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The hold, stopped.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of the owner, or the hold is decided already.</exception>
    public async Task<FleetBudgetHoldDto> StopAsync(DeviceRecord actor, string id, RequestOrigin origin, CancellationToken cancellationToken)
    {
        RequireController(actor);
        ArgumentNullException.ThrowIfNull(origin);
        var (hold, budget, definition) = await ReadHoldAsync(actor, id, cancellationToken).ConfigureAwait(false);
        if (budget.OwnerPersonId != actor.OwnerPersonId) throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only the owner of the budget stops its runs.");
        var now = _time.GetUtcNow();
        if (!await _budgets.DecideBudgetHoldAsync(actor.OrganizationId, id, FleetBudgetHoldStates.Stopped, null, null, actor.Id, now,
                origin.Audit("budget.stop", actor, id, details: Json(("budget", hold.BudgetId), ("machine", hold.MachineId), ("run", hold.RunId))), cancellationToken)
            .ConfigureAwait(false))
            throw CoordinatorException.Conflict(CoordinatorErrorCodes.BudgetHoldConflict, "The run was continued or stopped already.");
        _notifications.Tell(hold.MachineId, FleetBudgetMethods.Changed);
        await _notifications.TellAsync(actor.OrganizationId, actor.OwnerPersonId!, FleetBudgetMethods.Changed, controllers: true, cancellationToken, except: actor.Id)
            .ConfigureAwait(false);
        return ToDto(hold with { Status = FleetBudgetHoldStates.Stopped, DecidedByDeviceId = actor.Id, DecidedAt = now }, definition.Name);
    }

    /// <summary>
    /// Reads the consumption of the budgets of the owner of a machine whose facts just arrived, and tells the owner of each that
    /// passed 80% or 100% of its period, once. A reached budget is pushed to the owner's machines, which hold new runs at once.
    /// </summary>
    /// <param name="device">The machine that published.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A task that completes once the budgets were read.</returns>
    public async Task EvaluateAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.OwnerPersonId is null) return;
        try
        {
            var now = _time.GetUtcNow();
            foreach (var budget in await _budgets.ListBudgetsAsync(device.OrganizationId, device.OwnerPersonId, cancellationToken).ConfigureAwait(false))
            {
                if (budget.Removed || !FleetBudgetDocuments.TryRead(budget.Payload, out var definition, out _)) continue;
                var usage = await UsageAsync(budget, definition, now, cancellationToken).ConfigureAwait(false);
                await AlertAsync(budget, definition, usage, exhausted: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            // The facts are kept whatever the budgets say: the next publication reads them again.
            _logger.LogWarning("The budgets could not be read after a publication: {Message}", exception.Message);
        }
    }

    // 80% and 100% of a period, each once: the key names the budget, its period and the threshold.
    private async Task AlertAsync(BudgetRecord budget, FleetBudgetDefinition definition, Usage usage, bool exhausted, CancellationToken cancellationToken)
    {
        var used = usage.Dto.Used;
        var period = usage.Period.StartQuarter.ToString(CultureInfo.InvariantCulture);
        var detail = $"{used.ToString(CultureInfo.InvariantCulture)}/{definition.Limit.ToString(CultureInfo.InvariantCulture)}";
        var reached = exhausted || used >= definition.Limit;
        if (reached)
        {
            if (await _notifications.NotifyAsync(budget.OrganizationId, budget.OwnerPersonId, $"budget:{budget.Id}:{period}:100", FleetNotificationKinds.BudgetReached, "error",
                    budget.Id, null, null, definition.Name, detail, cancellationToken).ConfigureAwait(false) is not null)
            {
                // Budget reached is a stricter rule: the machines read it at once and hold or stop new runs.
                await _notifications.TellAsync(budget.OrganizationId, budget.OwnerPersonId, FleetBudgetMethods.Changed, controllers: false, cancellationToken).ConfigureAwait(false);
            }
        }

        if (reached || used * 100 >= definition.Limit * (long)WarningPercent)
        {
            await _notifications.NotifyAsync(budget.OrganizationId, budget.OwnerPersonId, $"budget:{budget.Id}:{period}:80", FleetNotificationKinds.BudgetWarning, "warning",
                budget.Id, null, null, definition.Name, detail, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Usage> UsageAsync(BudgetRecord budget, FleetBudgetDefinition definition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var period = FleetBudgetDocuments.PeriodOf(definition, now);
        var used = await _budgets.SumBudgetUsageAsync(Query(budget, definition, period), cancellationToken).ConfigureAwait(false);
        var granted = definition.Mode == FleetBudgetModes.Enforced
            ? await _budgets.ListAllowancesAsync(budget.Id, period.StartQuarter, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, long>(StringComparer.Ordinal);
        var total = used.Values.Sum();
        var committed = definition.Mode == FleetBudgetModes.Enforced ? BudgetAllowances.Committed(granted, used) : 0;
        var state = total >= definition.Limit ? FleetBudgetStates.Reached
            : total * 100 >= definition.Limit * (long)WarningPercent ? FleetBudgetStates.Warning : FleetBudgetStates.Ok;
        return new Usage(period, used, granted, new FleetBudgetUsageDto(period.Start, period.End, total, definition.Limit, committed, state));
    }

    private static BudgetUsageQuery Query(BudgetRecord budget, FleetBudgetDefinition definition, FleetBudgetPeriod period)
        => new(budget.OrganizationId, budget.OwnerPersonId, definition.ScopeKind, definition.ScopeId, definition.Measure, period.StartQuarter, period.EndQuarter);

    private async Task<(BudgetRecord Budget, FleetBudgetDefinition Definition)> GetOwnAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        if (!IsText(id, 128) || actor.OwnerPersonId is null) throw NotFound();
        var budget = await _budgets.GetBudgetAsync(actor.OrganizationId, id, cancellationToken).ConfigureAwait(false);
        if (budget is null || budget.Removed || budget.OwnerPersonId != actor.OwnerPersonId || !FleetBudgetDocuments.TryRead(budget.Payload, out var definition, out _))
            throw NotFound();
        return (budget, definition);
    }

    private async Task<(BudgetHoldRecord Hold, BudgetRecord Budget, FleetBudgetDefinition Definition)> ReadHoldAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!IsText(id, 64) || actor.OwnerPersonId is null) throw HoldNotFound();
        var hold = await _budgets.GetBudgetHoldAsync(actor.OrganizationId, id, cancellationToken).ConfigureAwait(false) ?? throw HoldNotFound();
        var budget = await _budgets.GetBudgetAsync(actor.OrganizationId, hold.BudgetId, cancellationToken).ConfigureAwait(false) ?? throw HoldNotFound();
        var mayRead = hold.MachineId == actor.Id || actor.Kind == FleetDeviceKinds.Controller && budget.OwnerPersonId == actor.OwnerPersonId;
        if (!mayRead || !FleetBudgetDocuments.TryRead(budget.Payload, out var definition, out _)) throw HoldNotFound();
        return (hold, budget, definition);
    }

    private static FleetBudgetDto ToDto(BudgetRecord budget, FleetBudgetUsageDto? usage)
        => new(budget.Id, new FleetSignedBudget(budget.Payload, budget.Signature), budget.Number, budget.Removed, budget.UpdatedAt, usage);

    private static FleetBudgetHoldDto ToDto(BudgetHoldRecord hold, string name)
        => new(hold.Id, hold.BudgetId, name, hold.Number, hold.PeriodStart, hold.MachineId, hold.RunId, hold.Nonce, hold.Title, hold.Status, hold.RequestedAt,
            hold.ContinuePayload is { } payload && hold.ContinueSignature is { } signature ? new FleetSignedBudgetContinue(payload, signature) : null, hold.DecidedAt);

    private static bool VerifiedBy(DeviceRecord actor, byte[] signed, string? signature)
        => FleetDevicePublicKey.TryParse(actor.PublicKey, out var key) && key.Verify(signed, signature);

    private static void RequireController(DeviceRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != FleetDeviceKinds.Controller || actor.OwnerPersonId is null)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a controller device of a person manages its budgets; a machine reads its own.");
    }

    private static string Json(params (string Name, string Value)[] fields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields) writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string NewId() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private static bool IsText(string? text, int max) => !string.IsNullOrEmpty(text) && text.Length <= max && !text.Any(char.IsControl);

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(CoordinatorErrorCodes.BudgetInvalid, message);

    private static CoordinatorException NotFound() => CoordinatorException.NotFound(CoordinatorErrorCodes.BudgetNotFound, "No budget of this person has this id.");

    private static CoordinatorException HoldNotFound() => CoordinatorException.NotFound(CoordinatorErrorCodes.BudgetHoldNotFound, "No hold the caller may see has this id.");

    private sealed record Usage(FleetBudgetPeriod Period, IReadOnlyDictionary<string, long> Used, IReadOnlyDictionary<string, long> Granted, FleetBudgetUsageDto Dto);
}
