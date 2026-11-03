using System.Diagnostics;

namespace CodeAlta.Fleet.Update;

/// <summary>Where a switch stood when <see cref="UpdateFollow.FollowAsync"/> stopped following it.</summary>
public enum UpdateFollowOutcome
{
    /// <summary>The new version runs and confirmed that it works.</summary>
    Confirmed,

    /// <summary>The process waits for its work (a node drains): it switches by itself later.</summary>
    Waiting,

    /// <summary>No running process took the switch (an older version): the next start takes it.</summary>
    Unseen,

    /// <summary>The new version was rolled back, or the switch failed.</summary>
    Failed,

    /// <summary>The switch was withdrawn.</summary>
    Withdrawn,

    /// <summary>The new version still runs on trial when the follow ended.</summary>
    StillOnTrial,
}

/// <summary>Follows a switch asked of a running process, from the state of its versions, for <c>update</c>.</summary>
public static class UpdateFollow
{
    /// <summary>How long a switch may stay unseen by a running process before <c>update</c> says so.</summary>
    public static readonly TimeSpan UnseenLimit = TimeSpan.FromSeconds(15);

    /// <summary>Follows a switch until the new version confirmed or was rolled back, the process waits for its work, or the limit passed.</summary>
    /// <param name="store">The folder of versions.</param>
    /// <param name="target">The version switched to.</param>
    /// <param name="limit">How long to follow.</param>
    /// <param name="report">Writes a line for a person: the restart, a wait.</param>
    /// <param name="cancellationToken">Stops following.</param>
    /// <returns>Where the switch stands, and what to tell about it.</returns>
    public static async Task<(UpdateFollowOutcome Outcome, string Message)> FollowAsync(UpdateVersionStore store, UpdateVersion target, TimeSpan limit,
        Func<string, Task> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(report);
        var elapsed = Stopwatch.StartNew();
        var restarting = false;
        string? phase = null;
        while (elapsed.Elapsed < limit)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            var state = store.Read();
            if (state.Pending is { } pending && UpdateVersionStore.Same(pending.Version, target))
            {
                if (pending.Phase is UpdatePhases.Draining or UpdatePhases.WaitingForIdle && pending.SessionsAtWork > 0)
                {
                    return (UpdateFollowOutcome.Waiting, pending.Phase == UpdatePhases.Draining
                        ? $"It waits for {pending.SessionsAtWork} session(s) at work and takes no new work meanwhile (for up to {UpdateDrain.DefaultBound.TotalMinutes:0} minutes, then until the next idle moment)."
                        : $"It takes work again and switches at the next idle moment ({pending.SessionsAtWork} session(s) at work).");
                }

                if (pending.Phase == UpdatePhases.Requested && elapsed.Elapsed > UnseenLimit)
                    return (UpdateFollowOutcome.Unseen, $"The running process has not taken the switch (a version older than its self-update does not): it starts {target} at its next start.");
                if (pending.Phase != phase && pending.Phase == UpdatePhases.BackingUp) await report("It copies its database first...").ConfigureAwait(false);
                phase = pending.Phase;
                continue;
            }

            if (UpdateVersionStore.Same(state.Current, target))
            {
                if (state.Trial is null) return (UpdateFollowOutcome.Confirmed, $"{target} runs and confirmed that it works.");
                if (!restarting)
                {
                    restarting = true;
                    await report($"It stopped and starts {target} on trial...").ConfigureAwait(false);
                }

                continue;
            }

            if (state.LastOutcome is { } outcome && UpdateVersionStore.Same(outcome.Version, target) && outcome.Outcome != UpdateOutcomes.Confirmed)
                return (UpdateFollowOutcome.Failed, $"{target} {(outcome.Outcome == UpdateOutcomes.RolledBack ? "was rolled back" : "failed")}: {outcome.Message}");
            return (UpdateFollowOutcome.Withdrawn, $"The switch to {target} was withdrawn.");
        }

        return (UpdateFollowOutcome.StillOnTrial, $"{target} has not confirmed yet.");
    }
}
