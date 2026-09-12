namespace CodeAlta.Agent;

/// <summary>Optional capability for settled manual compaction of a session only if idle at admission.</summary>
public interface IAgentIdleCompactionProvider
{
    /// <summary>Attempts admission without waiting and compacts the context current at admission.</summary>
    /// <param name="cancellationToken">Cancels admission or the actual compaction work.</param>
    /// <returns>Null only when busy and no compaction started; otherwise the settled outcome, including unsuccessful outcomes.</returns>
    /// <remarks>Implementations must atomically refuse active runs and exclude new runs until compaction settles.
    /// Returning without joining actual work, or falling back to unconditional compaction, is not supported.</remarks>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="NotSupportedException">The provider cannot perform idle compaction.</exception>
    /// <exception cref="Exception">Compaction failed; no background work is left unconfirmed by this call.</exception>
    Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(CancellationToken cancellationToken = default);
}
