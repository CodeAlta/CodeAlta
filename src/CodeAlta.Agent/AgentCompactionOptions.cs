namespace CodeAlta.Agent;

/// <summary>
/// What a manual compaction is asked to do besides compacting the conversation.
/// </summary>
public sealed record AgentCompactionOptions
{
    /// <summary>Gets the options of a compaction that is given nothing more.</summary>
    public static AgentCompactionOptions Default { get; } = new();

    /// <summary>
    /// Gets instructions for the summary of this compaction (what it must keep or how to write it), added to the
    /// request that writes the summary, or given to a provider that compacts the context it keeps;
    /// <see langword="null"/> for none.
    /// </summary>
    public string? Instructions { get; init; }
}
