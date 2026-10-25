namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The exception thrown when a runtime that takes the leases of its sessions cannot take the lease of one: the folder of the
/// leases cannot be created, written or read. Without its lease the session is neither attached nor changed, since nothing
/// would keep another process from writing it too; unlike <see cref="SessionInUseException"/>, nobody is known to hold it.
/// </summary>
public sealed class SessionLeaseUnavailableException : IOException
{
    /// <summary>Initializes the exception for a session and the failure of its lease.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="innerException">Why the lease cannot be taken.</param>
    /// <exception cref="ArgumentNullException"><paramref name="innerException"/> is <see langword="null"/>.</exception>
    public SessionLeaseUnavailableException(string sessionId, Exception innerException)
        : base($"The lease of session '{sessionId}' cannot be taken: {(innerException ?? throw new ArgumentNullException(nameof(innerException))).Message}", innerException)
    {
        SessionId = sessionId;
    }

    /// <summary>Gets the session.</summary>
    public string SessionId { get; }
}
