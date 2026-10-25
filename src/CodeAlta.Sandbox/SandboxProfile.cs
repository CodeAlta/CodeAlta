namespace CodeAlta.Sandbox;

/// <summary>What the network of a sandbox reaches.</summary>
public enum SandboxNetwork
{
    /// <summary>Nothing: no host of the internet, only the loopback inside the sandbox itself.</summary>
    Off,

    /// <summary>The hosts of the profile, through a proxy of CodeAlta that checks each name.</summary>
    Hosts,

    /// <summary>The internet, but never the loopback or the local network of the computer.</summary>
    On,
}

/// <summary>Whether the agent of a sandboxed session may ask to run a command outside the sandbox.</summary>
public enum SandboxOutside
{
    /// <summary>It may ask; the user is asked in every permission mode.</summary>
    Ask,

    /// <summary>It may not: everything runs in the sandbox.</summary>
    Never,
}

/// <summary>
/// A profile as the settings write it: what a sandbox allows beyond what every sandbox gets. A value left null is
/// taken from the profile it changes or extends.
/// </summary>
/// <remarks>
/// A profile named like a built-in one changes it: each key it sets replaces the built-in value. A profile with
/// <see cref="Extends"/> starts from that profile: its lists are added to the lists of the base, its other keys
/// replace them. A profile with neither starts from no folder, no host, no network and <see cref="SandboxOutside.Ask"/>.
/// </remarks>
public sealed record SandboxProfile
{
    /// <summary>Gets the name of the profile.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the profile it builds on, or null.</summary>
    public string? Extends { get; init; }

    /// <summary>Gets more folders, read-write; <c>~</c> at the start is the home folder.</summary>
    public IReadOnlyList<string>? Write { get; init; }

    /// <summary>Gets more folders, read-only.</summary>
    public IReadOnlyList<string>? Read { get; init; }

    /// <summary>Gets folders and files never readable, also inside <see cref="Write"/> and <see cref="Read"/>.</summary>
    public IReadOnlyList<string>? Deny { get; init; }

    /// <summary>Gets what the network reaches.</summary>
    public SandboxNetwork? Network { get; init; }

    /// <summary>Gets the hosts of <see cref="SandboxNetwork.Hosts"/>: names, or <c>*.example.org</c> for its subdomains.</summary>
    public IReadOnlyList<string>? Hosts { get; init; }

    /// <summary>Gets the ports of this computer it may reach, through the proxy.</summary>
    public IReadOnlyList<int>? LocalPorts { get; init; }

    /// <summary>Gets the names of the variables of the user's environment it receives.</summary>
    public IReadOnlyList<string>? Env { get; init; }

    /// <summary>Gets the MCP servers the sessions of the profile may use.</summary>
    public IReadOnlyList<string>? McpServers { get; init; }

    /// <summary>Gets whether a command may be asked to run outside the sandbox.</summary>
    public SandboxOutside? Outside { get; init; }
}

/// <summary>A profile with every value decided: the one a session runs with.</summary>
/// <param name="Name">The name of the profile.</param>
/// <param name="Write">More folders, read-write.</param>
/// <param name="Read">More folders, read-only.</param>
/// <param name="Deny">Folders and files never readable.</param>
/// <param name="Network">What the network reaches.</param>
/// <param name="Hosts">The hosts of <see cref="SandboxNetwork.Hosts"/>.</param>
/// <param name="LocalPorts">The ports of this computer it may reach.</param>
/// <param name="Env">The names of the variables of the user's environment it receives.</param>
/// <param name="McpServers">The MCP servers its sessions may use.</param>
/// <param name="Outside">Whether a command may be asked to run outside the sandbox.</param>
public sealed record ResolvedSandboxProfile(
    string Name,
    IReadOnlyList<string> Write,
    IReadOnlyList<string> Read,
    IReadOnlyList<string> Deny,
    SandboxNetwork Network,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<int> LocalPorts,
    IReadOnlyList<string> Env,
    IReadOnlyList<string> McpServers,
    SandboxOutside Outside);

/// <summary>The built-in profiles, and the resolution of the profiles of the settings.</summary>
public static class SandboxProfiles
{
    /// <summary>The name of the built-in profile with the package registries.</summary>
    public const string Workspace = "workspace";

    /// <summary>The name of the built-in profile without network.</summary>
    public const string Strict = "strict";

    /// <summary>The value of a scope that turns the sandbox off.</summary>
    public const string Off = "off";

    // Package registries, and the hosts certificate authorities answer revocation checks from (NuGet checks the
    // signatures of packages over plain HTTP): restores work, and none of them takes the data of a session.
    private static readonly string[] RegistryHosts =
    [
        "nuget.org", "*.nuget.org",
        "registry.npmjs.org", "registry.yarnpkg.com",
        "pypi.org", "files.pythonhosted.org",
        "crates.io", "*.crates.io",
        "ocsp.digicert.com", "crl3.digicert.com", "crl4.digicert.com",
        "ocsp.sectigo.com", "crl.sectigo.com",
        "ocsp.globalsign.com", "crl.globalsign.com",
        "crl.microsoft.com", "oneocsp.microsoft.com",
    ];

