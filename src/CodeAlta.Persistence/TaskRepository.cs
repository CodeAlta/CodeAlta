using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodeAlta.Persistence;

/// <summary>
/// Provides durable task CRUD operations.
/// </summary>
public sealed class TaskRepository
{
    private readonly CodeAltaDb _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskRepository"/> class.
    /// </summary>
    /// <param name="db">The database accessor.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="db"/> is <see langword="null"/>.</exception>
    public TaskRepository(CodeAltaDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// Creates a task and emits a <c>created</c> event.
    /// </summary>
    /// <param name="request">Task creation arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created task record.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when required fields are missing.</exception>
    public async Task<TaskRecord> CreateAsync(
        CreateTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Task title is required.", nameof(request));
        }

        var taskId = request.TaskId ?? TaskId.NewVersion7();
        var now = DateTimeOffset.UtcNow;

        return await _db.ExecuteWriteAsync<TaskRecord>(
            async (connection, ct) =>
            {
                await using var insertTask = connection.CreateCommand();
                insertTask.CommandText =
                    """
                    INSERT INTO tasks(
                        task_id,
                        workspace_id,
                        project_id,
                        parent_task_id,
                        title,
                        status,
                        assigned_agent_id,
                        created_at,
                        updated_at)
                    VALUES (
                        $task_id,
                        $workspace_id,
                        $project_id,
                        $parent_task_id,
                        $title,
                        $status,
                        $assigned_agent_id,
                        $created_at,
                        $updated_at);
                    """;
                insertTask.Parameters.AddWithValue("$task_id", taskId.ToString());
                insertTask.Parameters.AddWithValue("$workspace_id", (object?)request.WorkspaceId ?? DBNull.Value);
                insertTask.Parameters.AddWithValue("$project_id", (object?)request.ProjectId ?? DBNull.Value);
                insertTask.Parameters.AddWithValue("$parent_task_id", (object?)request.ParentTaskId ?? DBNull.Value);
                insertTask.Parameters.AddWithValue("$title", request.Title);
                insertTask.Parameters.AddWithValue("$status", StatusToString(request.Status));
                insertTask.Parameters.AddWithValue("$assigned_agent_id", (object?)request.AssignedAgentId ?? DBNull.Value);
                insertTask.Parameters.AddWithValue("$created_at", now.ToString("O"));
                insertTask.Parameters.AddWithValue("$updated_at", now.ToString("O"));
                await insertTask.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                await InsertTaskEventAsync(
                    connection,
                    taskId,
                    "created",
                    payloadJson: null,
                    now,
                    ct).ConfigureAwait(false);

                return new TaskRecord
                {
                    TaskId = taskId,
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    ParentTaskId = request.ParentTaskId,
                    Title = request.Title,
                    Status = request.Status,
                    AssignedAgentId = request.AssignedAgentId,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates a task record.
    /// </summary>
    /// <param name="request">Task update arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated task when found; otherwise <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    public async Task<TaskRecord?> UpdateAsync(
        UpdateTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _db.ExecuteWriteAsync(
            async (connection, ct) =>
            {
                var existing = await GetByIdInternalAsync(connection, request.TaskId, ct).ConfigureAwait(false);
                if (existing is null)
                {
                    return null;
                }

                var nextTitle = request.Title ?? existing.Title;
                var nextStatus = request.Status ?? existing.Status;
                var nextAssigned = request.AssignedAgentId ?? existing.AssignedAgentId;
                var now = DateTimeOffset.UtcNow;

                await using var update = connection.CreateCommand();
                update.CommandText =
                    """
                    UPDATE tasks
                    SET title = $title,
                        status = $status,
                        assigned_agent_id = $assigned_agent_id,
                        updated_at = $updated_at
                    WHERE task_id = $task_id;
                    """;
                update.Parameters.AddWithValue("$title", nextTitle);
                update.Parameters.AddWithValue("$status", StatusToString(nextStatus));
                update.Parameters.AddWithValue("$assigned_agent_id", (object?)nextAssigned ?? DBNull.Value);
                update.Parameters.AddWithValue("$updated_at", now.ToString("O"));
                update.Parameters.AddWithValue("$task_id", request.TaskId.ToString());
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                if (request.Status is not null && request.Status.Value != existing.Status)
                {
                    await InsertTaskEventAsync(
                        connection,
                        request.TaskId,
                        "status_changed",
                        payloadJson: $$"""{"from":"{{StatusToString(existing.Status)}}","to":"{{StatusToString(request.Status.Value)}}"}""",
                        now,
                        ct).ConfigureAwait(false);
                }

                return new TaskRecord
                {
                    TaskId = existing.TaskId,
                    WorkspaceId = existing.WorkspaceId,
                    ProjectId = existing.ProjectId,
                    ParentTaskId = existing.ParentTaskId,
                    Title = nextTitle,
                    Status = nextStatus,
                    AssignedAgentId = nextAssigned,
                    CreatedAt = existing.CreatedAt,
                    UpdatedAt = now,
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a task by id.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The task when found; otherwise <see langword="null"/>.</returns>
    public Task<TaskRecord?> GetAsync(TaskId taskId, CancellationToken cancellationToken = default)
    {
        return _db.ExecuteReadAsync((connection, ct) => GetByIdInternalAsync(connection, taskId, ct), cancellationToken);
    }

    /// <summary>
    /// Lists tasks in descending update order.
    /// </summary>
    /// <param name="workspaceId">Optional workspace filter.</param>
    /// <param name="projectId">Optional project filter.</param>
    /// <param name="limit">Result limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching tasks.</returns>
    public Task<IReadOnlyList<TaskRecord>> ListAsync(
        string? workspaceId = null,
        string? projectId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        }

        return _db.ExecuteReadAsync<IReadOnlyList<TaskRecord>>(
            async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT task_id, workspace_id, project_id, parent_task_id, title, status, assigned_agent_id, created_at, updated_at
                    FROM tasks
                    WHERE ($workspace_id IS NULL OR workspace_id = $workspace_id)
                      AND ($project_id IS NULL OR project_id = $project_id)
                    ORDER BY updated_at DESC, task_id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$workspace_id", (object?)workspaceId ?? DBNull.Value);
                command.Parameters.AddWithValue("$project_id", (object?)projectId ?? DBNull.Value);
                command.Parameters.AddWithValue("$limit", limit);

                var results = new List<TaskRecord>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    results.Add(ReadTask(reader));
                }

                return results;
            },
            cancellationToken);
    }

    /// <summary>
    /// Lists tasks using cursor-based pagination in descending update order.
    /// </summary>
    /// <param name="workspaceId">Optional workspace filter.</param>
    /// <param name="projectId">Optional project filter.</param>
    /// <param name="limit">Result limit.</param>
    /// <param name="cursor">Optional cursor returned by a previous page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of tasks plus an optional next cursor.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="limit"/> is not positive.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="cursor"/> is invalid.</exception>
    public Task<TaskListPage> ListPageAsync(
        string? workspaceId = null,
        string? projectId = null,
        int limit = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        }

        TaskListCursor? parsedCursor = null;
        if (!string.IsNullOrWhiteSpace(cursor) &&
            !TaskListCursor.TryParse(cursor, out parsedCursor))
        {
            throw new ArgumentException("Cursor is invalid.", nameof(cursor));
        }

        return _db.ExecuteReadAsync(
            async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT task_id, workspace_id, project_id, parent_task_id, title, status, assigned_agent_id, created_at, updated_at
                    FROM tasks
                    WHERE ($workspace_id IS NULL OR workspace_id = $workspace_id)
                      AND ($project_id IS NULL OR project_id = $project_id)
                      AND (
                        $cursor_updated_at IS NULL
                        OR updated_at < $cursor_updated_at
                        OR (updated_at = $cursor_updated_at AND task_id < $cursor_task_id)
                      )
                    ORDER BY updated_at DESC, task_id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$workspace_id", (object?)workspaceId ?? DBNull.Value);
                command.Parameters.AddWithValue("$project_id", (object?)projectId ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$cursor_updated_at",
                    parsedCursor is null ? DBNull.Value : parsedCursor.UpdatedAtText);
                command.Parameters.AddWithValue(
                    "$cursor_task_id",
                    parsedCursor is null ? DBNull.Value : parsedCursor.TaskIdText);
                command.Parameters.AddWithValue("$limit", limit);

                var results = new List<TaskRecord>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    results.Add(ReadTask(reader));
                }

                string? nextCursor = null;
                if (results.Count == limit)
                {
                    var last = results[^1];
                    nextCursor = TaskListCursor.FromTask(last).ToString();
                }

                return new TaskListPage(results, nextCursor);
            },
            cancellationToken);
    }

    /// <summary>
    /// Adds a free-form note event to a task.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    /// <param name="note">Note text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="note"/> is empty.</exception>
    public Task AddNoteAsync(
        TaskId taskId,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("Note is required.", nameof(note));
        }

        var payload = JsonSerializer.Serialize(new { note });
        return AddEventAsync(taskId, "note_added", payload, cancellationToken);
    }

