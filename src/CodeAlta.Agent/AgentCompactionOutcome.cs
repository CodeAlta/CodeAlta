namespace CodeAlta.Agent;

/// <summary>
/// Represents the outcome of a manual compaction operation when the provider returns a synchronous result.
/// </summary>
/// <param name="Success">Whether compaction completed successfully.</param>
/// <param name="Message">Optional user-visible summary.</param>
/// <param name="MessagesRemoved">Optional number of removed messages.</param>
/// <param name="TokensRemoved">Optional number of removed tokens.</param>
/// <param name="PreCompactionTokens">Optional token count before compaction.</param>
/// <param name="PostCompactionTokens">Optional token count after compaction.</param>
public sealed record AgentCompactionOutcome(
    bool Success,
    string? Message = null,
    int? MessagesRemoved = null,
    long? TokensRemoved = null,
    long? PreCompactionTokens = null,
    long? PostCompactionTokens = null)
{
    /// <summary>
    /// Gets the summary a successful compaction wrote of the conversation, when the session compacted it itself;
    /// <see langword="null"/> when a provider compacted the context it keeps, or when nothing was compacted.
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Optional session capability that exposes a synchronous manual compaction outcome.
/// </summary>
public interface IAgentCompactionOutcomeProvider
{
    /// <summary>
    /// Triggers manual compaction and returns a provider-reported outcome when available.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A compaction outcome when the provider completes the operation synchronously; otherwise <see langword="null" />.
    /// </returns>
    Task<AgentCompactionOutcome?> CompactWithOutcomeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers manual compaction with options and returns a provider-reported outcome when available.
    /// </summary>
    /// <param name="options">What the compaction is given besides the conversation, such as instructions for its summary.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A compaction outcome when the provider completes the operation synchronously; otherwise <see langword="null" />.
    /// </returns>
    /// <remarks>An implementation that takes no instructions refuses them rather than compacting without them.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="NotSupportedException">The session cannot follow instructions for a compaction.</exception>
    Task<AgentCompactionOutcome?> CompactWithOutcomeAsync(AgentCompactionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.Instructions)
            ? CompactWithOutcomeAsync(cancellationToken)
            : throw new NotSupportedException("The session cannot follow instructions for a compaction.");
    }
}
