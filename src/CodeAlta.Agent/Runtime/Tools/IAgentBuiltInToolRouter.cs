namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Decides where the built-in tools of a session run: the host gives one to a session whose tools may run elsewhere
/// than in its own process, such as in the worker of a sandbox. At the start of each run, the session calls
/// <see cref="RefuseRunAsync"/>, then <see cref="Route"/>.
/// </summary>
public interface IAgentBuiltInToolRouter
{
    /// <summary>Gives the built-in tools of a session, each with the handler that runs it where it must run.</summary>
    /// <param name="options">What the tools were made with: the session, its folder, its permission handler and guards.</param>
    /// <param name="tools">The built-in tools, as <see cref="AgentBuiltInToolFactory.CreateDefaultTools"/> made them.</param>
    /// <returns>The tools, with the specifications that apply to the run: a tool may take one more parameter there.</returns>
    IReadOnlyList<AgentToolDefinition> Route(AgentBuiltInToolOptions options, IReadOnlyList<AgentToolDefinition> tools);

    /// <summary>
    /// Says why a run of the session cannot start now, or null when it can: a session whose tools must run elsewhere
    /// cannot run on a provider that runs its own tools in its own process.
    /// </summary>
    /// <param name="providerRunsItsOwnTools">Whether the provider of the run runs its own tools.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reason, for the user and the agent; null when the run can start.</returns>
    ValueTask<string?> RefuseRunAsync(bool providerRunsItsOwnTools, CancellationToken cancellationToken);
}
