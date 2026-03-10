using System.Text.Json.Serialization;

namespace CodeAlta.Workspaces;

/// <summary>
/// Describes a durable work thread.
/// </summary>
public sealed class WorkThreadDescriptor
{
    /// <summary>
    /// Gets or sets the stable thread identifier.
    /// </summary>
    [JsonPropertyName("thread_id")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the thread kind.
    /// </summary>
    [JsonPropertyName("kind")]
    public WorkThreadKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the owning workspace identifier for workspace threads.
    /// </summary>
    [JsonPropertyName("workspace_ref")]
    public string? WorkspaceRef { get; set; }

    /// <summary>
    /// Gets or sets the active project identifiers.
    /// </summary>
    [JsonPropertyName("project_refs")]
    public List<string> ProjectRefs { get; set; } = [];

    /// <summary>
    /// Gets or sets the project scope mode.
    /// </summary>
    [JsonPropertyName("scope_mode")]
    public WorkThreadScopeMode ScopeMode { get; set; }

    /// <summary>
    /// Gets or sets the thread title.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the durable status.
    /// </summary>
    [JsonPropertyName("status")]
    public WorkThreadStatus Status { get; set; } = WorkThreadStatus.Draft;

    /// <summary>
    /// Gets or sets the creation time.
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the last update time.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the last active time.
    /// </summary>
    [JsonPropertyName("last_active_at")]
    public DateTimeOffset LastActiveAt { get; set; }

    /// <summary>
    /// Gets or sets the optional first prompt timestamp.
    /// </summary>
    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the latest durable summary.
    /// </summary>
    [JsonPropertyName("latest_summary")]
    public string? LatestSummary { get; set; }

    /// <summary>
    /// Gets the path to the source markdown file when loaded from disk.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the markdown body loaded from disk.
    /// </summary>
    public string? MarkdownBody { get; set; }

    /// <summary>
    /// Gets a value indicating whether the workspace is locked.
    /// </summary>
    public bool IsWorkspaceLocked => Kind == WorkThreadKind.WorkspaceThread && StartedAt is not null;

    /// <summary>
    /// Marks the thread as started and therefore workspace-locked.
    /// </summary>
    /// <param name="timestamp">The timestamp to record.</param>
    public void MarkStarted(DateTimeOffset timestamp)
    {
        if (StartedAt is null)
        {
            StartedAt = timestamp;
        }

        UpdatedAt = timestamp;
        LastActiveAt = timestamp;
        if (Status == WorkThreadStatus.Draft)
        {
            Status = WorkThreadStatus.Active;
        }
    }

    /// <summary>
    /// Validates the descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when required values are missing or invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ThreadId))
        {
            throw new ArgumentException("Thread id is required.", nameof(ThreadId));
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new ArgumentException("Thread title is required.", nameof(Title));
        }

        if (CreatedAt == default)
        {
            throw new ArgumentException("CreatedAt is required.", nameof(CreatedAt));
        }

        if (UpdatedAt == default)
        {
            throw new ArgumentException("UpdatedAt is required.", nameof(UpdatedAt));
        }

        if (LastActiveAt == default)
        {
            throw new ArgumentException("LastActiveAt is required.", nameof(LastActiveAt));
        }

        var duplicateRef = ProjectRefs
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .Where(static x => x.Count() > 1)
            .Select(static x => x.Key)
            .FirstOrDefault();

        if (duplicateRef is not null)
        {
            throw new ArgumentException($"Thread '{ThreadId}' contains duplicate project ref '{duplicateRef}'.", nameof(ProjectRefs));
        }

        if (Kind == WorkThreadKind.Global)
        {
            if (!string.IsNullOrWhiteSpace(WorkspaceRef))
            {
                throw new ArgumentException("Global threads cannot declare a workspace.", nameof(WorkspaceRef));
            }

            return;
        }

        if (!WorkspaceId.TryParse(WorkspaceRef, out _))
        {
            throw new ArgumentException("Workspace threads require a valid workspace_ref.", nameof(WorkspaceRef));
        }

        if (ScopeMode == WorkThreadScopeMode.SingleProject && ProjectRefs.Count != 1)
        {
            throw new ArgumentException("Single-project threads must declare exactly one project ref.", nameof(ProjectRefs));
        }

        if (ScopeMode == WorkThreadScopeMode.MultiProject && ProjectRefs.Count < 2)
        {
            throw new ArgumentException("Multi-project threads must declare at least two project refs.", nameof(ProjectRefs));
        }

        if (ScopeMode == WorkThreadScopeMode.AllProjects && ProjectRefs.Count > 0)
        {
            throw new ArgumentException("All-projects threads should not persist explicit project refs.", nameof(ProjectRefs));
        }
    }
}
