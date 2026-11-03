using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodeAlta.Server.Telemetry;

/// <summary>
/// The instruments of CodeAlta Server (<c>doc/fleet/observability.md</c>, health checks): its meter and its activity
/// source, both named <see cref="Name"/>, owned by one server. They carry ids, kinds, codes and counts, never content:
/// no title, path, prompt or key.
/// </summary>
/// <remarks>Nothing is recorded while nothing listens: a server without a collector pays a check per call.</remarks>
public sealed class ServerMetrics : IDisposable
{
    /// <summary>The name of the meter and of the activity source of the server.</summary>
    public const string Name = "CodeAlta.Server";

    private readonly Meter _meter;
    private readonly ActivitySource _source;
    private readonly UpDownCounter<long> _connections;
    private readonly Counter<long> _handshakes;
    private readonly Histogram<double> _controlDuration;
    private readonly Counter<long> _publications;
    private readonly Counter<long> _facts;
    private readonly UpDownCounter<long> _watchers;
    private readonly Counter<long> _directoryChanges;
    private readonly Counter<long> _watchersDropped;
    private readonly Counter<long> _relaysOpened;
    private readonly Counter<long> _relaysRefused;
    private readonly UpDownCounter<long> _relaysActive;
    private readonly Counter<long> _relayBytes;
    private readonly Counter<long> _relaysEnded;
    private readonly Counter<long> _enrollments;
    private readonly Counter<long> _deviceListChanges;
    private readonly Counter<long> _signedRefusals;
    private readonly Histogram<double> _storeDuration;
    private readonly Counter<long> _forwarded;
    private readonly Counter<long> _forwardedBytes;
    private readonly Counter<long> _settings;

    /// <summary>Initializes a new instance of the <see cref="ServerMetrics"/> class.</summary>
    /// <param name="version">The version of the server.</param>
    /// <param name="writeQueueDepth">Gives how many writes wait in the queue of the store; null for none.</param>
    public ServerMetrics(string? version, Func<int>? writeQueueDepth)
    {
        _meter = new Meter(Name, version);
        _source = new ActivitySource(Name, version);
        _connections = _meter.CreateUpDownCounter<long>("codealta.server.control.connections", "{connection}", "The control connections of devices open now.");
        _handshakes = _meter.CreateCounter<long>("codealta.server.control.handshakes", "{handshake}", "The handshakes of control connections, accepted or refused.");
        _controlDuration = _meter.CreateHistogram<double>("codealta.server.control.request.duration", "s", "The time the server takes to answer a request of a control connection.");
        _publications = _meter.CreateCounter<long>("codealta.server.publications", "{publication}", "The inventories, facts and withdrawals machines published.");
        _facts = _meter.CreateCounter<long>("codealta.server.facts", "{fact}", "The facts of sessions received, by what the server did with them.");
        _watchers = _meter.CreateUpDownCounter<long>("codealta.server.directory.watchers", "{watcher}", "The connections that follow the directory now.");
        _directoryChanges = _meter.CreateCounter<long>("codealta.server.directory.changes", "{change}", "The changes of the directory announced to watchers.");
        _watchersDropped = _meter.CreateCounter<long>("codealta.server.directory.dropped", "{watcher}", "The watchers disconnected because they read too slowly.");
        _relaysOpened = _meter.CreateCounter<long>("codealta.server.relay.opened", "{relay}", "The relays opened between two devices.");
        _relaysRefused = _meter.CreateCounter<long>("codealta.server.relay.refused", "{relay}", "The relays refused, by code.");
        _relaysActive = _meter.CreateUpDownCounter<long>("codealta.server.relay.active", "{relay}", "The relays offered or open now.");
        _relayBytes = _meter.CreateCounter<long>("codealta.server.relay.bytes", "By", "The bytes the relays carried, by direction.");
        _relaysEnded = _meter.CreateCounter<long>("codealta.server.relay.ended", "{relay}", "The relays that ended, by how.");
        _enrollments = _meter.CreateCounter<long>("codealta.server.enrollments", "{enrollment}", "The enrollments of devices, by outcome.");
        _deviceListChanges = _meter.CreateCounter<long>("codealta.server.device_list.changes", "{change}", "The approvals, revocations and refusals of devices, by outcome.");
        _signedRefusals = _meter.CreateCounter<long>("codealta.server.signed_requests.refused", "{request}", "The signed requests refused, by code.");
        _storeDuration = _meter.CreateHistogram<double>("codealta.server.store.duration", "s", "The time of the operations of the store, a write's wait for the writer included.");
        _forwarded = _meter.CreateCounter<long>("codealta.server.otlp.forwarded", "{request}", "The OTLP requests of machines forwarded to the collector, by signal and outcome.");
        _forwardedBytes = _meter.CreateCounter<long>("codealta.server.otlp.bytes", "By", "The bytes of OTLP forwarded to the collector, by signal.");
        _settings = _meter.CreateCounter<long>("codealta.server.telemetry.settings", "{change}", "The changes of the telemetry setting of the fleet, by outcome.");
        if (writeQueueDepth is not null)
        {
            _meter.CreateObservableGauge("codealta.server.store.write_queue", writeQueueDepth, "{write}", "The writes that wait for the writer of the store.");
        }
    }

