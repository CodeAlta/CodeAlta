namespace CodeAlta.Agent;

/// <summary>
/// Optional capability of a session that owns its title while it is attached: it is renamed, and told how to title
/// itself, without replacing its active attachment.
/// </summary>
public interface IAgentSessionTitleProvider
{
    /// <summary>Gets how the session titles itself while nobody named it.</summary>
    AgentSessionTitleMode TitleMode { get; }

    /// <summary>
    /// Persists a title a person or an agent chose in the session's state-write order. Later turns retain this
    /// title: its source becomes <see cref="AgentSessionTitleSource.User"/>.
    /// </summary>
    /// <param name="title">The nonblank title chosen by the user.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the title has been persisted.</returns>
    /// <exception cref="ArgumentException">The title is blank.</exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    Task RenameAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets how the session titles itself from now on. A title a person or an agent chose stays.
    /// </summary>
    /// <param name="mode">The mode.</param>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    void SetTitleMode(AgentSessionTitleMode mode);

    /// <summary>
    /// Hands the title back to CodeAlta: its source becomes <see cref="AgentSessionTitleSource.Auto"/>, and the mode
    /// of the session is applied to what the session holds already (its first prompt, or its last message).
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the title has been persisted.</returns>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    Task ReleaseTitleAsync(CancellationToken cancellationToken = default);
}
