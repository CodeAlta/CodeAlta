using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace CodeAlta.Plugins;

internal sealed record PluginAssemblyMetadata(string Name, IReadOnlyList<AssemblyName> References);
internal readonly record struct PluginAssemblyReferenceResolution(string Path, bool IsDefault);

// Assembly compatibility admission, not a trusted-code sandbox. Nothing here enumerates types or loads assemblies.
internal static class PluginAssemblyReferenceAdmission
{
    internal static void ValidateClosure(
        PluginAuthoringProfile profile,
        string entryPath,
        Func<string, PluginAssemblyMetadata> readMetadata,
        Func<AssemblyName, bool, PluginAssemblyReferenceResolution?> resolveReference,
        Func<string, bool> isPlatform,
        IEnumerable<string>? additionalSharedNames = null)
    {
        PluginAuthoringPolicy.Validate(profile);
        var shared = new HashSet<string>(PluginAuthoringPolicy.GetSharedAssemblies(profile, additionalSharedNames), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<PluginAssemblyReferenceResolution> { new(entryPath, false) };
        // Main LoadFromAssemblyPath bypasses Load: inspect its declared identity even for Terminal.
        var main = readMetadata(entryPath);
        if (string.IsNullOrWhiteSpace(main.Name)) throw new FileLoadException($"Plugin artifact '{entryPath}' has no assembly name.");
        if (PluginAuthoringPolicy.ClassifyAssembly(profile, main.Name, shared) != PluginAssemblyBinding.PrivateOrDefault)
            throw new FileLoadException($"Plugin artifact '{entryPath}' declares reserved host assembly identity '{main.Name}'.");
        if (profile == PluginAuthoringProfile.Terminal) return;
        VisitReferences(main, false);

        void VisitReferences(PluginAssemblyMetadata metadata, bool sourceIsDefault)
        {
            foreach (var name in metadata.References)
            {
                // Check before either resolution or opening a dependency, including platform candidates.
                if (PluginAuthoringPolicy.IsTerminalAssembly(name.Name ?? string.Empty))
                    throw new FileLoadException($"Assembly '{name.Name}' requires the Terminal authoring profile (referenced by '{metadata.Name}').");
                var resolution = resolveReference(name, sourceIsDefault);
                if (resolution is not { } resolved || string.IsNullOrWhiteSpace(resolved.Path))
                    throw new FileLoadException($"Neutral admission cannot inspect dependency '{name.FullName}' referenced by '{metadata.Name}'.");
                if (isPlatform(resolved.Path) || !visited.Add(resolved)) continue;
                var dependency = readMetadata(resolved.Path);
                if (!string.Equals(dependency.Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    throw new FileLoadException($"Dependency '{resolved.Path}' declares '{dependency.Name}', not requested assembly '{name.Name}'.");
                VisitReferences(dependency, resolved.IsDefault);
            }
        }
    }

    // A Default ancestor's dependencies stay in Default. Only plugin-domain nodes may consult the plugin ADR.
    internal static PluginAssemblyReferenceResolution? ResolveReference(
        AssemblyName name, bool sourceIsDefault, bool hostShared,
        Func<AssemblyName, string?> resolvePrivate, Func<AssemblyName, string?> resolveDefault)
    {
        if (!sourceIsDefault && !hostShared)
        {
            var privatePath = resolvePrivate(name);
            if (privatePath is not null) return new PluginAssemblyReferenceResolution(privatePath, false);
        }
        var defaultPath = resolveDefault(name);
        return defaultPath is null ? null : new PluginAssemblyReferenceResolution(defaultPath, true);
    }

    internal static void Inspect(
        string mainAssemblyPath, PluginAuthoringProfile profile,
        IReadOnlySet<string> sharedNames, Func<AssemblyName, string?> resolvePrivate)
    {
        // Only this actual runtime assembly is skipped. System.* names and TPA entries are NOT platform grants.
        // Every reachable other framework, host, application and third-party assembly is inspected as metadata.
        var coreLibraryPath = typeof(object).Assembly.Location;
        ValidateClosure(profile, mainAssemblyPath, ReadMetadata, ResolveForAdmission,
            path => string.Equals(path, coreLibraryPath, StringComparison.Ordinal), sharedNames);

        PluginAssemblyReferenceResolution? ResolveForAdmission(AssemblyName name, bool sourceIsDefault)
        {
            try
            {
                return ResolveReference(name, sourceIsDefault,
                    PluginAuthoringPolicy.ClassifyAssembly(profile, name.Name ?? string.Empty, sharedNames) == PluginAssemblyBinding.Host,
                    resolvePrivate, ResolveDefaultPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                // Narrow resolution boundary, including fileless Assembly.Location and invalid probing facts.
                throw new FileLoadException($"Cannot inspect resolution of '{name.FullName}': {ex.Message}", ex);
            }
        }
    }

    private static PluginAssemblyMetadata ReadMetadata(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new FileLoadException($"Metadata path '{path}' is not absolute.");
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly) throw new BadImageFormatException("The artifact has no assembly definition.");
            var definition = reader.GetAssemblyDefinition();
            var references = new List<AssemblyName>();
            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                var name = new AssemblyName
                {
                    Name = reader.GetString(reference.Name),
                    Version = reference.Version,
                    CultureName = reference.Culture.IsNil ? null : reader.GetString(reference.Culture),
                };
                var key = reader.GetBlobBytes(reference.PublicKeyOrToken);
                if ((reference.Flags & AssemblyFlags.PublicKey) != 0) name.SetPublicKey(key);
                else name.SetPublicKeyToken(key);
                references.Add(name);
            }
            return new PluginAssemblyMetadata(reader.GetString(definition.Name), references);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw new FileLoadException($"Cannot inspect plugin assembly metadata '{path}': {ex.Message}", path, ex);
        }
    }

    private static string? ResolveDefaultPath(AssemblyName name)
    {
        // The reserved-host Load route already uses simple-name identity from Default.Assemblies first.
        var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
        {
            if (loaded.IsDynamic || string.IsNullOrWhiteSpace(loaded.Location))
                throw new FileLoadException($"Neutral admission cannot inspect in-memory default assembly '{name.FullName}'.");
            return loaded.Location;
        }

        // TPA supplies default probing paths, NOT a whitelist of framework assemblies. Its app/package paths
        // go through the same metadata traversal as private paths. No fallback assembly is loaded to find its path.
        if (string.IsNullOrEmpty(name.CultureName) && AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPaths)
        {
            foreach (var path in trustedPaths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(Path.GetFileNameWithoutExtension(path), name.Name, StringComparison.OrdinalIgnoreCase)) return path;
        }

        if (AppContext.GetData("APP_PATHS") is string applicationPaths)
        {
            foreach (var directory in applicationPaths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Path.IsPathFullyQualified(directory))
                    throw new FileLoadException($"Default probing directory '{directory}' is not absolute.");
                foreach (var extension in new[] { ".dll", ".exe" })
                {
                    var path = Path.Combine(directory, name.CultureName ?? string.Empty, name.Name + extension);
                    if (File.Exists(path)) return path;
                }
            }
        }

        // Resolving/AssemblyResolve callbacks are executable policy, not inspectable path facts. Neutral declines
        // dependencies requiring them; it never invokes them or guesses a path from the cwd/stale plugin DLLs.
        return null;
    }
}
