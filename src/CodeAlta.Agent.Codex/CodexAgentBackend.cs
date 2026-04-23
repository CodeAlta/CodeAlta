using System.Collections.Concurrent;
using CodeAlta.CodexSdk;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Codex;

/// <summary>
/// Codex app-server implementation of <see cref="IAgentBackend"/>.
/// </summary>
public sealed class CodexAgentBackend : ICodexAgentBackend
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Codex");
    private readonly ConcurrentDictionary<string, CodexAgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingServerRequestInfo> _pendingServerRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly CodexAgentBackendOptions _options;
    private CancellationTokenSource? _pumpCancellationTokenSource;
    private Task? _pumpTask;
    private CodexClient? _client;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodexAgentBackend"/> class.
    /// </summary>
    /// <param name="options">Backend options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public CodexAgentBackend(CodexAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public AgentBackendId BackendId => AgentBackendIds.Codex;

    /// <inheritdoc />
    public string DisplayName => "Codex";

    /// <inheritdoc />
    public CodexClient Client => _client ?? throw new InvalidOperationException("The Codex backend is not started.");

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
                return;

            _client = await CodexClient.StartAsync(
                    _options.ClientInfo,
                    _options.ExperimentalApi,
                    _options.OptOutNotificationMethods,
                    _options.ProcessOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            _pumpCancellationTokenSource = new CancellationTokenSource();
            _pumpTask = RunMessagePumpAsync(_pumpCancellationTokenSource.Token);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var models = new List<AgentModelInfo>();
        string? cursor = null;
        do
        {
            var response = await client.ModelListAsync(
                    new ModelListParams
                    {
                        Cursor = cursor,
                        Limit = 100
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            models.AddRange(response.Data.Select(CodexAgentMapper.ToAgentModelInfo));
            cursor = response.NextCursor;
        } while (cursor is not null);

        return models;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var sessions = new List<AgentSessionMetadata>();
        string? cursor = null;
        do
        {
            var response = await client.ThreadListAsync(
                    new ThreadListParams
                    {
                        Cursor = cursor,
                        Limit = 100,
                        Cwd = !string.IsNullOrEmpty(filter?.Cwd) ? new ThreadListCwdFilter.StringValue() { Value = filter.Cwd } : null,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var thread in response.Data)
            {
                if (!CodexAgentMapper.MatchesFilter(thread, filter))
                    continue;

                sessions.Add(CodexAgentMapper.ToAgentSessionMetadata(thread));
            }

            cursor = response.NextCursor;
        } while (cursor is not null);

        return sessions;
    }

    /// <inheritdoc />
    public async Task<IAgentSession> CreateSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateTools(options.Tools);

        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var parameters = CodexAgentMapper.ToThreadStartParams(options, _options.ApprovalPolicy, _options.SandboxMode);
        var response = await client.ThreadStartAsync(parameters, cancellationToken).ConfigureAwait(false);
        return RegisterSession(
            response.Thread.Id,
            options.WorkingDirectory,
            options.Model,
            options.ReasoningEffort,
            _options.SandboxMode,
            options.OnPermissionRequest,
            options.OnUserInputRequest);
    }

    /// <inheritdoc />
    public async Task<IAgentSession> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        ValidateTools(options.Tools);

        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var parameters = CodexAgentMapper.ToThreadResumeParams(sessionId, options, _options.ApprovalPolicy, _options.SandboxMode);
        var response = await client.ThreadResumeAsync(parameters, cancellationToken).ConfigureAwait(false);
        return RegisterSession(
            response.Thread.Id,
            options.WorkingDirectory,
            options.Model,
            options.ReasoningEffort,
            _options.SandboxMode,
            options.OnPermissionRequest,
            options.OnUserInputRequest);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    internal void RemoveSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    internal void LogServerRequestReceived(ServerRequest request, string threadId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var info = CreatePendingServerRequestInfo(request, threadId);
        _pendingServerRequests[info.Key] = info;
        LogDebug(
            $"Codex server request received method={info.Method} requestId={info.RequestId} threadId={info.ThreadId} turnId={info.TurnId ?? "<none>"} itemId={info.ItemId ?? "<none>"} callId={info.CallId ?? "<none>"} tool={info.Tool ?? "<none>"}");
    }

    internal void LogServerRequestResponseStarted(ServerRequest request, string summary)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(summary);

        var key = GetPendingServerRequestKey(request);
        if (_pendingServerRequests.TryGetValue(key, out var info))
        {
            info.ResponseStartedAt = DateTimeOffset.UtcNow;
            info.ResponseSummary = summary;
            LogDebug(
                $"Codex server request response starting method={info.Method} requestId={info.RequestId} threadId={info.ThreadId} summary={summary}");
            return;
        }

        LogWarn(
            $"Codex server request response starting without pending request requestId={FormatRequestId(TryGetRequestId(request))} summary={summary}");
    }

    internal void LogServerRequestResponseSent(ServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = GetPendingServerRequestKey(request);
        if (_pendingServerRequests.TryGetValue(key, out var info))
        {
            info.ResponseSentAt = DateTimeOffset.UtcNow;
            LogDebug(
                $"Codex server request response sent method={info.Method} requestId={info.RequestId} threadId={info.ThreadId} summary={info.ResponseSummary ?? "<none>"} elapsedMs={(long)(info.ResponseSentAt.Value - info.ReceivedAt).TotalMilliseconds}");
            return;
        }

        LogWarn($"Codex server request response sent without pending request requestId={FormatRequestId(TryGetRequestId(request))}");
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await client.ThreadArchiveAsync(new ThreadArchiveParams { ThreadId = sessionId }, cancellationToken).ConfigureAwait(false);

        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        return true;
    }

    private static void ValidateTools(IReadOnlyList<AgentToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
            return;

        throw new NotSupportedException(
            "Codex dynamic tool registration is not currently available through CodeAlta.CodexSdk thread/start params.");
    }

    private CodexAgentSession RegisterSession(
        string threadId,
        string? workingDirectory,
        string? model,
        AgentReasoningEffort? reasoningEffort,
        SandboxMode? sandboxMode,
        AgentPermissionRequestHandler permissionHandler,
        AgentUserInputRequestHandler? userInputHandler)
    {
        var session = _sessions.GetOrAdd(
            threadId,
            static (key, tuple) => new CodexAgentSession(
                tuple.Backend,
                key,
                tuple.WorkingDirectory,
                tuple.Model,
                tuple.ReasoningEffort,
                tuple.SandboxMode,
                tuple.PermissionHandler,
                tuple.UserInputHandler),
            (
                Backend: this,
                WorkingDirectory: workingDirectory,
                Model: model,
                ReasoningEffort: reasoningEffort,
                SandboxMode: sandboxMode,
                PermissionHandler: permissionHandler,
                UserInputHandler: userInputHandler));

        session.UpdateSessionOptions(workingDirectory, model, reasoningEffort, sandboxMode, permissionHandler, userInputHandler);
        return session;
    }

    private async Task<CodexClient> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return Client;
    }

    private async Task RunMessagePumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in Client.StreamAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (message)
                {
                    case CodexNotification notification:
                        DispatchNotification(notification);
                        break;

                    case ServerRequest request:
                        await DispatchServerRequestAsync(request, cancellationToken).ConfigureAwait(false);
                        break;

                    case CodexUnknownServerRequest unknownRequest:
                        await DispatchUnknownServerRequestAsync(unknownRequest, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var session in _sessions.Values)
            {
                session.Publish(
                    new AgentErrorEvent(
                        AgentBackendIds.Codex,
                        session.SessionId,
                        now,
                        ex.Message,
                        ex));
            }
        }
        finally
        {
            foreach (var session in _sessions.Values)
            {
                session.CompleteEventStream();
            }
        }
    }

    private void DispatchNotification(CodexNotification notification)
    {
        if (notification is CodexNotification.ServerRequestResolved resolved)
        {
            HandleServerRequestResolved(resolved.Data);
        }

        if (notification is CodexNotification.AccountRateLimitsUpdated)
        {
            foreach (var activeSession in _sessions.Values)
            {
                activeSession.HandleNotification(notification);
            }

            return;
        }

        if (!CodexAgentMapper.TryGetThreadId(notification, out var threadId) || threadId is null)
            return;

        if (!_sessions.TryGetValue(threadId, out var session))
            return;

        session.HandleNotification(notification);
    }

    private async Task DispatchServerRequestAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        if (!CodexAgentMapper.TryGetThreadId(request, out var threadId) || threadId is null)
        {
            await RejectServerRequestAsync(
                    TryGetRequestId(request),
                    threadId: null,
                    $"Unsupported or unroutable Codex server request '{request.GetType().Name}'.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!_sessions.TryGetValue(threadId, out var session))
        {
            await RejectServerRequestAsync(
                    TryGetRequestId(request),
                    threadId,
                    $"Received Codex server request '{request.GetType().Name}' for unknown thread '{threadId}'.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        LogServerRequestReceived(request, threadId);
        await session.HandleServerRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchUnknownServerRequestAsync(
        CodexUnknownServerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var threadId = CodexAgentMapper.TryGetThreadId(request, out var extractedThreadId)
            ? extractedThreadId
            : null;
        LogWarn(
            $"Unsupported Codex server request method={request.Method} requestId={FormatRequestId(request.RequestId)} threadId={threadId ?? "<none>"}");
        await RejectServerRequestAsync(
                request.RequestId,
                threadId,
                $"Unsupported Codex server request method '{request.Method}'.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RejectServerRequestAsync(
        RequestId requestId,
        string? threadId,
        string message,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(threadId) && _sessions.TryGetValue(threadId, out var session))
        {
            session.Publish(new AgentErrorEvent(AgentBackendIds.Codex, threadId, now, message));
        }
        else
        {
            foreach (var activeSession in _sessions.Values)
            {
                activeSession.Publish(new AgentErrorEvent(AgentBackendIds.Codex, activeSession.SessionId, now, message));
            }
        }

        LogWarn($"Rejecting Codex server request requestId={FormatRequestId(requestId)} threadId={threadId ?? "<none>"} reason={message}");

        await Client.RespondToRequestErrorAsync(
                requestId,
                code: -32601,
                message,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static RequestId TryGetRequestId(ServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request switch
        {
            ServerRequest.ItemCommandExecutionRequestApprovalRequest value => value.Id,
            ServerRequest.ItemFileChangeRequestApprovalRequest value => value.Id,
            ServerRequest.ItemToolRequestUserInputRequest value => value.Id,
            ServerRequest.McpServerElicitationRequestRequest value => value.Id,
            ServerRequest.ItemPermissionsRequestApprovalRequest value => value.Id,
            ServerRequest.ItemToolCallRequest value => value.Id,
            ServerRequest.AccountChatgptAuthTokensRefreshRequest value => value.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, "Unsupported server request type.")
        };
    }

    private void HandleServerRequestResolved(ServerRequestResolvedNotification resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var key = CreatePendingServerRequestKey(resolved.ThreadId, resolved.RequestId);
        if (_pendingServerRequests.TryRemove(key, out var info))
        {
            var resolvedAt = DateTimeOffset.UtcNow;
            var responseLatency = info.ResponseSentAt is { } responseSentAt
                ? (long)(resolvedAt - responseSentAt).TotalMilliseconds
                : -1L;
            var totalLatency = (long)(resolvedAt - info.ReceivedAt).TotalMilliseconds;
            LogDebug(
                $"Codex server request resolved method={info.Method} requestId={info.RequestId} threadId={info.ThreadId} summary={info.ResponseSummary ?? "<none>"} totalElapsedMs={totalLatency} responseToResolvedMs={(responseLatency >= 0 ? responseLatency.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<unknown>")}");
            return;
        }

        LogWarn(
            $"Codex server request resolved without pending request requestId={FormatRequestId(resolved.RequestId)} threadId={resolved.ThreadId}");
    }

    private static PendingServerRequestInfo CreatePendingServerRequestInfo(ServerRequest request, string threadId)
    {
        var requestId = TryGetRequestId(request);
        return new PendingServerRequestInfo(
            key: CreatePendingServerRequestKey(threadId, requestId),
            method: GetRequestMethod(request),
            requestId: FormatRequestId(requestId),
            threadId: threadId,
            turnId: TryGetTurnId(request),
            itemId: TryGetItemId(request),
            callId: TryGetCallId(request),
            tool: TryGetTool(request),
            receivedAt: DateTimeOffset.UtcNow);
    }

    private static string GetPendingServerRequestKey(ServerRequest request)
    {
        var requestId = TryGetRequestId(request);
        if (!CodexAgentMapper.TryGetThreadId(request, out var threadId) || string.IsNullOrWhiteSpace(threadId))
        {
            return CreatePendingServerRequestKey("<unknown>", requestId);
        }

        return CreatePendingServerRequestKey(threadId, requestId);
    }

    private static string CreatePendingServerRequestKey(string threadId, RequestId requestId)
        => $"{threadId}:{FormatRequestId(requestId)}";

    private static string GetRequestMethod(ServerRequest request)
        => request switch
        {
            ServerRequest.ItemCommandExecutionRequestApprovalRequest => "item/commandExecution/requestApproval",
            ServerRequest.ItemFileChangeRequestApprovalRequest => "item/fileChange/requestApproval",
            ServerRequest.ItemToolRequestUserInputRequest => "item/tool/requestUserInput",
            ServerRequest.McpServerElicitationRequestRequest => "mcpServer/elicitation/request",
            ServerRequest.ItemPermissionsRequestApprovalRequest => "item/permissions/requestApproval",
            ServerRequest.ItemToolCallRequest => "item/tool/call",
            ServerRequest.AccountChatgptAuthTokensRefreshRequest => "account/chatgptAuthTokens/refresh",
            _ => request.GetType().Name
        };

    private static string? TryGetTurnId(ServerRequest request)
        => request switch
        {
            ServerRequest.ItemCommandExecutionRequestApprovalRequest value => value.Params.TurnId,
            ServerRequest.ItemFileChangeRequestApprovalRequest value => value.Params.TurnId,
            ServerRequest.ItemToolRequestUserInputRequest value => value.Params.TurnId,
            ServerRequest.McpServerElicitationRequestRequest value => value.Params switch
            {
                McpServerElicitationRequestParams.Form form => form.TurnId,
                McpServerElicitationRequestParams.Url url => url.TurnId,
                _ => null
            },
            ServerRequest.ItemPermissionsRequestApprovalRequest value => value.Params.TurnId,
            ServerRequest.ItemToolCallRequest value => value.Params.TurnId,
            _ => null
        };

    private static string? TryGetItemId(ServerRequest request)
        => request switch
        {
            ServerRequest.ItemCommandExecutionRequestApprovalRequest value => value.Params.ItemId,
            ServerRequest.ItemFileChangeRequestApprovalRequest value => value.Params.ItemId,
            ServerRequest.ItemToolRequestUserInputRequest value => value.Params.ItemId,
            ServerRequest.ItemPermissionsRequestApprovalRequest value => value.Params.ItemId,
            _ => null
        };

    private static string? TryGetCallId(ServerRequest request)
        => request switch
        {
            ServerRequest.ItemToolCallRequest value => value.Params.CallId,
            _ => null
        };

    private static string? TryGetTool(ServerRequest request)
        => request switch
        {
            ServerRequest.ItemToolCallRequest value => value.Params.Tool,
            _ => null
        };

    private static string FormatRequestId(RequestId requestId)
        => requestId switch
        {
            RequestId.StringValue value => value.Value,
            RequestId.IntegerValue value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => requestId.GetType().Name
        };

    private static void LogDebug(string message)
    {
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.Debug(message);
        }
    }

    private static void LogWarn(string message)
    {
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Warn))
        {
            Logger.Warn(message);
        }
    }

    private async Task StopCoreAsync()
    {
        _pumpCancellationTokenSource?.Cancel();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var session in _sessions.Values.ToArray())
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _pumpTask = null;
        _pumpCancellationTokenSource?.Dispose();
        _pumpCancellationTokenSource = null;
    }

    private sealed class PendingServerRequestInfo(
        string key,
        string method,
        string requestId,
        string threadId,
        string? turnId,
        string? itemId,
        string? callId,
        string? tool,
        DateTimeOffset receivedAt)
    {
        public string Key { get; } = key;

        public string Method { get; } = method;

        public string RequestId { get; } = requestId;

        public string ThreadId { get; } = threadId;

        public string? TurnId { get; } = turnId;

        public string? ItemId { get; } = itemId;

        public string? CallId { get; } = callId;

        public string? Tool { get; } = tool;

        public DateTimeOffset ReceivedAt { get; } = receivedAt;

        public DateTimeOffset? ResponseStartedAt { get; set; }

        public DateTimeOffset? ResponseSentAt { get; set; }

        public string? ResponseSummary { get; set; }
    }
}
