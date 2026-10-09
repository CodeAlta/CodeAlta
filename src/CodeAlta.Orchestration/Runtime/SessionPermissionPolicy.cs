namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// What the host does with the permission requests of a session it owns: the commands it runs and the files it
/// changes. It follows from the permission mode of the session.
/// </summary>
public enum SessionPermissionPolicy
{
    /// <summary>Every request waits for the user.</summary>
    Review,

    /// <summary>File changes are approved; commands wait for the user.</summary>
    AcceptEdits,

    /// <summary>Every request is approved.</summary>
    Approve,
}

/// <summary>
/// The permission modes the host itself gives a session, whatever its provider: the host answers the requests of
/// the session, so they need nothing from the provider. Their identifiers are those of the Claude Code CLI, which
/// has these modes and more.
/// </summary>
public static class SessionPermissionModes
{
    /// <summary>The mode in which commands and file changes wait for the user.</summary>
    public const string Ask = "default";

    /// <summary>The mode in which file changes are approved and commands wait for the user.</summary>
    public const string AcceptEdits = "acceptEdits";

    /// <summary>The mode in which nothing waits for the user.</summary>
    public const string Bypass = "bypassPermissions";

    /// <summary>
    /// Gets the modes a session of a provider without modes of its own can be given.
    /// </summary>
    public static IReadOnlyList<string> HostModes { get; } = [Ask, AcceptEdits, Bypass];

    /// <summary>
    /// Gets the policy of a permission mode.
    /// </summary>
    /// <param name="permissionMode">A mode, or null for none.</param>
    /// <param name="reviewByDefault">What no mode means: whether the requests are reviewed.</param>
    /// <returns>
    /// <see cref="SessionPermissionPolicy.Approve"/> for <see cref="Bypass"/>, <see cref="SessionPermissionPolicy.AcceptEdits"/>
    /// for <see cref="AcceptEdits"/>, and <see cref="SessionPermissionPolicy.Review"/> for any other mode: a mode
    /// of a provider that decides more by itself still has what it asks reviewed.
    /// </returns>
    public static SessionPermissionPolicy Policy(string? permissionMode, bool reviewByDefault)
        => string.IsNullOrWhiteSpace(permissionMode)
            ? reviewByDefault ? SessionPermissionPolicy.Review : SessionPermissionPolicy.Approve
            : permissionMode.Trim() switch
            {
                Bypass => SessionPermissionPolicy.Approve,
                AcceptEdits => SessionPermissionPolicy.AcceptEdits,
                _ => SessionPermissionPolicy.Review,
            };

    /// <summary>
    /// Gets the mode that names what no mode means.
    /// </summary>
    /// <param name="reviewByDefault">Whether the requests are reviewed when a session has no mode.</param>
    /// <returns><see cref="Ask"/> or <see cref="Bypass"/>.</returns>
    public static string Default(bool reviewByDefault) => reviewByDefault ? Ask : Bypass;
}
