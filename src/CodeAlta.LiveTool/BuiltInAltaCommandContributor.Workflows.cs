using CodeAlta.Agent;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta workflow` commands: the runs of the scripts of Code Mode, which another session can list, read and stop.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static Command CreateWorkflowCommand(AltaCommandContext context)
    {
        var group = Group("workflow", "Read and stop the runs of the scripts of Code Mode (`exec`): list them, show their steps and the sessions they started, read their result, cancel one.");
        group.Add(CreateWorkflowListCommand(context));
        group.Add(CreateWorkflowShowCommand(context));
        group.Add(CreateWorkflowResultCommand(context));
        group.Add(CreateWorkflowCancelCommand(context));
        AddHelpText(
            group,
            "A run is a script a session ran with `exec`. It runs while its turn waits for it, then in the background: its result comes back to its session as a prompt. CodeAlta keeps a run that ended for a while; its records (start, steps, the sessions it started, end) stay in the journal of its session.",
            "Examples: `alta workflow list`; `alta workflow list --session <session-id>`; `alta workflow show <run-id>`; `alta workflow result <run-id>`; `alta workflow cancel <run-id>`.");
        return group;
    }

    private static Command CreateWorkflowListCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var all = false;
        var running = false;
        var command = Leaf("list", "List the runs of a session: the ones that run, then the ones that ended a short while ago.");
        command.Add("session=", "Session whose runs are listed. Defaults to the calling session.", value => sessionId = value);
        command.Add("all", "List the runs of every session.", value => all = value is not null);
        command.Add("running", "List only the runs that go on.", value => running = value is not null);
        command.Add(async (_, _) => await RefuseJobsToSandboxAsync(context, all, sessionId).ConfigureAwait(false) ?? HandleWorkflowList(context, sessionId, all, running));
        return command;
    }

    private static Command CreateWorkflowShowCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("show", "Show one run: its state, its calls, and its steps and the sessions it started, from the journal of its session.");
        command.Add("<run-id>", "Run id, such as run-7f3c2a, from `exec`, `wait` or `alta workflow list`.", value => id = value);
        command.Add(async (_, _) => await HandleWorkflowShowAsync(context, id).ConfigureAwait(false));
        return command;
    }

    private static Command CreateWorkflowResultCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("result", "Read the result of a run that ended: what its session was given.");
        command.Add("<run-id>", "Run id from `alta workflow list`.", value => id = value);
        command.Add(async (_, _) => await HandleWorkflowResultAsync(context, id).ConfigureAwait(false));
        return command;
    }

    private static Command CreateWorkflowCancelCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("cancel", "Stop a run, and the sessions it started.");
        command.Add("<run-id>", "Run id from `alta workflow list`.", value => id = value);
        command.Add(async (_, _) => await HandleWorkflowCancelAsync(context, id).ConfigureAwait(false));
        AddHelpText(command, "A session cancels its own runs and those of the sessions it started. Its result still comes back to the session that ran it, as stopped.");
        return command;
    }

    private static int HandleWorkflowList(AltaCommandContext context, string? sessionId, bool all, bool running)
    {
        if (context.Services.Get<CodeModeRunRegistry>() is not { } runs) return WorkflowsUnavailable(context);
        if (all && NormalizeOptionalText(sessionId) is not null)
            return UsageError(context, "usage.sessionConflict", "Use either --session or --all, not both.", "alta workflow list");

        // A caller that is no session (the user's command line, an MCP client) sees the runs of every session.
        var scope = all ? null : NormalizeOptionalText(sessionId) ?? NormalizeOptionalText(context.Caller.SourceSessionId);
        var listed = runs.List(scope).Where(run => !running || run.Running).OrderByDescending(static run => run.Running).ToArray();
        var limit = Math.Max(1, (context.MaxOutputRecords ?? 200) - 1);
        foreach (var run in listed.Take(limit))
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, WorkflowRecord(context, "alta.workflow", run, runs.FindAny(run.Id)));
        }

        WriteSummary(context, "alta.workflow.summary", Math.Min(listed.Length, limit), truncated: listed.Length > limit);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWorkflowShowAsync(AltaCommandContext context, string? id)
    {
        if (context.Services.Get<CodeModeRunRegistry>() is not { } runs) return WorkflowsUnavailable(context);
        if (NormalizeOptionalText(id) is not { } runId) return UsageError(context, "usage.missingRun", "A run id is required.", "alta workflow show");
        if (runs.FindAny(runId) is not { } run) return WorkflowNotFound(context, runId);
        if (await RefuseRunToSandboxAsync(context, run).ConfigureAwait(false) is { } refused) return refused;

        var record = WorkflowRecord(context, "alta.workflow.show", run.Info(), run);
        // The steps and the sessions are in the journal of the session, written in order by the run.
        var steps = new List<Dictionary<string, object?>>();
        var sessions = new List<Dictionary<string, object?>>();
        if (context.Services.Get<SessionRuntimeService>() is { } runtime
            && await runtime.TryGetResumableSessionAsync(run.SessionId, context.CancellationToken).ConfigureAwait(false) is { } session
            && await runtime.TryReadStoredHistoryAsync(session, context.CancellationToken).ConfigureAwait(false) is { } history)
        {
            foreach (var written in history.OfType<AgentWorkflowEvent>().Where(written => written.WorkflowRunId == runId))
            {
                switch (written.Kind)
                {
                    case AgentWorkflowEventKind.StepEnded:
                        steps.Add(new(StringComparer.Ordinal) { ["stepId"] = written.StepId, ["status"] = written.Status, ["durationMs"] = written.DurationMs, ["message"] = written.Message });
                        break;
                    case AgentWorkflowEventKind.SessionStarted:
                        sessions.Add(new(StringComparer.Ordinal) { ["sessionId"] = written.ChildSessionId, ["stepId"] = written.StepId, ["title"] = written.Title, ["status"] = "running" });
                        break;
                    case AgentWorkflowEventKind.SessionEnded when sessions.FindLast(started => Equals(started["sessionId"], written.ChildSessionId)) is { } started:
                        started["status"] = written.Status;
                        started["durationMs"] = written.DurationMs;
                        started["inputTokens"] = written.InputTokens;
                        started["outputTokens"] = written.OutputTokens;
                        if (written.Message is not null) started["message"] = written.Message;
                        break;
                }
            }
        }

        record["steps"] = steps;
        record["sessions"] = sessions;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWorkflowResultAsync(AltaCommandContext context, string? id)
    {
        if (context.Services.Get<CodeModeRunRegistry>() is not { } runs) return WorkflowsUnavailable(context);
        if (NormalizeOptionalText(id) is not { } runId) return UsageError(context, "usage.missingRun", "A run id is required.", "alta workflow result");
        if (runs.FindAny(runId) is not { } run) return WorkflowNotFound(context, runId);
        if (await RefuseRunToSandboxAsync(context, run).ConfigureAwait(false) is { } refused) return refused;
        if (run.Running)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "workflow.running", AltaExitCodes.Failure,
                $"Run '{runId}' still runs: its result comes back to its session when it ends. `alta workflow show {runId}` says where it is.");
            return AltaExitCodes.Failure;
        }

        var result = await run.Completion.ConfigureAwait(false);
        var record = WorkflowRecord(context, "alta.workflow.result", run.Info(), run);
        record["text"] = CodeModeResultFormatter.Format(result, run.MaxOutputTokens);
        if (result.ValueJson is { } value) record["valueJson"] = value;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWorkflowCancelAsync(AltaCommandContext context, string? id)
    {
        if (context.Services.Get<CodeModeRunRegistry>() is not { } runs) return WorkflowsUnavailable(context);
        if (NormalizeOptionalText(id) is not { } runId) return UsageError(context, "usage.missingRun", "A run id is required.", "alta workflow cancel");
        if (runs.FindAny(runId) is not { } run) return WorkflowNotFound(context, runId);

        // A session stops its own runs and those of the sessions below it; a caller that is no session (the user), any.
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is { } caller && !await IsAtOrBelowAsync(context, run.SessionId, caller).ConfigureAwait(false))
            return PermissionDenied(context, "workflow.notOwnRun", $"Run '{runId}' belongs to a session that is neither this session nor one it started: it cannot cancel it.");

        var wasRunning = run.Running;
        run.Stop();
        var record = WorkflowRecord(context, "alta.workflow.cancelled", run.Info(), run);
        record["wasRunning"] = wasRunning;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    // Whether a session is the caller, or below it in the sessions it started.
    private static async Task<bool> IsAtOrBelowAsync(AltaCommandContext context, string sessionId, string callerId)
    {
        var runtime = context.Services.Get<SessionRuntimeService>();
        var current = sessionId;
        for (var depth = 0; depth < 16 && current is not null; depth++)
        {
            if (string.Equals(current, callerId, StringComparison.OrdinalIgnoreCase)) return true;
            current = runtime is null ? null
                : (await runtime.TryGetResumableSessionAsync(current, context.CancellationToken).ConfigureAwait(false)) is { } session
                    ? session.ParentSessionId ?? session.CreatedBy?.SourceSessionId
                    : null;
        }

        return false;
    }

    // A sandboxed caller reads the runs of the sessions it reaches.
    private static async Task<int?> RefuseRunToSandboxAsync(AltaCommandContext context, CodeModeRunHandle run)
        => !IsCaller(context, run.SessionId)
           && await AltaSandboxGate.OfCallerAsync(context).ConfigureAwait(false) is not null
           && await ResolveSessionInfoAsync(context, run.SessionId).ConfigureAwait(false) is { ExitCode: not AltaExitCodes.Success } refused
            ? refused.ExitCode
            : null;

    private static Dictionary<string, object?> WorkflowRecord(AltaCommandContext context, string type, CodeModeRunInfo info, CodeModeRunHandle? run)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = info.Id,
            ["sessionId"] = info.SessionId,
            ["state"] = info.Running ? "running" : run?.Outcome switch
            {
                AgentBackgroundTaskOutcome.Completed => "completed",
                AgentBackgroundTaskOutcome.Stopped => "cancelled",
                _ => "failed",
            },
            ["calls"] = info.Calls,
            ["elapsedSeconds"] = (int)info.Elapsed.TotalSeconds,
        };
        if (info.Title is not null) record["title"] = info.Title;
        if (run?.CallId is { } callId) record["callId"] = callId;
        if (run is not null) record["background"] = run.Backgrounded;
        return record;
    }

    private static int WorkflowNotFound(AltaCommandContext context, string id)
        => NotFound(context, "workflow.notFound", $"Run '{id}' was not found: it never was, or it ended long ago. List the runs with `alta workflow list --all`.");

    private static int WorkflowsUnavailable(AltaCommandContext context)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "This host runs no scripts of Code Mode.");
        return AltaExitCodes.ServiceUnavailable;
    }
}
