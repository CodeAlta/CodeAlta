using System.Collections.Concurrent;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Control;

/// <summary>The control connection of one device, while it is open.</summary>
public sealed class ControlSession
{
    private readonly Lock _gate = new();
    private string? _state;
    private int? _activeSessions;
    private int? _queuedSessions;
    private int? _sessionLimit;
    private FleetUpdateReport? _update;

    /// <summary>Initializes a new instance of the <see cref="ControlSession"/> class.</summary>
    /// <param name="device">The device, as it was when it connected.</param>
    /// <param name="connection">Its connection.</param>
    /// <param name="connectedAt">When it connected.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ControlSession(DeviceRecord device, FleetConnection connection, DateTimeOffset connectedAt)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(connection);
        Device = device;
        Connection = connection;
        ConnectedAt = connectedAt;
    }

    /// <summary>Gets the device.</summary>
    public DeviceRecord Device { get; }

    /// <summary>Gets the connection.</summary>
    public FleetConnection Connection { get; }

    /// <summary>Gets when the device connected.</summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>Gets the address the connection comes from, for the audit log, or <see langword="null"/> when unknown.</summary>
    public System.Net.IPAddress? RemoteAddress { get; init; }

    /// <summary>Closes the connection after telling the device why (<see cref="FleetServerMethods.ConnectionClosing"/>).</summary>
    /// <param name="error">Why, and what the device should do.</param>
    /// <returns>A task that completes when the close started.</returns>
    public async Task CloseAsync(FleetError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            // A reader that stays behind does not hold the close: the reason waits a moment for room, then the close goes on.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(error, FleetServerJsonContext.Default.FleetError);
            await Connection.SendEventAsync(new FleetEvent(FleetServerMethods.ConnectionClosing, payload), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetException or OperationCanceledException or InvalidOperationException)
        {
            // Closed already, or too slow to read: the close says the rest.
        }

        await Connection.CloseAsync(error.Message ?? error.Code).ConfigureAwait(false);
    }

    /// <summary>Gets the presence of the device now.</summary>
    /// <returns>Its presence, online.</returns>
    public FleetPresence GetPresence()
    {
        lock (_gate) return new FleetPresence(Device.Id, true, _state, _activeSessions, ConnectedAt, _update) { QueuedSessions = _queuedSessions, SessionLimit = _sessionLimit };
    }

    /// <summary>Keeps the state the device sent with its heartbeat.</summary>
    /// <param name="update">The state.</param>
    /// <returns>Whether it differs from the state held.</returns>
    public bool Update(FleetPresenceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var report = update.Update ?? _update;
            if (_state == update.State && _activeSessions == update.ActiveSessions && _queuedSessions == update.QueuedSessions && _sessionLimit == update.SessionLimit
                && Equals(_update, report))
                return false;
            _state = update.State;
            _activeSessions = update.ActiveSessions;
            _queuedSessions = update.QueuedSessions;
            _sessionLimit = update.SessionLimit;
            _update = report;
            return true;
        }
    }

    /// <summary>Keeps where the device stands in an update campaign, as it reported it.</summary>
    /// <param name="report">The report.</param>
    /// <returns>Whether it differs from the one held.</returns>
    public bool Update(FleetUpdateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            if (Equals(_update, report)) return false;
            _update = report;
            return true;
        }
    }
}

/// <summary>
/// The devices online: their control connections, in memory only. A heartbeat never writes the database; a restart
/// of the server forgets presence, and devices reconnect.
/// </summary>
public sealed class PresenceRegistry
{
    private readonly ConcurrentDictionary<string, ControlSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Records a new connection of a device.</summary>
    /// <param name="session">The connection.</param>
    /// <returns>The connection it replaces (an older one of the same device, which the caller closes), or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public ControlSession? Add(ControlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ControlSession? replaced = null;
        _sessions.AddOrUpdate(session.Device.Id, session, (_, old) =>
        {
            replaced = old;
            return session;
        });
        return ReferenceEquals(replaced, session) ? null : replaced;
    }

    /// <summary>Forgets a connection, unless a newer one of its device replaced it.</summary>
    /// <param name="session">The connection.</param>
    /// <returns>Whether the device is offline now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public bool Remove(ControlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _sessions.TryRemove(new KeyValuePair<string, ControlSession>(session.Device.Id, session));
    }

    /// <summary>Gets whether a device is online.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>Whether it holds a control connection.</returns>
    public bool IsOnline(string deviceId) => _sessions.ContainsKey(deviceId);

    /// <summary>Gets the connection of a device.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>Its connection, or <see langword="null"/>.</returns>
    public ControlSession? Get(string deviceId) => _sessions.TryGetValue(deviceId, out var session) ? session : null;

    /// <summary>Lists the devices online of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <returns>Their connections.</returns>
    public IReadOnlyList<ControlSession> List(string organizationId)
        => _sessions.Values.Where(session => session.Device.OrganizationId == organizationId).OrderBy(static session => session.Device.Id, StringComparer.Ordinal).ToArray();

    /// <summary>Closes the connection of a device, such as one just revoked.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="error">Why, sent to the device.</param>
    /// <returns>A task that completes when the close started.</returns>
    public Task DisconnectAsync(string deviceId, FleetError error)
        => _sessions.TryGetValue(deviceId, out var session) ? session.CloseAsync(error) : Task.CompletedTask;
}
