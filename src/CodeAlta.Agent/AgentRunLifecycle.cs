namespace CodeAlta.Agent;

/// <summary>Optional per-send observer of authoritative provider run activation and closing.</summary>
/// <remarks>
/// Supporting providers await Started outside state gates before permission-capable work and await Closing
/// on all admitted-run exit paths, including Started failure, before releasing the run's cancellation source.
/// Hooks must not await their own send or session disposal. They confer no approval or renderer authority.
/// Providers must explicitly support this contract; ignoring the option preserves their prior behavior but
/// does not establish a run binding suitable for exact-run control with owned permission review.
/// </remarks>
public abstract class AgentRunLifecycle
{
    /// <summary>Binds this send to its actual provider run and live execution token before permission work.</summary>
    /// <param name="runId">The actual activated run identity, not an observation or inferred identity.</param>
    /// <param name="executionToken">The provider-owned run token; not a token for abandoning this hook's task.</param>
    /// <exception cref="Exception">Binding failed. The provider must still call and join Closing.</exception>
    public abstract Task StartedAsync(AgentRunId runId, CancellationToken executionToken);

    /// <summary>Closes this exact send's authority and joins its deliveries before provider source release.</summary>
    /// <param name="runId">The same actual run identity delivered to Started.</param>
    /// <remarks>No caller cancellation can abandon closing. Previously accepted decisions are not revoked.</remarks>
    /// <exception cref="Exception">Closing failed; its original work must still be settled before the call fails.</exception>
    public abstract Task ClosingAsync(AgentRunId runId);
}
