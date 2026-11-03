using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>The OTLP protocols a collector is reached with.</summary>
public static class FleetTelemetryProtocols
{
    /// <summary>OTLP over HTTP with protobuf bodies (port 4318 by convention); the one the server forwards.</summary>
    public const string HttpProtobuf = "http/protobuf";

    /// <summary>OTLP over gRPC (port 4317 by convention); for a direct route only.</summary>
    public const string Grpc = "grpc";

    /// <summary>Gets whether a text is a known protocol.</summary>
    /// <param name="protocol">The text.</param>
    /// <returns>Whether it is <see cref="HttpProtobuf"/> or <see cref="Grpc"/>.</returns>
    public static bool IsKnown(string? protocol) => protocol is HttpProtobuf or Grpc;
}

/// <summary>How the machines of a fleet reach the collector.</summary>
public static class FleetTelemetryRoutes
{
    /// <summary>Each machine sends to the collector itself, with the headers of the setting.</summary>
    public const string Direct = "direct";

    /// <summary>
    /// Each machine sends to its server (<see cref="FleetTelemetryPaths.Forward"/>), which forwards the bytes to the
    /// collector with headers only the server holds: machines need no collector credential, nor a way out but the server.
    /// </summary>
    public const string Server = "server";

    /// <summary>Gets whether a text is a known route.</summary>
    /// <param name="route">The text.</param>
    /// <returns>Whether it is <see cref="Direct"/> or <see cref="Server"/>.</returns>
    public static bool IsKnown(string? route) => route is Direct or Server;
}

/// <summary>The signals of OpenTelemetry a setting turns on.</summary>
public static class FleetTelemetrySignals
{
    /// <summary>Spans.</summary>
    public const string Traces = "traces";

    /// <summary>Counters, gauges and histograms.</summary>
    public const string Metrics = "metrics";

    /// <summary>The records of the log of each machine: they may hold paths and errors, so they are off unless asked.</summary>
    public const string Logs = "logs";

    /// <summary>Gets the three signals, in their order.</summary>
    public static IReadOnlyList<string> All { get; } = new ReadOnlyCollection<string>([Traces, Metrics, Logs]);

    /// <summary>Gets whether a text is a known signal.</summary>
    /// <param name="signal">The text.</param>
    /// <returns>Whether it is one of <see cref="All"/>.</returns>
    public static bool IsKnown(string? signal) => signal is Traces or Metrics or Logs;
}

/// <summary>The paths of the OTLP forwarding of the server.</summary>
public static class FleetTelemetryPaths
{
    /// <summary>
    /// The prefix of the OTLP/HTTP endpoint of the server: <c>/otlp/v1/traces</c>, <c>/otlp/v1/metrics</c> and
    /// <c>/otlp/v1/logs</c>, which take requests signed by an approved device of the organization and forward their bytes.
    /// </summary>
    public const string Forward = "/otlp/v1";

    /// <summary>Gets the path of a signal on an OTLP/HTTP collector: <c>v1/traces</c>, <c>v1/metrics</c>, <c>v1/logs</c>.</summary>
    /// <param name="signal">The signal (<see cref="FleetTelemetrySignals"/>).</param>
    /// <returns>The relative path.</returns>
    /// <exception cref="ArgumentException">The signal is not known.</exception>
    public static string CollectorPath(string signal)
        => FleetTelemetrySignals.IsKnown(signal) ? "v1/" + signal : throw new ArgumentException($"'{signal}' is not a signal.", nameof(signal));

    /// <summary>Gets the address of a signal at a collector reached over OTLP/HTTP: the base address followed by <see cref="CollectorPath"/>.</summary>
    /// <param name="endpoint">The base address of the collector, such as <c>https://otlp.example:4318</c>.</param>
    /// <param name="signal">The signal.</param>
    /// <returns>The address of the signal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="ArgumentException">The signal is not known.</exception>
    public static Uri SignalAddress(Uri endpoint, string signal)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var builder = new UriBuilder(endpoint);
        builder.Path = builder.Path.TrimEnd('/') + "/" + CollectorPath(signal);
        return builder.Uri;
    }
}

