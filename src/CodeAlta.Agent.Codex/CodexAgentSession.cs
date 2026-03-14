using System.Collections.Concurrent;
using System.Threading.Channels;
using CodeAlta.CodexSdk;

namespace CodeAlta.Agent.Codex;

/// <summary>
/// Codex thread-backed implementation of <see cref="IAgentSession"/>.
/// </summary>
public sealed class CodexAgentSession : ICodexAgentSession
{
    private readonly CodexAgentBackend _backend;
    private readonly Channel<AgentEvent> _eventChannel;
    private readonly ConcurrentDictionary<Guid, Action<AgentEvent>> _subscribers = new();
    private readonly object _handlerLock = new();
    private string? _workingDirectory;
    private string? _model;
    private AgentReasoningEffort? _reasoningEffort;
    private SandboxMode? _sandboxMode;
    private AgentPermissionRequestHandler _permissionHandler;
    private AgentUserInputRequestHandler? _userInputHandler;
    private AgentRunId? _activeRunId;
    private readonly Dictionary<string, AgentContentKind> _agentMessageKindsByItemId = new(StringComparer.Ordinal);
    private bool _disposed;

    internal CodexAgentSession(
        CodexAgentBackend backend,
        string threadId,
        string? workingDirectory,
        string? model,
        AgentReasoningEffort? reasoningEffort,
        SandboxMode? sandboxMode,
        AgentPermissionRequestHandler permissionHandler,
        AgentUserInputRequestHandler? userInputHandler)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(permissionHandler);

        _backend = backend;
        _workingDirectory = workingDirectory;
        _model = model;
        _reasoningEffort = reasoningEffort;
        _sandboxMode = sandboxMode;
        _permissionHandler = permissionHandler;
        _userInputHandler = userInputHandler;
        ThreadId = threadId;
        _eventChannel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <inheritdoc />
    public AgentBackendId BackendId => AgentBackendIds.Codex;

    /// <inheritdoc />
    public string SessionId => ThreadId;

    /// <inheritdoc />
    public string ThreadId { get; }

