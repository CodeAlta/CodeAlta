using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

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
    /// Gets or sets the backend identifier for the thread.
    /// </summary>
    [JsonPropertyName("backend_id")]
    public string BackendId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the backend-owned session identifier.
    /// </summary>
    [JsonPropertyName("backend_session_id")]
    public string BackendSessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the owning project identifier for project threads.
    /// </summary>
    [JsonPropertyName("project_ref")]
    public string? ProjectRef { get; set; }

    /// <summary>
    /// Gets or sets the parent thread identifier for delegated child work.
    /// </summary>
    [JsonPropertyName("parent_thread_id")]
    public string? ParentThreadId { get; set; }

    /// <summary>
    /// Gets or sets the session working directory.
    /// </summary>
    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;

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
    /// Gets a value indicating whether the thread can no longer change backends.
    /// </summary>
    public bool IsBackendLocked => StartedAt is not null;

    /// <summary>
    /// Marks the thread as started.
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

        if (string.IsNullOrWhiteSpace(BackendId))
        {
            throw new ArgumentException("Backend id is required.", nameof(BackendId));
        }

        if (string.IsNullOrWhiteSpace(BackendSessionId))
        {
            throw new ArgumentException("Backend session id is required.", nameof(BackendSessionId));
        }

        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            throw new ArgumentException("Working directory is required.", nameof(WorkingDirectory));
        }

        switch (Kind)
        {
            case WorkThreadKind.GlobalThread:
                if (!string.IsNullOrWhiteSpace(ProjectRef))
                {
                    throw new ArgumentException("Global threads cannot declare a project.", nameof(ProjectRef));
                }

                break;

            case WorkThreadKind.ProjectThread:
                if (!ProjectId.TryParse(ProjectRef, out _))
                {
                    throw new ArgumentException("Project threads require a valid project_ref.", nameof(ProjectRef));
                }

                break;

            case WorkThreadKind.InternalThread:
                break;
        }
    }
}

