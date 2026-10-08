namespace CodeAlta.Agent;

/// <summary>
/// Something a provider goes on doing for a session after the tool call that started it returned, and possibly
/// after the run ended: a command in the background, a watch, an agent of its own.
/// </summary>
/// <param name="TaskId">The identity of the task for its provider, which stops it by that identity.</param>
/// <param name="Kind">What the task is: <c>command</c>, <c>agent</c>, <c>workflow</c>, or the name the provider gives it.</param>
/// <param name="Description">What the task does, as the provider says it; <see langword="null"/> when it does not say.</param>
/// <param name="ToolCallId">The tool call that started the task, when the provider says it.</param>
/// <param name="ParentTaskId">The task that started this one, for a task of an agent in the background.</param>
/// <param name="StartedAt">When the task was first known.</param>
public sealed record AgentBackgroundTask(
    string TaskId,
    string Kind,
    string? Description,
    string? ToolCallId,
    string? ParentTaskId,
    DateTimeOffset StartedAt);

/// <summary>How a background task ended.</summary>
public enum AgentBackgroundTaskOutcome
{
    /// <summary>The task ran to its end.</summary>
    Completed,

    /// <summary>The task failed.</summary>
    Failed,

    /// <summary>The task was stopped before its end.</summary>
    Stopped,
}

/// <summary>The end of a background task.</summary>
/// <param name="TaskId">The identity of the task.</param>
/// <param name="ToolCallId">The tool call that started the task, when the provider says it.</param>
/// <param name="Outcome">How the task ended.</param>
/// <param name="Summary">What the provider says of the end; <see langword="null"/> when it says nothing.</param>
public sealed record AgentBackgroundTaskEnd(
    string TaskId,
    string? ToolCallId,
    AgentBackgroundTaskOutcome Outcome,
    string? Summary);

/// <summary>
/// The background tasks of a session changed. The event is the state of a provider that runs now: it is given to
/// those who listen to the session and is never recorded, so that a session read again later shows no task.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="Tasks">Every task that goes on, in the order the provider lists them.</param>
/// <param name="Ended">The tasks that ended since the previous event of the session.</param>
public sealed record AgentBackgroundTasksEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    IReadOnlyList<AgentBackgroundTask> Tasks,
    IReadOnlyList<AgentBackgroundTaskEnd> Ended)
    : AgentEvent(ProviderId, SessionId, Timestamp, null);

/// <summary>
/// Optional capability of a session whose provider goes on working in the background outside its runs.
/// </summary>
public interface IAgentBackgroundTaskProvider
{
    /// <summary>Gets the background tasks that go on now, as the last <see cref="AgentBackgroundTasksEvent"/> listed them.</summary>
    IReadOnlyList<AgentBackgroundTask> BackgroundTasks { get; }

    /// <summary>
    /// Asks the provider to stop one background task. The end of the task is told by the events of the session;
    /// a task that already ended is not an error.
    /// </summary>
    /// <param name="taskId">The identity of the task.</param>
    /// <param name="cancellationToken">Cancels the request, not a stop that was already asked.</param>
    /// <returns>Whether the provider took the request; <see langword="false"/> when it runs nothing for the session.</returns>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> is blank.</exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    Task<bool> StopBackgroundTaskAsync(string taskId, CancellationToken cancellationToken = default);
}
