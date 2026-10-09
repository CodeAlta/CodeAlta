using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Options used when creating or reusing a session coordinator session.
/// </summary>
public sealed class SessionExecutionOptions
{
    /// <summary>
    /// Gets or initializes the model provider identifier.
    /// </summary>
    public ModelProviderId ProviderId { get; init; }

    /// <summary>
    /// Gets or initializes the provider key that should be selected within the provider runtime.
    /// </summary>
    public string? ProviderKey { get; init; }

    /// <summary>
    /// Gets or initializes the working directory for the session.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or initializes active project roots for project-local agent overlays.
    /// </summary>
    public IReadOnlyList<string> ProjectRoots { get; init; } = [];

    /// <summary>
    /// Gets or initializes the preferred model.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or initializes the preferred reasoning effort.
    /// </summary>
    public AgentReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    /// Gets or initializes the permission mode a session is created with, one of the modes of its provider or of
    /// the host. Null, the default, gives it none. A session that exists keeps the mode saved with it.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Gets or initializes the selected agent prompt identifier.
    /// </summary>
    public string? AgentPromptId { get; init; }

    /// <summary>
    /// Gets or initializes custom tools available to the coordinator session.
    /// </summary>
    public IReadOnlyList<AgentToolDefinition>? Tools { get; init; }

    /// <summary>
    /// Gets or initializes host/plugin-provided system prompt content appended to the coordinator profile system message.
    /// </summary>
    public string? AdditionalSystemMessage { get; init; }

    /// <summary>
    /// Gets or initializes host/plugin-provided developer instructions appended to coordinator developer instructions.
    /// </summary>
    public string? AdditionalDeveloperInstructions { get; init; }

    /// <summary>
    /// Gets or initializes preferred tool names for this run. Providers may use this as a hint when supported.
    /// </summary>
    public IReadOnlyList<string> PreferredToolNames { get; init; } = [];

    /// <summary>
    /// Gets or initializes the optional trusted plugin hook that can transform final system/developer instructions before provider submission.
    /// </summary>
    public SessionInstructionProcessor? InstructionProcessor { get; init; }

    /// <summary>
    /// Gets or initializes the permission request handler.
    /// </summary>
    public required AgentPermissionRequestHandler OnPermissionRequest { get; init; }

    /// <summary>
    /// Gets or initializes the optional user-input request handler.
    /// </summary>
    public AgentUserInputRequestHandler? OnUserInputRequest { get; init; }
}

/// <summary>
/// Processes final composed system/developer instructions for a session.
/// </summary>
/// <param name="request">The instruction processing request.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The processed instruction result.</returns>
public delegate ValueTask<SessionInstructionProcessingResult> SessionInstructionProcessor(
    SessionInstructionProcessingRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Describes final composed instructions passed to trusted plugin processors.
/// </summary>
public sealed record SessionInstructionProcessingRequest
{
    /// <summary>Gets the session identifier.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the project identifier.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the project path.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Gets the model provider identifier.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Gets the model identifier.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the system message.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>Gets the developer instructions.</summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>Gets active tool names.</summary>
    public IReadOnlyList<string> ActiveToolNames { get; init; } = [];

    /// <summary>Gets prompt manifest metadata, when available.</summary>
    public IReadOnlyDictionary<string, string> Manifest { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Describes final instruction processing output.
/// </summary>
public sealed record SessionInstructionProcessingResult
{
    /// <summary>Gets the final system message.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>Gets the final developer instructions.</summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>Gets a cancellation reason, when a plugin cancelled the run.</summary>
    public string? CancelReason { get; init; }

    /// <summary>Gets audit-safe transformation records.</summary>
    public IReadOnlyList<AgentInstructionTransformationInfo> Transformations { get; init; } = [];
}
