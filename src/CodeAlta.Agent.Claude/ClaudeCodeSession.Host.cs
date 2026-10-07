using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

// What the session answers the CLI while a turn runs: the results of its tools, its permission prompts, its
// questions, its hooks, and the calls of the tools of CodeAlta.
internal sealed partial class ClaudeCodeSession
{
    /// <summary>A tool call of the main conversation of the CLI (not one of a subagent).</summary>
    private sealed class ToolCallState(string id, string name, bool isNative, JsonElement arguments)
    {
        public string Id { get; } = id;

        /// <summary>The name the session of CodeAlta knows the tool by.</summary>
        public string Name { get; } = name;

        /// <summary>Whether the tool is one of CodeAlta, which the session of CodeAlta runs.</summary>
        public bool IsNative { get; } = isNative;

        public JsonElement Arguments { get; } = arguments;

        /// <summary>The result the CLI gave for the call.</summary>
        public TaskCompletionSource<AgentToolResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The request of the CLI to run the tool of CodeAlta.</summary>
        public TaskCompletionSource<McpToolCall> McpCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool McpMatched { get; set; }

        /// <summary>The session of CodeAlta started to run the call: it read the files the call changes.</summary>
        public bool HandlerStarted { get; set; }

        /// <summary>The hook of the CLI that waits for <see cref="HandlerStarted"/>.</summary>
        public string? HookRequestId { get; set; }

        public AgentToolProgressHandler? Progress { get; set; }

        /// <summary>What a subagent wrote before the session of CodeAlta started to run the call.</summary>
        public List<AgentToolProgressUpdate> BufferedProgress { get; } = [];

        public Task ProgressTail { get; set; } = Task.CompletedTask;
    }

    private sealed record McpToolCall(string ControlRequestId, JsonElement? JsonRpcId, string Name, JsonElement Arguments);

    private sealed class ConnectionHandler(ClaudeCodeSession session, int generation, ChannelWriter<TurnEvent> events) : IClaudeCodeConnectionHandler
    {
        // A process that was replaced can still write: it is not listened to any more.
        private bool IsCurrent => Volatile.Read(ref session._generation) == generation;

        public void OnMessage(string type, JsonElement message)
        {
            if (IsCurrent && session.OnMessage(type, message))
            {
                events.TryWrite(new MessageEvent(type, message));
            }
        }

        public void OnControlRequest(string requestId, string subtype, JsonElement request)
        {
            if (IsCurrent && session.OnControlRequest(requestId, subtype, request))
            {
                events.TryWrite(new ToolActivityEvent());
            }
        }

        public void OnControlCancel(string requestId)
        {
            if (IsCurrent)
            {
                session.CancelPermissionPrompt(requestId);
            }
        }

        public void OnClosed(Exception? failure)
        {
            if (IsCurrent)
            {
                session.ReleaseAllHookGates();
                session.FailOutstandingToolCalls("Claude Code stopped before the tool call completed.");
                session.CancelPermissionPrompts();
            }

            events.TryWrite(new ClosedEvent(failure));
            events.TryComplete();
        }
    }

    private readonly Dictionary<string, ToolCallState> _toolCalls = new(StringComparer.Ordinal);
    private readonly List<ToolCallState> _toolCallOrder = [];
    private readonly Dictionary<string, CancellationTokenSource> _permissionPrompts = new(StringComparer.Ordinal);
    private bool _stateEventsSeen;
    private bool _cliRunning;
    private bool _turnActive;

    // Whether the CLI is running a turn, as far as this side can tell.
    private bool IsCliBusy
    {
        get
        {
            lock (_gate)
            {
                return _stateEventsSeen ? _cliRunning : _turnActive;
            }
        }
    }

    /// <summary>
    /// Returns the definition that runs a tool call of a message of this session: the result of the CLI for one
    /// of its own tools, or the tool of CodeAlta whose result is given back to the CLI.
    /// </summary>
    public AgentToolDefinition? ResolveTool(AgentMessagePart.ToolCall toolCall, AgentToolDefinition? registered)
    {
        ToolCallState? state;
        lock (_gate)
        {
            _toolCalls.TryGetValue(toolCall.CallId, out state);
        }

        if (state is null)
        {
            return null;
        }

        var spec = registered is not null && state.IsNative
            ? registered.Spec
            : new AgentToolSpec(SanitizeToolName(toolCall.Name), "A tool run by Claude Code.", ClaudeCodeJson.EmptyObject);
        return state.IsNative
            ? new AgentToolDefinition(spec, (invocation, cancellationToken) => RunNativeToolAsync(state, registered, invocation, cancellationToken))
            : new AgentToolDefinition(spec, (invocation, cancellationToken) => AwaitCliToolAsync(state, invocation, cancellationToken));
    }

