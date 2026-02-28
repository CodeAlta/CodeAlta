namespace CodeAlta.Workspaces.Bootstrap;

/// <summary>
/// Represents a global repo sync outcome.
/// </summary>
public sealed record GlobalRepoSyncResult
{
    /// <summary>
    /// Gets whether any files were committed.
    /// </summary>
    public required bool CommittedChanges { get; init; }

    /// <summary>
    /// Gets whether a pull was executed successfully.
    /// </summary>
    public required bool Pulled { get; init; }

    /// <summary>
    /// Gets whether a push was executed successfully.
    /// </summary>
    public required bool Pushed { get; init; }
}

