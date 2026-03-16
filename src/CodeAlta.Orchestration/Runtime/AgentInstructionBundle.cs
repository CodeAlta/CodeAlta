namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Represents optional instruction overrides used when starting an agent session.
/// </summary>
public sealed record AgentInstructionBundle
{
    /// <summary>
    /// An empty bundle that leaves backend defaults untouched.
    /// </summary>
    public static AgentInstructionBundle Empty { get; } = new();

    /// <summary>
    /// Gets the system message override.
    /// </summary>
    public string? SystemMessage { get; init; }

    /// <summary>
    /// Gets the developer instructions override.
    /// </summary>
    public string? DeveloperInstructions { get; init; }
}
