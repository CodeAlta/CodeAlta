namespace CodeAlta.Agent;

/// <summary>
/// Represents a single conversation session.
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the provider/runtime identifier carried by this session.
    /// </summary>
    ModelProviderId ProviderId { get; }

    /// <summary>
    /// Gets the durable CodeAlta session identifier.
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// Gets the optional provider/runtime workspace directory for this session.
    /// </summary>
    string? WorkspacePath { get; }

    /// <summary>
    /// Streams normalized agent events.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the enumeration.</param>
    IAsyncEnumerable<AgentEvent> StreamEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to normalized agent events.
    /// </summary>
    /// <param name="handler">The event handler.</param>
    /// <returns>An <see cref="IDisposable"/> that unsubscribes when disposed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> is <see langword="null"/>.</exception>
    IDisposable Subscribe(Action<AgentEvent> handler);

    /// <summary>
    /// Sends user input to the session.
    /// </summary>
    /// <param name="options">Send options.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The provider/runtime run identifier (e.g. turn id or message id).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Steers the currently active run without starting a new one.
    /// </summary>
    /// <param name="options">Steering options.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The provider/runtime run identifier that accepted the steering input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when the provider/runtime does not support steering.</exception>
    /// <exception cref="InvalidOperationException">Thrown when there is no active run to steer.</exception>
    Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes back a steering input that the active run has not given to the model yet.
    /// </summary>
    /// <param name="withdrawalId">The <see cref="AgentSteerOptions.WithdrawalId"/> of the input.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the input was taken back and will never reach the model; <see langword="false"/> when the
    /// run already took it, or when the session cannot take an input back.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="withdrawalId"/> is empty.</exception>
    Task<bool> TryWithdrawSteerAsync(string withdrawalId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Aborts/cancels the currently running work in this session (best effort).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task AbortAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a manual session compaction when supported by the provider/runtime.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="NotSupportedException">Thrown when the provider/runtime does not support manual compaction.</exception>
    Task CompactAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the stored history for the session (best effort).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default);
}
