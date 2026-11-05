// Compiled into altaserver and altanode (a linked file): the server may reference no CodeAlta library but the protocol,
// so the OTLP export of the two hosts of a fleet shares its source rather than an assembly.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CodeAlta.Fleet.Protocol;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CodeAlta.Fleet.Telemetry;

/// <summary>What a <see cref="FleetOtlpPipeline"/> exports and how.</summary>
internal sealed class FleetOtlpPipelineOptions
{
    /// <summary>Gets what is sent and where.</summary>
    public required FleetTelemetryPlan Plan { get; init; }

    /// <summary>Gets the <c>service.name</c>: <c>altaserver</c> or <c>altanode</c>.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Gets the <c>service.version</c>.</summary>
    public required string ServiceVersion { get; init; }

    /// <summary>Gets the <c>service.instance.id</c>: the device id of a node, the id of a server.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Gets more attributes of the resource, such as the organization of the fleet.</summary>
    public IReadOnlyDictionary<string, object> Attributes { get; init; } = new Dictionary<string, object>();

    /// <summary>Gets the names of the activity sources whose spans are exported.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Gets the names of the meters whose instruments are exported.</summary>
    public IReadOnlyList<string> Meters { get; init; } = [];

    /// <summary>Gets the address of a signal when the machine sends to its server (the server route); null sends to the collector of the plan.</summary>
    public Func<string, Uri>? RouteAddress { get; init; }

    /// <summary>Gets what makes the HTTP client of the exporters (one that signs each request with the device key on the server route); null for the usual one.</summary>
    public Func<HttpClient>? HttpClientFactory { get; init; }

    /// <summary>Gets what adds processors to the spans before they are exported (the server scrubs paths); null for none.</summary>
    public Action<TracerProviderBuilder>? ConfigureTraces { get; init; }

    /// <summary>Gets how often metrics are read and how long spans and logs wait in their batch; null for the defaults of the SDK.</summary>
    public TimeSpan? ExportInterval { get; init; }

    /// <summary>Gets the lowest level of the log records exported.</summary>
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;
}

/// <summary>
/// One OTLP export of traces, metrics and logs, built from a resolved <see cref="FleetTelemetryPlan"/>: a tracer
/// provider for the sources, a meter provider for the meters, and a logger factory whose records go to the collector.
/// A change of plan builds a new pipeline and disposes the old one, which sends what it holds first.
/// </summary>
/// <remarks>
/// Unless the plan comes from the machine's own owner (<see cref="FleetTelemetryPlan.AllowsContent"/>), the attributes
/// that can hold the content of a run (prompts, answers, tool arguments and results) are removed from every span before
/// it is exported, whatever the environment of the process asks.
/// </remarks>
internal sealed class FleetOtlpPipeline : IDisposable
{
    private const int ShutdownMilliseconds = 5_000;

    // The attributes of the GenAI conventions that hold content (AgentRunTrace records them only when asked).
    private static readonly string[] ContentAttributes = ["gen_ai.input.messages", "gen_ai.output.messages", "gen_ai.system_instructions", "gen_ai.tool.call.arguments",
        "gen_ai.tool.call.result", "gen_ai.prompt", "gen_ai.completion"];

    private readonly TracerProvider? _traces;
    private readonly MeterProvider? _metrics;
    private readonly ILoggerFactory? _logs;
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private int _disposed;

    private FleetOtlpPipeline(FleetTelemetryPlan plan, TracerProvider? traces, MeterProvider? metrics, ILoggerFactory? logs)
    {
        Plan = plan;
        _traces = traces;
        _metrics = metrics;
        _logs = logs;
    }

    /// <summary>Gets the plan the pipeline exports.</summary>
    public FleetTelemetryPlan Plan { get; }

