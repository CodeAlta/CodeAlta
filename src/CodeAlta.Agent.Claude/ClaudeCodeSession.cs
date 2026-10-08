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
    private const string PreToolHookId = "codealta_pre_tool";
    private const string PlanModeHookId = "codealta_plan_mode";
    private const string PlanModeHookMatcher = "EnterPlanMode";
    private const string ReasoningDisplayOption = "--thinking-display";
    private const string NothingLeftOfOwnTurn = "Claude Code had nothing more to show for this turn.";
    private const int PreToolHookTimeoutSeconds = 600;
    private static readonly TimeSpan HookGateTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UnresolvedHookGateTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OwnTurnRunTimeout = TimeSpan.FromSeconds(30);

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
    // What the conversation of the CLI was told of the instructions of CodeAlta: the text when this process
    // gave it, and its hash, which the state of the session keeps.
    private string? _instructions;
    private string? _instructionsHash;
    private string? _pendingInstructionsUpdate;
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
        TaskCompletionSource? waiting;
        lock (_gate)
        {
            _run = context;
            _interrupt = null;
            _showsOwnTurn = context.ProviderInitiated;
            _ownTurnRunStarts = context.ProviderInitiated;
            if (_ownTurnsRead == _ownTurnsStarted)
            {
                // The tool calls of the previous run all have their result. Those of a turn the CLI started by
                // itself are kept for the run that reads it, which is this one.
                _toolCalls.Clear();
                _toolCallOrder.Clear();
            }

            waiting = _runForOwnTurn;
            _runForOwnTurn = null;
        }

        // What the CLI asked in a turn it started by itself is answered with the handlers of this run.
        waiting?.TrySetResult();
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
            // Before the process starts: the tools the CLI lists when it connects are the ones of this request.
            var toolsChanged = SetExposedTools(request.Tools);
            bool showsOwnTurn, starts, hasOwnTurn;
            lock (_gate)
            {
                showsOwnTurn = _showsOwnTurn;
                starts = _ownTurnRunStarts;
                _ownTurnRunStarts = false;
                hasOwnTurn = _ownTurnsRead < _ownTurnsStarted;
            }

            if (starts)
            {
                // The run shows a turn the CLI started by itself. Its message says what started the turn: the CLI
                // has that in its own words, and is not sent it.
                newUserMessages = [];
                if (!hasOwnTurn || _eventReader is null)
                {
                    // Another run read the turn, or its process is gone: there is nothing to read.
                    ArmIdleTimer();
                    return CompleteRun(NothingLeftOfOwnTurn);
                }
            }

            // A run that shows a turn of the CLI reads the process that wrote it: another one would not have it.
            if (!showsOwnTurn)
            {
                await EnsureConnectionAsync(request, cancellationToken).ConfigureAwait(false);
            }

            if (toolsChanged)
            {
                await AnnounceToolsAsync().ConfigureAwait(false);
            }

            if (newUserMessages.Count > 0)
            {
                _pendingInstructionsUpdate = TakeInstructionsUpdate(request);
            }

            // What the CLI wrote by itself since the last run, when no run was started for it, is read with this
            // one, before the answer: the result of the CLI names the messages a turn answers.
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
                _instructions = null;
                _instructionsHash = persisted.Instructions;
                _costTotal = persisted.Cost;
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

        // Claude Code keeps the system prompt a conversation started with: what is appended here is read by a
        // new conversation only. A conversation that is resumed is told what changed with its next prompt.
        var appendSystemPrompt = CreateAppendSystemPrompt(request);
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
        _instructions = appendSystemPrompt;
        _instructionsHash = ClaudeCodePrompts.HashInstructions(appendSystemPrompt);
        _costTotal = 0;
    }

    private string CreateAppendSystemPrompt(AgentTurnRequest request)
    {
        bool hasGateway;
        lock (_gate)
        {
            hasGateway = _exposedTools.ContainsKey(ClaudeCodePrompts.GatewayTool);
        }

        return ClaudeCodePrompts.CreateAppendSystemPrompt(request.DeveloperInstructions, hasGateway);
    }

    // Returns what the conversation has to be told of its instructions before the prompt that starts, if anything:
    // the parts that changed since it was given them, or all of them when what it was given is not known.
    private string? TakeInstructionsUpdate(AgentTurnRequest request)
    {
        var current = CreateAppendSystemPrompt(request);
        var hash = ClaudeCodePrompts.HashInstructions(current);
        if (string.Equals(hash, _instructionsHash, StringComparison.Ordinal))
        {
            _instructions = current;
            return null;
        }

        var update = ClaudeCodePrompts.CreateInstructionsUpdate(_instructions, current);
        _instructions = current;
        _instructionsHash = hash;
        return update;
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
            _backgroundTasks = 0;
        }

        ForgetOwnTurns();
        ResetRunState();
        connection.Start();
        await connection.RequestAsync(
                "initialize",
                writer =>
                {
                    // The CLI tells this side before it runs a tool, any of them: the session shows each call from
                    // when it starts, also those the CLI runs at the same time. It waits for this side before it
                    // edits a file, so that the file is read before the edit and the session shows the change:
                    // that can be for as long as the model writes the message of the call.
                    writer.WriteStartObject("hooks");
                    writer.WriteStartArray("PreToolUse");
                    writer.WriteStartObject();
                    writer.WriteStartArray("hookCallbackIds");
                    writer.WriteStringValue(PreToolHookId);
                    writer.WriteEndArray();
                    writer.WriteNumber("timeout", PreToolHookTimeoutSeconds);
                    writer.WriteEndObject();
                    // The plan mode of Claude Code ends with an approval of the user that CodeAlta has no way to
                    // ask for. CodeAlta has a plan mode of its own, which the user sees: that one is used.
                    writer.WriteStartObject();
                    writer.WriteString("matcher", PlanModeHookMatcher);
                    writer.WriteStartArray("hookCallbackIds");
                    writer.WriteStringValue(PlanModeHookId);
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

        ForgetOwnTurns();
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
            var preamble = (_pendingInstructionsUpdate, _pendingPreamble) switch
            {
                ({ } update, { } history) => update + "\n\n" + history,
                var (update, history) => update ?? history,
            };
            _pendingInstructionsUpdate = null;
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
            _unansweredUserMessages.Add(uuid);
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
            ForgetOwnTurns();
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

            ForgetOwnTurns();
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
    // A process that still works by itself is kept: closing it would end its background commands, and the turn
    // their end starts.
    private async Task CloseWhenIdleAsync(CancellationTokenSource idle)
    {
        try
        {
            await Task.Delay(_options.IdleTimeout, idle.Token).ConfigureAwait(false);
            if (!await _turnGate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            var waitsAgain = false;
            try
            {
                if (!idle.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
                {
                    waitsAgain = WorksByItself;
                    if (!waitsAgain)
                    {
                        await CloseConnectionAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _turnGate.Release();
            }

            if (waitsAgain && !idle.IsCancellationRequested)
            {
                ArmIdleTimer();
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

    // Returns whether the CLI listed other tools than these: it then has to be told to list them again.
    private bool SetExposedTools(IReadOnlyList<AgentToolDefinition> tools)
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

        lock (_gate)
        {
            var changed = _mcpInitialized && !string.Equals(_exposedToolsSignature, signature.ToString(), StringComparison.Ordinal);
            _exposedTools = exposed;
            _exposedToolsSignature = signature.ToString();
            return changed;
        }
    }

    // A tool was registered during the run (the UI tools, an MCP server of CodeAlta): the CLI lists again.
    private async Task AnnounceToolsAsync()
    {
        ClaudeCodeConnection? connection;
        lock (_gate)
        {
            // A process that started meanwhile lists the tools by itself.
            connection = _mcpInitialized ? _connection : null;
        }

        if (connection is null)
        {
            return;
        }

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
/// <param name="SessionId">The session of the CLI that holds the conversation.</param>
/// <param name="Users">The user messages of the conversation the CLI was sent.</param>
/// <param name="Assistants">The assistant messages of the conversation the CLI wrote.</param>
/// <param name="Instructions">The hash of the instructions of CodeAlta the conversation was told last.</param>
/// <param name="Cost">The cost of the conversation at its last result, from which the cost of the next turn is counted.</param>
internal sealed record ClaudeCodeProviderState(string SessionId, int Users, int Assistants, string? Instructions = null, double? Cost = null)
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
            (int)(ClaudeCodeJson.GetInt64(element, "assistants") ?? 0),
            ClaudeCodeJson.GetString(element, "instructions"),
            ClaudeCodeJson.GetDouble(element, "cost"));
    }

    public JsonElement ToJson()
        => ClaudeCodeJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Kind);
            writer.WriteString("sessionId", SessionId);
            writer.WriteNumber("users", Users);
            writer.WriteNumber("assistants", Assistants);
            if (Instructions is not null)
            {
                writer.WriteString("instructions", Instructions);
            }

            if (Cost is { } cost)
            {
                writer.WriteNumber("cost", cost);
            }

            writer.WriteEndObject();
        });
}