    /// <summary>
    /// Adds a custom event to a task.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    /// <param name="kind">Event kind.</param>
    /// <param name="payloadJson">Optional payload JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="kind"/> is empty.</exception>
    public async Task AddEventAsync(
        TaskId taskId,
        string kind,
        string? payloadJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("Event kind is required.", nameof(kind));
        }

        await _db.ExecuteWriteAsync(
            async (connection, ct) =>
            {
                await InsertTaskEventAsync(connection, taskId, kind, payloadJson, DateTimeOffset.UtcNow, ct)
                    .ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists events for a task.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The task events ordered by creation time.</returns>
    public Task<IReadOnlyList<TaskEventRecord>> ListEventsAsync(
        TaskId taskId,
        CancellationToken cancellationToken = default)
    {
        return _db.ExecuteReadAsync<IReadOnlyList<TaskEventRecord>>(
            async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT event_id, task_id, kind, payload_json, created_at
                    FROM task_events
                    WHERE task_id = $task_id
                    ORDER BY created_at ASC;
                    """;
                command.Parameters.AddWithValue("$task_id", taskId.ToString());

                var results = new List<TaskEventRecord>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    results.Add(
                        new TaskEventRecord
                        {
                            EventId = reader.GetInt64(0),
                            TaskId = TaskId.Parse(reader.GetString(1)),
                            Kind = reader.GetString(2),
                            PayloadJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                            CreatedAt = DateTimeOffset.Parse(reader.GetString(4), provider: null),
                        });
                }

                return results;
            },
            cancellationToken);
    }

    private static async Task InsertTaskEventAsync(
        SqliteConnection connection,
        TaskId taskId,
        string kind,
        string? payloadJson,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var insertEvent = connection.CreateCommand();
        insertEvent.CommandText =
            """
            INSERT INTO task_events(task_id, kind, payload_json, created_at)
            VALUES ($task_id, $kind, $payload_json, $created_at);
            """;
        insertEvent.Parameters.AddWithValue("$task_id", taskId.ToString());
        insertEvent.Parameters.AddWithValue("$kind", kind);
        insertEvent.Parameters.AddWithValue("$payload_json", (object?)payloadJson ?? DBNull.Value);
        insertEvent.Parameters.AddWithValue("$created_at", createdAt.ToString("O"));
        await insertEvent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TaskRecord?> GetByIdInternalAsync(
        SqliteConnection connection,
        TaskId taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT task_id, workspace_id, project_id, parent_task_id, title, status, assigned_agent_id, created_at, updated_at
            FROM tasks
            WHERE task_id = $task_id;
            """;
        command.Parameters.AddWithValue("$task_id", taskId.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadTask(reader);
    }

    private static TaskRecord ReadTask(SqliteDataReader reader)
    {
        return new TaskRecord
        {
            TaskId = TaskId.Parse(reader.GetString(0)),
            WorkspaceId = reader.IsDBNull(1) ? null : reader.GetString(1),
            ProjectId = reader.IsDBNull(2) ? null : reader.GetString(2),
            ParentTaskId = reader.IsDBNull(3) ? null : reader.GetString(3),
            Title = reader.GetString(4),
            Status = ParseStatus(reader.GetString(5)),
            AssignedAgentId = reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(7), provider: null),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(8), provider: null),
        };
    }

    private static string StatusToString(TaskStatus status)
    {
        return status switch
        {
            TaskStatus.Pending => "pending",
            TaskStatus.InProgress => "in_progress",
            TaskStatus.Completed => "completed",
            TaskStatus.Blocked => "blocked",
            TaskStatus.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
    }

    private static TaskStatus ParseStatus(string value)
    {
        return value switch
        {
            "pending" => TaskStatus.Pending,
            "in_progress" => TaskStatus.InProgress,
            "completed" => TaskStatus.Completed,
            "blocked" => TaskStatus.Blocked,
            "cancelled" => TaskStatus.Cancelled,
            _ => throw new InvalidDataException($"Unknown task status '{value}'."),
        };
    }

    private sealed record TaskListCursor(string UpdatedAtText, string TaskIdText)
    {
        public static TaskListCursor FromTask(TaskRecord task)
        {
            return new TaskListCursor(task.UpdatedAt.ToString("O"), task.TaskId.ToString());
        }

        public static bool TryParse(string value, out TaskListCursor? cursor)
        {
            cursor = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Split('|');
            if (parts.Length != 2)
            {
                return false;
            }

            if (!DateTimeOffset.TryParse(parts[0], provider: null, out var timestamp))
            {
                return false;
            }

            TaskId taskId;
            try
            {
                taskId = TaskId.Parse(parts[1]);
            }
            catch
            {
                return false;
            }

            cursor = new TaskListCursor(timestamp.ToString("O"), taskId.ToString());
            return true;
        }

        public override string ToString()
        {
            return $"{UpdatedAtText}|{TaskIdText}";
        }
    }
}
