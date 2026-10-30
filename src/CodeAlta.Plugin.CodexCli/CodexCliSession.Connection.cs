using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.CodexCli;

// The process of the session, its Codex thread, and what Codex sends while it runs.
internal sealed partial class CodexCliSession
{
    private abstract record TurnEvent;

    private sealed record ChunkEvent(AgentContentKind Kind, string Text) : TurnEvent;

    private sealed record ToolCallEvent(CodexCliToolCall Call) : TurnEvent;

    private sealed record UsageEvent : TurnEvent;

    private sealed record PlanEvent(string Text) : TurnEvent;

    private sealed record NoticeEvent(string Text, bool Warning) : TurnEvent;

    private sealed record TurnEndedEvent(Turn Turn) : TurnEvent;

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
                ContentIds.Add($"codex:{id}:{Parts.Count - 1}");
            }

            _current.Append(text);
            Parts[^1] = kind == AgentContentKind.Reasoning ? new AgentMessagePart.Reasoning(_current.ToString()) : new AgentMessagePart.Text(_current.ToString());
            return ContentIds[^1]!;
        }
    }

    /// <summary>An app-server process and the thread that holds the conversation of CodeAlta.</summary>
    private sealed class Connection
    {
        private readonly Channel<TurnEvent> _events = Channel.CreateUnbounded<TurnEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly Channel<(string CallId, string Delta)> _progress = Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
        private int _segments;
        private int _turnActive;
        private string? _activeTurnId;

        public Connection(CodexAppServerClient client, string workingDirectory, string launchKey)
        {
            Client = client;
            WorkingDirectory = workingDirectory;
            LaunchKey = launchKey;
        }

        public CodexAppServerClient Client { get; }

        public string WorkingDirectory { get; }

        public string LaunchKey { get; }

        public string ThreadId { get; set; } = string.Empty;

        public HarnessToolEndpoint? Endpoint { get; set; }

        /// <summary>Gets whether the app-server runs in the sandbox of the session.</summary>
        public bool InSandbox { get; init; }

        public bool TurnActive => Volatile.Read(ref _turnActive) != 0;

        public string? ActiveTurnId
        {
            get => Volatile.Read(ref _activeTurnId);
            set => Volatile.Write(ref _activeTurnId, value);
        }

        public ChannelReader<TurnEvent> Events => _events.Reader;

        public ChannelReader<(string CallId, string Delta)> Progress => _progress.Reader;

        public ConcurrentDictionary<string, CancellationTokenSource> Approvals { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the items whose text came as deltas: their completion does not repeat it.</summary>
        public ConcurrentDictionary<string, bool> StreamedItems { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the reasoning items that have a summary part already: the next one is a new paragraph.</summary>
        public ConcurrentDictionary<string, bool> ReasoningParts { get; } = new(StringComparer.Ordinal);

        public string? CurrentModel { get; set; }

        public ThreadTokenUsage? LastUsage { get; set; }

        public int NextSegmentId() => Interlocked.Increment(ref _segments);

        public void BeginTurn(string? turnId)
        {
            ActiveTurnId = turnId;
            Volatile.Write(ref _turnActive, 1);
        }

        public void EndTurn()
        {
            Volatile.Write(ref _turnActive, 0);
            ActiveTurnId = null;
        }

        public void Post(TurnEvent turnEvent) => _events.Writer.TryWrite(turnEvent);

        public void PostProgress(string callId, string delta) => _progress.Writer.TryWrite((callId, delta));

        public void Complete() => _progress.Writer.TryComplete();
    }

    // Starts the app-server when it does not run, and opens its thread: the one recorded, resumed or forked, else a new one.
    private async Task EnsureConnectionAsync(
        AgentTurnRequest request,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? Environment.CurrentDirectory : request.WorkingDirectory;
        var environment = new Dictionary<string, string>(_options.Settings.Environment, StringComparer.Ordinal);
        if (request.Environment is { IsEmpty: false } sessionEnvironment)
        {
            foreach (var (name, value) in sessionEnvironment.Variables)
            {
                environment[name] = value;
            }
        }

        // A process in another sandbox, or out of one, is another process. So is another size of context: Codex takes it
        // when a thread opens, and the new process resumes the thread with it.
        var launchKey = string.Join('\u001f', [workingDirectory, request.ProcessHost?.Key ?? "-", request.ContextSize?.ToString(CultureInfo.InvariantCulture) ?? "-",
            .. environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}")]);
        if (_connection is { } running && !running.Client.IsClosed && string.Equals(running.LaunchKey, launchKey, StringComparison.Ordinal) && running.ThreadId.Length > 0)
        {
            return;
        }

        await CloseConnectionAsync().ConfigureAwait(false);
        var downloading = new Progress<CodexInstallProgress>(progress => _ = onSessionUpdate(new AgentTurnSessionUpdate { Kind = AgentSessionUpdateKind.Info, Message = progress.Message }, CancellationToken.None));
        var command = await _options.ResolveCommandAsync(_options.Installer?.Installed is null ? downloading : null, cancellationToken).ConfigureAwait(false);
        try
        {
            CodexCliHomeConfig.Write(_options.Home);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentProviderFailureException($"{_options.ProviderName} could not write the configuration of its home {_options.Home}: {exception.Message}",
                new AgentFailureInfo(AgentFailureKind.Unavailable), exception);
        }

        // Codex reaches the tools of CodeAlta over HTTP: their endpoint is opened first, for its port in a sandbox.
        var endpoint = await _options.ToolEndpoints.OpenAsync(_mcpServer, cancellationToken).ConfigureAwait(false);
        Connection? connection = null;
        CodexAppServerClient client;
        try
        {
            client = await _options.ConnectionFactory.StartAsync(
                    new CodexCliConnectionRequest(command, _options.Home, environment, workingDirectory, _options.InitializeTimeout)
                    {
                        Host = request.ProcessHost,
                        LocalPorts = request.ProcessHost is null ? [] : [endpoint.Url.Port],
                    },
                    message => OnMessage(connection, message),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CodexConnectionClosedException or TimeoutException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException
            or CodexJsonRpcException && !cancellationToken.IsCancellationRequested)
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
            throw new AgentProviderFailureException($"{_options.ProviderName} could not be started. {ex.Message}", new AgentFailureInfo(AgentFailureKind.Unavailable), ex);
        }
        catch
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _options.Profile.Version = CodexAppServerCommand.VersionFromUserAgent(client.InitializeResponse.UserAgent);
        connection = new Connection(client, workingDirectory, launchKey) { InSandbox = request.ProcessHost is not null, Endpoint = endpoint };
        _ = client.Completion.ContinueWith(
            static (_, state) =>
            {
                var connection = (Connection)state!;
                connection.Post(new ClosedEvent(connection.Client.DescribeExit()));
                connection.Complete();
            },
            connection,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _ = PumpProgressAsync(connection);
        _connection = connection;

        try
        {
            await EnsureSignedInAsync(connection, cancellationToken).ConfigureAwait(false);
            await OpenThreadAsync(request, connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    // A home without a sign-in makes Codex call the OpenAI API without a key: its turns fail with a 401 that does not
    // say what to do. The sign-in is checked first, without a model call.
    private async Task EnsureSignedInAsync(Connection connection, CancellationToken cancellationToken)
    {
        GetAccountResponse account;
        try
        {
            account = await connection.Client.AccountReadAsync(new GetAccountParams { RefreshToken = false }, Timeout(cancellationToken)).ConfigureAwait(false);
        }
        catch (CodexJsonRpcException exception)
        {
            _logger?.Debug($"{_options.ProviderName} did not read its account: {exception.Message}. The turn goes on.");
            return;
        }

        if (account.Account is null && account.RequiresOpenaiAuth)
        {
            throw new AgentProviderFailureException(
                $"{_options.ProviderName} is not signed in (its Codex home is {_options.Home}). Sign in from Settings > Providers, then send the message again.",
                new AgentFailureInfo(AgentFailureKind.Auth));
        }
    }

    // Opens the thread of the session with what depends on it, which Codex does not keep: the MCP server of its tools, its
    // web search, the trust of its folder and the instructions of CodeAlta.
    private async Task OpenThreadAsync(AgentTurnRequest request, Connection connection, CancellationToken cancellationToken)
    {
        var instructions = CreateInstructions(request);
        var config = CreateThreadConfig(connection, WebSearchOf(request), request.ContextSize);
        var approval = ApprovalPolicyOf(_permissionMode);
        var model = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId;
        try
        {
            if (_origin is { ResumeId: { } resumeId })
            {
                try
                {
                    var resumed = await connection.Client.ThreadResumeAsync(
                            new ThreadResumeParams
                            {
                                ThreadId = resumeId,
                                Cwd = connection.WorkingDirectory,
                                ApprovalPolicy = approval,
                                Sandbox = SandboxMode.DangerFullAccess,
                                DeveloperInstructions = instructions,
                                Config = config,
                                Model = model,
                                ExcludeTurns = true,
                            },
                            Timeout(cancellationToken))
                        .ConfigureAwait(false);
                    Opened(connection, resumed.Thread.Id, resumed.Model, instructions);
                    return;
                }
                catch (CodexJsonRpcException ex)
                {
                    // Codex no longer has the thread (another computer, a cleaned home): a new one is told the
                    // conversation CodeAlta recorded.
                    _logger?.Info($"{_options.ProviderName} could not resume thread {resumeId}: {ex.Message}. A new thread is started with the conversation.");
                    await TellWholeConversationAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (_origin is { ForkFromId: { } forkFrom } origin)
            {
                try
                {
                    var forked = await connection.Client.ThreadForkAsync(
                            new ThreadForkParams
                            {
                                ThreadId = forkFrom,
                                LastTurnId = origin.ForkLastTurnId,
                                Cwd = connection.WorkingDirectory,
                                ApprovalPolicy = approval,
                                Sandbox = SandboxMode.DangerFullAccess,
                                DeveloperInstructions = instructions,
                                Config = config,
                                Model = model,
                                ExcludeTurns = true,
                            },
                            Timeout(cancellationToken))
                        .ConfigureAwait(false);
                    Opened(connection, forked.Thread.Id, forked.Model, instructions);
                    return;
                }
                catch (CodexJsonRpcException ex)
                {
                    _logger?.Info($"{_options.ProviderName} could not fork thread {forkFrom}: {ex.Message}. A new thread is started with the conversation.");
                    await TellWholeConversationAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }

            var started = await connection.Client.ThreadStartAsync(
                    new ThreadStartParams
                    {
                        Cwd = connection.WorkingDirectory,
                        ApprovalPolicy = approval,
                        Sandbox = SandboxMode.DangerFullAccess,
                        DeveloperInstructions = instructions,
                        Config = config,
                        Model = model,
                        ServiceName = "codealta",
                    },
                    Timeout(cancellationToken))
                .ConfigureAwait(false);
            Opened(connection, started.Thread.Id, started.Model, instructions);
        }
        catch (CodexJsonRpcException ex)
        {
            throw CodexCliFailures.FromRequest(ex, "open its thread");
        }
        catch (Exception ex) when (ex is CodexConnectionClosedException or TimeoutException)
        {
            throw Closed(connection, ex);
        }
    }

    private void Opened(Connection connection, string threadId, string? model, string instructions)
    {
        connection.ThreadId = threadId;
        connection.CurrentModel = model;
        _threadId = threadId;
        _origin = new ThreadOrigin(threadId, null, null);
        _instructions = instructions;
        _instructionsHash = HarnessInstructions.Hash(instructions);
    }

    private async Task TellWholeConversationAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        _origin = null;
        _threadId = null;
        _turns = [];
        var conversation = await AgentEarlierConversation.GetWholeConversationAsync(request, cancellationToken).ConfigureAwait(false);
        _pendingPreamble = HarnessHistory.CreatePreviousConversation(HarnessHistory.WithoutTrailingUserMessages(conversation));
    }

    // The configuration of a thread, over the config.toml of the home. A size of context is its model_context_window,
    // which Codex keeps to the max_context_window of the model and compacts at 90% of.
    private Dictionary<string, JsonElement> CreateThreadConfig(Connection connection, bool webSearch, long? contextSize)
    {
        var endpoint = connection.Endpoint!;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("mcp_servers");
            writer.WriteStartObject(CodexCliPrompts.McpServerName);
            writer.WriteString("url", endpoint.Url.ToString());
            writer.WriteStartObject("http_headers");
            foreach (var (name, value) in endpoint.Headers)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
            // CodeAlta's permissions decide for its own tools: Codex asks nothing more for them.
            writer.WriteString("default_tools_approval_mode", "approve");
            writer.WriteNumber("tool_timeout_sec", (long)_options.ToolTimeout.TotalSeconds);
            writer.WriteNumber("startup_timeout_sec", 60);
            writer.WriteBoolean("required", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteString("web_search", webSearch ? "live" : "disabled");
            if (contextSize is { } size)
            {
                writer.WriteNumber("model_context_window", size);
            }

            // Codex marks a folder trusted when a thread runs it with full access; trusted, it would also load the
            // .codex/config.toml and the AGENTS.md of the project, which CodeAlta gives in its own instructions.
            writer.WriteStartObject("projects");
            writer.WriteStartObject(connection.WorkingDirectory);
            writer.WriteString("trust_level", "untrusted");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private async Task CloseConnectionAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        CancelApprovals(connection);
        _bridge.FailOutstanding($"{_options.ProviderName} stopped before the tool call completed.");
        try
        {
            await connection.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.Debug($"The process of {_options.ProviderName} did not close cleanly: {ex.Message}");
        }

        if (connection.Endpoint is { } endpoint)
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }
    }

    // What a command writes while it runs, shown in the order Codex sends it.
    private async Task PumpProgressAsync(Connection connection)
    {
        try
        {
            await foreach (var (callId, delta) in connection.Progress.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await _bridge.ReportProgressAsync(callId, delta, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _logger?.Debug($"The output of a command of {_options.ProviderName} could not be shown: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    // Called on the reading loop of the app-server for each of its messages, in order: it must not wait.
    private void OnMessage(Connection? connection, CodexServerMessage message)
    {
        // The messages of a process are read once its connection exists: the initialize request is answered before.
        connection ??= _connection;
        if (connection is null)
        {
            if (message.RequestId is { } early)
            {
                _ = RespondErrorAsync(null, early, CodexErrorCodes.MethodNotFound, $"'{message.Method}' is not supported.");
            }

            return;
        }

        if (message.RequestId is { } requestId)
        {
            OnServerRequest(connection, requestId, message);
            return;
        }

        object? notification;
        try
        {
            notification = CodexMethods.ParseNotification(message.Method, message.Params);
        }
        catch (JsonException ex)
        {
            _logger?.Warn($"A notification of {_options.ProviderName} ({message.Method}) could not be read: {ex.Message}");
            return;
        }

        switch (notification)
        {
            case AgentMessageDeltaNotification delta when IsOfThread(connection, delta.ThreadId):
                connection.StreamedItems[delta.ItemId] = true;
                connection.Post(new ChunkEvent(AgentContentKind.Assistant, delta.Delta));
                break;
            case ReasoningSummaryTextDeltaNotification delta when IsOfThread(connection, delta.ThreadId):
                connection.StreamedItems[delta.ItemId] = true;
                connection.ReasoningParts[delta.ItemId] = true;
                connection.Post(new ChunkEvent(AgentContentKind.Reasoning, delta.Delta));
                break;
            case ReasoningSummaryPartAddedNotification part when IsOfThread(connection, part.ThreadId):
                if (connection.ReasoningParts.ContainsKey(part.ItemId))
                {
                    connection.Post(new ChunkEvent(AgentContentKind.Reasoning, "\n\n"));
                }

                break;
            case ReasoningTextDeltaNotification delta when IsOfThread(connection, delta.ThreadId):
                connection.StreamedItems[delta.ItemId] = true;
                connection.Post(new ChunkEvent(AgentContentKind.Reasoning, delta.Delta));
                break;
            case ItemStartedNotification started when IsOfThread(connection, started.ThreadId):
                OnItemStarted(connection, started.Item);
                break;
            case ItemCompletedNotification completed when IsOfThread(connection, completed.ThreadId):
                OnItemCompleted(connection, completed.Item);
                break;
            case CommandExecutionOutputDeltaNotification output when IsOfThread(connection, output.ThreadId):
                connection.PostProgress(output.ItemId, output.Delta);
                break;
            case McpToolCallProgressNotification progress when IsOfThread(connection, progress.ThreadId):
                connection.PostProgress(progress.ItemId, progress.Message + "\n");
                break;
            case TurnStartedNotification started when IsOfThread(connection, started.ThreadId):
                connection.ActiveTurnId = started.Turn.Id;
                break;
            case TurnCompletedNotification completed when IsOfThread(connection, completed.ThreadId):
                connection.Post(new TurnEndedEvent(completed.Turn));
                break;
            case TurnPlanUpdatedNotification plan when IsOfThread(connection, plan.ThreadId):
                connection.Post(new PlanEvent(CodexCliToolCalls.RenderPlan(plan)));
                break;
            case ThreadTokenUsageUpdatedNotification usage when IsOfThread(connection, usage.ThreadId):
                connection.LastUsage = usage.TokenUsage;
                connection.Post(new UsageEvent());
                break;
            case ErrorNotification error when IsOfThread(connection, error.ThreadId) && error.WillRetry:
                connection.Post(new NoticeEvent($"{_options.ProviderName} retries: {error.Error.Message}", Warning: true));
                break;
            case WarningNotification warning when warning.ThreadId is null || IsOfThread(connection, warning.ThreadId):
                connection.Post(new NoticeEvent(warning.Message, Warning: true));
                break;
            case ConfigWarningNotification warning:
                _logger?.Warn($"{_options.ProviderName} warns about its configuration: {warning.Summary}{(warning.Details is { } details ? $" ({details})" : string.Empty)}");
                break;
            case DeprecationNoticeNotification notice:
                _logger?.Info($"{_options.ProviderName}: {notice.Summary}");
                break;
            case ModelReroutedNotification rerouted when IsOfThread(connection, rerouted.ThreadId):
                connection.CurrentModel = rerouted.ToModel;
                connection.Post(new NoticeEvent($"{_options.ProviderName} runs this turn with {rerouted.ToModel} instead of {rerouted.FromModel}.", Warning: false));
                break;
            case McpServerStatusUpdatedNotification status when string.Equals(status.Name, CodexCliPrompts.McpServerName, StringComparison.Ordinal) && status.Error is { Length: > 0 } failure:
                connection.Post(new NoticeEvent($"{_options.ProviderName} could not connect to the tools of CodeAlta: {failure}", Warning: true));
                break;
        }
    }

    private static bool IsOfThread(Connection connection, string? threadId)
        => connection.ThreadId.Length == 0 || string.Equals(connection.ThreadId, threadId, StringComparison.Ordinal);

    private void OnItemStarted(Connection connection, ThreadItem item)
    {
        if (CodexCliToolCalls.Map(item, ExposedTools, connection.WorkingDirectory) is not { } call)
        {
            return;
        }

        if (_bridge.Find(call.CallId) is not null)
        {
            return;
        }

        _bridge.Register(call.CallId, call.Name, call.Arguments, call.CodeAlta);
        connection.Post(new ToolCallEvent(call));
        _bridge.MarkStarted(call.CallId);
    }

    private void OnItemCompleted(Connection connection, ThreadItem item)
    {
        switch (item)
        {
            case ThreadItem.AgentMessageThreadItem message when !connection.StreamedItems.ContainsKey(message.Id) && message.Text.Length > 0:
                // Codex gave the message whole, without deltas.
                connection.Post(new ChunkEvent(AgentContentKind.Assistant, message.Text));
                return;
            case ThreadItem.ReasoningThreadItem reasoning when !connection.StreamedItems.ContainsKey(reasoning.Id) && reasoning.Summary is { Count: > 0 } summary:
                connection.Post(new ChunkEvent(AgentContentKind.Reasoning, string.Join("\n\n", summary)));
                return;
        }

        if (CodexCliToolCalls.Map(item, ExposedTools, connection.WorkingDirectory) is not { } call)
        {
            return;
        }

        if (_bridge.Find(call.CallId) is null)
        {
            // An item Codex reports only once it is done (a web search): its call comes first.
            OnItemStarted(connection, item);
        }

        if (CodexCliToolCalls.Result(item) is { } result)
        {
            _bridge.Complete(call.CallId, result);
        }
    }

    private void CancelApprovals(Connection? connection)
    {
        if (connection is null)
        {
            return;
        }

        foreach (var (_, prompt) in connection.Approvals)
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

    private async Task RespondErrorAsync(Connection? connection, JsonElement requestId, int code, string message)
    {
        var client = connection?.Client ?? _connection?.Client;
        if (client is null)
        {
            return;
        }

        try
        {
            await client.RespondErrorAsync(requestId, code, message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or CodexConnectionClosedException)
        {
        }
    }
}
