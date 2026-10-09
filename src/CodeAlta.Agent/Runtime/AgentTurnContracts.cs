using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Provides cached or probed model metadata for a configured provider. Turn execution does not require this contract.
/// </summary>
public interface IModelProviderModelCatalog
{
    /// <summary>
    /// Lists models available to the provider implementation.
    /// </summary>
    /// <param name="provider">The configured provider runtime descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The available models.</returns>
    Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        ModelProviderRuntimeDescriptor provider,
        CancellationToken cancellationToken = default);
}

internal interface IAgentProviderSessionCleanup
{
    ValueTask DisposeProviderSessionAsync(string sessionId);
}

/// <summary>
/// The handlers of one run, given to a provider that runs tools itself.
/// </summary>
/// <param name="SessionId">The session of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="OnPermissionRequest">Asks the host whether a tool of the provider may run.</param>
/// <param name="OnUserInputRequest">Asks the user a question of the provider, when the run can.</param>
/// <param name="ProviderInitiated">Whether the run shows a turn the provider started by itself: its message is not sent to the provider.</param>
internal sealed record AgentProviderRunContext(
    string SessionId,
    AgentRunId RunId,
    AgentPermissionRequestHandler OnPermissionRequest,
    AgentUserInputRequestHandler? OnUserInputRequest,
    bool ProviderInitiated = false);

/// <summary>
/// A turn executor whose provider can start a turn by itself while its session has no run (an agent CLI whose
/// background command ended). Such a turn is shown as a run of the session, started for it.
/// </summary>
internal interface IAgentProviderInitiatedTurns
{
    /// <summary>
    /// Registers what is called when the provider started a turn by itself for a session. The handler is called
    /// while the provider is being read: it must not wait.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="handler">What is called for each such turn.</param>
    /// <returns>The registration, which is disposed to end it.</returns>
    IDisposable OnProviderTurn(string sessionId, Action handler);

    /// <summary>
    /// Returns the message of the run that shows a turn the provider started by itself and that no run has read
    /// yet, or <see langword="null" /> when there is none.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    string? GetPendingProviderTurn(string sessionId);
}

/// <summary>
/// A turn executor whose provider goes on working for a session in the background, outside its turns (an agent
/// CLI with a command that still runs).
/// </summary>
internal interface IAgentProviderBackgroundTasks
{
    /// <summary>
    /// Registers what is called when the background tasks of a session change, with the tasks that go on and
    /// those that just ended. The handler is called while the provider is being read: it must not wait.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="handler">What is called for each change.</param>
    /// <returns>The registration, which is disposed to end it.</returns>
    IDisposable OnBackgroundTasksChanged(string sessionId, Action<IReadOnlyList<AgentBackgroundTask>, IReadOnlyList<AgentBackgroundTaskEnd>> handler);

    /// <summary>Returns the background tasks of a session that go on now.</summary>
    /// <param name="sessionId">The session.</param>
    IReadOnlyList<AgentBackgroundTask> GetBackgroundTasks(string sessionId);

    /// <summary>Asks the provider to stop one background task of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="taskId">The identity of the task.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the provider took the request.</returns>
    Task<bool> StopBackgroundTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken);
}

/// <summary>
/// A turn executor whose provider runs tools itself (an agent CLI): the session still shows and records each
/// tool call, but the definition that "runs" it comes from the executor.
/// </summary>
internal interface IAgentProviderToolHost
{
    /// <summary>
    /// Gives the handlers of a run before its first turn.
    /// </summary>
    void AttachRun(AgentProviderRunContext context);

    /// <summary>
    /// Returns the definition that runs a tool call of a response of this executor, or
    /// <see langword="null" /> to run <paramref name="registered" /> as it is.
    /// </summary>
    /// <param name="sessionId">The session of the call.</param>
    /// <param name="toolCall">The tool call the executor returned.</param>
    /// <param name="registered">The tool of the session with that name, when there is one.</param>
    AgentToolDefinition? ResolveTool(string sessionId, AgentMessagePart.ToolCall toolCall, AgentToolDefinition? registered);

    /// <summary>
    /// Returns a task that ends when the provider starts to run a tool call of a response of this executor. The
    /// provider may run several calls of one response at the same time: the session shows each as running from
    /// then on, and at the latest when the calls before it in the response ended. It is asked after
    /// <see cref="ResolveTool"/>, for each call in the order of the response.
    /// </summary>
    /// <param name="sessionId">The session of the call.</param>
    /// <param name="toolCall">The tool call the executor returned.</param>
    /// <returns>The task, or <see langword="null" /> for a call that runs when the calls before it ended.</returns>
    Task? WhenToolStarts(string sessionId, AgentMessagePart.ToolCall toolCall);
}

/// <summary>
/// A turn executor whose provider keeps the context of a session and compacts it itself.
/// </summary>
internal interface IAgentProviderCompaction
{
    /// <summary>
    /// Compacts the context the provider keeps for the session of <paramref name="request" />.
    /// </summary>
    Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken);
}

internal sealed record AgentTurnFailure(
    string Message,
    bool IsContextOverflow);

