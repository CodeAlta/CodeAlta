using CodeAlta.Agent;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Runtime.Plugins;

/// <summary>Holds an exact supplied published-event reference and immutable scalar observation context.</summary>
/// <remarks>This envelope neither publishes nor sanitizes events, and does not establish runtime capture timing.</remarks>
public sealed class RuntimePluginAgentEventEnvelope
{
    /// <summary>Initializes an envelope without consulting a session descriptor, catalog or frontend.</summary>
    /// <param name="publishedEvent">The exact non-null event supplied by the caller.</param>
    /// <param name="sessionId">The captured session identifier; it is not replaced by a draft or selection identifier.</param>
    /// <param name="projectId">The captured project identifier, or null.</param>
    /// <param name="resolvedProjectPath">The already-resolved project path, or null.</param>
    /// <exception cref="ArgumentNullException">An event or session identifier is null.</exception>
    public RuntimePluginAgentEventEnvelope(AgentEvent publishedEvent, string sessionId, string? projectId, string? resolvedProjectPath)
    {
        ArgumentNullException.ThrowIfNull(publishedEvent);
        ArgumentNullException.ThrowIfNull(sessionId);
        Event = publishedEvent;
        SessionId = sessionId;
        ProjectId = projectId;
        ProjectPath = resolvedProjectPath;
    }

    /// <summary>Gets the supplied event reference, without cloning or sanitization.</summary>
    public AgentEvent Event { get; }

    /// <summary>Gets the captured session identifier.</summary>
    public string SessionId { get; }

    /// <summary>Gets the captured nullable project identifier.</summary>
    public string? ProjectId { get; }

    /// <summary>Gets the captured nullable resolved project path.</summary>
    public string? ProjectPath { get; }

    /// <summary>Captures the existing TUI event-path rule from scalar snapshots only.</summary>
    /// <param name="publishedEvent">The exact supplied event reference.</param>
    /// <param name="sessionId">The captured session identifier.</param>
    /// <param name="projectRef">The session's captured project reference.</param>
    /// <param name="workingDirectory">The session's captured working directory.</param>
    /// <param name="currentProjectId">The current project's captured identifier, or null when no current project exists.</param>
    /// <param name="currentProjectPath">The current project's captured path.</param>
    /// <returns>An envelope using the current path on a case-insensitive project-ID match, otherwise the working directory.</returns>
    /// <exception cref="ArgumentNullException">An event or session identifier is null.</exception>
    public static RuntimePluginAgentEventEnvelope Capture(
        AgentEvent publishedEvent, string sessionId, string? projectRef, string? workingDirectory,
        string? currentProjectId, string? currentProjectPath)
        => new(publishedEvent, sessionId, projectRef,
            currentProjectId is not null && string.Equals(currentProjectId, projectRef, StringComparison.OrdinalIgnoreCase)
                ? currentProjectPath : workingDirectory);

    internal PluginAdapterOperationOptions CreateOptions()
        => new()
        {
            ProjectId = ProjectId,
            ProjectPath = ProjectPath,
            SessionId = SessionId,
            RunId = Event.RunId?.Value,
            ProviderId = Event.ProviderId.Value,
            IsCodeAltaManagedProvider =
                !string.Equals(Event.ProviderId.Value, ModelProviderIds.Codex.Value, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Event.ProviderId.Value, ModelProviderIds.Copilot.Value, StringComparison.OrdinalIgnoreCase),
            HasInteractiveUi = false,
            IsHeadless = false,
        };
}

/// <summary>Observes supplied events through the existing bounded plugin adapter with an explicit escaping-failure policy.</summary>
/// <remarks>
/// Borrows the manager and each activation's existing services. It neither starts nor disposes them.
/// This helper creates no event reader and promises no publication, replay, ordering, durability or exactly-once semantics.
/// Its returned operation includes the awaited failure-policy tail, which is not a new activation lease.
/// The caller must retain that operation and its dependencies through actual completion.
/// </remarks>
public sealed class RuntimePluginAgentEventObserver
{
    private readonly PluginRuntimeManager _manager;
    private readonly Func<RuntimePluginAgentEventEnvelope, Exception, ValueTask> _failurePolicy;

    /// <summary>Initializes an observer without choosing a frontend or headless failure policy.</summary>
    /// <param name="manager">The borrowed plugin manager.</param>
    /// <param name="failurePolicy">Receives the exact envelope and escaping exception, including cancellation. Successful completion handles the failure.</param>
    /// <exception cref="ArgumentNullException">The manager or failure policy is null.</exception>
    public RuntimePluginAgentEventObserver(PluginRuntimeManager manager, Func<RuntimePluginAgentEventEnvelope, Exception, ValueTask> failurePolicy)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(failurePolicy);
        _manager = manager;
        _failurePolicy = failurePolicy;
    }

    /// <summary>Observes an event without caller cancellation.</summary>
    /// <param name="envelope">The supplied event and captured context.</param>
    /// <returns>Adapter diagnostics, or an empty result when the explicit policy handles an escaping failure.</returns>
    /// <exception cref="ArgumentNullException">The envelope is null.</exception>
    /// <exception cref="AggregateException">Both an adapter failure and a failure-policy exception are retained, in that order.</exception>
    public ValueTask<IReadOnlyList<PluginRuntimeDiagnostic>> ObserveAsync(RuntimePluginAgentEventEnvelope envelope)
        => ObserveAsync(envelope, CancellationToken.None);

    /// <summary>Observes an event using the active snapshot at this call, then awaits any escaping-failure policy.</summary>
    /// <param name="envelope">The exact supplied event and captured context; filtered/null publication must be skipped by the caller.</param>
    /// <param name="cancellationToken">The token passed unchanged to the existing adapter and callback contexts.</param>
    /// <returns>Adapter diagnostics, including explicit rejection outcomes. No partial diagnostic result is invented after an escaping failure.</returns>
    /// <remarks>
    /// Ordinary callback continuation, cancellation escape and success-only invalidation belong to the unchanged adapter.
    /// A policy that throws, including one that rethrows the supplied exception, produces an aggregate with both exact
    /// exception references. Shared identity is preserved as two entries; cancellation-shaped exceptions are not discarded.
    /// No implicit logging or fatal-reporting behavior is selected here.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The envelope is null.</exception>
    /// <exception cref="AggregateException">The escaping adapter exception and policy exception, including a rethrow, are retained in order.</exception>
    public async ValueTask<IReadOnlyList<PluginRuntimeDiagnostic>> ObserveAsync(RuntimePluginAgentEventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        try
        {
            var active = _manager.ActivePlugins;
            if (active.Count == 0) return [];
            var seed = active[0];
            return await _manager.Adapter.ObserveAgentEventAsync(active, new PluginAgentEventContext
            {
                Plugin = seed.Descriptor,
                Services = seed.RuntimeContext.Services,
                Event = envelope.Event,
            }, envelope.CreateOptions(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            try
            {
                await _failurePolicy(envelope, original).ConfigureAwait(false);
            }
            catch (Exception policyFailure)
            {
                throw new AggregateException("Plugin observation and its explicit failure policy both failed.", original, policyFailure);
            }
            return [];
        }
    }
}
