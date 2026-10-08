using System.Text.Json;

namespace CodeAlta.Agent.Claude;

// What the CLI goes on doing outside its turns: a command in the background, a watch, an agent of its own.
// `background_tasks_changed` lists the tasks that go on, all of them at each change: it is what says that a task
// is there. `task_started` says which tool call started a task, which the list does not; `task_updated` and
// `task_notification` say how a task ended, just before the list that no longer has it.
internal sealed partial class ClaudeCodeSession
{
    private sealed record TaskEntry(string TaskId, string Kind, string? Description, string? ParentTaskId, bool Ambient);

    // Taken before `_gate` by whatever changes the tasks, and held while those who listen are told: two changes
    // are told in the order they were made, whether they come from the reader of the connection or from the
    // start or the end of a process.
    private readonly Lock _backgroundTasksNotice = new();
    private readonly List<TaskEntry> _taskSet = [];
    private readonly Dictionary<string, string> _taskToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _taskStarts = new(StringComparer.Ordinal);
    private IReadOnlyList<AgentBackgroundTask> _shownTasks = [];
    private Action<IReadOnlyList<AgentBackgroundTask>, IReadOnlyList<AgentBackgroundTaskEnd>>? _onBackgroundTasks;

    /// <summary>Gets the background tasks that go on, without those the CLI runs for its own housekeeping.</summary>
    public IReadOnlyList<AgentBackgroundTask> BackgroundTasks
    {
        get
        {
            lock (_gate)
            {
                return _shownTasks;
            }
        }
    }

    /// <summary>
    /// Registers what is called when the background tasks change, with the tasks that go on and those that just
    /// ended. The handler is called while the CLI is being read: it must not wait.
    /// </summary>
    public IDisposable OnBackgroundTasksChanged(Action<IReadOnlyList<AgentBackgroundTask>, IReadOnlyList<AgentBackgroundTaskEnd>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _onBackgroundTasks = handler;
        }

