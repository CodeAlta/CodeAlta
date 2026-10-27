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

/// <summary>
/// A turn executor that keeps something for each session (a process of an agent CLI): the session releases it when
/// it is closed.
/// </summary>
public interface IAgentProviderSessionCleanup
{
    /// <summary>Releases what the executor keeps for a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>A task that ends once it is released.</returns>
    ValueTask DisposeProviderSessionAsync(string sessionId);
}

/// <summary>
/// A turn executor that tells how the instructions of CodeAlta reach the model of a session, when it is not as a
/// system and a developer message: recorded with the system prompt of the session, so that the user sees it.
/// </summary>
public interface IAgentProviderInstructionsChannel
{
    /// <summary>
    /// Returns how the instructions of a session reach the model, such as <c>appended-system-prompt</c> or
    /// <c>first-prompt</c>, or <see langword="null" /> for a system and a developer message.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    string? GetInstructionsChannel(string sessionId);
}

/// <summary>
/// The handlers of one run, given to a provider that runs tools itself.
/// </summary>
/// <param name="SessionId">The session of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="OnPermissionRequest">Asks the host whether a tool of the provider may run.</param>
/// <param name="OnUserInputRequest">Asks the user a question of the provider, when the run can.</param>
/// <param name="ProviderInitiated">Whether the run shows a turn the provider started by itself: its message is not sent to the provider.</param>
/// <param name="OnConversationHeld">
/// Asks the user what to do when the conversation the run is to continue is held by another process; null when the run
/// has nobody to ask: the run then fails with a message that says how to go on.
/// </param>
public sealed record AgentProviderRunContext(
    string SessionId,
    AgentRunId RunId,
    AgentPermissionRequestHandler OnPermissionRequest,
    AgentUserInputRequestHandler? OnUserInputRequest,
    bool ProviderInitiated = false,
    AgentConversationHeldHandler? OnConversationHeld = null);

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
/// A turn executor whose provider can put the turn of a session that runs now in another permission mode (an agent
/// CLI that decides by itself what it asks before using a tool).
/// </summary>
public interface IAgentProviderPermissionMode
{
    /// <summary>
    /// Puts the process of a session in a permission mode now, during a turn or between turns, without starting it again.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="permissionMode">One of the permission modes of the provider, or null for the one it is configured with.</param>
    /// <param name="cancellationToken">Cancels the wait for the provider.</param>
    /// <returns>Whether the provider took the mode; when it did not, its next turn requests it.</returns>
    Task<bool> SetRunningPermissionModeAsync(string sessionId, string? permissionMode, CancellationToken cancellationToken);
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
/// A turn executor whose provider can let a session be followed and driven from elsewhere (Claude Code's Remote
/// Control).
/// </summary>
internal interface IAgentProviderRemoteControl
{
    /// <summary>
    /// Registers what is called when the remote control of a session changes. The handler is called while the
    /// provider is being read: it must not wait.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="handler">What is called for each change.</param>
    /// <returns>The registration, which is disposed to end it.</returns>
    IDisposable OnRemoteControlChanged(string sessionId, Action<AgentRemoteControl> handler);

    /// <summary>Returns the remote control of a session now.</summary>
    /// <param name="sessionId">The session.</param>
    AgentRemoteControl GetRemoteControl(string sessionId);

