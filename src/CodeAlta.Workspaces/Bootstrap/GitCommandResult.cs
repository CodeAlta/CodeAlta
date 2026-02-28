namespace CodeAlta.Workspaces.Bootstrap;

/// <summary>
/// Represents the result of a <c>git</c> command execution.
/// </summary>
public sealed record GitCommandResult
{
    /// <summary>
    /// Gets the command line executed.
    /// </summary>
    public required string CommandLine { get; init; }

    /// <summary>
    /// Gets the working directory used for execution.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the process exit code.
    /// </summary>
    public required int ExitCode { get; init; }

    /// <summary>
    /// Gets captured standard output.
    /// </summary>
    public required string StandardOutput { get; init; }

    /// <summary>
    /// Gets captured standard error.
    /// </summary>
    public required string StandardError { get; init; }

    /// <summary>
    /// Gets whether the command succeeded.
    /// </summary>
    public bool Success => ExitCode == 0;

    /// <summary>
    /// Gets combined output.
    /// </summary>
    public string CombinedOutput =>
        string.Join("\n", new[] { StandardOutput, StandardError }.Where(static x => !string.IsNullOrWhiteSpace(x)));
}

