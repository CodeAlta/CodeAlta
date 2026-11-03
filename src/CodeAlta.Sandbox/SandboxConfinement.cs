namespace CodeAlta.Sandbox;

/// <summary>
/// What a sandbox profile confines, as its settings write it and independent of the machine: the floor a session carries
/// to the work it starts or drives on another machine (<c>doc/fleet/security-and-privacy.md</c>, "Remote execution",
/// rule 8). The machine that receives the work compares its own profile with it (<see cref="WeakerThan"/>) and refuses
/// a profile that would let the work do more than the session that asks.
/// </summary>
/// <param name="Profile">The name of the profile.</param>
/// <param name="Network">What the network reaches.</param>
/// <param name="Hosts">The hosts of <see cref="SandboxNetwork.Hosts"/>.</param>
/// <param name="LocalPorts">The ports of the computer it may reach.</param>
/// <param name="Write">The folders it writes beyond those of its session, as written (<c>~</c> for the home folder).</param>
/// <param name="Read">The folders it reads beyond those of its session, as written.</param>
/// <param name="Deny">The folders and files it never reads, as written.</param>
/// <param name="Env">The names of the variables of the user's environment it receives.</param>
/// <param name="McpServers">The MCP servers its sessions may use.</param>
/// <param name="Outside">Whether a command may be asked to run outside the sandbox.</param>
/// <param name="Fleet">Whether its sessions may start or drive work on other machines.</param>
public sealed record SandboxConfinement(
    string Profile,
    SandboxNetwork Network,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<int> LocalPorts,
    IReadOnlyList<string> Write,
    IReadOnlyList<string> Read,
    IReadOnlyList<string> Deny,
    IReadOnlyList<string> Env,
    IReadOnlyList<string> McpServers,
    SandboxOutside Outside,
    bool Fleet)
{
    /// <summary>Gets the confinement of a resolved profile.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>Its confinement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static SandboxConfinement Of(ResolvedSandboxProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new(profile.Name, profile.Network, profile.Hosts, profile.LocalPorts, profile.Write, profile.Read, profile.Deny, profile.Env, profile.McpServers,
            profile.Outside, profile.Fleet);
    }

    /// <summary>
    /// Says why this confinement lets a session do more than <paramref name="floor"/>: a wider network, a host, a port,
    /// a folder, a variable or an MCP server the floor does not give, a folder the floor denies and this one does not,
    /// commands outside the sandbox or the fleet where the floor has none. Folders compare as written, case included, so
    /// that the same profile compares the same on two machines; a folder written otherwise, or only in another case, is
    /// taken as another one, which is the stricter reading on a file system that tells case apart.
    /// </summary>
    /// <param name="floor">The confinement to be at least as strict as.</param>
    /// <returns>Why it is weaker, for a person; null when it is at least as strict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="floor"/> is null.</exception>
    public string? WeakerThan(SandboxConfinement floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (Rank(Network) > Rank(floor.Network)) return $"its network is '{Name(Network)}', wider than '{Name(floor.Network)}'";
        if (Network == SandboxNetwork.Hosts && floor.Network == SandboxNetwork.Hosts
            && Hosts.FirstOrDefault(host => !floor.Hosts.Any(allowed => Covers(allowed, host))) is { } host)
            return $"it reaches the host '{host}'";
        if (LocalPorts.Except(floor.LocalPorts).Select(static port => (int?)port).FirstOrDefault() is { } port) return $"it reaches the local port {port}";
        // Folders compare by case: the two machines may have file systems that tell case apart (Linux) where the other
        // does not, and a folder that differs only by case is taken as another one, which refuses rather than gives more.
        if (Missing(Write, floor.Write, StringComparer.Ordinal) is { } written) return $"it writes '{written}'";
        if (Missing(Read, [.. floor.Read, .. floor.Write], StringComparer.Ordinal) is { } read) return $"it reads '{read}'";
        if (Missing(floor.Deny, Deny, StringComparer.Ordinal) is { } denied) return $"it does not deny '{denied}'";
        if (Missing(Env, floor.Env, StringComparer.Ordinal) is { } variable) return $"it receives the variable '{variable}'";
        if (Missing(McpServers, floor.McpServers, StringComparer.OrdinalIgnoreCase) is { } server) return $"it uses the MCP server '{server}'";
        if (Outside == SandboxOutside.Ask && floor.Outside == SandboxOutside.Never) return "it may ask to run a command outside the sandbox";
        if (Fleet && !floor.Fleet) return "it starts and drives work on other machines";
        return null;
    }

    /// <summary>Gets the name of a network as the settings write it: <c>off</c>, <c>hosts</c> or <c>on</c>.</summary>
    /// <param name="network">The network.</param>
    /// <returns>Its name.</returns>
    public static string NetworkName(SandboxNetwork network) => network switch
    {
        SandboxNetwork.Off => "off",
        SandboxNetwork.Hosts => "hosts",
        _ => "on",
    };

    /// <summary>Reads the name of a network; a name no version knows is the strictest network, none.</summary>
    /// <param name="name"><c>off</c>, <c>hosts</c> or <c>on</c>.</param>
    /// <returns>The network.</returns>
    public static SandboxNetwork ParseNetwork(string? name) => name switch
    {
        "hosts" => SandboxNetwork.Hosts,
        "on" => SandboxNetwork.On,
        _ => SandboxNetwork.Off,
    };

    private static int Rank(SandboxNetwork network) => network switch
    {
        SandboxNetwork.Off => 0,
        SandboxNetwork.Hosts => 1,
        _ => 2,
    };

    private static string Name(SandboxNetwork network) => NetworkName(network);

    // Whether a host pattern of the floor lets through every host a pattern lets through: the same name, or a name or a
    // pattern under a `*.` of the floor.
    private static bool Covers(string allowed, string host)
    {
        if (string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase)) return true;
        if (!allowed.StartsWith("*.", StringComparison.Ordinal)) return false;
        var domain = allowed[1..];
        var name = host.StartsWith("*.", StringComparison.Ordinal) ? host[1..] : host;
        return name.EndsWith(domain, StringComparison.OrdinalIgnoreCase) && name.Length > domain.Length;
    }

    // The first entry of a list that another does not have, folders compared as written whatever their separators.
    private static string? Missing(IReadOnlyList<string> entries, IReadOnlyList<string> within, StringComparer comparer)
    {
        var known = new HashSet<string>(within.Select(Path), comparer);
        return entries.FirstOrDefault(entry => !known.Contains(Path(entry)));
    }

    private static string Path(string entry) => entry.Trim().Replace('\\', '/').TrimEnd('/');
}
