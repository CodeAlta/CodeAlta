namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The exception thrown when a session is created with an identifier that a session of the store, or one being
/// created, already has.
/// </summary>
public sealed class SessionIdConflictException : InvalidOperationException
{
    /// <summary>Initializes the exception for a session identifier.</summary>
    /// <param name="sessionId">The identifier that is taken.</param>
    public SessionIdConflictException(string sessionId)
        : base($"A session with the id '{sessionId}' already exists.")
    {
        SessionId = sessionId;
    }

    /// <summary>Gets the identifier that is taken.</summary>
    public string SessionId { get; }
}
