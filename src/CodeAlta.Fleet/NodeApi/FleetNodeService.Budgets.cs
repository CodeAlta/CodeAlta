using System.Collections.Concurrent;
using CodeAlta.Catalog;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>A run of the node API that the budgets of the machine admit before its prompt is sent.</summary>
/// <param name="RunId">The run: the session for a new one, the retry key of a send.</param>
/// <param name="Title">What the run is, for the owner.</param>
/// <param name="SpaceId">The space of the session.</param>
/// <param name="Project">Its project; null for a chat.</param>
public sealed record FleetNodeBudgetRun(string RunId, string? Title, string? SpaceId, ProjectDescriptor? Project);

/// <summary>The statuses of a send that the budgets of the machine decided.</summary>
public static class FleetBudgetCommandStatuses
{
    /// <summary>The send waits for its owner to continue it anyway: it is sent then, or never when it is stopped.</summary>
    public const string Held = "held";

    /// <summary>A budget is reached and stops new runs: the prompt was not sent.</summary>
    public const string Reached = "budget_reached";
}

public sealed partial class FleetNodeService
{
    private const int MaxBudgetAdmitted = 4096;

    // The sends that wait for a person, by their retry key: a retry while one waits answers that it waits.
    private readonly ConcurrentDictionary<string, byte> _budgetHolds = new(StringComparer.Ordinal);

    // The sends the budgets admitted, by their retry key: a retry after a lost answer is the runtime's replay, not a new run.
    private readonly ConcurrentDictionary<string, byte> _budgetAdmitted = new(StringComparer.Ordinal);

    // Sends a prompt once the budgets of the machine admit it: at once when they do, never when one stops it, and later when one
    // holds it (the answer says it waits; the send is made once its owner continues it anyway).
    private async Task<FleetCommandResult> WithinBudgetAsync(string runId, SessionViewDescriptor session, string? title, Func<FleetCommandResult> send)
    {
        if (_options.AdmitBudget is not { } admit || _budgetAdmitted.ContainsKey(runId)) return send();
        if (_budgetHolds.ContainsKey(runId)) return new FleetCommandResult(FleetBudgetCommandStatuses.Held, Code: FleetBudgetCommandStatuses.Held);
        var project = session.ProjectRef is { Length: > 0 } projectId ? await _host.ProjectCatalog.GetByIdAsync(projectId).ConfigureAwait(false) : null;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new FleetNodeBudgetRun(runId, title ?? session.Title, session.SpaceId, project);
        var admission = admit(run, () => held.TrySetResult(), CancellationToken.None);
        if (await Task.WhenAny(admission, held.Task).ConfigureAwait(false) == admission)
        {
            var decided = await admission.ConfigureAwait(false);
            if (decided.Problem is { } problem)
            {
                LogInfo($"The prompt of the session {session.SessionId} was not sent: {problem}");
                return new FleetCommandResult(FleetBudgetCommandStatuses.Reached, Code: FleetBudgetCommandStatuses.Reached);
            }

            return Admitted(runId, send);
        }

        if (!_budgetHolds.TryAdd(runId, 0)) return new FleetCommandResult(FleetBudgetCommandStatuses.Held, Code: FleetBudgetCommandStatuses.Held);
        LogInfo($"The prompt of the session {session.SessionId} waits for its budget.");
        _ = SendOnceAdmittedAsync(runId, session.SessionId, admission, send);
        return new FleetCommandResult(FleetBudgetCommandStatuses.Held, Code: FleetBudgetCommandStatuses.Held);
    }

    private async Task SendOnceAdmittedAsync(string runId, string sessionId, Task<Server.FleetBudgetAdmission> admission, Func<FleetCommandResult> send)
    {
        try
        {
            var decided = await admission.ConfigureAwait(false);
            if (decided.Problem is { } problem)
            {
                LogInfo($"The prompt of the session {sessionId} was not sent: {problem}");
                return;
            }

            var result = Admitted(runId, send);
            LogInfo($"The prompt of the session {sessionId} was sent once its budget let it ({result.Status}).");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException or OperationCanceledException)
        {
            LogInfo($"The prompt of the session {sessionId} that waited for its budget was not sent: {exception.Message}");
        }
        finally
        {
            _budgetHolds.TryRemove(runId, out _);
        }
    }

    private FleetCommandResult Admitted(string runId, Func<FleetCommandResult> send)
    {
        // Bounded: the runtime keeps its own receipts for retries; this only spares them a second admission.
        if (_budgetAdmitted.Count >= MaxBudgetAdmitted) _budgetAdmitted.Clear();
        _budgetAdmitted[runId] = 0;
        return send();
    }
}