    /// <summary>Builds the pipeline of a plan; null when the plan sends nothing.</summary>
    /// <param name="options">The plan and the resource.</param>
    /// <returns>The pipeline, or null.</returns>
    public static FleetOtlpPipeline? Start(FleetOtlpPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var plan = options.Plan;
        if (!plan.Enabled) return null;
        TracerProvider? traces = null;
        MeterProvider? metrics = null;
        ILoggerFactory? logs = null;
        try
        {
            if (plan.Traces && options.Sources.Count > 0)
            {
                var builder = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(Resource(options)).AddSource([.. options.Sources]);
                if (!plan.AllowsContent) builder.AddProcessor(new ContentFilter());
                options.ConfigureTraces?.Invoke(builder);
                traces = builder.AddOtlpExporter(exporter =>
                {
                    Configure(exporter, options, FleetTelemetrySignals.Traces);
                    if (options.ExportInterval is { } delay) exporter.BatchExportProcessorOptions.ScheduledDelayMilliseconds = Milliseconds(delay);
                }).Build();
            }

            if (plan.Metrics && options.Meters.Count > 0)
            {
                metrics = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(Resource(options)).AddMeter([.. options.Meters])
                    .AddOtlpExporter((exporter, reader) =>
                    {
                        Configure(exporter, options, FleetTelemetrySignals.Metrics);
                        if (options.ExportInterval is { } interval) reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = Milliseconds(interval);
                    }).Build();
            }

            if (plan.Logs)
            {
                logs = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(options.MinimumLogLevel);
                    builder.AddOpenTelemetry(logging =>
                    {
                        logging.SetResourceBuilder(Resource(options));
                        logging.IncludeFormattedMessage = true;
                        logging.IncludeScopes = false;
                        logging.AddOtlpExporter((exporter, processor) =>
                        {
                            Configure(exporter, options, FleetTelemetrySignals.Logs);
                            if (options.ExportInterval is { } delay) processor.BatchExportProcessorOptions.ScheduledDelayMilliseconds = Milliseconds(delay);
                        });
                    });
                });
            }
        }
        catch
        {
            traces?.Dispose();
            metrics?.Dispose();
            logs?.Dispose();
            throw;
        }

        return new FleetOtlpPipeline(plan, traces, metrics, logs);
    }

    /// <summary>Gets the logger of a category; null when the plan sends no log.</summary>
    /// <param name="category">The category, such as <c>CodeAlta.Server.Relay</c>.</param>
    /// <returns>The logger, or null.</returns>
    public ILogger? GetLogger(string category)
        => _logs is null || Volatile.Read(ref _disposed) != 0 ? null : _loggers.GetOrAdd(category, static (name, factory) => factory.CreateLogger(name), _logs);

    /// <summary>Sends what the pipeline holds now.</summary>
    public void Flush()
    {
        _traces?.ForceFlush(ShutdownMilliseconds);
        _metrics?.ForceFlush(ShutdownMilliseconds);
    }

    /// <summary>Sends what the pipeline holds, within a few seconds, and ends it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _traces?.Shutdown(ShutdownMilliseconds); } catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
        try { _metrics?.Shutdown(ShutdownMilliseconds); } catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
        _traces?.Dispose();
        _metrics?.Dispose();
        _logs?.Dispose();
    }

    /// <summary>Gets the value of <c>os.type</c> for this system, as the conventions name it.</summary>
    public static string OperatingSystemType
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsFreeBSD() ? "freebsd"
            : RuntimeInformation.OSDescription.Split(' ')[0].ToLowerInvariant();

    private static int Milliseconds(TimeSpan value) => (int)Math.Clamp(value.TotalMilliseconds, 50, int.MaxValue);

    private static void Configure(OtlpExporterOptions exporter, FleetOtlpPipelineOptions options, string signal)
    {
        var plan = options.Plan;
        if (options.HttpClientFactory is { } factory) exporter.HttpClientFactory = factory;
        if (options.RouteAddress is { } route)
        {
            // The server route: OTLP/HTTP to the server, which adds the collector's headers itself.
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            exporter.Endpoint = route(signal);
            exporter.Headers = null;
            return;
        }

        // The standard variables: the exporter reads its endpoint, protocol and headers from them.
        if (plan.Endpoint is not { } endpoint) return;
        exporter.Protocol = plan.Protocol == FleetTelemetryProtocols.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
        exporter.Endpoint = exporter.Protocol == OtlpExportProtocol.Grpc ? endpoint : FleetTelemetryPaths.SignalAddress(endpoint, signal);
        exporter.Headers = plan.Headers.Count == 0 ? null : string.Join(',', plan.Headers.Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    private static ResourceBuilder Resource(FleetOtlpPipelineOptions options)
    {
        // The default resource reads OTEL_SERVICE_NAME and OTEL_RESOURCE_ATTRIBUTES: a name the owner gave there stays.
        var resource = ResourceBuilder.CreateDefault();
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["service.instance.id"] = options.InstanceId,
            ["service.version"] = options.ServiceVersion,
            ["host.name"] = Environment.MachineName,
            ["os.type"] = OperatingSystemType,
        };
        if (!NamesService()) attributes["service.name"] = options.ServiceName;
        if (options.Plan.Environment is { Length: > 0 } environment)
        {
            attributes["deployment.environment.name"] = environment;
            // The name of the conventions before 1.27, which some collectors and vendors still read.
            attributes["deployment.environment"] = environment;
        }

        foreach (var (key, value) in options.Attributes) attributes[key] = value;
        return resource.AddAttributes(attributes);
    }

    private static bool NamesService()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME"))) return true;
        foreach (var attribute in (Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES") ?? string.Empty).Split(','))
        {
            var separator = attribute.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && attribute.AsSpan(0, separator).Trim().SequenceEqual("service.name")) return true;
        }

        return false;
    }

    // Removes the attributes that can hold content from a span before it is exported.
    private sealed class ContentFilter : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            foreach (var name in ContentAttributes)
            {
                if (data.GetTagItem(name) is not null) data.SetTag(name, null);
            }
        }
    }
}

