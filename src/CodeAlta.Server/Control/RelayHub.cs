using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Control;

/// <summary>The limits of the relayed session channels of a server.</summary>
public sealed class RelayOptions
{
    /// <summary>Gets the most bytes one relay carries, both directions together; past it, the relay is closed.</summary>
    public long MaxBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Gets how long a relay may carry nothing before it is closed. The channel inside sends a heartbeat every 25 seconds.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets how many relays one device may have at once, offered or open, as viewer or as target.</summary>
    public int MaxRelaysPerDevice { get; init; } = 16;

    /// <summary>Gets how long a ticket works, and how long a side waits for the other one.</summary>
    public TimeSpan TicketLifetime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long the target has to accept a relay.</summary>
    public TimeSpan AcceptTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Who may reach whom through the relay. This step allows two approved devices of the same organization; the grants of
/// <c>doc/fleet/access-control.md</c> come in its place later, without a change of the relay.
/// </summary>
public interface IRelayAccessPolicy
{
    /// <summary>Checks that a viewer may ask a relay to a target.</summary>
    /// <param name="viewer">The device that asks, as the store holds it now.</param>
    /// <param name="target">The device asked for, as the store holds it now.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The refusal, or <see langword="null"/> when the viewer may.</returns>
    ValueTask<CoordinatorException?> CheckAsync(DeviceRecord viewer, DeviceRecord target, CancellationToken cancellationToken);
}

/// <summary>The access of this step: two approved devices of the same organization reach each other.</summary>
public sealed class OrganizationRelayAccess : IRelayAccessPolicy
{
    /// <inheritdoc />
    public ValueTask<CoordinatorException?> CheckAsync(DeviceRecord viewer, DeviceRecord target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(target);
        CoordinatorException? refusal = null;
        if (!string.Equals(viewer.OrganizationId, target.OrganizationId, StringComparison.Ordinal))
            refusal = CoordinatorException.NotFound(CoordinatorErrorCodes.DeviceNotFound, "No device has this id.");
        else if (viewer.Status != DeviceStatuses.Approved)
            refusal = CoordinatorException.Forbidden(viewer.Status == DeviceStatuses.Revoked ? FleetErrorCodes.Revoked : FleetErrorCodes.Forbidden, "This device may not open relays.");
        else if (target.Status != DeviceStatuses.Approved)
            refusal = CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "The device asked for is not an approved member of the organization.");
        return ValueTask.FromResult(refusal);
    }
}

/// <summary>
/// The relay of session channels (<c>doc/fleet/protocol.md</c>, Connections): a viewer asks <c>relay.open</c> on its
/// control connection, the server checks that it may reach the target and asks the target with <c>relay.incoming</c>;
/// once the target answers <c>relay.accept</c>, each side opens <see cref="FleetServerMethods.RelayPath"/> with its
/// one-time ticket and a signed request, and the server splices the two WebSockets.
/// </summary>
/// <remarks>
/// The bytes are TLS between the two devices, with the target's key pinned by the viewer: the server copies frames from
/// one socket to the other without reading them, counts them, and closes a relay that exceeds its limits. The audit log
/// records who reached whom, when and how many bytes, never content.
/// </remarks>
public sealed class RelayHub
{
    private const int BufferBytes = 64 * 1024;
    private const int MaxIdLength = 64;
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private readonly ICoordinatorStore _store;
    private readonly PresenceRegistry _presence;
    private readonly RequestAuthenticator _authenticator;
    private readonly IRelayAccessPolicy _access;
    private readonly RelayOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Relay> _relays = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Side> _tickets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Initializes a new instance of the <see cref="RelayHub"/> class.</summary>
    /// <param name="store">The store: the devices, and the audit log.</param>
    /// <param name="presence">The devices online.</param>
    /// <param name="authenticator">Checks the signed request of each side.</param>
    /// <param name="access">Who may reach whom.</param>
    /// <param name="options">The limits.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The log.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public RelayHub(ICoordinatorStore store, PresenceRegistry presence, RequestAuthenticator authenticator, IRelayAccessPolicy access, RelayOptions options,
        TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _presence = presence;
        _authenticator = authenticator;
        _access = access;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets the instruments of the relays; null for none.</summary>
    public Telemetry.ServerMetrics? Metrics { get; init; }

