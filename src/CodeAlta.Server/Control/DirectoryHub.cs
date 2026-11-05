using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Server.Control;

/// <summary>
/// The watchers of the directory (<c>directory.watch</c>) and the changes they get: machines, published sessions and
/// presence, filtered to what the device's person may see. In this step, that is the devices of its organization.
/// </summary>
/// <remarks>
/// Each watcher has a bounded queue and a loop of its own that sends its events; a watcher that does not read in time
/// is disconnected with a reason (it reconnects and reads the current state), so a slow reader never costs memory or
/// holds back the others.
/// </remarks>
public sealed class DirectoryHub
{
    /// <summary>The events a watcher may have waiting.</summary>
    public const int WatcherCapacity = 512;

    private readonly ConcurrentDictionary<ControlSession, DirectoryWatcher> _watchers = new();
    // The devices revoked since the start, whose publications are announced no more: decided under one lock with each
    // announcement of what a device published, so that one is either sent before the revoke lists what it removes, or never.
    private readonly Lock _removals = new();
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

    /// <summary>Gets the instruments of the watchers; null for none.</summary>
    public Telemetry.ServerMetrics? Metrics { get; init; }

    /// <summary>Gets what decides which changes each watcher may see; null sends every change of the organization (tests of the hub alone).</summary>
    internal Services.DirectoryVisibility? Visibility { get; init; }

    /// <summary>Starts sending the changes of the directory to a connection.</summary>
    /// <param name="session">The connection.</param>
    /// <returns>Whether it was not watching already.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public bool Watch(ControlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_watchers.ContainsKey(session)) return false;
        var watcher = new DirectoryWatcher(session, Metrics, Visibility);
        if (!_watchers.TryAdd(session, watcher)) return false;
        Metrics?.Watcher(1);
        watcher.Start();
        return true;
    }

    /// <summary>Stops sending changes to a connection.</summary>
    /// <param name="session">The connection.</param>
    public void Unwatch(ControlSession session)
    {
        if (_watchers.TryRemove(session, out var watcher))
        {
            Metrics?.Watcher(-1);
            watcher.Stop();
        }
    }

    /// <summary>Sends a change to the watchers that may see it.</summary>
    /// <param name="organizationId">The organization of what changed.</param>
    /// <param name="change">The change.</param>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is <see langword="null"/>.</exception>
    public void Publish(string organizationId, FleetDirectoryChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Metrics?.DirectoryChange(change.Kind);
        foreach (var (session, watcher) in _watchers)
        {
            if (session.Device.OrganizationId == organizationId) watcher.Post(change);
        }
    }

    /// <summary>
    /// Says that a device was revoked: what it published is announced no more. Called once the revoke committed and before it
    /// lists what to remove, so that what was announced before is among it.
    /// </summary>
    /// <param name="deviceId">The device.</param>
    public void MarkRemoved(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        lock (_removals) _removed.Add(deviceId);
    }

    /// <summary>Sends the changes a device published to the watchers that may see them, unless the device was revoked.</summary>
    /// <param name="organizationId">The organization of the device.</param>
    /// <param name="deviceId">The device that published them.</param>
    /// <param name="changes">The changes.</param>
    /// <returns>Whether they were sent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="changes"/> is <see langword="null"/>.</exception>
    public bool PublishFrom(string organizationId, string deviceId, IReadOnlyList<FleetDirectoryChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (_removals)
        {
            if (_removed.Contains(deviceId)) return false;
            foreach (var change in changes) Publish(organizationId, change);
            return true;
        }
    }

    private sealed class DirectoryWatcher(ControlSession session, Telemetry.ServerMetrics? metrics, Services.DirectoryVisibility? visibility)
    {
        private readonly Channel<FleetDirectoryChange> _queue = Channel.CreateBounded<FleetDirectoryChange>(new BoundedChannelOptions(WatcherCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        private long _sequence;

        public void Start() => _ = Task.Run(PumpAsync);

        public void Stop() => _queue.Writer.TryComplete();

        public void Post(FleetDirectoryChange change)
        {
            if (_queue.Writer.TryWrite(change)) return;
            if (_queue.Writer.TryComplete())
            {
                metrics?.WatcherDropped();
                _ = session.CloseAsync(new FleetError(FleetErrorCodes.ServerBusy, FleetErrorHints.Reconnect,
                    "The directory events were not read in time: reconnect and watch again."));
            }
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var change in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    // A change this device may not see is not sent: decided when it is sent, with access as it stands then.
                    if (visibility is not null && !await visibility.SeesAsync(session.Device, change, CancellationToken.None).ConfigureAwait(false)) continue;
                    var payload = JsonSerializer.SerializeToElement(change, FleetServerJsonContext.Default.FleetDirectoryChange);
                    await session.Connection.SendEventAsync(new FleetEvent(FleetServerMethods.DirectoryChanged, payload, null, ++_sequence)).ConfigureAwait(false);
                }
            }
            catch (FleetException)
            {
                // The connection closed: its watch ends with it.
                _queue.Writer.TryComplete();
            }
        }
    }
}
