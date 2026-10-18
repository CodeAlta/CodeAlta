namespace CodeAlta.Agent;

/// <summary>
/// Represents user input composed of one or more input items.
/// </summary>
/// <param name="Items">The input items.</param>
public sealed record AgentInput(IReadOnlyList<AgentInputItem> Items)
{
    /// <summary>
    /// The identity the client that sent the prompt gave it, recorded with the message (absent when there is none). The
    /// recorded text may differ from what was typed (a project reference becomes a link, attached files add lines): the
    /// client recognizes its prompt by this identity.
    /// </summary>
    public string? ClientRequestId { get; init; }

    /// <summary>
    /// Creates a text-only input.
    /// </summary>
    /// <param name="text">The text to send.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static AgentInput Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new AgentInput([new AgentInputItem.Text(text)]);
    }
}

