namespace CodeAlta.Automations;

// The runs of automations with autonomy (doc/fleet/orchestration.md, "Autonomy and approvals"): a run waits while a call
// it may not make by itself waits for a person; a try records the calls of its allowed tools; an approval that waited
// too long stops the run, unless a person took it over.
internal sealed partial class AutomationService
{
    /// <summary>The trigger of a run that tries an automation on a sample event: a dry run.</summary>
    internal const string TryTrigger = "try";

    private readonly Dictionary<string, AutomationRunGuard> _guards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _stoppedWhy = new(StringComparer.Ordinal);

    /// <summary>
    /// A person takes a run over: what it waits for waits for them as long as they take, and no approval stops it any
    /// more; they answer its approvals and drive its session.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <returns>The session of the run; null when no run of an automation with autonomy is in progress with this id.</returns>
    internal string? TakeOver(string runId)
    {
        AutomationRunGuard? guard;
        lock (_gate) _guards.TryGetValue(runId, out guard);
        if (guard is null) return null;
        guard.TakeOver();
        var run = _state.Update(runId, current => current with { Message = "Taken over by a person." });
        Changed?.Invoke();
        return run?.SessionId;
    }

    /// <summary>Whether a person took a run in progress over.</summary>
    internal bool IsTakenOver(string runId)
    {
        lock (_gate) return _guards.TryGetValue(runId, out var guard) && guard.TakenOver;
    }

    // Follows the guard of a run while it works: its status says when it waits for a person, a try keeps the calls it
    // recorded, and a stop keeps why.
    private void Follow(string runId, AutomationRunGuard guard)
    {
        lock (_gate) _guards[runId] = guard;
        guard.WaitingChanged += waiting =>
        {
            _state.Update(runId, run => run.InProgress ? run with { Status = waiting ? AutomationRun.Waiting : AutomationRun.Running } : run);
            Changed?.Invoke();
        };
        guard.Recorded += call =>
        {
            _state.Update(runId, run => run with { Calls = [.. (run.Calls ?? []).TakeLast(AutomationRun.MaximumCalls - 1), AutomationText.Line(call, 2000)] });
            Changed?.Invoke();
        };
        guard.Stopped += why =>
        {
            lock (_gate) _stoppedWhy[runId] = why;
        };
    }
}
