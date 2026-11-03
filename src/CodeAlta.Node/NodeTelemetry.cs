using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using CodeAlta.Agent.Telemetry;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Fleet.Telemetry;
using Microsoft.Extensions.Logging;
using XenoAtom.Logging;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;
using XenoLogLevel = XenoAtom.Logging.LogLevel;

namespace CodeAlta.Node;

/// <summary>What <see cref="NodeTelemetry"/> reads and exports.</summary>
internal sealed record NodeTelemetryOptions
{
    /// <summary>Gets the fleet folder of the state root: the node's own <c>telemetry.json</c>, its key, and the links to its servers.</summary>
    public required string FleetRoot { get; init; }

    /// <summary>Gets the version of CodeAlta the node runs.</summary>
    public required string Version { get; init; }

    /// <summary>Gets what gives the value of a variable of the environment (the standard <c>OTEL_*</c> ones).</summary>
    public Func<string, string?> Variable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Gets how often metrics are read and batches sent; null for the defaults of OpenTelemetry.</summary>
    public TimeSpan? ExportInterval { get; init; }

    /// <summary>Gets how many sessions work now; null for none.</summary>
    public Func<int>? SessionsWorking { get; init; }

    /// <summary>Gets how many devices hold a channel to the node now; null for none.</summary>
    public Func<int>? ConnectedDevices { get; init; }

    /// <summary>Gets how many servers the node is connected to now; null for none.</summary>
    public Func<int>? ServersOnline { get; init; }

    /// <summary>Gets what masks the secrets of the machine in the log records exported, as in the log file; null for nothing.</summary>
    public CodeAlta.Agent.Runtime.AgentSecretMasker? SecretMasker { get; init; }
}

/// <summary>
/// The telemetry of a running node (<c>doc/fleet/observability.md</c>): the spans of its session runs
/// (<see cref="AgentTelemetry"/>, content off toward the fleet's collector), of its node API and moves, the metrics of
/// its sessions, devices and servers, and its log records, exported over OTLP as its plan says. The plan comes from the
/// node's own <c>fleet/telemetry.json</c>, then the <c>OTEL_*</c> variables, then the setting of the fleet, which a
/// server pushes and the node applies only once a controller device of its organization's verified device list signed
/// it. On the fleet's server route, the node sends to its server, each request signed with its device key.
/// </summary>
internal sealed class NodeTelemetry : IDisposable
{
    /// <summary>The name of the meter of the node.</summary>
    public const string MeterName = "CodeAlta.Node";

    /// <summary>The name of the file of the setting a server pushed, in the folder of its link.</summary>
    public const string SettingsFileName = "telemetry.json";

    private static readonly IReadOnlyList<string> Sources = [AgentTelemetry.SourceName, FleetMetrics.Name];
    private static readonly IReadOnlyList<string> Meters = [FleetMetrics.Name, MeterName, "System.Runtime"];

    private readonly NodeTelemetryOptions _options;
    private readonly DeviceIdentity _identity;
    private readonly Meter _meter;
    private readonly FleetServerLinkStore _links;
    private readonly ConcurrentDictionary<string, Accepted> _accepted = new(StringComparer.Ordinal);
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    // Takes one setting at a time, from its check to its file and its plan: two pushes checked at once could both pass
    // against the setting held, and the older one would then replace the newer one, also on disk.
    private readonly Lock _settingsGate = new();
    private Accepted? _applied;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="NodeTelemetry"/> class and applies what the node holds: its file, its variables and the settings servers pushed before.</summary>
    /// <param name="options">The options.</param>
    public NodeTelemetry(NodeTelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _identity = DeviceIdentity.LoadOrCreate(options.FleetRoot);
        _links = new FleetServerLinkStore(options.FleetRoot);
        Metrics = new FleetMetrics(options.Version);
        _meter = new Meter(MeterName, options.Version);
        if (options.SessionsWorking is { } working)
            _meter.CreateObservableGauge("codealta.node.sessions.working", working, "{session}", "The sessions of the node at work now.");
        if (options.ConnectedDevices is { } devices)
            _meter.CreateObservableGauge("codealta.node.devices.connected", devices, "{channel}", "The channels devices hold to the node now, direct or relayed.");
        if (options.ServersOnline is { } servers)
            _meter.CreateObservableGauge("codealta.node.servers.online", servers, "{server}", "The servers the node holds a control connection to now.");
        Runtime = new FleetTelemetryRuntime(PipelineOptions, message => Log(XenoLogLevel.Warn, message));
        LogWriter = new NodeOtlpLogWriter(Runtime, options.SecretMasker);
        _log = Runtime.CreateLogger("CodeAlta.Node.Telemetry");
        LoadAccepted();
        Reapply();
    }

