using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>
/// One CodeAlta session driven through the Codex app-server: one app-server process and one Codex thread.
/// </summary>
/// <remarks>
/// <para>
/// Codex runs its own loop: within one turn it calls its model, runs its tools and calls its model again. The agent
/// session of CodeAlta expects a provider that answers one model call at a time and lets the session run the tools.
/// This class presents the first as the second: each call to <see cref="ExecuteTurnAsync"/> returns the next assistant
/// message of Codex (its text up to a tool call, or up to the end of the turn), and the tool calls of that message are
/// "run" by handlers that wait for the result Codex reports (<see cref="HarnessToolCallBridge"/>).
/// </para>
/// <para>
/// The tools of CodeAlta are offered to Codex through an MCP server of the session over HTTP: such a call is run by the
/// session of CodeAlta itself, and its result is given back to Codex. A message the user sends while Codex works on a
/// turn steers that turn (<c>turn/steer</c>).
/// </para>
/// </remarks>
internal sealed partial class CodexCliSession : IAsyncDisposable
{
    /// <summary>How the instructions of CodeAlta reach Codex: as developer instructions of its thread.</summary>
    public const string InstructionsChannel = "codex-developer-instructions";

    private static readonly TimeSpan ToolsSyncTimeout = TimeSpan.FromSeconds(2);

    private readonly string _sessionId;
    private readonly CodexCliRuntimeOptions _options;
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
    private AgentToolInterceptor? _toolInterceptor;
    private IReadOnlyDictionary<string, AgentToolDefinition> _exposedTools = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
    // Completed when the session gives the next segment, with its tools.
    private TaskCompletionSource _toolsSynced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _idle;
    private int _disposed;

    // What the thread knows of the conversation of CodeAlta.
    private bool _conversationBound;
    private string? _threadId;
    private ThreadOrigin? _origin;
    private int _syncedUsers;
    private int _syncedAssistants;
    private string? _pendingPreamble;
    private readonly List<AgentConversationMessage> _unsent = [];
    private List<CodexCliTurnMark> _turns = [];

    // What the thread was told of the instructions of CodeAlta.
    private string? _instructions;
    private string? _instructionsHash;

    private int _conversationCount;

    public CodexCliSession(string sessionId, CodexCliRuntimeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionId = sessionId;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = options.Logger;
        _mcpServer = new HarnessMcpToolServer(CodexCliPrompts.McpServerName, () => ExposedTools, CallToolFromMcpAsync);
    }

    /// <summary>Where the next thread comes from: a thread to resume, or one to fork, at a turn.</summary>
    private sealed record ThreadOrigin(string? ResumeId, string? ForkFromId, string? ForkLastTurnId);

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

    /// <summary>Gives the handlers of a run before its first turn.</summary>
    public void AttachRun(AgentProviderRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            _run = context;
        }

