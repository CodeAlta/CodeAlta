using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Telemetry;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Storage;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace CodeAlta.Server.Telemetry;

/// <summary>Where the server forwards the OTLP of machines on the server route: the collector and the headers only the server holds.</summary>
/// <param name="Endpoint">The base address of the collector (OTLP/HTTP).</param>
/// <param name="Headers">The headers sent to it.</param>
/// <param name="Signals">The signals the fleet sends.</param>
internal sealed record OtlpForwardTarget(Uri Endpoint, IReadOnlyDictionary<string, string> Headers, IReadOnlySet<string> Signals);

/// <summary>
/// The telemetry of a running server: its own traces, metrics and logs, exported as its plan says (its own
/// <c>telemetry.json</c>, then the <c>OTEL_*</c> variables, then the fleet's setting), and where it forwards the OTLP of
/// the machines when the fleet's setting sends it through the server. A new setting applies at once.
/// </summary>
internal sealed class ServerTelemetry : IDisposable
{
    /// <summary>The activity sources the server exports: its own and ASP.NET Core's requests.</summary>
    public static readonly IReadOnlyList<string> Sources = [ServerMetrics.Name, "Microsoft.AspNetCore"];

    /// <summary>The meters the server exports: its own, ASP.NET Core's and Kestrel's, and the runtime's.</summary>
    public static readonly IReadOnlyList<string> Meters = [ServerMetrics.Name, "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Http.Connections", "Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Diagnostics", "System.Runtime"];

    private readonly ServerPaths _paths;
    private readonly Func<string, string?> _variable;
    private readonly TimeSpan? _exportInterval;
    private readonly string _instanceId;
    private readonly Lock _gate = new();
    private string? _organizationId;
    private OtlpForwardTarget? _forward;
    private ILogger? _log;

    /// <summary>Initializes a new instance of the <see cref="ServerTelemetry"/> class; nothing is exported before <see cref="Apply"/>.</summary>
    /// <param name="paths">The files of the server: its <c>telemetry.json</c> and its instance id.</param>
    /// <param name="metrics">The instruments of the server.</param>
    /// <param name="variable">Gives the value of a variable of the environment.</param>
    /// <param name="exportInterval">How often metrics are read and batches sent; null for the defaults.</param>
    public ServerTelemetry(ServerPaths paths, ServerMetrics metrics, Func<string, string?> variable, TimeSpan? exportInterval)
    {
        _paths = paths;
        Metrics = metrics;
        _variable = variable;
        _exportInterval = exportInterval;
        _instanceId = ReadInstanceId(paths);
        Runtime = new FleetTelemetryRuntime(Options, message => _log?.LogWarning("{Message}", message));
    }

    /// <summary>Gets the instruments of the server.</summary>
    public ServerMetrics Metrics { get; }

    /// <summary>Gets the export, whose loggers the server's log writes to.</summary>
    public FleetTelemetryRuntime Runtime { get; }

    /// <summary>Gets where the OTLP of the machines is forwarded now; null when the fleet does not send it through the server.</summary>
    public OtlpForwardTarget? Forward { get { lock (_gate) return _forward; } }

    /// <summary>Gets the instance id of the server in its resource.</summary>
    public string InstanceId => _instanceId;

    /// <summary>Sets the log where the applied plans are told.</summary>
    /// <param name="log">The log.</param>
    public void SetLog(ILogger log) => _log = log;