    /// <summary>Gets the instruments of the node API, the moves and the connections, given to them.</summary>
    public FleetMetrics Metrics { get; }

    /// <summary>Gets the export.</summary>
    public FleetTelemetryRuntime Runtime { get; }

    /// <summary>Gets the writer of the log of the node whose records go to the export, when it sends logs.</summary>
    public NodeOtlpLogWriter LogWriter { get; }

    /// <summary>Gets the plan applied.</summary>
    public FleetTelemetryPlan Plan => Runtime.Plan;

    /// <summary>Gets the device id of the node: the <c>service.instance.id</c> of its resource.</summary>
    public string DeviceId => _identity.DeviceId;

    /// <summary>
    /// Takes the setting a server pushed: verifies it against the device list of that server, read again (the one last
    /// verified when the server does not answer), keeps it in the folder of the link, and applies it.
    /// </summary>
    /// <param name="connector">The connector of the server.</param>
    /// <param name="link">The link of the server.</param>
    /// <param name="signed">The signed setting.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes once the setting is applied or refused.</returns>
    public async Task AcceptAsync(FleetConnector connector, FleetServerLink link, FleetSignedTelemetrySettings signed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(link);
        FleetDeviceListState? devices;
        try
        {
            devices = await connector.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            devices = connector.Devices.Current;
        }
        catch (FleetDeviceListException exception)
        {
            Refused(link, $"the device list of the server does not verify ({exception.Reason})");
            return;
        }

        if (devices is null)
        {
            Refused(link, "the device list of the server could not be read");
            return;
        }

        Accept(link, signed, devices, link.OrganizationId ?? devices.OrganizationId, out _);
    }

    /// <summary>Verifies a pushed setting against a device list and applies it when it holds and is newer than the one held.</summary>
    /// <param name="link">The link of the server that pushed it.</param>
    /// <param name="signed">The signed setting.</param>
    /// <param name="devices">The device list of that server, as the node verified it.</param>
    /// <param name="organizationId">The organization of the server.</param>
    /// <param name="reason">Why it was refused, or null.</param>
    /// <returns>Whether it holds (a setting the node holds already holds too).</returns>
    internal bool Accept(FleetServerLink link, FleetSignedTelemetrySettings signed, FleetDeviceListState devices, string? organizationId, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(devices);
        lock (_settingsGate) return AcceptCore(link, signed, devices, organizationId, out reason);
    }

    /// <summary>Resolves the plan again (the node's file may have changed) and applies it.</summary>
    public void Reapply()
    {
        lock (_settingsGate) ReapplyCore();
    }

