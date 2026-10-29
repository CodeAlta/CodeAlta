using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What an owned session's tools are created for.</summary>
/// <param name="SessionId">The session, or null while it is being created.</param>
/// <param name="ProjectId">The session's project, or null for a global session.</param>
/// <param name="WorkingDirectory">The directory the session works in.</param>
/// <param name="ProviderKey">The provider the session runs on.</param>
public sealed record OwnedSessionToolRequest(string? SessionId, string? ProjectId, string WorkingDirectory, string ProviderKey)
{
    /// <summary>Gets the model of this send, or null when its provider lists none; the mode of Code Mode depends on it.</summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Gets the folder of the session's project, or null for a global session. The tools of a session outlive a
    /// git worktree that is removed: once <see cref="WorkingDirectory"/> is gone, they work in this folder.
    /// </summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>
    /// Gets what the space of the session has, read at each send, or null while the session is being created: the tools a
    /// space limits (the scripts of the library offered as tools) are given only where it has them.
    /// </summary>
    public CodeAlta.Catalog.SpaceFilter? Spaces { get; init; }

    /// <summary>
    /// Gets the options of the session (<see cref="AgentSessionOptions"/>), read at each send: a session that cannot start
    /// sessions of its own is not offered the tools that start them, and its tools say nothing of them.
    /// </summary>
    public AgentSessionOptions Options { get; init; } = AgentSessionOptions.Default;
}

/// <summary>Text and optional bounded image admission input; no caller-owned execution objects are accepted.</summary>
/// <param name="ClientRequestId">Ordinal owner-lifetime retry key.</param>
/// <param name="SessionId">Case-insensitive durable session identity, without leading or trailing whitespace.</param>
/// <param name="Text">Exact text; whitespace is not normalized.</param>
public sealed record OwnedTextSendRequest(string ClientRequestId, string SessionId, string Text)
{
    /// <summary>Gets bounded encoded image copies; admission snapshots the list and preserves exact payloads for replay.</summary>
    public IReadOnlyList<OwnedPromptImage>? Images { get; init; }
    /// <summary>Gets the exact catalog scope whose references should be resolved once by the original worker.</summary>
    public OwnedProjectReferenceScope? References { get; init; }
    /// <summary>Gets the optional configuration captured for this send, never for an already-running turn.</summary>
    public OwnedSessionSelection? Selection { get; init; }
    /// <summary>
    /// Gets input items given after the text, by reference: files, folders and images by their path, text blocks
    /// (<see cref="Prompts.HeadlessPromptAttachmentService"/>); null for none. They are read when the run reads them.
    /// </summary>
    public IReadOnlyList<AgentInputItem>? Attachments { get; init; }
    /// <summary>
    /// Gets the provenance of a prompt the host sends (see <see cref="CodeAlta.Agent.AgentPromptProvenance"/>), recorded
    /// with the user message; null for a prompt the user typed.
    /// </summary>
    public string? Provenance { get; init; }
}

/// <summary>Expected catalog identity, not permission to use a renderer-supplied root.</summary>
/// <param name="ProjectId">Exact project ID.</param><param name="ProjectPath">Expected catalog path, compared before using the host catalog root.</param>
public sealed record OwnedProjectReferenceScope(string ProjectId, string ProjectPath);

/// <summary>Immutable next-send configuration. Null model/effort requests the provider default.</summary>
/// <param name="ProviderKey">Expected configured provider; this does not switch providers.</param>
/// <param name="AgentPromptId">Effective prompt identifier.</param>
/// <param name="ModelId">Selected model, or null for the provider default.</param>
/// <param name="ReasoningEffort">Supported effort, or null for the model default.</param>
public sealed record OwnedSessionSelection(string ProviderKey, string AgentPromptId, string? ModelId, AgentReasoningEffort? ReasoningEffort)
{
    /// <summary>Gets the immutable session choice; null keeps it, a selection with null tokens clears it.</summary>
    public AgentContextSelection? Context { get; init; }

