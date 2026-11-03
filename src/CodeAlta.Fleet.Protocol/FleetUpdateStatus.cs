namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The versions of a node or a server and where its self-update stands: what the node API (<c>node.info</c>,
/// <c>control.status</c>) and the server's REST API (<c>GET /api/v1/server/update</c>) report.
/// </summary>
/// <param name="Running">The version that runs.</param>
/// <param name="Installed">The version of the installed entry point (the tool, what the service starts); null when unknown.</param>
/// <param name="Selected">The version the pointer selects; null when the installed entry point runs itself.</param>
/// <param name="Previous">The version a rollback goes back to; null for the installed entry point.</param>
/// <param name="Trial">The selected version while it has not confirmed that it works; null otherwise.</param>
/// <param name="Pending">The switch that waits; null when none waits.</param>
/// <param name="Bad">The versions rolled back on this machine.</param>
/// <param name="LastOutcome">How the last switch ended; null before the first.</param>
public sealed record FleetUpdateStatus(
    string Running,
    string? Installed,
    string? Selected,
    string? Previous,
    FleetUpdateTrial? Trial,
    FleetUpdatePending? Pending,
    IReadOnlyList<FleetUpdateBadVersion> Bad,
    FleetUpdateOutcome? LastOutcome);

/// <summary>A version on trial.</summary>
/// <param name="Version">The version.</param>
/// <param name="SelectedAt">When it was selected.</param>
/// <param name="Starts">How many times it was started.</param>
public sealed record FleetUpdateTrial(string Version, DateTimeOffset SelectedAt, int Starts);

/// <summary>A switch that waits.</summary>
/// <param name="Version">The version.</param>
/// <param name="Phase">Where it stands: <c>requested</c>, <c>draining</c>, <c>waiting_idle</c>, <c>backing_up</c>, <c>switching</c>.</param>
/// <param name="Since">Since when.</param>
/// <param name="SessionsAtWork">The sessions a drain waits for.</param>
/// <param name="Now">Whether it does not wait for running work.</param>
/// <param name="RequestedBy">Who asked for it: <c>local</c> (the owner on the machine) or a campaign of the fleet; null when unknown.</param>
public sealed record FleetUpdatePending(string Version, string Phase, DateTimeOffset Since, int SessionsAtWork, bool Now, string? RequestedBy = null);

/// <summary>A version rolled back on this machine.</summary>
/// <param name="Version">The version.</param>
/// <param name="At">When.</param>
/// <param name="Reason">Why.</param>
public sealed record FleetUpdateBadVersion(string Version, DateTimeOffset At, string Reason);

/// <summary>How a switch ended.</summary>
/// <param name="Version">The version switched to.</param>
/// <param name="Outcome"><c>confirmed</c>, <c>rolled_back</c> or <c>failed</c>.</param>
/// <param name="At">When.</param>
/// <param name="Message">What happened; null when it went well.</param>
public sealed record FleetUpdateOutcome(string Version, string Outcome, DateTimeOffset At, string? Message);
