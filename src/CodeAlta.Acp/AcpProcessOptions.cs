namespace CodeAlta.Acp;

/// <summary>
/// Describes how an ACP agent process is started.
/// </summary>
public sealed class AcpProcessOptions
{
    /// <summary>
    /// Gets the executable file name.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Gets the command-line arguments.
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>
    /// Gets the working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the environment variables set for the process, over the inherited ones.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// Gets the names of inherited environment variables removed from the process.
    /// </summary>
    public IReadOnlyList<string>? RemovedEnvironmentVariables { get; init; }
}