/// <summary>
/// The telemetry setting of a fleet, signed by a controller device (an owner's desktop): where every machine sends its
/// traces, metrics and, when asked, logs. The server stores it and pushes it to the machines over their control
/// connection; a machine applies it only once it verified the signature against the device list it anchored, so that a
/// server alone cannot redirect what machines send.
/// </summary>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization the setting is for.</param>
/// <param name="Sequence">The number of the setting, from 1; a machine refuses one that is not newer than the one it holds.</param>
/// <param name="Enabled">Whether the machines send at all; false turns the fleet's telemetry off.</param>
/// <param name="Endpoint">The base address of the collector (<c>http://</c> or <c>https://</c>); null when disabled.</param>
/// <param name="Protocol">The protocol of the collector (<see cref="FleetTelemetryProtocols"/>).</param>
/// <param name="Route">How the machines reach it (<see cref="FleetTelemetryRoutes"/>).</param>
/// <param name="Signals">The signals sent (<see cref="FleetTelemetrySignals"/>).</param>
/// <param name="Headers">The headers of a direct route (API keys); null for the server route, whose headers only the server holds.</param>
/// <param name="Environment">The <c>deployment.environment.name</c> of the machines' resource, or null.</param>
/// <param name="SignerDeviceId">The controller device that signs the setting.</param>
/// <param name="Time">When the signer made it; for display, never for ordering.</param>
public sealed record FleetTelemetrySettings(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("signals")] IReadOnlyList<string> Signals,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers,
    [property: JsonPropertyName("environment")] string? Environment,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>A telemetry setting as it travels and is stored: the exact JSON text the signer signed, and the signature.</summary>
