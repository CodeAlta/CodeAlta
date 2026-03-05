using System.Text.Json;

namespace CodeAlta.CodexSdk;

/// <summary>
/// Internal parser for server-initiated JSON-RPC messages (notifications and requests).
/// </summary>
internal static class CodexMessageParser
{
    internal static object? ParseServerMessage(
        string method,
        JsonElement parameters,
        long? requestId,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (requestId is { } id)
            return ParseServerRequest(method, parameters, id, jsonOptions);

        return ParseNotification(method, parameters, jsonOptions);
    }

    internal static CodexServerRequest ParseServerRequest(
        string method,
        JsonElement parameters,
        long requestId,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        return method switch
        {
            "item/commandExecution/requestApproval" =>
                new CodexServerRequest.CommandExecutionApproval(
                    requestId,
                    parameters.Deserialize<CommandExecutionRequestApprovalParams>(jsonOptions)!),
            "item/fileChange/requestApproval" =>
                new CodexServerRequest.FileChangeApproval(
                    requestId,
                    parameters.Deserialize<FileChangeRequestApprovalParams>(jsonOptions)!),
            "item/tool/requestUserInput" =>
                new CodexServerRequest.ToolRequestUserInput(
                    requestId,
                    parameters.Deserialize<ToolRequestUserInputParams>(jsonOptions)!),
            "item/tool/call" =>
                new CodexServerRequest.ToolCall(
                    requestId,
                    parameters.Deserialize<DynamicToolCallParams>(jsonOptions)!),
            "account/chatgptAuthTokens/refresh" =>
                new CodexServerRequest.ChatgptAuthTokensRefresh(
                    requestId,
                    parameters.Deserialize<ChatgptAuthTokensRefreshParams>(jsonOptions)!),
            _ => new CodexServerRequest.UnknownRequest(requestId, method, parameters)
        };
    }

    internal static CodexNotification? ParseNotification(
        string method,
        JsonElement parameters,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(jsonOptions);

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
}
