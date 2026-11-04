using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

// The hooks of the REST API (doc/fleet/protocol.md): the addresses tools push their signed events to, for a machine of
// this device's person.
public sealed partial class FleetServerClient
{
    /// <summary>Makes a hook for one of this device's person's machines.</summary>
    /// <param name="machineId">The machine its events go to.</param>
    /// <param name="source">What pushes to it: <see cref="HookSources.IncidentIo"/> or <see cref="HookSources.Webhook"/>.</param>
    /// <param name="label">Its name; null for the name of its source.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hook, with the path of its address on the server.</returns>
    /// <exception cref="ArgumentException">The machine or the source is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it: the machine is not this person's, or the source is not known.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<HookDto> CreateHookAsync(string machineId, string source, string? label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentException.ThrowIfNullOrEmpty(source);
        return SendAsync(HttpMethod.Post, "api/v1/hooks", Body(new CreateHookRequest(machineId, source, label), FleetHookApiJsonContext.Default.CreateHookRequest),
            FleetHookApiJsonContext.Default.HookDto, cancellationToken);
    }

    /// <summary>Lists the hooks of this device's person.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hooks.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<HookListDto> ListHooksAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/hooks", null, FleetHookApiJsonContext.Default.HookListDto, cancellationToken);

    /// <summary>Deletes a hook: its address answers nothing any more.</summary>
    /// <param name="hookId">The hook.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hooks left.</returns>
    /// <exception cref="ArgumentException"><paramref name="hookId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it: no hook of this person has this id.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<HookListDto> DeleteHookAsync(string hookId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(hookId);
        return SendAsync(HttpMethod.Delete, "api/v1/hooks/" + Uri.EscapeDataString(hookId), null, FleetHookApiJsonContext.Default.HookListDto, cancellationToken);
    }

    /// <summary>Lists the latest events of a hook, as facts, the newest first.</summary>
    /// <param name="hookId">The hook.</param>
    /// <param name="limit">The most events; null for the server's default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events.</returns>
    /// <exception cref="ArgumentException"><paramref name="hookId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<HookEventListDto> ListHookEventsAsync(string hookId, int? limit = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(hookId);
        var query = limit is { } most ? "?limit=" + most.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        return SendAsync(HttpMethod.Get, $"api/v1/hooks/{Uri.EscapeDataString(hookId)}/events{query}", null, FleetHookApiJsonContext.Default.HookEventListDto, cancellationToken);
    }
}
