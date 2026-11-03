using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using XenoAtom.Logging;

namespace CodeAlta.Node;

/// <summary>
/// Takes the automations the servers of a node give it (<see cref="FleetAutomationMethods.Assigned"/>): each version is
/// verified against the device list of that server as the node anchored it, read again (the one last verified when the
/// server does not answer), opened with the key of this node, and taken by <see cref="NodeAutomations"/> only when it is
/// newer than the one held; the server is told what was taken and why the rest was refused. A server alone cannot make
/// the node run anything: it holds no key that signs a version, nor one that opens it.
/// </summary>
/// <param name="automations">The automations of the node.</param>
/// <param name="identity">The key of this node, which opens what a version does.</param>
internal sealed class NodeServerAutomations(NodeAutomations automations, DeviceIdentity identity) : IFleetServerAutomations
{
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Node.Automations");

    /// <inheritdoc />
    public async Task AcceptAsync(FleetConnector connector, FleetServerLink link, FleetAutomationDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(delivery);
        FleetDeviceListState? devices;
        try
        {
            devices = await connector.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            devices = connector.Devices.Current;
        }
        catch (FleetDeviceListException exception)
        {
            // A list that does not verify is no ground to take anything from this server: nothing is answered, nothing taken.
            Log($"The automations of {link.Url} were refused: the device list of the server does not verify ({exception.Reason}).");
            return;
        }

        if (devices is null)
        {
            Log($"The automations of {link.Url} wait: the device list of the server could not be read.");
            return;
        }

        var organization = link.OrganizationId ?? devices.OrganizationId;
        var acks = new List<FleetAutomationAck>(delivery.Versions.Count);
        foreach (var signed in delivery.Versions)
        {
            // A version that does not read names nothing to answer: the server keeps it pending, and nothing runs.
            if (signed is null || !FleetAutomationVersionDocuments.TryParse(signed.Payload, out var named)) continue;
            var outcome = await automations.AcceptFromServerAsync(link.Id, devices, organization, signed, version => Open(version, devices), cancellationToken)
                .ConfigureAwait(false);
            acks.Add(new FleetAutomationAck(named.AutomationId, named.Number, outcome.Applied, outcome.Message));
        }

        if (acks.Count > 0) await connector.AcknowledgeAutomationsAsync(new FleetAutomationAcks(acks), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RunAsync(FleetConnector connector, FleetServerLink link, FleetSignedAutomationRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(request);
        FleetDeviceListState? devices;
        try
        {
            devices = await connector.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            devices = connector.Devices.Current;
        }
        catch (FleetDeviceListException exception)
        {
            Log($"A run asked through {link.Url} was refused: the device list of the server does not verify ({exception.Reason}).");
            return;
        }

        if (devices is null)
        {
            Log($"A run asked through {link.Url} was refused: the device list of the server could not be read.");
            return;
        }

        // A refused request is logged by the automations; the run of one that holds is reported as the others.
        await automations.RunFromServerAsync(link.Id, devices, link.OrganizationId ?? devices.OrganizationId, request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task OnlineAsync(FleetConnector connector, FleetServerLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        return automations.ReportRunsAsync(link.Id, all: true, cancellationToken);
    }

    private (FleetAutomationContent? Content, string? Reason) Open(FleetAutomationVersion version, FleetDeviceListState devices)
        => FleetAutomationSigning.TryOpen(version.Sealed, version.AutomationId, version.Number, version.MachineId, identity, devices, out var content, out var reason)
            ? (content, null)
            : (null, reason);

    private void Log(string message)
    {
        if (LogManager.IsInitialized) _logger.Warn(message);
    }
}
