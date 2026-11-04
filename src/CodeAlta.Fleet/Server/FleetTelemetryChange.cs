using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// What an owner sets for the telemetry of the fleet, before <c>FleetServerClient.SetTelemetryAsync</c> numbers it,
/// names the organization and this device, and signs it (<see cref="FleetTelemetrySettings"/>).
/// </summary>
/// <param name="Enabled">Whether the machines send at all.</param>
/// <param name="Endpoint">The base address of the collector; null when disabled.</param>
/// <param name="Protocol">Its protocol (<see cref="FleetTelemetryProtocols"/>).</param>
/// <param name="Route">How the machines reach it (<see cref="FleetTelemetryRoutes"/>).</param>
/// <param name="Signals">The signals sent (<see cref="FleetTelemetrySignals"/>).</param>
/// <param name="Headers">The headers of a direct route; null for none, and for the server route.</param>
/// <param name="Environment">The <c>deployment.environment.name</c> of the machines, or null.</param>
public sealed record FleetTelemetryChange(bool Enabled, string? Endpoint, string Protocol, string Route, IReadOnlyList<string> Signals,
    IReadOnlyDictionary<string, string>? Headers, string? Environment);
