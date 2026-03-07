using System.Collections.Concurrent;
using CodeAlta.CodexSdk;

namespace CodeAlta.Agent.Codex;

/// <summary>
/// Codex app-server implementation of <see cref="IAgentBackend"/>.
/// </summary>
public sealed class CodexAgentBackend : ICodexAgentBackend
{
    private readonly ConcurrentDictionary<string, CodexAgentSession> _sessions = new(StringComparer.Ordinal);
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
                        Cwd = filter?.Cwd
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
        var parameters = CodexAgentMapper.ToThreadStartParams(options, _options.ApprovalPolicy);
        var response = await client.ThreadStartAsync(parameters, cancellationToken).ConfigureAwait(false);
        return RegisterSession(
            response.Thread.Id,
            options.WorkingDirectory,
            options.Model,
            options.ReasoningEffort,
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
        var parameters = CodexAgentMapper.ToThreadResumeParams(sessionId, options, _options.ApprovalPolicy);
        var response = await client.ThreadResumeAsync(parameters, cancellationToken).ConfigureAwait(false);
        return RegisterSession(
            response.Thread.Id,
            options.WorkingDirectory,
            options.Model,
            options.ReasoningEffort,
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
                tuple.PermissionHandler,
                tuple.UserInputHandler),
            (
                Backend: this,
                WorkingDirectory: workingDirectory,
                Model: model,
                ReasoningEffort: reasoningEffort,
                PermissionHandler: permissionHandler,
                UserInputHandler: userInputHandler));

        session.UpdateSessionOptions(workingDirectory, model, reasoningEffort, permissionHandler, userInputHandler);
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
        if (!CodexAgentMapper.TryGetThreadId(notification, out var threadId) || threadId is null)
            return;

        if (!_sessions.TryGetValue(threadId, out var session))
            return;

        session.HandleNotification(notification);
    }

    private async Task DispatchServerRequestAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        if (!CodexAgentMapper.TryGetThreadId(request, out var threadId) || threadId is null)
            return;

        if (!_sessions.TryGetValue(threadId, out var session))
            return;

        await session.HandleServerRequestAsync(request, cancellationToken).ConfigureAwait(false);
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
}
