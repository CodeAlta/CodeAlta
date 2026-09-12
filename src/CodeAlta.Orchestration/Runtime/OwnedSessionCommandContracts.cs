using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Immutable text-only admission input; no caller-owned execution objects are accepted.</summary>
/// <param name="ClientRequestId">Ordinal owner-lifetime retry key.</param>
/// <param name="SessionId">Case-insensitive durable session identity, without leading or trailing whitespace.</param>
/// <param name="Text">Exact text; whitespace is not normalized.</param>
public sealed record OwnedTextSendRequest(string ClientRequestId, string SessionId, string Text);

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
    /// <summary>The session already has an owned send or control operation.</summary>
    Busy,
    /// <summary>The owner-lifetime receipt bound is exhausted.</summary>
    Capacity,
    /// <summary>Disposal has closed new admission.</summary>
    Closed,
    /// <summary>The abort target is not an owned send.</summary>
    UnknownTarget,
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
public sealed record OwnedSessionCommandResult(OwnedSessionCommandOutcome Outcome, AgentRunId? RunId = null, string? Code = null);

/// <summary>Admission result; replay returns the original receipt object. Rejections have no receipt.</summary>
/// <param name="Kind">Admission decision.</param>
/// <param name="Receipt">The new or replayed receipt.</param>
public sealed record OwnedSessionCommandAdmission(OwnedSessionCommandAdmissionKind Kind, OwnedSessionCommandReceipt? Receipt = null);

/// <summary>
/// Immutable owner-lifetime receipt. Completion reports command dispatch/control, not a transcript.
/// Cancelling a caller's wait on Completion does not cancel the retained operation.
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
    /// <summary>Gets the targeted send identity for an abort.</summary>
    public Guid? TargetOperationId { get; }
    /// <summary>Gets the retained, non-faulting terminal result task.</summary>
    public Task<OwnedSessionCommandResult> Completion => _completion.Task;

    internal void Complete(OwnedSessionCommandResult result) => _completion.TrySetResult(result);
}
