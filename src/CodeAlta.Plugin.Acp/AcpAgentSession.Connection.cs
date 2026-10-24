using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

// The process of the agent of a session, its session of the agent, and what the agent sends while it runs.
internal sealed partial class AcpAgentSession
{
    private abstract record TurnEvent;

    private sealed record ChunkEvent(AgentContentKind Kind, string Text) : TurnEvent;

    private sealed record ToolCallEvent(string CallId, string Name, JsonElement Arguments) : TurnEvent;

    private sealed record UsageEvent(long? Used, long? Size, double? Cost) : TurnEvent;

    private sealed record PlanEvent(string Text) : TurnEvent;

    private sealed record NoticeEvent(string Text, bool Warning) : TurnEvent;

    private sealed record PromptEndedEvent(PromptResponse? Response, Exception? Failure) : TurnEvent;

    private sealed record ClosedEvent(string Message) : TurnEvent;

    /// <summary>The parts of the assistant message being read, and the content identifier of each.</summary>
    private sealed class Segment(int id)
    {
        public List<AgentMessagePart> Parts { get; } = [];

        public List<string?> ContentIds { get; } = [];

        private readonly StringBuilder _current = new();
        private AgentContentKind? _currentKind;

        public string Append(AgentContentKind kind, string text)
        {
            if (_currentKind != kind || Parts.Count == 0 || Parts[^1] is AgentMessagePart.ToolCall)
            {
                _current.Clear();
                _currentKind = kind;
                Parts.Add(kind == AgentContentKind.Reasoning ? new AgentMessagePart.Reasoning(string.Empty) : new AgentMessagePart.Text(string.Empty));
                ContentIds.Add($"acp:{id}:{Parts.Count - 1}");
            }

            _current.Append(text);
            Parts[^1] = kind == AgentContentKind.Reasoning ? new AgentMessagePart.Reasoning(_current.ToString()) : new AgentMessagePart.Text(_current.ToString());
            return ContentIds[^1]!;
        }
    }

    /// <summary>An agent process and the session of the agent that holds the conversation of CodeAlta.</summary>
    private sealed class Connection
    {
        private readonly Channel<TurnEvent> _events = Channel.CreateUnbounded<TurnEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private int _segments;
        private int _promptActive;

        public Connection(AcpClient client, AcpAgentCapabilities capabilities, string workingDirectory, string launchKey)
        {
            Client = client;
            Capabilities = capabilities;
            WorkingDirectory = workingDirectory;
            LaunchKey = launchKey;
        }

        public AcpClient Client { get; }

        public AcpAgentCapabilities Capabilities { get; }

        public string WorkingDirectory { get; }

        public string LaunchKey { get; }

        public string AgentSessionId { get; set; } = string.Empty;

        public HarnessToolEndpoint? Endpoint { get; set; }

        /// <summary>Gets or sets whether the session of the agent was started for this connection, and was given nothing yet.</summary>
        public bool NewSession { get; set; }

        /// <summary>Gets or sets whether the agent replays a session it loads: its updates are not part of a turn.</summary>
        public bool Loading { get; set; }

        public Task<PromptResponse>? Prompt { get; set; }

        public bool PromptActive => Volatile.Read(ref _promptActive) != 0;

        public ChannelReader<TurnEvent> Events => _events.Reader;

        public ConcurrentDictionary<string, CancellationTokenSource> PermissionRequests { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

        public JsonElement ConfigOptions { get; set; }

        public string? CurrentModel { get; set; }

        public long? ContextUsed { get; set; }

        public long? ContextSize { get; set; }

        public double? Cost { get; set; }

        public Usage? LastUsage { get; set; }

        public int NextSegmentId() => Interlocked.Increment(ref _segments);

        public void BeginPrompt() => Volatile.Write(ref _promptActive, 1);

        public void EndPrompt() => Volatile.Write(ref _promptActive, 0);

        public void Post(TurnEvent turnEvent) => _events.Writer.TryWrite(turnEvent);

        public string DescribeExit()
        {
            var tail = Client.ErrorTail.Trim();
            return tail.Length == 0 ? "The ACP agent stopped." : $"The ACP agent stopped.{Environment.NewLine}{tail}";
        }
    }

