using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// The sandbox the sessions of a node run in by default (<c>doc/fleet/node.md</c>, "Safety"): where the sandbox can run,
/// a session no scope sandboxes runs in the built-in profile <see cref="SandboxProfiles.Node"/>, which is <c>workspace</c>
/// (the package registries) with the hosts of git hosting. Its agents fetch, push and open pull requests without leaving
/// the sandbox, and still reach neither the home folder, the credentials nor the files of CodeAlta.
/// </summary>
public static class FleetNodeSandbox
{
    /// <summary>The name of the profile the sessions of a node run in by default.</summary>
    public const string ProfileName = SandboxProfiles.Node;

    /// <summary>Gets the sandbox the sessions of a node run in by default, and whether the sandbox can run there.</summary>
    /// <param name="runtime">The runtime of the node.</param>
    /// <returns>The sandbox.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    public static FleetNodeSandboxStatus Describe(SessionRuntimeService runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var availability = runtime.GetSandboxAvailability();
        var profile = runtime.ReadSandboxSettings().AllSessions?.Trim();
        return new FleetNodeSandboxStatus(
            string.IsNullOrEmpty(profile) || profile.Equals(SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase) ? null : profile,
            availability.IsAvailable, availability.Backend, availability.Reason, availability.Setup);
    }
}

/// <summary>The sandbox the sessions of a node run in by default, as it says it.</summary>
/// <param name="Profile">The profile a session no scope sandboxes runs in; null when such a session runs without a sandbox.</param>
/// <param name="Available">Whether the sandbox can run on the machine.</param>
/// <param name="Backend">The sandbox of its system.</param>
/// <param name="Reason">Why it cannot run; null when it can.</param>
/// <param name="Setup">What to install or set up for it to run; null when nothing is known.</param>
public sealed record FleetNodeSandboxStatus(string? Profile, bool Available, string Backend, string? Reason, string? Setup);
