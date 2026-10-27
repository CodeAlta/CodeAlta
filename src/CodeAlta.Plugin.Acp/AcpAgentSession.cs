using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

/// <summary>
/// One CodeAlta session driven through an ACP agent: one agent process and one session of the agent.
/// </summary>
/// <remarks>
/// <para>
/// The agent runs its own loop: it calls its model, runs its tools and calls its model again within one
/// <c>session/prompt</c>. The agent session of CodeAlta expects a provider that answers one model call at a time and
/// lets the session run the tools. This class presents the first as the second: each call to
/// <see cref="ExecuteTurnAsync"/> returns the next assistant message of the agent (its text up to a tool call, or up to
/// the end of the prompt), and the tool calls of that message are "run" by handlers that wait for the result the
/// agent reports (<see cref="HarnessToolCallBridge"/>).
/// </para>
/// <para>
/// The tools of CodeAlta are offered to the agent through an MCP server of the session over HTTP, when the agent
/// connects to one: such a call is run by the session of CodeAlta itself, and its result is given back to the agent.
/// </para>
/// </remarks>
internal sealed partial class AcpAgentSession : IAsyncDisposable
{
    private const string FirstPromptChannel = "first-prompt";
    private const string SystemPromptChannel = "acp-system-prompt";

    private readonly string _sessionId;
    private readonly AcpRuntimeOptions _options;
    private readonly Logger? _logger;
    private readonly HarnessToolCallBridge _bridge = new();
    private readonly HarnessMcpToolServer _mcpServer;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private Connection? _connection;
    private ModelProviderId _providerId;
    private AgentProviderRunContext? _run;
    private string? _permissionMode;
    private IReadOnlyDictionary<string, AgentToolDefinition> _exposedTools = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
    private static readonly TimeSpan ToolsSyncTimeout = TimeSpan.FromSeconds(2);
    // Completed when the session gives the next segment, with its tools.
    private TaskCompletionSource _toolsSynced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _idle;
    private int _disposed;

    // What the agent knows of the conversation of CodeAlta.
    private bool _conversationBound;
    private string? _agentSessionId;
    private int _syncedUsers;
    private int _syncedAssistants;
    private string? _pendingPreamble;
    private readonly List<AgentConversationMessage> _unsent = [];

    // What the agent was told of the instructions of CodeAlta.
    private string? _instructions;
    private string? _instructionsHash;
    private bool _instructionsLost;

    private int _conversationCount;

    public AcpAgentSession(string sessionId, AcpRuntimeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionId = sessionId;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = options.Logger;
        _mcpServer = new HarnessMcpToolServer(AcpPrompts.McpServerName, () => ExposedTools, CallToolFromMcpAsync);
    }

    private IReadOnlyDictionary<string, AgentToolDefinition> ExposedTools
    {
        get
        {
            lock (_gate)
            {
                return _exposedTools;
            }
        }
    }

    /// <summary>
    /// Gets how the instructions of CodeAlta reach the agent: in its system prompt when it takes one from its client,
    /// else in a block of the first prompt.
    /// </summary>
    public string InstructionsChannel => _options.Profile.Capabilities?.SystemPrompt == true ? SystemPromptChannel : FirstPromptChannel;

    /// <summary>Gives the handlers of a run before its first turn.</summary>
    public void AttachRun(AgentProviderRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            _run = context;
        }