    /// <summary>Starts the span of a request on a control connection; null while nothing listens.</summary>
    /// <param name="method">The method.</param>
    /// <param name="deviceId">The device that asks.</param>
    /// <returns>The span, which <see cref="EndControlRequest"/> ends.</returns>
    public Activity? StartControlRequest(string method, string deviceId)
    {
        var activity = _source.StartActivity("fleet.control " + method, ActivityKind.Server);
        if (activity is null) return null;
        activity.SetTag("rpc.system", "codealta.fleet");
        activity.SetTag("rpc.method", method);
        activity.SetTag("codealta.device.short_id", ShortId(deviceId));
        return activity;
    }

    /// <summary>Ends the span of a request on a control connection and records its duration.</summary>
    /// <param name="activity">The span, or null.</param>
    /// <param name="method">The method.</param>
    /// <param name="started">When it started (<see cref="Stopwatch.GetTimestamp"/>).</param>
    /// <param name="errorCode">The code of its failure, or null.</param>
    public void EndControlRequest(Activity? activity, string method, long started, string? errorCode)
    {
        var outcome = errorCode ?? "ok";
        _controlDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new("rpc.method", method), new("codealta.outcome", outcome));
        if (activity is null) return;
        if (errorCode is not null)
        {
            activity.SetTag("error.type", errorCode);
            activity.SetStatus(ActivityStatusCode.Error);
        }

