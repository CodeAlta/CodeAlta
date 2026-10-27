namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The text a provider sends as the instructions of a turn: the system message, the developer instructions, then the
/// instructions of the session alone (<see cref="AgentTurnRequest.SessionInstructions"/>), last, so that the text before
/// them is the same for every session of a prompt and the prompt cache of the provider serves them all.
/// </summary>
public static class AgentInstructionText
{
    /// <summary>The tag that opens the instructions of the session.</summary>
    public const string SessionOpening = "<session_context>";

    /// <summary>The tag that closes the instructions of the session.</summary>
    public const string SessionClosing = "</session_context>";

    /// <summary>Composes the instructions of a provider that sends them as one text.</summary>
    /// <param name="request">The request of the turn.</param>
    /// <returns>The system message, the developer instructions in <c>&lt;developer_instructions&gt;</c>, then the session part; null when all are empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static string? Compose(AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Join(ComposeShared(request), Session(request.SessionInstructions));
    }

    /// <summary>
    /// Composes what <see cref="Compose"/> sends before the instructions of the session: the same text for every session of
    /// a prompt.
    /// </summary>
    /// <param name="request">The request of the turn.</param>
    /// <returns>The system message and the developer instructions in <c>&lt;developer_instructions&gt;</c>; null when both are empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static string? ComposeShared(AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var system = Normalize(request.SystemMessage);
        var developer = Normalize(request.DeveloperInstructions);
        if (developer is null) return system;
        return system is null ? developer : system + "\n\n<developer_instructions>\n" + developer + "\n</developer_instructions>";
    }

    /// <summary>
    /// The developer instructions followed by the instructions of the session, as paragraphs of one text, for a provider
    /// that sends them apart from the system message.
    /// </summary>
    /// <param name="request">The request of the turn.</param>
    /// <returns>The text; null when both are empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static string? DeveloperAndSession(AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Join(Normalize(request.DeveloperInstructions), Normalize(request.SessionInstructions));
    }

    /// <summary>The instructions of a session in their tags, as <see cref="Compose"/> ends with them.</summary>
    /// <param name="sessionInstructions">The instructions of the session.</param>
    /// <returns>The tagged text; null when they are empty.</returns>
    public static string? Session(string? sessionInstructions)
        => Normalize(sessionInstructions) is { } session ? SessionOpening + "\n" + session + "\n" + SessionClosing : null;

    private static string? Join(string? first, string? second)
        => first is null ? second : second is null ? first : first + "\n\n" + second;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
