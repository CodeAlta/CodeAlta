namespace CodeAlta.Agent.Runtime;

/// <summary>Reports failure of notes feedback after the canonical journal record was acknowledged. The write was not rolled back.</summary>
public sealed class AgentNotesCommittedException : InvalidOperationException
{
    /// <summary>Creates an explicit post-commit failure.</summary>
    /// <param name="sessionId">The session whose notes were committed.</param>
    /// <param name="innerException">The cache or observer failure.</param>
    /// <exception cref="ArgumentException">The session identifier is empty.</exception>
    /// <exception cref="ArgumentNullException">The inner exception is null.</exception>
    public AgentNotesCommittedException(string sessionId, Exception innerException)
        : base($"Notes for session '{sessionId}' were committed, but cache or observer feedback failed. Read notes again before retrying.", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(innerException);
    }
}
