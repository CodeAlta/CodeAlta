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

    /// <summary>Attempts admission without waiting and compacts the context current at admission, with options.</summary>
    /// <param name="options">What the compaction is given besides the conversation, such as instructions for its summary.</param>
    /// <param name="cancellationToken">Cancels admission or the actual compaction work.</param>
    /// <returns>Null only when busy and no compaction started; otherwise the settled outcome, including unsuccessful outcomes.</returns>
    /// <remarks>An implementation that takes no instructions refuses them rather than compacting without them.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="NotSupportedException">The provider cannot perform idle compaction, or cannot follow instructions.</exception>
    /// <exception cref="Exception">Compaction failed; no background work is left unconfirmed by this call.</exception>
    Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(AgentCompactionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.Instructions)
            ? TryCompactWhenIdleAsync(cancellationToken)
            : throw new NotSupportedException("The session cannot follow instructions for a compaction.");
    }
}
