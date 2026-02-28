using System.ComponentModel;
using System.Text;
using CodeAlta.Persistence;
using ModelContextProtocol.Server;

namespace CodeAlta.Mcp.Tools;

/// <summary>
/// MCP tools for durable task and plan operations.
/// </summary>
[McpServerToolType]
public sealed class TasksTools
{
    private readonly TaskRepository _taskRepository;
    private readonly ArtifactStore _artifactStore;
    private readonly ArtifactRepository _artifactRepository;
    private readonly CodeAltaMcpOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="TasksTools"/> class.
    /// </summary>
    /// <param name="taskRepository">Task repository.</param>
    /// <param name="artifactStore">Artifact store.</param>
    /// <param name="artifactRepository">Artifact repository.</param>
    /// <param name="options">MCP options.</param>
    /// <exception cref="ArgumentNullException">Thrown when required dependencies are <see langword="null"/>.</exception>
    public TasksTools(
        TaskRepository taskRepository,
        ArtifactStore artifactStore,
        ArtifactRepository artifactRepository,
        CodeAltaMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(taskRepository);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(artifactRepository);
        ArgumentNullException.ThrowIfNull(options);

        _taskRepository = taskRepository;
        _artifactStore = artifactStore;
        _artifactRepository = artifactRepository;
        _options = options;
    }

    /// <summary>
    /// Creates a durable task.
    /// </summary>
    [McpServerTool(Name = "codealta.tasks.create"), Description("Creates a durable task.")]
    public async Task<string> CreateAsync(
        [Description("Task title.")] string title,
        [Description("Optional task description stored as a note event.")] string? description = null,
        [Description("Optional workspace identifier.")] string? workspaceId = null,
        [Description("Optional project identifier.")] string? projectId = null,
        [Description("Optional parent task identifier.")] string? parentTaskId = null,
        [Description("Optional assigned agent identifier.")] string? assignedAgentId = null,
        CancellationToken cancellationToken = default)
    {
        var created = await _taskRepository.CreateAsync(
            new CreateTaskRequest
            {
                Title = title,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                ParentTaskId = parentTaskId,
                AssignedAgentId = assignedAgentId,
            },
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(description))
        {
            await _taskRepository.AddNoteAsync(created.TaskId, description, cancellationToken).ConfigureAwait(false);
        }

        return McpToolJson.Serialize(ToContract(created));
    }

    /// <summary>
    /// Updates a durable task.
    /// </summary>
    [McpServerTool(Name = "codealta.tasks.update"), Description("Updates a durable task.")]
    public async Task<string> UpdateAsync(
        [Description("Task identifier.")] string taskId,
        [Description("Optional replacement title.")] string? title = null,
        [Description("Optional replacement status (pending|in_progress|completed|blocked|cancelled).")] string? status = null,
        [Description("Optional replacement assigned agent identifier.")] string? assignedAgentId = null,
        CancellationToken cancellationToken = default)
    {
        var updated = await _taskRepository.UpdateAsync(
            new UpdateTaskRequest
            {
                TaskId = TaskId.Parse(taskId),
                Title = title,
                Status = ParseStatus(status),
                AssignedAgentId = assignedAgentId,
            },
            cancellationToken).ConfigureAwait(false);

        if (updated is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' was not found.");
        }

        return McpToolJson.Serialize(ToContract(updated));
    }

    /// <summary>
    /// Gets a task by id.
    /// </summary>
    [McpServerTool(Name = "codealta.tasks.get"), Description("Gets a task by identifier.")]
    public async Task<string> GetAsync(
        [Description("Task identifier.")] string taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetAsync(TaskId.Parse(taskId), cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' was not found.");
        }

        var events = await _taskRepository.ListEventsAsync(task.TaskId, cancellationToken).ConfigureAwait(false);
        return McpToolJson.Serialize(
            new
            {
                task = ToContract(task),
                events = events.Select(static x => new
                {
                    eventId = x.EventId,
                    taskId = x.TaskId.ToString(),
                    kind = x.Kind,
                    payloadJson = x.PayloadJson,
                    createdAt = x.CreatedAt,
                }).ToArray(),
            });
    }

