using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Fleet;

/// <summary>
/// The way to give automations to a machine through a server that lists it (<c>doc/fleet/orchestration.md</c>): what an
/// automation does is sealed for the machine and the controller devices of this computer's person, as the device list of
/// that server verifies them here, and the signed version goes to the server, which delivers it to the machine.
/// </summary>
internal sealed class DesktopServerAutomationRoute : IDisposable
{
    private readonly FleetOrganizationDevices _devices;
    private readonly DeviceIdentity _identity;
    private readonly FleetServerClient _client;

    /// <summary>Creates a route; it owns the client.</summary>
    /// <param name="linkId">The link of the server.</param>
    /// <param name="organizationId">The organization of the server.</param>
    /// <param name="machineName">The name of the machine, as the server lists it.</param>
    /// <param name="devices">The anchored device list of the server.</param>
    /// <param name="identity">The key of this computer, which seals.</param>
    /// <param name="client">The client of the REST API of the server.</param>
    internal DesktopServerAutomationRoute(string linkId, string organizationId, string machineName, FleetOrganizationDevices devices, DeviceIdentity identity, FleetServerClient client)
    {
        (LinkId, OrganizationId, MachineName, _devices, _identity, _client) = (linkId, organizationId, machineName, devices, identity, client);
    }

    /// <summary>Gets the link of the server.</summary>
    internal string LinkId { get; }

    /// <summary>Gets the organization of the server, which each version names.</summary>
    internal string OrganizationId { get; }

    /// <summary>Gets the name of the machine.</summary>
    internal string MachineName { get; }

    /// <summary>
    /// Gets the team of the machine when this computer's person maintains it, as the signed access document this computer
    /// verified says: what is given to that machine is an automation of the team, which runs as its service identity; null otherwise.
    /// </summary>
    internal string? TeamId { get; init; }

    /// <summary>Gets the scope the versions given through this route name: the team of the machine, or none for a personal automation.</summary>
    internal string? Scope => TeamId is { } team ? FleetAutomationVersion.TeamScopePrefix + team : null;

    /// <summary>Seals what a version does for its machine and the controllers of this computer's person, as the device list of the server verifies them now.</summary>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="ArgumentException">The machine is not in the device list.</exception>
    internal async Task<string> SealAsync(FleetAutomationContent content, string automationId, long number, string machineId, CancellationToken cancellationToken)
    {
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return FleetAutomationSigning.Seal(content, automationId, number, machineId, list, _identity);
    }

    /// <summary>
    /// Says whether the machine is a node of this computer's person, as the device list of the server verifies it now: the
    /// authority the server and the machine check for each version this computer signs for it.
    /// </summary>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    internal async Task<bool> IsOwnMachineAsync(string machineId, CancellationToken cancellationToken)
    {
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return list.Members.TryGetValue(machineId, out var machine) && machine.Kind == FleetDeviceKinds.Node && !list.Removed.Contains(machineId)
            && list.Members.TryGetValue(_identity.DeviceId, out var self) && self.Kind == FleetDeviceKinds.Controller && !list.Removed.Contains(self.DeviceId)
            && self.OwnerId is { } person && string.Equals(machine.OwnerId, person, StringComparison.Ordinal);
    }

    /// <summary>Gives a signed version to the server.</summary>
    internal Task<AutomationDto> PutAsync(FleetSignedAutomationVersion signed, CancellationToken cancellationToken) => _client.PutAutomationAsync(signed, cancellationToken);

    /// <summary>Asks the machine, through the server, to run an automation now.</summary>
    internal Task<AutomationRunDto> RunAsync(FleetSignedAutomationRunRequest signed, CancellationToken cancellationToken) => _client.RunAutomationAsync(signed, cancellationToken);

    /// <summary>Passes a take-over of a run in progress, signed by this computer, to the machine through the server.</summary>
    internal Task<AutomationRunDto> TakeOverAsync(FleetSignedAutomationRunRequest signed, CancellationToken cancellationToken) => _client.TakeOverAutomationRunAsync(signed, cancellationToken);

    /// <summary>Forgets an automation for the machine once it took away what it held.</summary>
    internal Task<AutomationListDto> DeleteAsync(string automationId, string machineId, CancellationToken cancellationToken)
        => _client.DeleteAutomationAsync(automationId, machineId, cancellationToken);

    /// <summary>Lists the automations of this computer's person as the server holds them.</summary>
    internal Task<AutomationListDto> ListAsync(CancellationToken cancellationToken) => _client.ListAutomationsAsync(cancellationToken);

    /// <summary>Lists the runs of an automation as its machines reported them.</summary>
    internal Task<AutomationRunListDto> ListRunsAsync(string automationId, int limit, CancellationToken cancellationToken)
        => _client.ListAutomationRunsAsync(automationId, limit, cancellationToken);

    /// <inheritdoc />
    /// <summary>Makes a hook of the server for the machine: the address a tool pushes its signed events to.</summary>
    /// <returns>The hook and its full address.</returns>
    internal async Task<(HookDto Hook, Uri Address)> CreateHookAsync(string machineId, string source, string? label, CancellationToken cancellationToken)
    {
        var hook = await _client.CreateHookAsync(machineId, source, label, cancellationToken).ConfigureAwait(false);
        return (hook, new Uri(_client.ServerAddress, hook.Path));
    }

    /// <summary>Reads the address of a hook of this computer's person on the server, for its machine.</summary>
    /// <returns>The address; null when no hook of the person has this id for this machine.</returns>
    internal async Task<Uri?> HookAddressAsync(string machineId, string hookId, CancellationToken cancellationToken)
    {
        var hooks = await _client.ListHooksAsync(cancellationToken).ConfigureAwait(false);
        return hooks.Hooks.FirstOrDefault(hook => hook.Id == hookId && hook.MachineId == machineId) is { } found ? new Uri(_client.ServerAddress, found.Path) : null;
    }

    public void Dispose() => _client.Dispose();
}