    /// <summary>
    /// Turns the remote control of a session on or off. The request is the one a turn would have, without its
    /// prompt: the provider starts what it needs from it when nothing runs.
    /// </summary>
    /// <param name="request">What the session runs with.</param>
    /// <param name="enabled">Whether the session is to be controlled remotely.</param>
    /// <param name="name">The name the session is shown under remotely, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The remote control of the session after the request.</returns>
    Task<AgentRemoteControl> SetRemoteControlAsync(AgentTurnRequest request, bool enabled, string? name, CancellationToken cancellationToken);
}

/// <summary>
/// A turn executor whose provider runs tools itself (an agent CLI): the session still shows and records each
/// tool call, but the definition that "runs" it comes from the executor.
/// </summary>
public interface IAgentProviderToolHost
{
    /// <summary>
    /// Gives the handlers of a run before its first turn.
    /// </summary>
    /// <param name="context">The handlers of the run.</param>
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
public interface IAgentProviderCompaction
{
    /// <summary>
    /// Compacts the context the provider keeps for the session of <paramref name="request" />.
    /// </summary>
    /// <param name="request">What the session runs with, without a prompt.</param>
    /// <param name="cancellationToken">A token to cancel the compaction.</param>
    /// <returns>What the compaction did.</returns>
    Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A turn executor whose provider keeps the conversation of a session it continues (the transcript of a CLI): a
/// session read again for it does not read the conversation CodeAlta recorded, and gives it to the executor in
/// <see cref="AgentTurnRequest.EarlierConversation" /> instead, to load only when the provider cannot continue its own.
/// </summary>
/// <remarks>
/// A session is read again that way only when its state holds a provider state
/// (<see cref="AgentSessionState.ProviderState" />): a provider that has none is given the whole conversation, as
/// any other. The provider compacts the conversation it keeps itself (<see cref="IAgentProviderCompaction" />).
/// </remarks>
public interface IAgentProviderKeptConversation : IAgentProviderCompaction
{
}

/// <summary>
/// The messages of the conversation of a session that come before <see cref="AgentTurnRequest.Conversation" />, which
/// the session did not read when it was read again: the provider holds them and continues from
/// <see cref="State" />. They are read once, when they are first loaded.
/// </summary>
public sealed class AgentEarlierConversation
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentConversationMessage>>> _load;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AgentConversationMessage>? _messages;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentEarlierConversation" /> class.
    /// </summary>
    /// <param name="state">The state of the session when it was read again: what the provider held then.</param>
    /// <param name="load">Reads the messages; called again after a failure or a cancellation only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state" /> or <paramref name="load" /> is <see langword="null" />.</exception>
    public AgentEarlierConversation(
        AgentSessionState state,
        Func<CancellationToken, Task<IReadOnlyList<AgentConversationMessage>>> load)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(load);
        State = state;
        _load = load;
    }

    /// <summary>
    /// Gets the state of the session when it was read again. The provider state it holds counts the messages
    /// the provider had been given then: the earlier conversation is those messages.
    /// </summary>
    public AgentSessionState State { get; }

    /// <summary>
    /// Gets the messages once they were loaded; <see langword="null" /> before.
    /// </summary>
    public IReadOnlyList<AgentConversationMessage>? Messages => Volatile.Read(ref _messages);

    /// <summary>
    /// Loads the messages, or returns them when they were loaded.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The messages of the earlier conversation, in order.</returns>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<IReadOnlyList<AgentConversationMessage>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Messages is { } loaded)
        {
            return loaded;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Messages is { } read)
            {
                return read;
            }

            var messages = await _load(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _messages, messages);
            return messages;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the whole conversation of a request: the earlier messages, loaded when they are not yet, then
    /// <see cref="AgentTurnRequest.Conversation" />.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The conversation of the request from its start.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is <see langword="null" />.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public static async Task<IReadOnlyList<AgentConversationMessage>> GetWholeConversationAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EarlierConversation is not { } earlier)
        {
            return request.Conversation;
        }

        var messages = await earlier.LoadAsync(cancellationToken).ConfigureAwait(false);
        return messages.Count == 0 ? request.Conversation : [.. messages, .. request.Conversation];
    }
}

