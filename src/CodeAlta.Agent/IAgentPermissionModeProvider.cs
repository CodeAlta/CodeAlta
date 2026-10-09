namespace CodeAlta.Agent;

/// <summary>
/// Optional capability of a session whose permission mode can change while it stays attached.
/// </summary>
public interface IAgentPermissionModeProvider
{
    /// <summary>Gets the permission mode of the session, or <see langword="null"/> for the one of its provider.</summary>
    string? PermissionMode { get; }

    /// <summary>
    /// Sets the permission mode the next turns of the session request (<see cref="Runtime.AgentTurnRequest.PermissionMode"/>).
    /// A run in progress keeps the mode it started with.
    /// </summary>
    /// <param name="permissionMode">One of the permission modes of the provider, or <see langword="null"/> for the one it is configured with.</param>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    void SetPermissionMode(string? permissionMode);
}
