using System.Text.Json;

namespace CodeAlta.CodexSdk;

/// <summary>
/// Represents a server-initiated notification from the codex app-server.
/// Use pattern matching on the derived types to handle specific events.
/// </summary>
/// <remarks>
/// Server notifications are emitted without an <c>id</c> field and do not expect a response.
/// They arrive on the stdio stream interleaved with responses to client requests.
/// </remarks>
public abstract record CodexNotification
{
    private CodexNotification() { }

    // ── Thread lifecycle ──────────────────────────────────────────

    /// <summary>Emitted when a thread is created via <c>thread/start</c> or <c>thread/fork</c>.</summary>
    public sealed record ThreadStarted(ThreadStartedNotification Data) : CodexNotification;

    /// <summary>Emitted when a thread is archived.</summary>
    public sealed record ThreadArchived(ThreadArchivedNotification Data) : CodexNotification;

    /// <summary>Emitted when a thread is unarchived.</summary>
    public sealed record ThreadUnarchived(ThreadUnarchivedNotification Data) : CodexNotification;

    /// <summary>Emitted when a thread's name is updated.</summary>
    public sealed record ThreadNameUpdated(ThreadNameUpdatedNotification Data) : CodexNotification;

    /// <summary>Emitted when a loaded thread's status changes (e.g., loaded -&gt; notLoaded).</summary>
    public sealed record ThreadStatusChanged(ThreadStatusChangedNotification Data) : CodexNotification;

    /// <summary>Emitted when a thread is unloaded/closed by the server.</summary>
    public sealed record ThreadClosed(ThreadClosedNotification Data) : CodexNotification;

    // Thread realtime (experimental)

    /// <summary>Emitted when thread realtime starts for a thread.</summary>
    public sealed record ThreadRealtimeStarted(ThreadRealtimeStartedNotification Data) : CodexNotification;

    /// <summary>Emitted when a raw realtime item is added (non-audio).</summary>
    public sealed record ThreadRealtimeItemAdded(ThreadRealtimeItemAddedNotification Data) : CodexNotification;

    /// <summary>Emitted when an output audio delta is streamed for a realtime session.</summary>
    public sealed record ThreadRealtimeOutputAudioDelta(ThreadRealtimeOutputAudioDeltaNotification Data) : CodexNotification;

    /// <summary>Emitted when thread realtime encounters an error.</summary>
    public sealed record ThreadRealtimeError(ThreadRealtimeErrorNotification Data) : CodexNotification;

    /// <summary>Emitted when the realtime transport closes.</summary>
    public sealed record ThreadRealtimeClosed(ThreadRealtimeClosedNotification Data) : CodexNotification;

    // ── Turn lifecycle ────────────────────────────────────────────

    /// <summary>Emitted when a turn begins on a thread.</summary>
    public sealed record TurnStarted(TurnStartedNotification Data) : CodexNotification;

    /// <summary>Emitted when a turn finishes (completed, interrupted, or failed).</summary>
    public sealed record TurnCompleted(TurnCompletedNotification Data) : CodexNotification;

    /// <summary>Emitted when the turn-level unified diff is updated.</summary>
    public sealed record TurnDiffUpdated(TurnDiffUpdatedNotification Data) : CodexNotification;

    /// <summary>Emitted when the agent plan is updated.</summary>
    public sealed record TurnPlanUpdated(TurnPlanUpdatedNotification Data) : CodexNotification;

    // ── Item lifecycle ────────────────────────────────────────────

    /// <summary>Emitted when a new item begins within a turn.</summary>
    public sealed record ItemStarted(ItemStartedNotification Data) : CodexNotification;

    /// <summary>Emitted when an item finishes within a turn.</summary>
    public sealed record ItemCompleted(ItemCompletedNotification Data) : CodexNotification;

    /// <summary>Emitted when a raw response item completes.</summary>
    public sealed record RawResponseItemCompleted(RawResponseItemCompletedNotification Data) : CodexNotification;

