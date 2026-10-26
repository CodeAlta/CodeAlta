namespace CodeAlta.Agent.Runtime;

/// <summary>
/// How a provider that runs its own tools starts its process (the CLI of an agent), and what that process needs from
/// the computer, for a host that starts it elsewhere than as a child of this process, such as in a sandbox.
/// </summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">Its arguments, one per element.</param>
/// <param name="WorkingDirectory">The folder it works in.</param>
/// <param name="Environment">Variables set for the process; a <see langword="null" /> value removes one.</param>
public sealed record AgentProcessLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment)
{
    /// <summary>Gets the folders the process reads: the one of its executable, and its runtime.</summary>
    public IReadOnlyList<string> ReadOnlyFolders { get; init; } = [];

    /// <summary>Gets the folders the process writes besides the working directory: its account and its transcripts.</summary>
    public IReadOnlyList<string> ReadWriteFolders { get; init; } = [];

    /// <summary>
    /// Gets the files and folders inside <see cref="ReadWriteFolders"/> that stay read-only, and are not created when
    /// missing: what configures what the agent runs later, outside the sandbox too (its settings, hooks and plugins).
    /// </summary>
    public IReadOnlyList<string> ProtectedPaths { get; init; } = [];

    /// <summary>Gets the hosts the process reaches: the service of its vendor.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary>Gets the ports of this computer the process reaches, through the proxy of the sandbox: the MCP server of the tools of CodeAlta.</summary>
    public IReadOnlyList<int> LocalPorts { get; init; } = [];

    /// <summary>
    /// Gets the names of the variables the started process receives, such as the key of its vendor: with the value
    /// <see cref="Environment"/> gives one, else with the one of this process. A host that builds the environment from
    /// scratch gives it those, and <see cref="Environment"/>.
    /// </summary>
    public IReadOnlyList<string> PassedVariables { get; init; } = [];

    /// <summary>
    /// Gets the name of a home folder of its own the process has in a sandbox, kept between sessions, where an agent
    /// keeps its sign-in and its state; null for the home folder the sandbox gives every process of the session.
    /// </summary>
    public string? OwnHome { get; init; }
}

/// <summary>A process started by an <see cref="IAgentProcessHost"/>.</summary>
public interface IAgentHostedProcess : IAsyncDisposable
{
    /// <summary>Gets the process id, as the system knows it.</summary>
    int Id { get; }

    /// <summary>Gets what is written to the process.</summary>
    Stream StandardInput { get; }

    /// <summary>Gets what the process writes.</summary>
    Stream StandardOutput { get; }

    /// <summary>Gets what the process writes as errors.</summary>
    Stream StandardError { get; }

    /// <summary>Gets a task that ends with the exit code of the process.</summary>
    Task<int> Exited { get; }

    /// <summary>Ends the process and what it started.</summary>
    void Kill();
}

/// <summary>
/// Starts the process of a provider that runs its own tools elsewhere than as a child of this process: in the sandbox
/// of the session. A provider asks it for the process of a run when its turn request names one
/// (<see cref="AgentTurnRequest.ProcessHost"/>).
/// </summary>
public interface IAgentProcessHost
{
    /// <summary>Gets what tells two hosts apart: a process started by a host with another key is started again.</summary>
    string Key { get; }

    /// <summary>Starts a process.</summary>
    /// <param name="launch">What to start, and what it needs.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The process.</returns>
    /// <exception cref="InvalidOperationException">The process could not be started; the message says why.</exception>
    Task<IAgentHostedProcess> StartAsync(AgentProcessLaunch launch, CancellationToken cancellationToken);
}
