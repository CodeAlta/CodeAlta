namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// Typed access to a machine's sessions. An authenticated node client and the personal phone page hosted by that node
/// use the same admission: work is admitted by the machine, never by a frontend's unrestricted alta dispatcher.
/// </summary>
public interface IFleetSessionAccess
{
    /// <summary>Reads an allowlisted record of the machine.</summary>
    /// <param name="parameters">The read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The records.</returns>
    Task<FleetSessionReadResult> ReadSessionAsync(FleetSessionReadParams parameters, CancellationToken cancellationToken = default);

    /// <summary>Reads the exact run that a steer or stop may address.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its runtime attachment and run.</returns>
    Task<FleetSessionState> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Creates admitted, isolated work on the machine.</summary>
    /// <param name="parameters">The new session.</param>
    /// <param name="retryKey">The action's stable retry key.</param>
    /// <param name="cancellationToken">Cancels waiting, not an admitted creation.</param>
    /// <returns>The session created by this action.</returns>
    Task<FleetCreateSessionResult> CreateSessionAsync(FleetCreateSessionParams parameters, string? retryKey = null, CancellationToken cancellationToken = default);

    /// <summary>Admits a prompt through the machine's policy, worktree, budgets and seats.</summary>
    /// <param name="parameters">The prompt and selection.</param>
    /// <param name="retryKey">The action's stable retry key.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The admission.</returns>
    Task<FleetCommandResult> SendAsync(FleetSendParams parameters, string? retryKey = null, CancellationToken cancellationToken = default);

    /// <summary>Queues admitted work.</summary>
    /// <param name="parameters">The queued prompt.</param>
    /// <param name="retryKey">The action's stable retry key.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The queue item created by this action.</returns>
    Task<FleetQueueResult> QueueAsync(FleetQueueParams parameters, string? retryKey = null, CancellationToken cancellationToken = default);

    /// <summary>Steers one exact run.</summary>
    /// <param name="parameters">The run and prompt.</param>
    /// <param name="retryKey">The action's stable retry key.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The admission.</returns>
    Task<FleetCommandResult> SteerAsync(FleetSteerParams parameters, string? retryKey = null, CancellationToken cancellationToken = default);

    /// <summary>Stops one exact run.</summary>
    /// <param name="parameters">The run.</param>
    /// <param name="retryKey">The action's stable retry key.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The admission.</returns>
    Task<FleetCommandResult> AbortAsync(FleetAbortParams parameters, string? retryKey = null, CancellationToken cancellationToken = default);
}