    // A tool of Claude Code: the CLI runs it, this waits for its result.
    private async Task<AgentToolResult> AwaitCliToolAsync(ToolCallState state, AgentToolInvocation invocation, CancellationToken cancellationToken)
    {
        AttachProgress(state, invocation.Progress);
        MarkHandlerStarted(state);
        try
        {
            var result = await state.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task progress;
            lock (_gate)
            {
                progress = state.ProgressTail;
            }

            // What the subagent wrote is shown before the result of the call.
            await progress.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await InterruptAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                state.Progress = null;
            }
        }
    }

    // A tool of CodeAlta: the CLI asks for it through the MCP server, the session of CodeAlta runs it.
    private async Task<AgentToolResult> RunNativeToolAsync(
        ToolCallState state,
        AgentToolDefinition? registered,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        MarkHandlerStarted(state);
        McpToolCall? call = null;
        try
        {
            var first = await Task.WhenAny(state.McpCall.Task, state.Result.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (first == state.Result.Task && !state.McpCall.Task.IsCompletedSuccessfully)
            {
                // The CLI answered the call itself: a rule or a hook of the user denied it.
                return await state.Result.Task.ConfigureAwait(false);
            }

            call = await state.McpCall.Task.ConfigureAwait(false);
            AgentToolResult result;
            try
            {
                result = registered is null
                    ? Failure($"Tool '{state.Name}' is not available in this session.")
                    : await registered.Handler(invocation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = Failure($"Tool '{state.Name}' failed: {ex.Message}");
            }

            await RespondToolResultAsync(call, result).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (call is not null)
            {
                await RespondToolResultAsync(call, Failure("The tool call was interrupted.")).ConfigureAwait(false);
            }

            await InterruptAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Called by the reader of the connection. Returns whether the message is part of the answer of the turn.
    private bool OnMessage(string type, JsonElement message)
    {
        if (ClaudeCodeJson.GetString(message, "session_id") is { Length: > 0 } sessionId &&
            type is "system" or "result" or "assistant")
        {
            // A command of the CLI (`/clear`) can replace the conversation: the next resume follows it.
            _claudeSessionId = sessionId;
        }

        var isTopLevel = ClaudeCodeJson.IsNullOrMissing(message, "parent_tool_use_id");
        switch (type)
        {
            case "assistant":
                if (!isTopLevel)
                {
                    ReportSubagentProgress(message);
                    return false;
                }

                RegisterToolCalls(message);
                return true;
            case "user":
                if (ClaudeCodeJson.GetBoolean(message, "isReplay"))
                {
                    return false;
                }

                return CompleteToolResults(message) && isTopLevel;
            case "stream_event":
                return isTopLevel;
            case "result":
                lock (_gate)
                {
                    _turnActive = false;
                }

                // A tool call without a result when the turn ends will never have one.
                FailOutstandingToolCalls("The tool call did not complete.");
                ReleaseAllHookGates();
                return true;
            case "system":
                if (string.Equals(ClaudeCodeJson.GetString(message, "subtype"), "session_state_changed", StringComparison.Ordinal))
                {
                    lock (_gate)
                    {
                        _stateEventsSeen = true;
                        _cliRunning = !string.Equals(ClaudeCodeJson.GetString(message, "state"), "idle", StringComparison.Ordinal);
                    }
                }

                return true;
            default:
                return true;
        }
    }

    private void RegisterToolCalls(JsonElement message)
    {
        if (!ClaudeCodeJson.TryGetObject(message, "message", out var body) ||
            !ClaudeCodeJson.TryGetArray(body, "content", out var content))
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (!string.Equals(ClaudeCodeJson.GetString(block, "type"), "tool_use", StringComparison.Ordinal) ||
                ClaudeCodeJson.GetString(block, "id") is not { Length: > 0 } id ||
                ClaudeCodeJson.GetString(block, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            var arguments = block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                ? input
                : ClaudeCodeJson.EmptyObject;
            var isNative = name.StartsWith(ClaudeCodeLauncher.McpToolPrefix, StringComparison.Ordinal);
            lock (_gate)
            {
                if (_toolCalls.ContainsKey(id))
                {
                    continue;
                }

                var state = new ToolCallState(id, isNative ? name[ClaudeCodeLauncher.McpToolPrefix.Length..] : name, isNative, arguments);
                _toolCalls[id] = state;
                _toolCallOrder.Add(state);
            }
        }
    }

    // Returns whether the message held the result of a tool call of the main conversation.
    private bool CompleteToolResults(JsonElement message)
    {
        if (!ClaudeCodeJson.TryGetObject(message, "message", out var body) ||
            !ClaudeCodeJson.TryGetArray(body, "content", out var content))
        {
            return false;
        }

        var completed = false;
        foreach (var block in content.EnumerateArray())
        {
            if (!string.Equals(ClaudeCodeJson.GetString(block, "type"), "tool_result", StringComparison.Ordinal) ||
                ClaudeCodeJson.GetString(block, "tool_use_id") is not { Length: > 0 } id)
            {
                continue;
            }

            ToolCallState? state;
            lock (_gate)
            {
                _toolCalls.TryGetValue(id, out state);
            }

            if (state is null)
            {
                continue;
            }

            completed = true;
            state.Result.TrySetResult(ReadToolResult(block));
        }

        return completed;
    }

    private static AgentToolResult ReadToolResult(JsonElement block)
    {
        var isError = ClaudeCodeJson.GetBoolean(block, "is_error");
        var items = new List<AgentToolResultItem>();
        if (block.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                if (content.GetString() is { Length: > 0 } text)
                {
                    items.Add(new AgentToolResultItem.Text(text));
                }
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    switch (ClaudeCodeJson.GetString(item, "type"))
                    {
                        case "text" when ClaudeCodeJson.GetString(item, "text") is { Length: > 0 } text:
                            items.Add(new AgentToolResultItem.Text(text));
                            break;
                        case "image" when ClaudeCodeJson.TryGetObject(item, "source", out var source) &&
                                          ClaudeCodeJson.GetString(source, "data") is { Length: > 0 } data:
                            items.Add(new AgentToolResultItem.Image(data, ClaudeCodeJson.GetString(source, "media_type") ?? "image/png"));
                            break;
                    }
                }
            }
        }

        var error = isError ? ClaudeCodePrompts.RenderToolResult(new AgentToolResult(false, items)) : null;
        return new AgentToolResult(!isError, items, string.IsNullOrWhiteSpace(error) ? (isError ? "The tool call failed." : null) : error);
    }

    // What a subagent writes is shown under the tool call that started it.
    private void ReportSubagentProgress(JsonElement message)
    {
        if (ClaudeCodeJson.GetString(message, "parent_tool_use_id") is not { } parentId ||
            !ClaudeCodeJson.TryGetObject(message, "message", out var body) ||
            !ClaudeCodeJson.TryGetArray(body, "content", out var content))
        {
            return;
        }

        ToolCallState? parent;
        lock (_gate)
        {
            _toolCalls.TryGetValue(parentId, out parent);
        }

        if (parent is null)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            var line = ClaudeCodeJson.GetString(block, "type") switch
            {
                "text" => ClaudeCodeJson.GetString(block, "text"),
                "tool_use" => DescribeToolUse(block),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var update = new AgentToolProgressUpdate(line.TrimEnd() + "\n");
            lock (_gate)
            {
                if (parent.Progress is { } progress)
                {
                    parent.ProgressTail = ChainProgress(parent.ProgressTail, progress, update);
                }
                else if (parent.BufferedProgress.Count < MaxBufferedProgress)
                {
                    parent.BufferedProgress.Add(update);
                }
            }
        }
    }

    private const int MaxBufferedProgress = 256;

    private void AttachProgress(ToolCallState state, AgentToolProgressHandler? progress)
    {
        lock (_gate)
        {
            state.Progress = progress;
            if (progress is not null)
            {
                foreach (var update in state.BufferedProgress)
                {
                    state.ProgressTail = ChainProgress(state.ProgressTail, progress, update);
                }
            }

            state.BufferedProgress.Clear();
        }
    }

    // The updates of one tool call are delivered one after the other, off the reader of the connection.
    private Task ChainProgress(Task tail, AgentToolProgressHandler progress, AgentToolProgressUpdate update)
        => tail.ContinueWith(
            async _ =>
            {
                try
                {
                    await progress(update, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();

    private static string DescribeToolUse(JsonElement block)
    {
        var name = ClaudeCodeJson.GetString(block, "name") ?? "tool";
        if (!ClaudeCodeJson.TryGetObject(block, "input", out var input))
        {
            return $"[{name}]";
        }

        foreach (var property in (ReadOnlySpan<string>)["command", "file_path", "pattern", "url", "query", "description", "prompt"])
        {
            if (ClaudeCodeJson.GetString(input, property) is { Length: > 0 } value)
            {
                var firstLine = value.AsSpan().Trim();
                var newLine = firstLine.IndexOfAny('\r', '\n');
                if (newLine >= 0)
                {
                    firstLine = firstLine[..newLine];
                }

                return $"[{name}] {(firstLine.Length > 160 ? firstLine[..160].ToString() + " …" : firstLine.ToString())}";
            }
        }

        return $"[{name}]";
    }

    // Called by the reader of the connection. Returns whether the request proves that the message that made a
    // tool call of the main conversation is complete.
    private bool OnControlRequest(string requestId, string subtype, JsonElement request)
    {
        switch (subtype)
        {
            case "can_use_tool":
                _ = AnswerAsync(requestId, HandlePermissionAsync(requestId, request));
                return IsKnownToolCall(ClaudeCodeJson.GetString(request, "tool_use_id"));
            case "hook_callback":
                return HandleHook(requestId, request);
            case "mcp_message":
                return HandleMcpMessage(requestId, request);
            default:
                // The CLI is told, instead of being left waiting, when it asks for something this side does not do.
                _ = SendSafelyAsync(connection => connection.RespondErrorAsync(requestId, $"CodeAlta does not handle the '{subtype}' request."));
                return false;
        }
    }

    private bool IsKnownToolCall(string? toolUseId)
    {
        if (toolUseId is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _toolCalls.ContainsKey(toolUseId);
        }
    }

    private ToolCallState? GetToolCall(string toolUseId)
    {
        lock (_gate)
        {
            return _toolCalls.GetValueOrDefault(toolUseId);
        }
    }

    // The CLI asks before it edits a file. The answer waits until the session of CodeAlta runs the call, which is
    // when it reads the file to show the change afterwards. It never decides: the permissions of the CLI do.
    private bool HandleHook(string requestId, JsonElement request)
    {
        ClaudeCodeJson.TryGetObject(request, "input", out var input);
        var toolUseId = ClaudeCodeJson.GetString(request, "tool_use_id") ?? ClaudeCodeJson.GetString(input, "tool_use_id");
        var isSubagent = ClaudeCodeJson.GetString(input, "agent_id") is { Length: > 0 };
        var waits = false;
        lock (_gate)
        {
            if (toolUseId is not null &&
                !isSubagent &&
                _toolCalls.TryGetValue(toolUseId, out var state) &&
                !state.HandlerStarted &&
                state.HookRequestId is null)
            {
                state.HookRequestId = requestId;
                waits = true;
            }
        }

        if (!waits)
        {
            _ = SendSafelyAsync(connection => connection.RespondAsync(requestId, null));
            return false;
        }

        _ = ReleaseHookGateLaterAsync(toolUseId!);
        return true;
    }

    private async Task ReleaseHookGateLaterAsync(string toolUseId)
    {
        try
        {
            await Task.Delay(HookGateTimeout, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        ReleaseHookGate(toolUseId);
    }

    private void MarkHandlerStarted(ToolCallState state)
    {
        lock (_gate)
        {
            state.HandlerStarted = true;
        }

        ReleaseHookGate(state.Id);
    }

    private void ReleaseHookGate(string toolUseId)
    {
        string? requestId;
        lock (_gate)
        {
            if (!_toolCalls.TryGetValue(toolUseId, out var state) || state.HookRequestId is null)
            {
                return;
            }

            requestId = state.HookRequestId;
            state.HookRequestId = null;
        }

        _ = SendSafelyAsync(connection => connection.RespondAsync(requestId, null));
    }

    private void ReleaseAllHookGates()
    {
        string[] waiting;
        lock (_gate)
        {
            waiting = [.. _toolCalls.Values.Where(static state => state.HookRequestId is not null).Select(static state => state.Id)];
        }

        foreach (var toolUseId in waiting)
        {
            ReleaseHookGate(toolUseId);
        }
    }

    private void FailOutstandingToolCalls(string message)
    {
        ToolCallState[] outstanding;
        lock (_gate)
        {
            outstanding = [.. _toolCalls.Values.Where(static state => !state.Result.Task.IsCompleted)];
        }

        foreach (var state in outstanding)
        {
            state.Result.TrySetResult(Failure(message));
        }
    }

    private static AgentToolResult Failure(string message)
        => new(false, [new AgentToolResultItem.Text(message)], message);

    private static string SanitizeToolName(string name)
    {
        var valid = true;
        foreach (var ch in name)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-')
            {
                valid = false;
                break;
            }
        }

        if (valid && name.Length > 0)
        {
            return name;
        }

        var sanitized = string.Create(name.Length, name, static (span, source) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                var ch = source[index];
                span[index] = char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_';
            }
        });
        return sanitized.Length == 0 ? "tool" : sanitized;
    }

    private async Task AnswerAsync(string requestId, Task<Action<Utf8JsonWriter>?> answer)
    {
        try
        {
            var write = await answer.ConfigureAwait(false);
            if (write is not null)
            {
                await SendSafelyAsync(connection => connection.RespondAsync(requestId, write)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await SendSafelyAsync(connection => connection.RespondErrorAsync(requestId, ex.Message)).ConfigureAwait(false);
        }
    }

    // An answer to a process that is gone is dropped: the turn already failed with the reason.
    private async Task SendSafelyAsync(Func<ClaudeCodeConnection, ValueTask> send)
    {
        ClaudeCodeConnection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        if (connection is null)
        {
            return;
        }

        try
        {
            await send(connection).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}
