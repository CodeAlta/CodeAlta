using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Fleet;

/// <summary>The budgets, holds and notifications of a server as this computer reads them, with the names only it can give.</summary>
/// <param name="Budgets">The budgets of the person, each with its definition when it reads.</param>
/// <param name="Holds">The runs that wait or waited for them.</param>
/// <param name="Notifications">The notifications of the person.</param>
/// <param name="Devices">The devices of the organization, by id.</param>
/// <param name="Machines">The machines of the person in the device list this computer verified.</param>
/// <param name="ScopeNames">The names this computer gives the opaque ids of spaces and projects.</param>
internal sealed record DesktopFleetBudgets(IReadOnlyList<(FleetBudgetDto Budget, FleetBudgetDefinition? Definition)> Budgets, IReadOnlyList<FleetBudgetHoldDto> Holds,
    IReadOnlyList<FleetNotificationDto> Notifications, IReadOnlyDictionary<string, DeviceDto> Devices, IReadOnlyList<FleetDeviceListMember> Machines,
    IReadOnlyDictionary<string, string> ScopeNames);

// The budgets of a server (doc/fleet/observability.md, budgets and alerts): this computer signs them, and Continue anyway, with
// its key; it names the machines, spaces and projects the server knows by ids.
internal sealed partial class DesktopFleetServers
{
    /// <summary>Gets what manages the budgets of this computer's person at a server.</summary>
    /// <param name="linkId">The link.</param>
    /// <returns>The manager, over the anchored device list of the link.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    public FleetBudgetsManager BudgetsOf(string linkId) => new(SecretsOf(linkId).Devices);

    /// <summary>
    /// Lists the runs of machines of the servers that wait for this computer's person: the messages of kind approval they did
    /// not dismiss, whose problem lasts, each naming its automation, machine and run. A server that does not answer lists none.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The runs, by server.</returns>
    public async Task<IReadOnlyList<AltaWaitingRun>> WaitingRunsAsync(CancellationToken cancellationToken)
    {
        var runs = new List<AltaWaitingRun>();
        foreach (var connection in Connections)
        {
            try
            {
                var read = await ReadBudgetsAsync(connection.Link.Id, cancellationToken).ConfigureAwait(false);
                foreach (var notification in read.Notifications.Where(static notification => notification.Kind == FleetNotificationKinds.Approval && notification.ReadAt is null
                    && notification.ResolvedAt is null && notification is { Subject.Length: > 0, Detail.Length: > 0, MachineId.Length: > 0 }))
                {
                    runs.Add(new AltaWaitingRun(notification.Detail!, notification.Subject!, null, notification.MachineId,
                        read.Devices.GetValueOrDefault(notification.MachineId!)?.Name ?? notification.MachineId, null, notification.CreatedAt));
                }
            }
            catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or FleetDeviceListException or KeyNotFoundException
                or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // A server that does not answer now tells it again at the next read.
            }
        }

        return runs;
    }

    /// <summary>Reads the budgets, holds and notifications of this computer's person at a server.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>What the server holds, named.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused a call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DesktopFleetBudgets> ReadBudgetsAsync(string linkId, CancellationToken cancellationToken)
    {
        var secrets = SecretsOf(linkId);
        var devicesOfLink = secrets.Devices;
        var manager = new FleetBudgetsManager(devicesOfLink);
        var list = await devicesOfLink.RefreshAsync(cancellationToken).ConfigureAwait(false);
        using var client = Client(linkId);
        var budgets = await manager.ListAsync(cancellationToken).ConfigureAwait(false);
        var holds = await manager.ListHoldsAsync(cancellationToken).ConfigureAwait(false);
        var notifications = await manager.ListNotificationsAsync(cancellationToken).ConfigureAwait(false);
        var devices = (await client.ListDevicesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static device => device.Id, StringComparer.Ordinal);
        var person = list.Members.TryGetValue(client.DeviceId, out var self) && self.OwnerId is { } owner ? owner : string.Empty;
        // The machines of the person: those the signed access document gives them included, those it gives another person not.
        var access = secrets.Access?.Invoke();
        var machines = list.Members.Values.Where(member => FleetAccessState.PersonOf(access, member) == person && member.Kind == FleetDeviceKinds.Node && !list.Removed.Contains(member.DeviceId))
            .OrderBy(member => devices.GetValueOrDefault(member.DeviceId)?.Name ?? member.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray();
        return new DesktopFleetBudgets([.. budgets.Budgets.Select(static budget => (budget, FleetBudgetDocuments.TryRead(budget.Signed.Payload, out var definition, out _) ? definition : null))],
            holds.Holds, notifications.Notifications, devices, machines, await ScopeNamesAsync(linkId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Gives the id of the scope of a budget as the server knows it: a machine by its device, a space by its opaque id, a project by its blind index.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="kind">The kind of the scope (<see cref="FleetBudgetScopeKinds"/>).</param>
    /// <param name="id">The machine, or the space or the project on this computer; null for the person.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The id at the server; null for the person.</returns>
    /// <exception cref="ArgumentException">The scope cannot be given.</exception>
    public async Task<string?> BudgetScopeAsync(string linkId, string kind, string? id, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case FleetBudgetScopeKinds.Person:
                return null;
            case FleetBudgetScopeKinds.Machine when !string.IsNullOrWhiteSpace(id):
                return id;
            case FleetBudgetScopeKinds.Space or FleetBudgetScopeKinds.Project when !string.IsNullOrWhiteSpace(id):
                return (await ScopeAsync(linkId, kind == FleetBudgetScopeKinds.Space ? FleetSecretScopeKinds.Space : FleetSecretScopeKinds.Project, id, cancellationToken)
                    .ConfigureAwait(false)).Id;
            default:
                throw new ArgumentException("A budget limits the person, a machine, a space or a project.");
        }
    }
}
