using System.Reflection;
using CodeAlta.Agent.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CodeAlta.Hosting;

/// <summary>
/// The export of the traces of CodeAlta (see <see cref="AgentTelemetry"/>) to an OpenTelemetry collector, over OTLP, when
/// the environment names one: <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> or <c>OTEL_EXPORTER_OTLP_TRACES_ENDPOINT</c>, unless
/// <c>OTEL_SDK_DISABLED</c> is <c>true</c>. The exporter reads the other standard variables itself (its protocol, headers
/// and timeout, the service name and the attributes of the resource). Without them nothing is created, and the spans
/// of CodeAlta cost nothing.
/// </summary>
public sealed class CodeAltaTelemetry : IDisposable
{
    // How long the end of the application waits for the spans not yet sent: a short run (altacli in a pipeline) ends
    // right after its last span.
    private const int ShutdownTimeoutMilliseconds = 10_000;

    private readonly TracerProvider _provider;
    private int _disposed;

    private CodeAltaTelemetry(TracerProvider provider) => _provider = provider;

    /// <summary>Starts the export of the traces when the environment of the process asks for it.</summary>
    /// <param name="defaultServiceName">The name of the service when the environment names none: <c>alta</c>, <c>altatui</c> or <c>altacli</c>.</param>
    /// <returns>The export, to dispose when the application ends; <see langword="null"/> when the environment asks for none.</returns>
    /// <exception cref="ArgumentException"><paramref name="defaultServiceName"/> is <see langword="null"/>, empty or white space.</exception>
    public static CodeAltaTelemetry? StartFromEnvironment(string defaultServiceName)
        => StartFromEnvironment(defaultServiceName, Environment.GetEnvironmentVariable, configureExporter: null);

    /// <summary>Starts the export of the traces when the given environment asks for it.</summary>
    /// <param name="defaultServiceName">The name of the service when the environment names none.</param>
    /// <param name="variable">Gives the value of a variable of the environment, or <see langword="null"/>.</param>
    /// <param name="configureExporter">Changes the options of the exporter after it read the environment (a test sends to its own endpoint).</param>
    internal static CodeAltaTelemetry? StartFromEnvironment(string defaultServiceName, Func<string, string?> variable, Action<OtlpExporterOptions>? configureExporter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);
        ArgumentNullException.ThrowIfNull(variable);
        if (!OpenTelemetryEnvironment.ExportsTraces(variable))
        {
            return null;
        }

        // The default resource reads OTEL_SERVICE_NAME and OTEL_RESOURCE_ATTRIBUTES; the name of the application is the
        // service only when they name none, as a service added after them would replace theirs.
        var resource = ResourceBuilder.CreateDefault();
        if (!NamesService(variable))
        {
            resource.AddService(defaultServiceName, serviceVersion: Version);
        }

        var provider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(AgentTelemetry.SourceName)
            .AddOtlpExporter(options => configureExporter?.Invoke(options))
            .Build();
        return new CodeAltaTelemetry(provider);
    }

    /// <summary>Sends the spans not yet sent, within a few seconds, and ends the export.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _provider.Shutdown(ShutdownTimeoutMilliseconds);
        _provider.Dispose();
    }

    private static string? Version
        => typeof(CodeAltaTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];

    private static bool NamesService(Func<string, string?> variable)
    {
        if (!string.IsNullOrWhiteSpace(variable(OpenTelemetryEnvironment.ServiceNameVariable)))
        {
            return true;
        }

        foreach (var attribute in (variable(OpenTelemetryEnvironment.ResourceAttributesVariable) ?? string.Empty).Split(','))
        {
            var separator = attribute.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && attribute.AsSpan(0, separator).Trim().SequenceEqual("service.name"))
            {
                return true;
            }
        }

        return false;
    }
}