    // Starts the agent when it does not run, and opens its session: the one recorded, resumed or loaded when the agent
    // can, else a new one.
    private async Task EnsureConnectionAsync(
        AgentTurnRequest request,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? Environment.CurrentDirectory : request.WorkingDirectory;
        var environment = new Dictionary<string, string>(_options.Agent.Environment, StringComparer.Ordinal);
        if (request.Environment is { IsEmpty: false } sessionEnvironment)
        {
            foreach (var (name, value) in sessionEnvironment.Variables)
            {
                environment[name] = value;
            }
        }

        var launchKey = string.Join('\u001f', [workingDirectory, .. environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}")]);
        if (_connection is { } running && !running.Client.HasExited && !running.Client.Completion.IsCompleted &&
            string.Equals(running.LaunchKey, launchKey, StringComparison.Ordinal) && running.AgentSessionId.Length > 0)
        {
            return;
        }

        await CloseConnectionAsync().ConfigureAwait(false);
        var executable = _options.Agent.ResolveExecutable()
            ?? throw new AgentProviderFailureException(
                $"{_options.Agent.Command} was not found: {_options.AgentName} cannot start. " +
                (_options.Agent.UsesPackageRunner ? $"Install the tool that provides `{_options.Agent.CommandName}` (Node.js for npx, uv for uvx), then send the message again." : "Install the agent, or set its command in Settings > Providers."),
                new AgentFailureInfo(AgentFailureKind.Unavailable));

        if (_options.Agent.UsesPackageRunner)
        {
            await onSessionUpdate(new AgentTurnSessionUpdate { Kind = AgentSessionUpdateKind.Info, Message = $"Starting {_options.AgentName}…" }, cancellationToken).ConfigureAwait(false);
        }

        Connection? connection = null;
        AcpClient client;
        try
        {
            client = await _options.ConnectionFactory.StartAsync(
                    new AcpConnectionRequest(executable, _options.Agent.Arguments, environment, workingDirectory, _options.InitializeTimeout),
                    message => OnMessage(connection, message),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AcpConnectionClosedException or TimeoutException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            throw new AgentProviderFailureException($"{_options.AgentName} could not be started. {ex.Message}", new AgentFailureInfo(AgentFailureKind.Unavailable), ex);
        }

        var capabilities = AcpAgentCapabilities.Read(JsonSerializer.SerializeToElement(client.InitializeResponse, AcpClient.CreateJsonSerializerOptions()));
        _options.Profile.Capabilities = capabilities;
        connection = new Connection(client, capabilities, workingDirectory, launchKey);
        _ = client.Completion.ContinueWith(
            static (_, state) =>
            {
                var connection = (Connection)state!;
                connection.Post(new ClosedEvent(connection.DescribeExit()));
            },
            connection,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _connection = connection;

        try
        {
            if (capabilities.McpHttp)
            {
                connection.Endpoint = await _options.ToolEndpoints.OpenAsync(_mcpServer, cancellationToken).ConfigureAwait(false);
            }

            await OpenSessionAsync(request, connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task OpenSessionAsync(AgentTurnRequest request, Connection connection, CancellationToken cancellationToken)
    {
        var capabilities = connection.Capabilities;
        var mcpServers = CreateMcpServers(connection);
        var instructions = CreateInstructions(request, connection);
        var systemPrompt = capabilities.SystemPrompt
            ? new SystemPrompt { Mode = new SystemPromptMode { Value = JsonSerializer.SerializeToElement("append", AcpPluginJsonContext.Default.String) }, Content = [AcpPrompts.Text(instructions)] }
            : null;

        if (_agentSessionId is { } recorded && (capabilities.ResumeSession || capabilities.LoadSession))
        {
            try
            {
                JsonElement? options;
                if (capabilities.ResumeSession)
                {
                    var resumed = await connection.Client.SessionResumeAsync(
                            new ResumeSessionRequest
                            {
                                SessionId = new SessionId { Value = recorded },
                                Cwd = connection.WorkingDirectory,
                                McpServers = mcpServers,
                                SystemPrompt = systemPrompt,
                            },
                            Timeout(cancellationToken))
                        .ConfigureAwait(false);
                    options = Serialize(resumed.ConfigOptions);
                }
                else
                {
                    // The agent replays the session as updates before it answers: they are not part of a turn.
                    connection.Loading = true;
                    try
                    {
                        var loaded = await connection.Client.SessionLoadAsync(
                                new LoadSessionRequest
                                {
                                    SessionId = new SessionId { Value = recorded },
                                    Cwd = connection.WorkingDirectory,
                                    McpServers = mcpServers,
                                    SystemPrompt = systemPrompt,
                                },
                                Timeout(cancellationToken))
                            .ConfigureAwait(false);
                        options = Serialize(loaded.ConfigOptions);
                    }
                    finally
                    {
                        connection.Loading = false;
                    }
                }

                connection.AgentSessionId = recorded;
                SetConfigOptions(connection, options);
                if (systemPrompt is not null)
                {
                    _instructions = instructions;
                    _instructionsHash = HarnessInstructions.Hash(instructions);
                }

                return;
            }
            catch (AcpJsonRpcException ex) when (ex.Code != AcpErrorCodes.AuthRequired)
            {
                // The agent no longer has the session (another machine, a cleaned profile): a new one is told the
                // conversation CodeAlta recorded.
                _logger?.Info($"{_options.AgentName} could not resume session {recorded}: {ex.Message}. A new session is started with the conversation.");
            }
        }

        if (_agentSessionId is not null)
        {
            _agentSessionId = null;
            var conversation = await AgentEarlierConversation.GetWholeConversationAsync(request, cancellationToken).ConfigureAwait(false);
            _pendingPreamble = HarnessHistory.CreatePreviousConversation(HarnessHistory.WithoutTrailingUserMessages(conversation));
        }

        NewSessionResponse created;
        try
        {
            created = await connection.Client.SessionNewAsync(
                    new NewSessionRequest { Cwd = connection.WorkingDirectory, McpServers = mcpServers, SystemPrompt = systemPrompt },
                    Timeout(cancellationToken))
                .ConfigureAwait(false);
        }
        catch (AcpJsonRpcException ex)
        {
            throw MapFailure(ex, connection);
        }

        connection.AgentSessionId = created.SessionId.Value;
        connection.NewSession = true;
        _agentSessionId = created.SessionId.Value;
        SetConfigOptions(connection, Serialize(created.ConfigOptions));
        if (systemPrompt is not null)
        {
            _instructions = instructions;
            _instructionsHash = HarnessInstructions.Hash(instructions);
            connection.NewSession = false;
        }
    }

    private static List<McpServer> CreateMcpServers(Connection connection)
        => connection.Endpoint is { } endpoint
            ? [new McpServerHttp
            {
                Name = AcpPrompts.McpServerName,
                Url = endpoint.Url.ToString(),
                Headers = [.. endpoint.Headers.Select(static header => new HttpHeader { Name = header.Key, Value = header.Value })],
            }]
            : [];

    private static JsonElement? Serialize(List<SessionConfigOption>? options)
        => options is null ? null : JsonSerializer.SerializeToElement(options, AcpClient.CreateJsonSerializerOptions());

    private CancellationToken Timeout(CancellationToken cancellationToken)
    {
        // A source that ends with the session: a request it cancels is given up.
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        source.CancelAfter(_options.RequestTimeout);
        return source.Token;
    }

    private async Task CloseConnectionAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        CancelPermissionRequests(connection);
        _bridge.FailOutstanding($"{_options.AgentName} stopped before the tool call completed.");
        if (connection.Endpoint is { } endpoint)
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await connection.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.Debug($"The process of {_options.AgentName} did not close cleanly: {ex.Message}");
        }
    }

    // Called on the reading loop of the agent for each of its messages, in order: it must not wait.
    private void OnMessage(Connection? connection, AcpServerMessage message)
    {
        // The messages of a process are read once its connection exists: the initialize request is answered before.
        connection ??= _connection;
        if (connection is null)
        {
            if (message.RequestId is { } early)
            {
                _ = RespondErrorAsync(null, early, AcpErrorCodes.MethodNotFound, $"'{message.Method}' is not supported.");
            }

            return;
        }

        switch (message.Method)
        {
            case "session/update":
                if (message.Params.ValueKind == JsonValueKind.Object &&
                    AcpJson.TryGetObject(message.Params, "update", out var update) &&
                    (connection.AgentSessionId.Length == 0 || string.Equals(AcpJson.GetString(message.Params, "sessionId"), connection.AgentSessionId, StringComparison.Ordinal)))
                {
                    OnSessionUpdate(connection, update);
                }

                break;
            case "session/request_permission" when message.RequestId is { } requestId:
                _ = AnswerPermissionAsync(connection, requestId, message.Params);
                break;
            default:
                if (message.RequestId is { } other)
                {
                    // CodeAlta offers the agent no file system and no terminal of its own: the agent uses its own.
                    _ = RespondErrorAsync(connection, other, AcpErrorCodes.MethodNotFound, $"CodeAlta does not support '{message.Method}'.");
                }

                break;
        }
    }

    private void OnSessionUpdate(Connection connection, JsonElement update)
    {
        if (connection.Loading)
        {
            // A session the agent loads is replayed: CodeAlta has its history already.
            return;
        }

        switch (AcpJson.GetString(update, "sessionUpdate"))
        {
            case "agent_message_chunk" when AcpJson.TryGetObject(update, "content", out var content):
                if (AcpPrompts.RenderContentBlock(content) is { Length: > 0 } text)
                {
                    connection.Post(new ChunkEvent(AgentContentKind.Assistant, text));
                }

                break;
            case "agent_thought_chunk" when AcpJson.TryGetObject(update, "content", out var thought):
                if (AcpPrompts.RenderContentBlock(thought) is { Length: > 0 } reasoning)
                {
                    connection.Post(new ChunkEvent(AgentContentKind.Reasoning, reasoning));
                }

                break;
            case "tool_call":
                OnToolCall(connection, update);
                break;
            case "tool_call_update":
                if (AcpJson.GetString(update, "toolCallId") is { } updatedId && _bridge.Find(updatedId) is null)
                {
                    // An update of a call the agent did not announce: it is announced by it.
                    OnToolCall(connection, update);
                }
                else
                {
                    OnToolCallUpdate(update);
                }

                break;
            case "plan":
                connection.Post(new PlanEvent(AcpToolCalls.RenderPlan(update)));
                break;
            case "usage_update":
                connection.Post(new UsageEvent(AcpJson.GetInt64(update, "used"), AcpJson.GetInt64(update, "size"),
                    AcpJson.TryGetObject(update, "cost", out var cost) ? AcpJson.GetDouble(cost, "amount") : null));
                break;
            case "available_commands_update":
                if (AcpJson.TryGetArray(update, "availableCommands", out var commands))
                {
                    lock (connection.Commands)
                    {
                        connection.Commands.Clear();
                        foreach (var command in commands.EnumerateArray())
                        {
                            if (AcpJson.GetString(command, "name") is { Length: > 0 } name)
                            {
                                connection.Commands.Add(name.TrimStart('/'));
                            }
                        }
                    }
                }

                break;
            case "config_option_update":
                if (AcpJson.TryGetArray(update, "configOptions", out var options))
                {
                    SetConfigOptions(connection, options.Clone());
                }

                break;
            case "compaction_update":
                // The agent compacted its context: the block of the instructions may be gone with it.
                if (AcpJson.GetString(update, "status") is null or "completed")
                {
                    _instructionsLost = true;
                }

                break;
            case "notice":
                if (AcpJson.GetString(update, "message") is { Length: > 0 } notice)
                {
                    connection.Post(new NoticeEvent(notice, AcpJson.GetString(update, "severity") is "warning" or "error"));
                }

                break;
        }
    }

    private void OnToolCall(Connection connection, JsonElement update)
    {
        if (AcpJson.GetString(update, "toolCallId") is not { Length: > 0 } callId)
        {
            return;
        }

        var (name, arguments, codeAlta) = AcpToolCalls.Map(update, ExposedTools, connection.WorkingDirectory);
        _bridge.Register(callId, name, arguments, codeAlta);
        connection.Post(new ToolCallEvent(callId, name, arguments));
        OnToolCallUpdate(update);
    }

    private void OnToolCallUpdate(JsonElement update)
    {
        if (AcpJson.GetString(update, "toolCallId") is not { Length: > 0 } callId)
        {
            return;
        }

        switch (AcpJson.GetString(update, "status"))
        {
            case "in_progress":
                _bridge.MarkStarted(callId);
                break;
            case "completed":
                _bridge.Complete(callId, AcpToolCalls.CreateResult(update, success: true));
                break;
            case "failed":
                _bridge.Complete(callId, AcpToolCalls.CreateResult(update, success: false));
                break;
        }
    }

    private void SetConfigOptions(Connection connection, JsonElement? options)
    {
        if (options is not { ValueKind: JsonValueKind.Array } array)
        {
            return;
        }

        connection.ConfigOptions = array;
        if (AcpConfigOptions.Find(array, "model") is { } model)
        {
            connection.CurrentModel = AcpJson.GetString(model, "currentValue");
            _options.Profile.SetModels(AcpConfigOptions.ReadModels(array), connection.CurrentModel);
        }
    }

    private void CancelPermissionRequests(Connection? connection)
    {
        if (connection is null)
        {
            return;
        }

        foreach (var (_, prompt) in connection.PermissionRequests)
        {
            try
            {
                prompt.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task RespondErrorAsync(Connection? connection, RequestId requestId, int code, string message)
    {
        var client = connection?.Client ?? _connection?.Client;
        if (client is null)
        {
            return;
        }

        try
        {
            await client.RespondWithErrorAsync(requestId, code, message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AcpConnectionClosedException)
        {
        }
    }
}

/// <summary>The JSON-RPC error codes ACP names.</summary>
internal static class AcpErrorCodes
{
    public const int MethodNotFound = -32601;

    public const int AuthRequired = -32000;
}
