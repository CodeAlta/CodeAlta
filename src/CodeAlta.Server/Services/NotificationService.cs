using System.Security.Cryptography;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// The notifications of people (<c>doc/fleet/observability.md</c>, what people see): a budget at 80% or 100%, a lasting problem
/// of one of their machines. Each problem is told once: its key is unique per person, so a replay of the facts, a restart of
/// the server or a problem reported at every heartbeat makes no second one. The controller devices of the person online are
/// told at once; the others read them when they connect.
/// </summary>
internal sealed class NotificationService
{
    /// <summary>The most notifications listed.</summary>
    public const int MaxListed = 100;

    private readonly ICoordinatorStore _store;
    private readonly IBudgetStore _notifications;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="NotificationService"/> class.</summary>
    /// <param name="store">The store of the organization.</param>
    /// <param name="notifications">The store of the notifications.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public NotificationService(ICoordinatorStore store, IBudgetStore notifications, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _notifications = notifications;
        _time = time;
    }

    /// <summary>Gets the devices online, which are told of new notifications; null tells none.</summary>
    public PresenceRegistry? Presence { get; init; }

    /// <summary>Gets the access model, whose owner records say who owns a machine given to another person: it is told what its owner's machines are; null reads the device records alone.</summary>
    public IAccessStore? Access { get; init; }

    /// <summary>Adds a notification for a person, once per key.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="personId">The person.</param>
    /// <param name="key">What makes it once.</param>
    /// <param name="kind">What it is about (<see cref="FleetNotificationKinds"/>).</param>
    /// <param name="severity"><c>info</c>, <c>warning</c> or <c>error</c>.</param>
    /// <param name="budgetId">The budget, for a budget.</param>
    /// <param name="machineId">The machine, for a problem of a machine.</param>
    /// <param name="problem">The kind of the problem of a machine.</param>
    /// <param name="subject">What it is about.</param>
    /// <param name="detail">A short fact.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The id of the notification when it is new; null when the person had it.</returns>
    public async Task<string?> NotifyAsync(string organizationId, string personId, string key, string kind, string severity, string? budgetId, string? machineId,
        string? problem, string? subject, string? detail, CancellationToken cancellationToken)
    {
        var id = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var added = await _notifications.AddNotificationAsync(new NotificationRecord(id, organizationId, personId, key, kind, severity, budgetId, machineId, problem,
            Bounded(subject), Bounded(detail), _time.GetUtcNow(), null, null), cancellationToken).ConfigureAwait(false);
        if (!added) return null;
        await TellAsync(organizationId, personId, FleetBudgetMethods.NotificationsChanged, controllers: true, cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>Lists the notifications of the calling device's person, the newest first.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The notifications; none for a device without a person.</returns>
    public async Task<FleetNotificationListDto> ListAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is not { } person || actor.Kind != FleetDeviceKinds.Controller) return new FleetNotificationListDto([]);
        var listed = await _notifications.ListNotificationsAsync(actor.OrganizationId, person, MaxListed, cancellationToken).ConfigureAwait(false);
        return new FleetNotificationListDto([.. listed.Select(ToDto)]);
    }

    /// <summary>Marks a notification of the calling device's person read: it is no longer shown as new on any of the person's devices.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The notification.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The notifications of the person.</returns>
    /// <exception cref="CoordinatorException">The person has no such notification.</exception>
    public async Task<FleetNotificationListDto> ReadAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is not { } person || actor.Kind != FleetDeviceKinds.Controller || string.IsNullOrEmpty(id) || id.Length > 64
            || !await _notifications.ReadNotificationAsync(actor.OrganizationId, person, id, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            throw CoordinatorException.NotFound(CoordinatorErrorCodes.NotificationNotFound, "The person has no notification with this id.");
        await TellAsync(actor.OrganizationId, person, FleetBudgetMethods.NotificationsChanged, controllers: true, cancellationToken, except: actor.Id).ConfigureAwait(false);
        return await ListAsync(actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tells the devices of a person online that something of theirs changed.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="personId">The person.</param>
    /// <param name="method">The event (<see cref="FleetBudgetMethods"/>).</param>
    /// <param name="controllers">True for the person's controller devices, false for their machines.</param>
    /// <param name="cancellationToken">Cancels the read of the devices.</param>
    /// <param name="except">A device not told; null for none.</param>
    /// <returns>A task that completes once the events left.</returns>
    public async Task TellAsync(string organizationId, string personId, string method, bool controllers, CancellationToken cancellationToken, string? except = null)
    {
        if (Presence is not { } presence) return;
        // The machines of the person: those given to them included, by their owner record, and those of the teams they maintain,
        // whose budgets they set; never a machine of a team by the person the device list names alone.
        var snapshot = controllers ? null : await MachineOwners.ReadAsync(Access, organizationId, cancellationToken).ConfigureAwait(false);
        foreach (var device in await _store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false))
        {
            if (!MachineOwners.HoldsValuesOf(snapshot, device, personId) || device.Status != DeviceStatuses.Approved || device.Id == except) continue;
            if ((device.Kind == FleetDeviceKinds.Controller) != controllers) continue;
            if (presence.Get(device.Id) is { } session) _ = SendAsync(session, method);
        }
    }

    /// <summary>Tells every device of the organization online that something they all read changed, such as access.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="method">The event.</param>
    public void TellOrganization(string organizationId, string method)
    {
        if (Presence is not { } presence) return;
        foreach (var session in presence.List(organizationId)) _ = SendAsync(session, method);
    }

    /// <summary>Tells one device online that something of its own changed.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="method">The event.</param>
    public void Tell(string deviceId, string method)
    {
        if (Presence?.Get(deviceId) is { } session) _ = SendAsync(session, method);
    }

    internal static FleetNotificationDto ToDto(NotificationRecord record)
        => new(record.Id, record.Kind, record.Severity, record.BudgetId, record.MachineId, record.Problem, record.Subject, record.Detail, record.CreatedAt, record.ResolvedAt,
            record.ReadAt);

    private static async Task SendAsync(ControlSession session, string method)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await session.Connection.SendEventAsync(new FleetEvent(method, null), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetException or FleetConnectionClosedException or OperationCanceledException or InvalidOperationException)
        {
            // The device reads them when it asks next, or when it connects again.
        }
    }

    private static string? Bounded(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var kept = text.Length > 128 ? text[..128] : text;
        return kept.Any(char.IsControl) ? new string([.. kept.Select(static c => char.IsControl(c) ? ' ' : c)]) : kept;
    }
}
