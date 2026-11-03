using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>The update policies of a fleet (<see cref="FleetUpdatePolicy.Mode"/>).</summary>
public static class FleetUpdateModes
{
    /// <summary>Machines update only by a campaign a person starts.</summary>
    public const string Manual = "manual";

    /// <summary>The fleet stays on one version: no automatic campaign, and a campaign may only name that version.</summary>
    public const string Pinned = "pinned";

    /// <summary>The server follows the latest stable version of a feed, checked daily, and updates the fleet at its maintenance window.</summary>
    public const string Automatic = "automatic";

    /// <summary>Gets whether a text is a known mode.</summary>
    /// <param name="mode">The text.</param>
    /// <returns>Whether it is one of the modes.</returns>
    public static bool IsKnown(string? mode) => mode is Manual or Pinned or Automatic;
}

/// <summary>The states of an update campaign.</summary>
public static class FleetUpdateCampaignStates
{
    /// <summary>It waits for its time.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>The nodes update, a bounded number at a time.</summary>
    public const string UpdatingNodes = "updating_nodes";

    /// <summary>The nodes are done; the server updates itself.</summary>
    public const string UpdatingServer = "updating_server";

    /// <summary>The server and every machine run the version.</summary>
    public const string Done = "done";

    /// <summary>The server runs the version; machines were offline (they update when they reconnect), failed or refused.</summary>
    public const string PartlyDone = "partly_done";

    /// <summary>The server did not update (its update failed or rolled back, or every node that tried failed).</summary>
    public const string Failed = "failed";

    /// <summary>A person cancelled it before the server's step.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Gets whether a campaign in a state still moves on its own (scheduled, or updating).</summary>
    /// <param name="state">The state.</param>
    /// <returns>Whether it is active.</returns>
    public static bool IsActive(string? state) => state is Scheduled or UpdatingNodes or UpdatingServer;
}

/// <summary>The states of one machine in an update campaign, as the server records them and as nodes report them.</summary>
public static class FleetUpdateMachineStates
{
    /// <summary>Not asked yet: waiting for a slot, or offline (it is asked when it reconnects).</summary>
    public const string Pending = "pending";

    /// <summary>Asked; no report yet.</summary>
    public const string Instructed = "instructed";

    /// <summary>The node accepted the instruction and waits for its time.</summary>
    public const string Waiting = "waiting";

    /// <summary>The node downloads and checks the package.</summary>
    public const string Downloading = "downloading";

    /// <summary>The version is staged; the node switches when idle.</summary>
    public const string Switching = "switching";

    /// <summary>The machine runs the version.</summary>
    public const string Updated = "updated";

    /// <summary>The update failed (download, hash, state); the machine stays on its version.</summary>
    public const string Failed = "failed";

    /// <summary>The new version did not confirm itself: the machine went back to its previous version.</summary>
    public const string RolledBack = "rolled_back";

    /// <summary>The machine refused the instruction: a signature, a version or its owner's policy.</summary>
    public const string Refused = "refused";

    /// <summary>The campaign was cancelled before the machine switched.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>A newer campaign took over this machine.</summary>
    public const string Superseded = "superseded";

    /// <summary>Gets whether a machine in a state is finished with the campaign.</summary>
    /// <param name="state">The state.</param>
    /// <returns>Whether nothing more happens for it.</returns>
    public static bool IsTerminal(string? state) => state is Updated or Failed or RolledBack or Refused or Cancelled or Superseded;

    /// <summary>Gets whether a state is one a node reports.</summary>
    /// <param name="state">The state.</param>
    /// <returns>Whether a node may report it.</returns>
    public static bool IsReported(string? state) => state is Waiting or Downloading or Switching or Updated or Failed or RolledBack or Refused or Cancelled;
}

