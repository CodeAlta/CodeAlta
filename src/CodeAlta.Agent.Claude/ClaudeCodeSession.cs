using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// One CodeAlta session driven through the Claude Code CLI.
/// </summary>
/// <remarks>
/// <para>
/// The CLI runs its own loop: it calls the model, runs its tools and calls the model again. The agent session
/// of CodeAlta expects a provider that answers one model call at a time and lets the session run the tools.
/// This class presents the first as the second: each call to <see cref="ExecuteTurnAsync"/> returns the next
/// assistant message of the CLI, and the tool calls of that message are "run" by handlers that wait for the
/// result the CLI produces (<see cref="ResolveTool"/>). The session of CodeAlta then shows, records and replays
/// the run as it does for any provider.
/// </para>
/// <para>
/// The tools of CodeAlta are offered to the CLI through an MCP server served over the control protocol: such a
/// call is run by the session of CodeAlta itself, and its result is given back to the CLI.
/// </para>
/// </remarks>
internal sealed partial class ClaudeCodeSession : IAsyncDisposable
{
    private const string PreEditHookId = "codealta_pre_edit";
    private const string PreEditHookMatcher = "Edit|MultiEdit|Write|NotebookEdit";
    private const string ReasoningDisplayOption = "--thinking-display";
    private static readonly TimeSpan HookGateTimeout = TimeSpan.FromSeconds(20);

    private readonly ClaudeCodeModelProviderRuntimeOptions _options;
    private readonly IClaudeCodeTransportFactory _transportFactory;
    private readonly string _sessionId;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // The process of the session and what it was started with.
    private ClaudeCodeConnection? _connection;
    private ChannelWriter<TurnEvent>? _eventWriter;
    private ChannelReader<TurnEvent>? _eventReader;
    private ClaudeCodeLaunchKey? _launchKey;
    private int _generation;
    private CancellationTokenSource? _idle;

    // What the CLI knows of the conversation of CodeAlta.
    private bool _conversationBound;
    private string? _claudeSessionId;
    private int _syncedUsers;
    private int _syncedAssistants;
    private string? _pendingPreamble;

    private ModelProviderId _providerId;
    private AgentProviderRunContext? _run;
    private IReadOnlyDictionary<string, AgentToolDefinition> _exposedTools = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
    private string _exposedToolsSignature = string.Empty;
    private bool _mcpInitialized;
    private Task? _interrupt;
    private bool _showReasoning = true;
    private int _disposed;

    public ClaudeCodeSession(string sessionId, ClaudeCodeModelProviderRuntimeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionId = sessionId;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transportFactory = options.TransportFactory ?? ClaudeCodeProcessTransportFactory.Instance;
    }

    /// <summary>Gives the handlers of a run before its first turn, and forgets what the previous run left.</summary>
    public void AttachRun(AgentProviderRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            _run = context;
            _interrupt = null;
            // The tool calls of the previous run all have their result.
            _toolCalls.Clear();
            _toolCallOrder.Clear();
        }

