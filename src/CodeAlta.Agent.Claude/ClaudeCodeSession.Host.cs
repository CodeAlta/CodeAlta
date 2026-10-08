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

        /// <summary>
        /// Ends when the CLI starts to run the call: it asked something about it, a subagent it started wrote, or
        /// it gave its result. The CLI runs some calls of one message at the same time.
        /// </summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The session of CodeAlta was given the call, with the message that made it.</summary>
        public bool Resolved { get; set; }

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
            if (IsCurrent && session.OnMessage(type, message) is { } @event)
            {
                events.TryWrite(@event);
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
                session.ForgetBackgroundTasks();
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

    // What the reader of the connection knows of the turns of the CLI. A turn answers the user messages its
    // result names. A turn in which the model writes while no user message waits for its answer is one the CLI
    // started by itself: a background command of an earlier turn ended, a wake-up of its own fired. Such turns
    // are numbered, so that the run that reads their result knows it read them.
    private readonly HashSet<string> _unansweredUserMessages = new(StringComparer.Ordinal);
    private bool _ownTurnOpen;
    private int _ownTurnsStarted;
    private int _ownTurnsRead;
    private string? _ownTurnNotice;
    private string? _taskSummary;
    private Action? _onOwnTurn;
    private TaskCompletionSource? _runForOwnTurn;
    private bool _showsOwnTurn;
    private bool _ownTurnRunStarts;

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
    /// Gets the message of the run that shows a turn the CLI started by itself and that no run has read yet, or
    /// <see langword="null" /> when there is none.
    /// </summary>
    public string? PendingOwnTurn
    {
        get
        {
            lock (_gate)
            {
                return _ownTurnsRead < _ownTurnsStarted ? _ownTurnNotice ?? ClaudeCodePrompts.CreateOwnTurnNotice(null) : null;
            }
        }
    }

    /// <summary>
    /// Registers what is called when the CLI starts a turn by itself: a run is then started that reads it.
    /// </summary>
    public IDisposable OnOwnTurn(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _onOwnTurn = handler;
        }

        return new OwnTurnRegistration(this, handler);
    }

    private sealed class OwnTurnRegistration(ClaudeCodeSession session, Action handler) : IDisposable
    {
        public void Dispose()
        {
            TaskCompletionSource? waiting = null;
            lock (session._gate)
            {
                if (ReferenceEquals(session._onOwnTurn, handler))
                {
                    session._onOwnTurn = null;
                    // No run will come for a prompt that waits for one.
                    waiting = session._runForOwnTurn;
                    session._runForOwnTurn = null;
                }
            }

            waiting?.TrySetResult();
        }
    }

    // Called by the reader of the connection when the model writes in the main conversation.
    private void NoteModelOutput()
    {
        Action? notify;
        lock (_gate)
        {
            if (_ownTurnOpen || _unansweredUserMessages.Count > 0)
            {
                return;
            }

            _ownTurnOpen = true;
            _turnActive = true;
            _ownTurnsStarted++;
            _ownTurnNotice = ClaudeCodePrompts.CreateOwnTurnNotice(_taskSummary);
            _taskSummary = null;
            notify = _onOwnTurn;
            if (notify is not null)
            {
                // What the CLI asks in this turn is answered by the run that will show it.
                _runForOwnTurn ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        notify?.Invoke();
    }

    // Called by the reader of the connection for the result that ends a turn. Returns the number of the turn the
    // CLI started by itself that it ends, or zero for a turn that answers user messages.
    private int EndTurn(JsonElement result)
    {
        lock (_gate)
        {
            _turnActive = false;
            var ownTurn = _ownTurnOpen ? _ownTurnsStarted : 0;
            _ownTurnOpen = false;
            _taskSummary = null;
            if (ClaudeCodeJson.TryGetArray(result, "user_message_uuids", out var uuids))
            {
                foreach (var uuid in uuids.EnumerateArray())
                {
                    if (uuid.ValueKind == JsonValueKind.String)
                    {
                        _unansweredUserMessages.Remove(uuid.GetString()!);
                    }
                }
            }
            else if (ownTurn == 0 && !NamesItsOrigin(result))
            {
                // A CLI that does not name the messages of a turn runs them in one turn.
                _unansweredUserMessages.Clear();
            }

            return ownTurn;
        }
    }

    // Whether the CLI says what started the turn of a result when it was not a user message (a task notification).
    private static bool NamesItsOrigin(JsonElement result)
        => result.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object;

    // What the CLI did by itself is given up: its process ended, or what it wrote was withdrawn with a turn.
    private void ForgetOwnTurns()
    {
        TaskCompletionSource? waiting;
        lock (_gate)
        {
            _ownTurnOpen = false;
            _ownTurnsRead = _ownTurnsStarted;
            _ownTurnNotice = null;
            _taskSummary = null;
            waiting = _runForOwnTurn;
            _runForOwnTurn = null;
        }

        waiting?.TrySetResult();
    }

    // Whether the process still has something to do without a run: a turn of its own that goes on or that no run
    // read yet, or a background command whose end starts one.
    private bool WorksByItself
    {
        get
        {
            lock (_gate)
            {
                return _connection is { IsClosed: false } &&
                       ((_stateEventsSeen ? _cliRunning : _turnActive) || _taskSet.Count > 0 || _ownTurnsRead < _ownTurnsStarted);
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
        var hookWaits = false;
        lock (_gate)
        {
            if (_toolCalls.TryGetValue(toolCall.CallId, out state))
            {
                state.Resolved = true;
                hookWaits = state.HookRequestId is not null;
            }
        }

        if (state is null)
        {
            return null;
        }

        if (hookWaits)
        {
            // The CLI already waits to edit the file: the session runs the call now.
            _ = ReleaseHookGateLaterAsync(state.Id, HookGateTimeout);
        }

        var spec = registered is not null && state.IsNative
            ? registered.Spec
            : new AgentToolSpec(SanitizeToolName(toolCall.Name), "A tool run by Claude Code.", ClaudeCodeJson.EmptyObject);
        return state.IsNative
            ? new AgentToolDefinition(spec, (invocation, cancellationToken) => RunNativeToolAsync(state, registered, invocation, cancellationToken))
            : new AgentToolDefinition(spec, (invocation, cancellationToken) => AwaitCliToolAsync(state, invocation, cancellationToken));
    }

    /// <summary>
    /// Returns the task that ends when the CLI starts to run a tool call of a message of this session, or
    /// <see langword="null" /> for a call this session does not know.
    /// </summary>
    public Task? WhenToolStarts(AgentMessagePart.ToolCall toolCall)
        => GetToolCall(toolCall.CallId)?.Started.Task;

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

    // Called by the reader of the connection. Returns the message when it is part of the answer of a turn.
    private MessageEvent? OnMessage(string type, JsonElement message)
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
                    return null;
                }

                RegisterToolCalls(message);
                NoteModelOutput();
                return new MessageEvent(type, message);
            case "user":
                if (ClaudeCodeJson.GetBoolean(message, "isReplay"))
                {
                    return null;
                }

                return CompleteToolResults(message) && isTopLevel ? new MessageEvent(type, message) : null;
            case "stream_event":
                if (!isTopLevel)
                {
                    return null;
                }

                NoteModelOutput();
                return new MessageEvent(type, message);
            case "result":
                var ownTurn = EndTurn(message);

                // A tool call without a result when the turn ends will never have one.
                FailOutstandingToolCalls("The tool call did not complete.");
                ReleaseAllHookGates();
                return new MessageEvent(type, message) { OwnTurn = ownTurn };
            case "system":
                switch (ClaudeCodeJson.GetString(message, "subtype"))
                {
                    case "session_state_changed":
                        lock (_gate)
                        {
                            _stateEventsSeen = true;
                            _cliRunning = !string.Equals(ClaudeCodeJson.GetString(message, "state"), "idle", StringComparison.Ordinal);
                            return new MessageEvent(type, message) { Unanswered = _unansweredUserMessages.Count > 0 };
                        }
                    case "background_tasks_changed":
                    case "task_started":
                    case "task_updated":
                        // The commands and the subagents that go on in the background, whatever turn started them.
                        ReadBackgroundTaskMessage(ClaudeCodeJson.GetString(message, "subtype"), message);
                        break;
                    case "task_notification":
                        // What ended, as the CLI says it. It starts a turn of its own when no turn is running.
                        lock (_gate)
                        {
                            _taskSummary = ClaudeCodeJson.GetString(message, "summary");
                        }

                        ReadBackgroundTaskMessage("task_notification", message);
                        break;
                }

                return new MessageEvent(type, message);
            default:
                return new MessageEvent(type, message);
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
            state.Started.TrySetResult();
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

        parent.Started.TrySetResult();
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
                if (ClaudeCodeJson.GetString(request, "tool_use_id") is not { } askedFor || GetToolCall(askedFor) is not { } asked)
                {
                    return false;
                }

                asked.Started.TrySetResult();
                return true;
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

    private ToolCallState? GetToolCall(string toolUseId)
    {
        lock (_gate)
        {
            return _toolCalls.GetValueOrDefault(toolUseId);
        }
    }

    // The CLI asks before it runs a tool: the call starts. Before it edits a file, the answer waits until the
    // session of CodeAlta runs the call, which is when it reads the file to show the change afterwards. The answer
    // never decides: the permissions of the CLI do.
    private bool HandleHook(string requestId, JsonElement request)
    {
        if (string.Equals(ClaudeCodeJson.GetString(request, "callback_id"), PlanModeHookId, StringComparison.Ordinal))
        {
            _ = SendSafelyAsync(connection => connection.RespondAsync(requestId, static writer =>
            {
                writer.WriteStartObject("hookSpecificOutput");
                writer.WriteString("hookEventName", "PreToolUse");
                writer.WriteString("permissionDecision", "deny");
                writer.WriteString(
                    "permissionDecisionReason",
                    "The plan mode of Claude Code is not used in a CodeAlta session: the approval that ends it cannot be asked of the user here. " +
                    $"Go on without it. When the user wants planning only, the plan mode of CodeAlta is the one its instructions describe (`{ClaudeCodePrompts.GatewayTool} session set_agent --prompt-id plan`).");
                writer.WriteEndObject();
            }));
            return false;
        }

        ClaudeCodeJson.TryGetObject(request, "input", out var input);
        var toolUseId = ClaudeCodeJson.GetString(request, "tool_use_id") ?? ClaudeCodeJson.GetString(input, "tool_use_id");
        var isSubagent = ClaudeCodeJson.GetString(input, "agent_id") is { Length: > 0 };
        ToolCallState? state = null;
        var waits = false;
        var resolved = false;
        lock (_gate)
        {
            if (toolUseId is not null && !isSubagent && _toolCalls.TryGetValue(toolUseId, out state) &&
                state is { IsNative: false, Name: "Edit" or "MultiEdit" or "Write" or "NotebookEdit", HandlerStarted: false, HookRequestId: null })
            {
                state.HookRequestId = requestId;
                waits = true;
                resolved = state.Resolved;
            }
        }

        state?.Started.TrySetResult();
        if (!waits)
        {
            _ = SendSafelyAsync(connection => connection.RespondAsync(requestId, null));
            return state is not null;
        }

        // The CLI may ask while the model still writes the message of the call: the session of CodeAlta is given
        // the call with the whole message, and runs it then.
        _ = ReleaseHookGateLaterAsync(toolUseId!, resolved ? HookGateTimeout : UnresolvedHookGateTimeout);
        return true;
    }

    private async Task ReleaseHookGateLaterAsync(string toolUseId, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, _lifetime.Token).ConfigureAwait(false);
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
            state.Started.TrySetResult();
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
