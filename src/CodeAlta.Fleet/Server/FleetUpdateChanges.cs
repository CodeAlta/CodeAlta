namespace CodeAlta.Fleet.Server;

/// <summary>What an update campaign a person starts installs, from where, when and on which machines.</summary>
/// <param name="Version">The version; null for the latest every product of the fleet lists on the feed.</param>
/// <param name="Feed">The feed; null for nuget.org.</param>
/// <param name="NotBefore">When it starts; null for now.</param>
/// <param name="TimeZone">The time zone the person chose the time in, for display; null for none.</param>
/// <param name="Machines">The device ids of the nodes; null for all.</param>
/// <param name="Rollback">Whether the person chose a version older than the one machines run.</param>
/// <param name="Prerelease">Whether the latest version may be a prerelease.</param>
public sealed record FleetUpdateCampaignChange(
    string? Version = null,
    string? Feed = null,
    DateTimeOffset? NotBefore = null,
    string? TimeZone = null,
    IReadOnlyList<string>? Machines = null,
    bool Rollback = false,
    bool Prerelease = false);

/// <summary>The update policy of a fleet to sign.</summary>
/// <param name="Mode">The mode (<see cref="Protocol.FleetUpdateModes"/>).</param>
/// <param name="PinnedVersion">The version of a pinned fleet; null otherwise.</param>
/// <param name="Feed">The feed an automatic fleet follows; null for nuget.org.</param>
/// <param name="Window">The start of the daily maintenance window (<c>HH:mm</c>); null for any time.</param>
/// <param name="TimeZone">The time zone of the window; null for UTC.</param>
public sealed record FleetUpdatePolicyChange(string Mode, string? PinnedVersion = null, string? Feed = null, string? Window = null, string? TimeZone = null);