        activity.Dispose();
    }

    /// <summary>Records the handshake of a control connection.</summary>
    /// <param name="errorCode">Its refusal, or null when accepted.</param>
    public void Handshake(string? errorCode)
        => _handshakes.Add(1, new KeyValuePair<string, object?>("codealta.outcome", errorCode is null ? "accepted" : "refused"),
            new KeyValuePair<string, object?>("error.type", errorCode));

    /// <summary>Records a control connection that opened or closed.</summary>
    /// <param name="kind">The class of the device.</param>
    /// <param name="delta">+1 when it opened, -1 when it closed.</param>
    public void Connection(string kind, int delta) => _connections.Add(delta, new KeyValuePair<string, object?>("codealta.device.kind", kind));

    /// <summary>Records a publication of a machine.</summary>
    /// <param name="kind"><c>inventory</c>, <c>facts</c> or <c>withdraw</c>.</param>
    /// <param name="errorCode">Its refusal, or null.</param>
    public void Publication(string kind, string? errorCode)
        => _publications.Add(1, new KeyValuePair<string, object?>("codealta.publication.kind", kind), new KeyValuePair<string, object?>("codealta.outcome", errorCode ?? "ok"));

    /// <summary>Records what happened to the facts of a batch.</summary>
    /// <param name="applied">The facts that changed what the server holds.</param>
    /// <param name="unchanged">The facts it held already.</param>
    /// <param name="stale">The facts refused as stale.</param>
    public void Facts(int applied, int unchanged, int stale)
    {
        if (applied > 0) _facts.Add(applied, new KeyValuePair<string, object?>("codealta.outcome", "applied"));
        if (unchanged > 0) _facts.Add(unchanged, new KeyValuePair<string, object?>("codealta.outcome", "unchanged"));
        if (stale > 0) _facts.Add(stale, new KeyValuePair<string, object?>("codealta.outcome", "stale"));
    }

    /// <summary>Records a watcher of the directory that started or stopped.</summary>
    /// <param name="delta">+1 or -1.</param>
    public void Watcher(int delta) => _watchers.Add(delta);

    /// <summary>Records a change announced to the watchers of the directory.</summary>
    /// <param name="kind">The kind of change.</param>
    public void DirectoryChange(string kind) => _directoryChanges.Add(1, new KeyValuePair<string, object?>("codealta.change.kind", kind));

    /// <summary>Records a watcher disconnected because it read too slowly.</summary>
    public void WatcherDropped() => _watchersDropped.Add(1);

    /// <summary>Records a relay offered: it counts as active until <see cref="RelayEnded"/>.</summary>
    public void RelayOffered() => _relaysActive.Add(1);

    /// <summary>Records a relay both sides accepted.</summary>
    public void RelayOpened() => _relaysOpened.Add(1);

    /// <summary>Records a relay refused.</summary>
    /// <param name="code">Why.</param>
    public void RelayRefused(string code) => _relaysRefused.Add(1, new KeyValuePair<string, object?>("error.type", code));

    /// <summary>Records a relay that ended, with its bytes.</summary>
    /// <param name="end">How it ended.</param>
    /// <param name="up">The bytes from the viewer to the target.</param>
    /// <param name="down">The bytes from the target to the viewer.</param>
    public void RelayEnded(string end, long up, long down)
    {
        _relaysActive.Add(-1);
        _relaysEnded.Add(1, new KeyValuePair<string, object?>("codealta.relay.end", end));
        if (up > 0) _relayBytes.Add(up, new KeyValuePair<string, object?>("codealta.direction", "up"));
        if (down > 0) _relayBytes.Add(down, new KeyValuePair<string, object?>("codealta.direction", "down"));
    }

    /// <summary>Records an enrollment of a device.</summary>
    /// <param name="outcome"><c>enrolled</c> or the code of its refusal.</param>
    public void Enrollment(string outcome) => _enrollments.Add(1, new KeyValuePair<string, object?>("codealta.outcome", outcome));

    /// <summary>Records an approval, a revocation or a refusal of a device.</summary>
    /// <param name="operation"><c>approve</c> or <c>revoke</c>.</param>
    /// <param name="errorCode">Its refusal, or null.</param>
    public void DeviceListChange(string operation, string? errorCode)
        => _deviceListChanges.Add(1, new KeyValuePair<string, object?>("codealta.operation", operation), new KeyValuePair<string, object?>("codealta.outcome", errorCode ?? "ok"));

    /// <summary>Records a signed request refused.</summary>
    /// <param name="code">Why.</param>
    public void SignedRequestRefused(string code) => _signedRefusals.Add(1, new KeyValuePair<string, object?>("error.type", code));

    /// <summary>Records an operation of the store.</summary>
    /// <param name="operation">Its name.</param>
    /// <param name="duration">How long it took.</param>
    /// <param name="failed">Whether it failed.</param>
    public void StoreOperation(string operation, TimeSpan duration, bool failed)
        => _storeDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("db.operation.name", operation),
            new KeyValuePair<string, object?>("codealta.outcome", failed ? "error" : "ok"));

    /// <summary>Records an OTLP request of a machine forwarded, or not, to the collector.</summary>
    /// <param name="signal">The signal.</param>
    /// <param name="outcome"><c>ok</c>, the status of the collector, or why it was not forwarded.</param>
    /// <param name="bytes">The bytes forwarded.</param>
    public void Forwarded(string signal, string outcome, long bytes)
    {
        _forwarded.Add(1, new KeyValuePair<string, object?>("codealta.signal", signal), new KeyValuePair<string, object?>("codealta.outcome", outcome));
        if (bytes > 0) _forwardedBytes.Add(bytes, new KeyValuePair<string, object?>("codealta.signal", signal));
    }

    /// <summary>Records a change of the telemetry setting of the fleet.</summary>
    /// <param name="errorCode">Its refusal, or null.</param>
    public void TelemetrySettings(string? errorCode) => _settings.Add(1, new KeyValuePair<string, object?>("codealta.outcome", errorCode ?? "ok"));

    /// <summary>Gets the first characters of a device id: enough to tell devices apart in a trace.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <returns>Its first 8 characters.</returns>
    public static string ShortId(string? deviceId) => string.IsNullOrEmpty(deviceId) ? "-" : deviceId.Length <= 8 ? deviceId : deviceId[..8];

    /// <inheritdoc />
    public void Dispose()
    {
        _source.Dispose();
        _meter.Dispose();
    }
}
