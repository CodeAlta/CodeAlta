using System.Collections.Concurrent;
using System.Globalization;
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

    private sealed record SessionUpdateEvent(AgentSessionUpdateKind Kind, string Message) : TurnEvent;

    private sealed record PromptEndedEvent(PromptResponse? Response, Exception? Failure) : TurnEvent;

    private sealed record ClosedEvent(string Message) : TurnEvent;

    /// <summary>An in-flight agent-to-client request (permission or elicitation) cancellable locally, on prompt retirement, or by <c>$/cancel_request</c>.</summary>
    private sealed class PendingClientRequest : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private int _cancelledByAgent;

        public PendingClientRequest(CancellationToken lifetimeToken, CancellationToken promptToken)
        {
            _source = promptToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, promptToken)
                : CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        }

        public CancellationToken Token => _source.Token;

        public bool CancelledByAgent => Volatile.Read(ref _cancelledByAgent) != 0;

        public void Cancel(bool byAgent)
        {
            if (byAgent)
            {
                Volatile.Write(ref _cancelledByAgent, 1);
            }

            try
            {
                _source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _source.Dispose();
    }

    private sealed record CachedToolCall(
        string? Title,
        string? Name,
        string? Kind,
        JsonElement? RawInput,
        JsonElement? Locations,
        JsonElement? Content,
        JsonElement? RawOutput);

    private const int CompactionSummaryLimit = 4000;

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
        private readonly Lock _promptGate = new();
        private CancellationTokenSource? _promptCts;
        private int _segments;
        private int _promptActive;
        private int _closed;

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

        /// <summary>Gets whether the agent runs in the sandbox of the session, without the sign-in of the user.</summary>
        public bool InSandbox { get; init; }

        /// <summary>Gets whether this connection advertised <c>elicitation.form</c> during <c>initialize</c>.</summary>
        public bool ElicitationFormEnabled { get; init; }

        /// <summary>Gets or sets whether the session of the agent was started for this connection, and was given nothing yet.</summary>
        public bool NewSession { get; set; }

        /// <summary>Gets or sets whether the agent replays a session it loads: its updates are not part of a turn.</summary>
        public bool Loading { get; set; }

        public Task<PromptResponse>? Prompt { get; set; }

        public bool PromptActive => Volatile.Read(ref _promptActive) != 0;

        public ChannelReader<TurnEvent> Events => _events.Reader;

        public ConcurrentDictionary<string, PendingClientRequest> PendingRequests { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, CachedToolCall> ToolCalls { get; } = new(StringComparer.Ordinal);

        public OrderedDictionary<string, string> Plans { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, StringBuilder> CompactionSummaries { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ActiveCompactions { get; } = new(StringComparer.Ordinal);

        public string? SessionTitle { get; set; }

        /// <summary>Gets or sets the exit description when the agent process stopped unexpectedly during an active prompt.</summary>
        public string? UnexpectedExit { get; set; }

        public HashSet<string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

        public JsonElement ConfigOptions { get; set; }

        public string? CurrentModel { get; set; }

        public long? ContextUsed { get; set; }

        public long? ContextSize { get; set; }

        public double? Cost { get; set; }

        public Usage? LastUsage { get; set; }

        public int NextSegmentId() => Interlocked.Increment(ref _segments);

        public void BeginPrompt()
        {
            lock (_promptGate)
            {
                _promptCts?.Dispose();
                _promptCts = new CancellationTokenSource();
                Volatile.Write(ref _promptActive, 1);
            }
        }

        public void EndPrompt()
        {
            CancellationTokenSource? previous;
            lock (_promptGate)
            {
                Volatile.Write(ref _promptActive, 0);
                previous = _promptCts;
                _promptCts = null;
            }

            if (previous is not null)
            {
                try { previous.Cancel(); }
                catch (ObjectDisposedException) { }
                previous.Dispose();
            }
        }

        public void MarkClosed()
        {
            Volatile.Write(ref _closed, 1);
            EndPrompt();
        }

        public PendingClientRequest CreatePendingRequest(CancellationToken lifetimeToken)
        {
            lock (_promptGate)
            {
                if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _promptActive) == 0 || _promptCts is null || _promptCts.IsCancellationRequested)
                {
                    var alreadyCancelled = new PendingClientRequest(lifetimeToken, CancellationToken.None);
                    alreadyCancelled.Cancel(byAgent: false);
                    return alreadyCancelled;
                }

                return new PendingClientRequest(lifetimeToken, _promptCts.Token);
            }
        }

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
        var workingDirectory = WorkingDirectoryOf(request);
        var environment = CreateEnvironment(request);
        var enableElicitationForm = !Standalone && HasInteractiveQuestionHandler();

        // A process in another sandbox, with different elicitation capability, or out of one, is another process.
        var launchKey = string.Join('\u001f', [workingDirectory, request.ProcessHost?.Key ?? "-", enableElicitationForm ? "elicitation-form" : "-",
            .. environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}")]);
        if (_connection is { } running && !running.Client.HasExited && !running.Client.Completion.IsCompleted &&
            string.Equals(running.LaunchKey, launchKey, StringComparison.Ordinal) && running.AgentSessionId.Length > 0)
        {
            return;
        }

        await CloseConnectionAsync().ConfigureAwait(false);
        var executable = ResolveExecutable();
        if (_options.Agent.UsesPackageRunner)
        {
            await onSessionUpdate(new AgentTurnSessionUpdate { Kind = AgentSessionUpdateKind.Info, Message = $"Starting {_options.AgentName}…" }, cancellationToken).ConfigureAwait(false);
        }

        // In the sandbox of the session, when it has one, the agent runs its tools in its own process, and reaches the
        // tools of CodeAlta through its proxy: their endpoint is opened first, for its port.
        HarnessToolEndpoint? endpoint = null;
        var needs = AcpAgentNeeds.None;
        if (request.ProcessHost is not null)
        {
            endpoint = await _options.ToolEndpoints.OpenAsync(_mcpServer, cancellationToken).ConfigureAwait(false);
            needs = _options.Quirks.SandboxNeeds(_options.Agent, executable, _options.ProviderKey) with { LocalPorts = [endpoint.Url.Port] };
        }

        Connection connection;
        try
        {
            connection = await StartAgentAsync(
                    new AcpConnectionRequest(executable, _options.Agent.Arguments, environment, workingDirectory, _options.InitializeTimeout)
                    {
                        Host = request.ProcessHost,
                        Needs = needs,
                        EnableElicitationForm = enableElicitationForm,
                    },
                    launchKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (endpoint is not null) await endpoint.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var capabilities = connection.Capabilities;
        try
        {
            if (!capabilities.McpHttp)
            {
                if (endpoint is not null) await endpoint.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                connection.Endpoint = endpoint ?? await _options.ToolEndpoints.OpenAsync(_mcpServer, cancellationToken).ConfigureAwait(false);
            }

            await OpenSessionAsync(request, connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string WorkingDirectoryOf(AgentTurnRequest request)
        => string.IsNullOrWhiteSpace(request.WorkingDirectory) ? Environment.CurrentDirectory : request.WorkingDirectory;

    // The variables of the provider, then those of the session over them.
    private Dictionary<string, string> CreateEnvironment(AgentTurnRequest request)
    {
        var environment = new Dictionary<string, string>(_options.Agent.Environment, StringComparer.Ordinal);
        if (request.Environment is { IsEmpty: false } sessionEnvironment)
        {
            foreach (var (name, value) in sessionEnvironment.Variables)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    private string ResolveExecutable()
        => _options.Agent.ResolveExecutable()
            ?? throw new AgentProviderFailureException(
                $"{_options.Agent.Command} was not found: {_options.AgentName} cannot start. " +
                (_options.Agent.UsesPackageRunner ? $"Install the tool that provides `{_options.Agent.CommandName}` (Node.js for npx, uv for uvx), then send the message again." : "Install the agent, or set its command in Settings > Providers."),
                new AgentFailureInfo(AgentFailureKind.Unavailable));

    // Starts the process of the agent and makes it the connection of the session.
    private async Task<Connection> StartAgentAsync(AcpConnectionRequest start, string launchKey, CancellationToken cancellationToken)
    {
        Connection? connection = null;
        AcpClient client;
        try
        {
            client = await _options.ConnectionFactory.StartAsync(start, message => OnMessage(connection, message), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AcpConnectionClosedException or TimeoutException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            throw new AgentProviderFailureException($"{_options.AgentName} could not be started. {ex.Message}", new AgentFailureInfo(AgentFailureKind.Unavailable), ex);
        }

        var initialize = JsonSerializer.SerializeToElement(client.InitializeResponse, AcpClient.CreateJsonSerializerOptions());
        var capabilities = _options.Quirks.Capabilities(AcpAgentCapabilities.Read(initialize), initialize);
        _options.Profile.Capabilities = capabilities;
        connection = new Connection(client, capabilities, start.WorkingDirectory ?? Environment.CurrentDirectory, launchKey)
        {
            InSandbox = start.Host is not null,
            ElicitationFormEnabled = start.EnableElicitationForm,
        };
        _ = client.Completion.ContinueWith(
            static (_, state) =>
            {
                var (session, completedConnection) = ((AcpAgentSession, Connection))state!;
                session.OnConnectionClosed(completedConnection);
            },
            (this, connection),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _connection = connection;
        return connection;
    }

    private void OnConnectionClosed(Connection connection)
    {
        var exitMessage = connection.DescribeExit();
        var wasActive = connection.PromptActive;
        connection.MarkClosed();
        RetirePrompt(connection);
        if (ReferenceEquals(_connection, connection))
        {
            if (wasActive)
            {
                connection.UnexpectedExit = exitMessage;
            }

            // Unblock any tool-call wait in AgentSession immediately instead of waiting for the event loop to read ClosedEvent.
            _bridge.FailOutstanding(exitMessage);
        }

        connection.Post(new ClosedEvent(exitMessage));
    }

    // Appended to the system prompt of the agent, for an agent that takes one from its client: in the systemPrompt of
    // the protocol, or in the _meta of the request for an agent that takes it there.
    private (SystemPrompt? Prompt, Dictionary<string, JsonElement>? Meta) CreateSystemPrompt(string text)
        => _options.Quirks.SystemPromptMeta(text) is { } meta
            ? (null, meta)
            : (new SystemPrompt { Mode = new SystemPromptMode { Value = JsonSerializer.SerializeToElement("append", AcpPluginJsonContext.Default.String) }, Content = [AcpPrompts.Text(text)] }, null);

    private async Task OpenSessionAsync(AgentTurnRequest request, Connection connection, CancellationToken cancellationToken)
    {
        var capabilities = connection.Capabilities;
        var mcpServers = CreateMcpServers(connection);
        var instructions = CreateInstructions(request, connection);
        var (systemPrompt, systemPromptMeta) = capabilities.SystemPrompt ? CreateSystemPrompt(instructions) : default;

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
                                Meta = systemPromptMeta,
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
                                    Meta = systemPromptMeta,
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
                if (capabilities.SystemPrompt)
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
                    new NewSessionRequest { Cwd = connection.WorkingDirectory, McpServers = mcpServers, SystemPrompt = systemPrompt, Meta = systemPromptMeta },
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
        if (capabilities.SystemPrompt)
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

        connection.MarkClosed();
        RetirePrompt(connection);
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
            case "elicitation/create" when message.RequestId is { } elicitationId:
                _ = AnswerElicitationAsync(connection, elicitationId, message.Params);
                break;
            case "elicitation/complete":
                // URL-mode completion notification: clients MUST ignore unknown or completed IDs.
                break;
            case "$/cancel_request":
                OnCancelRequest(connection, message.Params);
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
                EndToolCallsLeftBehind();
                if (AcpPrompts.RenderContentBlock(content) is { Length: > 0 } text)
                {
                    connection.Post(new ChunkEvent(AgentContentKind.Assistant, text));
                }

                break;
            case "agent_thought_chunk" when AcpJson.TryGetObject(update, "content", out var thought):
                EndToolCallsLeftBehind();
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
                    OnToolCallUpdate(connection, update);
                }

                break;
            case "plan":
                connection.Post(new PlanEvent(AcpToolCalls.RenderPlan(update)));
                break;
            case "plan_update":
                if (AcpJson.TryGetObject(update, "plan", out var planObject) &&
                    AcpJson.GetString(planObject, "planId") is { Length: > 0 } planId)
                {
                    connection.Plans[planId] = AcpToolCalls.RenderPlanUpdate(planObject);
                    connection.Post(new PlanEvent(RenderAllPlans(connection.Plans)));
                }

                break;
            case "plan_removed":
                if (AcpJson.GetString(update, "planId") is { Length: > 0 } removedPlanId &&
                    connection.Plans.Remove(removedPlanId))
                {
                    connection.Post(new PlanEvent(connection.Plans.Count == 0 ? "Plan cleared." : RenderAllPlans(connection.Plans)));
                }

                break;
            case "session_info_update":
                if (update.TryGetProperty("title", out var titleProp))
                {
                    connection.SessionTitle = titleProp.ValueKind == JsonValueKind.String
                        ? (string.IsNullOrWhiteSpace(titleProp.GetString()) ? null : titleProp.GetString()!.Trim())
                        : null;
                }

                break;
            case "current_mode_update":
                if (AcpJson.GetString(update, "currentModeId") is { Length: > 0 } modeId)
                {
                    connection.Post(new SessionUpdateEvent(AgentSessionUpdateKind.ModeChanged, $"{_options.AgentName} switched to mode '{modeId}'."));
                }

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
            case "compaction_summary_chunk":
                OnCompactionSummaryChunk(connection, update);
                break;
            case "compaction_update":
                OnCompactionUpdate(connection, update);
                break;
            case "notice":
                if (FormatNotice(update) is { Length: > 0 } notice)
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

        if (_bridge.Find(callId) is null)
        {
            EndToolCallsLeftBehind();
        }

        CacheToolCall(connection, callId, update);
        var (name, arguments, codeAlta) = AcpToolCalls.Map(update, connection.Endpoint is not null ? ExposedTools : EmptyTools, connection.WorkingDirectory);
        _bridge.Register(callId, name, arguments, codeAlta);
        connection.Post(new ToolCallEvent(callId, name, arguments));
        OnToolCallUpdate(connection, update);
    }

    // An agent that reports the end of its tool calls only with its prompt went on: the calls of its own tools it
    // started before are done, and the session goes on with it instead of showing nothing until the prompt ends.
    private void EndToolCallsLeftBehind()
    {
        if (_options.Quirks.EndsToolCallsWithThePrompt)
        {
            _bridge.CompleteOwnOutstanding(AcpToolCalls.CreateUnreportedResult());
        }
    }

    private void OnToolCallUpdate(Connection connection, JsonElement update)
    {
        if (AcpJson.GetString(update, "toolCallId") is not { Length: > 0 } callId)
        {
            return;
        }

        CacheToolCall(connection, callId, update);
        switch (AcpJson.GetString(update, "status"))
        {
            case "in_progress":
                _bridge.MarkStarted(callId);
                break;
            case "completed":
                var completed = MergeToolCall(connection, update);
                connection.ToolCalls.TryRemove(callId, out _);
                _bridge.Complete(callId, AcpToolCalls.CreateResult(completed, success: true));
                break;
            case "failed":
                var failed = MergeToolCall(connection, update);
                connection.ToolCalls.TryRemove(callId, out _);
                _bridge.Complete(callId, AcpToolCalls.CreateResult(failed, success: false));
                break;
        }
    }

    private static void CacheToolCall(Connection connection, string callId, JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        connection.ToolCalls.AddOrUpdate(
            callId,
            _ => MergeCached(null, update),
            (_, existing) => MergeCached(existing, update));

        static CachedToolCall MergeCached(CachedToolCall? existing, JsonElement patch)
            => new(
                Title: AcpJson.GetString(patch, "title") ?? existing?.Title,
                Name: AcpJson.GetString(patch, "name") ?? existing?.Name,
                Kind: AcpJson.GetString(patch, "kind") ?? existing?.Kind,
                RawInput: patch.TryGetProperty("rawInput", out var rawInput) && rawInput.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    ? rawInput.Clone()
                    : existing?.RawInput,
                Locations: AcpJson.TryGetArray(patch, "locations", out var locations)
                    ? locations.Clone()
                    : existing?.Locations,
                Content: AcpJson.TryGetArray(patch, "content", out var content)
                    ? content.Clone()
                    : existing?.Content,
                RawOutput: patch.TryGetProperty("rawOutput", out var rawOutput) && rawOutput.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    ? rawOutput.Clone()
                    : existing?.RawOutput);
    }

    private static JsonElement MergeToolCall(Connection connection, JsonElement toolCall)
    {
        if (toolCall.ValueKind != JsonValueKind.Object ||
            AcpJson.GetString(toolCall, "toolCallId") is not { Length: > 0 } callId ||
            !connection.ToolCalls.TryGetValue(callId, out var cached))
        {
            return toolCall;
        }

        return AcpJson.Build(writer =>
        {
            writer.WriteStartObject();
            var hasTitle = false;
            var hasName = false;
            var hasKind = false;
            var hasRawInput = false;
            var hasLocations = false;
            var hasContent = false;
            var hasRawOutput = false;

            foreach (var property in toolCall.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                switch (property.Name)
                {
                    case "title": hasTitle = true; break;
                    case "name": hasName = true; break;
                    case "kind": hasKind = true; break;
                    case "rawInput": hasRawInput = true; break;
                    case "locations": hasLocations = true; break;
                    case "content": hasContent = true; break;
                    case "rawOutput": hasRawOutput = true; break;
                }

                property.WriteTo(writer);
            }

            if (!hasTitle && cached.Title is not null) writer.WriteString("title", cached.Title);
            if (!hasName && cached.Name is not null) writer.WriteString("name", cached.Name);
            if (!hasKind && cached.Kind is not null) writer.WriteString("kind", cached.Kind);
            if (!hasRawInput && cached.RawInput is { } rawInput) { writer.WritePropertyName("rawInput"); rawInput.WriteTo(writer); }
            if (!hasLocations && cached.Locations is { } locations) { writer.WritePropertyName("locations"); locations.WriteTo(writer); }
            if (!hasContent && cached.Content is { } content) { writer.WritePropertyName("content"); content.WriteTo(writer); }
            if (!hasRawOutput && cached.RawOutput is { } rawOutput) { writer.WritePropertyName("rawOutput"); rawOutput.WriteTo(writer); }
            writer.WriteEndObject();
        });
    }

    private void OnCompactionSummaryChunk(Connection connection, JsonElement update)
    {
        var compactionId = AcpJson.GetString(update, "compactionId") ?? string.Empty;
        if (compactionId.Length > 0 && connection.ActiveCompactions.Add(compactionId))
        {
            connection.Post(new SessionUpdateEvent(AgentSessionUpdateKind.CompactionStarted, $"{_options.AgentName} is compacting its context…"));
        }

        if (!AcpJson.TryGetObject(update, "content", out var block))
        {
            return;
        }

        var text = AcpPrompts.RenderContentBlock(block);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!connection.CompactionSummaries.TryGetValue(compactionId, out var builder))
        {
            builder = new StringBuilder();
            connection.CompactionSummaries[compactionId] = builder;
        }

        var remaining = CompactionSummaryLimit - builder.Length;
        if (remaining > 0)
        {
            builder.Append(text.Length <= remaining ? text : text[..remaining]);
        }
    }

    private void OnCompactionUpdate(Connection connection, JsonElement update)
    {
        var compactionId = AcpJson.GetString(update, "compactionId") ?? string.Empty;
        if (update.TryGetProperty("summary", out var summaryElement))
        {
            if (summaryElement.ValueKind == JsonValueKind.Null)
            {
                connection.CompactionSummaries.Remove(compactionId);
            }
            else if (summaryElement.ValueKind == JsonValueKind.Array)
            {
                var rendered = RenderSummaryBlocks(summaryElement);
                if (rendered.Length == 0)
                {
                    connection.CompactionSummaries.Remove(compactionId);
                }
                else
                {
                    connection.CompactionSummaries[compactionId] = new StringBuilder(rendered.Length <= CompactionSummaryLimit ? rendered : rendered[..CompactionSummaryLimit]);
                }
            }
        }

        switch (AcpJson.GetString(update, "status"))
        {
            case "in_progress":
                if (connection.ActiveCompactions.Add(compactionId))
                {
                    connection.Post(new SessionUpdateEvent(AgentSessionUpdateKind.CompactionStarted, $"{_options.AgentName} is compacting its context…"));
                }

                break;
            case "failed":
                connection.ActiveCompactions.Remove(compactionId);
                connection.CompactionSummaries.Remove(compactionId);
                var error = AcpJson.GetString(update, "error");
                connection.Post(new NoticeEvent(
                    string.IsNullOrWhiteSpace(error)
                        ? $"{_options.AgentName} failed to compact its context."
                        : $"{_options.AgentName} failed to compact its context: {error.Trim()}",
                    Warning: true));
                break;
            case "cancelled":
                connection.ActiveCompactions.Remove(compactionId);
                connection.CompactionSummaries.Remove(compactionId);
                break;
            case "completed":
                // The agent compacted its context: the block of the instructions may be gone with it.
                _instructionsLost = true;
                connection.ActiveCompactions.Remove(compactionId);
                string? summary = null;
                if (connection.CompactionSummaries.Remove(compactionId, out var builder) && builder.Length > 0)
                {
                    summary = builder.ToString().Trim();
                }

                connection.Post(new SessionUpdateEvent(
                    AgentSessionUpdateKind.CompactionCompleted,
                    string.IsNullOrEmpty(summary)
                        ? $"{_options.AgentName} compacted its context."
                        : $"{_options.AgentName} compacted its context.\n{summary}"));
                break;
        }
    }

    private static string RenderSummaryBlocks(JsonElement blocks)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            var text = AcpPrompts.RenderContentBlock(block);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    private static string RenderAllPlans(OrderedDictionary<string, string> plans)
        => plans.Count == 0
            ? "Plan updated."
            : string.Join("\n\n", plans.Values.Where(static text => !string.IsNullOrWhiteSpace(text)));

    private static string? FormatNotice(JsonElement update)
    {
        var title = AcpJson.GetString(update, "title")?.Trim();
        var description = AcpJson.GetString(update, "description")?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            // Compatibility fallback for older/custom agents that emit a `message` field.
            title = AcpJson.GetString(update, "message")?.Trim();
        }

        if (string.IsNullOrEmpty(title))
        {
            return string.IsNullOrEmpty(description) ? null : description;
        }

        return string.IsNullOrEmpty(description) ? title : $"{title}: {description}";
    }

    private void OnCancelRequest(Connection connection, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("requestId", out var requestId))
        {
            return;
        }

        if (connection.PendingRequests.TryGetValue(RequestIdKey(requestId), out var pending))
        {
            pending.Cancel(byAgent: true);
        }
    }

    private static string RequestIdKey(RequestId requestId) => RequestIdKey(requestId.Value);

    private static string RequestIdKey(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => "s:" + value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) => "n:" + number.ToString(CultureInfo.InvariantCulture),
            _ => "r:" + value.GetRawText(),
        };

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

    private static readonly IReadOnlyDictionary<string, AgentToolDefinition> EmptyTools =
        System.Collections.Frozen.FrozenDictionary<string, AgentToolDefinition>.Empty;

    private static void RetirePrompt(Connection? connection)
    {
        if (connection is null)
        {
            return;
        }

        connection.EndPrompt();
        CancelPendingRequests(connection);
        connection.ToolCalls.Clear();
    }

    private static void CancelPendingRequests(Connection? connection)
    {
        if (connection is null)
        {
            return;
        }

        foreach (var (_, pending) in connection.PendingRequests)
        {
            pending.Cancel(byAgent: false);
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