/// <param name="Payload">The JSON of a <see cref="FleetTelemetrySettings"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetTelemetrySettingsDocuments.GetSignedBytes"/>, r and s in base64url.</param>
public sealed record FleetSignedTelemetrySettings(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>Writes, reads, checks and verifies the telemetry settings of a fleet.</summary>
public static class FleetTelemetrySettingsDocuments
{
    /// <summary>The most headers a setting holds.</summary>
    public const int MaxHeaders = 16;

    private const string SignDomain = "codealta-telemetry-settings/1\n";
    private const int MaxEndpointLength = 2048;
    private const int MaxHeaderNameLength = 128;
    private const int MaxHeaderValueLength = 4096;
    private const int MaxEnvironmentLength = 64;

    // Headers the HTTP stack owns, and those of the signed requests of the fleet: a setting never sets them.
    private static readonly System.Collections.Frozen.FrozenSet<string> ReservedHeaders = System.Collections.Frozen.FrozenSet.ToFrozenSet(["host", "content-length", "content-type", "content-encoding", "transfer-encoding", "connection",
        "upgrade", "te", "trailer", "keep-alive", "expect", "cookie", "authority"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes the JSON text of a setting, the text a signer signs.</summary>
    /// <param name="settings">The setting.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static string Serialize(FleetTelemetrySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, FleetTelemetryJsonContext.Default.FleetTelemetrySettings);
    }

    /// <summary>Reads the JSON text of a setting.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="settings">The setting read, or null.</param>
    /// <returns>Whether the text is a setting with its required fields.</returns>
    public static bool TryParse(string? payload, [NotNullWhen(true)] out FleetTelemetrySettings? settings)
    {
        settings = null;
        if (string.IsNullOrEmpty(payload)) return false;
        try
        {
            settings = JsonSerializer.Deserialize(payload, FleetTelemetryJsonContext.Default.FleetTelemetrySettings);
        }
        catch (JsonException)
        {
            return false;
        }

        if (settings is null || string.IsNullOrEmpty(settings.OrganizationId) || string.IsNullOrEmpty(settings.SignerDeviceId) || settings.Signals is null
            || settings.Protocol is null || settings.Route is null)
        {
            settings = null;
            return false;
        }

        return true;
    }

    /// <summary>Gets the bytes a signer signs for a setting.</summary>
    /// <param name="payload">The JSON text of the setting.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(SignDomain + payload);
    }

    /// <summary>
    /// Checks what a setting says, apart from its signature: a known version, protocol, route and signals, a collector
    /// address when it is enabled (an OTLP/HTTP one for the server route, which forwards bytes), headers that HTTP lets a
    /// client set, and no header on the server route.
    /// </summary>
    /// <param name="settings">The setting.</param>
    /// <param name="reason">Why it does not hold, or null.</param>
    /// <returns>Whether it holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static bool TryValidate(FleetTelemetrySettings settings, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(settings);
        reason = Check(settings);
        return reason is null;
    }

    /// <summary>
    /// Verifies a signed setting for a machine: it reads, holds (<see cref="TryValidate"/>), names the organization, and is
    /// signed by a controller device of the verified device list, with the key the list holds for it.
    /// </summary>
    /// <param name="signed">The signed setting.</param>
    /// <param name="devices">The device list as the machine verified and anchored it.</param>
    /// <param name="organizationId">The organization of the server the machine is linked to.</param>
    /// <param name="settings">The setting, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public static bool TryVerify(FleetSignedTelemetrySettings? signed, FleetDeviceListState devices, string? organizationId,
        [NotNullWhen(true)] out FleetTelemetrySettings? settings, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(devices);
        settings = null;
        if (signed is null || !TryParse(signed.Payload, out var parsed))
        {
            reason = "the setting does not read";
            return false;
        }

        if (string.IsNullOrEmpty(organizationId) || !string.Equals(parsed.OrganizationId, organizationId, StringComparison.Ordinal)
            || devices.OrganizationId is { } listed && !string.Equals(listed, organizationId, StringComparison.Ordinal))
        {
            reason = "the setting is for another organization";
            return false;
        }

        if (!devices.Members.TryGetValue(parsed.SignerDeviceId, out var signer) || signer.Kind != FleetDeviceKinds.Controller
            || devices.Removed.Contains(parsed.SignerDeviceId))
        {
            reason = "the setting is not signed by a controller device of the organization";
            return false;
        }

        if (!signer.Key.Verify(GetSignedBytes(signed.Payload), signed.Signature))
        {
            reason = "the signature does not verify with the key of its signer";
            return false;
        }

        if (!TryValidate(parsed, out reason)) return false;
        settings = parsed;
        return true;
    }

    /// <summary>Checks the headers of a collector: names HTTP lets a client set, values on one line, at most <see cref="MaxHeaders"/>.</summary>
    /// <param name="headers">The headers; null for none.</param>
    /// <param name="reason">Why they do not hold, or null.</param>
    /// <returns>Whether they hold.</returns>
    public static bool TryValidateHeaders(IReadOnlyDictionary<string, string>? headers, [NotNullWhen(false)] out string? reason)
    {
        reason = CheckHeaders(headers);
        return reason is null;
    }

    /// <summary>Reads the base address of a collector: an absolute <c>http://</c> or <c>https://</c> address without user, query or fragment.</summary>
    /// <param name="text">The text.</param>
    /// <param name="endpoint">The address.</param>
    /// <returns>Whether the text is the address of a collector.</returns>
    public static bool TryParseEndpoint(string? text, [NotNullWhen(true)] out Uri? endpoint)
    {
        endpoint = null;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaxEndpointLength) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return false;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        endpoint = uri;
        return true;
    }

    private static string? Check(FleetTelemetrySettings settings)
    {
        if (settings.Version != FleetTelemetrySettings.CurrentVersion) return $"version {settings.Version.ToString(CultureInfo.InvariantCulture)} is not known";
        if (settings.Sequence <= 0) return "the sequence starts at 1";
        if (!FleetTelemetryProtocols.IsKnown(settings.Protocol)) return $"'{settings.Protocol}' is not a protocol of OTLP";
        if (!FleetTelemetryRoutes.IsKnown(settings.Route)) return $"'{settings.Route}' is not a route";
        if (settings.Signals.Count > FleetTelemetrySignals.All.Count || settings.Signals.Any(static signal => !FleetTelemetrySignals.IsKnown(signal))
            || settings.Signals.Distinct(StringComparer.Ordinal).Count() != settings.Signals.Count)
            return "the signals are traces, metrics and logs, each once";
        if (settings.Environment is { } environment && (environment.Length is 0 or > MaxEnvironmentLength || environment.Any(char.IsControl)))
            return $"the environment is 1 to {MaxEnvironmentLength} characters, without control characters";
        if (CheckHeaders(settings.Headers) is { } headers) return headers;
        if (!settings.Enabled) return null;
        if (!TryParseEndpoint(settings.Endpoint, out _)) return "an enabled setting names the http:// or https:// address of its collector";
        if (settings.Route == FleetTelemetryRoutes.Server)
        {
            if (settings.Protocol != FleetTelemetryProtocols.HttpProtobuf) return "the server forwards OTLP over HTTP only (http/protobuf)";
            if (settings.Headers is { Count: > 0 }) return "the headers of the server route stay with the server";
        }

        return null;
    }

    private static string? CheckHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return null;
        if (headers.Count > MaxHeaders) return $"a collector takes at most {MaxHeaders} headers";
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxHeaderNameLength || !name.All(IsTokenChar)) return $"'{name}' is not the name of a header";
            if (ReservedHeaders.Contains(name) || name.StartsWith("alta-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase))
                return $"the header '{name}' is not one a setting may set";
            // The OTLP exporters take their headers as "name=value,name=value": a value holds no comma.
            if (value is null || value.Length > MaxHeaderValueLength || value.Any(static c => c is '\r' or '\n' or '\0' or ','))
                return $"the value of the header '{name}' is at most {MaxHeaderValueLength} characters on one line, without a comma";
        }

        return null;
    }

    private static bool IsTokenChar(char c)
        => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}