/// <summary>
/// The manifest of an update campaign, signed by a controller device (an owner's desktop): what the machines install and
/// when. The server stores it and passes it to the nodes as signed; a node installs only what a controller of the device
/// list it anchored signed, a version newer than the one it runs (an older one only when the manifest says a person chose
/// a rollback), from a package that matches the hash signed for its runtime. The server checks the same before it
/// updates itself.
/// </summary>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="CampaignId">The id of the campaign, made by the signer.</param>
/// <param name="Sequence">The number of the campaign in the organization, from 1; a machine refuses one older than the last it accepted.</param>
/// <param name="TargetVersion">The version to install.</param>
/// <param name="Feed">The NuGet v3 feed or the folder to read; the machine's own <c>update.json</c> decides whether it may.</param>
/// <param name="Rollback">Whether a person chose a version older than the one machines run.</param>
/// <param name="NotBefore">When the update may start, in UTC; null for now.</param>
/// <param name="TimeZone">The time zone the person chose the time in, for display.</param>
/// <param name="Machines">The device ids of the nodes to update; null for all.</param>
/// <param name="Packages">The SHA-512 (base64) of the runtime package of each runtime, by product: <c>CodeAlta.Node</c> → <c>win-x64</c> → hash.</param>
/// <param name="SignerDeviceId">The controller device that signs it.</param>
/// <param name="Time">When the signer made it; for display, never for ordering.</param>
public sealed record FleetUpdateManifest(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("id")] string CampaignId,
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("version")] string TargetVersion,
    [property: JsonPropertyName("feed")] string Feed,
    [property: JsonPropertyName("rollback")] bool Rollback,
    [property: JsonPropertyName("notBefore")] DateTimeOffset? NotBefore,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("machines")] IReadOnlyList<string>? Machines,
    [property: JsonPropertyName("packages")] IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Packages,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets the signed hash of the runtime package of a product for a runtime.</summary>
    /// <param name="productId">The id of the tool package (<c>CodeAlta.Node</c>).</param>
    /// <param name="runtime">The runtime (<c>win-x64</c>).</param>
    /// <returns>The hash, or null when the manifest signs none for it.</returns>
    public string? HashFor(string productId, string runtime)
        => Packages.TryGetValue(productId, out var runtimes) && runtimes.TryGetValue(runtime, out var hash) ? hash : null;

    /// <summary>Gets whether a node is a target of the campaign.</summary>
    /// <param name="deviceId">The device of the node.</param>
    /// <returns>Whether the manifest names it, or names no machine (all).</returns>
    public bool Targets(string deviceId) => Machines is null || Machines.Contains(deviceId, StringComparer.Ordinal);
}

/// <summary>
/// The update policy of a fleet, signed by a controller device: manual, pinned to a version, or automatic. For an
/// automatic policy the server checks the feed daily and starts a campaign at the maintenance window; since no person
/// signs the hash of that version, each machine checks the package against the feed's own hash, and takes only the
/// latest stable version the feed lists, newer than the one it runs.
/// </summary>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Sequence">The number of the policy, from 1; a policy replaces only an older one.</param>
/// <param name="Mode">The mode (<see cref="FleetUpdateModes"/>).</param>
/// <param name="PinnedVersion">The version a pinned fleet stays on; null otherwise.</param>
/// <param name="Feed">The feed an automatic fleet follows; null for nuget.org.</param>
/// <param name="Window">The start of the daily maintenance window, <c>HH:mm</c> in <see cref="TimeZone"/>; null for any time.</param>
/// <param name="TimeZone">The time zone of the window (an IANA or Windows id); null for UTC.</param>
/// <param name="SignerDeviceId">The controller device that signs it.</param>
/// <param name="Time">When the signer made it.</param>
public sealed record FleetUpdatePolicy(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("pinned")] string? PinnedVersion,
    [property: JsonPropertyName("feed")] string? Feed,
    [property: JsonPropertyName("window")] string? Window,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>A manifest or a policy as it travels and is stored: the exact JSON text the signer signed, and the signature.</summary>
