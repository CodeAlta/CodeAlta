using CodeAlta.Automations;
using CodeAlta.Desktop.Fleet;

namespace CodeAlta.Desktop;

/// <summary>What a take-over of a run on a machine of a server came to.</summary>
/// <param name="Problem">Why it was not passed on, or why its machine refused it; null otherwise.</param>
/// <param name="Confirmed">Whether its machine said it took the run over: only then is the run the person's.</param>
internal sealed record AutomationTakeOverOutcome(string? Problem, bool Confirmed)
{
    /// <summary>The status of the take-over for the window, <c>alta</c> and the phone.</summary>
    /// <returns><c>ok</c> once confirmed, <c>requested</c> when not, <c>refused</c> with why.</returns>
    internal (string Status, string? Message, string? SessionId) Status()
        => Problem is not null ? ("refused", Problem, null) : Confirmed ? ("ok", null, null) : ("requested", AutomationTakeOvers.NotConfirmed, null);
}

/// <summary>
/// Takes a run of an automation with autonomy over, for a person: the window, the phone. A run on this computer is taken
/// over here; one on a machine of a server through that server, with a request this computer signs.
/// </summary>
internal static class AutomationTakeOvers
{
    /// <summary>The most runs of this computer looked through for a run named by its id alone.</summary>
    private const int RunsLookedAt = 1000;

    /// <summary>Takes a run over.</summary>
    /// <param name="automations">The automations of the application.</param>
    /// <param name="placement">What places automations on machines; null without a fleet.</param>
    /// <param name="automationId">The automation of the run; null to find it from the run.</param>
    /// <param name="runId">The run.</param>
    /// <param name="cancellationToken">Cancels before the request is passed on.</param>
    /// <returns>
    /// <c>ok</c> with the session of a run of this computer, or once its machine confirmed it; <c>requested</c> when its machine
    /// did not confirm it; <c>not_found</c>; or <c>refused</c> with why.
    /// </returns>
    internal static async Task<(string Status, string? Message, string? SessionId)> TakeOverAsync(AutomationService automations, DesktopAutomationPlacement? placement,
        string? automationId, string runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(automations);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var entry = automationId is not null ? automations.Snapshot.Find(automationId) : Find(automations, placement, runId);
        if (entry is null) return ("not_found", null, null);
        if (entry.Definition.IsPlaced)
        {
            if (placement is null) return ("refused", "Its machine is reached from no fleet here.", null);
            return (await placement.TakeOverAsync(entry, runId, cancellationToken).ConfigureAwait(false)).Status();
        }

        return automations.TakeOver(runId) is { } sessionId ? ("ok", null, sessionId)
            : ("refused", "This run is not in progress, or its automation does not decide what its runs do without a person.", null);
    }

    /// <summary>The message of a take-over its machine did not confirm.</summary>
    internal const string NotConfirmed = "Asked: its machine has not confirmed the take-over yet. Until it does, an approval that waits too long may still stop the run.";

    // The automation of a run: one of this computer, or one placed on a machine whose runs it reported.
    private static AutomationEntry? Find(AutomationService automations, DesktopAutomationPlacement? placement, string runId)
    {
        if (automations.Runs(null, RunsLookedAt).FirstOrDefault(run => run.Id == runId) is { } local) return automations.Snapshot.Find(local.AutomationId);
        if (placement is null) return null;
        return automations.Snapshot.Entries.FirstOrDefault(entry => entry.Definition.IsPlaced && placement.Runs(entry.Id).Any(run => run.Id == runId));
    }
}
