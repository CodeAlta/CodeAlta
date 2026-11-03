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

    /// <summary>Seals what a version does for its machine and the controllers of this computer's person, as the device list of the server verifies them now.</summary>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="ArgumentException">The machine is not in the device list.</exception>
    internal async Task<string> SealAsync(FleetAutomationContent content, string automationId, long number, string machineId, CancellationToken cancellationToken)
    {
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return FleetAutomationSigning.Seal(content, automationId, number, machineId, list, _identity);
    }

    /// <summary>Gives a signed version to the server.</summary>
    internal Task<AutomationDto> PutAsync(FleetSignedAutomationVersion signed, CancellationToken cancellationToken) => _client.PutAutomationAsync(signed, cancellationToken);

    /// <summary>Asks the machine, through the server, to run an automation now.</summary>
    internal Task<AutomationRunDto> RunAsync(FleetSignedAutomationRunRequest signed, CancellationToken cancellationToken) => _client.RunAutomationAsync(signed, cancellationToken);

    /// <summary>Forgets an automation for the machine once it took away what it held.</summary>
    internal Task<AutomationListDto> DeleteAsync(string automationId, string machineId, CancellationToken cancellationToken)
        => _client.DeleteAutomationAsync(automationId, machineId, cancellationToken);

    /// <summary>Lists the automations of this computer's person as the server holds them.</summary>
    internal Task<AutomationListDto> ListAsync(CancellationToken cancellationToken) => _client.ListAutomationsAsync(cancellationToken);

    /// <summary>Lists the runs of an automation as its machines reported them.</summary>
    internal Task<AutomationRunListDto> ListRunsAsync(string automationId, int limit, CancellationToken cancellationToken)
        => _client.ListAutomationRunsAsync(automationId, limit, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
