namespace CodeAlta.LiveTool;

/// <summary>
/// Says which built-in tools a session does without, for a host that does not give the commands the runtime of its
/// sessions. Where neither is given, no session does without a tool. A host with a runtime needs none: the commands
/// read the settings of the user, of the space and of the agent prompt of the session through it.
/// </summary>
/// <param name="Of">Gives the names of the tools a session does without, such as <c>shell_command</c>.</param>
public sealed record AltaDisabledTools(Func<string, CancellationToken, Task<IReadOnlySet<string>>> Of);
