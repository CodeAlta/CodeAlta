using System.Text;
using CodeAlta.CodeMode;

namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>A resume or a rerun that may start: the script to run, what it replays, and the run it comes from.</summary>
/// <param name="Source">The source that runs, and that the new run keeps: the kept one, or the changed code of a rerun.</param>
/// <param name="Replay">The recorded steps it replays, and how.</param>
/// <param name="Run">The run it resumes or reruns.</param>
public sealed record CodeModeResumePlan(string Source, CodeModeReplay Replay, CodeModeStoredRun Run);

/// <summary>
/// Resumes and reruns of a run over what the store recorded (<see cref="CodeModeRunStore"/>): <c>exec</c> plans them from the
/// options <c>resume</c>, <c>replay</c>, <c>from</c> and <c>rerun_in_flight</c>, and the host asks a session for one with
/// <see cref="Prompt"/>. Never automatic: a session runs it, with its tools and its permissions.
/// </summary>
public static class CodeModeResume
{
    private const int MaxListedCalls = 3;

    /// <summary>
    /// Plans the resume or the rerun the options of a source ask for. A resume (<c>replay</c> <c>resume</c>, the default) runs
    /// the kept script, unchanged; a rerun (<c>"replay": "all"</c>, or <c>from</c>) may run changed code over the recorded
    /// results. It is refused for a run the store does not have for this session, one that still runs, a changed script, or
    /// a step that would run again a call that was in flight and is not idempotent, unless <c>rerun_in_flight</c> says so.
    /// </summary>
    /// <param name="store">The store of the host, or null for a host that keeps no runs.</param>
    /// <param name="sessionId">The session that resumes.</param>
    /// <param name="code">The code <c>exec</c> was given: only the options line resumes the kept script.</param>
    /// <param name="options">The options of its first line; <see cref="CodeModeExecOptions.Resume"/> names the run.</param>
    /// <param name="refusal">Why it is refused.</param>
    /// <returns>The plan, or null when <paramref name="refusal"/> is set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/>, <paramref name="code"/> or <paramref name="options"/> is null.</exception>
    public static CodeModeResumePlan? TryPlan(CodeModeRunStore? store, string sessionId, string code, CodeModeExecOptions options, out string? refusal)
        => TryPlan(store, sessionId, code, options, running: null, out refusal);

