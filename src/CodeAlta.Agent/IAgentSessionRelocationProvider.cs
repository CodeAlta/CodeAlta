using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent;

/// <summary>
/// Optional capability of a provider runtime whose provider keeps its own copy of a conversation in a place named
/// after the folder the session works in, so that a session that works in another folder no longer finds it.
/// </summary>
public interface IAgentSessionRelocationProvider
{
    /// <summary>
    /// Moves what the provider keeps of a session from the place of one folder to the place of another. Called while
    /// no process of the provider runs for the session. A failure leaves what the provider keeps where it was: the
    /// session then goes on as the provider does with a conversation it cannot find.
    /// </summary>
    /// <param name="state">The provider state saved for the session.</param>
    /// <param name="fromDirectory">The folder the session worked in.</param>
    /// <param name="toDirectory">The folder the session works in now.</param>
    /// <param name="cancellationToken">Cancels the move before it changes anything.</param>
    /// <returns><see langword="null"/> when the move succeeded or had nothing to do; otherwise what went wrong, in words.</returns>
    /// <exception cref="ArgumentNullException">The state is null.</exception>
    /// <exception cref="ArgumentException">A folder is empty.</exception>
    /// <exception cref="OperationCanceledException">The move was cancelled before it changed anything.</exception>
    Task<string?> RelocateSessionAsync(AgentSessionState state, string fromDirectory, string toDirectory, CancellationToken cancellationToken = default);
}
