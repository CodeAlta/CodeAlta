using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodeAlta.Fleet.Telemetry;

/// <summary>
/// The instruments of the fleet part of a host (<c>doc/fleet/observability.md</c>): the requests of the node API, the
/// moves, and the control connections to servers with their publications. One meter and one activity source, both
/// named <see cref="Name"/>, owned by the host. They carry methods, codes, counts and short device ids; never a
/// parameter, a title, a path or a prompt.
/// </summary>
/// <remarks>Nothing is recorded while nothing listens.</remarks>
public sealed class FleetMetrics : IDisposable
{
    /// <summary>The name of the meter and of the activity source.</summary>
    public const string Name = "CodeAlta.Fleet";

    private readonly Meter _meter;
    private readonly ActivitySource _source;
    private readonly Histogram<double> _nodeApiDuration;
    private readonly Counter<long> _moves;
    private readonly Counter<long> _connects;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _facts;
    private readonly Counter<long> _settings;
    private readonly ConcurrentDictionary<string, int> _backlogs = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="FleetMetrics"/> class.</summary>
    /// <param name="version">The version of CodeAlta.</param>
    public FleetMetrics(string? version)
    {
        _meter = new Meter(Name, version);
        _source = new ActivitySource(Name, version);
        _nodeApiDuration = _meter.CreateHistogram<double>("codealta.node_api.duration", "s", "The time the node takes to answer a request of the node API.");
        _moves = _meter.CreateCounter<long>("codealta.moves", "{move}", "The moves of sessions, by side and outcome.");
        _connects = _meter.CreateCounter<long>("codealta.fleet.connector.connects", "{connection}", "The control connections to a server that came online, the first and the reconnections.");
        _failures = _meter.CreateCounter<long>("codealta.fleet.connector.failures", "{failure}", "The control connections to a server that failed or closed, by code.");
        _facts = _meter.CreateCounter<long>("codealta.fleet.facts.published", "{fact}", "The facts of sessions published to a server, by what it did with them.");
        _settings = _meter.CreateCounter<long>("codealta.fleet.telemetry.settings", "{setting}", "The telemetry settings of the fleet received, accepted or refused.");
        _meter.CreateObservableGauge("codealta.fleet.publication.backlog", ObserveBacklogs, "{fact}",
            "The facts that waited for their publication at the last read of each server's outbox.");
    }

    /// <summary>Starts the span of a request of the node API; null while nothing listens.</summary>
    /// <param name="method">The method.</param>
    /// <param name="deviceId">The device that calls.</param>
    /// <returns>The span, which <see cref="EndNodeApiRequest"/> ends.</returns>
    public Activity? StartNodeApiRequest(string method, string deviceId)
    {
        var activity = _source.StartActivity("node_api " + method, ActivityKind.Server);
        if (activity is null) return null;
        activity.SetTag("rpc.system", "codealta.node");
        activity.SetTag("rpc.method", method);
        activity.SetTag("codealta.device.short_id", ShortId(deviceId));
        return activity;
    }

    /// <summary>Ends the span of a request of the node API and records its duration.</summary>
    /// <param name="activity">The span, or null.</param>
    /// <param name="method">The method.</param>
    /// <param name="started">When it started (<see cref="Stopwatch.GetTimestamp"/>).</param>
    /// <param name="errorCode">The code of its failure, or null.</param>
    public void EndNodeApiRequest(Activity? activity, string method, long started, string? errorCode)
    {
        _nodeApiDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("rpc.method", method),
            new KeyValuePair<string, object?>("codealta.outcome", errorCode ?? "ok"));
        if (activity is null) return;
        activity.SetTag("codealta.outcome", errorCode ?? "ok");
        if (errorCode is not null)
        {
            activity.SetTag("error.type", errorCode);
            activity.SetStatus(ActivityStatusCode.Error);
        }

        activity.Dispose();
    }

    /// <summary>Records the end of a move, or of one of its steps on the target.</summary>
    /// <param name="side"><c>source</c> or <c>target</c>.</param>
    /// <param name="outcome">Its status.</param>
    public void Move(string side, string outcome)
        => _moves.Add(1, new KeyValuePair<string, object?>("codealta.move.side", side), new KeyValuePair<string, object?>("codealta.outcome", outcome));

    /// <summary>Records a control connection that came online.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="reconnection">Whether it follows a connection that failed or closed.</param>
    public void ConnectorOnline(Uri server, bool reconnection)
        => _connects.Add(1, new KeyValuePair<string, object?>("server.address", server.Host), new KeyValuePair<string, object?>("codealta.reconnection", reconnection));

    /// <summary>Records a control connection that failed or closed.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="code">Why.</param>
    public void ConnectorFailed(Uri server, string code)
        => _failures.Add(1, new KeyValuePair<string, object?>("server.address", server.Host), new KeyValuePair<string, object?>("error.type", code));

    /// <summary>Records how many facts wait for their publication to a server.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="pending">The facts that wait.</param>
    public void Backlog(Uri server, int pending) => _backlogs[server.Host + ":" + server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)] = pending;

    /// <summary>Records what a server did with a batch of facts.</summary>
    /// <param name="server">The address of the server.</param>
    /// <param name="applied">The facts it applied.</param>
    /// <param name="unchanged">Those it held already.</param>
    /// <param name="stale">Those it refused as stale.</param>
    public void FactsPublished(Uri server, int applied, int unchanged, int stale)
    {
        var host = new KeyValuePair<string, object?>("server.address", server.Host);
        if (applied > 0) _facts.Add(applied, host, new KeyValuePair<string, object?>("codealta.outcome", "applied"));
        if (unchanged > 0) _facts.Add(unchanged, host, new KeyValuePair<string, object?>("codealta.outcome", "unchanged"));
        if (stale > 0) _facts.Add(stale, host, new KeyValuePair<string, object?>("codealta.outcome", "stale"));
    }

    /// <summary>Records a telemetry setting of the fleet received.</summary>
    /// <param name="accepted">Whether it was accepted.</param>
    public void TelemetrySetting(bool accepted) => _settings.Add(1, new KeyValuePair<string, object?>("codealta.outcome", accepted ? "accepted" : "refused"));

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

    private IEnumerable<Measurement<int>> ObserveBacklogs()
        => [.. _backlogs.Select(static pair => new Measurement<int>(pair.Value, new KeyValuePair<string, object?>("server.address", pair.Key)))];
}
