using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

// A separately named projection: never reuse metadata UpdatedAt, which inspection can change.
internal sealed record SessionMeaningfulActivity(DateTimeOffset? LastMeaningfulActivityUtc, string? BlockedReason)
{
    internal static async Task<SessionMeaningfulActivity> ReadAsync(Stream journal, string sessionId,
        DateTimeOffset createdAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!journal.CanSeek || journal.Length == 0 || createdAt == default || createdAt > now)
            return new(null, "activity_unknown");
        journal.Position = journal.Length - 1;
        if (journal.ReadByte() != '\n') return new(null, "incomplete_journal");
        journal.Position = 0;
        using var reader = new StreamReader(journal, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024, leaveOpen: true);
        DateTimeOffset? last = null;
        var header = false;
        var hasNonHeader = false;
        var pendingRuns = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) return new(null, "activity_unknown");
                var item = JsonSerializer.Deserialize(line, AgentJsonSerializerContext.Default.AgentEvent);
                if (item is null || !string.Equals(item.SessionId, sessionId, StringComparison.Ordinal)
                    || item.Timestamp == default || item.Timestamp > now || item.Timestamp < createdAt)
                    return new(null, "activity_unknown");
                if (!header && item is not AgentRawEvent { BackendEventType: "codealta.sessionHeader" })
                    return new(null, "activity_unknown");
                var meaningful = false;
                hasNonHeader |= item is not AgentRawEvent { BackendEventType: "codealta.sessionHeader" };
                switch (item)
                {
                    case AgentRawEvent raw:
                        switch (raw.BackendEventType)
                        {
                            case "codealta.sessionHeader":
                                if (header) return new(null, "activity_unknown");
                                header = true;
                                break;
                            // These are snapshots, not evidence of accepted work. Future inspection must not age sessions.
                            case "codealta.sessionState":
                                if (raw.Raw.ValueKind != JsonValueKind.Object) return new(null, "activity_unknown");
                                if (raw.Raw.TryGetProperty("queued_prompts", out var queue))
                                {
                                    if (queue.ValueKind != JsonValueKind.Array) return new(null, "activity_unknown");
                                    foreach (var prompt in queue.EnumerateArray())
                                        if (!prompt.TryGetProperty("state", out var state) || state.GetString() != "submitted")
                                            return new(null, "queued_work");
                                }
                                break;
                            case "local.sessionSummary":
                            case "local.sessionState":
                                break;
                            default: return new(null, "activity_unknown");
                        }
                        break;
                    case AgentContentCompletedEvent content when Enum.IsDefined(content.Kind):
                        meaningful = true;
                        break;
                    case AgentContentDeltaEvent:
                    case AgentNotesEvent:
                    case AgentPlanSnapshotEvent:
                        meaningful = true;
                        break;
                    case AgentActivityEvent activity when Enum.IsDefined(activity.Kind) && Enum.IsDefined(activity.Phase):
                        meaningful = true;
                        if (activity.Kind == AgentActivityKind.Turn && activity.RunId is { } run)
                        {
                            if (activity.Phase is AgentActivityPhase.Started or AgentActivityPhase.Requested)
                                pendingRuns.Add(run.Value);
                            else if (activity.Phase is AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled)
                                pendingRuns.Remove(run.Value);
                        }
                        break;
                    case AgentSessionUpdateEvent update when Enum.IsDefined(update.Kind):
                        meaningful = update.Kind is AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.TaskCompleted
                            or AgentSessionUpdateKind.CompactionStarted or AgentSessionUpdateKind.CompactionCompleted
                            or AgentSessionUpdateKind.Truncated or AgentSessionUpdateKind.Handoff;
                        if (update.Kind == AgentSessionUpdateKind.Idle && update.RunId is { } idleRun)
                            pendingRuns.Remove(idleRun.Value);
                        break;
                    case AgentErrorEvent error:
                        meaningful = true;
                        if (error.RunId is { } failedRun) pendingRuns.Remove(failedRun.Value);
                        break;
                    case AgentSystemPromptEvent:
                        // Loading/resuming may record a prompt before any run is accepted.
                        break;
                    // These histories require complete pending-work reconstruction. Protect rather than guess that
                    // the latest UI row means permission/input/provider/remote work has finished.
                    case AgentPermissionRequest:
                    case AgentUserInputRequest:
                    case AgentInteractionEvent:
                    case AgentBackgroundTasksEvent:
                    case AgentRemoteControlEvent:
                        return new(null, "work_history_requires_review");
                    default: return new(null, "activity_unknown");
                }
                if (meaningful && (last is null || item.Timestamp > last)) last = item.Timestamp.ToUniversalTime();
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return new(null, "activity_unknown");
        }
        if (!header) return new(null, "activity_unknown");
        if (pendingRuns.Count != 0) return new(null, "unfinished_run");
        if (last is null && hasNonHeader) return new(null, "activity_unknown");
        return new(last ?? createdAt.ToUniversalTime(), null);
    }
}