internal sealed class AgentTurnExecutionException : Exception
{
    public AgentTurnExecutionException(AgentTurnFailure failure, Exception? innerException = null)
        : base(failure?.Message, innerException)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public AgentTurnFailure Failure { get; }
}

/// <summary>
/// Represents a single provider turn request.
/// </summary>
public sealed record AgentTurnRequest
{
    /// <summary>
    /// Gets or initializes the configured provider descriptor.
    /// </summary>
    public required ModelProviderRuntimeDescriptor Provider { get; init; }

    /// <summary>
    /// Gets or initializes the model provider identifier.
    /// </summary>
    public required ModelProviderId ProviderId { get; init; }

    /// <summary>
    /// Gets or initializes the session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets or initializes the active run identifier.
    /// </summary>
    public required AgentRunId RunId { get; init; }

    /// <summary>
    /// Gets or initializes the model identifier.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Gets or initializes the resolved model metadata when available.
    /// </summary>
    public AgentModelInfo? ModelInfo { get; init; }

    /// <summary>
    /// Gets or initializes the working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the effective system message.
    /// </summary>
    public string? SystemMessage { get; init; }

    /// <summary>
    /// Gets or initializes the effective developer instructions.
    /// </summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>
    /// Gets or initializes the requested reasoning effort.
    /// </summary>
    public AgentReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    /// Gets or initializes the requested permission mode, one of the <see cref="AgentProviderProfile.PermissionModes"/>
    /// of the provider. Null leaves the mode the provider is configured with.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Gets or initializes the maximum number of output tokens the provider should generate when supported.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Gets or initializes the replayable conversation.
    /// </summary>
    public required IReadOnlyList<AgentConversationMessage> Conversation { get; init; }

    /// <summary>
    /// Gets or initializes the available tool definitions.
    /// </summary>
    public required IReadOnlyList<AgentToolDefinition> Tools { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether provider-native continuation state from earlier turns in this in-memory session may be reused.
    /// </summary>
    public bool CanUseProviderContinuation { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether the request stands alone: no later request starts with what it
    /// sends, as for the summary of a compaction. A provider that bills what it writes to a prompt cache writes
    /// nothing for such a request.
    /// </summary>
    public bool IsStandalone { get; init; }

    /// <summary>
    /// Gets or initializes the persisted local session state.
    /// </summary>
    public required AgentSessionState State { get; init; }
}

/// <summary>
/// Represents a provider turn response.
/// </summary>
public sealed record AgentTurnResponse
{
    /// <summary>
    /// Gets or initializes the final assistant message.
    /// </summary>
    public required AgentConversationMessage AssistantMessage { get; init; }

    /// <summary>
    /// Gets or initializes optional stable content identifiers aligned with <see cref="AssistantMessage"/> parts.
    /// Entries may be <see langword="null" /> for parts that do not map to timeline content.
    /// </summary>
    public IReadOnlyList<string?>? AssistantPartContentIds { get; init; }

    /// <summary>
    /// Gets or initializes the latest usage snapshot.
    /// </summary>
    public AgentSessionUsage? Usage { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether the provider explicitly requested another inference cycle.
    /// </summary>
    public bool RequiresProviderFollowUp { get; init; }

    /// <summary>
    /// Gets or initializes the provider-native session identifier when available.
    /// </summary>
    public string? ProviderSessionId { get; init; }

    /// <summary>
    /// Gets or initializes provider-specific replay hints or diagnostics.
    /// </summary>
    public JsonElement? ProviderState { get; init; }

    /// <summary>
    /// Gets or initializes an optional title candidate.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets or initializes an optional summary candidate.
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Represents a best-effort streaming turn update.
/// </summary>
public sealed record AgentTurnDelta
{
    /// <summary>
    /// Gets or initializes the streaming content kind.
    /// </summary>
    public required AgentContentKind Kind { get; init; }

    /// <summary>
    /// Gets or initializes the stable content identifier.
    /// </summary>
    public required string ContentId { get; init; }

    /// <summary>
    /// Gets or initializes the delta text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets or initializes the provider attempt identifier when the delta represents replaceable draft output.
    /// </summary>
    public string? AttemptId { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether this delta is live draft content that can be discarded by a retry.
    /// </summary>
    public bool IsDraft { get; init; } = true;

    /// <summary>
    /// Gets or initializes optional structured delta metadata.
    /// </summary>
    public JsonElement? Details { get; init; }
}

/// <summary>
/// Represents a best-effort provider session update emitted while a turn is running.
/// </summary>
public sealed record AgentTurnSessionUpdate
{
    /// <summary>
    /// Gets or initializes the session update kind.
    /// </summary>
    public required AgentSessionUpdateKind Kind { get; init; }

    /// <summary>
    /// Gets or initializes the user-facing update message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets or initializes optional structured update details.
    /// </summary>
    public JsonElement? Details { get; init; }

    /// <summary>
    /// Gets or initializes an optional live usage snapshot associated with this update.
    /// </summary>
    public AgentSessionUsage? Usage { get; init; }
}
