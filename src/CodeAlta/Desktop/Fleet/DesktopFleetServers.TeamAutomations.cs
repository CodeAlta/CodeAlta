using CodeAlta.Automations;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Fleet;

// The automations of the teams this person maintains, and of the organization they answer for, that another person gave
// their machines (doc/fleet/orchestration.md, scopes): what each does is sealed for every person who answers for its scope,
// so this computer opens it, and the person changes it in their own editor; the next version is signed here.
internal sealed partial class DesktopFleetServers
{
    /// <summary>
    /// Lists the automations of a team or of the organization this computer's person answers for that the servers hold and
    /// this computer does not: each verified against the device list and the access document this computer verified, and
    /// opened here, for its name.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The automations; those of a server that does not answer are left out.</returns>
    internal async Task<IReadOnlyList<AutomationTeamItem>> TeamAutomationsAsync(CancellationToken cancellationToken)
    {
        var items = new List<AutomationTeamItem>();
        foreach (var connection in Connections)
        {
            try
            {
                var found = await ReadTeamAutomationsAsync(connection, cancellationToken).ConfigureAwait(false);
                if (found.Count == 0) continue;
                // The names of the teams come from the server; the organization is named by its link.
                var teams = found.Any(static item => item.Version.TeamId is not null)
                    ? (await AccessCallAsync(connection.Link.Id, static (client, token) => client.ListTeamsAsync(token), cancellationToken).ConfigureAwait(false)).Items
                    : [];
                items.AddRange(found.Select(item => new AutomationTeamItem(connection.Link.Id, item.Version.AutomationId, item.Version.MachineId, item.MachineName, item.Name,
                    item.Version.Scope!, item.Version.TeamId is { } team ? teams.FirstOrDefault(other => other.Id == team)?.Name ?? team
                        : connection.Link.OrganizationName ?? "Organization")));
            }
            catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or FleetDeviceListException
                or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        return items;
    }

    /// <summary>
    /// Takes an automation of a team or of the organization that another person gave a machine into the automations of this
    /// computer, as the server holds it now, so that this person changes it in their own editor.
    /// </summary>
    /// <param name="linkId">The server.</param>
    /// <param name="automationId">The automation.</param>
    /// <param name="machineId">The machine it runs on.</param>
    /// <param name="cancellationToken">Cancels the reads and the write.</param>
    /// <returns>Why it could not be taken, or null once it is.</returns>
    /// <exception cref="FleetServerException">The server refused the read.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    internal async Task<string?> OpenTeamAutomationAsync(string linkId, string automationId, string machineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(linkId);
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        if (Handover is not { } handover) return "Automations are not available here.";
        if (Connections.FirstOrDefault(connection => connection.Link.Id == linkId) is not { } connection) return "This computer is not connected to that server.";
        var matches = (await ReadTeamAutomationsAsync(connection, cancellationToken).ConfigureAwait(false))
            .Where(item => item.Version.AutomationId == automationId && item.Version.MachineId == machineId).ToList();
        if (matches.Count == 0) return "No automation of a team you maintain has this id on this machine, or it is here already.";
        var found = matches[0];
        return await handover.EditAsync(found.Content, machineId, found.Version.Scope!, linkId, found.Version.Number, cancellationToken).ConfigureAwait(false);
    }

    // The automations of a scope a server holds, that this computer does not, verified and opened here.
    private async Task<IReadOnlyList<(FleetAutomationVersion Version, FleetAutomationContent Content, string Name, string MachineName)>> ReadTeamAutomationsAsync(
        FleetServerConnection connection, CancellationToken cancellationToken)
    {
        if (connection.Connector is not { State: FleetConnectorState.Online } connector || connector.Access.Current is not { } access) return [];
        var organization = connection.Link.OrganizationId ?? connector.Devices.Current?.OrganizationId;
        if (organization is null) return [];
        var list = await connector.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var identity = Identity();
        using var client = Client(connection.Link.Id);
        var held = await client.ListAutomationsAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<(FleetAutomationVersion, FleetAutomationContent, string, string)>();
        foreach (var item in held.Automations.Where(static item => !item.Removed))
        {
            if (Handover?.Holds(item.Id) == true) continue;
            // As its machine verifies it: signed by a controller of the organization who answers for its scope there.
            if (!FleetAutomationVersionDocuments.TryVerify(item.Version, list, access, organization, item.MachineId, out var version, out _) || version.Scope is null) continue;
            if (!FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, identity, list, access, out var content, out _)) continue;
            var (definitions, faults) = AutomationConfig.Read(content.Definition, new AutomationSource("server", null, null));
            if (definitions.Count != 1 || faults.Count > 0 || definitions[0].Id != version.AutomationId) continue;
            var machineName = connector.Directory.Machines.TryGetValue(version.MachineId, out var machine) ? machine.Inventory.Name : version.MachineId;
            found.Add((version, content, definitions[0].Name, machineName));
        }

        return found;
    }
}