        ResetRunState();
    }

    /// <summary>
    /// Returns the next assistant message of the CLI for the session: it starts or resumes the process when
    /// needed, sends the user messages the CLI has not seen yet, and reads until a message is complete.
    /// </summary>
    public async Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelIdleTimer();
            lock (_gate)
            {
                if (_interrupt is { IsCompleted: true })
                {
                    _interrupt = null;
                }
            }

            _providerId = request.ProviderId;
            _conversationCount = request.Conversation.Count;
            var newUserMessages = BindConversation(request);
            await EnsureConnectionAsync(request, cancellationToken).ConfigureAwait(false);
            await UpdateExposedToolsAsync(request.Tools).ConfigureAwait(false);
            await SendUserMessagesAsync(newUserMessages, cancellationToken).ConfigureAwait(false);
            var response = await ReadSegmentAsync(request, onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false);
            if (!response.RequiresProviderFollowUp && response.AssistantMessage.Parts.All(static part => part is not AgentMessagePart.ToolCall))
            {
                ArmIdleTimer();
            }

            return response;
        }
        catch
        {
            // The turn is given up, by the user or by a failure: the CLI does not go on with it alone, and what it
            // wrote for it is not read as the answer of a later turn.
            await InterruptAsync().ConfigureAwait(false);
            ArmIdleTimer();
            throw;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Asks the CLI to compact the context it keeps for the session (its <c>/compact</c> command).
    /// </summary>
    public async Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelIdleTimer();
            lock (_gate)
            {
                _interrupt = null;
            }

            _providerId = request.ProviderId;
            _ = BindConversation(request, hasNewPrompt: false);
            if (_claudeSessionId is null || _pendingPreamble is not null)
            {
                return new AgentCompactionOutcome(true, "Claude Code holds no context for this session yet: nothing to compact.");
            }

            ResetRunState();
            await EnsureConnectionAsync(request, cancellationToken).ConfigureAwait(false);
            await SendUserTextAsync("/compact", cancellationToken).ConfigureAwait(false);
            return await ReadCompactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await InterruptAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArmIdleTimer();
            _turnGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelIdleTimer();
        _lifetime.Cancel();
        await CloseConnectionAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    // Decides what the CLI already knows of the conversation and returns the user messages it has to be sent.
    private IReadOnlyList<AgentConversationMessage> BindConversation(AgentTurnRequest request, bool hasNewPrompt = true)
    {
        var conversation = request.Conversation;
        var users = 0;
        var assistants = 0;
        foreach (var message in conversation)
        {
            if (message.Role is AgentConversationRole.User)
            {
                users++;
            }
            else if (message.Role is AgentConversationRole.Assistant)
            {
                assistants++;
            }
        }

        // The user messages at the end of the conversation are the ones of the turn that starts.
        var trailing = 0;
        if (hasNewPrompt)
        {
            for (var index = conversation.Count - 1; index >= 0 && conversation[index].Role is AgentConversationRole.User; index--)
            {
                trailing++;
            }
        }

        if (!_conversationBound || users < _syncedUsers || assistants < _syncedAssistants)
        {
            var persisted = ClaudeCodeProviderState.Read(request.State);
            var priorUsers = users - trailing;
            var bound = _conversationBound;
            _conversationBound = true;
            if (!bound && persisted is not null && persisted.Assistants == assistants && persisted.Users <= priorUsers)
            {
                // The transcript of the CLI holds the conversation: it is resumed.
                _claudeSessionId = persisted.SessionId;
                _pendingPreamble = null;
            }
            else
            {
                // A new session, or one whose history another provider wrote or rewrote: the CLI starts a new
                // conversation and is told what was said before.
                _claudeSessionId = null;
                _pendingPreamble = priorUsers > 0 || assistants > 0
                    ? ClaudeCodePrompts.CreateHistoryPreamble([.. conversation.Take(conversation.Count - trailing)])
                    : null;
                if (bound)
                {
                    _launchKey = null;
                }
            }

            _syncedUsers = priorUsers;
            _syncedAssistants = assistants;
        }

        var missing = users - _syncedUsers;
        _syncedUsers = users;
        _syncedAssistants = assistants;
        if (missing <= 0)
        {
            return [];
        }

        var newMessages = new List<AgentConversationMessage>(missing);
        for (var index = conversation.Count - 1; index >= 0 && newMessages.Count < missing; index--)
        {
            if (conversation[index].Role is AgentConversationRole.User)
            {
                newMessages.Add(conversation[index]);
            }
        }

        newMessages.Reverse();
        return newMessages;
    }

    private async Task EnsureConnectionAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        // An effort is only given to a model the CLI lists it for; a model it does not list is given what was asked.
        var effort = request.ModelInfo is { } modelInfo &&
                     (request.ReasoningEffort is not { } requested || modelInfo.SupportedReasoningEfforts?.Contains(requested) != true)
            ? null
            : ClaudeCodeLauncher.ToEffort(request.ReasoningEffort);
        var key = new ClaudeCodeLaunchKey(
            string.IsNullOrWhiteSpace(request.WorkingDirectory) ? null : request.WorkingDirectory,
            ClaudeCodeLauncher.ToModelOption(request.ModelId),
            effort);
        if (_connection is { IsClosed: false } && key == _launchKey)
        {
            return;
        }

        await CloseConnectionAsync().ConfigureAwait(false);

        var resolution = _options.ResolveCli?.Invoke() ?? ClaudeCodeCliLocator.Resolve(_options.Command);
        if (resolution.Path is null)
        {
            throw new InvalidOperationException(resolution.Error ?? "Claude Code was not found.");
        }

        var appendSystemPrompt = ClaudeCodePrompts.CreateAppendSystemPrompt(request.DeveloperInstructions, hasTools: true);
        if (_claudeSessionId is { } resumeSessionId)
        {
            try
            {
                await StartConnectionAsync(resolution.Path, key, newSessionId: null, resumeSessionId, appendSystemPrompt, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException && !cancellationToken.IsCancellationRequested)
            {
                // The transcript is gone (another folder, another machine, a cleaned profile): the conversation
                // starts again in the CLI, with what CodeAlta recorded of it as context.
                await CloseConnectionAsync().ConfigureAwait(false);
                _claudeSessionId = null;
                _pendingPreamble = ClaudeCodePrompts.CreateHistoryPreamble(WithoutTrailingUsers(request.Conversation));
            }
        }

        var newSessionId = Guid.NewGuid().ToString();
        try
        {
            await StartConnectionAsync(resolution.Path, key, newSessionId, resumeSessionId: null, appendSystemPrompt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            var detail = _connection?.DescribeExit() ?? ex.Message;
            await CloseConnectionAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Claude Code could not be started. {detail}", ex);
        }

        _claudeSessionId = newSessionId;
    }

    private async Task StartConnectionAsync(
        string executable,
        ClaudeCodeLaunchKey key,
        string? newSessionId,
        string? resumeSessionId,
        string appendSystemPrompt,
        CancellationToken cancellationToken)
    {
        try
        {
            await StartConnectionCoreAsync(executable, key, newSessionId, resumeSessionId, appendSystemPrompt, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (_showReasoning && _connection?.DescribeExit().Contains(ReasoningDisplayOption, StringComparison.Ordinal) == true)
        {
            // A CLI that predates the option refuses to start with it: the session runs without the summaries.
            _showReasoning = false;
            await CloseConnectionAsync().ConfigureAwait(false);
            await StartConnectionCoreAsync(executable, key, newSessionId, resumeSessionId, appendSystemPrompt, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartConnectionCoreAsync(
        string executable,
        ClaudeCodeLaunchKey key,
        string? newSessionId,
        string? resumeSessionId,
        string appendSystemPrompt,
        CancellationToken cancellationToken)
    {
        var launch = ClaudeCodeLauncher.Create(executable, _options, key, newSessionId, resumeSessionId, withTools: true, _showReasoning);
        var channel = Channel.CreateUnbounded<TurnEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var generation = Interlocked.Increment(ref _generation);
        var transport = _transportFactory.Start(launch);
        var connection = new ClaudeCodeConnection(transport, new ConnectionHandler(this, generation, channel.Writer));
        lock (_gate)
        {
            _connection = connection;
            _eventWriter = channel.Writer;
            _eventReader = channel.Reader;
            _launchKey = key;
            _mcpInitialized = false;
            _toolCalls.Clear();
            _toolCallOrder.Clear();
            _stateEventsSeen = false;
            _cliRunning = false;
            _turnActive = false;
        }

        ResetRunState();
        connection.Start();
        await connection.RequestAsync(
                "initialize",
                writer =>
                {
                    // The CLI waits for this side before it runs an edit tool, so that the file is read before
                    // the edit and the session shows the change.
                    writer.WriteStartObject("hooks");
                    writer.WriteStartArray("PreToolUse");
                    writer.WriteStartObject();
                    writer.WriteString("matcher", PreEditHookMatcher);
                    writer.WriteStartArray("hookCallbackIds");
                    writer.WriteStringValue(PreEditHookId);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteStartArray("sdkMcpServers");
                    writer.WriteStringValue(ClaudeCodeLauncher.McpServerName);
                    writer.WriteEndArray();
                    writer.WriteString("appendSystemPrompt", appendSystemPrompt);
                },
                _options.StartupTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var usage = await connection.RequestAsync("get_context_usage", null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if ((ClaudeCodeJson.GetInt64(usage, "rawMaxTokens") ?? ClaudeCodeJson.GetInt64(usage, "maxTokens")) is > 0 and var contextWindow)
            {
                _contextWindow = contextWindow;
            }
        }
        catch (Exception ex) when (ex is ClaudeCodeControlException or TimeoutException)
        {
            // An older CLI does not answer this request: the window is learnt from the first result.
        }
    }

    private async Task CloseConnectionAsync()
    {
        ClaudeCodeConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
            _eventWriter = null;
            _eventReader = null;
            _launchKey = null;
        }

        // What a process that is being replaced still writes is not listened to.
        Interlocked.Increment(ref _generation);

        FailOutstandingToolCalls("Claude Code stopped before the tool call completed.");
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendUserMessagesAsync(IReadOnlyList<AgentConversationMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            var preamble = _pendingPreamble;
            _pendingPreamble = null;
            await SendUserAsync(writer => ClaudeCodePrompts.WriteUserContent(writer, message, preamble), cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendUserTextAsync(string text, CancellationToken cancellationToken)
        => SendUserAsync(
            writer => ClaudeCodePrompts.WriteUserContent(writer, new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text(text)]), null),
            cancellationToken);

    private async Task SendUserAsync(Action<Utf8JsonWriter> writeContent, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new InvalidOperationException("Claude Code is not running.");
        var uuid = Guid.NewGuid().ToString();
        lock (_gate)
        {
            _outstandingUserMessages.Add(uuid);
            _turnActive = true;
            _cliRunning = true;
        }

        try
        {
            await connection.SendAsync(writer =>
            {
                writer.WriteString("type", "user");
                writer.WriteString("uuid", uuid);
                writer.WriteString("session_id", _claudeSessionId ?? string.Empty);
                writer.WriteNull("parent_tool_use_id");
                writer.WriteStartObject("message");
                writer.WriteString("role", "user");
                writeContent(writer);
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(connection.DescribeExit(), ex);
        }
    }

    /// <summary>
    /// Stops the turn the CLI is running: it is asked to interrupt, and the process is stopped when it does not.
    /// The conversation stays resumable either way.
    /// </summary>
    public Task InterruptAsync()
    {
        lock (_gate)
        {
            return _interrupt ??= InterruptCoreAsync();
        }
    }

    private async Task InterruptCoreAsync()
    {
        // Let the caller observe its own cancellation first.
        await Task.Yield();
        ClaudeCodeConnection? connection;
        ChannelReader<TurnEvent>? reader;
        lock (_gate)
        {
            connection = _connection;
            reader = _eventReader;
        }

        ReleaseAllHookGates();
        FailOutstandingToolCalls("The tool call was interrupted.");
        CancelPermissionPrompts();
        if (connection is null || reader is null || connection.IsClosed)
        {
            return;
        }

        var stopped = !IsCliBusy;
        if (!stopped)
        {
            try
            {
                using var timeout = new CancellationTokenSource(_options.InterruptTimeout);
                // The messages sent while the turn ran are withdrawn with it.
                await connection.RequestAsync(
                        "interrupt",
                        static writer => writer.WriteBoolean("cancel_queued", true),
                        _options.InterruptTimeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                while (IsCliBusy && !connection.IsClosed)
                {
                    await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                }

                stopped = !connection.IsClosed;
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ClaudeCodeControlException)
            {
            }
        }

        if (!stopped || connection.IsClosed)
        {
            // The conversation is resumed by the next turn from the transcript of the CLI.
            await CloseConnectionAsync().ConfigureAwait(false);
        }
        else
        {
            // What the CLI wrote for the interrupted turn is not part of a later answer.
            while (reader.TryRead(out _))
            {
            }
        }

        ResetRunState();
        ArmIdleTimer();
    }

    private void ArmIdleTimer()
    {
        if (_options.IdleTimeout <= TimeSpan.Zero || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var idle = new CancellationTokenSource();
        Cancel(Interlocked.Exchange(ref _idle, idle));
        _ = CloseWhenIdleAsync(idle);
    }

    private void CancelIdleTimer()
        => Cancel(Interlocked.Exchange(ref _idle, null));

    private static void Cancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The timer ended and released its source at the same moment.
        }
    }

    // The process of an idle session is closed: the next turn resumes the conversation from the CLI's transcript.
    private async Task CloseWhenIdleAsync(CancellationTokenSource idle)
    {
        try
        {
            await Task.Delay(_options.IdleTimeout, idle.Token).ConfigureAwait(false);
            if (!await _turnGate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                if (!idle.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
                {
                    await CloseConnectionAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _turnGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            // The source is forgotten before it is released: a later turn does not cancel a released source.
            Interlocked.CompareExchange(ref _idle, null, idle);
            idle.Dispose();
        }
    }

    private async Task UpdateExposedToolsAsync(IReadOnlyList<AgentToolDefinition> tools)
    {
        var exposed = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
        var signature = new System.Text.StringBuilder();
        foreach (var (name, definition) in AgentToolBridge.CreateDefinitionMap(tools))
        {
            if (ClaudeCodePrompts.ReplacedTools.Contains(name))
            {
                continue;
            }

            exposed[name] = definition;
            signature.Append(name).Append('\u001f').Append(definition.Spec.Description.Length).Append('\u001e');
        }

        bool changed;
        ClaudeCodeConnection? connection;
        lock (_gate)
        {
            changed = _mcpInitialized && !string.Equals(_exposedToolsSignature, signature.ToString(), StringComparison.Ordinal);
            _exposedTools = exposed;
            _exposedToolsSignature = signature.ToString();
            connection = _connection;
        }

        if (!changed || connection is null)
        {
            return;
        }

        // A tool was registered during the run (an MCP server of CodeAlta was activated): the CLI lists again.
        try
        {
            await connection.RequestAsync(
                    "mcp_message",
                    writer =>
                    {
                        writer.WriteString("server_name", ClaudeCodeLauncher.McpServerName);
                        writer.WriteStartObject("message");
                        writer.WriteString("jsonrpc", "2.0");
                        writer.WriteString("method", "notifications/tools/list_changed");
                        writer.WriteEndObject();
                    },
                    TimeSpan.FromSeconds(5),
                    _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ClaudeCodeControlException or TimeoutException or IOException or OperationCanceledException)
        {
        }
    }

    private static IReadOnlyList<AgentConversationMessage> WithoutTrailingUsers(IReadOnlyList<AgentConversationMessage> conversation)
    {
        var count = conversation.Count;
        while (count > 0 && conversation[count - 1].Role is AgentConversationRole.User)
        {
            count--;
        }

        return [.. conversation.Take(count)];
    }
}

/// <summary>
/// What a session records in its journal to find the conversation of the CLI again: the identifier of its
/// transcript, and how much of the conversation of CodeAlta it holds.
/// </summary>
internal sealed record ClaudeCodeProviderState(string SessionId, int Users, int Assistants)
{
    private const string Kind = "claude-code";

    public static ClaudeCodeProviderState? Read(AgentSessionState state)
    {
        if (state.ProviderState is not { ValueKind: JsonValueKind.Object } element ||
            !string.Equals(ClaudeCodeJson.GetString(element, "kind"), Kind, StringComparison.Ordinal) ||
            ClaudeCodeJson.GetString(element, "sessionId") is not { Length: > 0 } sessionId)
        {
            return null;
        }

        return new ClaudeCodeProviderState(
            sessionId,
            (int)(ClaudeCodeJson.GetInt64(element, "users") ?? 0),
            (int)(ClaudeCodeJson.GetInt64(element, "assistants") ?? 0));
    }

    public JsonElement ToJson()
        => ClaudeCodeJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Kind);
            writer.WriteString("sessionId", SessionId);
            writer.WriteNumber("users", Users);
            writer.WriteNumber("assistants", Assistants);
            writer.WriteEndObject();
        });
}
