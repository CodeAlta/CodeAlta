namespace CodeAlta.Workspaces.Bootstrap;

/// <summary>
/// Represents global repository bootstrap outcome.
/// </summary>
public sealed record GlobalRepoBootstrapResult
{
    /// <summary>
    /// Gets the global repository root.
    /// </summary>
    public required string GlobalRepoRoot { get; init; }

    /// <summary>
    /// Gets whether the repo directory was created during bootstrap.
    /// </summary>
    public required bool CreatedDirectory { get; init; }

    /// <summary>
    /// Gets whether <c>git init</c> was executed during bootstrap.
    /// </summary>
    public required bool InitializedRepository { get; init; }

    /// <summary>
    /// Gets whether a clone was attempted during bootstrap.
    /// </summary>
    public required bool ClonedRepository { get; init; }

    /// <summary>
    /// Gets optional origin remote URL set during bootstrap.
    /// </summary>
    public string? OriginRemoteUrl { get; init; }
}

