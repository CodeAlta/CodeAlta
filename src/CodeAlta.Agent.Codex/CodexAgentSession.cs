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
    private AgentPermissionRequestHandler _permissionHandler;
    private AgentUserInputRequestHandler? _userInputHandler;
    private AgentRunId? _activeRunId;
    private bool _disposed;

    internal CodexAgentSession(
        CodexAgentBackend backend,
        string threadId,
        AgentPermissionRequestHandler permissionHandler,
        AgentUserInputRequestHandler? userInputHandler)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(permissionHandler);

        _backend = backend;
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

        var parameters = new TurnStartParams
        {
            ThreadId = ThreadId,
            Input = CodexAgentMapper.ToTurnInput(options.Input)
        };

        var response = await _backend.Client.TurnStartAsync(parameters, cancellationToken).ConfigureAwait(false);
        var runId = new AgentRunId(response.Turn.Id);
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

        var response = await _backend.Client.ThreadReadAsync(
                new ThreadReadParams
                {
                    ThreadId = ThreadId,
                    IncludeTurns = true
                },
                cancellationToken)
            .ConfigureAwait(false);

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

    internal void UpdateHandlers(
        AgentPermissionRequestHandler permissionHandler,
        AgentUserInputRequestHandler? userInputHandler)
    {
        ArgumentNullException.ThrowIfNull(permissionHandler);

        lock (_handlerLock)
        {
            _permissionHandler = permissionHandler;
            _userInputHandler = userInputHandler;
        }
    }

    internal void HandleNotification(CodexNotification notification)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var eventData = CodexAgentMapper.ToAgentEvent(ThreadId, notification, timestamp);

        switch (eventData)
        {
            case AgentSessionIdleEvent:
                lock (_handlerLock)
                {
                    _activeRunId = null;
                }
                break;
            case AgentAssistantMessageDeltaEvent delta when delta.RunId is not null:
                lock (_handlerLock)
                {
                    _activeRunId = delta.RunId;
                }
                break;
        }

        Publish(eventData);
    }

    internal async Task HandleServerRequestAsync(CodexServerRequest request, CancellationToken cancellationToken)
    {
        switch (request)
        {
            case CodexServerRequest.CommandExecutionApproval commandApproval:
            {
                CommandExecutionRequestApprovalResponse response;
                try
                {
                    var permissionRequest = CodexAgentMapper.ToPermissionRequest(
                        ThreadId,
                        commandApproval.Data);

                    var decision = await GetPermissionHandler()
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
                    response = CodexAgentMapper.ToCommandApprovalResponse(
                        new AgentPermissionDecision(AgentPermissionDecisionKind.Deny));
                }

                await _backend.Client.RespondToApprovalAsync(
                        commandApproval.RequestId,
                        response,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case CodexServerRequest.FileChangeApproval fileApproval:
            {
                FileChangeRequestApprovalResponse response;
                try
                {
                    var permissionRequest = CodexAgentMapper.ToPermissionRequest(
                        ThreadId,
                        fileApproval.Data);

                    var decision = await GetPermissionHandler()
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
                    response = CodexAgentMapper.ToFileApprovalResponse(
                        new AgentPermissionDecision(AgentPermissionDecisionKind.Deny));
                }

                await _backend.Client.RespondToApprovalAsync(
                        fileApproval.RequestId,
                        response,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case CodexServerRequest.ToolRequestUserInput requestUserInput:
            {
                ToolRequestUserInputResponse mappedResponse;
                try
                {
                    var handler = GetUserInputHandler();
                    if (handler is null)
                    {
                        throw new InvalidOperationException("No AgentUserInputRequestHandler is configured for this session.");
                    }

                    var mappedRequest = CodexAgentMapper.ToAgentUserInputRequest(requestUserInput.Data);
                    var response = await handler.Invoke(mappedRequest, cancellationToken).ConfigureAwait(false);
                    mappedResponse = CodexAgentMapper.ToToolRequestUserInputResponse(response);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PublishHandlerError("tool user input", ex);
                    mappedResponse = CodexAgentMapper.CreateEmptyToolRequestUserInputResponse(requestUserInput.Data);
                }

                await _backend.Client.RespondToApprovalAsync(
                        requestUserInput.RequestId,
                        mappedResponse,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case CodexServerRequest.ToolCall toolCall:
            {
                DynamicToolCallResponse response;
                try
                {
                    var toolResult = await InvokeToolAsync(toolCall.Data, cancellationToken).ConfigureAwait(false);
                    response = CodexAgentMapper.ToDynamicToolCallResponse(toolResult);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PublishHandlerError("dynamic tool call", ex);
                    response = CodexAgentMapper.ToDynamicToolCallResponse(
                        new AgentToolResult(
                            Success: false,
                            Items: [new AgentToolResultItem.Text("Dynamic tool call execution failed.")],
                            Error: ex.Message));
                }

                await _backend.Client.RespondToApprovalAsync(
                        toolCall.RequestId,
                        response,
                        cancellationToken)
                    .ConfigureAwait(false);
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

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
