using System.Collections;

namespace CodeAlta.Agent.Telemetry;

/// <summary>
/// The standard OpenTelemetry variables of the environment that decide whether CodeAlta exports its traces, and those it
/// hides from the agents it starts so that they do not report the same work a second time.
/// </summary>
public static class OpenTelemetryEnvironment
{
    /// <summary>The prefix of the variables of OpenTelemetry.</summary>
    public const string VariablePrefix = "OTEL_";

    /// <summary>The OTLP endpoint of every signal.</summary>
    public const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The OTLP endpoint of the traces.</summary>
    public const string TracesEndpointVariable = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";

    /// <summary>The variable that turns OpenTelemetry off when it is <c>true</c>.</summary>
    public const string SdkDisabledVariable = "OTEL_SDK_DISABLED";

    /// <summary>The name of the service of the traces.</summary>
    public const string ServiceNameVariable = "OTEL_SERVICE_NAME";

    /// <summary>The attributes of the resource of the traces, as <c>key=value</c> pairs separated by commas.</summary>
    public const string ResourceAttributesVariable = "OTEL_RESOURCE_ATTRIBUTES";

    /// <summary>
    /// The variable that asks the instrumentations of generative AI to record the content of the conversations: <c>true</c>
    /// (or <c>span_only</c>, <c>span_and_event</c>) records it on the spans.
    /// </summary>
    public const string CaptureMessageContentVariable = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    /// <summary>
    /// Gets whether the spans record the content of the runs: their prompts and answers, the arguments and the results of
    /// their tool calls. It is off unless <see cref="CaptureMessageContentVariable"/> asks for it on the spans: content
    /// recorded only as events (<c>event_only</c>) is not recorded at all, as CodeAlta exports no event.
    /// </summary>
    /// <param name="variable">Gives the value of a variable, or <see langword="null"/> when it is not set.</param>
    /// <returns><see langword="true"/> when the content is recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is <see langword="null"/>.</exception>
    public static bool CapturesMessageContent(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        var value = variable(CaptureMessageContentVariable)?.Trim();
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "span_only", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "span_and_event", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gets whether the environment asks for traces: an OTLP endpoint is set and OpenTelemetry is not turned off.</summary>
    /// <param name="variable">Gives the value of a variable, or <see langword="null"/> when it is not set.</param>
    /// <returns><see langword="true"/> when traces are exported.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is <see langword="null"/>.</exception>
    public static bool ExportsTraces(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        return (!string.IsNullOrWhiteSpace(variable(EndpointVariable)) || !string.IsNullOrWhiteSpace(variable(TracesEndpointVariable)))
               && !string.Equals(variable(SdkDisabledVariable)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the OpenTelemetry variables of this process that an agent it starts (an ACP agent, the Claude Code CLI, the
    /// Codex app-server) must not inherit: all of them when CodeAlta exports its traces, which already cover the runs of
    /// that agent; none otherwise. Shell commands, jobs and MCP servers keep them: what they report is their own work.
    /// </summary>
    /// <returns>The names of the variables.</returns>
    public static IReadOnlyList<string> VariablesHiddenFromAgents()
        => VariablesHiddenFromAgents(Environment.GetEnvironmentVariables());

    /// <summary>Gets the OpenTelemetry variables of an environment that an agent must not inherit (see <see cref="VariablesHiddenFromAgents()"/>).</summary>
    /// <param name="environment">The environment, as <see cref="Environment.GetEnvironmentVariables()"/> gives it.</param>
    /// <returns>The names of the variables.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> VariablesHiddenFromAgents(IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string name && name.StartsWith(VariablePrefix, StringComparison.OrdinalIgnoreCase))
            {
                values[name] = entry.Value as string;
            }
        }

        return ExportsTraces(name => values.GetValueOrDefault(name)) ? [.. values.Keys.Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>
    /// Removes the OpenTelemetry variables of this process from the environment of an agent about to start (see
    /// <see cref="VariablesHiddenFromAgents()"/>): each is given a <see langword="null"/> value, which removes an
    /// inherited variable, unless the environment already sets it (a variable the user gave the provider).
    /// </summary>
    /// <param name="environment">The variables set for the agent, where a <see langword="null"/> value removes one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is <see langword="null"/>.</exception>
    public static void HideFromAgent(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var name in VariablesHiddenFromAgents())
        {
            environment.TryAdd(name, null);
        }
    }
}
