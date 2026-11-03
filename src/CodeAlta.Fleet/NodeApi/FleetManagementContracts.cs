namespace CodeAlta.Fleet.NodeApi;

/// <summary>The parameters of <c>control.log</c>.</summary>
/// <param name="Lines">How many of the last lines (1 to <see cref="MaxLines"/>); null for <see cref="DefaultLines"/>.</param>
public sealed record FleetNodeLogParams(int? Lines = null)
{
    /// <summary>The lines given when the caller names none.</summary>
    public const int DefaultLines = 200;

    /// <summary>The most lines one call gives.</summary>
    public const int MaxLines = 2000;
}

/// <summary>The result of <c>control.log</c>: the end of the log of the node.</summary>
/// <param name="Text">The last lines, each with its end of line; empty when the node wrote none yet.</param>
/// <param name="Path">Where the log is on the node.</param>
/// <param name="Truncated">Whether the text was cut to its last bytes to stay within the size of one answer.</param>
public sealed record FleetNodeLog(string Text, string Path, bool Truncated);

/// <summary>The parameters of <c>control.update</c>.</summary>
/// <param name="Check">Whether the feed is only read: nothing is downloaded nor installed.</param>
/// <param name="Version">The version to install; null for the latest one of the feed.</param>
/// <param name="Now">Whether the node switches without waiting for its sessions (their turns stop and stay resumable).</param>
public sealed record FleetNodeUpdateParams(bool Check = true, string? Version = null, bool Now = false);

/// <summary>The result of <c>control.update</c>.</summary>
/// <param name="Status">
/// <c>up_to_date</c> (nothing newer), <c>available</c> (a check found <paramref name="Target"/>), <c>staged</c> (the node
/// switches to <paramref name="Target"/> when idle, or now), <c>unavailable</c> (the node does not update itself) or
/// <c>failed</c> (<paramref name="Message"/> says why).
/// </param>
/// <param name="Running">The version the node runs.</param>
/// <param name="Latest">The latest version the feed lists; null when it was not read.</param>
/// <param name="Target">The version a check found or an update staged; null for none.</param>
/// <param name="Message">What else the person should know, or why it failed; null for nothing.</param>
public sealed record FleetNodeUpdateResult(string Status, string? Running, string? Latest = null, string? Target = null, string? Message = null)
{
    /// <summary>Nothing newer than what runs.</summary>
    public const string UpToDate = "up_to_date";

    /// <summary>A check found a version to install.</summary>
    public const string Available = "available";

    /// <summary>A version is downloaded, checked and waits for the node to switch to it.</summary>
    public const string Staged = "staged";

    /// <summary>The node does not update itself (a host without self-update).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The check or the download failed.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// What a node gives the devices that manage it beyond its catalog and devices: its log and its self-update. The node
/// application implements it; a host without them leaves <see cref="FleetNodeServiceOptions.Management"/> null.
/// </summary>
public interface IFleetNodeManagement
{
    /// <summary>Reads the last lines of the log of the node.</summary>
    /// <param name="lines">How many lines, already bounded.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The end of the log.</returns>
    Task<FleetNodeLog> ReadLogAsync(int lines, CancellationToken cancellationToken);

    /// <summary>Checks the feed of the node for an update, or installs one, as <c>altanode update</c> does.</summary>
    /// <param name="parameters">What to do.</param>
    /// <param name="requestedBy">Who asks, recorded with a switch (<c>manage:&lt;device id&gt;</c>, or <c>local</c>).</param>
    /// <param name="cancellationToken">Cancels the check or the download.</param>
    /// <returns>Where the update stands.</returns>
    Task<FleetNodeUpdateResult> UpdateAsync(FleetNodeUpdateParams parameters, string requestedBy, CancellationToken cancellationToken);
}
