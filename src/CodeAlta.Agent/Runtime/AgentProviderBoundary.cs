namespace CodeAlta.Agent.Runtime;

/// <summary>
/// What the requests of a confidential session pass through before they reach the provider, and its answers on the way
/// back: the session keeps the real values, and the provider sees only what <see cref="PrepareRequestAsync"/> leaves.
/// </summary>
/// <remarks>
/// A boundary belongs to one session. Its methods are called for one request at a time, in this order:
/// <see cref="PrepareRequestAsync"/>, then <see cref="CreateResponseStream"/> for each streamed content, then
/// <see cref="RestoreResponse"/> once the answer is complete.
/// </remarks>
public interface IAgentProviderBoundary
{
    /// <summary>Returns the request as the provider may see it.</summary>
    /// <param name="request">The request the session made, with the real values.</param>
    /// <param name="cancellationToken">Stops the preparation.</param>
    /// <returns>The request to send.</returns>
    /// <exception cref="AgentProviderBoundaryException">The request cannot be made safe: nothing is sent.</exception>
    ValueTask<AgentTurnRequest> PrepareRequestAsync(AgentTurnRequest request, CancellationToken cancellationToken);

    /// <summary>Creates what restores one streamed text of the answer (one content identifier) as its parts arrive.</summary>
    /// <param name="kind">The kind of the content.</param>
    /// <param name="contentId">The identifier of the content.</param>
    /// <returns>The restoring stream, which belongs to its caller.</returns>
    IAgentBoundaryTextStream CreateResponseStream(AgentContentKind kind, string contentId);

    /// <summary>Restores the complete answer: its text, its reasoning and the arguments of its tool calls.</summary>
    /// <param name="request">The request as it was sent (what <see cref="PrepareRequestAsync"/> returned).</param>
    /// <param name="response">The answer of the provider.</param>
    /// <returns>The answer the session keeps.</returns>
    /// <exception cref="AgentProviderBoundaryException">The answer cannot be restored safely.</exception>
    AgentTurnResponse RestoreResponse(AgentTurnRequest request, AgentTurnResponse response);
}

/// <summary>
/// Restores one text that comes in parts: the end of a part that could begin a token is held back until the next part,
/// or the end of the text, says what it is. It is used by one writer at a time.
/// </summary>
public interface IAgentBoundaryTextStream
{
    /// <summary>Takes the next part of the text.</summary>
    /// <param name="part">The part, as the provider sent it.</param>
    /// <returns>What can be shown now, restored; empty when all of it is held back.</returns>
    string Write(string part);

    /// <summary>Ends the text: what was held back is given, restored.</summary>
    /// <returns>The end of the text; empty when nothing was held back.</returns>
    string Flush();
}

/// <summary>
/// Gives the boundary the requests of one session pass through. The host decides with it whether the session is
/// confidential, at each request: a session can become confidential while it runs.
/// </summary>
public interface IAgentProviderBoundarySource
{
    /// <summary>
    /// Gets whether the session is known to be confidential: its requests then never carry a document as it is, and a PDF
    /// attached to a prompt goes as the text it was converted to on this machine, which the boundary pseudonymizes.
    /// </summary>
    bool IsConfidential { get; }

    /// <summary>Gets the boundary of a request.</summary>
    /// <param name="request">The request the session is about to send.</param>
    /// <param name="cancellationToken">Stops the resolution.</param>
    /// <returns>The boundary; <see langword="null"/> when the session is not confidential and the request goes as it is.</returns>
    /// <exception cref="AgentProviderBoundaryException">The session is confidential and no boundary is ready: nothing is sent.</exception>
    ValueTask<IAgentProviderBoundary?> GetBoundaryAsync(AgentTurnRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when a request of a confidential session cannot go to its provider safely: nothing of it was sent.
/// </summary>
public sealed class AgentProviderBoundaryException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AgentProviderBoundaryException"/> class.</summary>
    public AgentProviderBoundaryException()
        : base("The request of a confidential session could not be made safe: nothing was sent.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AgentProviderBoundaryException"/> class.</summary>
    /// <param name="message">What failed, for the user; it must not hold a value the boundary protects.</param>
    public AgentProviderBoundaryException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AgentProviderBoundaryException"/> class.</summary>
    /// <param name="message">What failed, for the user; it must not hold a value the boundary protects.</param>
    /// <param name="innerException">The failure that caused it.</param>
    public AgentProviderBoundaryException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
