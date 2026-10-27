using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Hosting;

/// <summary>What a headless run opens: a new session, a session to resume, or a session named by the caller.</summary>
public sealed record HeadlessSessionRequest
{
    /// <summary>
    /// Gets the folder a new session works in, an absolute path: a project of the catalog, or a folder outside it, which the
    /// session is bound to without registering a project. Ignored for a session that exists.
    /// </summary>
    public string? ProjectFolder { get; init; }

    /// <summary>
    /// Gets the git worktree a new session works in, an existing folder of a checkout of the repository of
    /// <see cref="ProjectFolder"/>; null for <see cref="ProjectFolder"/> itself. Ignored for a session that exists.
    /// </summary>
    public string? WorktreeDirectory { get; init; }

    /// <summary>Gets the model as a reference <c>provider:model</c> or <c>provider:model@reasoning</c>; null for <see cref="Tier"/> or the usual model.</summary>
    public string? ModelRef { get; init; }

    /// <summary>Gets the tier of models to take the model from; null for <see cref="ModelRef"/> or the usual model.</summary>
    public ModelTier? Tier { get; init; }

    /// <summary>Gets the reasoning effort, over the one of the model reference or the tier; null for theirs.</summary>
    public AgentReasoningEffort? Reasoning { get; init; }

    /// <summary>Gets the agent prompt (<c>default</c>, <c>plan</c>...); null for the one of the session, or the default one.</summary>
    public string? AgentPromptId { get; init; }

    /// <summary>
    /// Gets an identifier the caller chooses: the session is opened when it exists, and created with this identifier
    /// otherwise (<see cref="SessionRuntimeService.TryNormalizeSessionId"/>).
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the identifier of a session to resume, which must exist.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>Gets the title of a new session; null for the usual one.</summary>
    public string? Title { get; init; }

    /// <summary>Gets patterns over the names of the tools the session does without (<see cref="AgentToolPatterns"/>).</summary>
    public IReadOnlyList<string> DisabledToolPatterns { get; init; } = [];

    /// <summary>
    /// Gets the sandbox profile the session runs in, saved as its own choice (<c>workspace</c>, <c>strict</c>, a profile
    /// of the user, or <c>off</c>); null for what the settings decide.
    /// </summary>
    public string? Sandbox { get; init; }

    /// <summary>
    /// Gets the options a new session is created with (<see cref="CodeAlta.Agent.AgentSessionOptions"/>), narrowed by those
    /// of the session that creates it; null for the defaults. A session that exists keeps the options saved with it.
    /// </summary>
    public CodeAlta.Agent.AgentSessionOptions? SessionOptions { get; init; }

    /// <summary>
    /// Gets the session a new session is the child of, or null. The child is in its tree (aborts, retention) and takes
    /// the permission mode a session it creates takes (<see cref="SessionRuntimeService.GetCreatedSessionPermissionMode"/>).
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>Gets who creates a new session, or null for the driver itself.</summary>
    public AltaActorProvenance? CreatedBy { get; init; }
}

/// <summary>How the opening of a headless session went.</summary>
public enum HeadlessOpenStatus
{
    /// <summary>A session that exists was opened.</summary>
    Opened,

    /// <summary>A new session was created.</summary>
    Created,

    /// <summary>The request is not valid; its message says why.</summary>
    InvalidRequest,

    /// <summary>The session to resume does not exist.</summary>
    NotFound,

    /// <summary>The provider or the model cannot be used now: none is configured, the model is not listed, or a sign-in is needed.</summary>
    ProviderNotReady,

    /// <summary>Another process runs the session.</summary>
    SessionInUse,
}

/// <summary>A session a headless driver opened.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Descriptor">The session as the runtime knows it.</param>
/// <param name="ProviderKey">The provider its prompts run on.</param>
/// <param name="ModelId">The model its prompts run with.</param>
/// <param name="ReasoningEffort">The reasoning effort, or <see langword="null"/> for the one of the model.</param>
/// <param name="AgentPromptId">The agent prompt.</param>
/// <param name="Tier">The tier the model was taken from, or <see langword="null"/>.</param>
/// <param name="Created">Whether the session was created by the opening.</param>
public sealed record HeadlessSession(
    string SessionId,
    SessionViewDescriptor Descriptor,
    string ProviderKey,
    string ModelId,
    AgentReasoningEffort? ReasoningEffort,
    string AgentPromptId,
    ModelTier? Tier,
    bool Created)
{
    /// <summary>Gets the model as a reference <c>provider:model@reasoning</c>.</summary>
    public string ModelRef => SessionProcessEnvironment.FormatModel(ProviderKey, ModelId, ReasoningEffort);

    // What each prompt is sent with: the model, the effort, the agent prompt and the mode that bypasses permissions.
    internal OwnedSessionSelection? Selection { get; init; }
}

