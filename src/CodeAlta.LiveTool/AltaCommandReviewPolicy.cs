namespace CodeAlta.LiveTool;

/// <summary>
/// What a host lets its sessions do with the commands nobody reviews: start one in the background
/// (<c>alta job start</c>), type in a terminal, give an automation a command to run, or build and start a plugin,
/// which runs its code. A host that registers none lets them.
/// </summary>
/// <param name="AcceptsCommands">
/// Whether a session may do any of it. A host that has the user review the commands of every session does not let
/// them.
/// </param>
public sealed record AltaCommandReviewPolicy(bool AcceptsCommands)
{
    /// <summary>
    /// Gets or initializes what decides for one caller, in a host whose sessions do not all have the same policy:
    /// it is given the identifier of the session that acts, or null when no session does (a client of the host),
    /// and answers whether that caller runs commands without a review. Null, the default, leaves
    /// <see cref="AcceptsCommands"/> to decide for every caller.
    /// </summary>
    public Func<string?, bool>? AcceptsCommandsOf { get; init; }

    /// <summary>Gets whether a caller may do what runs a command nobody reviews.</summary>
    /// <param name="sessionId">The session that acts, or null when no session does.</param>
    /// <returns>False when the commands of that caller are reviewed.</returns>
    public bool Accepts(string? sessionId)
        => AcceptsCommands && AcceptsCommandsOf?.Invoke(sessionId) != false;
}
