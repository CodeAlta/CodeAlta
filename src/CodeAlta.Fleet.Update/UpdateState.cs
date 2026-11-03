using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Update;

/// <summary>
/// The versions of a machine, as <c>state.json</c> of its folder of versions holds them: the pointer, the version on
/// trial, the switch that waits, the versions rolled back. The installed entry point (the tool's shim, what the service
/// starts) runs the version the pointer selects; without one, it runs itself.
/// </summary>
public sealed record UpdateState
{
    /// <summary>Gets the version the pointer selects (a folder of versions); null for the installed entry point itself.</summary>
    public string? Current { get; init; }

    /// <summary>Gets the version a rollback goes back to; null for the installed entry point.</summary>
    public string? Previous { get; init; }

    /// <summary>Gets the selected version while it has not confirmed that it works; null once confirmed.</summary>
    public UpdateTrial? Trial { get; init; }

    /// <summary>Gets the switch to a staged version that waits (for an idle moment, a backup); null when none waits.</summary>
    public UpdatePending? Pending { get; init; }

    /// <summary>
    /// Gets the rollback whose backup is not restored yet: a launcher restores it before it starts any version, and starts
    /// none while it cannot; null when none waits.
    /// </summary>
    public UpdateRollback? RollingBack { get; init; }

    /// <summary>Gets the versions rolled back on this machine, which an update skips unless it names them.</summary>
    public IReadOnlyList<UpdateBadVersion> Bad { get; init; } = [];

    /// <summary>Gets how the last switch ended; null before the first.</summary>
    public UpdateOutcome? LastOutcome { get; init; }

    /// <summary>Gets whether a version was rolled back on this machine.</summary>
    /// <param name="version">The version.</param>
    /// <returns>Whether it is marked bad.</returns>
    public bool IsBad(UpdateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Bad.Any(bad => UpdateVersion.TryParse(bad.Version, out var marked) && marked.Equals(version));
    }
}

/// <summary>A selected version on trial: it confirms itself once healthy, or is rolled back.</summary>
/// <param name="Version">The version.</param>
/// <param name="SelectedAt">When it was selected.</param>
/// <param name="Starts">How many times a launcher started it so far.</param>
/// <param name="Backup">The backup taken before the switch (the server's database), restored by a rollback; null for none.</param>
public sealed record UpdateTrial(string Version, DateTimeOffset SelectedAt, int Starts, string? Backup = null);

/// <summary>A switch that waits: asked for by <c>update</c>, carried out by the running process when it may.</summary>
/// <param name="Version">The staged version to switch to.</param>
/// <param name="RequestedAt">When it was asked for.</param>
/// <param name="Now">Whether the switch does not wait for running work (<c>--now</c>).</param>
/// <param name="Phase">Where it stands (<see cref="UpdatePhases"/>).</param>
/// <param name="PhaseSince">Since when it stands there.</param>
/// <param name="SessionsAtWork">How many sessions the node waits for, when it drains.</param>
/// <param name="RequestedBy">Who asked: <c>local</c> (the owner on the machine) or what the fleet names.</param>
public sealed record UpdatePending(string Version, DateTimeOffset RequestedAt, bool Now, string Phase, DateTimeOffset PhaseSince, int SessionsAtWork = 0,
    string? RequestedBy = null);

/// <summary>
/// A rollback whose backup is not restored yet. It is recorded with the move of the pointer and kept until the restore
/// succeeds, so that a crash or a failed restore in between is taken up again at the next start.
/// </summary>
/// <param name="Trial">The trial rolled back, with its backup.</param>
/// <param name="At">When it was rolled back.</param>
/// <param name="Reason">Why, for a person.</param>
/// <param name="Error">Why the last restore failed; null before a restore failed.</param>
public sealed record UpdateRollback(UpdateTrial Trial, DateTimeOffset At, string Reason, string? Error = null);

/// <summary>A version rolled back on this machine.</summary>
/// <param name="Version">The version.</param>
/// <param name="At">When.</param>
/// <param name="Reason">Why, for a person.</param>
public sealed record UpdateBadVersion(string Version, DateTimeOffset At, string Reason);

/// <summary>How a switch ended.</summary>
/// <param name="Version">The version switched to.</param>
/// <param name="Outcome">The outcome (<see cref="UpdateOutcomes"/>).</param>
/// <param name="At">When.</param>
/// <param name="Message">What happened, for a person; null when it went well.</param>
public sealed record UpdateOutcome(string Version, string Outcome, DateTimeOffset At, string? Message = null);

/// <summary>The phases of a pending switch.</summary>
public static class UpdatePhases
{
    /// <summary>Asked for; the running process has not taken it yet (or none runs).</summary>
    public const string Requested = "requested";

    /// <summary>The node takes no new work and waits for the sessions at work, within a bound.</summary>
    public const string Draining = "draining";

    /// <summary>The bound of the drain passed: the node takes work again and switches at the next idle moment.</summary>
    public const string WaitingForIdle = "waiting_idle";

    /// <summary>The server copies its database before the switch.</summary>
    public const string BackingUp = "backing_up";

    /// <summary>The process stops to start the new version.</summary>
    public const string Switching = "switching";
}

/// <summary>The outcomes of a switch.</summary>
public static class UpdateOutcomes
{
    /// <summary>The new version started and confirmed that it works.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>The new version did not confirm in time or crashed: the previous one runs again, the new one is marked bad.</summary>
    public const string RolledBack = "rolled_back";

    /// <summary>The switch did not happen (the staged version went missing, the backup failed).</summary>
    public const string Failed = "failed";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateState))]
[JsonSerializable(typeof(UpdateSourceSettings))]
[JsonSerializable(typeof(StagedVersionInfo))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
