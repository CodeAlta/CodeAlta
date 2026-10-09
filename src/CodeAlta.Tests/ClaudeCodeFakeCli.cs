using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodeAlta.Agent.Claude;

namespace CodeAlta.Tests;

/// <summary>
/// A scripted Claude Code CLI: it answers the control requests as the CLI does and runs the turn a test wrote
/// when it receives a user message. The messages have the shape the CLI 2.1 writes.
/// </summary>
internal sealed class ClaudeCodeFakeCli : IClaudeCodeTransportFactory
{
    private readonly ConcurrentQueue<ClaudeCodeFakeProcess> _processes = new();
    private readonly ConcurrentDictionary<string, double> _totalCosts = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the turn run for each user message. The default answers "ok".</summary>
    public Func<ClaudeCodeFakeProcess, JsonElement, Task> OnUserMessage { get; set; } = static (process, message) =>
    {
        process.EmitTextTurn("msg_1", "ok", message);
        return Task.CompletedTask;
    };

    /// <summary>Gets or sets a value indicating whether the CLI has a way to authenticate.</summary>
    public bool SignedIn { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the CLI has no transcript to resume.</summary>
    public bool FailResume { get; set; }

    /// <summary>Gets or sets a value indicating whether the CLI predates the option that shows the reasoning.</summary>
    public bool RefuseReasoningDisplay { get; set; }

    /// <summary>Gets or sets a value indicating whether the CLI lists the tools of CodeAlta once initialized, as the real one does.</summary>
    public bool ListsToolsAtStart { get; set; }

    /// <summary>Gets or sets a value indicating whether the CLI answers the interrupt request.</summary>
    public bool AnswerInterrupt { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the CLI answers a switch of permission mode it takes.</summary>
    public bool AnswerPermissionModeSwitch { get; set; } = true;

    /// <summary>Gets or sets the permission mode the settings of the user give a process started without one.</summary>
    public string SettingsPermissionMode { get; set; } = "default";

    /// <summary>Gets or sets what a result says of the models of the conversation. The default names the model that answers.</summary>
    public Func<JsonObject>? ModelUsage { get; set; }

    /// <summary>Adds the cost of a turn to its conversation and returns the total, which is what the CLI reports.</summary>
    public double AddCost(string conversationId, double cost)
        => _totalCosts.AddOrUpdate(conversationId, cost, (_, total) => total + cost);

    /// <summary>Gets the processes that were started, in order.</summary>
    public IReadOnlyList<ClaudeCodeFakeProcess> Processes => [.. _processes];

    public ClaudeCodeFakeProcess Last => _processes.Last();

    public IClaudeCodeTransport Start(ClaudeCodeLaunch launch)
    {
        var process = new ClaudeCodeFakeProcess(this, launch);
        _processes.Enqueue(process);
        return process;
    }

    /// <summary>The options of a provider that runs this CLI.</summary>
    public ClaudeCodeModelProviderRuntimeOptions CreateOptions(string providerKey = "claude-code")
        => new()
        {
            ProviderKey = providerKey,
            DisplayName = "Claude Code",
            TransportFactory = this,
            ResolveCli = static () => new ClaudeCodeCliResolution("/fake/bin/claude", null),
            // Not the environment of the machine that runs the tests, where an API key may wait for an answer.
            GetEnvironmentVariable = static _ => null,
            IdleTimeout = TimeSpan.Zero,
            StartupTimeout = TimeSpan.FromSeconds(10),
            InterruptTimeout = TimeSpan.FromSeconds(2),
        };
}

internal sealed class ClaudeCodeFakeProcess : IClaudeCodeTransport
{
    private readonly ClaudeCodeFakeCli _cli;
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<JsonElement> _received = new();
    private readonly TaskCompletionSource _interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string[]> _toolsListedAtStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextRequestId;
    private int _disposed;

    public ClaudeCodeFakeProcess(ClaudeCodeFakeCli cli, ClaudeCodeLaunch launch)
    {
        _cli = cli;
        Launch = launch;
        SessionId = FindOption("--session-id=") ?? FindOption("--resume=") ?? "fake-session";
        LaunchPermissionMode = launch.Arguments.SkipWhile(static argument => argument != "--permission-mode").Skip(1).FirstOrDefault();
        PermissionMode = LaunchPermissionMode ?? cli.SettingsPermissionMode;
        if (cli.RefuseReasoningDisplay && launch.Arguments.Contains("--thinking-display"))
        {
            StandardErrorTail = "error: unknown option '--thinking-display'";
            ExitCode = 1;
            _output.Writer.TryComplete();
        }
        else if (cli.FailResume && FindOption("--resume=") is { } missing)
        {
            StandardErrorTail = $"No conversation found with session ID: {missing}";
            ExitCode = 1;
            _output.Writer.TryComplete();
        }
    }

    public ClaudeCodeLaunch Launch { get; }

    public string SessionId { get; }

    public string? ResumedSessionId => FindOption("--resume=");

    /// <summary>Gets the permission mode the process was started in, if any.</summary>
    public string? LaunchPermissionMode { get; }

    /// <summary>Gets the permission mode the process is in.</summary>
    public string PermissionMode { get; private set; }

    /// <summary>Gets the modes the host asked the process to switch to, refused or not.</summary>
    public IReadOnlyList<string> PermissionModeRequests => [.. _received
        .Where(static message => Type(message) == "control_request" && message.GetProperty("request").GetProperty("subtype").GetString() == "set_permission_mode")
        .Select(static message => message.GetProperty("request").GetProperty("mode").GetString()!)];

    /// <summary>Gets everything the host wrote to the process.</summary>
    public IReadOnlyList<JsonElement> Received => [.. _received];

    /// <summary>Gets the user messages the host sent.</summary>
    public IReadOnlyList<JsonElement> UserMessages => [.. _received.Where(static message => Type(message) == "user")];

    /// <summary>Gets the initialize request of the host.</summary>
    public JsonElement InitializeRequest => _received.First(static message =>
        Type(message) == "control_request" && message.GetProperty("request").GetProperty("subtype").GetString() == "initialize").GetProperty("request");

    /// <summary>Completes with the names of the tools listed at the start, when <see cref="ClaudeCodeFakeCli.ListsToolsAtStart" /> is set.</summary>
    public Task<string[]> ToolsListedAtStart => _toolsListedAtStart.Task;

    /// <summary>Completes when the host interrupted the turn.</summary>
    public Task Interrupted => _interrupted.Task;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public string StandardErrorTail { get; private set; } = string.Empty;

    public int? ExitCode { get; private set; }

    public ValueTask WriteLineAsync(ReadOnlyMemory<byte> utf8Line, CancellationToken cancellationToken)
    {
        if (IsDisposed || ExitCode is not null)
        {
            throw new IOException("The fake CLI exited.");
        }

        using var document = JsonDocument.Parse(utf8Line);
        var message = document.RootElement.Clone();
        _received.Enqueue(message);
        switch (Type(message))
        {
            case "control_request":
                AnswerControlRequest(message);
                break;
            case "control_response":
                var response = message.GetProperty("response");
                if (_requests.TryRemove(response.GetProperty("request_id").GetString()!, out var completion))
                {
                    completion.TrySetResult(response);
                }

                break;
            case "user":
                Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "running", ["session_id"] = SessionId });
                Emit(Clone(message, replay =>
                {
                    replay["isReplay"] = true;
                    replay["session_id"] = SessionId;
                }));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _cli.OnUserMessage(this, message).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        StandardErrorTail = ex.ToString();
                        Exit(2);
                    }
                });
                break;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ExitCode ??= 0;
            _output.Writer.TryComplete();
            foreach (var request in _requests.Values)
            {
                request.TrySetCanceled();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Ends the process as a crash would.</summary>
    public void Exit(int exitCode)
    {
        ExitCode = exitCode;
        _output.Writer.TryComplete();
    }

    /// <summary>Writes one message.</summary>
    public void Emit(JsonNode message) => _output.Writer.TryWrite(message.ToJsonString());

    /// <summary>Writes a line that is not a message.</summary>
    public void EmitRaw(string line) => _output.Writer.TryWrite(line);

    /// <summary>Sends a control request to the host and waits for its response object.</summary>
    public async Task<JsonElement> RequestAsync(JsonObject request, TimeSpan? timeout = null)
    {
        var requestId = $"cli-{Interlocked.Increment(ref _nextRequestId)}";
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[requestId] = completion;
        Emit(new JsonObject { ["type"] = "control_request", ["request_id"] = requestId, ["request"] = request });
        return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    /// <summary>Sends a control request and returns its identifier without waiting.</summary>
    public (string RequestId, Task<JsonElement> Response) BeginRequest(JsonObject request)
    {
        var requestId = $"cli-{Interlocked.Increment(ref _nextRequestId)}";
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[requestId] = completion;
        Emit(new JsonObject { ["type"] = "control_request", ["request_id"] = requestId, ["request"] = request });
        return (requestId, completion.Task);
    }

    /// <summary>Asks the host whether a tool may run and returns its decision.</summary>
    public async Task<JsonElement> AskPermissionAsync(string toolName, JsonObject input, string toolUseId)
        => (await RequestAsync(new JsonObject
        {
            ["subtype"] = "can_use_tool",
            ["tool_name"] = toolName,
            ["input"] = input.DeepClone(),
            ["tool_use_id"] = toolUseId,
            ["title"] = $"Claude wants to use {toolName}",
            ["permission_suggestions"] = new JsonArray(new JsonObject { ["type"] = "addRules", ["behavior"] = "allow" }),
        }).ConfigureAwait(false)).GetProperty("response");

    /// <summary>Calls a tool of the MCP server of the host and returns the JSON-RPC result.</summary>
    public async Task<JsonElement> CallMcpToolAsync(string name, JsonObject arguments, string? toolUseId = null)
    {
        var parameters = new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() };
        if (toolUseId is not null)
        {
            parameters["_meta"] = new JsonObject { ["claudecode/toolUseId"] = toolUseId };
        }

        var response = await McpAsync("tools/call", parameters).ConfigureAwait(false);
        return response.GetProperty("result");
    }

    /// <summary>Sends a JSON-RPC request to the MCP server of the host and returns its response.</summary>
    public async Task<JsonElement> McpAsync(string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _nextRequestId), ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        var response = await RequestAsync(new JsonObject { ["subtype"] = "mcp_message", ["server_name"] = "codealta", ["message"] = message }).ConfigureAwait(false);
        return response.GetProperty("response").GetProperty("mcp_response");
    }

    public void EmitInit()
        => Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["session_id"] = SessionId,
            ["model"] = "claude-test-1",
            ["claude_code_version"] = "2.1.289",
        });

    /// <summary>Writes the stream of one text block of a message.</summary>
    public void EmitTextStream(string messageId, int index, params string[] deltas)
    {
        if (index == 0)
        {
            EmitStreamEvent(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = messageId, ["model"] = "claude-test-1" } });
        }

        EmitStreamEvent(new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } });
        foreach (var delta in deltas)
        {
            EmitStreamEvent(new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = delta } });
        }

        EmitStreamEvent(new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
    }

    /// <summary>Announces a block of a message in the stream, without its content.</summary>
    public void EmitBlockStart(string messageId, int index, string type)
    {
        if (index == 0)
        {
            EmitStreamEvent(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = messageId, ["model"] = "claude-test-1" } });
        }

        EmitStreamEvent(new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = new JsonObject { ["type"] = type } });
        EmitStreamEvent(new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
    }

    public void EmitMessageStop(string stopReason = "end_turn")
    {
        EmitStreamEvent(new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = stopReason }, ["usage"] = new JsonObject { ["output_tokens"] = 12 } });
        EmitStreamEvent(new JsonObject { ["type"] = "message_stop" });
    }

    public void EmitStreamEvent(JsonObject @event, string? parentToolUseId = null)
        => Emit(new JsonObject { ["type"] = "stream_event", ["event"] = @event, ["session_id"] = SessionId, ["parent_tool_use_id"] = parentToolUseId });

    /// <summary>Writes an assistant message that holds the given content blocks.</summary>
    public void EmitAssistant(string messageId, JsonArray content, string? parentToolUseId = null, string? error = null, string model = "claude-test-1")
    {
        var message = new JsonObject
        {
            ["type"] = "assistant",
            ["session_id"] = SessionId,
            ["parent_tool_use_id"] = parentToolUseId,
            ["message"] = new JsonObject
            {
                ["id"] = messageId,
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = content,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 100,
                    ["cache_read_input_tokens"] = 2000,
                    ["cache_creation_input_tokens"] = 300,
                    ["output_tokens"] = 1,
                },
            },
        };
        if (error is not null)
        {
            message["error"] = error;
        }

        Emit(message);
    }

    public static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    public static JsonObject ToolUseBlock(string id, string name, JsonObject input)
        => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input.DeepClone() };

    /// <summary>Writes the user message that carries the result of a tool call.</summary>
    public void EmitToolResult(string toolUseId, string content, bool isError = false, string? parentToolUseId = null)
        => Emit(new JsonObject
        {
            ["type"] = "user",
            ["session_id"] = SessionId,
            ["parent_tool_use_id"] = parentToolUseId,
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUseId,
                    ["content"] = content,
                    ["is_error"] = isError,
                }),
            },
        });

    /// <summary>Writes the result that ends a turn, then the idle state.</summary>
    public void EmitResult(string? text, JsonElement? userMessage = null, bool isError = false, string subtype = "success", bool idle = true)
    {
        var result = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = subtype,
            ["is_error"] = isError,
            ["result"] = text,
            ["session_id"] = SessionId,
            // The cost is the one of the conversation so far, also in a process that resumed it.
            ["total_cost_usd"] = _cli.AddCost(ResumedSessionId ?? SessionId, 0.01),
            ["duration_ms"] = 42,
            ["modelUsage"] = _cli.ModelUsage?.Invoke() ?? new JsonObject { ["claude-test-1"] = new JsonObject { ["contextWindow"] = 200000 } },
        };
        if (userMessage is { } user && user.TryGetProperty("uuid", out var uuid))
        {
            result["user_message_uuids"] = new JsonArray(uuid.GetString());
        }

        Emit(result);
        if (idle)
        {
            Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "idle", ["session_id"] = SessionId });
        }
    }

    /// <summary>Writes a whole turn that answers with one text.</summary>
    public void EmitTextTurn(string messageId, string text, JsonElement userMessage)
    {
        EmitInit();
        EmitTextStream(messageId, 0, text);
        EmitAssistant(messageId, new JsonArray(TextBlock(text)));
        EmitMessageStop();
        EmitResult(text, userMessage);
    }

    /// <summary>Returns the text of a user message the host sent.</summary>
    public static string UserText(JsonElement userMessage)
    {
        var builder = new StringBuilder();
        foreach (var block in userMessage.GetProperty("message").GetProperty("content").EnumerateArray())
        {
            if (block.GetProperty("type").GetString() == "text")
            {
                builder.AppendLine(block.GetProperty("text").GetString());
            }
        }

        return builder.ToString().Trim();
    }

    private async Task ListToolsAtStartAsync()
    {
        try
        {
            await McpAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25" }).ConfigureAwait(false);
            var listed = await McpAsync("tools/list").ConfigureAwait(false);
            _toolsListedAtStart.TrySetResult([.. listed.GetProperty("result").GetProperty("tools").EnumerateArray().Select(static tool => tool.GetProperty("name").GetString()!)]);
        }
        catch (Exception ex)
        {
            _toolsListedAtStart.TrySetException(ex);
        }
    }

    private void AnswerControlRequest(JsonElement message)
    {
        var requestId = message.GetProperty("request_id").GetString()!;
        var request = message.GetProperty("request");
        switch (request.GetProperty("subtype").GetString())
        {
            case "initialize":
                var initialized = new JsonObject
                {
                    ["commands"] = new JsonArray(),
                    ["models"] = new JsonArray(
                        new JsonObject
                        {
                            ["value"] = "default",
                            ["resolvedModel"] = "claude-test-1",
                            ["displayName"] = "Default (recommended)",
                            ["description"] = "Use the default model",
                            ["supportsEffort"] = true,
                            ["supportedEffortLevels"] = new JsonArray("low", "medium", "high", "xhigh", "max"),
                        },
                        new JsonObject { ["value"] = "sonnet", ["displayName"] = "Sonnet", ["description"] = "Sonnet", ["supportedEffortLevels"] = new JsonArray("low", "high") },
                        new JsonObject { ["value"] = "haiku", ["displayName"] = "Haiku", ["description"] = "Haiku" },
                        new JsonObject { ["value"] = "cc-update-required-1", ["displayName"] = "Update required", ["disabled"] = true }),
                    ["account"] = _cli.SignedIn
                        ? new JsonObject { ["email"] = "someone@example.test", ["organization"] = "Someone's Organization", ["subscriptionType"] = "Claude Max", ["apiProvider"] = "firstParty" }
                        : new JsonObject { ["tokenSource"] = "none", ["apiProvider"] = "firstParty" },
                    ["current_permission_mode"] = PermissionMode,
                };
                if (_cli.ListsToolsAtStart)
                {
                    // The server of CodeAlta is connected while the CLI initializes, before it answers.
                    _ = Task.Run(async () =>
                    {
                        await ListToolsAtStartAsync().ConfigureAwait(false);
                        Respond(requestId, initialized);
                    });
                }
                else
                {
                    Respond(requestId, initialized);
                }

                break;
            case "get_context_usage":
                Respond(requestId, new JsonObject { ["totalTokens"] = 18000, ["maxTokens"] = 200000, ["rawMaxTokens"] = 200000 });
                break;
            case "stop_task":
                var stopped = request.GetProperty("task_id").GetString();
                Respond(requestId, null);
                // The order of a real CLI: the task was killed, its notice, then the list that no longer has it comes from the test.
                Emit(new JsonObject { ["type"] = "system", ["subtype"] = "task_updated", ["task_id"] = stopped, ["patch"] = new JsonObject { ["status"] = "killed" }, ["session_id"] = SessionId });
                Emit(new JsonObject { ["type"] = "system", ["subtype"] = "task_notification", ["task_id"] = stopped, ["status"] = "stopped", ["summary"] = "Stopped", ["session_id"] = SessionId });
                break;
            case "interrupt":
                _interrupted.TrySetResult();
                if (_cli.AnswerInterrupt)
                {
                    Respond(requestId, new JsonObject { ["still_queued"] = new JsonArray() });
                    Emit(new JsonObject { ["type"] = "result", ["subtype"] = "error_during_execution", ["is_error"] = true, ["terminal_reason"] = "aborted_tools", ["session_id"] = SessionId });
                    Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "idle", ["session_id"] = SessionId });
                }

                break;
            case "mcp_message":
                Respond(requestId, null);
                break;
            case "set_permission_mode":
                // What Claude Code 2.1.295 answers.
                var mode = request.GetProperty("mode").GetString();
                if (mode == "bypassPermissions" && LaunchPermissionMode != "bypassPermissions")
                {
                    RespondError(requestId, "Cannot set permission mode to bypassPermissions because the session was not launched with --dangerously-skip-permissions");
                }
                else if (mode is "default" or "manual" or "acceptEdits" or "plan" or "auto" or "dontAsk" or "bypassPermissions")
                {
                    PermissionMode = mode == "manual" ? "default" : mode;
                    if (_cli.AnswerPermissionModeSwitch)
                    {
                        Respond(requestId, new JsonObject { ["mode"] = PermissionMode });
                    }
                }
                else
                {
                    RespondError(requestId, "must be one of acceptEdits, auto, bypassPermissions, default, dontAsk, plan");
                }

                break;
            default:
                RespondError(requestId, "Unsupported control request subtype");
                break;
        }
    }

    private void RespondError(string requestId, string error)
        => Emit(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = error },
        });

    private void Respond(string requestId, JsonObject? response)
    {
        var body = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId };
        if (response is not null)
        {
            body["response"] = response;
        }

        Emit(new JsonObject { ["type"] = "control_response", ["response"] = body });
    }

    private string? FindOption(string prefix)
        => Launch.Arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static string? Type(JsonElement message)
        => message.TryGetProperty("type", out var type) ? type.GetString() : null;

    private static JsonObject Clone(JsonElement message, Action<JsonObject> change)
    {
        var clone = JsonNode.Parse(message.GetRawText())!.AsObject();
        change(clone);
        return clone;
    }
}