        return new BackgroundTasksRegistration(this, handler);
    }

    private sealed class BackgroundTasksRegistration(
        ClaudeCodeSession session, Action<IReadOnlyList<AgentBackgroundTask>, IReadOnlyList<AgentBackgroundTaskEnd>> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (session._gate)
            {
                if (ReferenceEquals(session._onBackgroundTasks, handler))
                {
                    session._onBackgroundTasks = null;
                }
            }
        }
    }

    /// <summary>
    /// Asks the CLI to stop one background task. The CLI then says that the task ended, as for any other end.
    /// </summary>
    /// <returns>Whether the CLI took the request; <see langword="false"/> when no process runs or it refused.</returns>
    public async Task<bool> StopBackgroundTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ClaudeCodeConnection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        if (connection is null || connection.IsClosed)
        {
            return false;
        }

        try
        {
            // The turn gate is not taken: a task is stopped while a turn runs as well as between two turns.
            await connection.RequestAsync("stop_task", writer => writer.WriteString("task_id", taskId), _options.InterruptTimeout, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ClaudeCodeControlException or TimeoutException or IOException)
        {
            // An older CLI does not know the request, or the process ended meanwhile: nothing was stopped.
            return false;
        }
    }

    // The tasks of a process end with it, and a process that starts has none yet.
    private void ForgetBackgroundTasks()
        => ChangeBackgroundTasks(_ =>
        {
            _taskSet.Clear();
            _taskToolCalls.Clear();
            _taskStarts.Clear();
        });

    // Reads one message of the CLI about its tasks. Called by the reader of the connection.
    private void ReadBackgroundTaskMessage(string? subtype, JsonElement message)
    {
        var now = DateTimeOffset.UtcNow;
        switch (subtype)
        {
            case "background_tasks_changed":
                ChangeBackgroundTasks(_ =>
                {
                    _taskSet.Clear();
                    if (ClaudeCodeJson.TryGetArray(message, "tasks", out var tasks))
                    {
                        foreach (var task in tasks.EnumerateArray())
                        {
                            if (task.ValueKind == JsonValueKind.Object && ClaudeCodeJson.GetString(task, "task_id") is { Length: > 0 } id &&
                                !_taskSet.Exists(entry => entry.TaskId == id))
                            {
                                _taskSet.Add(new TaskEntry(id, TaskKind(ClaudeCodeJson.GetString(task, "task_type")), Text(task, "description"),
                                    Text(task, "parent_task_id"), ClaudeCodeJson.GetBoolean(task, "ambient")));
                                _taskStarts.TryAdd(id, now);
                            }
                        }
                    }

                });
                break;
            case "task_started":
                if (ClaudeCodeJson.GetString(message, "task_id") is { Length: > 0 } started)
                {
                    ChangeBackgroundTasks(_ =>
                    {
                        _taskStarts.TryAdd(started, now);
                        if (Text(message, "tool_use_id") is { } toolCall)
                        {
                            _taskToolCalls[started] = toolCall;
                        }
                    });
                }

                break;
            case "task_notification":
                EndBackgroundTask(message, ClaudeCodeJson.GetString(message, "status"));
                break;
            case "task_updated":
                EndBackgroundTask(message, ClaudeCodeJson.TryGetObject(message, "patch", out var patch) ? ClaudeCodeJson.GetString(patch, "status") : null);
                break;
        }
    }

    // A task says that it ended, once or twice: by its update, by its notice, or by both. The first one is told.
    private void EndBackgroundTask(JsonElement message, string? status)
    {
        AgentBackgroundTaskOutcome outcome;
        switch (status)
        {
            case "completed": outcome = AgentBackgroundTaskOutcome.Completed; break;
            case "failed": outcome = AgentBackgroundTaskOutcome.Failed; break;
            case "stopped" or "killed": outcome = AgentBackgroundTaskOutcome.Stopped; break;
            default: return;
        }

        if (ClaudeCodeJson.GetString(message, "task_id") is not { Length: > 0 } id)
        {
            return;
        }

        ChangeBackgroundTasks(ended =>
        {
            var toolCall = Text(message, "tool_use_id") ?? _taskToolCalls.GetValueOrDefault(id);
            _taskToolCalls.Remove(id);
            _taskStarts.Remove(id);
            // The list that no longer has the task comes after this: the task is not shown as going on meanwhile,
            // and what else the CLI says of its end changes nothing.
            _taskSet.RemoveAll(entry => entry.TaskId == id);
            // Only a task that was shown as going on in the background has an end to show, and it is shown once:
            // what is shown no longer has the task when the CLI says its end a second time.
            if (_shownTasks.Any(task => task.TaskId == id))
            {
                ended.Add(new AgentBackgroundTaskEnd(id, toolCall, outcome, Text(message, "summary")));
            }
        });
    }

    // Applies a change under the gate, then tells those who listen when what they are shown changed.
    private void ChangeBackgroundTasks(Action<List<AgentBackgroundTaskEnd>> change)
    {
        lock (_backgroundTasksNotice)
        {
            Action<IReadOnlyList<AgentBackgroundTask>, IReadOnlyList<AgentBackgroundTaskEnd>>? handler;
            IReadOnlyList<AgentBackgroundTask> shown;
            List<AgentBackgroundTaskEnd> ended = [];
            lock (_gate)
            {
                change(ended);
                // The housekeeping of the CLI keeps its process alive and is not shown.
                shown = [.. _taskSet.Where(static entry => !entry.Ambient).Select(entry => new AgentBackgroundTask(
                    entry.TaskId, entry.Kind, entry.Description, _taskToolCalls.GetValueOrDefault(entry.TaskId), entry.ParentTaskId,
                    _taskStarts.GetValueOrDefault(entry.TaskId, DateTimeOffset.UtcNow)))];
                if (ended.Count == 0 && shown.SequenceEqual(_shownTasks))
                {
                    return;
                }

                _shownTasks = shown;
                handler = _onBackgroundTasks;
            }

            handler?.Invoke(shown, ended);
        }
    }

    private static string? Text(JsonElement element, string name)
        => ClaudeCodeJson.GetString(element, name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    // The kinds every provider shares; what the CLI names otherwise is kept as it names it.
    private static string TaskKind(string? taskType)
        => taskType switch
        {
            "local_bash" => "command",
            "local_agent" or "remote_agent" => "agent",
            "local_workflow" => "workflow",
            { Length: > 0 } => taskType,
            _ => "task",
        };
}