    /// <summary>Ends the export, once it sent what it holds.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        LogWriter.Detach();
        Runtime.Dispose();
        _meter.Dispose();
        Metrics.Dispose();
        _identity.Dispose();
    }

    private bool AcceptCore(FleetServerLink link, FleetSignedTelemetrySettings signed, FleetDeviceListState devices, string? organizationId, out string? reason)
    {
        if (!FleetTelemetrySettingsDocuments.TryVerify(signed, devices, organizationId, out var settings, out reason))
        {
            Refused(link, reason);
            return false;
        }

        if (_accepted.TryGetValue(link.Id, out var held))
        {
            // A server sends the setting again on each connection: the same one is no change.
            if (held.Signed == signed) return true;
            if (settings.Sequence <= held.Settings.Sequence)
            {
                reason = $"setting {settings.Sequence} is not newer than setting {held.Settings.Sequence}";
                Refused(link, reason);
                return false;
            }
        }

        var accepted = new Accepted(link.Id, link.Address, organizationId, signed, settings);
        _accepted[link.Id] = accepted;
        try
        {
            var path = SettingsPath(link.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(signed, FleetTelemetryJsonContext.Default.FleetSignedTelemetrySettings));
            File.Move(staging, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log(XenoLogLevel.Warn, $"The telemetry setting of {link.Url} could not be kept: {exception.Message}");
        }

        Metrics.TelemetrySetting(true);
        Log(XenoLogLevel.Info, $"The telemetry setting {settings.Sequence} of {link.Url}, signed by {FleetMetrics.ShortId(settings.SignerDeviceId)}, was accepted: "
            + (settings.Enabled ? $"{string.Join(", ", settings.Signals)} {settings.Route} to {settings.Endpoint}" : "off") + ".");
        ReapplyCore();
        return true;
    }

    private void ReapplyCore()
    {
        FleetLocalTelemetrySettings? local;
        try
        {
            local = FleetLocalTelemetrySettings.Read(Path.Combine(_options.FleetRoot, FleetLocalTelemetrySettings.FileName));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // A file the owner wrote and that does not read sends nothing rather than guess what it meant.
            Log(XenoLogLevel.Error, $"The telemetry file of the node does not read, so telemetry is off: {exception.Message}");
            local = new FleetLocalTelemetrySettings { Enabled = false };
        }

        var fleet = Chosen();
        var plan = FleetTelemetryPlanner.Resolve(local, _options.Variable, fleet?.Settings);
        bool changed;
        lock (_gate)
        {
            // The resource names the organization and the route names the server: a change of either builds the export again.
            changed = !Equals(_applied?.LinkId, fleet?.LinkId) || !Equals(_applied?.OrganizationId, fleet?.OrganizationId);
            _applied = fleet;
        }

        if (Runtime.Apply(plan, force: changed && plan.Enabled)) Describe(plan);
    }

    // The setting of the first link, in the order the links were made, that pushed one: a node linked to several servers follows the first.
    private Accepted? Chosen()
    {
        IReadOnlyList<FleetServerLink> links;
        try { links = _links.List(); }
        catch (InvalidDataException) { links = []; }
        foreach (var link in links)
        {
            if (link.State != FleetServerLinkStates.Revoked && _accepted.TryGetValue(link.Id, out var accepted)) return accepted;
        }

        return null;
    }

    private void LoadAccepted()
    {
        IReadOnlyList<FleetServerLink> links;
        try { links = _links.List(); }
        catch (InvalidDataException) { return; }
        foreach (var link in links)
        {
            try
            {
                // Verified when the server pushed it, and kept in the node's own state root since.
                var signed = JsonSerializer.Deserialize(File.ReadAllBytes(SettingsPath(link.Id)), FleetTelemetryJsonContext.Default.FleetSignedTelemetrySettings);
                if (signed is not null && FleetTelemetrySettingsDocuments.TryParse(signed.Payload, out var settings))
                    _accepted[link.Id] = new Accepted(link.Id, link.Address, link.OrganizationId ?? settings.OrganizationId, signed, settings);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException or IOException or InvalidDataException)
            {
            }
        }
    }

    private string SettingsPath(string linkId) => Path.Combine(_links.LinkFolder(linkId), SettingsFileName);

    private FleetOtlpPipelineOptions PipelineOptions(FleetTelemetryPlan plan)
    {
        Accepted? fleet;
        lock (_gate) fleet = _applied;
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal);
        if (fleet?.OrganizationId is { } organization) attributes["codealta.fleet.organization.id"] = organization;
        var server = plan.Source == FleetTelemetrySources.Fleet && plan.Route == FleetTelemetryRoutes.Server ? fleet?.Server : null;
        return new FleetOtlpPipelineOptions
        {
            Plan = plan,
            ServiceName = "altanode",
            ServiceVersion = _options.Version,
            InstanceId = _identity.DeviceId,
            Attributes = attributes,
            Sources = Sources,
            Meters = Meters,
            ExportInterval = _options.ExportInterval,
            // The server route: OTLP/HTTP to the server, each request signed with the key of the node, as its REST API takes them.
            RouteAddress = server is null ? null : signal => new Uri(server, FleetTelemetryPaths.Forward.TrimStart('/') + "/" + signal),
            HttpClientFactory = server is null ? null : () => new HttpClient(new FleetSignedRequestHandler(_identity, new SocketsHttpHandler())),
        };
    }

    private void Describe(FleetTelemetryPlan plan)
    {
        if (!plan.Enabled)
        {
            Log(XenoLogLevel.Info, "Telemetry: off.");
            return;
        }

        var signals = string.Join(", ", new[] { plan.Traces ? "traces" : null, plan.Metrics ? "metrics" : null, plan.Logs ? "logs" : null }.Where(static name => name is not null));
        Accepted? fleet;
        lock (_gate) fleet = _applied;
        var target = plan.Source == FleetTelemetrySources.Fleet && plan.Route == FleetTelemetryRoutes.Server
            ? $"{fleet?.Server} (the server forwards them to its collector)"
            : plan.Endpoint?.ToString() ?? "the collector of the OTEL_* variables";
        var message = $"Telemetry: {signals} to {target} ({plan.Source}).";
        Log(XenoLogLevel.Info, message);
        _log.LogInformation("{Message}", message);
    }

    private void Refused(FleetServerLink link, string? reason)
    {
        Metrics.TelemetrySetting(false);
        Log(XenoLogLevel.Warn, $"A telemetry setting from {link.Url} was refused: {reason}.");
    }

    private static void Log(XenoLogLevel level, string message)
    {
        if (!LogManager.IsInitialized) return;
        var logger = LogManager.GetLogger("CodeAlta.Node.Telemetry");
        switch (level)
        {
            case XenoLogLevel.Error: logger.Error(message); break;
            case XenoLogLevel.Warn: logger.Warn(message); break;
            default: logger.Info(message); break;
        }
    }

    private sealed record Accepted(string LinkId, Uri Server, string? OrganizationId, FleetSignedTelemetrySettings Signed, FleetTelemetrySettings Settings);
}