    /// <summary>
    /// Gets what ends every relay when the server stops (the host's <c>ApplicationStopping</c>): both sides are cut at
    /// once, not when the host's shutdown timeout aborts the requests; none by default.
    /// </summary>
    public CancellationToken Stopping { get; init; }

    /// <summary>Gets how many relays a device has now, offered or open.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>The count.</returns>
    public int Count(string deviceId)
    {
        lock (_gate) return CountLocked(deviceId);
    }

    /// <summary>Opens a relay for a viewer: checks the access, asks the target, and gives the viewer its ticket once the target accepted.</summary>
    /// <param name="viewer">The control connection of the viewer.</param>
    /// <param name="request">The target.</param>
    /// <param name="cancellationToken">Cancels the wait for the target.</param>
    /// <returns>The ticket of the viewer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewer"/> is <see langword="null"/>.</exception>
    /// <exception cref="CoordinatorException">The relay is refused: no such device, not allowed, offline, too many relays, declined.</exception>
    public async Task<FleetRelayTicket> OpenAsync(ControlSession viewer, FleetRelayOpen? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var origin = new RequestOrigin(viewer.RemoteAddress, Guid.CreateVersion7().ToString("N"));
        var actor = viewer.Device;
        string? target = request?.Target;
        try
        {
            if (string.IsNullOrEmpty(target) || target.Length > MaxIdLength)
                throw CoordinatorException.BadRequest(FleetErrorCodes.InvalidParams, "relay.open needs the id of the device to reach.");
            if (string.Equals(target, actor.Id, StringComparison.Ordinal))
                throw CoordinatorException.BadRequest(FleetErrorCodes.InvalidParams, "A device does not relay to itself.");
            // The records as they are now: a revocation since the connection opened applies.
            actor = await _store.GetDeviceAsync(actor.Id, cancellationToken).ConfigureAwait(false) ?? actor with { Status = DeviceStatuses.Revoked };
            var targetDevice = await _store.GetDeviceAsync(target, cancellationToken).ConfigureAwait(false);
            if (targetDevice is null || !string.Equals(targetDevice.OrganizationId, actor.OrganizationId, StringComparison.Ordinal))
                throw CoordinatorException.NotFound(CoordinatorErrorCodes.DeviceNotFound, "No device has this id.");
            if (await _access.CheckAsync(actor, targetDevice, cancellationToken).ConfigureAwait(false) is { } refusal) throw refusal;
            var targetSession = _presence.Get(targetDevice.Id)
                ?? throw new CoordinatorException(StatusCodes.Status409Conflict, FleetRelayErrorCodes.TargetOffline, "The device asked for is not online.", FleetErrorHints.Retry);

            var relay = Reserve(actor, targetDevice, origin);
            try
            {
                var incoming = new FleetRelayIncoming(relay.Id, actor.Id, _time.GetUtcNow() + _options.AcceptTimeout);
                using (var send = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    send.CancelAfter(_options.AcceptTimeout);
                    await targetSession.Connection.SendEventAsync(new FleetEvent(FleetServerMethods.RelayIncoming,
                        JsonSerializer.SerializeToElement(incoming, FleetServerJsonContext.Default.FleetRelayIncoming)), send.Token).ConfigureAwait(false);
                }

                bool accepted;
                try
                {
                    accepted = await relay.Answer.Task.WaitAsync(_options.AcceptTimeout, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    accepted = false;
                }

                if (!accepted) throw CoordinatorException.Forbidden(FleetRelayErrorCodes.Declined, "The device asked for did not accept the relay.", FleetErrorHints.Retry);
                var ticket = Issue(relay, viewerSide: true);
                _ = AbandonLaterAsync(relay);
                return ticket;
            }
            catch
            {
                relay.Answer.TrySetResult(false);
                await FinishAsync(relay, Ends.Refused, audit: false).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception exception) when (exception is CoordinatorException or FleetException or FleetConnectionClosedException or InvalidOperationException
            or OperationCanceledException)
        {
            var code = exception switch
            {
                CoordinatorException refused => refused.Code,
                OperationCanceledException => FleetErrorCodes.Cancelled,
                _ => FleetRelayErrorCodes.TargetOffline,
            };
            Metrics?.RelayRefused(code);
            // A refused relay leaves a trace too: who tried to reach whom.
            await _store.AppendAuditAsync(origin.Audit("relay.open", actor, target, code), CancellationToken.None).ConfigureAwait(false);
            // The control connection of the target closed under the offer: it is offline as far as the viewer can tell.
            if (exception is FleetConnectionClosedException or FleetException or InvalidOperationException)
                throw new CoordinatorException(StatusCodes.Status409Conflict, FleetRelayErrorCodes.TargetOffline, "The device asked for is not online.", FleetErrorHints.Retry);
            throw;
        }
    }

    /// <summary>Accepts a relay offered to this device, and gives its ticket.</summary>
    /// <param name="target">The device that accepts.</param>
    /// <param name="request">The relay.</param>
    /// <returns>The ticket of the target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="CoordinatorException">No relay is offered to this device with that id.</exception>
    public FleetRelayTicket Accept(DeviceRecord target, FleetRelayRef? request)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (request?.RelayId is not { } id || !_relays.TryGetValue(id, out var relay) || !string.Equals(relay.Target.Id, target.Id, StringComparison.Ordinal)
            || relay.Answer.Task.IsCompleted)
            throw CoordinatorException.NotFound(FleetRelayErrorCodes.NotFound, "No relay with this id waits for this device.");
        var ticket = Issue(relay, viewerSide: false);
        if (!relay.Answer.TrySetResult(true))
        {
            _tickets.TryRemove(ticket.Ticket, out _);
            throw CoordinatorException.NotFound(FleetRelayErrorCodes.NotFound, "No relay with this id waits for this device.");
        }

        return ticket;
    }

    /// <summary>Declines a relay offered to this device, or ends one of its relays.</summary>
    /// <param name="device">The device.</param>
    /// <param name="request">The relay.</param>
    /// <returns>A task that completes once the relay is closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    /// <exception cref="CoordinatorException">The device has no relay with that id.</exception>
    public async Task CloseAsync(DeviceRecord device, FleetRelayClose? request)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (request?.RelayId is not { } id || !_relays.TryGetValue(id, out var relay) || !relay.Involves(device.Id))
            throw CoordinatorException.NotFound(FleetRelayErrorCodes.NotFound, "This device has no relay with this id.");
        if (relay.Answer.TrySetResult(false)) return;
        await FinishAsync(relay, Ends.Closed, audit: true).ConfigureAwait(false);
    }

