namespace CodeAlta.LiveTool;

/// <summary>
/// Says which sessions are sandboxed, for a host that does not give the commands the runtime of its sessions. Where
/// neither is given, no session is sandboxed. A host with a runtime needs none: the commands ask it.
/// </summary>
/// <param name="Of">Gives the sandbox of a session, or null when it is not sandboxed.</param>
public sealed record AltaSandboxedSessions(Func<string, CancellationToken, Task<AltaSessionSandbox?>> Of);

/// <summary>The sandbox of a session, as the commands of <c>alta</c> see it.</summary>
/// <param name="Profile">The name of its profile, or of the profile that could not be used.</param>
/// <param name="McpServers">The MCP servers it may use.</param>
public sealed record AltaSessionSandbox(string Profile, IReadOnlyList<string> McpServers);
