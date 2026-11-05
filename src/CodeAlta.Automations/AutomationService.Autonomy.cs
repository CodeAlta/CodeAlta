using System.Runtime.CompilerServices;
using CodeAlta.Automations.Integrations;

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
    internal string? TakeOver(string runId) => TakeOver(runId, null);

    /// <summary>A person takes a run of a given automation over, as <see cref="TakeOver(string)"/> does.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="automationId">
    /// The automation the run must be of: the one whose approvers or owner were checked for the request; null for a person at
    /// this machine, who may take any run over.
    /// </param>
    /// <returns>The session of the run; null when no run of that automation with autonomy is in progress with this id.</returns>
    internal string? TakeOver(string runId, string? automationId)
    {
        AutomationRunGuard? guard;
        lock (_gate) _guards.TryGetValue(runId, out guard);
        if (guard is null || automationId is not null && !string.Equals(guard.AutomationId, automationId, StringComparison.Ordinal)) return null;
        guard.TakeOver();
        var run = _state.Update(runId, current => current with { Message = "Taken over by a person." });
        Changed?.Invoke();
        return run?.SessionId;
    }

    /// <summary>How long a run waits for its on-call tool to say who is on call before it starts without knowing.</summary>
    internal static readonly TimeSpan OnCallWait = TimeSpan.FromSeconds(10);

    // Reads, before a run starts, who its on-call tool says is on call, with the token this machine holds: what the run tells
    // its server when it waits, which asks that person if they answer its approvals. A tool that does not answer leaves the
    // run to the rotation of its team.
    private async Task ReadOnCallAsync(AutomationEntry entry, string runId, CancellationToken cancellationToken)
    {
        if (await ReadOnCallPeopleAsync(entry, cancellationToken).ConfigureAwait(false) is { } people) _state.Update(runId, run => run with { OnCall = people });
    }

    // Who the on-call tool of an automation says is on call now; null when it says nobody, does not answer, or none is set.
    private async Task<IReadOnlyList<string>?> ReadOnCallPeopleAsync(AutomationEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Definition.Autonomy is not { Approvers: AutomationAutonomy.OnCallApprovers, OnCall: { } onCall } || Integrations is not { } integrations) return null;
        var (token, problem) = Resolve(entry.ProjectId, onCall.Token);
        IReadOnlyList<string>? people = null;
        if (token is not null)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(OnCallWait);
            try
            {
                var read = await new OnCallClient(integrations.Http, integrations.IncidentApi, integrations.PagerDutyApi, integrations.OpsgenieApi)
                    .OnCallAsync(onCall.Source, onCall.Schedule, token, onCall.Region, _time.GetUtcNow(), wait.Token).ConfigureAwait(false);
                problem = read.Problem;
                people = read.Value is { } found ? [.. found.SelectMany(static person => new[] { person.Email, person.Name }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)] : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                problem = $"it did not answer within {OnCallWait.TotalSeconds:0} seconds";
            }
        }

        if (problem is not null)
            integrations.Log?.Invoke($"Who is on call for '{entry.Definition.Name}' could not be read from {onCall.Source}: {problem}. Its approvals go to the rotation of its team.");
        return people is { Count: > 0 } ? people : null;
    }

    // A run that starts to wait for an approval, when its approvals go to the person on call: who is on call is read again first,
    // and the approval is told only then, with that person, or with nobody when the tool names nobody or does not answer (the
    // rotation of its team): never with the person on call for an earlier approval. A wait that ended meanwhile is not told.
    private async Task WaitingOnCallAsync(AutomationEntry entry, string runId, AutomationRunGuard guard, StrongBox<int> changes, int change)
    {
        IReadOnlyList<string>? people;
        try
        {
            people = await ReadOnCallPeopleAsync(entry, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        if (Volatile.Read(ref changes.Value) != change || !guard.Waiting) return;
        _state.Update(runId, run => run.InProgress ? run with { Status = AutomationRun.Waiting, Approval = Guid.CreateVersion7().ToString("N"), OnCall = people } : run);
        Changed?.Invoke();
    }

    /// <summary>Whether a person took a run in progress over.</summary>
    internal bool IsTakenOver(string runId)
    {
        lock (_gate) return _guards.TryGetValue(runId, out var guard) && guard.TakenOver;
    }

    // Follows the guard of a run while it works: its status says when it waits for a person, a try keeps the calls it
    // recorded, and a stop keeps why.
    private void Follow(AutomationEntry entry, string runId, AutomationRunGuard guard)
    {
        lock (_gate) _guards[runId] = guard;
        var changes = new StrongBox<int>();
        guard.WaitingChanged += waiting =>
        {
            var change = Interlocked.Increment(ref changes.Value);
            // Each time the run starts to wait, an approval of its own (its server tells a person of each one), and who is on
            // call is read again before it is told: the person of the rotation now, not the one of the start or of an earlier approval.
            if (waiting && entry.Definition.Autonomy is { Approvers: AutomationAutonomy.OnCallApprovers })
            {
                _ = WaitingOnCallAsync(entry, runId, guard, changes, change);
                return;
            }

            _state.Update(runId, run => run.InProgress
                ? run with { Status = waiting ? AutomationRun.Waiting : AutomationRun.Running, Approval = waiting ? Guid.CreateVersion7().ToString("N") : null }
                : run);
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