    /// <summary>Closes every relay of a device, such as one just revoked.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>A task that completes once they are closed.</returns>
    public async Task CloseDeviceAsync(string deviceId)
    {
        foreach (var relay in _relays.Values.Where(relay => relay.Involves(deviceId)).ToArray())
        {
            relay.Answer.TrySetResult(false);
            await FinishAsync(relay, Ends.Revoked, audit: true).ConfigureAwait(false);
        }
    }

    /// <summary>Serves a side of a relay on <c>/fleet/v1/relay/{ticket}</c> until the relay ends.</summary>
    /// <param name="context">The request.</param>
    /// <param name="ticket">The ticket of the path.</param>
    /// <returns>A task that completes when the relay ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="CoordinatorException">The request is not signed by the device of the ticket, or the ticket does not work.</exception>
    public async Task HandleAsync(HttpContext context, string ticket)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.WebSockets.IsWebSocketRequest)
            throw CoordinatorException.BadRequest(FleetErrorCodes.InvalidRequest, "A relayed channel is a WebSocket.");
        // The key first: a ticket seen by someone else is of no use without the device key of its side.
        var device = await _authenticator.AuthenticateAsync(SignedRequestData.From(context.Request, FleetSignedRequest.HashBody([])), context.RequestAborted)
            .ConfigureAwait(false);
        if (!_tickets.TryGetValue(ticket ?? "", out var side) || !string.Equals(side.Device.Id, device.Id, StringComparison.Ordinal)
            || side.ExpiresAt < _time.GetUtcNow() || !_tickets.TryRemove(new KeyValuePair<string, Side>(ticket!, side)))
            throw CoordinatorException.Forbidden(FleetRelayErrorCodes.TicketInvalid, "The ticket is unknown, used or expired, or it is another device's.", FleetErrorHints.Retry);

        var relay = side.Relay;
        var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        if (!relay.Attach(side.IsViewer, socket, out var paired))
        {
            socket.Abort();
            socket.Dispose();
            return;
        }

        // The side that completes the pair starts the splice, once.
        if (paired) _ = RunAsync(relay);
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, Stopping);
        try
        {
            await relay.Completion.WaitAsync(ended.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // This side went away, or the server stops. A splice that runs says how it ended (a limit cuts both sides
            // too); one that never started ends here.
            if (!relay.BothAttached)
            {
                await FinishAsync(relay, Ends.Abandoned, audit: true).ConfigureAwait(false);
                return;
            }

            try { await relay.End.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            await relay.Completion.ConfigureAwait(false);
        }
    }

    private Relay Reserve(DeviceRecord viewer, DeviceRecord target, RequestOrigin origin)
    {
        lock (_gate)
        {
            if (CountLocked(viewer.Id) >= _options.MaxRelaysPerDevice || CountLocked(target.Id) >= _options.MaxRelaysPerDevice)
                throw new CoordinatorException(StatusCodes.Status429TooManyRequests, FleetRelayErrorCodes.TooMany,
                    $"A device has at most {_options.MaxRelaysPerDevice} relays at once.", FleetErrorHints.Retry);
            var relay = new Relay(NewSecret(16), viewer, target, origin, _time.GetUtcNow());
            _relays[relay.Id] = relay;
            Metrics?.RelayOffered();
            return relay;
        }
    }

    private int CountLocked(string deviceId) => _relays.Values.Count(relay => relay.Involves(deviceId));

    private FleetRelayTicket Issue(Relay relay, bool viewerSide)
    {
        var ticket = NewSecret(32);
        var expires = _time.GetUtcNow() + _options.TicketLifetime;
        _tickets[ticket] = new Side(relay, viewerSide ? relay.Viewer : relay.Target, viewerSide, expires);
        return new FleetRelayTicket(relay.Id, ticket, expires, viewerSide ? relay.Target.Id : relay.Viewer.Id);
    }

    private static string NewSecret(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    // A relay whose sides did not both come within the life of a ticket ends.
    private async Task AbandonLaterAsync(Relay relay)
    {
        try { await Task.Delay(_options.TicketLifetime + TimeSpan.FromSeconds(1), _time, relay.End.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (!relay.BothAttached) await FinishAsync(relay, Ends.Abandoned, audit: true).ConfigureAwait(false);
    }

    private async Task RunAsync(Relay relay)
    {
        relay.StartedAt = _time.GetUtcNow();
        Metrics?.RelayOpened();
        using var idle = new CancellationTokenSource();
        idle.CancelAfter(_options.IdleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(relay.End.Token, idle.Token);
        var token = linked.Token;
        var up = PumpAsync(relay, relay.ViewerSocket!, relay.TargetSocket!, upward: true, idle, token);
        var down = PumpAsync(relay, relay.TargetSocket!, relay.ViewerSocket!, upward: false, idle, token);
        var first = await Task.WhenAny(up, down).ConfigureAwait(false);
        // One side closed: the other is told and has a moment to close too.
        try { await Task.WhenAll(up, down).WaitAsync(CloseGrace, _time).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or WebSocketException or IOException) { }

        // A relay another path ended (a revocation, a close) was audited there; this end is a no-op then.
        var end = relay.Limit ? Ends.Limit : idle.IsCancellationRequested ? Ends.Idle : first.IsFaulted ? Ends.Broken : Ends.Closed;
        await FinishAsync(relay, end, audit: true).ConfigureAwait(false);
    }

    private async Task PumpAsync(Relay relay, WebSocket from, WebSocket to, bool upward, CancellationTokenSource idle, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            while (true)
            {
                var received = await from.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await to.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", token).ConfigureAwait(false);
                    return;
                }

                // The channel is TLS records in binary frames; a text frame is not what this path carries.
                if (received.MessageType != WebSocketMessageType.Binary) throw new WebSocketException(WebSocketError.InvalidMessageType);
                if (relay.Add(received.Count, upward) > _options.MaxBytes)
                {
                    relay.Limit = true;
                    await relay.End.CancelAsync().ConfigureAwait(false);
                    return;
                }

                idle.CancelAfter(_options.IdleTimeout);
                await to.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Binary, received.EndOfMessage, token).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task FinishAsync(Relay relay, string end, bool audit)
    {
        if (!relay.TryFinish(end)) return;
        _relays.TryRemove(new KeyValuePair<string, Relay>(relay.Id, relay));
        Metrics?.RelayEnded(end, relay.Up, relay.Down);
        foreach (var pair in _tickets.Where(pair => ReferenceEquals(pair.Value.Relay, relay)).ToArray()) _tickets.TryRemove(pair);
        try { await relay.End.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        relay.Close(end == Ends.Closed);
        if (!audit) return;
        var now = _time.GetUtcNow();
        var seconds = relay.StartedAt is { } started ? (now - started).TotalSeconds : 0;
        var details = string.Create(CultureInfo.InvariantCulture,
            $"{{\"relay\":\"{relay.Id}\",\"up\":{relay.Up},\"down\":{relay.Down},\"opened\":\"{relay.CreatedAt:O}\",\"seconds\":{seconds:0.###},\"end\":\"{end}\"}}");
        var entry = relay.Origin.Audit("relay", relay.Viewer, relay.Target.Id, end == Ends.Closed ? AuditEntry.Success : "relay." + end, details);
        try
        {
            await _store.AppendAuditAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "The audit record of relay {Relay} could not be written", relay.Id);
        }

        _logger.LogInformation("Relay {Relay} from {Viewer} to {Target} ended ({End}): {Up} bytes up, {Down} bytes down", relay.Id, relay.Viewer.Id, relay.Target.Id, end,
            relay.Up, relay.Down);
    }

    /// <summary>How a relay ended, as the audit log says it.</summary>
    private static class Ends
    {
        public const string Closed = "closed";
        public const string Refused = "refused";
        public const string Revoked = "revoked";
        public const string Abandoned = "abandoned";
        public const string Idle = "idle";
        public const string Limit = "limit";
        public const string Broken = "broken";
    }

    private sealed record Side(Relay Relay, DeviceRecord Device, bool IsViewer, DateTimeOffset ExpiresAt);

    private sealed class Relay(string id, DeviceRecord viewer, DeviceRecord target, RequestOrigin origin, DateTimeOffset createdAt)
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _up;
        private long _down;
        private int _finished;

        public string Id { get; } = id;

        public DeviceRecord Viewer { get; } = viewer;

        public DeviceRecord Target { get; } = target;

        public RequestOrigin Origin { get; } = origin;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public DateTimeOffset? StartedAt { get; set; }

        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource End { get; } = new();

        public WebSocket? ViewerSocket { get; private set; }

        public WebSocket? TargetSocket { get; private set; }

        public bool BothAttached { get { lock (_gate) return ViewerSocket is not null && TargetSocket is not null; } }

        public bool Limit { get; set; }

        public string? Reason { get; private set; }

        public long Up => Interlocked.Read(ref _up);

        public long Down => Interlocked.Read(ref _down);

        public Task Completion => _completion.Task;

        public bool Involves(string deviceId) => string.Equals(Viewer.Id, deviceId, StringComparison.Ordinal) || string.Equals(Target.Id, deviceId, StringComparison.Ordinal);

        // Whether the socket was taken; paired says that this one completed the two sides.
        public bool Attach(bool viewerSide, WebSocket socket, out bool paired)
        {
            lock (_gate)
            {
                paired = false;
                if (Volatile.Read(ref _finished) != 0) return false;
                if (viewerSide) ViewerSocket ??= socket;
                else TargetSocket ??= socket;
                if (!ReferenceEquals(viewerSide ? ViewerSocket : TargetSocket, socket)) return false;
                paired = ViewerSocket is not null && TargetSocket is not null;
                return true;
            }
        }

        // The total after these bytes, both directions together.
        public long Add(int count, bool upward) => (upward ? Interlocked.Add(ref _up, count) + Down : Interlocked.Add(ref _down, count) + Up);

        public bool TryFinish(string reason)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return false;
            Reason = reason;
            return true;
        }

        public void Close(bool clean)
        {
            WebSocket?[] sockets;
            lock (_gate) sockets = [ViewerSocket, TargetSocket];
            foreach (var socket in sockets)
            {
                if (socket is null) continue;
                // A clean end was closed by the sides already; any other end cuts both at once.
                if (!clean || socket.State is not (WebSocketState.Closed or WebSocketState.Aborted)) socket.Abort();
            }

            _completion.TrySetResult();
        }
    }
}
