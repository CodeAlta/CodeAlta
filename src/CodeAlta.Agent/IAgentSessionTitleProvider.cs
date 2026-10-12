namespace CodeAlta.Agent;

/// <summary>Optional capability for renaming a session without replacing its active attachment.</summary>
public interface IAgentSessionTitleProvider
{
    /// <summary>Persists a user-chosen title in the session's state-write order. Later turns retain this title.</summary>
    /// <param name="title">The nonblank title chosen by the user.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the title has been persisted.</returns>
    /// <exception cref="ArgumentException">The title is blank.</exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    Task RenameAsync(string title, CancellationToken cancellationToken = default);
}
