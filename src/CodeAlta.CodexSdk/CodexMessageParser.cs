using System.IO;
using System.Text.Json;
using XenoAtom.Logging;

namespace CodeAlta.CodexSdk;

/// <summary>
/// Internal parser for server-initiated JSON-RPC messages (notifications and requests).
/// </summary>
internal static class CodexMessageParser
{
    internal static object? ParseServerMessage(
        string method,
        JsonElement parameters,
        RequestId? requestId,
        JsonSerializerOptions jsonOptions,
        Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (requestId is not null)
            return ParseServerRequest(method, parameters, requestId, jsonOptions, logger);

        return ParseNotification(method, parameters, jsonOptions, logger);
    }

    internal static object ParseServerRequest(
        string method,
        JsonElement parameters,
        RequestId requestId,
        JsonSerializerOptions jsonOptions,
        Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        try
        {
            var envelope = CreateServerRequestEnvelope(method, parameters, requestId);
            var request = envelope.Deserialize<ServerRequest>(jsonOptions);
            return request is not null
                ? request
                : new CodexUnknownServerRequest(requestId, method, parameters);
        }
        catch (JsonException)
        {
            LogParseFailure(logger, method, parameters, "Failed to deserialize Codex server request.");
            return new CodexUnknownServerRequest(requestId, method, parameters);
        }
        catch (NotSupportedException)
        {
            LogParseFailure(logger, method, parameters, "Failed to deserialize Codex server request.");
            return new CodexUnknownServerRequest(requestId, method, parameters);
        }
    }