internal sealed record AgentTurnFailure(
    string Message,
    bool IsContextOverflow,
    AgentFailureInfo? Failure = null);

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
    /// Gets or initializes the key that groups the requests of the session for the prompt cache of a provider that
    /// routes by one; null for the session identifier. A session that was forked has the key of the session at the
    /// start of its chain of forks, so that its first request reads what the source wrote to the cache.
    /// </summary>
    public string? PromptCacheKey { get; init; }

    /// <summary>
    /// Gets or initializes the model identifier.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Gets or initializes what starts the process of a provider that runs its own tools, for this run: the sandbox of
    /// the session; null to start it as a child of this process.
    /// </summary>
    public IAgentProcessHost? ProcessHost { get; init; }

    /// <summary>
    /// Gets or initializes the resolved model metadata when available.
    /// </summary>
    public AgentModelInfo? ModelInfo { get; init; }

    /// <summary>
    /// Gets or initializes the working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the environment variables of the session (<see cref="AgentSessionCreateOptions.Environment"/>),
    /// for a provider that starts a process of its own, such as a CLI. Null sets none.
    /// </summary>
    public AgentProcessEnvironment? Environment { get; init; }

    /// <summary>
    /// Gets or initializes the effective system message.
    /// </summary>
    public string? SystemMessage { get; init; }

    /// <summary>
    /// Gets or initializes the effective developer instructions.
    /// </summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>
    /// Gets or initializes the instructions of this session alone (where it works, its parent, its scratchpad, how it is
    /// titled), which a provider sends last, after <see cref="SystemMessage"/> and <see cref="DeveloperInstructions"/>:
    /// those are the same text for every session of a prompt, and the prompt cache of the provider serves them all.
    /// <see cref="AgentInstructionText"/> composes them. Null for none.
    /// </summary>
    public string? SessionInstructions { get; init; }

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
    /// Gets or initializes the built-in tools the session does without, by name (such as <c>shell_command</c>); a
    /// provider that runs its own tools turns off its counterparts. <see langword="null"/> or empty for none.
    /// </summary>
    public IReadOnlySet<string>? DisabledTools { get; init; }

    /// <summary>
    /// Gets or initializes where the file tools of the session may write, for a session that does without
    /// <c>shell_command</c>: a provider that runs its own file tools refuses the other paths. <see langword="null"/> for no limit.
    /// </summary>
    public CodeAlta.Agent.Runtime.Tools.AgentWriteGuard? WriteGuard { get; init; }

    /// <summary>
    /// Gets or initializes what checks each shell command before it runs, for a provider that runs its own shell and
    /// asks CodeAlta before a command; <see langword="null"/> for no check.
    /// </summary>
    public CodeAlta.Agent.Runtime.Tools.AgentCommandGuard? CommandGuard { get; init; }

    /// <summary>
    /// Gets or initializes the maximum number of output tokens the provider should generate when supported.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Gets or initializes the replayable conversation.
    /// </summary>
    public required IReadOnlyList<AgentConversationMessage> Conversation { get; init; }

    /// <summary>
    /// Gets or initializes the messages that come before <see cref="Conversation" /> and were not read, for a provider
    /// that keeps the conversation (<see cref="IAgentProviderKeptConversation" />); <see langword="null" /> when
    /// <see cref="Conversation" /> is the conversation from its start.
    /// </summary>
    public AgentEarlierConversation? EarlierConversation { get; init; }

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
    /// Gets or initializes the tool calls of <see cref="AssistantMessage"/> that must not run, by call identifier, with
    /// the error their result gives the model: a call whose arguments could not be read, such as one the output limit
    /// cut.
    /// </summary>
    public IReadOnlyDictionary<string, string>? UnrunnableToolCalls { get; init; }

    /// <summary>
    /// Gets or initializes the latest usage snapshot.
    /// </summary>
    public AgentSessionUsage? Usage { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether the request sent only what was added to a context the provider
    /// kept from a previous response (a provider-native continuation), so that <see cref="Usage"/> may count only
    /// that part. The session then keeps its own count of the whole conversation when the reported one is smaller;
    /// otherwise the reported count is the size of the context, even when it is smaller than the local estimate.
    /// </summary>
    public bool UsageIsIncremental { get; init; }

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
    /// Gets or initializes an optional provider title candidate. The shared runtime does not use it to rename
    /// the session; its saved title belongs to creation and explicit user Rename.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets or initializes an optional summary candidate.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets or initializes what the provider ran itself during the turn, such as a hosted web search. They are shown
    /// and journaled as activities, never replayed to the model: the assistant message keeps what it needs of them.
    /// </summary>
    public IReadOnlyList<AgentTurnProviderActivity>? ProviderActivities { get; init; }
}

/// <summary>
/// Represents an activity the provider ran itself during a turn, such as a hosted web search.
/// </summary>
/// <param name="Kind">The activity kind.</param>
/// <param name="ActivityId">The identifier the provider gave the activity.</param>
/// <param name="PartIndex">The index of the assistant message part the activity came before; the count of the parts when it came after them.</param>
/// <param name="Phase">The final phase of the activity.</param>
/// <param name="Name">The short name of the activity.</param>
/// <param name="Message">The text shown for the activity, such as the query of a search.</param>
/// <param name="Details">Optional structured details, such as <c>{"query": "..."}</c>.</param>
public sealed record AgentTurnProviderActivity(
    AgentActivityKind Kind,
    string ActivityId,
    int PartIndex,
    AgentActivityPhase Phase,
    string? Name,
    string? Message,
    JsonElement? Details);

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

    /// <summary>
    /// Gets or initializes whether the journal keeps this update: a retry or a fallback of the provider, with what
    /// failed. A <see cref="AgentSessionUpdateKind.Reconnecting"/> update is always kept; the others are transient
    /// unless this is set.
    /// </summary>
    public bool Durable { get; init; }
}