    /// <summary>Applies the fleet's setting, with the server's own file and environment, which win over it.</summary>
    /// <param name="settings">The setting the server holds, or null.</param>
    /// <param name="organizationId">The organization of the server, once it has an owner.</param>
    public void Apply(TelemetrySettingsRecord? settings, string? organizationId)
    {
        FleetLocalTelemetrySettings? local;
        var localBroken = false;
        try
        {
            local = FleetLocalTelemetrySettings.Read(_paths.TelemetryPath);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // A file the owner wrote and that does not read sends nothing rather than guess what it meant.
            _log?.LogError("The telemetry file does not read, so telemetry is off: {Reason}", exception.Message);
            local = new FleetLocalTelemetrySettings { Enabled = false };
            localBroken = true;
        }

        FleetTelemetrySettings? fleet = null;
        IReadOnlyDictionary<string, string>? serverHeaders = null;
        if (settings is not null && FleetTelemetrySettingsDocuments.TryParse(settings.Payload, out var parsed))
        {
            fleet = parsed;
            serverHeaders = ReadHeaders(settings.ServerHeaders);
        }

        // The server sends its own telemetry straight to the collector: on the server route, with the headers it holds.
        var own = fleet is { Route: FleetTelemetryRoutes.Server } ? fleet with { Route = FleetTelemetryRoutes.Direct, Headers = serverHeaders } : fleet;
        var plan = FleetTelemetryPlanner.Resolve(local, _variable, own);
        OtlpForwardTarget? forward = null;
        if (local?.Enabled != false && !localBroken && fleet is { Enabled: true, Route: FleetTelemetryRoutes.Server }
            && FleetTelemetrySettingsDocuments.TryParseEndpoint(fleet.Endpoint, out var collector))
        {
            forward = new OtlpForwardTarget(collector, serverHeaders ?? new Dictionary<string, string>(), fleet.Signals.ToHashSet(StringComparer.Ordinal));
        }

        bool organizationChanged;
        lock (_gate)
        {
            organizationChanged = !string.Equals(_organizationId, organizationId, StringComparison.Ordinal);
            _organizationId = organizationId;
            _forward = forward;
        }

        if (Runtime.Apply(plan, force: organizationChanged && plan.Enabled)) Describe(plan, forward);
    }

    /// <summary>Ends the export, once it sent what it holds.</summary>
    public void Dispose() => Runtime.Dispose();

    /// <summary>Reads the headers the server holds for the collector.</summary>
    /// <param name="json">Their JSON, or null.</param>
    /// <returns>The headers, or null.</returns>
    public static IReadOnlyDictionary<string, string>? ReadHeaders(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize(json, FleetTelemetryJsonContext.Default.DictionaryStringString);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Describe(FleetTelemetryPlan plan, OtlpForwardTarget? forward)
    {
        if (_log is null) return;
        if (!plan.Enabled)
        {
            _log.LogInformation("Telemetry: off{Forward}", forward is null ? "" : $"; machines' OTLP is forwarded to {forward.Endpoint}");
            return;
        }

        var signals = string.Join(", ", new[] { plan.Traces ? "traces" : null, plan.Metrics ? "metrics" : null, plan.Logs ? "logs" : null }.Where(static name => name is not null));
        _log.LogInformation("Telemetry: {Signals} to {Endpoint} ({Source}){Forward}", signals, plan.Endpoint?.ToString() ?? "the collector of the OTEL_* variables", plan.Source,
            forward is null ? "" : $"; machines' OTLP is forwarded to {forward.Endpoint}");
    }

    private FleetOtlpPipelineOptions Options(FleetTelemetryPlan plan)
    {
        string? organizationId;
        lock (_gate) organizationId = _organizationId;
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal);
        if (organizationId is not null) attributes["codealta.fleet.organization.id"] = organizationId;
        return new FleetOtlpPipelineOptions
        {
            Plan = plan,
            ServiceName = "altaserver",
            ServiceVersion = ServerVersion.Current,
            InstanceId = _instanceId,
            Attributes = attributes,
            Sources = Sources,
            Meters = Meters,
            ExportInterval = _exportInterval,
            ConfigureTraces = static builder => builder.AddProcessor(new HttpSpanScrubber()),
        };
    }

    private static string ReadInstanceId(ServerPaths paths)
    {
        try
        {
            var text = File.ReadAllText(paths.InstanceIdPath).Trim();
            if (text.Length is > 0 and <= 64) return text;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
        }

        var id = Guid.CreateVersion7().ToString("N");
        try
        {
            File.WriteAllText(paths.InstanceIdPath, id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An id for this run only: the resource tells this process apart all the same.
        }

        return id;
    }

    /// <summary>
    /// Keeps secrets and content out of the spans of HTTP requests: the ticket in the path of a relay is replaced by its
    /// name, and the query goes.
    /// </summary>
    private sealed class HttpSpanScrubber : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            if (data.GetTagItem("url.path") is string path && path.StartsWith(FleetServerMethods.RelayPath + "/", StringComparison.Ordinal))
                data.SetTag("url.path", FleetServerMethods.RelayPath + "/{ticket}");
            if (data.GetTagItem("url.query") is not null) data.SetTag("url.query", null);
            if (data.GetTagItem("url.full") is not null) data.SetTag("url.full", null);
        }
    }
}
