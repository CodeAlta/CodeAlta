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
        group.Add(CreateWorkflowResumeCommand(context));
        AddHelpText(
            group,
            "A run is a script a session ran with `exec`. It runs while its turn waits for it, then in the background: its result comes back to its session as a prompt. CodeAlta keeps a run that ended for a while; its records (start, steps, the sessions it started, end) stay in the journal of its session. CodeAlta Desktop and the TUI also keep the results of its steps, so that a run CodeAlta ended during is resumed, and a run is run again over what it recorded.",
            "Examples: `alta workflow list`; `alta workflow list --session <session-id>`; `alta workflow show <run-id>`; `alta workflow result <run-id>`; `alta workflow cancel <run-id>`; `alta workflow resume <run-id>`; `alta workflow resume <run-id> --from merge`.");
        return group;
    }

    private static Command CreateWorkflowResumeCommand(AltaCommandContext context)
    {
        string? id = null;
        string? from = null;
        var all = false;
        var command = Leaf("resume", "Ask the session of a run to resume it, or to run it again over the results its steps recorded.");
        command.Add("<run-id>", "Run id, such as run-7f3c2a, from the card of the run or `alta workflow list`.", value => id = value);
        command.Add("from=", "Run again from this step (`merge`, `merge#1` for its second occurrence, `lib/merge` for a step of the script `lib` that `workflow()` started, `lib#1/merge` for its second call): the steps before it give their recorded results.", value => from = value);
        command.Add("all", "Replay every step the run recorded, wherever it is: only the rest of the code runs (a rerun with recorded results).", value => all = value is not null);
        command.Add(async (_, _) => await HandleWorkflowResumeAsync(context, id, from, all).ConfigureAwait(false));
        AddHelpText(command,
            "The session of the run gets a prompt that calls `exec` with `// @exec: {\"resume\": \"<run-id>\"}`: never automatic, the run goes with the tools and the permissions of that session. "
            + "A changed script does not resume; a call that was in flight when the run stopped and may not run twice runs again only once the user agrees.");
        return command;
    }

    private static async ValueTask<int> HandleWorkflowResumeAsync(AltaCommandContext context, string? id, string? from, bool all)
    {
        const string path = "alta workflow resume";
        if (context.Services.Get<CodeModeRunRegistry>() is not { } runs) return WorkflowsUnavailable(context);
        if (NormalizeOptionalText(id) is not { } runId) return UsageError(context, "usage.missingRun", "A run id is required.", path);
        if (NormalizeOptionalText(from) is not null && all) return UsageError(context, "usage.fromAll", "Use either --from or --all, not both.", path);
        if (await AltaSandboxGate.OfCallerAsync(context).ConfigureAwait(false) is { } sandbox)
            return PermissionDenied(context, "sandbox.workflowResume", AltaSandboxGate.Refusal(sandbox, "resume a run of a script"));
        if (runs.Store is not { } store)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
                "This CodeAlta keeps no record of runs: a run cannot be resumed here.");
            return AltaExitCodes.ServiceUnavailable;
        }

        await runs.WhenKeptAsync(runId).ConfigureAwait(false);
        if (store.TryLoad(runId) is not { } run) return WorkflowNotFound(context, runId);
        // A run that ended here a moment ago may not have its end written yet.
        if (runs.FindAny(runId)?.Running ?? run.EndedAt is null)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "workflow.running", AltaExitCodes.Failure,
                $"Run '{runId}' still runs: wait for it, or stop it with `alta workflow cancel {runId}`, before running it again.");
            return AltaExitCodes.Failure;
        }

        // A step of a workflow() script is named by its path (`lib/merge`), or by its id alone when no other step has it.
        string? step = null;
        if (NormalizeOptionalText(from) is { } named && (step = CodeModeResume.ResolveStep(run, named, out var problem)) is null)
            return UsageError(context, "workflow.unknownStep", problem!, path);

        // A session resumes its own runs and those of the sessions below it; a caller that is no session (the user), any.
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is { } caller && !await IsAtOrBelowAsync(context, run.SessionId, caller).ConfigureAwait(false))
            return PermissionDenied(context, "workflow.notOwnRun", $"Run '{runId}' belongs to a session that is neither this session nor one it started: it cannot resume it.");
        if (context.Services.Get<SessionRuntimeService>() is not { } runtime) return WorkflowsUnavailable(context);

        string delivery;
        try
        {
            delivery = await runtime.DeliverHostPromptAsync(run.SessionId, CodeModeResume.Prompt(runId, step, all), CodeAlta.Agent.AgentPromptProvenance.Workflow, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "workflow.sessionUnavailable", AltaExitCodes.Failure,
                $"The session of run '{runId}' cannot take a prompt: {exception.Message}");
            return AltaExitCodes.Failure;
        }

        if (delivery == "missing_session")
            return NotFound(context, "session.notFound", $"The session '{run.SessionId}' of run '{runId}' is no longer known.");

        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.workflow.resume",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = runId,
            ["sessionId"] = run.SessionId,
            ["mode"] = step is not null ? "from" : all ? "all" : "resume",
            ["delivery"] = delivery,
        };
        if (step is not null) record["from"] = step;
        if (run.Title is not null) record["title"] = run.Title;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static Command CreateWorkflowListCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var all = false;
        var running = false;
        var command = Leaf("list", "List the runs of a session: the ones that run, then the ones that ended, which CodeAlta keeps for a while (the 100 newest, 14 days at most).");
        command.Add("session=", "Session whose runs are listed. Defaults to the calling session.", value => sessionId = value);
        command.Add("all", "List the runs of every session.", value => all = value is not null);
        command.Add("running", "List only the runs that go on.", value => running = value is not null);
        command.Add(async (_, _) => await RefuseJobsToSandboxAsync(context, all, sessionId).ConfigureAwait(false) ?? HandleWorkflowList(context, sessionId, all, running));
        return command;
    }

    private static Command CreateWorkflowShowCommand(AltaCommandContext context)
    {
        string? id = null;
        var profile = false;
        string? format = null;
        var command = Leaf("show", "Show one run: its state, its calls, its graph (the steps its script declares, and how far each went), and the sessions it started, from the journal of its session.");
        command.Add("<run-id>", "Run id, such as run-7f3c2a, from `exec`, `wait` or `alta workflow list`.", value => id = value);
        command.Add("profile", "Add where the time of a run that ended went: each step, each function (calls, total, slowest, time queued) and the script's own time.", value => profile = value is not null);
        command.Add("format=", "`json` (the default), or `mermaid`: the graph of the run as a Mermaid flowchart, in the `mermaid` field, as Copy as Mermaid gives it.", value => format = value);
        command.Add(async (_, _) => await HandleWorkflowShowAsync(context, id, profile, format).ConfigureAwait(false));
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
        // The runs of this process, then those the store keeps: listing never reads a journal.
        var listed = runs.ListKnown(scope).Where(run => !running || run.Running).OrderByDescending(static run => run.Running).ThenByDescending(static run => run.StartedAt).ToArray();
        var limit = Math.Max(1, (context.MaxOutputRecords ?? 200) - 1);
        foreach (var run in listed.Take(limit))
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, WorkflowRecord(context, "alta.workflow", run, runs.FindAny(run.Id)));
        }

        WriteSummary(context, "alta.workflow.summary", Math.Min(listed.Length, limit), truncated: listed.Length > limit);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWorkflowShowAsync(AltaCommandContext context, string? id, bool profile, string? format)
    {
        if (context.Services.Get<CodeModeRunRegistry>() is not { } runs) return WorkflowsUnavailable(context);
        if (NormalizeOptionalText(id) is not { } runId) return UsageError(context, "usage.missingRun", "A run id is required.", "alta workflow show");
        var mermaid = NormalizeOptionalText(format)?.ToLowerInvariant() switch { null or "json" => false, "mermaid" => true, _ => (bool?)null };
        if (mermaid is null) return UsageError(context, "usage.invalidFormat", "--format is `json` or `mermaid`.", "alta workflow show");
        // A run of this process, or one the store keeps: after a restart, the runs that ended before it.
        var run = runs.FindAny(runId);
        if ((run?.Info() ?? runs.TryInfo(runId)) is not { } info) return WorkflowNotFound(context, runId);
        if (await RefuseRunToSandboxAsync(context, info.SessionId).ConfigureAwait(false) is { } refused) return refused;

        var records = new List<AgentWorkflowEvent>();
        if (context.Services.Get<SessionRuntimeService>() is { } runtime
            && await runtime.TryGetResumableSessionAsync(info.SessionId, context.CancellationToken).ConfigureAwait(false) is { } session
            && await runtime.TryReadStoredHistoryAsync(session, context.CancellationToken).ConfigureAwait(false) is { } history)
        {
            records.AddRange(history.OfType<AgentWorkflowEvent>().Where(written => written.WorkflowRunId == runId));
        }

        var graph = CodeModeRunGraph.Read(records);
        var record = WorkflowRecord(context, "alta.workflow.show", info, run);
        if (mermaid == true)
        {
            record["mermaid"] = CodeModeRunGraph.ToMermaid(graph);
            AltaJsonlWriter.WriteRecord(context.Stdout, record);
            return AltaExitCodes.Success;
        }

        // The steps and the sessions are in the journal of the session, written in order by the run.
        var steps = new List<Dictionary<string, object?>>();
        var sessions = new List<Dictionary<string, object?>>();
        {
            foreach (var written in records)
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
        if (graph.Count > 0)
        {
            record["graph"] = graph.Select(static step => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = step.Id,
                ["title"] = step.Title,
                ["after"] = step.After,
                ["each"] = step.Each,
                ["state"] = step.State,
                ["sessions"] = step.Sessions,
                ["declared"] = step.Declared,
            }).ToArray();
        }

        if (profile && run is not null && !run.Running && run.Completion.IsCompletedSuccessfully)
        {
            record["profile"] = Profile(run.Completion.Result);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleWorkflowResultAsync(AltaCommandContext context, string? id)
    {
        if (context.Services.Get<CodeModeRunRegistry>() is not { } runs) return WorkflowsUnavailable(context);
        if (NormalizeOptionalText(id) is not { } runId) return UsageError(context, "usage.missingRun", "A run id is required.", "alta workflow result");
        if (runs.FindAny(runId) is not { } run)
        {
            // A run that ended before CodeAlta started again: the store kept what its session was given.
            if (runs.TryInfo(runId) is not { } stored) return WorkflowNotFound(context, runId);
            if (await RefuseRunToSandboxAsync(context, stored.SessionId).ConfigureAwait(false) is { } storedRefused) return storedRefused;
            var kept = WorkflowRecord(context, "alta.workflow.result", stored, null);
            kept["text"] = runs.Store?.TryReadResult(runId) ?? $"Run '{runId}' ended ({stored.Status}) before CodeAlta started again; its result was not kept.";
            AltaJsonlWriter.WriteRecord(context.Stdout, kept);
            return AltaExitCodes.Success;
        }

        if (await RefuseRunToSandboxAsync(context, run.SessionId).ConfigureAwait(false) is { } refused) return refused;
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

    // Where the time of a run went: its own time, its steps, and its functions from the slowest in total.
    private static Dictionary<string, object?> Profile(CodeModeRunResult result)
        => new(StringComparer.Ordinal)
        {
            ["durationMs"] = (long)result.Duration.TotalMilliseconds,
            ["scriptMs"] = (long)result.ScriptTime.TotalMilliseconds,
            ["maxCallsAtOnce"] = result.MaxCallsAtOnce,
            ["steps"] = result.Steps.Select(static step => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["stepId"] = step.StepId,
                ["startedAtMs"] = (long)step.StartedAt.TotalMilliseconds,
                ["durationMs"] = (long)step.Duration.TotalMilliseconds,
                ["succeeded"] = step.Succeeded,
            }).ToArray(),
            ["functions"] = result.Calls.GroupBy(static call => call.Path)
                .Select(static group => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = group.Key,
                    ["calls"] = group.Count(),
                    ["failed"] = group.Count(static call => call.Status == CodeModeCallStatus.Failed),
                    ["totalMs"] = (long)group.Sum(static call => call.Duration.TotalMilliseconds),
                    ["slowestMs"] = (long)group.Max(static call => call.Duration.TotalMilliseconds),
                    ["queuedMs"] = (long)group.Sum(static call => call.Queued.TotalMilliseconds),
                })
                .OrderByDescending(static function => (long)function["totalMs"]!).ToArray(),
        };

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
    private static async Task<int?> RefuseRunToSandboxAsync(AltaCommandContext context, string sessionId)
        => !IsCaller(context, sessionId)
           && await AltaSandboxGate.OfCallerAsync(context).ConfigureAwait(false) is not null
           && await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false) is { ExitCode: not AltaExitCodes.Success } refused
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
            ["state"] = info.Running ? "running" : run is null ? info.Status ?? "completed" : run.Outcome switch
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
