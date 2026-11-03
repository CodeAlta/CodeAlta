using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.Fleet.Server;

// The automations a server gives this machine: the versions it delivers, the claims of their due times and the reports of their runs.
public sealed partial class FleetConnectorOptions
{
    /// <summary>
    /// Gets what receives the automation versions the server delivers (<see cref="FleetAutomationMethods.Assigned"/>), as
    /// signed: it verifies each before it takes it, and answers with <see cref="FleetConnector.AcknowledgeAutomationsAsync"/>.
    /// It is given one delivery at a time, in the order the server sent them. Null ignores them (a desktop does).
    /// </summary>
    public Func<FleetAutomationDelivery, CancellationToken, Task>? AutomationsAssigned { get; init; }

    /// <summary>
    /// Gets what receives a run asked by hand through the server (<see cref="FleetAutomationMethods.Run"/>), as signed: it
    /// verifies the request before it runs anything. Null ignores it.
    /// </summary>
    public Func<FleetSignedAutomationRunRequest, CancellationToken, Task>? AutomationsRun { get; init; }

    /// <summary>Gets what is called each time the connection is online, after the deliveries of the welcome; null for nothing.</summary>
    public Func<CancellationToken, Task>? AutomationsOnline { get; init; }
}

public sealed partial class FleetConnector
{
    private Task _automationTail = Task.CompletedTask;

    /// <summary>Claims a due time of an automation of this server for a run, when connected.</summary>
    /// <param name="request">The claim.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The answer of the server; null while offline or when it did not answer (the machine then decides alone).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async Task<FleetAutomationClaimResult?> ClaimAutomationAsync(FleetAutomationClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _connection) is not { State: FleetConnectionState.Open } connection) return null;
        try
        {
            return await connection.SendRequestAsync(FleetAutomationMethods.Claim, request, FleetAutomationServerJsonContext.Default.FleetAutomationClaimRequest,
                FleetAutomationServerJsonContext.Default.FleetAutomationClaimResult, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOffline(exception))
        {
            return null;
        }
    }

    /// <summary>Tells the server what this machine did with delivered versions, when connected.</summary>
    /// <param name="acks">The answers.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the server received them; false while offline (it delivers them again on the next connection).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="acks"/> is null.</exception>
    public Task<bool> AcknowledgeAutomationsAsync(FleetAutomationAcks acks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acks);
        return SendAutomationRequestAsync(FleetAutomationMethods.Ack, JsonSerializer.SerializeToElement(acks, FleetAutomationServerJsonContext.Default.FleetAutomationAcks),
            cancellationToken);
    }

    /// <summary>Reports runs of the automations of this server, when connected.</summary>
    /// <param name="facts">The runs.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the server received them; false while offline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facts"/> is null.</exception>
    public Task<bool> ReportAutomationRunsAsync(FleetAutomationRunFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return SendAutomationRequestAsync(FleetAutomationMethods.Runs, JsonSerializer.SerializeToElement(facts, FleetAutomationServerJsonContext.Default.FleetAutomationRunFacts),
            cancellationToken);
    }

    private async Task<bool> SendAutomationRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _connection) is not { State: FleetConnectionState.Open } connection) return false;
        try
        {
            await connection.SendRequestAsync(method, parameters, null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsOffline(exception))
        {
            return false;
        }
    }

    // On the reader of the connection: read here, taken beside it, one delivery at a time (the check reads the device list from the server).
    private void OnAutomationsAssigned(FleetEvent fleetEvent, CancellationToken stop)
    {
        if (_options.AutomationsAssigned is not { } receive) return;
        FleetAutomationDelivery? delivery = null;
        try { delivery = fleetEvent.Payload?.Deserialize(FleetAutomationServerJsonContext.Default.FleetAutomationDelivery); }
        catch (JsonException) { /* A delivery this machine does not read is not taken: the server sends it again on the next connection. */ }
        if (delivery is { Versions.Count: > 0 }) QueueAutomations(() => receive(delivery, stop), stop);
    }

    private void OnAutomationRun(FleetEvent fleetEvent, CancellationToken stop)
    {
        if (_options.AutomationsRun is not { } run) return;
        FleetSignedAutomationRunRequest? request = null;
        try { request = fleetEvent.Payload?.Deserialize(FleetAutomationRunRequestJsonContext.Default.FleetSignedAutomationRunRequest); }
        catch (JsonException) { /* A request this machine does not read runs nothing. */ }
        if (request is { Payload.Length: > 0 }) QueueAutomations(() => run(request, stop), stop);
    }

    private void OnAutomationsOnline(CancellationToken stop)
    {
        if (_options.AutomationsOnline is { } online) QueueAutomations(() => online(stop), stop);
    }

    private void QueueAutomations(Func<Task> receive, CancellationToken stop)
    {
        Task next;
        lock (_gate)
        {
            var previous = _automationTail;
            _automationTail = next = Task.Run(async () =>
            {
                await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                try
                {
                    await receive().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or HttpRequestException or FleetServerException
                    or FleetDeviceListException or UnauthorizedAccessException or OperationCanceledException or FleetException)
                {
                    if (LogManager.IsInitialized)
                        _logger.Warn($"The automations of {FleetServerAddress.Format(_options.ServerAddress)} were not taken: {exception.Message}");
                }
            }, CancellationToken.None);
        }

        Track(next);
    }

    private static bool IsOffline(Exception exception)
        => exception is FleetException or FleetConnectionClosedException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException
            or InvalidOperationException or ObjectDisposedException;
}
