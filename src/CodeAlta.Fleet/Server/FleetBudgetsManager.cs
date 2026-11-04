using System.Buffers.Text;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>What a budget limits, as a person sets it; its id, version, owner and signer are the manager's.</summary>
/// <param name="Name">Its name.</param>
/// <param name="ScopeKind">Whose spending it limits (<see cref="FleetBudgetScopeKinds"/>).</param>
/// <param name="ScopeId">The machine, project key or space; null for the person.</param>
/// <param name="Measure">Tokens or the estimated cost (<see cref="FleetBudgetMeasures"/>).</param>
/// <param name="Limit">The limit per period: tokens, or millionths of a US dollar.</param>
/// <param name="Period">The period (<see cref="FleetBudgetPeriods"/>).</param>
/// <param name="TimeZone">The time zone its periods start in.</param>
/// <param name="Mode">Advisory or enforced (<see cref="FleetBudgetModes"/>).</param>
/// <param name="Reached">What a new run does once it is reached (<see cref="FleetBudgetReachedActions"/>).</param>
public sealed record FleetBudgetSettings(string Name, string ScopeKind, string? ScopeId, string Measure, long Limit, string Period, string TimeZone, string Mode, string Reached);

/// <summary>
/// What a controller device of a person does with the budgets of that person at one server (<c>doc/fleet/observability.md</c>,
/// budgets and alerts): it signs each budget, so that machines verify it against the device list they anchored; it signs
/// Continue anyway for a run that waits; it stops one; it reads and dismisses the notifications of the person.
/// </summary>
public sealed class FleetBudgetsManager
{
    /// <summary>How long a Continue anyway this device signs stays valid.</summary>
    public static readonly TimeSpan ContinueLifetime = TimeSpan.FromHours(1);

    private readonly FleetOrganizationDevices _devices;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="FleetBudgetsManager"/> class.</summary>
    /// <param name="devices">The anchored device list of the server, whose key and HTTP handler the calls use too.</param>
    /// <param name="time">The clock; null for the system's.</param>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public FleetBudgetsManager(FleetOrganizationDevices devices, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Lists the budgets of this device's person, with their consumption.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The budgets.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetBudgetListDto> ListAsync(CancellationToken cancellationToken)
    {
        using var client = Client();
        return await client.ListBudgetsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs and stores a budget: a new one, or the next version of the one with this id.</summary>
    /// <param name="id">The budget to change; null for a new one.</param>
    /// <param name="settings">What it limits.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The budget as held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException">The budget does not hold, or this device is not a controller device of the list.</exception>
    /// <exception cref="KeyNotFoundException">No budget of the person has the id.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetBudgetDto> PutAsync(string? id, FleetBudgetSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SignAsync(id, settings, removed: false, cancellationToken);
    }

