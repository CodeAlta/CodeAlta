namespace CodeAlta.Plugins;

/// <summary>Selects the host's source-plugin assembly compatibility policy, independently of UI interactivity.</summary>
/// <remarks>
/// Reusable hosts default to Neutral. Hosts preserving rich terminal source-plugin compilation must explicitly
/// select Terminal for generation and loading, including their noninteractive command paths. This policy is
/// not a security sandbox or a restriction on packages explicitly requested by trusted plugin source.
/// Additional reference/shared-name lists contain individual literal simple names, not MSBuild expressions,
/// item lists or assembly display names; they cannot remove mandatory profile identities.
/// </remarks>
public enum PluginAuthoringProfile
{
    /// <summary>Uses neutral authoring references and refuses terminal assembly dependencies before type discovery.</summary>
    Neutral,

    /// <summary>Includes rich terminal authoring references and shares terminal assembly identity with the host.</summary>
    Terminal,
}

internal enum PluginAssemblyBinding
{
    PrivateOrDefault,
    Host,
    Forbidden,
}

// No runtime initialization or process-wide mutable state: these operations consume only explicit facts.
internal static class PluginAuthoringPolicy
{
    internal const string PolicyVersion = "1";
    internal const string HostApiVersion = "1.0.0";

    internal static void Validate(PluginAuthoringProfile profile)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
    }

    internal static bool IsTerminalAssembly(string name)
        => string.Equals(name, "CodeAlta.Plugins.Tui", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "XenoAtom.Terminal", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("XenoAtom.Terminal.", StringComparison.OrdinalIgnoreCase);

    internal static string[] GetHostReferences(PluginAuthoringProfile profile, IEnumerable<string>? additionalNames = null)
    {
        var defaults = PluginRootBuildFileGenerator.DefaultHostAssemblyNames.AsEnumerable();
        if (profile == PluginAuthoringProfile.Terminal) defaults = defaults.Append("CodeAlta.Plugins.Tui");
        return Merge(profile, defaults, additionalNames);
    }

    internal static string[] GetPackageReferences(PluginAuthoringProfile profile, IEnumerable<string>? additionalNames = null)
        => Merge(profile, PluginRootBuildFileGenerator.DefaultSharedPackageNames, additionalNames);

    internal static string[] GetSharedAssemblies(PluginAuthoringProfile profile, IEnumerable<string>? additionalNames = null)
        => Merge(profile, PluginAssemblyLoader.DefaultHostSharedAssemblyNames, additionalNames);

    private static string[] Merge(PluginAuthoringProfile profile, IEnumerable<string> defaults, IEnumerable<string>? additionalNames)
    {
        Validate(profile);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in defaults)
            if (profile == PluginAuthoringProfile.Terminal || !IsTerminalAssembly(name)) names.Add(name);
        if (additionalNames is not null)
        {
            foreach (var name in additionalNames)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                // Each override is one literal simple name, not an MSBuild item list/expression or assembly display name.
                if (!string.Equals(name, name.Trim(), StringComparison.Ordinal) || name.AsSpan().IndexOfAny(";,/\\$%@") >= 0 || name.Any(char.IsControl))
                    throw new ArgumentException($"Reference '{name}' must be a literal simple name.", nameof(additionalNames));
                if (profile == PluginAuthoringProfile.Neutral && IsTerminalAssembly(name))
                    throw new ArgumentException($"Reference '{name}' requires the Terminal authoring profile.", nameof(additionalNames));
                names.Add(name);
            }
        }
        return names.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static PluginAssemblyBinding ClassifyAssembly(PluginAuthoringProfile profile, string name, IReadOnlySet<string> sharedNames)
    {
        Validate(profile);
        ArgumentNullException.ThrowIfNull(sharedNames);
        if (IsTerminalAssembly(name))
            return profile == PluginAuthoringProfile.Terminal ? PluginAssemblyBinding.Host : PluginAssemblyBinding.Forbidden;
        // Mandatory neutral identities remain reserved even if a caller supplies an empty/custom set.
        return sharedNames.Contains(name) || PluginAssemblyLoader.DefaultHostSharedAssemblyNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? PluginAssemblyBinding.Host : PluginAssemblyBinding.PrivateOrDefault;
    }

    internal static void ValidateBuildOptions(PluginRootBuildFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options.AuthoringProfile);
        _ = GetHostReferences(options.AuthoringProfile, options.HostAssemblyNames);
        _ = GetPackageReferences(options.AuthoringProfile, options.SharedPackageNames);
    }

    internal static IReadOnlyList<PluginBuildRequest> FilterBuildRequests(
        IReadOnlyList<PluginBuildRequest> requests, IEnumerable<string> successfulRoots)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(successfulRoots);
        var roots = new HashSet<string>(successfulRoots, StringComparer.OrdinalIgnoreCase);
        return requests.Where(request => roots.Contains(request.Package.Root.RootPath)).ToArray();
    }
}