    /// <summary>
    /// The <see cref="PermissionMode"/> that takes the session back to the mode its provider is configured with. It is
    /// never the identifier of a mode.
    /// </summary>
    public const string ProviderPermissionMode = "provider";

    /// <summary>
    /// Gets the permission mode chosen for the session from <see cref="OwnedSelectionChoices.PermissionModes"/>,
    /// <see cref="ProviderPermissionMode"/> for the one of its provider, or null to keep the one it has. The session
    /// keeps the choice for its next sends.
    /// </summary>
    public string? PermissionMode { get; init; }
}

/// <summary>Immutable abort input targeting one send, never a later send on the same session.</summary>
/// <param name="ClientRequestId">Ordinal owner-lifetime retry key.</param>
/// <param name="TargetOperationId">The accepted send receipt's operation identity.</param>
public sealed record OwnedAbortRequest(string ClientRequestId, Guid TargetOperationId);

/// <summary>Immutable text steering for one observed runtime attachment and run; never retargeted.</summary>
/// <param name="ClientRequestId">Ordinal owner-lifetime retry key.</param>
/// <param name="SessionId">Durable session identity without surrounding whitespace.</param>
/// <param name="ExpectedRuntimeInstanceId">Observed runtime instance identity.</param>
/// <param name="ExpectedAttachmentGeneration">Observed positive attachment generation.</param>
/// <param name="ExpectedRunId">Real, nonblank provider run identity from the observation.</param>
/// <param name="Text">Exact text; whitespace is not normalized.</param>
public sealed record OwnedTextSteerRequest(string ClientRequestId, string SessionId, Guid ExpectedRuntimeInstanceId,
    long ExpectedAttachmentGeneration, string ExpectedRunId, string Text);

/// <summary>Immutable request to compact an existing attachment if idle at provider admission; never retargeted.</summary>
/// <param name="ClientRequestId">Ordinal owner-lifetime retry key.</param>
/// <param name="SessionId">Exact durable session identity without surrounding whitespace.</param>
/// <param name="ExpectedRuntimeInstanceId">Observed runtime instance identity.</param>
/// <param name="ExpectedAttachmentGeneration">Observed positive attachment generation; not a history revision.</param>
public sealed record OwnedCompactRequest(string ClientRequestId, string SessionId, Guid ExpectedRuntimeInstanceId, long ExpectedAttachmentGeneration);

/// <summary>Immutable exact-run cancellation target; distinct from aborting an owned send receipt.</summary>
/// <param name="ClientRequestId">Ordinal owner-lifetime retry key.</param>
/// <param name="SessionId">Exact durable session identity without surrounding whitespace.</param>
/// <param name="ExpectedRuntimeInstanceId">Observed runtime instance identity.</param>
/// <param name="ExpectedAttachmentGeneration">Observed positive attachment generation.</param>
/// <param name="ExpectedRunId">Original provider run identity, never retargeted.</param>
public sealed record OwnedAbortRunRequest(string ClientRequestId, string SessionId, Guid ExpectedRuntimeInstanceId,
    long ExpectedAttachmentGeneration, string ExpectedRunId)
{
    internal void Validate()
    {
        if (!ValidIdentity(ClientRequestId) || !ValidIdentity(SessionId) || !ValidIdentity(ExpectedRunId)
            || ExpectedRuntimeInstanceId == Guid.Empty || ExpectedAttachmentGeneration <= 0)
            throw new ArgumentException("Exact cancellation requires bounded, well-formed session, runtime, attachment, run and retry identities.");
    }

    private static bool ValidIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value != value.Trim()) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || ++index == value.Length || !char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
}

/// <summary>The admitted command kind.</summary>
public enum OwnedSessionCommandKind
{
    /// <summary>Text submission.</summary>
    Send,
    /// <summary>Abort one owned submission.</summary>
    Abort,
    /// <summary>Submit text to one exactly targeted active run.</summary>
    Steer,
    /// <summary>Compact an exactly targeted attachment only if idle at actual admission.</summary>
    Compact,
    /// <summary>Signal cancellation of an exactly targeted provider run, not an owned send receipt.</summary>
    AbortRun,
}