        if (_connection is not { TurnActive: true })
        {
            // The tool calls of the previous run all have their result.
            _bridge.ForgetCompleted();
        }
    }

    public AgentToolDefinition? ResolveTool(AgentMessagePart.ToolCall toolCall, AgentToolDefinition? registered)
        => _bridge.Resolve(toolCall, registered, $"A tool run by {_options.ProviderName}.", InterruptAsync);

    public Task? WhenToolStarts(AgentMessagePart.ToolCall toolCall) => _bridge.WhenStarted(toolCall.CallId);

    /// <summary>
    /// Returns the next assistant message of Codex for the session: it starts or resumes the app-server and its thread
    /// when needed, sends the user messages the thread has not seen yet (a turn, or a steer of the running one), and
    /// reads until a message is complete.
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
                _toolInterceptor = request.ToolInterceptor;
            }

            var newMessages = BindConversation(request);
            if (SetExposedTools(request.Tools))
            {
                await AnnounceToolsAsync(cancellationToken).ConfigureAwait(false);
            }

            // The calls of tools of CodeAlta that waited for this segment answer Codex now.
            Interlocked.Exchange(ref _toolsSynced, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

            if (_connection is { TurnActive: true } running)
            {
                // What the user adds while Codex works goes into its running turn; a turn that takes no more input
                // gets it as its next turn.
                if (newMessages.Count > 0 && !await TrySteerAsync(running, newMessages, cancellationToken).ConfigureAwait(false))
                {
                    _unsent.AddRange(newMessages);
                }
            }
            else
            {
                var messages = new List<AgentConversationMessage>(_unsent.Count + newMessages.Count);
                messages.AddRange(_unsent);
                messages.AddRange(newMessages);
                _unsent.Clear();
                if (messages.Count == 0)
                {
                    ArmIdleTimer();
                    return CreateResponse([], [], requiresFollowUp: false, _connection, turnId: null);
                }

                await EnsureConnectionAsync(request, onSessionUpdate, cancellationToken).ConfigureAwait(false);
                await StartTurnAsync(request, messages, cancellationToken).ConfigureAwait(false);
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
            // The turn is given up, by the user or by a failure: Codex does not go on with it alone.
            await InterruptAsync().ConfigureAwait(false);
            ArmIdleTimer();
            throw;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>Asks Codex to compact the context of its thread (<c>thread/compact/start</c>), and waits for it.</summary>
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
            if ((_threadId is null && _origin is null) || _pendingPreamble is not null)
            {
                return new AgentCompactionOutcome(true, $"{_options.ProviderName} holds no context for this session yet: nothing to compact.");
            }

            await EnsureConnectionAsync(request, static (_, _) => ValueTask.CompletedTask, cancellationToken).ConfigureAwait(false);
            var connection = _connection!;
            connection.BeginTurn(null);
            try
            {
                await connection.Client.ThreadCompactStartAsync(new ThreadCompactStartParams { ThreadId = connection.ThreadId }, Timeout(cancellationToken)).ConfigureAwait(false);
            }
            catch (CodexJsonRpcException exception)
            {
                connection.EndTurn();
                return new AgentCompactionOutcome(false, $"{_options.ProviderName} could not compact its context: {exception.Message}");
            }

            // The compaction runs as a turn of the thread: it ends with it.
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
                    case TurnEndedEvent ended:
                        connection.EndTurn();
                        return ended.Turn.Status is TurnStatus.Failed && ended.Turn.Error is { } error
                            ? new AgentCompactionOutcome(false, $"{_options.ProviderName} could not compact its context: {error.Message}")
                            : new AgentCompactionOutcome(true, $"{_options.ProviderName} compacted its context.");
                    case ClosedEvent closed:
                        throw new AgentProviderFailureException(closed.Message, new AgentFailureInfo(AgentFailureKind.Unavailable));
                }
            }
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

    /// <summary>Puts the session in a permission mode now: the approval requests of Codex are answered by it.</summary>
    public void SetPermissionMode(string? permissionMode)
    {
        lock (_gate)
        {
            _permissionMode = NormalizePermissionMode(permissionMode);
        }
    }

    /// <summary>
    /// Stops the turn Codex works on (<c>turn/interrupt</c>), and its process when it does not end in time. The thread
    /// stays resumable either way.
    /// </summary>
    public async Task InterruptAsync()
    {
        var connection = _connection;
        CancelApprovals(connection);
        _bridge.FailOutstanding("The tool call was interrupted.");
        if (connection is null || !connection.TurnActive)
        {
            return;
        }

        try
        {
            if (connection.ActiveTurnId is { } turnId)
            {
                using var timeout = new CancellationTokenSource(_options.CancelTimeout);
                await connection.Client.TurnInterruptAsync(new TurnInterruptParams { ThreadId = connection.ThreadId, TurnId = turnId }, timeout.Token).ConfigureAwait(false);
            }

            await WaitForTurnEndAsync(connection, _options.CancelTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or CodexJsonRpcException or CodexConnectionClosedException or OperationCanceledException)
        {
            // Codex did not stop: its process is, and the next turn resumes its thread.
            await CloseConnectionAsync().ConfigureAwait(false);
        }
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

    // A session read again without its conversation (AgentEarlierConversation) keeps it unread while Codex continues the
    // thread it holds: the binding counts the messages not read as the state it was read with counted them. Any other
    // binding tells Codex what was said, and reads them: the request is then given its whole conversation.
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

    // Whether BindConversation keeps the thread Codex holds for a request whose earlier messages were not read.
    private bool ContinuesWithoutEarlierConversation(AgentTurnRequest request, bool hasNewPrompt)
    {
        if (CodexCliProviderState.Read(request.EarlierConversation!.State) is null)
        {
            return false;
        }

        var (users, assistants, trailing) = CountConversation(request, hasNewPrompt);
        if (_conversationBound)
        {
            return users >= _syncedUsers && assistants >= _syncedAssistants;
        }

        var persisted = CodexCliProviderState.Read(request.State);
        return persisted is not null && request.State.ProviderBranch is null && persisted.Assistants == assistants && persisted.Users <= users - trailing;
    }

    // The messages a request has not read of its conversation, as the state of the session counted them when it was read.
    private static (int Users, int Assistants) EarlierCounts(AgentTurnRequest request)
        => request.EarlierConversation is { Messages: null } earlier && CodexCliProviderState.Read(earlier.State) is { } resumed
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

    // Decides what the thread already knows of the conversation and returns the user messages it has to be sent.
    private List<AgentConversationMessage> BindConversation(AgentTurnRequest request, bool hasNewPrompt = true)
    {
        var conversation = request.Conversation;
        var (users, assistants, trailing) = CountConversation(request, hasNewPrompt);
        var startsNew = false;
        if (!_conversationBound || users < _syncedUsers || assistants < _syncedAssistants)
        {
            var persisted = CodexCliProviderState.Read(request.State);
            var priorUsers = users - trailing;
            var bound = _conversationBound;
            _conversationBound = true;
            _unsent.Clear();
            _pendingPreamble = null;
            var origin = bound
                ? ForkOfThisThread(priorUsers, assistants)
                : persisted is not null && persisted.Assistants == assistants && persisted.Users <= priorUsers ? OriginOf(persisted, request.State.ProviderBranch) : null;
            if (origin is not null)
            {
                // Codex holds the conversation in a thread: it is resumed, or forked where the conversation now ends.
                _origin = origin;
                _threadId = origin.ResumeId;
                _turns = bound ? [.. _turns.TakeWhile(turn => turn.Assistants <= assistants)] : [.. persisted!.Turns];
                if (!bound)
                {
                    _instructions = null;
                    _instructionsHash = persisted!.Instructions;
                }
            }
            else
            {
                // A new session, one whose history another provider wrote or rewrote, or one cut where no turn ended:
                // Codex starts a new thread and is told what was said before.
                _origin = null;
                _threadId = null;
                _turns = [];
                startsNew = true;
                _pendingPreamble = priorUsers > 0 || assistants > 0
                    ? HarnessHistory.CreatePreviousConversation([.. conversation.Take(conversation.Count - trailing)])
                    : null;
            }

            if (bound)
            {
                // The running thread has another history: another one is opened.
                _ = CloseConnectionAsync();
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
            // Prompts another provider left unanswered are one request: sent apart, Codex would answer the first, then
            // the next as if it came after that answer.
            return [new AgentConversationMessage(AgentConversationRole.User, [.. newMessages.SelectMany(static message => message.Parts)])];
        }

        return newMessages;
    }

    // The thread a session that was forked or rewound continues: the same one when nothing follows the point there,
    // else a fork of it at the turn the point ends, when that turn is known.
    private static ThreadOrigin? OriginOf(CodexCliProviderState persisted, AgentProviderBranch? branch)
    {
        if (branch is null || (branch.Kind == AgentProviderBranchKind.Rewind && branch.AtEnd))
        {
            return new ThreadOrigin(persisted.ThreadId, null, null);
        }

        if (branch.AtEnd)
        {
            // Another session goes on from the end of the thread: it gets a thread of its own.
            return new ThreadOrigin(null, persisted.ThreadId, null);
        }

        return persisted.FindTurn(persisted.Users, persisted.Assistants) is { } turn ? new ThreadOrigin(null, persisted.ThreadId, turn.Id) : null;
    }

    // The conversation of this session was cut (a rewind in this process): the thread is forked at the turn it ends.
    private ThreadOrigin? ForkOfThisThread(int users, int assistants)
    {
        var thread = _connection?.ThreadId ?? _threadId ?? _origin?.ForkFromId;
        return thread is not null && _turns.LastOrDefault(turn => turn.Users == users && turn.Assistants == assistants) is { } turn
            ? new ThreadOrigin(null, thread, turn.Id)
            : null;
    }

    // Returns whether the tools differ from the ones Codex may have listed: a tool registered during the run (the UI
    // tools, an MCP server of CodeAlta) or one gone.
    private bool SetExposedTools(IReadOnlyList<AgentToolDefinition> tools)
    {
        var exposed = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
        foreach (var (name, definition) in AgentToolBridge.CreateDefinitionMap(tools))
        {
            if (!CodexCliPrompts.ReplacedTools.Contains(name))
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

    // Codex, which listed the tools of CodeAlta, is told to list them again, on the stream of events it opened.
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
                _logger?.Debug($"{_options.ProviderName} opened no stream of MCP events: the tools added during the run reach it when it lists the tools again.");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    // A tool of CodeAlta can register tools for the run (`alta ui activate`): they reach the session with its next
    // segment. Its result waits for that segment, a short while at most, so that Codex is told of the new tools with
    // the result of the call that added them.
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

    private string CreateInstructions(AgentTurnRequest request)
        => CodexCliPrompts.CreateInstructions(
            AgentInstructionText.DeveloperAndSession(request),
            hasGateway: ExposedTools.ContainsKey(CodexCliPrompts.GatewayTool),
            webSearch: WebSearchOf(request));

    private static bool WebSearchOf(AgentTurnRequest request) => request.DisabledTools?.Contains("webget") != true;

    // Starts the turn: a change of the instructions, the conversation of another provider, then the messages of the user.
    private async Task StartTurnAsync(AgentTurnRequest request, IReadOnlyList<AgentConversationMessage> messages, CancellationToken cancellationToken)
    {
        var connection = _connection!;
        var preambles = new List<string>();
        var instructions = CreateInstructions(request);
        var hash = HarnessInstructions.Hash(instructions);
        if (!string.Equals(hash, _instructionsHash, StringComparison.Ordinal))
        {
            preambles.Add(HarnessInstructions.CreateUpdate(_instructions, instructions, CodexCliPrompts.GivenAt));
        }

        _instructions = instructions;
        _instructionsHash = hash;
        if (_pendingPreamble is { } history)
        {
            preambles.Add(history);
            _pendingPreamble = null;
        }

        var parameters = new TurnStartParams
        {
            ThreadId = connection.ThreadId,
            Input = CodexCliPrompts.CreateInput(preambles, messages),
            ApprovalPolicy = ApprovalPolicyOf(_permissionMode),
            Cwd = connection.WorkingDirectory,
            Model = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId,
            Effort = CodexCliModels.FromEffort(request.ReasoningEffort) is { } effort ? (ReasoningEffort?)new ReasoningEffort { Value = effort } : null,
            Summary = request.OmitReasoningSummaries ? new ReasoningSummary.None() : null,
        };
        connection.CurrentModel = parameters.Model ?? connection.CurrentModel;
        connection.BeginTurn(null);
        try
        {
            var started = await connection.Client.TurnStartAsync(parameters, Timeout(cancellationToken)).ConfigureAwait(false);
            connection.ActiveTurnId ??= started.Turn.Id;
        }
        catch (Exception exception) when (exception is CodexJsonRpcException or CodexConnectionClosedException or TimeoutException)
        {
            connection.EndTurn();
            throw exception is CodexJsonRpcException rpc ? CodexCliFailures.FromRequest(rpc, "start the turn") : Closed(connection, exception);
        }
    }

    // Sends messages into the turn Codex works on; false when it takes no more input.
    private async Task<bool> TrySteerAsync(Connection connection, IReadOnlyList<AgentConversationMessage> messages, CancellationToken cancellationToken)
    {
        if (connection.ActiveTurnId is not { } turnId)
        {
            return false;
        }

        try
        {
            await connection.Client.TurnSteerAsync(
                    new TurnSteerParams { ThreadId = connection.ThreadId, ExpectedTurnId = turnId, Input = CodexCliPrompts.CreateInput([], messages) },
                    Timeout(cancellationToken))
                .ConfigureAwait(false);
            return true;
        }
        catch (CodexJsonRpcException exception)
        {
            _logger?.Debug($"{_options.ProviderName} took no steer of its turn: {exception.Message}. The messages go with its next turn.");
            return false;
        }
    }

    /// <summary>The approval policy of a turn: Codex asks for what is not plainly safe, unless the mode allows everything.</summary>
    internal static AskForApproval ApprovalPolicyOf(string? permissionMode)
        => permissionMode == "bypassPermissions" ? new AskForApproval.Never() : new AskForApproval.Untrusted();

    // Reads the events of the turn until an assistant message is complete: at a tool call, or at the end of the turn.
    private async Task<AgentTurnResponse> ReadSegmentAsync(
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new InvalidOperationException($"{_options.ProviderName} is not running.");
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
                    segment.Parts.Add(new AgentMessagePart.ToolCall(toolCall.Call.CallId, toolCall.Call.Name, toolCall.Call.Arguments));
                    segment.ContentIds.Add(null);
                    return CreateResponse(segment.Parts, segment.ContentIds, requiresFollowUp: false, connection, turnId: null);
                case UsageEvent:
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
                            new AgentTurnSessionUpdate { Kind = notice.Warning ? AgentSessionUpdateKind.Warning : AgentSessionUpdateKind.Info, Message = notice.Text },
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case TurnEndedEvent ended:
                    connection.EndTurn();
                    // A tool call without a result when the turn ends will never have one.
                    _bridge.FailOutstanding($"{_options.ProviderName} ended its turn before the tool call completed.");
                    if (ended.Turn.Status is TurnStatus.Failed)
                    {
                        throw CodexCliFailures.FromTurnError(ended.Turn.Error ?? new TurnError { Message = "The turn failed." });
                    }

                    if (ended.Turn.Status is TurnStatus.Interrupted)
                    {
                        segment.Parts.Add(new AgentMessagePart.Text($"[{_options.ProviderName} stopped the turn.]"));
                        segment.ContentIds.Add(null);
                    }

                    return CreateResponse(segment.Parts, segment.ContentIds, requiresFollowUp: _unsent.Count > 0, connection, ended.Turn.Id);
                case ClosedEvent closed:
                    throw new AgentProviderFailureException(closed.Message, new AgentFailureInfo(AgentFailureKind.Unavailable));
            }
        }
    }

    private AgentTurnResponse CreateResponse(List<AgentMessagePart> parts, List<string?> contentIds, bool requiresFollowUp, Connection? connection, string? turnId)
    {
        var durable = parts.Count > 0 || !requiresFollowUp;
        if (durable)
        {
            _syncedAssistants++;
        }

        if (turnId is not null)
        {
            _turns.Add(new CodexCliTurnMark(turnId, _syncedUsers, _syncedAssistants));
            if (_turns.Count > CodexCliProviderState.MaxTurns)
            {
                _turns.RemoveRange(0, _turns.Count - CodexCliProviderState.MaxTurns);
            }
        }

        var threadId = connection?.ThreadId is { Length: > 0 } open ? open : _threadId;
        return new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [.. parts]),
            AssistantPartContentIds = contentIds.Exists(static id => id is not null) ? [.. contentIds] : null,
            RequiresProviderFollowUp = requiresFollowUp,
            Usage = connection is null ? null : CreateUsage(connection, _conversationCount + (durable ? 1 : 0)),
            ProviderSessionId = threadId,
            ProviderState = threadId is { } thread
                ? new CodexCliProviderState(thread, _syncedUsers, _syncedAssistants, _instructionsHash, [.. _turns]).ToJson()
                : null,
        };
    }

    private static AgentSessionUsage? CreateUsage(Connection connection, int messageCount)
    {
        if (connection.LastUsage is not { } usage)
        {
            return null;
        }

        var last = usage.Last;
        return new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(last.TotalTokens, usage.ModelContextWindow, messageCount, Label: "Active context window", TotalContextEnvelope: usage.ModelContextWindow),
            LastOperation: new AgentOperationUsageSnapshot(
                Model: connection.CurrentModel,
                InputTokens: last.InputTokens,
                OutputTokens: last.OutputTokens,
                CacheReadTokens: last.CachedInputTokens,
                CacheWriteTokens: last.CacheWriteInputTokens,
                CachedInputTokens: last.CachedInputTokens,
                ReasoningTokens: last.ReasoningOutputTokens,
                Cost: null),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.ProviderUsage,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private AgentProviderFailureException Closed(Connection connection, Exception? inner = null)
        => new(inner?.Message ?? connection.Client.DescribeExit(), new AgentFailureInfo(AgentFailureKind.Unavailable), inner);

    // Reads the events of a turn that is being stopped until it ends: they are not part of a segment.
    private static async Task WaitForTurnEndAsync(Connection connection, TimeSpan timeout)
    {
        using var source = new CancellationTokenSource(timeout);
        while (connection.TurnActive)
        {
            var turnEvent = await connection.Events.ReadAsync(source.Token).ConfigureAwait(false);
            if (turnEvent is TurnEndedEvent or ClosedEvent)
            {
                connection.EndTurn();
            }
        }
    }

    private CancellationToken Timeout(CancellationToken cancellationToken)
    {
        // A source that ends with the session: a request it cancels is given up.
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        source.CancelAfter(_options.RequestTimeout);
        return source.Token;
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

    // The process of an idle session is closed: the next turn resumes its thread.
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
                if (!idle.IsCancellationRequested && Volatile.Read(ref _disposed) == 0 && _connection is not { TurnActive: true })
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
