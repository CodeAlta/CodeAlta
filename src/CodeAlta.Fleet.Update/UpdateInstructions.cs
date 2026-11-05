using System.Diagnostics.CodeAnalysis;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Update;

/// <summary>What a machine checks an instruction of the fleet against.</summary>
public sealed record UpdateInstructionContext
{
    /// <summary>Gets the device list as the machine verified and anchored it (the server: as it replays its own).</summary>
    public required FleetDeviceListState Devices { get; init; }

    /// <summary>Gets the organization of the server the machine is linked to.</summary>
    public required string? OrganizationId { get; init; }

    /// <summary>Gets the device of the machine; null for the server, which is the target of every campaign.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Gets the product that updates itself.</summary>
    public required UpdateProduct Product { get; init; }

    /// <summary>Gets the runtime of the machine (<c>win-x64</c>), whose package hash the manifest must sign.</summary>
    public required string Runtime { get; init; }

    /// <summary>Gets the version that runs.</summary>
    public required UpdateVersion Running { get; init; }

    /// <summary>Gets the sequence of the last manifest the machine accepted from this server; 0 for none.</summary>
    public long LastSequence { get; init; }

    /// <summary>Gets the campaign of that manifest; null for none.</summary>
    public string? LastCampaignId { get; init; }

    /// <summary>Gets the sequence of the last update policy the machine accepted from this server, of any mode; 0 for none.</summary>
    public long LastPolicySequence { get; init; }

    /// <summary>
    /// Gets the last update policy the machine accepted from this server, of any mode, once verified; null for none (the
    /// server, which holds the policy itself). A manifest of a pinned fleet must name its version, and an automatic
    /// update needs this policy, or a newer one.
    /// </summary>
    public FleetUpdatePolicy? Policy { get; init; }

    /// <summary>
    /// Gets when the machine checks the instruction, for the start of an automatic update: the maintenance window of its
    /// policy (<see cref="FleetUpdateDocuments.WindowOpening"/>); null leaves the start to the caller.
    /// </summary>
    public DateTimeOffset? Now { get; init; }
}

/// <summary>An instruction of the fleet once verified: what to install, from where, checked against which hash, and when.</summary>
/// <param name="CampaignId">The campaign.</param>
/// <param name="Version">The version to install.</param>
/// <param name="Feed">The feed to read.</param>
/// <param name="ExpectedSha512">The hash a controller device signed for the package of this machine; null for an automatic campaign (the feed's hash checks it).</param>
/// <param name="NotBefore">When the update may start, in UTC; null for now.</param>
/// <param name="Rollback">Whether a person chose an older version.</param>
/// <param name="Sequence">The sequence of the manifest, or of the policy of an automatic campaign.</param>
/// <param name="Automatic">Whether it comes from the automatic policy.</param>
public sealed record VerifiedUpdate(string CampaignId, UpdateVersion Version, string Feed, string? ExpectedSha512, DateTimeOffset? NotBefore, bool Rollback, long Sequence,
    bool Automatic)
{
    /// <summary>Gets the automatic policy the update comes from, as signed and verified; null for a manifest.</summary>
    public FleetUpdatePolicy? Policy { get; init; }

    /// <summary>Gets the request of the self-update that carries it out.</summary>
    /// <returns>The request, from the fleet.</returns>
    public UpdateRequest ToRequest() => new() { Version = Version.ToString(), Source = Feed, ExpectedSha512 = ExpectedSha512, Origin = UpdateOrigin.Remote };
}