/// <summary>The opening of a headless session.</summary>
/// <param name="Status">How it went.</param>
/// <param name="Session">The session, when it was opened or created.</param>
/// <param name="Message">What went wrong, for a status that is not a success.</param>
public sealed record HeadlessSessionOpenResult(HeadlessOpenStatus Status, HeadlessSession? Session, string? Message = null)
{
    /// <summary>Gets whether a session was opened or created.</summary>
    public bool Succeeded => Session is not null;
}

/// <summary>What <see cref="HeadlessSessionDriver.ChangeSelectionAsync"/> changes; what is null stays as it is.</summary>
public sealed record HeadlessSelectionChange
{
    /// <summary>Gets the model as a reference <c>provider:model</c> or <c>provider:model@reasoning</c>; null keeps the model.</summary>
    public string? ModelRef { get; init; }

    /// <summary>Gets the reasoning effort, over the one of <see cref="ModelRef"/>; null keeps the effort, or takes the model's.</summary>
    public AgentReasoningEffort? Reasoning { get; init; }

    /// <summary>Gets the agent prompt (<c>default</c>, <c>plan</c>...); null keeps it.</summary>
    public string? AgentPromptId { get; init; }
}

/// <summary>A prompt of a headless run.</summary>
/// <param name="Text">The text of the prompt.</param>
public sealed record HeadlessPrompt(string Text)
{
    /// <summary>Gets what is attached to the prompt by reference: files, folders, images, text blocks.</summary>
    public IReadOnlyList<SessionPromptAttachment> Attachments { get; init; } = [];

    /// <summary>Gets the provenance the prompt is recorded with (<see cref="CodeAlta.Agent.AgentPromptProvenance"/>), or null for a prompt of the user.</summary>
    public string? Provenance { get; init; }
}

/// <summary>How a headless run ended.</summary>
public enum HeadlessRunStatus
{
    /// <summary>The session and the sessions it started settled, and the last run answered.</summary>
    Completed,

    /// <summary>A run failed (provider, tool), or ended without an answer.</summary>
    Failed,

    /// <summary>The run was aborted (<see cref="HeadlessSessionDriver.CancelAsync"/>, a signal, the caller's token).</summary>
    Aborted,

    /// <summary>The time the run was given ran out; the run was aborted.</summary>
    Timeout,

    /// <summary>The provider or the model cannot be used: none is configured, the model is not listed, or a sign-in is needed.</summary>
    ProviderNotReady,

    /// <summary>Another process runs the session.</summary>
    SessionInUse,
}

/// <summary>The result of a headless run: what a script or a protocol reports when the session has settled.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="SessionId">The session.</param>
/// <param name="RunId">The run the prompt started, when it started one.</param>
/// <param name="FinalMessage">The last answer of the session, whatever the status; null when it gave none.</param>
/// <param name="AbortReason">Why the run was aborted (<c>timeout</c>, <c>signal</c>...), for an aborted run.</param>
/// <param name="FailureKind">The kind of failure the provider classified, for a failed run.</param>
/// <param name="FailureMessage">What went wrong, for a run that did not complete.</param>
/// <param name="Usage">The tokens and the cost of the run: the session and the sessions it started, from the prompt on.</param>
/// <param name="Duration">The time from the prompt to the settlement.</param>
public sealed record HeadlessRunResult(
    HeadlessRunStatus Status,
    string SessionId,
    AgentRunId? RunId,
    string? FinalMessage,
    string? AbortReason,
    AgentFailureKind? FailureKind,
    string? FailureMessage,
    SessionUsageTotals Usage,
    TimeSpan Duration);

/// <summary>An event of a session a headless driver runs, or of a session it started.</summary>
/// <param name="RootSessionId">The session the driver opened.</param>
/// <param name="Event">The event, of that session or of one it started.</param>
public sealed record HeadlessSessionEvent(string RootSessionId, SessionRuntimeEvent Event)
{
    /// <summary>
    /// Gets how many runtime events, of any session, the bounded runtime stream dropped between the previous event given to
    /// the observer and this one. Zero when none was: when it is not, what the observer saw of a stream of deltas may have
    /// gaps, which a face repairs from the completed content.
    /// </summary>
    public long DroppedBefore { get; init; }
}

/// <summary>The options of a headless driver.</summary>
public sealed record HeadlessSessionDriverOptions
{
    /// <summary>
    /// Gets what receives the events of the sessions the driver opened and of the sessions they started, in order, from
    /// the one reader of the runtime events the driver owns; null reads none. A slow observer costs events, never memory:
    /// the runtime keeps a bounded number of them.
    /// </summary>
    public Func<HeadlessSessionEvent, CancellationToken, ValueTask>? Observer { get; init; }

    /// <summary>
    /// Gets how long a run that was aborted (its time ran out, it was cancelled) is waited for to settle before its result
    /// is reported anyway. The default is 30 seconds.
    /// </summary>
    public TimeSpan AbortGrace { get; init; } = TimeSpan.FromSeconds(30);
}
