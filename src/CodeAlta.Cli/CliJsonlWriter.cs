using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Acp;
using CodeAlta.Orchestration.Runtime;
using static CodeAlta.Cli.CodeAltaAcpExtensions;

namespace CodeAlta.Cli;

/// <summary>How a run ended, as <c>altacli</c> reports it in every format.</summary>
/// <param name="Status"><c>completed</c>, <c>failed</c>, <c>aborted</c>, <c>timeout</c>, <c>provider_not_ready</c>, <c>session_in_use</c>, <c>invalid_output</c> or <c>invalid_usage</c>.</param>
/// <param name="ExitCode">The exit code of the process (<see cref="CliExitCodes"/>).</param>
internal sealed record CliRunReport(string Status, int ExitCode)
{
    public string? SessionId { get; init; }

    public string? RunId { get; init; }

    public string? FinalMessage { get; init; }

    public string? AbortReason { get; init; }

    public string? FailureKind { get; init; }

    public string? FailureMessage { get; init; }

    public SessionUsageTotals? Usage { get; init; }

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// The output of <c>--format jsonl</c>: standard output carries the JSON-RPC 2.0 notifications an ACP agent sends over
/// stdio, one per line ended by a line feed, without <c>id</c>. The schema is ACP's; what ACP has no notification for
/// uses the custom notifications of <see cref="CodeAltaAcpExtensions"/>, whose methods start with <c>_codealta/</c>.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The first line is <see cref="CodeAltaAcpExtensions.SessionMethod"/>, the header of the run: <c>schemaVersion</c> (1), <c>sessionId</c>,
/// <c>created</c>, <c>cwd</c>, <c>provider</c>, <c>model</c>, <c>reasoning</c>, <c>agentPrompt</c>, <c>tier</c>,
/// <c>storage</c>. A run that ends before it has a session (invalid usage, a provider not ready, a session in use) has no
/// header.</item>
/// <item>Then the <c>session/update</c> notifications (<c>params</c> is a <see cref="SessionNotification"/>) and the
/// extension notifications of <see cref="AcpEventProjector"/>, in the order of the events of the run.</item>
/// <item>The last line is always <see cref="CodeAltaAcpExtensions.ResultMethod"/>: <c>sessionId</c>, <c>runId</c>, <c>stopReason</c>
/// (<c>end_turn</c> for completed, <c>cancelled</c> for aborted and timeout, null otherwise), <c>status</c>, <c>exitCode</c>,
/// <c>finalMessage</c>, <c>abortReason</c>, <c>failure</c> (<c>kind</c>, <c>message</c>) or null, <c>usage</c> in the shape of
/// ACP's <see cref="Usage"/> (<c>inputTokens</c>, <c>outputTokens</c>, <c>totalTokens</c>, <c>cachedReadTokens</c>,
/// <c>cachedWriteTokens</c>, <c>thoughtTokens</c>) or null, <c>cost</c> in the shape of ACP's <see cref="Cost"/>
/// (<c>amount</c>, <c>currency</c>, which is the unit the provider reports, such as <c>USD</c>) or null when it is unknown,
/// and <c>durationMs</c>.</item>
/// </list>
/// </remarks>
internal sealed class CliJsonlWriter : IAcpEventSink
{
    private readonly CliOutput _output;
    // The source-generated contract of ACP, as its client serializes: no reflection.
    private readonly JsonTypeInfo<SessionNotification> _notificationInfo;
    private readonly JsonTypeInfo<Usage> _usageInfo;
    private readonly JsonTypeInfo<Cost> _costInfo;
    private int _closed;

    public CliJsonlWriter(CliOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        var options = AcpClient.CreateJsonSerializerOptions();
        _notificationInfo = (JsonTypeInfo<SessionNotification>)options.GetTypeInfo(typeof(SessionNotification));
        _usageInfo = (JsonTypeInfo<Usage>)options.GetTypeInfo(typeof(Usage));
        _costInfo = (JsonTypeInfo<Cost>)options.GetTypeInfo(typeof(Cost));
    }

