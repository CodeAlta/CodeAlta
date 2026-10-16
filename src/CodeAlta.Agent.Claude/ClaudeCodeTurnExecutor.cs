using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Executes the turns of CodeAlta sessions through the Claude Code CLI: one CLI conversation per session.
/// </summary>
internal sealed class ClaudeCodeTurnExecutor :
    IModelProviderTurnExecutor,
    IAgentProviderToolHost,
    IAgentProviderCompaction,
    IAgentProviderSessionCleanup,
    IAgentProviderInitiatedTurns,
    IAgentProviderBackgroundTasks,
    IAgentProviderRemoteControl,
    IAgentProviderPermissionMode,
    IAgentProviderPromptSuggestions,
    IAgentProviderExternalTurns,
    IAgentProviderExternalConversations,
    IAgentProviderSharedConversation,
    IAgentProviderStateUpdates,
    IAsyncDisposable
{
    private readonly ClaudeCodeModelProviderRuntimeOptions _options;
    private readonly ConcurrentDictionary<string, ClaudeCodeSession> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public ClaudeCodeTurnExecutor(ClaudeCodeModelProviderRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var probe = options.ProcessProbe ?? ClaudeCodeProcessProbe.Instance;
        OwnProcesses = options.OwnProcesses;
        // The folder of the provider's account, where its CLI keeps the registry and the transcripts.
        var configRoot = options.ResolveConfigRoot();
        LiveSessions = new ClaudeCodeLiveSessions(configRoot, OwnProcesses, probe, TimeProvider.System);
        ExternalConversations = new ClaudeCodeExternalConversations(configRoot, LiveSessions);
    }

    /// <summary>Gets the CLI processes the sessions of the executors of these options run, this one's included.</summary>
    internal ClaudeCodeOwnProcesses OwnProcesses { get; }

    /// <summary>Gets the reader of the registry of live sessions of the CLI, which tells whether another process holds a conversation.</summary>
    internal ClaudeCodeLiveSessions LiveSessions { get; }

    /// <summary>Gets the reader of the conversations started in VS Code or a terminal, which a project can import.</summary>
    internal ClaudeCodeExternalConversations ExternalConversations { get; }

    /// <summary>Gets where the reads of the transcripts of the sessions of this executor stopped.</summary>
    internal ClaudeCodeTranscriptPositions TranscriptPositions { get; } = new();

    /// <inheritdoc />
    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteTurnAsync(request, onUpdate, static (_, _) => ValueTask.CompletedTask, cancellationToken);

    /// <inheritdoc />
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

        // The CLI keeps and compacts the context of a session: a request to summarize a conversation for a local
        // compaction is not a turn of that session.
        if (request.RunId.Value.StartsWith("compaction-summary:", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Claude Code compacts its own context: CodeAlta does not summarize a Claude Code session.");
        }

        return GetSession(request.SessionId).ExecuteTurnAsync(request, onUpdate, onSessionUpdate, cancellationToken);
    }

    /// <inheritdoc />
    public void AttachRun(AgentProviderRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GetSession(context.SessionId).AttachRun(context);
    }

    /// <inheritdoc />
    public AgentToolDefinition? ResolveTool(string sessionId, AgentMessagePart.ToolCall toolCall, AgentToolDefinition? registered)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return _sessions.TryGetValue(sessionId, out var session) ? session.ResolveTool(toolCall, registered) : null;
    }

    /// <inheritdoc />
    public Task? WhenToolStarts(string sessionId, AgentMessagePart.ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return _sessions.TryGetValue(sessionId, out var session) ? session.WhenToolStarts(toolCall) : null;
    }

    /// <inheritdoc />
    public Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetSession(request.SessionId).CompactAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public IDisposable OnProviderTurn(string sessionId, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return GetSession(sessionId).OnOwnTurn(handler);
    }

    /// <inheritdoc />
    public string? GetPendingProviderTurn(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.PendingOwnTurn : null;

    /// <inheritdoc />
    public IDisposable OnRemoteControlChanged(string sessionId, Action<AgentRemoteControl> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return GetSession(sessionId).OnRemoteControlChanged(handler);
    }

    /// <inheritdoc />
    public AgentRemoteControl GetRemoteControl(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.RemoteControl : AgentRemoteControl.Off;

    /// <inheritdoc />
    public Task<AgentRemoteControl> SetRemoteControlAsync(AgentTurnRequest request, bool enabled, string? name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetSession(request.SessionId).SetRemoteControlAsync(request, enabled, name, cancellationToken);
    }

    /// <inheritdoc />
    public IDisposable OnPromptSuggestion(string sessionId, Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return GetSession(sessionId).OnPromptSuggestion(handler);
    }

    /// <inheritdoc />
    public IDisposable OnBackgroundTasksChanged(string sessionId, Action<IReadOnlyList<AgentBackgroundTask>, IReadOnlyList<AgentBackgroundTaskEnd>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return GetSession(sessionId).OnBackgroundTasksChanged(handler);
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentBackgroundTask> GetBackgroundTasks(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.BackgroundTasks : [];

    /// <inheritdoc />
    public Task<bool> StopBackgroundTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken)
        => _sessions.TryGetValue(sessionId, out var session) ? session.StopBackgroundTaskAsync(taskId, cancellationToken) : Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> SetRunningPermissionModeAsync(string sessionId, string? permissionMode, CancellationToken cancellationToken)
        => _sessions.TryGetValue(sessionId, out var session) ? session.SetRunningPermissionModeAsync(permissionMode, cancellationToken) : Task.FromResult(false);

    /// <summary>
    /// Reads the turns VS Code or the CLI in a terminal added to the transcript of the conversation of a session since
    /// it recorded it. Nothing is started; the transcript is only read.
    /// </summary>
    /// <inheritdoc />
    /// <remarks>
    /// While another live process holds the conversation and is not idle (VS Code in the middle of a turn, or waiting
    /// for a permission), nothing is read: <see cref="AgentExternalTurnsStatus.Held"/>. A registry of live sessions that
    /// cannot be read does not stop the read, as it does not stop a turn.
    /// </remarks>
    public Task<AgentExternalTurns> ReadExternalTurnsAsync(AgentSessionState state, string workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var root = _options.ResolveConfigRoot();
        // A transcript can be tens of megabytes: it is read on the pool, not on the thread of the caller.
        return Task.Run(() =>
        {
            var persisted = ClaudeCodeProviderState.Read(state);
            if (persisted is { Leaf: not null } &&
                LiveSessions.FindHolder(persisted.SessionId) is { Status: ClaudeCodeSessionHoldStatus.Held } hold &&
                !string.Equals(hold.Holder?.Status, "idle", StringComparison.Ordinal))
            {
                return AgentExternalTurns.Held;
            }

            return ClaudeCodeExternalTurns.Read(root, persisted, workingDirectory, TranscriptPositions, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Lists the conversations of Claude Code started in VS Code or by the CLI in a terminal in a folder, which VS Code
    /// lists too, and which no session of CodeAlta continues.
    /// </summary>
    /// <inheritdoc />
    public Task<AgentExternalConversationList> ListExternalConversationsAsync(string workingDirectory, Func<string, bool> isKnown, int offset, int count,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(isKnown);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        // Many transcripts may be looked at: on the pool, not on the thread of the caller.
        return Task.Run(() => ExternalConversations.List(workingDirectory, isKnown, offset, count, cancellationToken), cancellationToken);
    }

    /// <summary>Reads a whole conversation of Claude Code started in VS Code or a terminal, to import it.</summary>
    /// <inheritdoc />
    /// <remarks>A conversation another process holds is read all the same: importing it only reads it.</remarks>
    public Task<AgentExternalConversationImport?> ReadExternalConversationAsync(string workingDirectory, string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return Task.Run(() => ExternalConversations.Read(workingDirectory, conversationId, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Reads a conversation of Claude Code started in VS Code or a terminal in a folder to show it, as it would be
    /// imported, for a transcript of at most <paramref name="maxBytes"/>; the last reads are kept while their transcript
    /// does not change.
    /// </summary>
    /// <inheritdoc />
    public Task<AgentExternalConversationPreview> PreviewExternalConversationAsync(string workingDirectory, string conversationId, long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return Task.Run(() => ExternalConversations.Preview(workingDirectory, conversationId, maxBytes, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Describes a conversation of Claude Code started in VS Code or a terminal in a folder, which VS Code lists too: the
    /// command and the VS Code address that open it, and the live Claude process that holds it now. The processes
    /// CodeAlta runs are not holders.
    /// </summary>
    /// <inheritdoc />
    public Task<AgentSharedConversation?> DescribeExternalConversationAsync(string workingDirectory, string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return Task.Run(() => ExternalConversations.Describe(workingDirectory, conversationId), cancellationToken);
    }

    /// <summary>
    /// Describes the conversation of the CLI a session continues: the command and the VS Code address that open it, and
    /// the other live Claude process that holds it now, if any. The processes CodeAlta runs are not holders.
    /// </summary>
    /// <inheritdoc />
    /// <remarks>
    /// A session whose next turn forks or rewinds its conversation, or whose recorded identifier is not one the CLI
    /// gives, has none to open. A registry of live sessions that cannot be read names no holder.
    /// </remarks>
    public Task<AgentSharedConversation?> DescribeSharedConversationAsync(AgentSessionState state, string workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (state.ProviderBranch is not null ||
            ClaudeCodeProviderState.Read(state) is not { } persisted ||
            !ClaudeCodeConversationLinks.IsConversationId(persisted.SessionId))
        {
            return Task.FromResult<AgentSharedConversation?>(null);
        }

        // The registry is a folder of small files: read on the pool, not on the thread of the caller.
        return Task.Run<AgentSharedConversation?>(() =>
        {
            var hold = LiveSessions.FindHolder(persisted.SessionId);
            var holder = hold is { Status: ClaudeCodeSessionHoldStatus.Held, Holder: { } held } ? ClaudeCodeSession.ToAgentHolder(held) : null;
            return new AgentSharedConversation(
                persisted.SessionId,
                ClaudeCodeConversationLinks.ResumeCommand(persisted.SessionId),
                workingDirectory,
                ClaudeCodeConversationLinks.VsCodeUri(persisted.SessionId),
                holder);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public JsonElement? TakeProviderState(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.TakeUnrecordedState() : null;

    /// <inheritdoc />
    public async ValueTask DisposeProviderSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
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

    private ClaudeCodeSession GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _sessions.GetOrAdd(sessionId, static (id, executor) => new ClaudeCodeSession(id, executor._options, executor.OwnProcesses, executor.LiveSessions, executor.TranscriptPositions), this);
    }
}
