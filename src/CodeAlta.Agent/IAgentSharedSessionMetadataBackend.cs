namespace CodeAlta.Agent;

/// <summary>
/// Identifies backends whose sessions are loaded from a CodeAlta-owned shared session store rather than a backend-specific store.
/// </summary>
/// <remarks>
/// Backends that implement this marker should still support <see cref="IAgentBackend.ListSessionsAsync" /> for direct lookups,
/// but orchestration can recover the shared session store once and avoid asking every interchangeable local provider to rescan it.
/// </remarks>
public interface IAgentSharedSessionMetadataBackend
{
}