    /// <summary>Gets the built-in profiles: <see cref="Workspace"/> and <see cref="Strict"/>.</summary>
    public static IReadOnlyList<ResolvedSandboxProfile> BuiltIn { get; } =
    [
        new(Workspace, [], [], [], SandboxNetwork.Hosts, RegistryHosts, [], [], [], SandboxOutside.Ask),
        new(Strict, [], [], [], SandboxNetwork.Off, [], [], [], [], SandboxOutside.Never),
    ];

    /// <summary>Gets whether a name is a valid profile name: letters, digits, <c>-</c> and <c>_</c>, not <see cref="Off"/>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True when a profile can have that name.</returns>
    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && !name.Equals(Off, StringComparison.OrdinalIgnoreCase)
           && name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    /// <summary>Lists the names of the profiles: the built-in ones, then those of the settings.</summary>
    /// <param name="profiles">The profiles of the settings.</param>
    /// <returns>The names, each once.</returns>
    public static IReadOnlyList<string> Names(IEnumerable<SandboxProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return [.. BuiltIn.Select(static profile => profile.Name)
            .Concat(profiles.Select(static profile => profile.Name).Where(IsValidName))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Resolves a profile by its name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="profiles">The profiles of the settings.</param>
    /// <returns>The profile with every value decided.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="profiles"/> is null.</exception>
    /// <exception cref="SandboxProfileException">No profile has that name, or its <c>extends</c> loop or name an unknown profile.</exception>
    public static ResolvedSandboxProfile Resolve(string name, IEnumerable<SandboxProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(profiles);
        var byName = new Dictionary<string, SandboxProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (!IsValidName(profile.Name)) throw new SandboxProfileException($"'{profile.Name}' is not a valid name for a sandbox profile: use letters, digits, '-' and '_'.");
            byName[profile.Name] = profile;
        }

        return Resolve(name, byName, []);
    }

    private static ResolvedSandboxProfile Resolve(string name, Dictionary<string, SandboxProfile> byName, List<string> visiting)
    {
        if (visiting.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new SandboxProfileException($"The sandbox profile '{name}' extends itself: {string.Join(" > ", visiting.Append(name))}.");
        var builtIn = BuiltIn.FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!byName.TryGetValue(name, out var written))
        {
            return builtIn ?? throw new SandboxProfileException($"There is no sandbox profile '{name}'.");
        }

        visiting.Add(name);
        ResolvedSandboxProfile result;
        if (written.Extends is { } baseName && !string.IsNullOrWhiteSpace(baseName))
        {
            if (!byName.ContainsKey(baseName) && !BuiltIn.Any(profile => profile.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
                throw new SandboxProfileException($"The sandbox profile '{name}' extends '{baseName}', which does not exist.");
            var basis = baseName.Equals(name, StringComparison.OrdinalIgnoreCase) && builtIn is not null ? builtIn : Resolve(baseName, byName, visiting);
            result = new(written.Name,
                Union(basis.Write, written.Write), Union(basis.Read, written.Read), Union(basis.Deny, written.Deny),
                written.Network ?? basis.Network, Union(basis.Hosts, written.Hosts), [.. basis.LocalPorts.Union(written.LocalPorts ?? [])],
                Union(basis.Env, written.Env), Union(basis.McpServers, written.McpServers), written.Outside ?? basis.Outside);
        }
        else
        {
            var basis = builtIn ?? new ResolvedSandboxProfile(written.Name, [], [], [], SandboxNetwork.Off, [], [], [], [], SandboxOutside.Ask);
            result = new(builtIn?.Name ?? written.Name,
                written.Write ?? basis.Write, written.Read ?? basis.Read, written.Deny ?? basis.Deny,
                written.Network ?? basis.Network, written.Hosts ?? basis.Hosts, written.LocalPorts ?? basis.LocalPorts,
                written.Env ?? basis.Env, written.McpServers ?? basis.McpServers, written.Outside ?? basis.Outside);
        }

        visiting.RemoveAt(visiting.Count - 1);
        Validate(result);
        return result;
    }

    private static void Validate(ResolvedSandboxProfile profile)
    {
        foreach (var host in profile.Hosts)
        {
            if (!SandboxHostPattern.IsValid(host))
                throw new SandboxProfileException($"The sandbox profile '{profile.Name}' lists '{host}', which is not a host name or '*.' and a domain.");
        }

        foreach (var port in profile.LocalPorts)
        {
            if (port is < 1 or > 65535) throw new SandboxProfileException($"The sandbox profile '{profile.Name}' lists the port {port}, which is not a port.");
        }
    }

    private static IReadOnlyList<string> Union(IReadOnlyList<string> basis, IReadOnlyList<string>? added)
        => added is null or { Count: 0 } ? basis : [.. basis.Concat(added).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>A sandbox profile that cannot be used: unknown, invalid, or extending itself.</summary>
public sealed class SandboxProfileException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What is wrong, for the user.</param>
    public SandboxProfileException(string message) : base(message)
    {
    }
}