        if (_connection is not { PromptActive: true })
        {
            // The tool calls of the previous run all have their result.
            _bridge.ForgetCompleted();
        }
    }

    public AgentToolDefinition? ResolveTool(AgentMessagePart.ToolCall toolCall, AgentToolDefinition? registered)
        => _bridge.Resolve(toolCall, registered, $"A tool run by {_options.AgentName}.", InterruptAsync);

    public Task? WhenToolStarts(AgentMessagePart.ToolCall toolCall) => _bridge.WhenStarted(toolCall.CallId);

    /// <summary>
    /// Returns the next assistant message of the agent for the session: it starts or resumes the agent when needed,
    /// sends the user messages it has not seen yet in a prompt, and reads until a message is complete.
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
            _providerId = request.ProviderId;
            request = await ResolveConversationAsync(request, hasNewPrompt: true, cancellationToken).ConfigureAwait(false);
            var (earlierUsers, earlierAssistants) = EarlierCounts(request);
            _conversationCount = request.Conversation.Count + earlierUsers + earlierAssistants;
            lock (_gate)
            {
                _permissionMode = NormalizePermissionMode(request.PermissionMode);
            }

            var newMessages = BindConversation(request);
            if (SetExposedTools(request.Tools))
            {
                await AnnounceToolsAsync(cancellationToken).ConfigureAwait(false);
            }

            // The calls of tools of CodeAlta that waited for this segment answer the agent now.
            Interlocked.Exchange(ref _toolsSynced, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

            if (_connection is { PromptActive: true })
            {
                // The agent takes no message while it works on a prompt: what the user adds is its next prompt.
                _unsent.AddRange(newMessages);
            }
            else
            {
                var messages = new List<AgentConversationMessage>(_unsent.Count + newMessages.Count);
                messages.AddRange(_unsent);
                messages.AddRange(newMessages);
                _unsent.Clear();
                await EnsureConnectionAsync(request, onSessionUpdate, cancellationToken).ConfigureAwait(false);
                await ApplyModelAsync(request, cancellationToken).ConfigureAwait(false);
                if (messages.Count == 0)
                {
                    ArmIdleTimer();
                    return CreateResponse([], [], requiresFollowUp: false, null);
                }

                StartPrompt(request, messages);
            }

            var response = await ReadSegmentAsync(onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false);
            if (!response.RequiresProviderFollowUp && response.AssistantMessage.Parts.All(static part => part is not AgentMessagePart.ToolCall))
            {
                ArmIdleTimer();
            }

            return response;
        }
        catch
        {
            // The turn is given up, by the user or by a failure: the agent does not go on with it alone.
            await InterruptAsync().ConfigureAwait(false);
            ArmIdleTimer();
            throw;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>Asks the agent to compact its context, with its <c>/compact</c> command when it lists one.</summary>
    public async Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelIdleTimer();
            _providerId = request.ProviderId;
            request = await ResolveConversationAsync(request, hasNewPrompt: false, cancellationToken).ConfigureAwait(false);
            _ = BindConversation(request, hasNewPrompt: false);
            if (_agentSessionId is null || _pendingPreamble is not null)
            {
                return new AgentCompactionOutcome(true, $"{_options.AgentName} holds no context for this session yet: nothing to compact.");
            }

            await EnsureConnectionAsync(request, static (_, _) => ValueTask.CompletedTask, cancellationToken).ConfigureAwait(false);
            var connection = _connection!;
            if (!connection.Commands.Contains("compact"))
            {
                return new AgentCompactionOutcome(false, $"{_options.AgentName} has no command to compact its context.");
            }

            var response = await connection.Client.SessionPromptAsync(
                    new PromptRequest { SessionId = new SessionId { Value = connection.AgentSessionId }, Prompt = [AcpPrompts.Text("/compact")] },
                    cancellationToken)
                .ConfigureAwait(false);
            DrainEvents(connection);
            // The context the instructions were given in may be gone with the compaction.
            _instructionsLost = true;
            return new AgentCompactionOutcome(true, $"{_options.AgentName} compacted its context ({response.StopReason.Value}).");
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

    /// <summary>Puts the session in a permission mode now: the permission requests of the agent are answered by it.</summary>
    public void SetPermissionMode(string? permissionMode)
    {
        lock (_gate)
        {
            _permissionMode = NormalizePermissionMode(permissionMode);
        }
    }

    /// <summary>
    /// Stops the prompt the agent works on: it is asked to cancel, and its process is stopped when it does not end in
    /// time. The session of the agent stays resumable either way.
    /// </summary>
    public async Task InterruptAsync()
    {
        var connection = _connection;
        CancelPermissionRequests(connection);
        _bridge.FailOutstanding("The tool call was interrupted.");
        if (connection is null || !connection.PromptActive)
        {
            return;
        }

        try
        {
            await connection.Client.CancelSessionAsync(connection.AgentSessionId).ConfigureAwait(false);
            await connection.Prompt!.WaitAsync(_options.CancelTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or AcpJsonRpcException or OperationCanceledException)
        {
            // The agent did not stop: its process is, and the next turn resumes its session.
            await CloseConnectionAsync().ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The prompt ended with an error: it is over all the same.
            _logger?.Debug($"The interrupted prompt of {_options.AgentName} ended with an error: {ex.Message}");
        }

        DrainEvents(connection);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelIdleTimer();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await CloseConnectionAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    // A session read again without its conversation (AgentEarlierConversation) keeps it unread while the agent continues
    // the session it holds: the binding counts the messages not read as the state it was read with counted them. Any
    // other binding tells the agent what was said, and reads them: the request is then given its whole conversation.
    private async Task<AgentTurnRequest> ResolveConversationAsync(AgentTurnRequest request, bool hasNewPrompt, CancellationToken cancellationToken)
    {
        if (request.EarlierConversation is not { } earlier ||
            (earlier.Messages is null && ContinuesWithoutEarlierConversation(request, hasNewPrompt)))
        {
            return request;
        }

        var conversation = await AgentEarlierConversation.GetWholeConversationAsync(request, cancellationToken).ConfigureAwait(false);
        return request with { Conversation = conversation, EarlierConversation = null };
    }

    // Whether BindConversation keeps the session the agent holds for a request whose earlier messages were not read.
    private bool ContinuesWithoutEarlierConversation(AgentTurnRequest request, bool hasNewPrompt)
    {
        if (AcpProviderState.Read(request.EarlierConversation!.State) is null)
        {
            return false;
        }

        var (users, assistants, trailing) = CountConversation(request, hasNewPrompt);
        if (_conversationBound)
        {
            return users >= _syncedUsers && assistants >= _syncedAssistants;
        }

        var persisted = AcpProviderState.Read(request.State);
        return persisted is not null && request.State.ProviderBranch is null && persisted.Assistants == assistants && persisted.Users <= users - trailing;
    }

    // The messages a request has not read of its conversation, as the state of the session counted them when it was read.
    private static (int Users, int Assistants) EarlierCounts(AgentTurnRequest request)
        => request.EarlierConversation is { Messages: null } earlier && AcpProviderState.Read(earlier.State) is { } resumed
            ? (resumed.Users, resumed.Assistants)
            : (0, 0);

    // The user and assistant messages of the conversation of a request from its start, and the user messages at its end.
    private static (int Users, int Assistants, int Trailing) CountConversation(AgentTurnRequest request, bool hasNewPrompt)
    {
        var conversation = request.Conversation;
        var (users, assistants) = EarlierCounts(request);
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

        return (users, assistants, trailing);
    }

    // Decides what the agent already knows of the conversation and returns the user messages it has to be sent.
    private List<AgentConversationMessage> BindConversation(AgentTurnRequest request, bool hasNewPrompt = true)
    {
        var conversation = request.Conversation;
        var (users, assistants, trailing) = CountConversation(request, hasNewPrompt);
        var startsNew = false;
        if (!_conversationBound || users < _syncedUsers || assistants < _syncedAssistants)
        {
            var persisted = AcpProviderState.Read(request.State);
            var priorUsers = users - trailing;
            var bound = _conversationBound;
            _conversationBound = true;
            _unsent.Clear();
            if (!bound && persisted is not null && request.State.ProviderBranch is null &&
                persisted.Assistants == assistants && persisted.Users <= priorUsers)
            {
                // The agent holds the conversation in its session: it is resumed.
                _agentSessionId = persisted.SessionId;
                _pendingPreamble = null;
                _instructions = null;
                _instructionsHash = persisted.Instructions;
            }
            else
            {
                // A new session, one whose history another provider wrote or rewrote, or one forked or rewound: the
                // agent starts a new session and is told what was said before.
                _agentSessionId = null;
                startsNew = true;
                _pendingPreamble = priorUsers > 0 || assistants > 0
                    ? HarnessHistory.CreatePreviousConversation([.. conversation.Take(conversation.Count - trailing)])
                    : null;
                if (bound)
                {
                    // The running session of the agent has another history: a new one is started.
                    _ = CloseConnectionAsync();
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
        if (startsNew && newMessages.Count > 1)
        {
            // Prompts another provider left unanswered are one request: sent apart, the agent would answer the first,
            // then the next as if it came after that answer.
            return [new AgentConversationMessage(AgentConversationRole.User, [.. newMessages.SelectMany(static message => message.Parts)])];
        }

        return newMessages;
    }

    // Returns whether the tools differ from the ones the agent may have listed: a tool registered during the run (the
    // UI tools, an MCP server of CodeAlta) or one gone.
    private bool SetExposedTools(IReadOnlyList<AgentToolDefinition> tools)
    {
        var exposed = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
        foreach (var (name, definition) in AgentToolBridge.CreateDefinitionMap(tools))
        {
            if (!AcpPrompts.ReplacedTools.Contains(name))
            {
                exposed[name] = definition;
            }
        }

        lock (_gate)
        {
            var changed = !exposed.Keys.Order(StringComparer.Ordinal).SequenceEqual(_exposedTools.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
            _exposedTools = exposed;
            return changed;
        }
    }

    // The agent that listed the tools of CodeAlta is told to list them again, on the stream of events it opened.
    private async Task AnnounceToolsAsync(CancellationToken cancellationToken)
    {
        if (!_mcpServer.IsInitialized || _connection?.Endpoint is not { } endpoint)
        {
            return;
        }

        try
        {
            if (!await endpoint.NotifyAsync(HarnessMcpToolServer.CreateToolsChangedNotification(), cancellationToken).ConfigureAwait(false))
            {
                _logger?.Debug($"{_options.AgentName} opened no stream of MCP events: the tools added during the run reach it when it lists the tools again.");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    // A tool of CodeAlta can register tools for the run (`alta ui activate`): they reach the session with its next
    // segment. Its result waits for that segment, a short while at most, so that the agent is told of the new tools
    // with the result of the call that added them.
    private async Task<AgentToolResult> CallToolFromMcpAsync(HarnessMcpToolCall call, CancellationToken cancellationToken)
    {
        var synced = Volatile.Read(ref _toolsSynced).Task;
        var result = await _bridge.CallFromMcpAsync(call, name => ExposedTools.GetValueOrDefault(name), _providerId, _sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            await synced.WaitAsync(ToolsSyncTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        return result;
    }

    // Starts the prompt of the turn: the instructions when the agent has not been given them, the conversation of
    // another provider, a change of the instructions, then the messages of the user.
    private void StartPrompt(AgentTurnRequest request, IReadOnlyList<AgentConversationMessage> messages)
    {
        var connection = _connection!;
        var preambles = new List<string>();
        var instructions = CreateInstructions(request, connection);
        var hash = HarnessInstructions.Hash(instructions);
        var systemPrompt = connection.Capabilities.SystemPrompt;
        if (!systemPrompt && (connection.NewSession || _instructionsLost))
        {
            // A new session of the agent, or one that may have lost the block in a compaction: the block, whole.
            preambles.Add(AcpPrompts.CreateHarnessBlock(instructions));
        }
        else if (!string.Equals(hash, _instructionsHash, StringComparison.Ordinal))
        {
            preambles.Add(HarnessInstructions.CreateUpdate(_instructions, instructions, AcpPrompts.GivenAt(systemPrompt)));
        }

        _instructions = instructions;
        _instructionsHash = hash;
        _instructionsLost = false;
        connection.NewSession = false;
        if (_pendingPreamble is { } history)
        {
            preambles.Add(history);
            _pendingPreamble = null;
        }

        var prompt = AcpPrompts.CreatePrompt(preambles, messages, connection.Capabilities.Images);
        connection.BeginPrompt();
        var task = connection.Client.SessionPromptAsync(
            new PromptRequest { SessionId = new SessionId { Value = connection.AgentSessionId }, Prompt = prompt },
            _lifetime.Token);
        connection.Prompt = task;
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var connection = (Connection)state!;
                connection.Post(completed.IsCompletedSuccessfully
                    ? new PromptEndedEvent(completed.Result, null)
                    : new PromptEndedEvent(null, completed.Exception?.GetBaseException() ?? new OperationCanceledException()));
            },
            connection,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private string CreateInstructions(AgentTurnRequest request, Connection connection)
        => AcpPrompts.CreateInstructions(
            _options.AgentName,
            AgentInstructionText.DeveloperAndSession(request),
            hasTools: connection.Endpoint is not null,
            hasGateway: ExposedTools.ContainsKey(AcpPrompts.GatewayTool));

    // Reads the events of the prompt until an assistant message is complete: at a tool call, or at the end.
    private async Task<AgentTurnResponse> ReadSegmentAsync(
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new InvalidOperationException($"{_options.AgentName} is not running.");
        var segment = new Segment(connection.NextSegmentId());
        while (true)
        {
            TurnEvent turnEvent;
            try
            {
                turnEvent = await connection.Events.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw Closed(connection);
            }

            switch (turnEvent)
            {
                case ChunkEvent chunk:
                    var contentId = segment.Append(chunk.Kind, chunk.Text);
                    await onUpdate(new AgentTurnDelta { Kind = chunk.Kind, ContentId = contentId, Text = chunk.Text }, cancellationToken).ConfigureAwait(false);
                    break;
                case ToolCallEvent toolCall:
                    segment.Parts.Add(new AgentMessagePart.ToolCall(toolCall.CallId, toolCall.Name, toolCall.Arguments));
                    segment.ContentIds.Add(null);
                    return CreateResponse(segment.Parts, segment.ContentIds, requiresFollowUp: false, connection);
                case UsageEvent usage:
                    connection.ContextUsed = usage.Used ?? connection.ContextUsed;
                    connection.ContextSize = usage.Size ?? connection.ContextSize;
                    connection.Cost = usage.Cost ?? connection.Cost;
                    await onSessionUpdate(
                            new AgentTurnSessionUpdate
                            {
                                Kind = AgentSessionUpdateKind.UsageUpdated,
                                Message = "Context usage updated.",
                                Usage = CreateUsage(connection, _conversationCount),
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case PlanEvent plan:
                    await onSessionUpdate(new AgentTurnSessionUpdate { Kind = AgentSessionUpdateKind.PlanUpdated, Message = plan.Text }, cancellationToken).ConfigureAwait(false);
                    break;
                case NoticeEvent notice:
                    await onSessionUpdate(
                            new AgentTurnSessionUpdate
                            {
                                Kind = notice.Warning ? AgentSessionUpdateKind.Warning : AgentSessionUpdateKind.Info,
                                Message = notice.Text,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case PromptEndedEvent ended:
                    connection.EndPrompt();
                    // A tool call without a result when the prompt ends will never have one.
                    _bridge.FailOutstanding($"{_options.AgentName} ended its turn before the tool call completed.");
                    if (ended.Failure is { } failure)
                    {
                        throw MapFailure(failure, connection);
                    }

                    connection.LastUsage = ended.Response?.Usage;
                    if (ended.Response is { } response && AcpStopReasons.Describe(response.StopReason.Value, _options.AgentName) is { } note)
                    {
                        segment.Parts.Add(new AgentMessagePart.Text(note));
                        segment.ContentIds.Add(null);
                    }

                    return CreateResponse(segment.Parts, segment.ContentIds, requiresFollowUp: _unsent.Count > 0, connection);
                case ClosedEvent closed:
                    throw new AgentProviderFailureException(closed.Message, new AgentFailureInfo(AgentFailureKind.Unavailable));
            }
        }
    }

    private AgentTurnResponse CreateResponse(List<AgentMessagePart> parts, List<string?> contentIds, bool requiresFollowUp, Connection? connection)
    {
        var durable = parts.Count > 0 || !requiresFollowUp;
        if (durable)
        {
            _syncedAssistants++;
        }

        return new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [.. parts]),
            AssistantPartContentIds = contentIds.Exists(static id => id is not null) ? [.. contentIds] : null,
            RequiresProviderFollowUp = requiresFollowUp,
            Usage = connection is null ? null : CreateUsage(connection, _conversationCount + (durable ? 1 : 0)),
            ProviderSessionId = _agentSessionId,
            ProviderState = _agentSessionId is { } sessionId
                ? new AcpProviderState(sessionId, _syncedUsers, _syncedAssistants, _instructionsHash, InstructionsChannel).ToJson()
                : null,
        };
    }

    private static AgentSessionUsage? CreateUsage(Connection connection, int messageCount)
    {
        var usage = connection.LastUsage;
        if (usage is null && connection.ContextUsed is null)
        {
            return null;
        }

        var input = usage is null ? (long?)null : (long)usage.InputTokens;
        var output = usage is null ? (long?)null : (long)usage.OutputTokens;
        var current = connection.ContextUsed ?? (input + output);
        return new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(current, connection.ContextSize, messageCount, Label: "Active context window", TotalContextEnvelope: connection.ContextSize),
            LastOperation: usage is null
                ? null
                : new AgentOperationUsageSnapshot(
                    Model: connection.CurrentModel,
                    InputTokens: input,
                    OutputTokens: output,
                    CacheReadTokens: (long?)usage.CachedReadTokens,
                    CacheWriteTokens: (long?)usage.CachedWriteTokens,
                    CachedInputTokens: (long?)usage.CachedReadTokens,
                    ReasoningTokens: (long?)usage.ThoughtTokens,
                    Cost: connection.Cost),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.ProviderUsage,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private Exception MapFailure(Exception failure, Connection connection)
        => failure switch
        {
            AcpJsonRpcException { Code: AcpErrorCodes.AuthRequired } auth => new AgentProviderFailureException(
                connection.InSandbox
                    ? $"{_options.AgentName} is not signed in, in the sandbox of this session, where it has a home folder of its own. Give its provider a token or an API key in its Environment (Settings > Providers), then send the message again. ({auth.Message})"
                    : $"{_options.AgentName} is not signed in. Sign in from Settings > Providers, then send the message again. ({auth.Message})",
                new AgentFailureInfo(AgentFailureKind.Auth), auth),
            AcpJsonRpcException rpc => new AgentProviderFailureException($"{_options.AgentName}: {rpc.Message}", AgentFailureClassifier.Classify(rpc) ?? new AgentFailureInfo(AgentFailureKind.Other), rpc),
            AcpConnectionClosedException closed => Closed(connection, closed),
            OperationCanceledException canceled => canceled,
            _ => failure,
        };

    private AgentProviderFailureException Closed(Connection connection, Exception? inner = null)
        => new(inner?.Message ?? connection.DescribeExit(), new AgentFailureInfo(AgentFailureKind.Unavailable), inner);

    private static void DrainEvents(Connection connection)
    {
        while (connection.Events.TryRead(out var turnEvent))
        {
            if (turnEvent is PromptEndedEvent)
            {
                connection.EndPrompt();
            }
        }
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

    private void CancelIdleTimer() => Cancel(Interlocked.Exchange(ref _idle, null));

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

    // The process of an idle session is closed: the next turn resumes the session of the agent.
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
                if (!idle.IsCancellationRequested && Volatile.Read(ref _disposed) == 0 && _connection is not { PromptActive: true })
                {
                    await CloseConnectionAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _turnGate.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _idle, null, idle);
            idle.Dispose();
        }
    }

    private static string? NormalizePermissionMode(string? permissionMode)
        => string.IsNullOrWhiteSpace(permissionMode) ? null : permissionMode.Trim();
}