    /// <summary>Takes a budget away: its next version, signed as removed.</summary>
    /// <param name="id">The budget.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The budget as held.</returns>
    /// <exception cref="ArgumentException">The id is empty, or this device is not a controller device of the list.</exception>
    /// <exception cref="KeyNotFoundException">No budget of the person has the id.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetBudgetDto> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return SignAsync(id, null, removed: true, cancellationToken);
    }

    /// <summary>Lists the runs that wait or waited for the budgets of this device's person.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The holds, the newest first.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetBudgetHoldListDto> ListHoldsAsync(CancellationToken cancellationToken)
    {
        using var client = Client();
        return await client.ListBudgetHoldsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Continues anyway a run that waits for a reached budget: this device signs a Continue anyway bound to the budget and its
    /// version, the period, the machine, the run and the nonce the machine chose, valid for <see cref="ContinueLifetime"/>.
    /// </summary>
    /// <param name="holdId">The hold.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The hold, continued.</returns>
    /// <exception cref="ArgumentException">The id is empty, or this device is not a controller device of the list.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused it: the run was continued or stopped already.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetBudgetHoldDto> ContinueAsync(string holdId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(holdId);
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var person = PersonOf(list);
        using var client = Client();
        var hold = await client.GetBudgetHoldAsync(holdId, cancellationToken).ConfigureAwait(false);
        var now = Now();
        var decision = new FleetBudgetContinue(FleetBudgetContinue.CurrentVersion, list.OrganizationId!, hold.Id, hold.BudgetId, hold.Number, hold.PeriodStart, hold.MachineId,
            hold.RunId, hold.Nonce, person, _devices.Identity.DeviceId, now, now + ContinueLifetime);
        return await client.ContinueBudgetHoldAsync(holdId, SignContinue(_devices.Identity, decision), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops a run that waits for a reached budget: it does not start.</summary>
    /// <param name="holdId">The hold.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hold, stopped.</returns>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetBudgetHoldDto> StopAsync(string holdId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(holdId);
        using var client = Client();
        return await client.StopBudgetHoldAsync(holdId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the notifications of this device's person.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The notifications, the newest first.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetNotificationListDto> ListNotificationsAsync(CancellationToken cancellationToken)
    {
        using var client = Client();
        return await client.ListNotificationsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Dismisses a notification on every device of the person.</summary>
    /// <param name="id">The notification.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The notifications.</returns>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetNotificationListDto> ReadNotificationAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        using var client = Client();
        return await client.ReadNotificationAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs a budget with a device key, named as its signer.</summary>
    /// <param name="identity">The key of the controller device.</param>
    /// <param name="definition">The budget.</param>
    /// <returns>The signed budget.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The budget does not hold.</exception>
    public static FleetSignedBudget SignBudget(DeviceIdentity identity, FleetBudgetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(definition);
        var text = FleetBudgetDocuments.Serialize(definition with { SignerDeviceId = identity.DeviceId });
        if (!FleetBudgetDocuments.TryRead(text, out _, out var reason)) throw new ArgumentException($"The budget does not hold: {reason}.", nameof(definition));
        return new FleetSignedBudget(text, Base64Url.EncodeToString(identity.SignData(FleetBudgetDocuments.GetSignedBytes(text))));
    }

    /// <summary>Signs a Continue anyway with a device key, named as its signer.</summary>
    /// <param name="identity">The key of the controller device.</param>
    /// <param name="decision">The Continue anyway.</param>
    /// <returns>The signed Continue anyway.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The Continue anyway does not hold.</exception>
    public static FleetSignedBudgetContinue SignContinue(DeviceIdentity identity, FleetBudgetContinue decision)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(decision);
        var text = FleetBudgetDocuments.Serialize(decision with { SignerDeviceId = identity.DeviceId });
        if (!FleetBudgetDocuments.TryReadContinue(text, out _, out var reason)) throw new ArgumentException($"The Continue anyway does not hold: {reason}.", nameof(decision));
        return new FleetSignedBudgetContinue(text, Base64Url.EncodeToString(identity.SignData(FleetBudgetDocuments.GetContinueSignedBytes(text))));
    }

    private async Task<FleetBudgetDto> SignAsync(string? id, FleetBudgetSettings? settings, bool removed, CancellationToken cancellationToken)
    {
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var person = PersonOf(list);
        using var client = Client();
        FleetBudgetDefinition? held = null;
        if (id is not null)
        {
            var found = (await client.ListBudgetsAsync(cancellationToken).ConfigureAwait(false)).Budgets.FirstOrDefault(budget => budget.Id == id)
                ?? throw new KeyNotFoundException("No budget of this person has this id.");
            if (!FleetBudgetDocuments.TryRead(found.Signed.Payload, out held, out var unread)) throw new ArgumentException($"The budget held does not read: {unread}.", nameof(id));
        }

        var basis = settings ?? (held is null ? throw new ArgumentNullException(nameof(settings))
            : new FleetBudgetSettings(held.Name, held.ScopeKind, held.ScopeId, held.Measure, held.Limit, held.Period, held.TimeZone, held.Mode, held.Reached));
        var definition = new FleetBudgetDefinition(FleetBudgetDefinition.CurrentVersion, list.OrganizationId ?? throw new ArgumentException("The list names no organization yet."),
            person, id ?? FleetSecretSealing.NewId(), basis.Name.Trim(), basis.ScopeKind, FleetBudgetScopeKinds.HasId(basis.ScopeKind) ? basis.ScopeId : null, basis.Measure,
            basis.Limit, basis.Period, string.IsNullOrWhiteSpace(basis.TimeZone) ? "UTC" : basis.TimeZone, basis.Mode, basis.Reached, (held?.Number ?? 0) + 1, removed,
            _devices.Identity.DeviceId, Now());
        return await client.PutBudgetAsync(SignBudget(_devices.Identity, definition), cancellationToken).ConfigureAwait(false);
    }

    private FleetServerClient Client() => _devices.CreateClient();

    // To the second, as the signed documents of the fleet are.
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeSeconds(_time.GetUtcNow().ToUnixTimeSeconds());

    private string PersonOf(FleetDeviceListState list)
        => list.Members.TryGetValue(_devices.Identity.DeviceId, out var self) && self.Kind == FleetDeviceKinds.Controller && self.OwnerId is { } person
            ? person
            : throw new ArgumentException("Only a controller device of the device list, with its person, manages its budgets.");
}
