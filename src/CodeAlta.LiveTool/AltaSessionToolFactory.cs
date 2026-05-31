using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.LiveTool;

/// <summary>
/// Creates the CodeAlta-managed <c>alta</c> session tool definition.
/// </summary>
public static class AltaSessionToolFactory
{
    private const string ToolName = "alta";
    private const string ToolDescription =
        "In-process gateway to the current CodeAlta host. " +
        "Use it to discover projects, sessions, providers/models, skills, plugins, and tool capabilities; " +
        "inspect live or recoverable session status/history; create project/global child sessions; " +
        "send, queue, steer, abort, compact, or coordinate sessions and peer-agent requests; " +
        "create/list/delete delayed prompt reminders; " +
        "and activate CodeAlta-managed skills. Pass CLI-style args excluding `alta`. " +
        "Start with args [\"--help\"] for the quick-start, then narrower help such as [\"session\",\"send\",\"--help\"] for options. " +
        "Calls are finite/non-streaming; non-help results are compact JSONL headed by alta.result.";
    private const string InputSchemaJson = """
        {
          "type": "object",
          "properties": {
            "args": {
              "type": "array",
              "description": "Arguments to pass to alta, excluding the executable name. Start with [\"--help\"] for discovery.",
              "items": { "type": "string" },
              "minItems": 1
            },
            "stdin": {
              "type": ["string", "null"],
              "description": "Optional stdin text for commands that explicitly read --stdin."
            },
            "cwd": {
              "type": ["string", "null"],
              "description": "Optional working directory used for project-relative command resolution."
            },
            "maxOutputRecords": {
              "type": ["integer", "null"],
              "description": "Optional positive cap on returned JSONL records after the alta.result header. Omit unless setting a positive integer.",
              "minimum": 1
            },
            "maxOutputBytes": {
              "type": ["integer", "null"],
              "description": "Optional positive cap on returned UTF-8 transcript bytes. Omit unless setting a positive integer.",
              "minimum": 1
            },
            "timeoutMs": {
              "type": ["integer", "null"],
              "description": "Optional positive timeout for this finite alta command invocation, in milliseconds. Omit unless setting a positive integer.",
              "minimum": 1
            }
          },
          "required": ["args"],
          "additionalProperties": false
        }
        """;

    /// <summary>
    /// Creates the <c>alta</c> agent tool definition.
    /// </summary>
    /// <param name="dispatcher">The in-process alta dispatcher.</param>
    /// <param name="options">Session-specific tool options.</param>
    /// <returns>The tool definition.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dispatcher"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static AgentToolDefinition Create(AltaCommandDispatcher dispatcher, AltaSessionToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);

