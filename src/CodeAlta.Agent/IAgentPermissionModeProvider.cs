namespace CodeAlta.Agent;

/// <summary>
/// Optional capability of a session whose permission mode can change while it stays attached.
/// </summary>
public interface IAgentPermissionModeProvider
{
    /// <summary>Gets the permission mode of the session, or <see langword="null"/> for the one of its provider.</summary>
    string? PermissionMode { get; }

    /// <summary>
    /// Sets the permission mode the next turns of the session request (<see cref="Runtime.AgentTurnRequest.PermissionMode"/>).
    /// The session takes it at once when <see cref="ApplyPermissionModeNowAsync"/> is called.
    /// </summary>
    /// <param name="permissionMode">One of the permission modes of the provider, or <see langword="null"/> for the one it is configured with.</param>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    void SetPermissionMode(string? permissionMode);

    /// <summary>
    /// Gives the permission mode set last to the session now: the next turns of a run in progress request it, and a
    /// provider that keeps a process for the session is told at once, between runs as during one, so that what it
    /// does next, a turn it starts by itself included, follows it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for the provider.</param>
    /// <returns>Whether the provider took the mode now; false when it only takes it with the next turn.</returns>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    Task<bool> ApplyPermissionModeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