/// <summary>
/// The machine's own telemetry setting (<c>telemetry.json</c> of the node's fleet folder or of the server's data root):
/// the owner's choice, which wins over the fleet's. Every field is optional.
/// </summary>
/// <remarks>
/// <c>{"enabled": false}</c> turns telemetry off whatever the fleet says; <c>{"fleet": false}</c> refuses the fleet's
/// setting; an <c>endpoint</c> sends to that collector instead of the fleet's.
/// </remarks>
public sealed record FleetLocalTelemetrySettings
{
    /// <summary>The name of the file.</summary>
    public const string FileName = "telemetry.json";

    /// <summary>Gets false to turn telemetry off on this machine; null or true lets the other levels decide.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>Gets false to refuse the setting of the fleet; null or true accepts it when nothing local decides.</summary>
    [JsonPropertyName("fleet")]
    public bool? AcceptFleet { get; init; }

    /// <summary>Gets the base address of a collector of the owner's choice, or null.</summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    /// <summary>Gets its protocol (<see cref="FleetTelemetryProtocols"/>); http/protobuf when null.</summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    /// <summary>Gets its headers, or null.</summary>
    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Gets the signals sent to it; traces and metrics when null.</summary>
    [JsonPropertyName("signals")]
    public IReadOnlyList<string>? Signals { get; init; }

    /// <summary>Gets the <c>deployment.environment.name</c> of this machine, which wins over the fleet's; null for the fleet's.</summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    /// <summary>Reads the file of a folder; a missing file is no setting.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The setting, or null when the file does not exist.</returns>
    /// <exception cref="InvalidDataException">The file exists but does not read, or what it says does not hold.</exception>
    public static FleetLocalTelemetrySettings? Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        FleetLocalTelemetrySettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize(bytes, FleetTelemetryJsonContext.Default.FleetLocalTelemetrySettings);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"'{path}' does not read: {exception.Message}", exception);
        }

        if (settings is null) return null;
        if (settings.Endpoint is not null && !FleetTelemetrySettingsDocuments.TryParseEndpoint(settings.Endpoint, out _))
            throw new InvalidDataException($"'{path}': '{settings.Endpoint}' is not the http:// or https:// address of a collector.");
        if (settings.Protocol is not null && !FleetTelemetryProtocols.IsKnown(settings.Protocol))
            throw new InvalidDataException($"'{path}': the protocol is '{FleetTelemetryProtocols.HttpProtobuf}' or '{FleetTelemetryProtocols.Grpc}'.");
        if (settings.Signals is { } signals && signals.Any(static signal => !FleetTelemetrySignals.IsKnown(signal)))
            throw new InvalidDataException($"'{path}': the signals are traces, metrics and logs.");
        if (!FleetTelemetrySettingsDocuments.TryValidateHeaders(settings.Headers, out var reason)) throw new InvalidDataException($"'{path}': {reason}.");
        return settings;
    }
}

