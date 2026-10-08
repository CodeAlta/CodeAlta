namespace CodeAlta.Agent;

/// <summary>
/// Optional capability of a session whose provider can start a turn by itself while the session has no run.
/// What orders the runs of the session starts one for such a turn, so that it is shown and recorded.
/// </summary>
internal interface IAgentProviderInitiatedRuns
{
    /// <summary>
    /// Registers what is called when the provider started a turn by itself. The handler must not wait.
    /// </summary>
    /// <param name="handler">What is called for each such turn.</param>
    /// <returns>The registration, which is disposed to end it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    IDisposable OnProviderInitiatedRun(Action handler);

    /// <summary>
    /// Returns the options of the run that shows the turn the provider started by itself, or
    /// <see langword="null" /> when nothing is left to show: a run that started meanwhile read it.
    /// </summary>
    AgentSendOptions? TakeProviderInitiatedRun();
}