    /// <inheritdoc />
    public ValueTask SessionUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken)
    {
        Write("session/update", writer => JsonSerializer.Serialize(writer, new SessionNotification { SessionId = sessionId, Update = update }, _notificationInfo));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ExtensionAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        Write(method, parameters.WriteTo);
        return ValueTask.CompletedTask;
    }

    /// <summary>Writes the header of the run.</summary>
    public void WriteHeader(CliSessionHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        Write(SessionMethod, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("sessionId", header.SessionId);
            writer.WriteBoolean("created", header.Created);
            writer.WriteString("cwd", header.Cwd);
            writer.WriteString("provider", header.Provider);
            writer.WriteString("model", header.Model);
            AcpEventProjector.WriteStringOrNull(writer, "reasoning", header.Reasoning);
            writer.WriteString("agentPrompt", header.AgentPrompt);
            AcpEventProjector.WriteStringOrNull(writer, "tier", header.Tier);
            writer.WriteString("storage", header.Storage);
            writer.WriteEndObject();
        });
    }

    /// <summary>Writes the result of the run, the last line: nothing is written after it.</summary>
    public void WriteResult(CliRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Write(ResultMethod, writer =>
        {
            writer.WriteStartObject();
            AcpEventProjector.WriteStringOrNull(writer, "sessionId", report.SessionId);
            AcpEventProjector.WriteStringOrNull(writer, "runId", report.RunId);
            AcpEventProjector.WriteStringOrNull(writer, "stopReason", report.Status switch
            {
                "completed" => "end_turn",
                "aborted" or "timeout" => "cancelled",
                _ => null,
            });
            writer.WriteString("status", report.Status);
            writer.WriteNumber("exitCode", report.ExitCode);
            AcpEventProjector.WriteStringOrNull(writer, "finalMessage", report.FinalMessage);
            AcpEventProjector.WriteStringOrNull(writer, "abortReason", report.AbortReason);
            if (report.FailureMessage is null && report.FailureKind is null)
            {
                writer.WriteNull("failure");
            }
            else
            {
                writer.WriteStartObject("failure");
                writer.WriteString("kind", report.FailureKind ?? "other");
                AcpEventProjector.WriteStringOrNull(writer, "message", report.FailureMessage);
                writer.WriteEndObject();
            }

            writer.WritePropertyName("usage");
            if (report.Usage is { } usage)
            {
                JsonSerializer.Serialize(writer, new Usage
                {
                    InputTokens = (ulong)Math.Max(0, usage.InputTokens),
                    OutputTokens = (ulong)Math.Max(0, usage.OutputTokens),
                    TotalTokens = (ulong)Math.Max(0, usage.TotalTokens),
                    CachedReadTokens = (ulong)Math.Max(0, usage.CachedInputTokens),
                    CachedWriteTokens = (ulong)Math.Max(0, usage.CacheWriteTokens),
                    ThoughtTokens = (ulong)Math.Max(0, usage.ReasoningTokens),
                }, _usageInfo);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WritePropertyName("cost");
            // A cost the provider did not report is unknown, never zero.
            if (report.Usage is { Cost: { } amount } known)
            {
                JsonSerializer.Serialize(writer, new Cost { Amount = amount, Currency = known.CostUnit ?? string.Empty }, _costInfo);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteNumber("durationMs", (long)Math.Round(report.Duration.TotalMilliseconds));
            writer.WriteEndObject();
        });
        Volatile.Write(ref _closed, 1);
    }

    // One notification, one line; nothing after the result.
    private void Write(string method, Action<Utf8JsonWriter> writeParameters)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            writeParameters(writer);
            writer.WriteEndObject();
        }

        _output.WriteLine(buffer.WrittenSpan);
    }
}

/// <summary>The header of a run: the session and what it runs with.</summary>
internal sealed record CliSessionHeader(string SessionId, bool Created, string Cwd, string Provider, string Model, string? Reasoning,
    string AgentPrompt, string? Tier, string Storage);