/// <summary>
/// Checks what the fleet asks a machine to install (<see cref="FleetUpdateInstruction"/>): the server alone cannot make a
/// machine install anything. A campaign a person started must carry a manifest signed by a controller device of the
/// device list the machine anchored, newer than the last one it accepted, naming it, for a version newer than the one it
/// runs (an older one only when the manifest says a person chose a rollback), with the hash of the package of its
/// runtime. An automatic campaign must carry an automatic policy signed the same way, for a stable version not older than
/// the one it runs (the one it runs already installs nothing: the machine is updated once that version confirmed), and
/// starts at the maintenance window of that policy; the machine then checks the feed itself
/// (<see cref="UpdateRequest.StableOnly"/>). The latest policy the machine accepted (<see cref="UpdateInstructionContext.Policy"/>)
/// wins: an automatic policy it replaced allows nothing, and a pinned one allows only its version.
/// </summary>
public static class UpdateInstructions
{
    /// <summary>Verifies an instruction.</summary>
    /// <param name="instruction">The instruction.</param>
    /// <param name="context">What the machine checks it against.</param>
    /// <param name="update">What it asks, when it verifies.</param>
    /// <param name="reason">Why it is refused, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool TryVerify(FleetUpdateInstruction instruction, UpdateInstructionContext context, [NotNullWhen(true)] out VerifiedUpdate? update,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(context);
        update = null;
        if (instruction.Manifest is { } manifest) return TryVerifyManifest(instruction, manifest, context, out update, out reason);
        if (instruction.Policy is { } policy) return TryVerifyAutomatic(instruction, policy, context, out update, out reason);
        reason = "the instruction carries no signed manifest";
        return false;
    }

    /// <summary>Verifies a signed manifest for the server's own step, or for a node.</summary>
    /// <param name="signed">The signed manifest.</param>
    /// <param name="context">What the machine checks it against.</param>
    /// <param name="update">What it asks, when it verifies.</param>
    /// <param name="reason">Why it is refused, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool TryVerifyManifest(FleetSignedUpdateDocument signed, UpdateInstructionContext context, [NotNullWhen(true)] out VerifiedUpdate? update,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(context);
        update = null;
        if (!FleetUpdateDocuments.TryParseManifest(signed.Payload, out var parsed, out reason)) return false;
        return TryVerifyManifest(new FleetUpdateInstruction(parsed.CampaignId, signed), signed, context, out update, out reason);
    }

    private static bool TryVerifyManifest(FleetUpdateInstruction instruction, FleetSignedUpdateDocument signed, UpdateInstructionContext context,
        [NotNullWhen(true)] out VerifiedUpdate? update, [NotNullWhen(false)] out string? reason)
    {
        update = null;
        if (!FleetUpdateDocuments.TryParseManifest(signed.Payload, out var manifest, out reason)) return false;
        if (!string.Equals(manifest.CampaignId, instruction.CampaignId, StringComparison.Ordinal))
        {
            reason = "the manifest is for another campaign";
            return false;
        }

        if (!FleetUpdateDocuments.TryVerifySigner(signed, FleetUpdateDocuments.GetManifestSignedBytes(signed.Payload), manifest.OrganizationId, manifest.SignerDeviceId,
                context.Devices, context.OrganizationId, out var signerReason))
        {
            reason = "the manifest " + signerReason;
            return false;
        }

        // A manifest replayed by the server (an older campaign, another one under the same number, or the one already
        // accepted) never starts anything: the machine answers the same campaign sent again with what it kept of it.
        if (manifest.Sequence <= context.LastSequence)
        {
            reason = manifest.Sequence == context.LastSequence && string.Equals(manifest.CampaignId, context.LastCampaignId, StringComparison.Ordinal)
                ? $"campaign {manifest.Sequence} was accepted already: it does not start again"
                : $"the manifest of campaign {manifest.Sequence} is older than campaign {context.LastSequence}, the last one this machine accepted";
            return false;
        }

        if (context.DeviceId is { } device && !manifest.Targets(device))
        {
            reason = "the manifest does not name this machine";
            return false;
        }

        if (!UpdateVersion.TryParse(manifest.TargetVersion, out var target))
        {
            reason = $"'{manifest.TargetVersion}' is not a version";
            return false;
        }

        if (target < context.Running && !manifest.Rollback)
        {
            reason = $"{target} is older than {context.Running}, which runs: a downgrade needs a rollback a person chooses";
            return false;
        }

        // A manifest signed before the fleet was pinned, delivered after it.
        if (context.Policy is { Mode: FleetUpdateModes.Pinned } pinned && !(UpdateVersion.TryParse(pinned.PinnedVersion, out var pin) && pin.Equals(target)))
        {
            reason = $"the fleet is pinned to {pinned.PinnedVersion} by policy {pinned.Sequence}, not {target}";
            return false;
        }

        var hash = manifest.HashFor(context.Product.PackageId, context.Runtime);
        if (hash is null || !PackageHash.TryParse(hash, out _))
        {
            reason = $"the manifest signs no hash of {context.Product.RuntimePackageId(context.Runtime)}, the package of this machine";
            return false;
        }

        update = new VerifiedUpdate(manifest.CampaignId, target, manifest.Feed, hash, manifest.NotBefore, manifest.Rollback, manifest.Sequence, Automatic: false);
        reason = null;
        return true;
    }

    private static bool TryVerifyAutomatic(FleetUpdateInstruction instruction, FleetSignedUpdateDocument signed, UpdateInstructionContext context,
        [NotNullWhen(true)] out VerifiedUpdate? update, [NotNullWhen(false)] out string? reason)
    {
        update = null;
        if (!FleetUpdateDocuments.TryParsePolicy(signed.Payload, out var policy, out reason)) return false;
        if (policy.Mode != FleetUpdateModes.Automatic)
        {
            reason = $"the policy of the fleet is {policy.Mode}, not automatic";
            return false;
        }

        if (!FleetUpdateDocuments.TryVerifySigner(signed, FleetUpdateDocuments.GetPolicySignedBytes(signed.Payload), policy.OrganizationId, policy.SignerDeviceId,
                context.Devices, context.OrganizationId, out var signerReason))
        {
            reason = "the policy " + signerReason;
            return false;
        }

        // A policy replaced (by a manual or a pinned one too) no longer allows anything.
        if (policy.Sequence < Math.Max(context.LastPolicySequence, context.Policy?.Sequence ?? 0))
        {
            reason = $"policy {policy.Sequence} is older than policy {Math.Max(context.LastPolicySequence, context.Policy?.Sequence ?? 0)}, the last one this machine accepted";
            return false;
        }

        if (context.Policy is { } kept && kept.Sequence == policy.Sequence && kept != policy)
        {
            reason = $"policy {policy.Sequence} is another policy than the one this machine accepted under that number";
            return false;
        }

        if (!UpdateVersion.TryParse(instruction.Version, out var target) || target.IsPrerelease)
        {
            reason = "an automatic update installs a stable version";
            return false;
        }

        // No person chose this version: never older than what runs. The version that runs already is taken too: nothing is
        // installed, and the machine is updated once that version confirmed.
        if (target < context.Running)
        {
            reason = $"{target} is older than {context.Running}, which runs";
            return false;
        }

        // Its time is the signed maintenance window, whenever the server sends it.
        DateTimeOffset? notBefore = context.Now is { } now && FleetUpdateDocuments.WindowOpening(policy, now) is var opening && opening > now ? opening : null;
        update = new VerifiedUpdate(instruction.CampaignId, target, policy.Feed ?? UpdateFeeds.NuGetOrg, null, notBefore, false, policy.Sequence, Automatic: true) { Policy = policy };
        reason = null;
        return true;
    }
}
