using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Control;

/// <summary>
/// The control connection of devices (<c>wss://&lt;server&gt;/fleet/v1</c>): the handshake proves the device key against
/// the device table, then presence, publications and the directory flow over one <see cref="FleetConnection"/>.
/// </summary>
public sealed class ControlService
{
    /// <summary>The versions of the fleet protocol the server speaks.</summary>
    public static readonly FleetProtocolRange Protocol = new(1, 1);

    /// <summary>The features the server announces in its welcome.</summary>
    public static readonly IReadOnlyList<string> Features = ["presence", "inventory", "facts", "facts.withdraw", "directory", "relay", "telemetry"];

    private const int MaxStateLength = 32;

    private readonly ICoordinatorStore _store;
    private readonly PresenceRegistry _presence;
    private readonly DirectoryHub _directory;
    private readonly PublicationService _publications;
    private readonly RelayHub? _relays;
    private readonly TimeProvider _time;
    private readonly FleetConnectionPolicy _policy;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ControlService"/> class.</summary>
    /// <param name="store">The store.</param>
    /// <param name="presence">The devices online.</param>
    /// <param name="directory">The watchers of the directory.</param>
    /// <param name="publications">What machines publish.</param>
    /// <param name="time">The clock.</param>
    /// <param name="policy">The limits of the connections.</param>
    /// <param name="logger">The log.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ControlService(ICoordinatorStore store, PresenceRegistry presence, DirectoryHub directory, PublicationService publications, TimeProvider time,
        FleetConnectionPolicy policy, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(publications);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _presence = presence;
        _directory = directory;
        _publications = publications;
        _time = time;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>Initializes a new instance of the <see cref="ControlService"/> class, with the relay of session channels.</summary>
    /// <param name="store">The store.</param>
    /// <param name="presence">The devices online.</param>
    /// <param name="directory">The watchers of the directory.</param>
    /// <param name="publications">What machines publish.</param>
    /// <param name="relays">The relay of session channels.</param>
    /// <param name="time">The clock.</param>
    /// <param name="policy">The limits of the connections.</param>
    /// <param name="logger">The log.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ControlService(ICoordinatorStore store, PresenceRegistry presence, DirectoryHub directory, PublicationService publications, RelayHub relays, TimeProvider time,
        FleetConnectionPolicy policy, ILogger logger)
        : this(store, presence, directory, publications, time, policy, logger)
    {
        ArgumentNullException.ThrowIfNull(relays);
        _relays = relays;
    }

    /// <summary>Gets the instruments of the control connections; null for none.</summary>
    public Telemetry.ServerMetrics? Metrics { get; init; }

    /// <summary>Gets what sends the fleet's telemetry setting to a device that connects; null for none.</summary>
    internal TelemetrySettingsService? TelemetrySettings { get; init; }

    /// <summary>Serves a WebSocket request on <see cref="FleetServerMethods.ControlPath"/>.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the connection ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("The control connection of the fleet is a WebSocket.").ConfigureAwait(false);
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await using var transport = new FleetWebSocketTransport(socket);
        await ServeAsync(transport, context.Connection.RemoteIpAddress, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Runs the handshake and the connection on a transport, until the connection ends.</summary>
    /// <param name="transport">The transport; the caller disposes it.</param>
    /// <param name="cancellationToken">Ends the connection.</param>
    /// <returns>A task that completes when the connection ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is <see langword="null"/>.</exception>
    public Task ServeAsync(IFleetTransport transport, CancellationToken cancellationToken) => ServeAsync(transport, null, cancellationToken);

    /// <summary>Runs the handshake and the connection on a transport, until the connection ends.</summary>
    /// <param name="transport">The transport; the caller disposes it.</param>
    /// <param name="remoteAddress">The address the connection comes from, for the audit log; null when unknown.</param>
    /// <param name="cancellationToken">Ends the connection.</param>
    /// <returns>A task that completes when the connection ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is <see langword="null"/>.</exception>
    public async Task ServeAsync(IFleetTransport transport, System.Net.IPAddress? remoteAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var verifier = new DeviceVerifier(_store);
        FleetAcceptedClient accepted;
        try
        {
            accepted = await FleetHandshake.AcceptAsync(transport, new FleetHandshakeServerOptions
            {
                Protocol = Protocol,
                Policy = _policy,
                Features = Features,
                TimeProvider = _time,
                ResolveCursorsAsync = ResolveCursorsAsync,
            }, verifier, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetHandshakeException or TimeoutException or IOException or OperationCanceledException
            or System.Net.WebSockets.WebSocketException)
        {
            _logger.LogDebug("A control connection was refused: {Reason}", exception.Message);
            Metrics?.Handshake(exception is FleetHandshakeException refused ? refused.Error.Code : FleetErrorCodes.TransportError);
            return;
        }

        Metrics?.Handshake(null);

        var session = new ControlSession(verifier.Device!, new FleetConnection(transport, FleetConnectionOptions.FromPolicy(accepted.Welcome.Policy)), _time.GetUtcNow())
        {
            RemoteAddress = remoteAddress,
        };
        await using var connection = session.Connection;
        Register(session);
        // Online before the first request is read: an answer to it implies the presence.
        var replaced = _presence.Add(session);
        connection.Start();
        if (replaced is not null)
        {
            _ = replaced.CloseAsync(new FleetError(FleetErrorCodes.ConnectionClosed, null, "Replaced by a newer connection of the same device."));
        }

        _directory.Publish(session.Device.OrganizationId, PresenceChange(session.GetPresence()));
        _logger.LogInformation("Device {Device} ({Kind}) connected", session.Device.Id, session.Device.Kind);
        Metrics?.Connection(session.Device.Kind, 1);
        // The fleet's telemetry setting, signed by a controller device: the device checks it before it applies it.
        if (TelemetrySettings is { } telemetry) _ = telemetry.SendCurrentAsync(session);
        try
        {
            using var registration = cancellationToken.Register(static state =>
                _ = ((ControlSession)state!).CloseAsync(new FleetError(FleetErrorCodes.ConnectionClosed, FleetErrorHints.Reconnect, "The server stops.")), session);
            var closed = await connection.Completion.ConfigureAwait(false);
            _logger.LogInformation("Device {Device} disconnected: {Code} {Message}", session.Device.Id, closed.Code, closed.Message);
        }
        finally
        {
            Metrics?.Connection(session.Device.Kind, -1);
            _directory.Unwatch(session);
            if (_presence.Remove(session))
            {
                _directory.Publish(session.Device.OrganizationId, PresenceChange(new FleetPresence(session.Device.Id, false, null, null, _time.GetUtcNow())));
            }
        }
    }

    private async ValueTask<JsonElement?> ResolveCursorsAsync(FleetHello hello, CancellationToken cancellationToken)
    {
        var cursors = await _store.GetPublicationCursorsAsync(hello.Device.Id, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(cursors, FleetServerJsonContext.Default.FleetPublicationCursors);
    }

    private void Register(ControlSession session)
    {
        var connection = session.Connection;
        void On(string method, FleetRequestHandler handler) => connection.RegisterRequestHandler(method, Traced(session, handler));
        On(FleetServerMethods.PresenceUpdate, Handler(FleetServerJsonContext.Default.FleetPresenceUpdate, (update, _) =>
        {
            if (update is null || string.IsNullOrEmpty(update.State) || update.State.Length > MaxStateLength || update.ActiveSessions < 0)
            {
                throw CoordinatorException.BadRequest(CoordinatorErrorCodes.PublicationInvalid, $"presence.update needs a state of at most {MaxStateLength} characters.");
            }

            if (session.Update(update)) _directory.Publish(session.Device.OrganizationId, PresenceChange(session.GetPresence()));
            return Task.FromResult<JsonElement?>(null);
        }));

        On(FleetServerMethods.InventoryPublish, Handler(FleetServerJsonContext.Default.FleetInventoryPublish, async (publication, token) =>
        {
            var ack = await _publications.PublishInventoryAsync(session.Device, publication, token).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(ack, FleetServerJsonContext.Default.FleetPublishAck);
        }));

        On(FleetServerMethods.FactsPublish, Handler(FleetServerJsonContext.Default.FleetFactsPublish, async (publication, token) =>
        {
            var result = await _publications.PublishFactsAsync(session.Device, publication, token).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, FleetServerJsonContext.Default.FleetFactsPublishResult);
        }));

        On(FleetServerMethods.FactsWithdraw, Handler(FleetServerJsonContext.Default.FleetFactsWithdraw, async (withdrawal, token) =>
        {
            var result = await _publications.WithdrawFactsAsync(session.Device, withdrawal, token).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, FleetServerJsonContext.Default.FleetFactsWithdrawResult);
        }));

        On(FleetServerMethods.DirectoryWatch, async (_, token) =>
        {
            // Watch first, then read: a change between the two comes as an event, and versions make it idempotent.
            _directory.Watch(session);
            var organization = session.Device.OrganizationId;
            var devices = (await _store.ListDevicesAsync(organization, token).ConfigureAwait(false)).ToDictionary(static device => device.Id, StringComparer.Ordinal);
            var machines = (await _store.ListMachinesAsync(organization, token).ConfigureAwait(false))
                .Select(machine => machine.ToDirectory(devices.TryGetValue(machine.DeviceId, out var device) ? device.Kind : FleetDeviceKinds.Node))
                .ToArray();
            var sessions = (await _store.ListSessionsAsync(organization, token).ConfigureAwait(false)).Select(static record => record.ToDirectory()).ToArray();
            var presence = _presence.List(organization).Select(static online => online.GetPresence()).ToArray();
            return JsonSerializer.SerializeToElement(new FleetDirectorySnapshot(machines, sessions, presence), FleetServerJsonContext.Default.FleetDirectorySnapshot);
        });

        if (_relays is not { } relays) return;
        On(FleetServerMethods.RelayOpen, Handler(FleetServerJsonContext.Default.FleetRelayOpen, async (open, token) =>
        {
            var ticket = await relays.OpenAsync(session, open, token).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(ticket, FleetServerJsonContext.Default.FleetRelayTicket);
        }));

        On(FleetServerMethods.RelayAccept, Handler(FleetServerJsonContext.Default.FleetRelayRef, (accept, _) =>
            Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(relays.Accept(session.Device, accept), FleetServerJsonContext.Default.FleetRelayTicket))));

        On(FleetServerMethods.RelayClose, Handler(FleetServerJsonContext.Default.FleetRelayClose, async (close, _) =>
        {
            await relays.CloseAsync(session.Device, close).ConfigureAwait(false);
            return null;
        }));
    }

    // A span per request: the method, the device in short and the outcome; never the parameters.
    private FleetRequestHandler Traced(ControlSession session, FleetRequestHandler handler)
        => async (context, cancellationToken) =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var activity = Metrics?.StartControlRequest(context.Method, session.Device.Id);
            string? failure = null;
            try
            {
                return await handler(context, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetException exception)
            {
                failure = exception.Error.Code;
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failure = FleetErrorCodes.InternalError;
                throw;
            }
            finally
            {
                Metrics?.EndControlRequest(activity, context.Method, started, failure);
            }
        };

    private static FleetRequestHandler Handler<T>(JsonTypeInfo<T> type, Func<T?, CancellationToken, Task<JsonElement?>> handle)
        => async (context, cancellationToken) =>
        {
            T? parameters;
            try
            {
                parameters = context.Params is { } element ? element.Deserialize(type) : default;
            }
            catch (JsonException)
            {
                throw new FleetException(FleetErrorCodes.InvalidParams, null, $"The parameters of {context.Method} do not read.");
            }

            try
            {
                return await handle(parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (CoordinatorException exception)
            {
                throw new FleetException(exception.ToFleetError());
            }
        };

    private static FleetDirectoryChange PresenceChange(FleetPresence presence)
        => new(FleetServerMethods.ChangeKinds.Presence, presence.DeviceId, Presence: presence);

    /// <summary>Checks the device proof of a hello against the device table.</summary>
    private sealed class DeviceVerifier(ICoordinatorStore store) : IFleetHandshakeVerifier
    {
        public DeviceRecord? Device { get; private set; }

        public async ValueTask<FleetProofVerification> VerifyAsync(FleetHello hello, FleetProofContext context, CancellationToken cancellationToken)
        {
            var device = await store.GetDeviceAsync(context.DeviceId, cancellationToken).ConfigureAwait(false);
            // The proof is checked before the status, so that a pending or revoked answer tells only the holder of the key.
            if (device is null || !FleetDevicePublicKey.TryParse(device.PublicKey, out var key) || !key.Verify(context.GetSignedBytes(), hello.Device.Proof))
            {
                return FleetProofVerification.Reject(new FleetError(FleetErrorCodes.Unauthenticated, FleetErrorHints.Reauthenticate,
                    "The device proof does not verify with a device of this server."));
            }

            switch (device.Status)
            {
                case DeviceStatuses.Pending:
                    return FleetProofVerification.Reject(new FleetError(FleetErrorCodes.PendingApproval, FleetErrorHints.WaitForApproval,
                        "The device waits for its approval by a controller device."));
                case DeviceStatuses.Revoked:
                    return FleetProofVerification.Reject(new FleetError(FleetErrorCodes.Revoked, null, "The device was revoked."));
            }

            Device = device;
            return FleetProofVerification.Accept();
        }
    }
}