    // ── Agent message streaming ───────────────────────────────────

    /// <summary>Streamed delta of agent message text.</summary>
    public sealed record AgentMessageDelta(AgentMessageDeltaNotification Data) : CodexNotification;

    // ── Plan streaming ────────────────────────────────────────────

    /// <summary>Streamed delta of plan text (experimental).</summary>
    public sealed record PlanDelta(PlanDeltaNotification Data) : CodexNotification;

    // ── Command execution streaming ───────────────────────────────

    /// <summary>Streamed stdout/stderr delta from a running command.</summary>
    public sealed record CommandExecutionOutputDelta(CommandExecutionOutputDeltaNotification Data) : CodexNotification;

    /// <summary>Terminal interaction notification for a running command.</summary>
    public sealed record CommandExecutionTerminalInteraction(TerminalInteractionNotification Data) : CodexNotification;

    // ── File change streaming ─────────────────────────────────────

    /// <summary>Streamed delta of file change tool output.</summary>
    public sealed record FileChangeOutputDelta(FileChangeOutputDeltaNotification Data) : CodexNotification;

    // ── MCP tool call streaming ───────────────────────────────────

    /// <summary>Progress notification for an MCP tool call.</summary>
    public sealed record McpToolCallProgress(McpToolCallProgressNotification Data) : CodexNotification;

    // ── Reasoning streaming ───────────────────────────────────────

    /// <summary>Streamed reasoning summary text delta.</summary>
    public sealed record ReasoningSummaryTextDelta(ReasoningSummaryTextDeltaNotification Data) : CodexNotification;

    /// <summary>Marks a new reasoning summary section boundary.</summary>
    public sealed record ReasoningSummaryPartAdded(ReasoningSummaryPartAddedNotification Data) : CodexNotification;

    /// <summary>Streamed raw reasoning text delta.</summary>
    public sealed record ReasoningTextDelta(ReasoningTextDeltaNotification Data) : CodexNotification;

    // ── Token usage ───────────────────────────────────────────────

    /// <summary>Emitted when token usage is updated for a thread.</summary>
    public sealed record ThreadTokenUsageUpdated(ThreadTokenUsageUpdatedNotification Data) : CodexNotification;

    // ── Context compaction ────────────────────────────────────────

    /// <summary>Emitted when context compaction occurs on a thread (deprecated — use ContextCompaction item type).</summary>
    public sealed record ThreadCompacted(ContextCompactedNotification Data) : CodexNotification;

    // ── Account ───────────────────────────────────────────────────

    /// <summary>Emitted when the authentication state changes.</summary>
    public sealed record AccountUpdated(AccountUpdatedNotification Data) : CodexNotification;

    /// <summary>Emitted when a login attempt completes.</summary>
    public sealed record AccountLoginCompleted(AccountLoginCompletedNotification Data) : CodexNotification;

    /// <summary>Emitted when rate limits are updated.</summary>
    public sealed record AccountRateLimitsUpdated(AccountRateLimitsUpdatedNotification Data) : CodexNotification;

    // ── App list ──────────────────────────────────────────────────

    /// <summary>Emitted when the app list changes (experimental).</summary>
    public sealed record AppListUpdated(AppListUpdatedNotification Data) : CodexNotification;

    // ── Error ─────────────────────────────────────────────────────

    /// <summary>Emitted when the server encounters an error during a turn.</summary>
    public sealed record Error(ErrorNotification Data) : CodexNotification;

    // ── MCP OAuth ─────────────────────────────────────────────────

    /// <summary>Emitted when an MCP server OAuth login flow completes.</summary>
    public sealed record McpServerOauthLoginCompleted(McpServerOauthLoginCompletedNotification Data) : CodexNotification;

    // Fuzzy file search (experimental)

    /// <summary>Emitted when the fuzzy file search session returns updated matches.</summary>
    public sealed record FuzzyFileSearchSessionUpdated(FuzzyFileSearchSessionUpdatedNotification Data) : CodexNotification;

