using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

// The automation resources of the REST API (doc/fleet/protocol.md), for a controller device of the person who gives them.
public sealed partial class FleetServerClient
{
    /// <summary>Lists the automations of this device's person, per machine, as the server holds them.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The automations.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AutomationListDto> ListAutomationsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/automations", null, FleetAutomationApiJsonContext.Default.AutomationListDto, cancellationToken);

    /// <summary>
    /// Stores a version of an automation this device signed for one of its person's machines (or a removal); the server
    /// delivers it to the machine, which verifies the signature before it takes it.
    /// </summary>
    /// <param name="signed">The version, signed and sealed (<see cref="FleetAutomationSigning"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version the server holds now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signed"/> is null.</exception>
    /// <exception cref="ArgumentException">The version does not read.</exception>
    /// <exception cref="FleetServerException">The server refused it: not newer, not of this device, or for a machine of another person.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AutomationDto> PutAutomationAsync(FleetSignedAutomationVersion signed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signed);
        if (!FleetAutomationVersionDocuments.TryParse(signed.Payload, out var version)) throw new ArgumentException("The version does not read.", nameof(signed));
        return SendAsync(HttpMethod.Put, "api/v1/automations/" + Uri.EscapeDataString(version.AutomationId),
            Body(new PutAutomationRequest(signed), FleetAutomationApiJsonContext.Default.PutAutomationRequest), FleetAutomationApiJsonContext.Default.AutomationDto, cancellationToken);
    }

    /// <summary>Forgets an automation for a machine, once the machine took away what it held after a signed removal.</summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The automations left.</returns>
    /// <exception cref="ArgumentException">An id is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it: the machine has not taken it away yet.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AutomationListDto> DeleteAutomationAsync(string automationId, string machineId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        return SendAsync(HttpMethod.Delete, $"api/v1/automations/{Uri.EscapeDataString(automationId)}?machine={Uri.EscapeDataString(machineId)}", null,
            FleetAutomationApiJsonContext.Default.AutomationListDto, cancellationToken);
    }

    /// <summary>Lists the runs of an automation as its machines reported them, the newest first.</summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="limit">The most runs; null for the server's default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The runs.</returns>
    /// <exception cref="ArgumentException"><paramref name="automationId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<AutomationRunListDto> ListAutomationRunsAsync(string automationId, int? limit = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        var query = limit is { } most ? "?limit=" + most.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        return SendAsync(HttpMethod.Get, $"api/v1/automations/{Uri.EscapeDataString(automationId)}/runs{query}", null,
            FleetAutomationApiJsonContext.Default.AutomationRunListDto, cancellationToken);
    }
}
