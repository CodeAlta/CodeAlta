using System.Collections.Concurrent;
using GitHub.Copilot.SDK;

namespace CodeNoesis.Agent.Copilot;

/// <summary>
/// Copilot implementation of <see cref="IAgentBackend"/>.
/// </summary>
public sealed class CopilotAgentBackend : ICopilotAgentBackend
{
    private readonly ConcurrentDictionary<string, CopilotAgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly CopilotAgentBackendOptions _options;
    private CopilotClient? _client;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CopilotAgentBackend"/> class.
    /// </summary>
    /// <param name="options">Backend options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public CopilotAgentBackend(CopilotAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public AgentBackendId BackendId => AgentBackendIds.Copilot;

    /// <inheritdoc />
    public string DisplayName => "Copilot";

    /// <inheritdoc />
    public CopilotClient Client => _client ?? throw new InvalidOperationException("The Copilot backend is not started.");

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
                return;

            _client = new CopilotClient(_options.ClientOptions.Clone());
            await _client.StartAsync(cancellationToken).ConfigureAwait(false);
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
            if (_client is null)
                return;

            foreach (var session in _sessions.Values.ToArray())
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _sessions.Clear();
            await _client.StopAsync().ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
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
        var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.Select(CopilotAgentMapper.ToAgentModelInfo).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var copilotFilter = filter is null
            ? null
            : new SessionListFilter
            {
                Cwd = filter.Cwd,
                GitRoot = filter.GitRoot,
                Repository = filter.Repository,
                Branch = filter.Branch
            };

        var sessions = await client.ListSessionsAsync(copilotFilter, cancellationToken).ConfigureAwait(false);
        return sessions.Select(CopilotAgentMapper.ToAgentSessionMetadata).ToArray();
    }

    /// <inheritdoc />
    public async Task<IAgentSession> CreateSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var config = CopilotAgentMapper.ToSessionConfig(options);
        var session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        return await RegisterSessionAsync(session).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IAgentSession> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(options);

        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var config = CopilotAgentMapper.ToResumeSessionConfig(options);
        var session = await client.ResumeSessionAsync(sessionId, config, cancellationToken).ConfigureAwait(false);
        return await RegisterSessionAsync(session).ConfigureAwait(false);
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

    internal void RemoveSession(string sessionId, CopilotAgentSession session)
    {
        if (_sessions.TryGetValue(sessionId, out var existing) &&
            ReferenceEquals(existing, session))
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private async Task<CopilotAgentSession> RegisterSessionAsync(CopilotSession session)
    {
        var wrappedSession = new CopilotAgentSession(this, session);
        if (_sessions.TryAdd(session.SessionId, wrappedSession))
            return wrappedSession;

        await wrappedSession.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException(
            $"A Copilot session '{session.SessionId}' is already active. Dispose it before creating or resuming it again.");
    }

    private async Task<CopilotClient> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return Client;
    }
}