/// <summary>
/// The telemetry of a host of the fleet while it runs: the pipeline of its current plan, replaced when the plan
/// changes (the fleet's setting arrives, the owner's file changes), and the <see cref="ILoggerProvider"/> whose
/// loggers write to the pipeline of the moment.
/// </summary>
internal sealed class FleetTelemetryRuntime : ILoggerProvider
{
    private readonly Func<FleetTelemetryPlan, FleetOtlpPipelineOptions> _options;
    private readonly Action<string>? _report;
    private readonly Lock _gate = new();
    private FleetOtlpPipeline? _current;
    private FleetTelemetryPlan _plan = FleetTelemetryPlan.Off;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="FleetTelemetryRuntime"/> class; nothing is exported before <see cref="Apply"/>.</summary>
    /// <param name="options">Gives the options of the pipeline of a plan (the resource and the instruments of the host).</param>
    /// <param name="report">Where a pipeline that cannot be built is reported; null for nowhere.</param>
    public FleetTelemetryRuntime(Func<FleetTelemetryPlan, FleetOtlpPipelineOptions> options, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _report = report;
    }

    /// <summary>Gets the plan applied.</summary>
    public FleetTelemetryPlan Plan { get { lock (_gate) return _plan; } }

    /// <summary>Applies a plan: builds its pipeline and ends the previous one, unless it is the same.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="force">Builds the pipeline again even for the same plan (the resource changed).</param>
    /// <returns>Whether the pipeline changed.</returns>
    public bool Apply(FleetTelemetryPlan plan, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        FleetOtlpPipeline? previous;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !force && plan.SameAs(_plan)) return false;
            FleetOtlpPipeline? next;
            try
            {
                next = FleetOtlpPipeline.Start(_options(plan));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or UriFormatException)
            {
                _report?.Invoke($"The telemetry to {plan.Endpoint?.ToString() ?? plan.Source} could not start: {exception.Message}");
                next = null;
                plan = FleetTelemetryPlan.Off;
            }

            previous = _current;
            _current = next;
            _plan = plan;
        }

        // The previous pipeline sends what it holds in the background: an apply never waits for a collector. Its end
        // blocks for up to a shutdown per signal, so it runs on a thread of its own: plans applied in a burst would
        // otherwise hold the threads of the pool, and the process (its HTTP server, its exports) would starve.
        if (previous is not null)
            _ = Task.Factory.StartNew(previous.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        return true;
    }

    /// <summary>Sends what the current pipeline holds now.</summary>
    public void Flush() => Volatile.Read(ref _current)?.Flush();

    /// <summary>Gets a logger that writes to the pipeline of the moment.</summary>
    /// <param name="categoryName">The category.</param>
    /// <returns>The logger.</returns>
    public ILogger CreateLogger(string categoryName) => new RuntimeLogger(this, categoryName);

    /// <summary>Ends the pipeline, once it sent what it holds.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        FleetOtlpPipeline? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
            _plan = FleetTelemetryPlan.Off;
        }

        current?.Dispose();
    }

    private ILogger? Current(string category) => Volatile.Read(ref _current)?.GetLogger(category);

    /// <summary>A logger of a category that finds the pipeline of the moment at each record.</summary>
    private sealed class RuntimeLogger(FleetTelemetryRuntime runtime, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && runtime.Current(category)?.IsEnabled(logLevel) == true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => runtime.Current(category)?.Log(logLevel, eventId, state, exception, formatter);
    }
}

/// <summary>Short forms of identifiers in telemetry: enough to tell devices apart, not to address them.</summary>
internal static class FleetTelemetryIds
{
    /// <summary>The first characters of a device id.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <returns>Its first 8 characters.</returns>
    public static string Short(string? deviceId) => string.IsNullOrEmpty(deviceId) ? "-" : deviceId.Length <= 8 ? deviceId : deviceId[..8];

    /// <summary>A number as invariant text.</summary>
    /// <param name="value">The number.</param>
    /// <returns>Its text.</returns>
    public static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