    /// <inheritdoc />
    public string? WorkspacePath => null;

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> StreamEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _eventChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<AgentEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = Guid.CreateVersion7();
        _subscribers.TryAdd(key, handler);
        return new Unsubscriber(() => _subscribers.TryRemove(key, out _));
    }

    /// <inheritdoc />
    public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? workingDirectory;
        string? model;
        AgentReasoningEffort? reasoningEffort;
        SandboxMode? sandboxMode;
        lock (_handlerLock)
        {
            workingDirectory = _workingDirectory;
            model = _model;
            reasoningEffort = _reasoningEffort;
            sandboxMode = _sandboxMode;
        }

        var parameters = CodexAgentMapper.ToTurnStartParams(
            ThreadId,
            options.Input,
            workingDirectory,
            model,
            reasoningEffort,
            sandboxMode);

        var response = await _backend.Client.TurnStartAsync(parameters, cancellationToken).ConfigureAwait(false);
        var runId = new AgentRunId(response.Turn.Id);
        lock (_handlerLock)
        {
            _activeRunId = runId;
        }

        return runId;
    }

    /// <inheritdoc />
    public async Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        AgentRunId? activeRunId;
        lock (_handlerLock)
        {
            activeRunId = _activeRunId;
        }

        var expectedRunId = options.ExpectedRunId ?? activeRunId;
        if (expectedRunId is null)
        {
            throw new InvalidOperationException("Codex steering requires an active run id.");
        }

        var parameters = CodexAgentMapper.ToTurnSteerParams(ThreadId, expectedRunId.Value, options.Input);
        var response = await _backend.Client.TurnSteerAsync(parameters, cancellationToken).ConfigureAwait(false);
        var runId = new AgentRunId(response.TurnId);
        lock (_handlerLock)
        {
            _activeRunId = runId;
        }

        return runId;
    }

    /// <inheritdoc />
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        AgentRunId? activeRunId;
        lock (_handlerLock)
        {
            activeRunId = _activeRunId;
        }

        if (activeRunId is null)
            return;

        await _backend.Client.TurnInterruptAsync(
                new TurnInterruptParams
                {
                    ThreadId = ThreadId,
                    TurnId = activeRunId.Value.Value
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ThreadReadResponse response;
        try
        {
            response = await _backend.Client.ThreadReadAsync(
                    new ThreadReadParams
                    {
                        ThreadId = ThreadId,
                        IncludeTurns = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcException ex) when (IsHistoryUnavailableBeforeFirstMessage(ex))
        {
            return [];
        }

        return CodexAgentMapper.ToHistoryEvents(ThreadId, response.Thread);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        CompleteEventStream();
        _backend.RemoveSession(ThreadId);
        return ValueTask.CompletedTask;
    }

    internal void UpdateSessionOptions(
        string? workingDirectory,
        string? model,
        AgentReasoningEffort? reasoningEffort,
        SandboxMode? sandboxMode,
        AgentPermissionRequestHandler permissionHandler,
        AgentUserInputRequestHandler? userInputHandler)
    {
        ArgumentNullException.ThrowIfNull(permissionHandler);

        lock (_handlerLock)
        {
            _workingDirectory = workingDirectory;
            _model = model;
            _reasoningEffort = reasoningEffort;
            _sandboxMode = sandboxMode;
            _permissionHandler = permissionHandler;
            _userInputHandler = userInputHandler;
        }
    }

    internal void HandleNotification(CodexNotification notification)
    {
        TrackAgentMessageKind(notification);
        var timestamp = DateTimeOffset.UtcNow;
        var eventData = NormalizeNotificationEvent(CodexAgentMapper.ToAgentEvent(ThreadId, notification, timestamp), notification);

        switch (eventData)
        {
            case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }:
                lock (_handlerLock)
                {
                    _activeRunId = null;
                }
                break;
            case AgentContentDeltaEvent { Kind: AgentContentKind.Assistant } delta when delta.RunId is not null:
                lock (_handlerLock)
                {
                    _activeRunId = delta.RunId;
                }
                break;
        }

        Publish(eventData);
    }

    internal static bool IsHistoryUnavailableBeforeFirstMessage(JsonRpcException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Message.Contains("includeTurns is unavailable before first user message", StringComparison.OrdinalIgnoreCase);
    }

    private void TrackAgentMessageKind(CodexNotification notification)
    {
        lock (_handlerLock)
        {
            switch (notification)
            {
                case CodexNotification.ItemStarted { Data.Item: ThreadItem.AgentMessageThreadItem message }:
                    _agentMessageKindsByItemId[message.Id] = CodexAgentMapper.ToAgentContentKind(message.Phase);
                    break;

                case CodexNotification.ItemCompleted { Data.Item: ThreadItem.AgentMessageThreadItem message }:
                    _agentMessageKindsByItemId.Remove(message.Id, out _);
                    break;
            }
        }
    }

    private AgentEvent NormalizeNotificationEvent(AgentEvent eventData, CodexNotification notification)
    {
        return notification switch
        {
            CodexNotification.AgentMessageDelta delta when eventData is AgentContentDeltaEvent contentDelta =>
                contentDelta with { Kind = GetTrackedAgentMessageKind(delta.Data.ItemId) ?? contentDelta.Kind },

            _ => eventData,
        };
    }

    private AgentContentKind? GetTrackedAgentMessageKind(string itemId)
    {
        lock (_handlerLock)
        {
            return _agentMessageKindsByItemId.TryGetValue(itemId, out var kind)
                ? kind
                : null;
        }
    }

    internal async Task HandleServerRequestAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        switch (request)
        {
            case ServerRequest.ItemCommandExecutionRequestApprovalRequest commandApproval:
            {
                var permissionRequest = CodexAgentMapper.ToPermissionRequest(
                    ThreadId,
                    commandApproval.Params);
                Publish(permissionRequest);

                CommandExecutionRequestApprovalResponse response;
                AgentPermissionDecision decision;
                try
                {
                    decision = await GetPermissionHandler()
                        .Invoke(permissionRequest, cancellationToken)
                        .ConfigureAwait(false);

                    response = CodexAgentMapper.ToCommandApprovalResponse(decision);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PublishHandlerError("command approval", ex);
                    decision = new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);
                    response = CodexAgentMapper.ToCommandApprovalResponse(decision);
                }

                await _backend.Client.RespondToRequestAsync(
                        commandApproval.Id,
                        response,
                        cancellationToken)
                    .ConfigureAwait(false);
                PublishPermissionResolved(permissionRequest, decision);
                break;
            }
            case ServerRequest.ItemFileChangeRequestApprovalRequest fileApproval:
            {
                var permissionRequest = CodexAgentMapper.ToPermissionRequest(
                    ThreadId,
                    fileApproval.Params);
                Publish(permissionRequest);

                FileChangeRequestApprovalResponse response;
                AgentPermissionDecision decision;
                try
                {
                    decision = await GetPermissionHandler()
                        .Invoke(permissionRequest, cancellationToken)
                        .ConfigureAwait(false);

                    response = CodexAgentMapper.ToFileApprovalResponse(decision);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PublishHandlerError("file approval", ex);
                    decision = new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);
                    response = CodexAgentMapper.ToFileApprovalResponse(decision);
                }

                await _backend.Client.RespondToRequestAsync(
                        fileApproval.Id,
                        response,
                        cancellationToken)
                    .ConfigureAwait(false);
                PublishPermissionResolved(permissionRequest, decision);
                break;
            }
            case ServerRequest.ItemToolRequestUserInputRequest requestUserInput:
            {
                var mappedRequest = CodexAgentMapper.ToAgentUserInputRequest(requestUserInput.Params);
                Publish(mappedRequest);

                ToolRequestUserInputResponse mappedResponse;
                AgentUserInputResponse responsePayload;
                try
                {
                    var handler = GetUserInputHandler();
                    if (handler is null)
                    {
                        throw new InvalidOperationException("No AgentUserInputRequestHandler is configured for this session.");
                    }

                    responsePayload = await handler.Invoke(mappedRequest, cancellationToken).ConfigureAwait(false);
                    mappedResponse = CodexAgentMapper.ToToolRequestUserInputResponse(responsePayload);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PublishHandlerError("tool user input", ex);
                    responsePayload = new AgentUserInputResponse(
                        requestUserInput.Params.Questions.ToDictionary(static question => question.Id, static _ => string.Empty, StringComparer.Ordinal));
                    mappedResponse = CodexAgentMapper.CreateEmptyToolRequestUserInputResponse(requestUserInput.Params);
                }

                await _backend.Client.RespondToRequestAsync(
                        requestUserInput.Id,
                        mappedResponse,
                        cancellationToken)
                    .ConfigureAwait(false);
                Publish(
                    new AgentInteractionEvent(
                        AgentBackendIds.Codex,
                        ThreadId,
                        DateTimeOffset.UtcNow,
                        mappedRequest.RunId,
                        AgentInteractionKind.UserInputResolved,
                        mappedRequest.InteractionId,
                        $"User input resolved ({responsePayload.Answers.Count} answer(s))."));
                break;
            }
            case ServerRequest.ItemToolCallRequest toolCall:
            {
                var runId = new AgentRunId(toolCall.Params.TurnId);
                Publish(
                    new AgentActivityEvent(
                        AgentBackendIds.Codex,
                        ThreadId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentActivityKind.DynamicToolCall,
                        AgentActivityPhase.Requested,
                        toolCall.Params.CallId,
                        toolCall.Params.TurnId,
                        toolCall.Params.Tool,
                        null));

                DynamicToolCallResponse response;
                bool success;
                try
                {
                    var toolResult = await InvokeToolAsync(toolCall.Params, cancellationToken).ConfigureAwait(false);
                    response = CodexAgentMapper.ToDynamicToolCallResponse(toolResult);
                    success = toolResult.Success;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PublishHandlerError("dynamic tool call", ex);
                    success = false;
                    response = CodexAgentMapper.ToDynamicToolCallResponse(
                        new AgentToolResult(
                            Success: false,
                            Items: [new AgentToolResultItem.Text("Dynamic tool call execution failed.")],
                            Error: ex.Message));
                }

                await _backend.Client.RespondToRequestAsync(
                        toolCall.Id,
                        response,
                        cancellationToken)
                    .ConfigureAwait(false);
                Publish(
                    new AgentActivityEvent(
                        AgentBackendIds.Codex,
                        ThreadId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentActivityKind.DynamicToolCall,
                        success ? AgentActivityPhase.Completed : AgentActivityPhase.Failed,
                        toolCall.Params.CallId,
                        toolCall.Params.TurnId,
                        toolCall.Params.Tool,
                        success ? "Dynamic tool call resolved." : "Dynamic tool call failed."));
                break;
            }
        }
    }

    internal void Publish(AgentEvent eventData)
    {
        if (!_eventChannel.Writer.TryWrite(eventData))
            return;

        foreach (var subscriber in _subscribers.Values)
        {
            try
            {
                subscriber(eventData);
            }
            catch
            {
            }
        }
    }

    internal void CompleteEventStream()
    {
        _eventChannel.Writer.TryComplete();
    }

    private AgentPermissionRequestHandler GetPermissionHandler()
    {
        lock (_handlerLock)
        {
            return _permissionHandler;
        }
    }

    private AgentUserInputRequestHandler? GetUserInputHandler()
    {
        lock (_handlerLock)
        {
            return _userInputHandler;
        }
    }

    private Task<AgentToolResult> InvokeToolAsync(DynamicToolCallParams toolCall, CancellationToken cancellationToken)
    {
        var eventData = new AgentErrorEvent(
            AgentBackendIds.Codex,
            ThreadId,
            DateTimeOffset.UtcNow,
            $"Codex dynamic tool call '{toolCall.Tool}' cannot be handled because tools are not registered for this adapter.");
        Publish(eventData);

        return Task.FromResult(new AgentToolResult(
            Success: false,
            Items: [new AgentToolResultItem.Text("Dynamic tool calls are not enabled in this adapter.")],
            Error: "dynamic tool calls are not enabled"));
    }

    private void PublishHandlerError(string handlerName, Exception exception)
    {
        Publish(
            new AgentErrorEvent(
                AgentBackendIds.Codex,
                ThreadId,
                DateTimeOffset.UtcNow,
                $"Failed while handling {handlerName}: {exception.Message}",
                exception));
    }

    private void PublishPermissionResolved(AgentPermissionRequest request, AgentPermissionDecision decision)
    {
        Publish(
            new AgentInteractionEvent(
                request.BackendId,
                request.SessionId,
                DateTimeOffset.UtcNow,
                request.RunId,
                AgentInteractionKind.PermissionResolved,
                request.InteractionId,
                $"Permission resolved: {decision.Kind}."));
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
