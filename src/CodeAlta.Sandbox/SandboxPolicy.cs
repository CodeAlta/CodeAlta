namespace CodeAlta.Sandbox;

/// <summary>The folders a session works in, as the runtime knows them.</summary>
public sealed record SandboxSessionFolders
{
    /// <summary>Gets the working directory of the session: its project folder or its worktree.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Gets more folders the session writes in (other roots of the session).</summary>
    public IReadOnlyList<string> Writable { get; init; } = [];

    /// <summary>
    /// Gets the repositories a worktree of the session may belong to, as CodeAlta knows them (its project folders): the
    /// git folder a worktree names is written only when it is the one of such a repository, since the <c>.git</c> file
    /// that names it is in the writable working tree.
    /// </summary>
    public IReadOnlyList<string> Repositories { get; init; } = [];

    /// <summary>Gets more files and folders that stay read-only, or are not created, inside what the session writes.</summary>
    public IReadOnlyList<string> Protected { get; init; } = [];

    /// <summary>Gets the scratchpad folder of the session, or null.</summary>
    public string? Scratchpad { get; init; }

    /// <summary>
    /// Gets the name of a home folder of its own, kept between sessions and profiles: the one of an agent that signs in
    /// there once and that no other process of a sandbox reaches; null for the home folder of the profile.
    /// </summary>
    public string? OwnHome { get; init; }
}

/// <summary>What the computer gives a sandbox: where its data goes, the home folder, the environment and the tools.</summary>
public sealed record SandboxHost
{
    /// <summary>Gets the folder where the sandboxes keep their homes, temporary folders and caches.</summary>
    public required string DataRoot { get; init; }

    /// <summary>Gets the home folder of the user.</summary>
    public required string HomeDirectory { get; init; }

    /// <summary>Gets the environment of the application: where tools are found and what a profile may pass on.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>Gets the folders of the developer tools: those of <c>PATH</c> and of the variables tools are found with.</summary>
    public IReadOnlyList<string> ToolFolders { get; init; } = [];

    /// <summary>Gets the folders of the programs the sandbox runs itself: CodeAlta's install folder, the .NET runtime.</summary>
    public IReadOnlyList<string> ProgramFolders { get; init; } = [];

    /// <summary>Gets more folders that hold secrets of CodeAlta (its global and state roots when they are not <c>~/.alta</c>).</summary>
    public IReadOnlyList<string> SecretFolders { get; init; } = [];
}

/// <summary>
/// Everything the sandbox of one session allows, resolved: the worker of the session is started with it.
/// </summary>
/// <remarks>
/// Paths are full. <see cref="Protected"/> lists entries inside the writable folders that other programs run later
/// (git hooks and configuration, the settings of CodeAlta and of coding agents): the backend makes those that exist
/// read-only and denies those that do not, so that they cannot be created.
/// </remarks>
public sealed record SandboxPolicy
{
    /// <summary>Gets the name of the profile.</summary>
    public required string ProfileName { get; init; }

    /// <summary>Gets the working directory of the worker.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Gets the folders read and written.</summary>
    public required IReadOnlyList<string> ReadWrite { get; init; }

    /// <summary>Gets the folders and files read only.</summary>
    public required IReadOnlyList<string> ReadOnly { get; init; }

    /// <summary>Gets the folders and files never reached, also inside the others.</summary>
    public required IReadOnlyList<string> Denied { get; init; }

    /// <summary>Gets the entries inside the writable folders that are read-only, or denied while they do not exist.</summary>
    public required IReadOnlyList<string> Protected { get; init; }

    /// <summary>Gets what the network reaches.</summary>
    public required SandboxNetwork Network { get; init; }

    /// <summary>Gets the hosts of <see cref="SandboxNetwork.Hosts"/>.</summary>
    public required IReadOnlyList<string> Hosts { get; init; }

    /// <summary>Gets the ports of this computer reached through the proxy.</summary>
    public required IReadOnlyList<int> LocalPorts { get; init; }

    /// <summary>Gets the whole environment of the worker and of the programs it starts.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>Gets the private temporary folder of the session.</summary>
    public required string Temp { get; init; }

    /// <summary>Gets the home folder the sandbox's programs see.</summary>
    public required string Home { get; init; }

    /// <summary>Gets the folder where the sandboxes keep their homes, temporary folders and caches, or null.</summary>
    public string? DataRoot { get; init; }

    /// <summary>Gets the MCP servers the session may use.</summary>
    public required IReadOnlyList<string> McpServers { get; init; }

    /// <summary>Gets whether a command may be asked to run outside the sandbox.</summary>
    public required SandboxOutside Outside { get; init; }

    /// <summary>
    /// Gets whether the network goes through a proxy of CodeAlta: the hosts of <see cref="SandboxNetwork.Hosts"/>, and
    /// on macOS and Linux every host of <see cref="SandboxNetwork.On"/>, where the container does not keep the local
    /// network out by itself (Seatbelt filters no address but this computer, and the rules of Bubblewrap leave no route).
    /// </summary>
    public bool ThroughProxy => Network == SandboxNetwork.Hosts || (Network == SandboxNetwork.On && !OperatingSystem.IsWindows());

    /// <summary>Gets the host patterns the proxy lets through: null for every host of the internet.</summary>
    public IReadOnlyList<string>? ProxyHosts => Network == SandboxNetwork.On ? null : Hosts;
}
