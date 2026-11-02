using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent;

/// <summary>
/// Optional capability of a provider runtime whose provider keeps its own copy of a conversation on the machine (Claude
/// Code's transcript), so that a session moved to another machine carries it: the source copies it into the session
/// bundle, the target places it where its provider looks for it under the folder the session works in there.
/// </summary>
public interface IAgentSessionTransferProvider
{
    /// <summary>
    /// Copies what the provider keeps of a session into a folder of a bundle. Called while no process of the provider
    /// runs for the session. What the provider keeps on this machine is not changed.
    /// </summary>
    /// <param name="state">The provider state saved for the session.</param>
    /// <param name="workingDirectory">The folder the session works in.</param>
    /// <param name="destination">The folder of the bundle the copy goes to; created when something is copied.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>
    /// Whether something was copied, and what went wrong in words when the provider keeps something that could not be
    /// copied (the session then continues on the target as after a provider switch).
    /// </returns>
    /// <exception cref="ArgumentNullException">The state is null.</exception>
    /// <exception cref="ArgumentException">A folder is empty.</exception>
    /// <exception cref="OperationCanceledException">The copy was canceled.</exception>
    Task<(bool Copied, string? Warning)> ExportSessionAsync(AgentSessionState state, string workingDirectory, string destination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Places a copy made by <see cref="ExportSessionAsync"/> on another machine where the provider of this machine looks
    /// for it, for the folder the session works in here. Called before any process of the provider runs for the session.
    /// </summary>
    /// <param name="state">The provider state saved for the session.</param>
    /// <param name="source">The folder of the bundle that holds the copy.</param>
    /// <param name="workingDirectory">The folder the session works in on this machine.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns><see langword="null"/> when it was placed or there was nothing to place; otherwise what went wrong, in words.</returns>
    /// <exception cref="ArgumentNullException">The state is null.</exception>
    /// <exception cref="ArgumentException">A folder is empty.</exception>
    /// <exception cref="OperationCanceledException">The copy was canceled.</exception>
    Task<string?> ImportSessionAsync(AgentSessionState state, string source, string workingDirectory, CancellationToken cancellationToken = default);
}