/// <summary>
/// A writer of the log of the node (XenoAtom.Logging) whose records also go to the OTLP export, when it sends logs:
/// the category, the level, the text and the exception of each record the node's log keeps, with the secrets of the
/// machine masked as the log file has them.
/// </summary>
internal sealed class NodeOtlpLogWriter : XenoAtom.Logging.LogWriter
{
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private readonly CodeAlta.Agent.Runtime.AgentSecretMasker? _masker;
    private FleetTelemetryRuntime? _runtime;

    /// <summary>Initializes a new instance of the <see cref="NodeOtlpLogWriter"/> class.</summary>
    /// <param name="runtime">The export the records go to.</param>
    /// <param name="masker">Masks the secrets of the machine in the text and the exception of each record; null for nothing.</param>
    public NodeOtlpLogWriter(FleetTelemetryRuntime runtime, CodeAlta.Agent.Runtime.AgentSecretMasker? masker = null)
    {
        _runtime = runtime;
        _masker = masker;
    }

    /// <summary>Stops sending records: the export ends.</summary>
    public void Detach() => Volatile.Write(ref _runtime, null);

    /// <summary>Sends a record to the export.</summary>
    /// <param name="category">The name of its logger.</param>
    /// <param name="level">Its level.</param>
    /// <param name="text">Its text.</param>
    /// <param name="exception">Its exception, or null.</param>
    internal void Write(string category, MicrosoftLogLevel level, string text, Exception? exception)
    {
        if (Volatile.Read(ref _runtime) is not { } runtime) return;
        var logger = _loggers.GetOrAdd(category, static (name, export) => export.CreateLogger(name), runtime);
        if (!logger.IsEnabled(level)) return;
        // The exporter writes the message, the type and the text of the exception (its stack and inner exceptions included).
        if (_masker is { } masker)
        {
            text = masker.MaskText(text);
            exception = masker.MaskException(exception);
        }

        logger.Log(level, default, text, exception, static (state, _) => state);
    }

    /// <inheritdoc />
    protected override void Log(LogMessage logMessage)
    {
        // The telemetry's own records stay in the file: a failing export must not feed itself.
        var category = logMessage.Logger.Name;
        if (category.StartsWith("CodeAlta.Node.Telemetry", StringComparison.Ordinal)) return;
        Write(category, Map(logMessage.Level), logMessage.Text.ToString(), logMessage.Exception);
    }

    private static MicrosoftLogLevel Map(XenoLogLevel level) => level switch
    {
        XenoLogLevel.Trace => MicrosoftLogLevel.Trace,
        XenoLogLevel.Debug => MicrosoftLogLevel.Debug,
        XenoLogLevel.Info => MicrosoftLogLevel.Information,
        XenoLogLevel.Warn => MicrosoftLogLevel.Warning,
        XenoLogLevel.Error => MicrosoftLogLevel.Error,
        XenoLogLevel.Fatal => MicrosoftLogLevel.Critical,
        _ => MicrosoftLogLevel.None,
    };
}