    internal static CodexNotification? ParseNotification(
        string method,
        JsonElement parameters,
        JsonSerializerOptions jsonOptions,
        Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        try
        {
            return method switch
            {
            // Thread lifecycle
            "thread/started" => new CodexNotification.ThreadStarted(
                parameters.Deserialize<ThreadStartedNotification>(jsonOptions)!),
            "thread/archived" => new CodexNotification.ThreadArchived(
                parameters.Deserialize<ThreadArchivedNotification>(jsonOptions)!),
            "thread/unarchived" => new CodexNotification.ThreadUnarchived(
                parameters.Deserialize<ThreadUnarchivedNotification>(jsonOptions)!),
            "thread/name/updated" => new CodexNotification.ThreadNameUpdated(
                parameters.Deserialize<ThreadNameUpdatedNotification>(jsonOptions)!),
            "thread/status/changed" => new CodexNotification.ThreadStatusChanged(
                parameters.Deserialize<ThreadStatusChangedNotification>(jsonOptions)!),
            "thread/closed" => new CodexNotification.ThreadClosed(
                parameters.Deserialize<ThreadClosedNotification>(jsonOptions)!),

            // Thread realtime (experimental)
            "thread/realtime/started" => new CodexNotification.ThreadRealtimeStarted(
                parameters.Deserialize<ThreadRealtimeStartedNotification>(jsonOptions)!),
            "thread/realtime/itemAdded" => new CodexNotification.ThreadRealtimeItemAdded(
                parameters.Deserialize<ThreadRealtimeItemAddedNotification>(jsonOptions)!),
            "thread/realtime/outputAudio/delta" => new CodexNotification.ThreadRealtimeOutputAudioDelta(
                parameters.Deserialize<ThreadRealtimeOutputAudioDeltaNotification>(jsonOptions)!),
            "thread/realtime/error" => new CodexNotification.ThreadRealtimeError(
                parameters.Deserialize<ThreadRealtimeErrorNotification>(jsonOptions)!),
            "thread/realtime/closed" => new CodexNotification.ThreadRealtimeClosed(
                parameters.Deserialize<ThreadRealtimeClosedNotification>(jsonOptions)!),

            // Turn lifecycle
            "turn/started" => new CodexNotification.TurnStarted(
                parameters.Deserialize<TurnStartedNotification>(jsonOptions)!),
            "turn/completed" => new CodexNotification.TurnCompleted(
                parameters.Deserialize<TurnCompletedNotification>(jsonOptions)!),
            "turn/diff/updated" => new CodexNotification.TurnDiffUpdated(
                parameters.Deserialize<TurnDiffUpdatedNotification>(jsonOptions)!),
            "turn/plan/updated" => new CodexNotification.TurnPlanUpdated(
                parameters.Deserialize<TurnPlanUpdatedNotification>(jsonOptions)!),

            // Item lifecycle
            "item/started" => new CodexNotification.ItemStarted(
                parameters.Deserialize<ItemStartedNotification>(jsonOptions)!),
            "item/completed" => new CodexNotification.ItemCompleted(
                parameters.Deserialize<ItemCompletedNotification>(jsonOptions)!),
            "rawResponseItem/completed" => new CodexNotification.RawResponseItemCompleted(
                parameters.Deserialize<RawResponseItemCompletedNotification>(jsonOptions)!),

            // Agent message streaming
            "item/agentMessage/delta" => new CodexNotification.AgentMessageDelta(
                parameters.Deserialize<AgentMessageDeltaNotification>(jsonOptions)!),

            // Plan streaming (experimental)
            "item/plan/delta" => new CodexNotification.PlanDelta(
                parameters.Deserialize<PlanDeltaNotification>(jsonOptions)!),

            // Command execution streaming
            "item/commandExecution/outputDelta" => new CodexNotification.CommandExecutionOutputDelta(
                parameters.Deserialize<CommandExecutionOutputDeltaNotification>(jsonOptions)!),
            "item/commandExecution/terminalInteraction" => new CodexNotification.CommandExecutionTerminalInteraction(
                parameters.Deserialize<TerminalInteractionNotification>(jsonOptions)!),

            // File change streaming
            "item/fileChange/outputDelta" => new CodexNotification.FileChangeOutputDelta(
                parameters.Deserialize<FileChangeOutputDeltaNotification>(jsonOptions)!),

            // MCP tool call
            "item/mcpToolCall/progress" => new CodexNotification.McpToolCallProgress(
                parameters.Deserialize<McpToolCallProgressNotification>(jsonOptions)!),

            // Reasoning streaming
            "item/reasoning/summaryTextDelta" => new CodexNotification.ReasoningSummaryTextDelta(
                parameters.Deserialize<ReasoningSummaryTextDeltaNotification>(jsonOptions)!),
            "item/reasoning/summaryPartAdded" => new CodexNotification.ReasoningSummaryPartAdded(
                parameters.Deserialize<ReasoningSummaryPartAddedNotification>(jsonOptions)!),
            "item/reasoning/textDelta" => new CodexNotification.ReasoningTextDelta(
                parameters.Deserialize<ReasoningTextDeltaNotification>(jsonOptions)!),

            // Token usage
            "thread/tokenUsage/updated" => new CodexNotification.ThreadTokenUsageUpdated(
                parameters.Deserialize<ThreadTokenUsageUpdatedNotification>(jsonOptions)!),

            // Context compaction (deprecated)
            "thread/compacted" => new CodexNotification.ThreadCompacted(
                parameters.Deserialize<ContextCompactedNotification>(jsonOptions)!),

            // Account
            "account/updated" => new CodexNotification.AccountUpdated(
                parameters.Deserialize<AccountUpdatedNotification>(jsonOptions)!),
            "account/login/completed" => new CodexNotification.AccountLoginCompleted(
                parameters.Deserialize<AccountLoginCompletedNotification>(jsonOptions)!),
            "account/rateLimits/updated" => new CodexNotification.AccountRateLimitsUpdated(
                parameters.Deserialize<AccountRateLimitsUpdatedNotification>(jsonOptions)!),

            // App list (experimental)
            "app/list/updated" => new CodexNotification.AppListUpdated(
                parameters.Deserialize<AppListUpdatedNotification>(jsonOptions)!),

            // Error
            "error" => new CodexNotification.Error(
                parameters.Deserialize<ErrorNotification>(jsonOptions)!),

            // MCP OAuth
            "mcpServer/oauthLogin/completed" => new CodexNotification.McpServerOauthLoginCompleted(
                parameters.Deserialize<McpServerOauthLoginCompletedNotification>(jsonOptions)!),

            // Fuzzy file search (experimental)
            "fuzzyFileSearch/sessionUpdated" => new CodexNotification.FuzzyFileSearchSessionUpdated(
                parameters.Deserialize<FuzzyFileSearchSessionUpdatedNotification>(jsonOptions)!),
            "fuzzyFileSearch/sessionCompleted" => new CodexNotification.FuzzyFileSearchSessionCompleted(
                parameters.Deserialize<FuzzyFileSearchSessionCompletedNotification>(jsonOptions)!),

            // Windows sandbox setup
            "windowsSandbox/setupCompleted" => new CodexNotification.WindowsSandboxSetupCompleted(
                parameters.Deserialize<WindowsSandboxSetupCompletedNotification>(jsonOptions)!),

            // Server request lifecycle
            "serverRequest/resolved" => new CodexNotification.ServerRequestResolved(
                ParseServerRequestResolvedNotification(parameters)),

            // Config
            "configWarning" => new CodexNotification.ConfigWarning(
                parameters.Deserialize<ConfigWarningNotification>(jsonOptions)!),
            "deprecationNotice" => new CodexNotification.DeprecationNotice(
                parameters.Deserialize<DeprecationNoticeNotification>(jsonOptions)!),

            // Model reroute
            "model/rerouted" => new CodexNotification.ModelRerouted(
                parameters.Deserialize<ModelReroutedNotification>(jsonOptions)!),

            // Windows
            "windows/worldWritableWarning" => new CodexNotification.WindowsWorldWritableWarning(
                parameters.Deserialize<WindowsWorldWritableWarningNotification>(jsonOptions)!),

            // Catch-all
            _ => new CodexNotification.Unknown(method, parameters)
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            LogParseFailure(logger, method, parameters, "Failed to deserialize Codex notification.", ex);
            return new CodexNotification.Unknown(method, parameters);
        }
    }

    private static ServerRequestResolvedNotification ParseServerRequestResolvedNotification(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected object params for serverRequest/resolved.");

        if (!parameters.TryGetProperty("threadId", out var threadIdProp) || threadIdProp.ValueKind != JsonValueKind.String)
            throw new JsonException("serverRequest/resolved missing required 'threadId' string.");
        if (!parameters.TryGetProperty("requestId", out var requestIdProp))
            throw new JsonException("serverRequest/resolved missing required 'requestId'.");

        RequestId requestId = requestIdProp.ValueKind switch
        {
            JsonValueKind.Number => new RequestId.IntegerValue { Value = requestIdProp.GetInt64() },
            JsonValueKind.String => new RequestId.StringValue { Value = requestIdProp.GetString()! },
            _ => throw new JsonException("serverRequest/resolved 'requestId' must be a number or string.")
        };

        return new ServerRequestResolvedNotification
        {
            ThreadId = threadIdProp.GetString()!,
            RequestId = requestId
        };
    }

    private static JsonElement CreateServerRequestEnvelope(
        string method,
        JsonElement parameters,
        RequestId requestId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            writer.WritePropertyName("id");
            JsonSerializer.Serialize(writer, requestId, CodexJsonSerializerContext.Default.RequestId);
            writer.WritePropertyName("params");
            parameters.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void LogParseFailure(
        Logger? logger,
        string method,
        JsonElement parameters,
        string message,
        Exception? exception = null)
    {
        if (logger is null || !logger.IsEnabled(LogLevel.Error))
        {
            return;
        }

        var payload = parameters.ValueKind == JsonValueKind.Undefined
            ? "<undefined>"
            : parameters.GetRawText();
        var trimmedPayload = payload.Length <= 2048 ? payload : payload[..2048] + "...";
        if (exception is null)
        {
            logger.Error($"{message} Method: {method}. Payload: {trimmedPayload}");
        }
        else
        {
            logger.Error(exception, $"{message} Method: {method}. Payload: {trimmedPayload}");
        }
    }
}
