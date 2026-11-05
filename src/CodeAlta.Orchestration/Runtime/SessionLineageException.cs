namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The lineage of a session cannot be followed to its start: it is deeper than <see cref="SessionToolGuards.MaximumLineageDepth"/>,
/// or its links make a cycle. Whether it comes from the run of an automation cannot be told, so what depends on it (the guard of
/// the run, the credentials of an unattended run) holds as if it did.
/// </summary>
public sealed class SessionLineageException : InvalidOperationException
{
    /// <summary>What an agent is told when its session is refused for that.</summary>
    public const string Explanation = "The lineage of this session cannot be followed to the session it starts from (too deep, or its links make a cycle): "
        + "it may run for an automation that decides what it does without a person, so nothing is done without one. Ask the user.";

    /// <summary>Initializes a new instance of the <see cref="SessionLineageException"/> class.</summary>
    public SessionLineageException()
        : base(Explanation)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SessionLineageException"/> class for a session.</summary>
    /// <param name="sessionId">The session whose lineage cannot be followed.</param>
    public SessionLineageException(string sessionId)
        : base($"The lineage of the session '{sessionId}' cannot be followed to its start (too deep, or its links make a cycle).")
    {
        SessionId = sessionId;
    }

    /// <summary>Initializes a new instance of the <see cref="SessionLineageException"/> class with a message and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SessionLineageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the session whose lineage cannot be followed, when known.</summary>
    public string? SessionId { get; }
}