        using var schemaDocument = JsonDocument.Parse(InputSchemaJson);
        var schema = schemaDocument.RootElement.Clone();
        return new AgentToolDefinition(
            new AgentToolSpec(
                ToolName,
                ToolDescription,
                schema),
            (invocation, cancellationToken) => InvokeAsync(dispatcher, options, invocation, cancellationToken));
    }

    private static async Task<AgentToolResult> InvokeAsync(
        AltaCommandDispatcher dispatcher,
        AltaSessionToolOptions options,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!TryReadArguments(invocation.Arguments, out var args, out var error))
        {
            return CreateArgumentError(error!);
        }

        if (!TryReadOptionalString(invocation.Arguments, "stdin", out var stdin, out error))
        {
            return CreateArgumentError(error!);
        }

        if (!TryReadOptionalString(invocation.Arguments, "cwd", out var cwd, out error))
        {
            return CreateArgumentError(error!);
        }

        var maxOutputRecords = ReadOptionalPositiveInt(invocation.Arguments, "maxOutputRecords", out error);
        if (error is not null)
        {
            return CreateArgumentError(error);
        }

        var maxOutputBytes = ReadOptionalPositiveInt(invocation.Arguments, "maxOutputBytes", out error);
        if (error is not null)
        {
            return CreateArgumentError(error);
        }

        var timeout = ReadTimeoutOverride(invocation.Arguments, out error);
        if (error is not null)
        {
            return CreateArgumentError(error);
        }

        using var timeoutCancellation = CreateTimeoutCancellationTokenSource(
            cancellationToken,
            timeout,
            options.DefaultTimeout);
        var effectiveCancellationToken = timeoutCancellation?.Token ?? cancellationToken;

        var caller = new AltaCallerIdentity
        {
            Kind = "agent",
            SourceSessionId = options.SourceSessionIdProvider?.Invoke() ?? options.SourceSessionId,
            SourceAgentId = options.SourceAgentId,
            SourceProjectId = options.SourceProjectIdProvider?.Invoke() ?? options.SourceProjectId,
            PluginRuntimeKey = options.PluginRuntimeKey,
        };
        var result = await dispatcher.InvokeAsync(
                args,
                stdin ?? string.Empty,
                caller,
                cwd ?? options.WorkingDirectoryProvider?.Invoke() ?? options.WorkingDirectory,
                maxOutputRecords ?? options.DefaultMaxOutputRecords,
                maxOutputBytes ?? options.DefaultMaxOutputBytes,
                effectiveCancellationToken)
            .ConfigureAwait(false);
        var transcript = result.Transcript;
        var errorMessage = result.ExitCode == AltaExitCodes.Success ? null : ExtractErrorMessage(result.Error) ?? result.Error;
        return new AgentToolResult(
            result.ExitCode == AltaExitCodes.Success,
            [new AgentToolResultItem.Text(transcript)],
            errorMessage);
    }

    private static bool TryReadArguments(JsonElement root, out IReadOnlyList<string> args, out string? error)
    {
        args = [];
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("args", out var argsElement) ||
            argsElement.ValueKind != JsonValueKind.Array)
        {
            error = "Tool argument 'args' must be a non-empty string array.";
            return false;
        }

        var values = new List<string>();
        foreach (var item in argsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = "Tool argument 'args' must contain only strings.";
                return false;
            }

            values.Add(item.GetString() ?? string.Empty);
        }

        if (values.Count == 0)
        {
            error = "Tool argument 'args' must contain at least one argument.";
            return false;
        }

        args = values;
        error = null;
        return true;
    }

    private static bool TryReadOptionalString(JsonElement root, string propertyName, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var element))
        {
            return true;
        }

        if (element.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        error = $"Tool argument '{propertyName}' must be a string or null.";
        return false;
    }

    private static int? ReadOptionalPositiveInt(JsonElement root, string propertyName, out string? error)
    {
        error = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value <= 0)
        {
            error = $"Tool argument '{propertyName}' must be a positive integer.";
            return null;
        }

        return value;
    }

    private static TimeSpan? ReadTimeoutOverride(JsonElement root, out string? error)
    {
        var timeoutMs = ReadOptionalPositiveInt(root, "timeoutMs", out error);
        if (error is not null)
        {
            return null;
        }

        // Compatibility for callers that used the initial pre-release field name before the spec was finalized.
        var timeoutSeconds = ReadOptionalPositiveInt(root, "timeoutSeconds", out error);
        if (error is not null)
        {
            return null;
        }

        if (timeoutMs is not null && timeoutSeconds is not null)
        {
            error = "Use either 'timeoutMs' or 'timeoutSeconds', not both.";
            return null;
        }

        error = null;
        return timeoutMs is { } milliseconds
            ? TimeSpan.FromMilliseconds(milliseconds)
            : timeoutSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : null;
    }

    private static AgentToolResult CreateArgumentError(string message)
    {
        var correlationId = AltaCommandDispatcher.CreateCorrelationId();
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        AltaJsonlWriter.WriteRecord(writer, AltaJsonlWriter.CreateResultRecord(
            correlationId,
            AltaExitCodes.Usage,
            truncated: false,
            recordCount: 0,
            diagnosticCount: 1,
            duration: TimeSpan.Zero));
        AltaJsonlWriter.WriteError(
            writer,
            correlationId,
            "usage.invalidToolArguments",
            AltaExitCodes.Usage,
            message,
            ToolName,
            "Call the alta tool with an args string array, for example {\"args\":[\"--help\"]}.");
        return new AgentToolResult(false, [new AgentToolResultItem.Text(writer.ToString())], message);
    }

    private static CancellationTokenSource? CreateTimeoutCancellationTokenSource(
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride,
        TimeSpan? defaultTimeout)
    {
        var timeout = timeoutOverride ?? defaultTimeout;
        if (timeout is null)
        {
            return null;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(timeout.Value);
        return cancellation;
    }

    private static string? ExtractErrorMessage(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(error);
            return document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : error;
        }
        catch (JsonException)
        {
            return error;
        }
    }
}

/// <summary>
/// Session-specific options for the <c>alta</c> agent tool.
/// </summary>
public sealed record AltaSessionToolOptions
{
    /// <summary>Gets the source CodeAlta session id, when known.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Gets a provider that returns the current source CodeAlta session id, when it can change after tool creation.</summary>
    public Func<string?>? SourceSessionIdProvider { get; init; }

    /// <summary>Gets the source agent/session id, when known.</summary>
    public string? SourceAgentId { get; init; }

    /// <summary>Gets the source project id, when known.</summary>
    public string? SourceProjectId { get; init; }

    /// <summary>Gets a provider that returns the current source project id, when it can change after tool creation.</summary>
    public Func<string?>? SourceProjectIdProvider { get; init; }

    /// <summary>Gets the invoking plugin runtime key, when the tool is exposed by a plugin.</summary>
    public string? PluginRuntimeKey { get; init; }

    /// <summary>Gets the working directory used as command context.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets a provider that returns the current working directory, when it can change after tool creation.</summary>
    public Func<string?>? WorkingDirectoryProvider { get; init; }

    /// <summary>Gets the default maximum output record count, when any.</summary>
    public int? DefaultMaxOutputRecords { get; init; }

    /// <summary>Gets the default maximum transcript byte count, when any.</summary>
    public int? DefaultMaxOutputBytes { get; init; }

    /// <summary>Gets the default command timeout, when any.</summary>
    public TimeSpan? DefaultTimeout { get; init; }
}