/// <param name="Payload">The JSON text, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of the domain line and the text, r and s in base64url.</param>
public sealed record FleetSignedUpdateDocument(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>
/// The payload of <see cref="FleetServerMethods.UpdateInstruction"/>: what the server asks a node to install. A campaign a
/// person started carries its signed manifest; an automatic one carries the signed policy and the version the server
/// found, which the node checks against the feed itself.
/// </summary>
/// <param name="CampaignId">The campaign.</param>
/// <param name="Manifest">The signed manifest; null for an automatic campaign.</param>
/// <param name="Policy">The signed automatic policy; null for a campaign a person started.</param>
/// <param name="Version">The version an automatic campaign installs; null with a manifest.</param>
public sealed record FleetUpdateInstruction(string CampaignId, FleetSignedUpdateDocument? Manifest, FleetSignedUpdateDocument? Policy = null, string? Version = null);

/// <summary>The payload of <see cref="FleetServerMethods.UpdateCancel"/>: a campaign cancelled before the node switched.</summary>
/// <param name="CampaignId">The campaign.</param>
public sealed record FleetUpdateCancel(string CampaignId);

/// <summary>
/// Where a node stands in a campaign (<see cref="FleetServerMethods.UpdateReport"/>, and in its presence): sent when it
/// changes and again on each connection.
/// </summary>
/// <param name="CampaignId">The campaign.</param>
/// <param name="State">The state (<see cref="FleetUpdateMachineStates"/>, one a node reports).</param>
/// <param name="Version">The version the campaign installs.</param>
/// <param name="Running">The version the node runs.</param>
/// <param name="Message">What happened, for a failure or a refusal; null otherwise.</param>
/// <param name="At">When the state was reached.</param>
public sealed record FleetUpdateReport(string CampaignId, string State, string Version, string Running, string? Message, DateTimeOffset At);

/// <summary>Writes, reads and checks the signed documents of the fleet's updates; the versions are compared by the self-update.</summary>
public static class FleetUpdateDocuments
{
    /// <summary>The most machines a manifest names.</summary>
    public const int MaxMachines = 1000;

    /// <summary>The most bytes of a signed document.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    private const string ManifestDomain = "codealta-update-manifest/1\n";
    private const string PolicyDomain = "codealta-update-policy/1\n";
    private const int MaxTextLength = 2048;

    /// <summary>Writes the JSON text of a manifest, the text a signer signs.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public static string Serialize(FleetUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, FleetUpdateJsonContext.Default.FleetUpdateManifest);
    }

    /// <summary>Writes the JSON text of a policy, the text a signer signs.</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public static string Serialize(FleetUpdatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return JsonSerializer.Serialize(policy, FleetUpdateJsonContext.Default.FleetUpdatePolicy);
    }

    /// <summary>Gets the bytes a signer signs for a manifest.</summary>
    /// <param name="payload">The JSON text of the manifest.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetManifestSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(ManifestDomain + payload);
    }

    /// <summary>Gets the bytes a signer signs for a policy.</summary>
    /// <param name="payload">The JSON text of the policy.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetPolicySignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(PolicyDomain + payload);
    }

    /// <summary>Reads the JSON text of a manifest and checks what it says (<see cref="Check(FleetUpdateManifest)"/>).</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="manifest">The manifest, when it reads and holds.</param>
    /// <param name="reason">Why it does not, or null.</param>
    /// <returns>Whether it reads and holds.</returns>
    public static bool TryParseManifest(string? payload, [NotNullWhen(true)] out FleetUpdateManifest? manifest, [NotNullWhen(false)] out string? reason)
    {
        manifest = null;
        if (string.IsNullOrEmpty(payload) || Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            reason = "the manifest does not read";
            return false;
        }

        try
        {
            manifest = JsonSerializer.Deserialize(payload, FleetUpdateJsonContext.Default.FleetUpdateManifest);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null || string.IsNullOrEmpty(manifest.OrganizationId) || string.IsNullOrEmpty(manifest.CampaignId) || string.IsNullOrEmpty(manifest.SignerDeviceId)
            || string.IsNullOrEmpty(manifest.TargetVersion) || string.IsNullOrEmpty(manifest.Feed) || manifest.Packages is null)
        {
            manifest = null;
            reason = "the manifest does not read";
            return false;
        }

        reason = Check(manifest);
        if (reason is null) return true;
        manifest = null;
        return false;
    }

    /// <summary>Reads the JSON text of a policy and checks what it says (<see cref="Check(FleetUpdatePolicy)"/>).</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="policy">The policy, when it reads and holds.</param>
    /// <param name="reason">Why it does not, or null.</param>
    /// <returns>Whether it reads and holds.</returns>
    public static bool TryParsePolicy(string? payload, [NotNullWhen(true)] out FleetUpdatePolicy? policy, [NotNullWhen(false)] out string? reason)
    {
        policy = null;
        if (string.IsNullOrEmpty(payload) || Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            reason = "the policy does not read";
            return false;
        }

        try
        {
            policy = JsonSerializer.Deserialize(payload, FleetUpdateJsonContext.Default.FleetUpdatePolicy);
        }
        catch (JsonException)
        {
            policy = null;
        }

        if (policy is null || string.IsNullOrEmpty(policy.OrganizationId) || string.IsNullOrEmpty(policy.SignerDeviceId) || string.IsNullOrEmpty(policy.Mode))
        {
            policy = null;
            reason = "the policy does not read";
            return false;
        }

        reason = Check(policy);
        if (reason is null) return true;
        policy = null;
        return false;
    }

    /// <summary>
    /// Checks that a signed document names the organization and is signed by a controller device of a device list, with
    /// the key the list holds for it.
    /// </summary>
    /// <param name="signed">The signed document.</param>
    /// <param name="signedBytes">The bytes its signature covers.</param>
    /// <param name="organizationId">The organization the document names.</param>
    /// <param name="signerDeviceId">The device the document names as its signer.</param>
    /// <param name="devices">The device list as the checking device verified it.</param>
    /// <param name="expectedOrganizationId">The organization of the server the device is linked to.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signed"/> or <paramref name="devices"/> is null.</exception>
    public static bool TryVerifySigner(FleetSignedUpdateDocument signed, byte[] signedBytes, string organizationId, string signerDeviceId, FleetDeviceListState devices,
        string? expectedOrganizationId, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(devices);
        if (string.IsNullOrEmpty(expectedOrganizationId) || !string.Equals(organizationId, expectedOrganizationId, StringComparison.Ordinal)
            || devices.OrganizationId is { } listed && !string.Equals(listed, expectedOrganizationId, StringComparison.Ordinal))
        {
            reason = "it is for another organization";
            return false;
        }

        if (!devices.Members.TryGetValue(signerDeviceId, out var signer) || signer.Kind != FleetDeviceKinds.Controller || devices.Removed.Contains(signerDeviceId))
        {
            reason = "it is not signed by a controller device of the organization";
            return false;
        }

        if (string.IsNullOrEmpty(signed.Signature) || !signer.Key.Verify(signedBytes, signed.Signature))
        {
            reason = "the signature does not verify with the key of its signer";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Checks what a manifest says, apart from its signature and the versions.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>Why it does not hold, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public static string? Check(FleetUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Version != FleetUpdateManifest.CurrentVersion) return $"version {manifest.Version.ToString(CultureInfo.InvariantCulture)} of the manifest is not known";
        if (manifest.Sequence <= 0) return "the sequence starts at 1";
        if (manifest.CampaignId.Length > 64 || !manifest.CampaignId.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return "the id of the campaign is not valid";
        if (manifest.TargetVersion.Length > 64 || manifest.Feed.Length > MaxTextLength || manifest.Feed.Any(char.IsControl)) return "the version or the feed is not valid";
        if (manifest.TimeZone is { } zone && (zone.Length is 0 or > 64 || zone.Any(char.IsControl))) return "the time zone is not valid";
        if (manifest.Machines is { } machines && (machines.Count is 0 or > MaxMachines || machines.Any(static id => string.IsNullOrEmpty(id) || id.Length > 128)))
            return $"a manifest names 1 to {MaxMachines} machines, or none for all";
        if (manifest.Packages.Count is 0 or > 4) return "the manifest signs the packages of 1 to 4 products";
        foreach (var (product, runtimes) in manifest.Packages)
        {
            if (string.IsNullOrEmpty(product) || product.Length > 128 || runtimes is null || runtimes.Count is 0 or > 32) return "the packages of the manifest are not valid";
            foreach (var (runtime, hash) in runtimes)
            {
                if (string.IsNullOrEmpty(runtime) || runtime.Length > 64 || string.IsNullOrEmpty(hash) || hash.Length > 256) return "the packages of the manifest are not valid";
            }
        }

        return null;
    }

    /// <summary>Checks what a policy says, apart from its signature.</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>Why it does not hold, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public static string? Check(FleetUpdatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Version != FleetUpdatePolicy.CurrentVersion) return $"version {policy.Version.ToString(CultureInfo.InvariantCulture)} of the policy is not known";
        if (policy.Sequence <= 0) return "the sequence starts at 1";
        if (!FleetUpdateModes.IsKnown(policy.Mode)) return $"'{policy.Mode}' is not a mode (manual, pinned, automatic)";
        if (policy.Mode == FleetUpdateModes.Pinned && string.IsNullOrEmpty(policy.PinnedVersion)) return "a pinned policy names its version";
        if (policy.PinnedVersion is { Length: > 64 }) return "the version is not valid";
        if (policy.Feed is { } feed && (feed.Length is 0 or > MaxTextLength || feed.Any(char.IsControl))) return "the feed is not valid";
        if (policy.Window is { } window && !TryParseWindow(window, out _)) return "the window is HH:mm";
        if (policy.TimeZone is { } zone && (zone.Length is 0 or > 64 || zone.Any(char.IsControl))) return "the time zone is not valid";
        return null;
    }

    /// <summary>Reads the start of a maintenance window: <c>HH:mm</c>, 24 hours.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">The time of day.</param>
    /// <returns>Whether it reads.</returns>
    public static bool TryParseWindow(string? text, out TimeOnly start)
        => TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out start);

    /// <summary>
    /// Gets the next start of the maintenance window of a policy at or after a time: today's or tomorrow's window in its
    /// time zone, or the time itself when it has no window. A time zone this machine does not know counts as UTC.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="after">The time.</param>
    /// <returns>The start of the window, in UTC.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public static DateTimeOffset NextWindow(FleetUpdatePolicy policy, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!TryParseWindow(policy.Window, out var start)) return after;
        var zone = FindZone(policy.TimeZone);
        var local = TimeZoneInfo.ConvertTime(after, zone);
        for (var day = 0; day < 3; day++)
        {
            var date = DateOnly.FromDateTime(local.DateTime).AddDays(day);
            var wall = date.ToDateTime(start, DateTimeKind.Unspecified);
            // A time skipped by a change of daylight saving time moves an hour later.
            if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
            var candidate = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            if (candidate >= after) return candidate.ToUniversalTime();
        }

        return after;
    }

    private static TimeZoneInfo FindZone(string? id)
    {
        if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

/// <summary>The source-generated contracts of the fleet's updates.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = false)]
[JsonSerializable(typeof(FleetUpdateManifest))]
[JsonSerializable(typeof(FleetUpdatePolicy))]
[JsonSerializable(typeof(FleetSignedUpdateDocument))]
public sealed partial class FleetUpdateJsonContext : JsonSerializerContext;
