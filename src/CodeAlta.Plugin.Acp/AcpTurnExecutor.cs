using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Plugin.Acp;

/// <summary>
/// Executes the turns of CodeAlta sessions through an ACP agent: one agent process and session per CodeAlta session.
/// </summary>
internal sealed class AcpTurnExecutor :
    IModelProviderTurnExecutor,
    IAgentProviderToolHost,
    IAgentProviderCompaction,
    IAgentProviderSessionCleanup,
    IAgentProviderPermissionMode,
    IAgentProviderInstructionsChannel,
    IAsyncDisposable
{
    private readonly AcpRuntimeOptions _options;
    private readonly ConcurrentDictionary<string, AcpAgentSession> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public AcpTurnExecutor(AcpRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteTurnAsync(request, onUpdate, static (_, _) => ValueTask.CompletedTask, cancellationToken);

    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);
        ArgumentNullException.ThrowIfNull(onSessionUpdate);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // The agent keeps and compacts the context of a session: a request to summarize a conversation for a local
        // compaction is not a turn of that session.
        if (request.RunId.Value.StartsWith("compaction-summary:", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"{_options.AgentName} compacts its own context: CodeAlta does not summarize its sessions.");
        }

        return GetSession(request.SessionId).ExecuteTurnAsync(request, onUpdate, onSessionUpdate, cancellationToken);
    }

    public void AttachRun(AgentProviderRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GetSession(context.SessionId).AttachRun(context);
    }

    public AgentToolDefinition? ResolveTool(string sessionId, AgentMessagePart.ToolCall toolCall, AgentToolDefinition? registered)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return _sessions.TryGetValue(sessionId, out var session) ? session.ResolveTool(toolCall, registered) : null;
    }

    public Task? WhenToolStarts(string sessionId, AgentMessagePart.ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return _sessions.TryGetValue(sessionId, out var session) ? session.WhenToolStarts(toolCall) : null;
    }

    public Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetSession(request.SessionId).CompactAsync(request, cancellationToken);
    }

    public Task<bool> SetRunningPermissionModeAsync(string sessionId, string? permissionMode, CancellationToken cancellationToken)
    {
        // CodeAlta answers the permission requests of the agent: the mode applies to the next one, at once.
        GetSession(sessionId).SetPermissionMode(permissionMode);
        return Task.FromResult(true);
    }

    public string? GetInstructionsChannel(string sessionId) => GetSession(sessionId).InstructionsChannel;

    public async ValueTask DisposeProviderSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var sessionId in _sessions.Keys)
        {
            await DisposeProviderSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    private AcpAgentSession GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _sessions.GetOrAdd(sessionId, static (id, options) => new AcpAgentSession(id, options), _options);
    }
}
