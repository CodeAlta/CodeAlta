namespace CodeAlta.Agent;

/// <summary>Optional exact-run cancellation, without an unconditional abort fallback.</summary>
/// <remarks>Supporting providers must honor a supplied <see cref="AgentSendOptions.RunLifecycle"/>.
/// Run/source identity is validated atomically at mutation admission, not inferred from events.</remarks>
public interface IAgentTargetedAbortProvider
{
    /// <summary>Signals only the expected active run and joins its original cancellation traversal.</summary>
    /// <param name="expectedRunId">The nonblank exact provider run identity; never retargeted.</param>
    /// <param name="cancellationToken">Cancels admission only. Once admitted, the original traversal remains owned and joined.</param>
    /// <returns>Whether cancellation was signalled, or the expected run is no longer active.</returns>
    /// <remarks>Signalling is not run completion, rollback, or revocation of previously accepted decisions.
    /// Cancellation callbacks must not wait for their own send, abort, or session disposal.</remarks>
    /// <exception cref="ArgumentException">The expected run identity is blank.</exception>
    /// <exception cref="OperationCanceledException">Admission was cancelled before mutation.</exception>
    /// <exception cref="ObjectDisposedException">Session admission is closed.</exception>
    /// <exception cref="NotSupportedException">Exact-run cancellation is unsupported; no fallback may be performed.</exception>
    /// <exception cref="Exception">Cancellation callbacks failed, possibly after cancellation was signalled.</exception>
    Task<AgentTargetedAbortOutcome> AbortRunAsync(AgentRunId expectedRunId, CancellationToken cancellationToken = default);
}

/// <summary>Settled exact-run cancellation dispatch outcome, not a claim of run completion.</summary>
public enum AgentTargetedAbortOutcome
{
    /// <summary>The expected run was not active at authoritative admission; no cancellation was started.</summary>
    TargetNotActive,
    /// <summary>The original run was signalled and its original cancellation traversal was joined.</summary>
    CancellationSignalled,
}
