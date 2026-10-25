using System.Text.Json;
using CodeAlta.Acp;
using static CodeAlta.Cli.CodeAltaAcpExtensions;

namespace CodeAlta.Cli;

/// <summary>
/// The progress of a run in text mode, on standard error: one short line per tool call, session started, compaction,
/// retry, fallback or error, read from the same ACP projection as <c>--format jsonl</c>. The deltas are not shown.
/// </summary>
internal sealed class CliProgressWriter(CliOutput output, string rootSessionId) : IAcpEventSink
{
    // The titles of the tool calls, to name a call that failed.
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask SessionUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken)
    {
        var value = update.Value;
        var prefix = Prefix(sessionId);
        switch (AcpJson.GetString(value, "sessionUpdate"))
        {
            case "tool_call":
                var title = AcpJson.GetString(value, "title") ?? "tool";
                if (AcpJson.GetString(value, "toolCallId") is { } id) _titles[id] = title;
                output.Error($"{prefix}> {title}{(AcpJson.GetString(value, "status") == "failed" ? " (failed)" : string.Empty)}");
                break;
            case "tool_call_update" when AcpJson.GetString(value, "status") == "failed":
                var failed = AcpJson.GetString(value, "toolCallId") is { } callId && _titles.TryGetValue(callId, out var known) ? known : "tool";
                output.Error($"{prefix}  {failed} failed");
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ExtensionAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        var prefix = Prefix(AcpJson.GetString(parameters, "sessionId"));
        var message = AcpJson.GetString(parameters, "message");
        switch (method)
        {
            case SessionStartedMethod:
                output.Error($"> session {AcpJson.GetString(parameters, "sessionId")} started{(AcpJson.GetString(parameters, "title") is { } title ? ": " + title : string.Empty)}");
                break;
            case CompactionMethod:
                output.Error($"{prefix}> compaction {AcpJson.GetString(parameters, "phase")}");
                break;
            case RetryMethod:
                output.Error($"{prefix}> retry{(message is null ? string.Empty : ": " + message)}");
                break;
            case FallbackMethod:
                output.Error($"{prefix}> fallback {AcpJson.GetString(parameters, "from")} -> {AcpJson.GetString(parameters, "to")}");
                break;
            case ErrorMethod:
                output.Error($"{prefix}error: {message}");
                break;
        }

        return ValueTask.CompletedTask;
    }

    // The lines of a session the run started name it.
    private string Prefix(string? sessionId)
        => sessionId is null || string.Equals(sessionId, rootSessionId, StringComparison.OrdinalIgnoreCase) ? string.Empty : $"[{Short(sessionId)}] ";

    private static string Short(string sessionId) => sessionId.Length <= 8 ? sessionId : sessionId[..8];
}