    /// <summary>
    /// Plans the resume or the rerun the options of a source ask for, as <see cref="TryPlan(CodeModeRunStore?, string, string, CodeModeExecOptions, out string?)"/>
    /// does, with what the process knows of the run: one that ended here a moment ago may not have its end written yet.
    /// </summary>
    /// <param name="store">The store of the host, or null for a host that keeps no runs.</param>
    /// <param name="sessionId">The session that resumes.</param>
    /// <param name="code">The code <c>exec</c> was given: only the options line resumes the kept script.</param>
    /// <param name="options">The options of its first line; <see cref="CodeModeExecOptions.Resume"/> names the run.</param>
    /// <param name="running">Says whether a run of this process still runs, or null for a run it does not know; null for none.</param>
    /// <param name="refusal">Why it is refused.</param>
    /// <returns>The plan, or null when <paramref name="refusal"/> is set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/>, <paramref name="code"/> or <paramref name="options"/> is null.</exception>
    public static CodeModeResumePlan? TryPlan(CodeModeRunStore? store, string sessionId, string code, CodeModeExecOptions options, Func<string, bool?>? running,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(options);
        refusal = null;
        if (options.Resume is not { } runId)
        {
            refusal = "`resume` names the run to resume.";
            return null;
        }

        if (store is null)
        {
            refusal = "This CodeAlta keeps no record of runs: a run cannot be resumed here.";
            return null;
        }

        if (store.TryLoad(runId) is not { } run || !string.Equals(run.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            refusal = $"No run `{runId}` of this session in the record of runs: it is not one of this session's, or it is older than what CodeAlta keeps.";
            return null;
        }

        if (running?.Invoke(runId) ?? run.EndedAt is null)
        {
            refusal = $"Run `{runId}` still runs: wait for it, or stop it, before running it again.";
            return null;
        }

        string? from = null;
        if (options.From is { } typed && (from = ResolveStep(run, typed, out refusal)) is null)
        {
            return null;
        }

        var mode = from is not null ? CodeModeReplayMode.UntilStep : options.Replay == "all" ? CodeModeReplayMode.All : CodeModeReplayMode.Resume;
        var replay = new CodeModeReplay(run.Steps, mode, from);

        var source = run.Source;
        if (CodeModeSource.BodyOf(code).Length > 0)
        {
            // A resume runs the script that ran; a rerun over the recorded results runs changed code, which is its point.
            if (mode == CodeModeReplayMode.Resume && CodeModeSource.BodyHashOf(code) != run.SourceHash)
            {
                refusal = $"The code differs from the script of run `{runId}`: a changed script does not resume. Send only the options line to resume it as it ran, or rerun the changed code over its recorded results with \"replay\": \"all\".";
                return null;
            }

            source = code;
        }

        if (!options.RerunInFlight && InFlightRerun(run, replay) is { Count: > 0 } calls)
        {
            refusal = Describe(run.Id, calls);
            return null;
        }

        return new CodeModeResumePlan(source, replay, run);
    }

    /// <summary>
    /// Finds the recorded step a rerun from a step names: its full key (<c>lib#0/merge#1</c>), its full id, its path without the numbers of the calls (<c>lib/merge</c>), or its bare id
    /// (<c>merge</c>) when one full id only ends with it (<see cref="CodeModeReplay.ResolveKey"/>).
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="typed">What names the step.</param>
    /// <param name="problem">Why no step was found: none has it, or several do.</param>
    /// <returns>The full key, or null when <paramref name="problem"/> is set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> or <paramref name="typed"/> is null.</exception>
    public static string? ResolveStep(CodeModeStoredRun run, string typed, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(typed);
        problem = null;
        if (CodeModeReplay.ResolveKey(typed, run.Steps.Select(static step => step.Key), out var candidates) is { } key)
        {
            return key;
        }

        if (candidates.Count > 1)
        {
            problem = $"Run `{run.Id}` recorded several steps `{typed.Trim()}`: name one by its path, such as {string.Join(", ", candidates.Select(static id => $"`{id}`"))}.";
            return null;
        }

        var keys = run.Steps.Select(static step => step.Key).Take(16).ToArray();
        problem = $"Run `{run.Id}` recorded no step `{CodeModeReplay.NormalizeKey(typed)}`.{(keys.Length == 0 ? " It recorded no step." : $" Its steps: {string.Join(", ", keys)}.")}";
        return null;
    }

    /// <summary>
    /// The prompt that asks the session of a run to resume it, or to rerun it over its recorded results: one <c>exec</c> call
    /// with the options line only, then a short answer. It tells the session to ask the user before it reruns a call that was
    /// in flight.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="fromKey">The step from which a rerun runs live, or null.</param>
    /// <param name="all">Whether every recorded step replays (<c>"replay": "all"</c>); false resumes.</param>
    /// <returns>The prompt.</returns>
    /// <exception cref="ArgumentException"><paramref name="runId"/> is not the id of a run.</exception>
    public static string Prompt(string runId, string? fromKey, bool all)
    {
        if (!CodeModeRunStore.IsRunId(runId))
        {
            throw new ArgumentException($"'{runId}' is not the id of a run.", nameof(runId));
        }

        var options = new StringBuilder("{\"resume\": ").Append(Quote(runId));
        string what;
        if (fromKey is { Length: > 0 } from)
        {
            options.Append(", \"from\": ").Append(Quote(CodeModeReplay.NormalizeKey(from)));
            what = $"Rerun the script of the run `{runId}` from its step {CodeModeReplay.Describe(from)}: the steps before it give the results they recorded, and that step and what follows run again.";
        }
        else if (all)
        {
            options.Append(", \"replay\": \"all\"");
            what = $"Rerun the script of the run `{runId}` over its recorded results: every step it recorded gives its result again without running, and only the rest of the code runs.";
        }
        else
        {
            what = $"Resume the run `{runId}`, which stopped before its end: the steps it completed give their recorded results, and the script goes on from there.";
        }

        options.Append(", \"yield_ms\": 600000}");
        return $"""
            {what} Call `exec` with this code only, as it is written: CodeAlta takes the script, its arguments and its recorded results from the run.

            ```js
            // @exec: {options}
            ```

            Wait for it to end, then answer with its result in a few lines. If `exec` refuses because a call was in flight when the run stopped, do not add `"rerun_in_flight": true` yourself: ask the user whether that call may run again, and add it only if they agree.
            """;
    }

    // The calls in flight, of functions that are not idempotent, whose step runs again: outside any step, or in a step the
    // replay does not give back. The steps replayed are those of the unchanged prefix, or every replayable one for "all".
    private static List<CodeModeStoredCall> InFlightRerun(CodeModeStoredRun run, CodeModeReplay replay)
    {
        if (run.InFlightCalls.Count == 0)
        {
            return [];
        }

        var replayed = new HashSet<string>(StringComparer.Ordinal);
        if (replay.Mode == CodeModeReplayMode.All)
        {
            replayed.UnionWith(run.Steps.Where(static step => step.Replayable).Select(static step => step.Key));
        }
        else
        {
            var byIndex = run.Steps.ToDictionary(static step => step.Index);
            var children = run.Steps.Where(static step => step.ParentIndex is not null).ToLookup(static step => step.ParentIndex!.Value);
            var covered = new HashSet<int>();
            var end = run.Steps.Count == 0 ? 0 : run.Steps[^1].Index + 1;
            for (var index = 0; index < end; index++)
            {
                if (covered.Contains(index))
                {
                    continue;
                }

                if (!byIndex.TryGetValue(index, out var step) || !step.Replayable || step.Key == replay.UntilKey)
                {
                    break;
                }

                Cover(step);
            }

            void Cover(CodeModeReplayStep step)
            {
                covered.Add(step.Index);
                replayed.Add(step.Key);
                foreach (var child in children[step.Index])
                {
                    Cover(child);
                }
            }
        }

        return [.. run.InFlightCalls.Where(call => call.StepKey is null || !replayed.Contains(call.StepKey))];
    }

    private static string Describe(string runId, List<CodeModeStoredCall> calls)
    {
        var listed = string.Join("; ", calls.Take(MaxListedCalls).Select(static call =>
            $"`{call.Path}` {(call.StepKey is null ? "outside any step" : $"in step {CodeModeReplay.Describe(call.StepKey)}")} with {call.Arguments}"));
        var more = calls.Count > MaxListedCalls ? $" and {calls.Count - MaxListedCalls} more" : string.Empty;
        return $"Run `{runId}` stopped while {(calls.Count == 1 ? "a call" : "calls")} that may not run twice {(calls.Count == 1 ? "was" : "were")} in flight: {listed}{more}. "
            + "Its outcome is unknown, and running the step again runs the call again. Ask the user; if they agree, add \"rerun_in_flight\": true to the options.";
    }

    private static string Quote(string value) => "\"" + System.Text.Json.JsonEncodedText.Encode(value) + "\"";
}
