using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>
/// Executes the turns of CodeAlta sessions through the Codex app-server: one app-server process and one Codex thread
/// per CodeAlta session.
/// </summary>
internal sealed class CodexCliTurnExecutor :
    IModelProviderTurnExecutor,
    IAgentProviderToolHost,
    IAgentProviderKeptConversation,
    IAgentProviderSessionCleanup,
    IAgentProviderPermissionMode,
    IAgentProviderInstructionsChannel,
    IAsyncDisposable
{
    private readonly CodexCliRuntimeOptions _options;
    private readonly ConcurrentDictionary<string, CodexCliSession> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public CodexCliTurnExecutor(CodexCliRuntimeOptions options)
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

        // Codex keeps and compacts the context of a thread: a request to summarize a conversation for a local
        // compaction is not a turn of that session.
        if (request.RunId.Value.StartsWith("compaction-summary:", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"{_options.ProviderName} compacts its own context: CodeAlta does not summarize its sessions.");
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
        // CodeAlta answers the approval requests of Codex: the mode applies to the next one at once, and to the policy
        // of the next turn.
        GetSession(sessionId).SetPermissionMode(permissionMode);
        return Task.FromResult(true);
    }

    public string? GetInstructionsChannel(string sessionId) => CodexCliSession.InstructionsChannel;

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

    private CodexCliSession GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _sessions.GetOrAdd(sessionId, static (id, options) => new CodexCliSession(id, options), _options);
    }
}
