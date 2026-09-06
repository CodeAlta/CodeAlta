namespace CodeAlta.Orchestration.Runtime;

/// <summary>Indicates that notes cannot resolve an active or recoverable session in the configured backend root.</summary>
public sealed class SessionNotesSessionNotFoundException : InvalidOperationException
{
    /// <summary>Creates a missing-session error.</summary>
    /// <param name="sessionId">The unresolved identifier.</param>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    public SessionNotesSessionNotFoundException(string sessionId)
        : base($"No known session '{sessionId}' is available for notes.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    }
}
