using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.App;

internal sealed class PluginManagementService
{
    private readonly CatalogOptions _catalogOptions;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly PluginManagementModelBuilder _modelBuilder = new();
    private readonly SourcePluginDiscoveryService _sourceDiscovery = new();

    public PluginManagementService(CatalogOptions catalogOptions, Func<ProjectDescriptor?> getSelectedProject)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        _catalogOptions = catalogOptions;
        _getSelectedProject = getSelectedProject;
    }

    public PluginManagementSnapshot LoadSnapshot()
    {
        var selectedProject = _getSelectedProject();
        var configStore = new CodeAltaConfigStore(_catalogOptions);
        var globalConfig = configStore.LoadGlobal();
        var projectConfig = configStore.LoadProject(selectedProject?.ProjectPath);
        var safeMode = PluginRuntimeConfigResolver.IsSafeModeEnabled([]);
        var sourcePackages = DiscoverSourcePackages(selectedProject).ToArray();
        var entries = _modelBuilder.Build(
            CodeAltaBuiltInPlugins.All,
            sourcePackages,
            globalConfig,
            projectConfig,
            pendingChanges: [],
            buildResults: [],
            diagnostics: [],
            contributions: [],
            safeMode);
        return new PluginManagementSnapshot(EnrichDescriptions(entries), safeMode, selectedProject?.ProjectPath);
    }

    public void SetPluginEnabled(PluginManagementEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.PluginId))
        {
            throw new ArgumentException("Plugin id is required to save enablement.", nameof(entry));
        }

        var configStore = new CodeAltaConfigStore(_catalogOptions);
        if (entry.Scope == PluginScope.Project)
        {
            var selectedProject = _getSelectedProject()
                ?? throw new InvalidOperationException("A project must be selected to save project plugin settings.");
            configStore.SaveProjectPluginEnabled(selectedProject.ProjectPath, entry.PluginId, enabled);
            return;
        }

        configStore.SaveGlobalPluginEnabled(entry.PluginId, enabled);
    }

    private IEnumerable<SourcePluginPackage> DiscoverSourcePackages(ProjectDescriptor? selectedProject)
    {
        var globalRoot = Path.Combine(_catalogOptions.GlobalRoot, "plugins");
        if (Directory.Exists(globalRoot))
        {
            foreach (var package in _sourceDiscovery.Discover(new PluginRoot { RootPath = globalRoot, Scope = PluginScope.Global }))
            {
                yield return package;
            }
        }

        if (selectedProject is null)
        {
            yield break;
        }

        var projectRoot = Path.Combine(selectedProject.ProjectPath, ".alta", "plugins");
        if (!Directory.Exists(projectRoot))
        {
            yield break;
        }

        foreach (var package in _sourceDiscovery.Discover(new PluginRoot
        {
            RootPath = projectRoot,
            Scope = PluginScope.Project,
            ProjectId = selectedProject.Id,
            ProjectPath = selectedProject.ProjectPath,
        }))
        {
            yield return package;
        }
    }

    private static IReadOnlyList<PluginManagementEntry> EnrichDescriptions(IReadOnlyList<PluginManagementEntry> entries)
    {
        var builtInDescriptions = CodeAltaBuiltInPlugins.All
            .Where(static plugin => !string.IsNullOrWhiteSpace(plugin.Description))
            .ToDictionary(static plugin => plugin.Id, static plugin => plugin.Description!, StringComparer.OrdinalIgnoreCase);
        return entries
            .Select(entry => entry with
            {
                Metadata = AddDescription(entry.Metadata, ResolveDescription(entry, builtInDescriptions)),
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> AddDescription(IReadOnlyDictionary<string, string> metadata, string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return metadata;
        }

        var updated = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["Description"] = description.Trim(),
        };
        return updated;
    }

    private static string? ResolveDescription(PluginManagementEntry entry, IReadOnlyDictionary<string, string> builtInDescriptions)
    {
        if (entry.LoadUnitKind == PluginLoadUnitKind.BuiltIn &&
            entry.PluginId is not null &&
            builtInDescriptions.TryGetValue(entry.PluginId, out var builtInDescription))
        {
            return builtInDescription;
        }

        return ReadReadmeDescription(entry.ReadmePath);
    }

    private static string? ReadReadmeDescription(string? readmePath)
    {
        if (string.IsNullOrWhiteSpace(readmePath) || !File.Exists(readmePath))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(readmePath))
            {
                var trimmed = line.Trim().TrimStart('#').Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return trimmed;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}

internal sealed record PluginManagementSnapshot(
    IReadOnlyList<PluginManagementEntry> Entries,
    bool SafeMode,
    string? ProjectPath);
