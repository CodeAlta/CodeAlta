using System.Globalization;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Update;

/// <summary>Describes the versions of a machine: as the APIs report them (<see cref="FleetUpdateStatus"/>), and for a person.</summary>
public static class UpdateStatusReport
{
    /// <summary>Describes the state of the versions.</summary>
    /// <param name="state">The state.</param>
    /// <param name="running">The version that runs.</param>
    /// <param name="installed">The version of the installed entry point; null when unknown.</param>
    /// <returns>The status.</returns>
    public static FleetUpdateStatus Describe(UpdateState state, string running, string? installed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(running);
        return new FleetUpdateStatus(running, installed, state.Current, state.Previous,
            state.Trial is { } trial ? new FleetUpdateTrial(trial.Version, trial.SelectedAt, trial.Starts) : null,
            state.Pending is { } pending ? new FleetUpdatePending(pending.Version, pending.Phase, pending.PhaseSince, pending.SessionsAtWork, pending.Now) : null,
            [.. state.Bad.Select(static bad => new FleetUpdateBadVersion(bad.Version, bad.At, bad.Reason))],
            state.LastOutcome is { } outcome ? new FleetUpdateOutcome(outcome.Version, outcome.Outcome, outcome.At, outcome.Message) : null);
    }

    /// <summary>Describes the status for a person, a few lines.</summary>
    /// <param name="status">The status.</param>
    /// <param name="command">The executable (<c>altanode</c>, <c>altaserver</c>), for the hints.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Lines(FleetUpdateStatus status, string command)
    {
        ArgumentNullException.ThrowIfNull(status);
        var lines = new List<string>();
        var origin = status.Selected is null
            ? "the installed entry point"
            : $"selected by {command} update{(status.Installed is { } installed ? $", installed {installed}" : "")}{(status.Previous is { } previous ? $", previous {previous}" : "")}";
        lines.Add($"Version: {status.Running} ({origin}).");
        if (status.Trial is { } trial)
            lines.Add($"Update: {trial.Version} is on trial since {Local(trial.SelectedAt)} ({trial.Starts} start(s)); it is rolled back if it does not confirm that it works.");
        if (status.Pending is { } pending)
        {
            var phase = pending.Phase switch
            {
                UpdatePhases.Requested => "asked for; the running process takes it within seconds (or the next start)",
                UpdatePhases.Draining => $"no new work is taken; waiting for {pending.SessionsAtWork} session(s) at work",
                UpdatePhases.WaitingForIdle => $"taking work again; switches at the next idle moment ({pending.SessionsAtWork} session(s) at work)",
                UpdatePhases.BackingUp => "the database is being copied",
                UpdatePhases.Switching => "switching",
                _ => pending.Phase,
            };
            lines.Add($"Update: switch to {pending.Version}{(pending.Now ? " (now)" : "")}: {phase}, since {Local(pending.Since)}.");
        }

        if (status.LastOutcome is { } outcome && status.Trial is null && status.Pending is null)
        {
            lines.Add(outcome.Outcome switch
            {
                UpdateOutcomes.Confirmed => $"Last update: {outcome.Version}, confirmed {Local(outcome.At)}.",
                UpdateOutcomes.RolledBack => $"Last update: {outcome.Version} was rolled back {Local(outcome.At)}: {outcome.Message}",
                _ => $"Last update: {outcome.Version} failed {Local(outcome.At)}: {outcome.Message}",
            });
        }

        if (status.Bad.Count > 0) lines.Add($"Rolled back on this machine: {string.Join(", ", status.Bad.Select(static bad => bad.Version))} (skipped unless named with --version).");
        return lines;
    }

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