    /// <summary>Emitted when the fuzzy file search session completes indexing/matching for the query.</summary>
    public sealed record FuzzyFileSearchSessionCompleted(FuzzyFileSearchSessionCompletedNotification Data) : CodexNotification;

    // ── Config ────────────────────────────────────────────────────

    /// <summary>Emitted when a config warning is detected.</summary>
    public sealed record ConfigWarning(ConfigWarningNotification Data) : CodexNotification;

    /// <summary>Emitted when a deprecation notice is sent.</summary>
    public sealed record DeprecationNotice(DeprecationNoticeNotification Data) : CodexNotification;

    // ── Model reroute ─────────────────────────────────────────────

    /// <summary>Emitted when the backend reroutes a request to a different model.</summary>
    public sealed record ModelRerouted(ModelReroutedNotification Data) : CodexNotification;

    // ── Windows ───────────────────────────────────────────────────

    /// <summary>Emitted when world-writable directories are detected on Windows.</summary>
    public sealed record WindowsWorldWritableWarning(WindowsWorldWritableWarningNotification Data) : CodexNotification;

    /// <summary>Emitted when Windows sandbox setup completes.</summary>
    public sealed record WindowsSandboxSetupCompleted(WindowsSandboxSetupCompletedNotification Data) : CodexNotification;

    // Server request lifecycle

    /// <summary>Emitted when a pending server request has been resolved or cleared.</summary>
    public sealed record ServerRequestResolved(ServerRequestResolvedNotification Data) : CodexNotification;

    // ── Catch-all ─────────────────────────────────────────────────

    /// <summary>Represents a notification with a method not explicitly handled by <see cref="CodexNotification"/>.</summary>
    public sealed record Unknown(string Method, JsonElement Params) : CodexNotification;
}

/// <summary>
/// Represents a server-initiated request that requires a client response (e.g., approval requests).
/// </summary>
public abstract record CodexServerRequest
{
    private CodexServerRequest() { }

    /// <summary>
    /// Gets the JSON-RPC request id that the client must use when responding.
    /// </summary>
    public abstract long RequestId { get; }

    /// <summary>Server requests approval for a command execution.</summary>
    public sealed record CommandExecutionApproval(
        long RequestId,
        CommandExecutionRequestApprovalParams Data) : CodexServerRequest
    {
        /// <inheritdoc />
        public override long RequestId { get; } = RequestId;
    }

    /// <summary>Server requests approval for a file change.</summary>
    public sealed record FileChangeApproval(
        long RequestId,
        FileChangeRequestApprovalParams Data) : CodexServerRequest
    {
        /// <inheritdoc />
        public override long RequestId { get; } = RequestId;
    }

    /// <summary>Server requests user input for a tool call (experimental).</summary>
    public sealed record ToolRequestUserInput(
        long RequestId,
        ToolRequestUserInputParams Data) : CodexServerRequest
    {
        /// <inheritdoc />
        public override long RequestId { get; } = RequestId;
    }

    /// <summary>Server requests execution of a dynamic tool call.</summary>
    public sealed record ToolCall(
        long RequestId,
        DynamicToolCallParams Data) : CodexServerRequest
    {
        /// <inheritdoc />
        public override long RequestId { get; } = RequestId;
    }

    /// <summary>Server requests ChatGPT auth tokens refresh.</summary>
    public sealed record ChatgptAuthTokensRefresh(
        long RequestId,
        ChatgptAuthTokensRefreshParams Data) : CodexServerRequest
    {
        /// <inheritdoc />
        public override long RequestId { get; } = RequestId;
    }

    /// <summary>Server-initiated request not explicitly handled.</summary>
    public sealed record UnknownRequest(
        long RequestId,
        string Method,
        JsonElement Params) : CodexServerRequest
    {
        /// <inheritdoc />
        public override long RequestId { get; } = RequestId;
    }
}
