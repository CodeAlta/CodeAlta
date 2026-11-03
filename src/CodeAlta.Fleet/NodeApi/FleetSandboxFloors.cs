using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// Turns the floor a session carries to another machine (<see cref="SessionFleetFloor"/>, <see cref="SandboxConfinement"/>)
/// into what a request of an agent carries, and back (<c>doc/fleet/security-and-privacy.md</c>, "Remote execution", rule 8).
/// </summary>
public static class FleetSandboxFloors
{
    /// <summary>Writes what a profile confines as a request carries it.</summary>
    /// <param name="confinement">The confinement.</param>
    /// <returns>Its form on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="confinement"/> is null.</exception>
    public static FleetSandboxConfinement ToWire(SandboxConfinement confinement)
    {
        ArgumentNullException.ThrowIfNull(confinement);
        return new(confinement.Profile, SandboxConfinement.NetworkName(confinement.Network), [.. confinement.Hosts], [.. confinement.LocalPorts],
            [.. confinement.Write], [.. confinement.Read], [.. confinement.Deny], [.. confinement.Env], [.. confinement.McpServers],
            confinement.Outside == SandboxOutside.Ask ? "ask" : "never", confinement.Fleet);
    }

    /// <summary>Writes the floor of a session that sends a prompt as a request carries it.</summary>
    /// <param name="floor">The floor.</param>
    /// <returns>Its form on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="floor"/> is null.</exception>
    /// <exception cref="ArgumentException">The floor names a sandbox without what it confines: such a session reaches no other machine.</exception>
    public static FleetAgentFloor ToWire(SessionFleetFloor floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (floor.Sandbox is not null && floor.Confinement is null) throw new ArgumentException("A sandboxed session whose profile cannot be used reaches no other machine.", nameof(floor));
        return new(floor.PermissionMode, floor.Sandbox, floor.Confinement is { } confinement ? ToWire(confinement) : null);
    }

    /// <summary>Reads what a profile confines as a request carries it: a value no version knows is the strictest one.</summary>
    /// <param name="confinement">Its form on the wire.</param>
    /// <returns>The confinement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="confinement"/> is null.</exception>
    public static SandboxConfinement FromWire(FleetSandboxConfinement confinement)
    {
        ArgumentNullException.ThrowIfNull(confinement);
        return new(confinement.Profile, SandboxConfinement.ParseNetwork(confinement.Network), confinement.Hosts ?? [], confinement.LocalPorts ?? [],
            confinement.Write ?? [], confinement.Read ?? [], confinement.Deny ?? [], confinement.Env ?? [], confinement.McpServers ?? [],
            confinement.Outside == "ask" ? SandboxOutside.Ask : SandboxOutside.Never, confinement.Fleet);
    }
}