    /// <summary>
    /// Lists tasks by optional scope filters.
    /// </summary>
    [McpServerTool(Name = "codealta.tasks.list"), Description("Lists tasks by optional scope filters.")]
    public async Task<string> ListAsync(
        [Description("Optional workspace identifier filter.")] string? workspaceId = null,
        [Description("Optional project identifier filter.")] string? projectId = null,
        [Description("Optional cursor for pagination.")] string? cursor = null,
        [Description("Maximum number of tasks.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var page = await _taskRepository.ListPageAsync(
            workspaceId,
            projectId,
            limit,
            cursor,
            cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(
            new
            {
                items = page.Tasks.Select(ToContract).ToArray(),
                nextCursor = page.NextCursor,
            });
    }

    /// <summary>
    /// Adds a note to a task and persists a markdown artifact for compaction-safe recovery.
    /// </summary>
    [McpServerTool(Name = "codealta.tasks.add_note"), Description("Adds a note to a task and persists it as a markdown artifact.")]
    public async Task<string> AddNoteAsync(
        [Description("Task identifier.")] string taskId,
        [Description("Markdown note content.")] string note,
        [Description("Optional workspace identifier for artifact scoping.")] string? workspaceId = null,
        [Description("Optional project identifier for artifact scoping.")] string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var parsedTaskId = TaskId.Parse(taskId);
        await _taskRepository.AddNoteAsync(parsedTaskId, note, cancellationToken).ConfigureAwait(false);

        var artifact = await WriteTaskNoteArtifactAsync(
            parsedTaskId,
            note,
            workspaceId,
            projectId,
            cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(
            new
            {
                taskId,
                artifactId = artifact.ArtifactId.ToString(),
                artifactUri = artifact.Uri,
                artifactPath = artifact.Path,
            });
    }

    /// <summary>
    /// Exports a task snapshot as human-readable markdown.
    /// </summary>
    [McpServerTool(Name = "codealta.tasks.export_markdown"), Description("Exports a task snapshot to markdown.")]
    public async Task<string> ExportMarkdownAsync(
        [Description("Task identifier.")] string taskId,
        [Description("Optional explicit destination file path.")] string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        var parsedTaskId = TaskId.Parse(taskId);
        var task = await _taskRepository.GetAsync(parsedTaskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' was not found.");
        }

        var events = await _taskRepository.ListEventsAsync(parsedTaskId, cancellationToken).ConfigureAwait(false);
        var path = destinationPath ?? Path.Combine(
            _options.ArtifactRoot,
            "task-exports",
            $"{parsedTaskId}.md");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var markdown = BuildTaskMarkdown(task, events);
        await File.WriteAllTextAsync(path, markdown, cancellationToken).ConfigureAwait(false);

        return McpToolJson.Serialize(
            new
            {
                taskId,
                path,
                eventCount = events.Count,
            });
    }

    private async Task<ArtifactRecord> WriteTaskNoteArtifactAsync(
        TaskId taskId,
        string note,
        string? workspaceId,
        string? projectId,
        CancellationToken cancellationToken)
    {
        var artifactId = ArtifactId.NewVersion7();
        var timestamp = DateTimeOffset.UtcNow;
        var relativePath = Path.Combine(
            workspaceId ?? "global",
            "tasks",
            taskId.ToString(),
            "notes",
            $"{timestamp:yyyyMMddTHHmmssfffZ}_{artifactId}.md");
        var artifactPath = Path.Combine(_options.ArtifactRoot, relativePath);

        var document = new ArtifactDocument
        {
            Frontmatter = new ArtifactFrontmatter
            {
                Id = artifactId.ToString(),
                Type = "task.note",
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Title = $"Task note {taskId}",
                Tags = ["task", "note"],
                Links = new ArtifactLinks
                {
                    Tasks = [taskId.ToString()],
                },
            },
            Body = note,
        };

        var writtenPath = await _artifactStore.WriteMarkdownAsync(
            artifactPath,
            document,
            cancellationToken).ConfigureAwait(false);

        var record = new ArtifactRecord
        {
            ArtifactId = artifactId,
            Uri = $"artifact://task/{taskId}/note/{artifactId}",
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Type = "task.note",
            Path = writtenPath,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };

        await _artifactRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        await _artifactRepository.AddLinkAsync(
            new ArtifactLinkRecord
            {
                FromArtifactId = artifactId,
                ToKind = "task",
                ToId = taskId.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return record;
    }

    private static string BuildTaskMarkdown(TaskRecord task, IReadOnlyList<TaskEventRecord> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Task {task.TaskId}");
        builder.AppendLine();
        builder.AppendLine($"- Title: {task.Title}");
        builder.AppendLine($"- Status: {ToStatusString(task.Status)}");
        builder.AppendLine($"- WorkspaceId: {task.WorkspaceId ?? "(none)"}");
        builder.AppendLine($"- ProjectId: {task.ProjectId ?? "(none)"}");
        builder.AppendLine($"- AssignedAgentId: {task.AssignedAgentId ?? "(none)"}");
        builder.AppendLine($"- CreatedAt: {task.CreatedAt:O}");
        builder.AppendLine($"- UpdatedAt: {task.UpdatedAt:O}");
        builder.AppendLine();
        builder.AppendLine("## Events");
        builder.AppendLine();

        foreach (var taskEvent in events)
        {
            builder.AppendLine($"- `{taskEvent.CreatedAt:O}` `{taskEvent.Kind}`");
            if (!string.IsNullOrWhiteSpace(taskEvent.PayloadJson))
            {
                builder.AppendLine();
                builder.AppendLine("```json");
                builder.AppendLine(taskEvent.PayloadJson);
                builder.AppendLine("```");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static object ToContract(TaskRecord task)
    {
        return new
        {
            taskId = task.TaskId.ToString(),
            workspaceId = task.WorkspaceId,
            projectId = task.ProjectId,
            parentTaskId = task.ParentTaskId,
            title = task.Title,
            status = ToStatusString(task.Status),
            assignedAgentId = task.AssignedAgentId,
            createdAt = task.CreatedAt,
            updatedAt = task.UpdatedAt,
        };
    }

    private static CodeAlta.Persistence.TaskStatus? ParseStatus(string? status)
    {
        if (status is null)
        {
            return null;
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "pending" => CodeAlta.Persistence.TaskStatus.Pending,
            "in_progress" => CodeAlta.Persistence.TaskStatus.InProgress,
            "completed" => CodeAlta.Persistence.TaskStatus.Completed,
            "blocked" => CodeAlta.Persistence.TaskStatus.Blocked,
            "cancelled" => CodeAlta.Persistence.TaskStatus.Cancelled,
            _ => throw new ArgumentException(
                "Status must be one of pending, in_progress, completed, blocked, cancelled.",
                nameof(status)),
        };
    }

    private static string ToStatusString(CodeAlta.Persistence.TaskStatus status)
    {
        return status switch
        {
            CodeAlta.Persistence.TaskStatus.Pending => "pending",
            CodeAlta.Persistence.TaskStatus.InProgress => "in_progress",
            CodeAlta.Persistence.TaskStatus.Completed => "completed",
            CodeAlta.Persistence.TaskStatus.Blocked => "blocked",
            CodeAlta.Persistence.TaskStatus.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
    }
}
