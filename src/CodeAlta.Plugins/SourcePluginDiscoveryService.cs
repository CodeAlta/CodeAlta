using System.Text.RegularExpressions;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Discovers source plugin packages under CodeAlta plugin roots.
/// </summary>
public sealed partial class SourcePluginDiscoveryService
{
    private static readonly string[] UnsupportedBuildFileNames =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
    ];

    /// <summary>
    /// Discovers source plugin packages under a plugin root.
    /// </summary>
    /// <param name="root">The plugin root.</param>
    /// <returns>Discovered source plugin packages. Missing roots return an empty list.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="root"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<SourcePluginPackage> Discover(PluginRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!Directory.Exists(root.RootPath))
        {
            return [];
        }

        var packages = new List<SourcePluginPackage>();
        var seenPackageIds = new Dictionary<string, string>(GetPathComparer());
        foreach (var directory in Directory.EnumerateDirectories(root.RootPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var packageId = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(packageId))
            {
                continue;
            }

            var entryFilePath = Path.Combine(directory, "plugin.cs");
            if (!File.Exists(entryFilePath))
            {
                continue;
            }

            var diagnostics = new List<PluginRuntimeDiagnostic>();
            if (!PackageIdRegex().IsMatch(packageId))
            {
                diagnostics.Add(PluginRuntimeDiagnostic.Error(
                    PluginRuntimeDiagnosticSource.Discovery,
                    $"Plugin package id '{packageId}' contains unsupported characters. Use letters, digits, '.', '_', or '-' and start with a letter or digit.",
                    packageId,
                    directory));
            }

            if (seenPackageIds.TryGetValue(packageId, out var existingDirectory))
            {
                diagnostics.Add(new PluginRuntimeDiagnostic
                {
                    Severity = PluginDiagnosticSeverity.Error,
                    Source = PluginRuntimeDiagnosticSource.Discovery,
                    Message = $"Duplicate plugin package id '{packageId}' was discovered. Another package with the same id is at '{existingDirectory}'.",
                    PackageId = packageId,
                    Path = directory,
                    Metadata = new Dictionary<string, string> { ["DuplicateOf"] = existingDirectory },
                });
            }
            else
            {
                seenPackageIds.Add(packageId, directory);
            }

            var unsupportedBuildFiles = UnsupportedBuildFileNames
                .Select(name => Path.Combine(directory, name))
                .Where(File.Exists)
                .ToArray();
            foreach (var unsupportedBuildFile in unsupportedBuildFiles)
            {
                diagnostics.Add(PluginRuntimeDiagnostic.Error(
                    PluginRuntimeDiagnosticSource.Discovery,
                    $"Source plugin packages must not contain '{Path.GetFileName(unsupportedBuildFile)}' in v1 because it can shadow CodeAlta-generated root build files.",
                    packageId,
                    unsupportedBuildFile));
            }

            packages.Add(new SourcePluginPackage
            {
                PackageId = packageId,
                Root = root,
                PackageDirectory = PluginRuntimePathService.NormalizeDirectory(directory),
                EntryFilePath = Path.GetFullPath(entryFilePath),
                Sidecars = DiscoverSidecars(directory),
                UnsupportedBuildFiles = unsupportedBuildFiles,
                Diagnostics = diagnostics,
            });
        }

        return packages;
    }

    /// <summary>
    /// Discovers source plugin packages under multiple plugin roots.
    /// </summary>
    /// <param name="roots">The plugin roots.</param>
    /// <returns>Discovered source plugin packages in root and package order.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="roots"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<SourcePluginPackage> Discover(IEnumerable<PluginRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var packages = roots.SelectMany(Discover).ToList();
        var seenPackages = new Dictionary<(PluginScope Scope, string PackageId), SourcePluginPackage>();
        for (var index = 0; index < packages.Count; index++)
        {
            var package = packages[index];
            var key = (package.Root.Scope, package.PackageId);
            if (!seenPackages.TryGetValue(key, out var existingPackage))
            {
                seenPackages.Add(key, package);
                continue;
            }

            var diagnostics = package.Diagnostics.Concat(
            [
                new PluginRuntimeDiagnostic
                {
                    Severity = PluginDiagnosticSeverity.Error,
                    Source = PluginRuntimeDiagnosticSource.Discovery,
                    Message = $"Duplicate {package.Root.Scope.ToString().ToLowerInvariant()} plugin package id '{package.PackageId}' was discovered. Another package with the same id is at '{existingPackage.PackageDirectory}'.",
                    PackageId = package.PackageId,
                    Path = package.PackageDirectory,
                    Metadata = new Dictionary<string, string> { ["DuplicateOf"] = existingPackage.PackageDirectory },
                },
            ]).ToArray();
            packages[index] = package with { Diagnostics = diagnostics };
        }

        return packages;
    }

    private static SourcePluginSidecars DiscoverSidecars(string packageDirectory)
    {
        // The file as it is named on disk: where names ignore case, a path written in another case opens the
        // file and names none for git or for another system. Of several, the lowercase one, as before.
        string? Readme()
        {
            try
            {
                return Directory.EnumerateFiles(packageDirectory, "*.md", SearchOption.TopDirectoryOnly)
                    .Where(static path => string.Equals(Path.GetFileName(path), "readme.md", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal)
                    .Select(Path.GetFullPath)
                    .FirstOrDefault();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        string? ExistingDirectory(string name)
        {
            var path = Path.Combine(packageDirectory, name);
            return Directory.Exists(path) ? PluginRuntimePathService.NormalizeDirectory(path) : null;
        }

        return new SourcePluginSidecars
        {
            ReadmePath = Readme(),
            SkillsDirectory = ExistingDirectory("skills"),
            PromptsDirectory = ExistingDirectory("prompts"),
            TemplatesDirectory = ExistingDirectory("templates"),
            ThemesDirectory = ExistingDirectory("themes"),
            AssetsDirectory = ExistingDirectory("assets"),
        };
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();
}