/// <summary>Admission decision; only accepted requests allocate new retry receipts.</summary>
public enum OwnedSessionCommandAdmissionKind
{
    /// <summary>A new receipt was reserved.</summary>
    Accepted,
    /// <summary>The same immutable request already has this receipt.</summary>
    Replay,
    /// <summary>The retry key belongs to a different payload or command kind.</summary>
    Conflict,
    /// <summary>The session already has an outstanding operation in the requested command slot.</summary>
    Busy,
    /// <summary>Every receipt the owner keeps belongs to a command that is still pending.</summary>
    Capacity,
    /// <summary>Disposal has closed new admission.</summary>
    Closed,
    /// <summary>The target is not an owned operation of the required kind, or its receipt is no longer kept.</summary>
    UnknownTarget,
    /// <summary>
    /// The retry key belongs to a command that settled earlier and whose receipt made room for newer commands.
    /// The command is not run again, and its result is no longer known to the owner.
    /// </summary>
    Expired,
}

/// <summary>Terminal command outcome; successful submission is not transcript completion.</summary>
public enum OwnedSessionCommandOutcome
{
    /// <summary>The submission or control route returned.</summary>
    Completed,
    /// <summary>Owned cancellation stopped submission.</summary>
    Cancelled,
    /// <summary>The owner observed and retained a failure.</summary>
    Failed,
}

/// <summary>Immutable terminal result with a stable code, not raw provider exception text.</summary>
/// <param name="Outcome">Terminal outcome.</param>
/// <param name="RunId">Run identity when submission returned successfully.</param>
/// <param name="Code">Stable failure or no-op code; null for ordinary success.</param>
public sealed record OwnedSessionCommandResult(OwnedSessionCommandOutcome Outcome, AgentRunId? RunId = null, string? Code = null)
{
    /// <summary>
    /// Gets what a plugin told the user of a send or a steer it handled or cancelled itself
    /// (<see cref="OwnedSessionCommandService.PluginHandledCode"/>, <see cref="OwnedSessionCommandService.PluginCancelledCode"/>);
    /// null when it said nothing, and for every other result. It is the plugin's text, not the host's.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>Admission result; replay returns the original receipt object. Rejections have no receipt.</summary>
/// <param name="Kind">Admission decision.</param>
/// <param name="Receipt">The new or replayed receipt.</param>
public sealed record OwnedSessionCommandAdmission(OwnedSessionCommandAdmissionKind Kind, OwnedSessionCommandReceipt? Receipt = null);

/// <summary>
/// Immutable receipt of an admitted command. Completion reports command dispatch/control, not a transcript.
/// Cancelling a caller's wait on Completion does not cancel the retained operation. The owner keeps the
/// receipt while its command is pending, and afterwards until newer commands need its place; the object
/// stays valid for whoever holds it.
/// </summary>
public sealed class OwnedSessionCommandReceipt
{
    private readonly TaskCompletionSource<OwnedSessionCommandResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal OwnedSessionCommandReceipt(string clientRequestId, OwnedSessionCommandKind kind, string sessionId, Guid? targetOperationId = null)
    {
        OperationId = Guid.CreateVersion7();
        ClientRequestId = clientRequestId;
        Kind = kind;
        SessionId = sessionId;
        TargetOperationId = targetOperationId;
    }

    /// <summary>Gets the stable operation identity.</summary>
    public Guid OperationId { get; }
    /// <summary>Gets the exact ordinal retry key.</summary>
    public string ClientRequestId { get; }
    /// <summary>Gets the command kind.</summary>
    public OwnedSessionCommandKind Kind { get; }
    /// <summary>Gets the durable target session identity.</summary>
    public string SessionId { get; }
    /// <summary>Gets the original send identity for Abort.</summary>
    public Guid? TargetOperationId { get; }
    /// <summary>Gets the retained, non-faulting terminal result task.</summary>
    public Task<OwnedSessionCommandResult> Completion => _completion.Task;

    internal void Complete(OwnedSessionCommandResult result) => _completion.TrySetResult(result);
}
