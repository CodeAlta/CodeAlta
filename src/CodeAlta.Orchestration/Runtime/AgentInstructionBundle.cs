using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Represents optional instruction overrides used when starting an agent session.
/// </summary>
public sealed record AgentInstructionBundle
{
    /// <summary>
    /// An empty bundle that leaves provider defaults untouched.
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

    /// <summary>
    /// Gets what the instructions say of this session alone (where it works, its parent, its scratchpad), sent after the
    /// system message and the developer instructions, which are then the same for the sessions of a prompt.
    /// </summary>
    public string? SessionInstructions { get; init; }

    /// <summary>
    /// Gets the file-backed prompt bundle used to produce the instruction text, when available.
    /// </summary>
    public SystemPromptBundle? PromptBundle { get; init; }
}
