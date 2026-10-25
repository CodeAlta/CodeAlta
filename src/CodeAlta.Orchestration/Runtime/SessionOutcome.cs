using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>How the runs of a session ended, as its journal tells it.</summary>
public enum SessionOutcomeStatus
{
    /// <summary>Nothing tells it: no answer and no error.</summary>
    Unknown,

    /// <summary>A run is in flight.</summary>
    Running,

    /// <summary>The last run answered.</summary>
    Completed,

    /// <summary>The last run failed.</summary>
    Failed,

    /// <summary>The last run was stopped, or the session was aborted after it answered.</summary>
    Cancelled,
}

/// <summary>
/// The tokens and the cost of the operations of the runs of a session: each request to a model once, summaries of
/// compaction included.
/// </summary>
/// <param name="OperationCount">How many operations reported their usage.</param>
/// <param name="InputTokens">The input tokens, the part read from or written to the prompt cache included.</param>
/// <param name="OutputTokens">The output tokens.</param>
/// <param name="CachedInputTokens">The part of the input read from the prompt cache.</param>
/// <param name="ReasoningTokens">The reasoning tokens, when the providers report them.</param>
/// <param name="CacheWriteTokens">The part of the input written to the prompt cache.</param>
/// <param name="Cost">The cost the providers reported, in <paramref name="CostUnit"/>; null when none reported one.</param>
/// <param name="CostUnit">The unit of the cost: that of the first operation that reported one; the others in another unit are left out.</param>
public sealed record SessionUsageTotals(
    int OperationCount,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningTokens,
    long CacheWriteTokens = 0,
    double? Cost = null,
    string? CostUnit = null)
{
    /// <summary>Gets totals with no operation.</summary>
    public static SessionUsageTotals None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Gets the input and output tokens together.</summary>
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// How the runs of a session ended, read from the events of its journal: what <c>alta session result</c> reports and what
/// a headless run returns.
/// </summary>
/// <param name="Status">How the last run ended.</param>
/// <param name="AbortReason">The reason recorded by an abort of the session (<see cref="SessionRuntimeService.AbortTreeAsync"/>) after its last answer; null otherwise.</param>
/// <param name="FinalMessage">The last answer of the assistant, whatever the status; null when there is none.</param>
/// <param name="FinalMessageAt">When the last answer was written.</param>
/// <param name="FinalError">The last error, when it ended the last run (a failed or a cancelled one).</param>
/// <param name="StartedAt">When the events read start: the first prompt, else the first event.</param>
/// <param name="CompletedAt">When they end: the last answer, else the last event.</param>
/// <param name="Usage">The tokens and the cost of the operations.</param>
/// <param name="FinalUsage">The last usage the session reported (its context window, its last operation).</param>
/// <param name="EventCount">How many events were read.</param>
/// <param name="RunCount">How many runs they hold.</param>
/// <param name="UserMessageCount">How many prompts.</param>
/// <param name="AssistantMessageCount">How many answers.</param>
/// <param name="ToolCallCount">How many tool calls were asked.</param>
public sealed record SessionOutcome(
    SessionOutcomeStatus Status,
    string? AbortReason,
    string? FinalMessage,
    DateTimeOffset? FinalMessageAt,
    AgentErrorEvent? FinalError,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    SessionUsageTotals Usage,
    AgentSessionUsage? FinalUsage,
    int EventCount,
    int RunCount,
    int UserMessageCount,
    int AssistantMessageCount,
    int ToolCallCount)
{
    /// <summary>Gets the time from <see cref="StartedAt"/> to <see cref="CompletedAt"/>, when both are known and in order.</summary>
    public TimeSpan? Duration => StartedAt is { } start && CompletedAt is { } end && end >= start ? end - start : null;

    /// <summary>Gets the kind of failure of <see cref="FinalError"/>, when its provider classified it.</summary>
    public AgentFailureKind? FailureKind => FinalError?.Failure?.Kind;

    /// <summary>Gets the message of <see cref="FinalError"/>.</summary>
    public string? FailureMessage => FinalError?.Message;

    /// <summary>
    /// Reads the outcome of events of a journal.
    /// </summary>
    /// <param name="events">The events, in the order of the journal: a whole history, or a part of it (<see cref="SelectLastTurn"/>, <see cref="SelectFromRun"/>).</param>
    /// <param name="isRunning">Whether a run of the session is in flight.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is null.</exception>
    public static SessionOutcome From(IReadOnlyList<AgentEvent> events, bool isRunning)
    {
        ArgumentNullException.ThrowIfNull(events);
        var finalAssistant = events.OfType<AgentContentCompletedEvent>().LastOrDefault(static content => content.Kind == AgentContentKind.Assistant);
        var firstUser = events.OfType<AgentContentCompletedEvent>().FirstOrDefault(static content => content.Kind == AgentContentKind.User);
        var lastError = events.OfType<AgentErrorEvent>().LastOrDefault();
        var abort = events.OfType<AgentSessionUpdateEvent>().LastOrDefault(static update => AbortReasonOf(update) is not null);
        var endingError = lastError is not null && (finalAssistant is null || lastError.Timestamp >= finalAssistant.Timestamp) ? lastError : null;
        var aborted = abort is not null && (finalAssistant is null || abort.Timestamp >= finalAssistant.Timestamp) ? abort : null;
        var status = isRunning ? SessionOutcomeStatus.Running
            : aborted is not null ? SessionOutcomeStatus.Cancelled
            : endingError is not null ? IsCancellation(endingError) ? SessionOutcomeStatus.Cancelled : SessionOutcomeStatus.Failed
            : finalAssistant is not null ? SessionOutcomeStatus.Completed
            : SessionOutcomeStatus.Unknown;
        var usageUpdates = events.OfType<AgentSessionUpdateEvent>().Where(static update => update.Usage is not null).ToArray();
        return new SessionOutcome(
            status,
            aborted is null ? null : AbortReasonOf(aborted),
            finalAssistant?.Content,
            finalAssistant?.Timestamp,
            status is SessionOutcomeStatus.Failed or SessionOutcomeStatus.Cancelled ? endingError : null,
            firstUser?.Timestamp ?? events.FirstOrDefault()?.Timestamp,
            finalAssistant?.Timestamp ?? events.LastOrDefault()?.Timestamp,
            TotalUsage(usageUpdates),
            usageUpdates.LastOrDefault()?.Usage,
            events.Count,
            events.Select(static item => item.RunId?.Value).Where(static runId => !string.IsNullOrWhiteSpace(runId)).Distinct(StringComparer.Ordinal).Count(),
            events.OfType<AgentContentCompletedEvent>().Count(static content => content.Kind == AgentContentKind.User),
            events.OfType<AgentContentCompletedEvent>().Count(static content => content.Kind == AgentContentKind.Assistant),
            events.OfType<AgentActivityEvent>().Count(static activity => IsToolCall(activity.Kind) && activity.Phase == AgentActivityPhase.Requested));
    }

    /// <summary>Keeps the events of the run of the last answer, or of the last run when no answer has one.</summary>
    /// <param name="history">The history of the session.</param>
    /// <returns>The events of that run; the whole history when no event names a run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> is null.</exception>
    public static IReadOnlyList<AgentEvent> SelectLastTurn(IReadOnlyList<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0) return history;
        var lastRunId = history.OfType<AgentContentCompletedEvent>()
            .Where(static content => content.Kind == AgentContentKind.Assistant && content.RunId is not null)
            .Select(static content => content.RunId!.Value.Value)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastRunId))
        {
            lastRunId = history.Where(static item => item.RunId is not null).Select(static item => item.RunId!.Value.Value).LastOrDefault();
        }

        return string.IsNullOrWhiteSpace(lastRunId)
            ? history
            : history.Where(item => string.Equals(item.RunId?.Value, lastRunId, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>
    /// Keeps the events from the first event of a run to the end of the history: that run and what came after it (the
    /// runs a queued prompt or the answer of a child started).
    /// </summary>
    /// <param name="history">The history of the session.</param>
    /// <param name="runId">The run.</param>
    /// <returns>The events from that run on; none when no event names it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> is null.</exception>
    public static IReadOnlyList<AgentEvent> SelectFromRun(IReadOnlyList<AgentEvent> history, AgentRunId runId)
    {
        ArgumentNullException.ThrowIfNull(history);
        for (var index = 0; index < history.Count; index++)
        {
            if (string.Equals(history[index].RunId?.Value, runId.Value, StringComparison.Ordinal))
                return index == 0 ? history : history.Skip(index).ToArray();
        }

        return [];
    }

    /// <summary>Gets whether an error is the end of a run that was stopped rather than a failure.</summary>
    /// <param name="error">The error.</param>
    /// <returns><see langword="true"/> for a cancellation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static bool IsCancellation(AgentErrorEvent error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Exception is OperationCanceledException
               || string.Equals(error.ExceptionInfo?.Type, typeof(OperationCanceledException).FullName, StringComparison.Ordinal)
               || string.Equals(error.ExceptionInfo?.Type, typeof(TaskCanceledException).FullName, StringComparison.Ordinal)
               || error.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gets the reason an update event records for an abort (<see cref="SessionRuntimeService.AbortReasonDetail"/>).</summary>
    /// <param name="update">The event.</param>
    /// <returns>The reason, or <see langword="null"/> for an event that records no abort.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="update"/> is null.</exception>
    public static string? AbortReasonOf(AgentSessionUpdateEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return update.Details is { ValueKind: System.Text.Json.JsonValueKind.Object } details
               && details.TryGetProperty(SessionRuntimeService.AbortReasonDetail, out var reason)
               && reason.ValueKind == System.Text.Json.JsonValueKind.String
            ? reason.GetString()
            : null;
    }

    /// <summary>Adds the usage of the operations of the runs: each operation once, however many events repeat it.</summary>
    /// <param name="usageUpdates">The usage events of the runs, in order.</param>
    /// <returns>The totals.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="usageUpdates"/> is null.</exception>
    public static SessionUsageTotals TotalUsage(IEnumerable<AgentSessionUpdateEvent> usageUpdates)
    {
        ArgumentNullException.ThrowIfNull(usageUpdates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long inputTokens = 0, outputTokens = 0, cachedInputTokens = 0, reasoningTokens = 0, cacheWriteTokens = 0;
        double? cost = null;
        string? costUnit = null;
        var operationCount = 0;
        foreach (var usage in usageUpdates)
        {
            if (usage.Usage?.LastOperation is not { } operation) continue;
            var signature = string.Join('\u001f', usage.RunId?.Value, operation.Model, operation.InputTokens, operation.OutputTokens, operation.CachedInputTokens,
                operation.CacheReadTokens, operation.CacheWriteTokens, operation.ReasoningTokens, operation.ParentToolCallId, operation.Initiator);
            if (!seen.Add(signature)) continue;
            operationCount++;
            // The input of an operation holds what the cache read and wrote: the cached and written counts are parts of the
            // input, not counts to add to it.
            var input = AgentInputTokenUsage.From(operation);
            inputTokens += input?.Total ?? 0;
            outputTokens += operation.OutputTokens ?? 0;
            cachedInputTokens += input?.CacheRead ?? 0;
            reasoningTokens += operation.ReasoningTokens ?? 0;
            cacheWriteTokens += input?.CacheWrite ?? 0;
            // Costs add up in one unit: the unit of the first operation that reports one.
            if (operation.Cost is { } operationCost && (cost is null || string.Equals(costUnit, operation.CostUnit, StringComparison.Ordinal)))
            {
                cost = (cost ?? 0) + operationCost;
                costUnit = operation.CostUnit;
            }
        }

        return new SessionUsageTotals(operationCount, inputTokens, outputTokens, cachedInputTokens, reasoningTokens, cacheWriteTokens, cost, costUnit);
    }

    private static bool IsToolCall(AgentActivityKind kind)
        => kind is AgentActivityKind.ToolCall or AgentActivityKind.CommandExecution or AgentActivityKind.FileChange or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall or AgentActivityKind.CollabAgentToolCall;
}
