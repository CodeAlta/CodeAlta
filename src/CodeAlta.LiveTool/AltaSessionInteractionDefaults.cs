using CodeAlta.Agent;

namespace CodeAlta.LiveTool;

/// <summary>
/// How the sessions that <c>alta session</c> commands create or drive answer permission and user-input
/// requests. A host registers one when its own commands must be able to drive those sessions afterwards:
/// the runtime only lets the owner of a session's defaults keep using its attachment.
/// </summary>
/// <remarks>Without this service such sessions allow every permission once and answer user input with nothing.</remarks>
public interface IAltaSessionInteractionDefaults
{
    /// <summary>Gets the permission handler of sessions created or driven by alta commands.</summary>
    AgentPermissionRequestHandler OnPermissionRequest { get; }

    /// <summary>Gets the user-input handler of sessions created or driven by alta commands.</summary>
    AgentUserInputRequestHandler OnUserInputRequest { get; }
}

/// <summary>Fixed handlers for <see cref="IAltaSessionInteractionDefaults"/>.</summary>
/// <param name="OnPermissionRequest">The permission handler.</param>
/// <param name="OnUserInputRequest">The user-input handler.</param>
public sealed record AltaSessionInteractionDefaults(AgentPermissionRequestHandler OnPermissionRequest, AgentUserInputRequestHandler OnUserInputRequest)
    : IAltaSessionInteractionDefaults;