/// <summary>Where a <see cref="FleetTelemetryPlan"/> comes from.</summary>
public static class FleetTelemetrySources
{
    /// <summary>Nothing is sent.</summary>
    public const string Off = "off";

    /// <summary>The machine's own file names the collector.</summary>
    public const string Local = "local";

    /// <summary>The standard <c>OTEL_*</c> variables of the process name the collector; the exporter reads them itself.</summary>
    public const string Environment = "environment";

    /// <summary>The signed setting of the fleet names the collector.</summary>
    public const string Fleet = "fleet";
}

/// <summary>
/// What a machine sends and where, once the three levels are resolved (<see cref="FleetTelemetryPlanner.Resolve"/>).
/// </summary>
/// <param name="Source">Where it comes from (<see cref="FleetTelemetrySources"/>).</param>
/// <param name="Endpoint">The base address of the collector; null for <see cref="FleetTelemetrySources.Environment"/> and when off.</param>
/// <param name="Protocol">The protocol of the collector.</param>
/// <param name="Headers">The headers sent to it.</param>
/// <param name="Traces">Whether spans are sent.</param>
/// <param name="Metrics">Whether metrics are sent.</param>
/// <param name="Logs">Whether log records are sent.</param>
/// <param name="Route">Whether a machine sends to the collector or to its server (<see cref="FleetTelemetryRoutes"/>).</param>
/// <param name="Environment">The <c>deployment.environment.name</c> of the resource, or null.</param>
/// <param name="FleetSequence">The sequence of the fleet's setting it comes from; 0 for another source.</param>
public sealed record FleetTelemetryPlan(
    string Source,
    Uri? Endpoint,
    string Protocol,
    IReadOnlyDictionary<string, string> Headers,
    bool Traces,
    bool Metrics,
    bool Logs,
    string Route,
    string? Environment,
    long FleetSequence)
{
    /// <summary>Gets the plan that sends nothing.</summary>
    public static FleetTelemetryPlan Off { get; } = new(FleetTelemetrySources.Off, null, FleetTelemetryProtocols.HttpProtobuf, ReadOnlyDictionary<string, string>.Empty,
        false, false, false, FleetTelemetryRoutes.Direct, null, 0);

    /// <summary>Gets whether anything is sent.</summary>
    public bool Enabled => Source != FleetTelemetrySources.Off && (Traces || Metrics || Logs);

    /// <summary>
    /// Gets whether the spans may carry the content of runs when the machine's own environment asks for it: only toward
    /// a collector the machine's owner named (its file or its variables), never toward the fleet's.
    /// </summary>
    public bool AllowsContent => Source is FleetTelemetrySources.Local or FleetTelemetrySources.Environment;

    /// <summary>Gets whether two plans send the same signals to the same place in the same way.</summary>
    /// <param name="other">The other plan.</param>
    /// <returns>Whether they are the same.</returns>
    public bool SameAs(FleetTelemetryPlan? other)
        => other is not null && Source == other.Source && Endpoint == other.Endpoint && Protocol == other.Protocol && Traces == other.Traces && Metrics == other.Metrics
           && Logs == other.Logs && Route == other.Route && Environment == other.Environment && Headers.Count == other.Headers.Count
           && Headers.All(pair => other.Headers.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

/// <summary>
/// Resolves the three levels of a machine's telemetry: its own file wins (it can turn telemetry off, refuse the fleet,
/// or name its own collector); then the standard <c>OTEL_*</c> variables of the process; then the signed setting of the
/// fleet. <c>OTEL_SDK_DISABLED=true</c> turns everything off.
/// </summary>
public static class FleetTelemetryPlanner
{
    /// <summary>Resolves what a machine sends.</summary>
    /// <param name="local">The machine's own file, or null.</param>
    /// <param name="variable">Gives the value of a variable of the environment, or null.</param>
    /// <param name="fleet">The fleet's setting the machine accepted, or null.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is null.</exception>
    public static FleetTelemetryPlan Resolve(FleetLocalTelemetrySettings? local, Func<string, string?> variable, FleetTelemetrySettings? fleet)
    {
        ArgumentNullException.ThrowIfNull(variable);
        if (local?.Enabled == false || IsTrue(variable("OTEL_SDK_DISABLED"))) return FleetTelemetryPlan.Off;
        var environment = local?.Environment;
        if (local?.Endpoint is { } endpoint && FleetTelemetrySettingsDocuments.TryParseEndpoint(endpoint, out var address))
        {
            var signals = local.Signals ?? [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Metrics];
            return new FleetTelemetryPlan(FleetTelemetrySources.Local, address, local.Protocol ?? FleetTelemetryProtocols.HttpProtobuf, Copy(local.Headers),
                signals.Contains(FleetTelemetrySignals.Traces), signals.Contains(FleetTelemetrySignals.Metrics), signals.Contains(FleetTelemetrySignals.Logs),
                FleetTelemetryRoutes.Direct, environment, 0);
        }

        if (!string.IsNullOrWhiteSpace(variable("OTEL_EXPORTER_OTLP_ENDPOINT")) || !string.IsNullOrWhiteSpace(variable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"))
            || !string.IsNullOrWhiteSpace(variable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT")) || !string.IsNullOrWhiteSpace(variable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT")))
        {
            // The standard variables: traces and metrics unless their exporter is "none"; logs only when asked for with "otlp".
            return new FleetTelemetryPlan(FleetTelemetrySources.Environment, null, FleetTelemetryProtocols.HttpProtobuf, ReadOnlyDictionary<string, string>.Empty,
                !IsNone(variable("OTEL_TRACES_EXPORTER")), !IsNone(variable("OTEL_METRICS_EXPORTER")),
                (variable("OTEL_LOGS_EXPORTER") ?? "").Split(',').Any(static value => value.Trim().Equals("otlp", StringComparison.OrdinalIgnoreCase)),
                FleetTelemetryRoutes.Direct, environment, 0);
        }

        if (local?.AcceptFleet == false || fleet is not { Enabled: true } || !FleetTelemetrySettingsDocuments.TryValidate(fleet, out _)
            || !FleetTelemetrySettingsDocuments.TryParseEndpoint(fleet.Endpoint, out var collector))
            return FleetTelemetryPlan.Off;
        return new FleetTelemetryPlan(FleetTelemetrySources.Fleet, collector, fleet.Protocol, Copy(fleet.Headers), fleet.Signals.Contains(FleetTelemetrySignals.Traces),
            fleet.Signals.Contains(FleetTelemetrySignals.Metrics), fleet.Signals.Contains(FleetTelemetrySignals.Logs), fleet.Route, environment ?? fleet.Environment, fleet.Sequence);
    }

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? headers)
        => headers is null || headers.Count == 0 ? ReadOnlyDictionary<string, string>.Empty
            : new ReadOnlyDictionary<string, string>(headers.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsNone(string? value) => string.Equals(value?.Trim(), "none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The source-generated contracts of the telemetry of a fleet.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = false)]
[JsonSerializable(typeof(FleetTelemetrySettings))]
[JsonSerializable(typeof(FleetSignedTelemetrySettings))]
[JsonSerializable(typeof(FleetLocalTelemetrySettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class FleetTelemetryJsonContext : JsonSerializerContext;
