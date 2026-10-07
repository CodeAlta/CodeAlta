using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;
using Tomlyn;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The plugins of the Settings page: lists them from what is on disk (source packages under the global and
/// project plugin folders, plus plugin ids named in configuration) with what the running host did with each,
/// saves plugin enablement in the global or project configuration, creates a source plugin, and builds one
/// again in the running host.
/// </summary>
/// <remarks>
/// The page lists the built-in plugins itself, so one is returned here only when configuration names
/// it. Enablement can be saved for any well-formed plugin id; a source plugin of the running host is started or
/// stopped at once, and the plugin runtime reads the rest when the host starts.
/// </remarks>
[NeoRpcService("plugins", Version = 1)]
internal sealed class PluginsService
{
    /// <summary>Largest number of plugins returned by one listing.</summary>
    internal const int MaximumPlugins = 256;

    /// <summary>The most compiler errors listed for one plugin.</summary>
    internal const int MaximumErrors = 5;

    private const int MaximumIdLength = 128;
    private const int MaximumDescriptionLength = 512;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly ProjectCatalog? _projects;
    private readonly CodeAltaConfigStore? _store;
    private readonly string? _epoch;
    private readonly PluginRuntimeManager? _runtime;
    private readonly PluginManagementModelBuilder _builder = new();
    private readonly SourcePluginDiscoveryService _discovery = new();
    private readonly Lock _gate = new();

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal PluginsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The host's project catalog; its global root holds the configuration and global plugins.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="runtime">The host's plugin runtime, which says what was started and loads a plugin again; null leaves that out.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal PluginsService(ProjectCatalog projects, string epoch, PluginRuntimeManager? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _projects = projects;
        _store = new CodeAltaConfigStore(projects.Options);
        _epoch = epoch;
        _runtime = runtime;
    }

    /// <summary>Lists discovered source plugins and configured plugin ids with their enablement.</summary>
    [NeoRpcMethod("list")]
    public async Task<PluginsListResponse> ListAsync(PluginsListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PluginsListResponse Failed(string status) => new(status, request.ProjectId, [], 0);
        if (_projects is null || _store is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        try
        {
            CodeAltaConfigDocument global;
            CodeAltaConfigDocument? local;
            lock (_gate)
            {
                global = _store.LoadGlobal();
                local = project.Root is null ? null : _store.LoadProject(project.Root);
            }

            // No built-in definitions: the page has their rows, whichever host runs them.
            var entries = _builder.Build([], _discovery.Discover(Roots(request.ProjectId, project.Root)), global, local);
            // What the running host did with the packages of its own folders, by the file of each.
            var hosted = (_runtime?.GetPackages() ?? []).ToDictionary(static package => package.Package.EntryFilePath, PathComparer);
            var plugins = new List<PluginsEntry>();
            foreach (var entry in entries)
            {
                if (plugins.Count == MaximumPlugins || !ValidId(entry.PluginId)) continue;
                var id = entry.PluginId!;
                var enabledGlobal = Configured(global, id);
                var enabledProject = Configured(local, id);
                var discovered = entry.State != PluginManagementState.UnknownConfig;
                var enabled = discovered ? entry.Enabled : enabledGlobal ?? enabledProject ?? true;
                var status = discovered && entry.SourcePath is { } source ? hosted.GetValueOrDefault(source) : null;
                // A package the host does not load (the one of another project) is not started.
                var (runtime, message) = _runtime is null || !discovered || !enabled ? (null, null) : status is null ? ("stopped", null) : RuntimeState(status);
                var folder = discovered ? new PluginFolder(entry.Scope == PluginScope.Project ? request.ProjectId : null, id) : (PluginFolder?)null;
                // A plugin that runs says its own name and what it does; the README of its package otherwise.
                var running = status?.Plugins.Count == 1 ? status.Plugins[0] : null;
                var description = Text(running?.Description) ?? (discovered ? Describe(entry.ReadmePath) : null);
                plugins.Add(new(id, Text(running?.DisplayName) ?? entry.DisplayName, description, discovered ? "Source" : "Config",
                    entry.Scope.ToString(), enabledGlobal, enabledProject, enabled,
                    discovered ? entry.State.ToString() : enabled ? "Configured" : nameof(PluginManagementState.Disabled), runtime, message,
                    folder?.Id, discovered ? Path.GetDirectoryName(entry.SourcePath) : null, status is not null, status?.SourceChanged ?? false, Errors(status)));
            }

            return new("ok", request.ProjectId, plugins, entries.Count - plugins.Count);
        }
        catch (InvalidDataException)
        {
            return Failed("config_invalid"); // A configuration file that does not parse hides the enablement.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("read_failed");
        }
    }

    /// <summary>Saves the enablement override of one plugin id in the global or project configuration.</summary>
    [NeoRpcMethod("setEnabled")]
    public async Task<PluginsMutationResponse> SetEnabledAsync(PluginsSetEnabledRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null || _store is null) return new("unavailable", null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null);
        var projectScope = string.Equals(request.Scope, "Project", StringComparison.OrdinalIgnoreCase);
        if (!projectScope && !string.Equals(request.Scope, "Global", StringComparison.OrdinalIgnoreCase)) return new("invalid", "The scope must be Global or Project.");
        if (!ValidId(request.Id)) return new("invalid", "A plugin id starts with a letter or digit and uses letters, digits, '.', '_' or '-' (at most 128).");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, null);
        if (projectScope && project.Root is null) return new("invalid", "The project scope requires a project.");
        try
        {
            lock (_gate)
            {
                if (projectScope)
                {
                    _store.SaveProjectPluginEnabled(project.Root!, request.Id!, request.Enabled);
                }
                else
                {
                    // A missing global file means the bundled template, exactly as the configuration editor reads it.
                    _store.EnsureGlobalConfigExists();
                    _store.SaveGlobalPluginEnabled(request.Id!, request.Enabled);
                }
            }
        }
        catch (Exception exception) when (exception is TomlException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new("config_invalid", null); // The file does not parse and is left as it is.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("write_failed", null);
        }

        // The source plugins of the running host follow at once: started, or stopped.
        var applied = false;
        foreach (var package in Hosted().Where(package => string.Equals(package.Package.PackageId, request.Id, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await _runtime!.ReloadPackageAsync(package.Package, force: false, cancellationToken).ConfigureAwait(false);
                applied = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // The host is closing: the saved configuration is read at the next start.
            }
        }

        return new("ok", null, applied);
    }

    /// <summary>
    /// Builds a source plugin again and replaces its running plugins, or starts it. When the build fails, what
    /// ran keeps running and the answer is <c>build_failed</c> with the first error.
    /// </summary>
    [NeoRpcMethod("reload")]
    public async Task<PluginsMutationResponse> ReloadAsync(PluginsPackageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null || _runtime is null) return new("unavailable", null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null);
        var (status, root) = await RootAsync(request.ProjectId, request.Scope, cancellationToken).ConfigureAwait(false);
        if (root is null) return new(status, null);
        if (!ValidId(request.Id)) return new("invalid", null);
        var file = Path.Combine(root.RootPath, request.Id!, "plugin.cs");
        if (!File.Exists(file)) return new("unknown", null);
        if (Hosted().FirstOrDefault(package => PathComparer.Equals(package.Package.EntryFilePath, Path.GetFullPath(file))) is not { } hosted)
        {
            // Plugins that were not started for this run, or a plugin of a project the host was not started in.
            return new(_runtime.StartOptions is null ? "unavailable" : "not_loaded", null);
        }

        try
        {
            var result = await _runtime.ReloadPackageAsync(hosted.Package, force: false, cancellationToken).ConfigureAwait(false);
            return result.Change switch
            {
                PluginPackageChange.BuildFailed => new("build_failed", Errors(result.Status).FirstOrDefault() ?? RuntimeState(result.Status).Message),
                PluginPackageChange.StartFailed => new("start_failed", RuntimeState(result.Status).Message),
                PluginPackageChange.Disabled or PluginPackageChange.Stopped => new("disabled", null),
                _ => new("ok", null, true),
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return new("unavailable", null);
        }
    }

    /// <summary>
    /// Creates a source plugin with a first <c>plugin.cs</c>, in the plugin folder of the user or of a project,
    /// and starts it when the running host loads that folder.
    /// </summary>
    [NeoRpcMethod("create")]
    public async Task<PluginsCreateResponse> CreateAsync(PluginsCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PluginsCreateResponse Refused(string status, string? message = null) => new(status, message, null, null, null);
        if (_projects is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        var (status, root) = await RootAsync(request.ProjectId, request.Scope, cancellationToken).ConfigureAwait(false);
        if (root is null) return Refused(status);
        if (request.Description is { Length: > MaximumDescriptionLength }) return Refused("invalid", "The description is too long.");
        var created = SourcePluginScaffold.Create(root, request.Id?.Trim(), displayName: null, request.Description);
        if (created.Package is not { } package)
        {
            return Refused(created.Error == "invalid_id" ? "invalid" : created.Error!, created.Error == "write_failed" ? null : created.Message);
        }

        if (Hosted().FirstOrDefault(found => PathComparer.Equals(found.Package.EntryFilePath, package.EntryFilePath)) is { } hosted)
        {
            try
            {
                await _runtime!.ReloadPackageAsync(hosted.Package, force: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // The host is closing: the plugin is started the next time.
            }
        }

        return new("ok", null, PluginFolder.Of(package, request.ProjectId).Id, package.PackageDirectory, package.PackageId);
    }

    /// <summary>
    /// Says what the running host did with an enabled source package: <c>running</c> when one of its plugins is
    /// active, <c>unsupported</c> when none supports this application, <c>failed</c> with the first error when
    /// its build or start failed, and <c>stopped</c> when the host has not started it (enabled since the start).
    /// </summary>
    /// <param name="status">The package, as the plugin runtime describes it.</param>
    internal static (string State, string? Message) RuntimeState(PluginPackageStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var error = status.Diagnostics.FirstOrDefault(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error);
        return status.State switch
        {
            // The first error also says why the last build of a running plugin was not loaded.
            PluginPackageState.Running => ("running", error is null ? null : Clean(error.Message)),
            PluginPackageState.Failed => ("failed", error is null ? null : Clean(error.Message)),
            PluginPackageState.Unsupported => ("unsupported", null),
            _ => ("stopped", null),
        };
    }

    // The compiler errors of the last build, as the compiler writes them without the folder.
    private static IReadOnlyList<string> Errors(PluginPackageStatus? status)
        => status?.Build is not { Succeeded: false } build ? [] : [.. build.Diagnostics
            .Where(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error)
            .Take(MaximumErrors)
            .Select(static diagnostic => Clean(diagnostic.LineNumber > 0
                ? $"{diagnostic.File}({diagnostic.LineNumber},{diagnostic.ColumnNumber}): error {diagnostic.Code}: {diagnostic.Message}"
                : $"error {diagnostic.Code}: {diagnostic.Message}"))];

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : Clean(value);

    private static string Clean(string message)
    {
        var text = new string([.. message.Where(static character => !char.IsControl(character))]).Trim();
        return text.Length <= MaximumDescriptionLength ? text : text[..MaximumDescriptionLength];
    }

    private IReadOnlyList<PluginPackageStatus> Hosted() => _runtime?.GetPackages() ?? [];

    private List<PluginRoot> Roots(string? projectId, string? projectRoot)
    {
        var roots = new List<PluginRoot> { new() { RootPath = Path.Combine(_projects!.Options.GlobalRoot, "plugins"), Scope = PluginScope.Global } };
        if (projectRoot is not null)
        {
            roots.Add(new() { RootPath = Path.Combine(projectRoot, ".alta", "plugins"), Scope = PluginScope.Project, ProjectId = projectId, ProjectPath = projectRoot });
        }

        return roots;
    }

    // The plugin folder a request names by its scope, or why it names none.
    private async Task<(string Status, PluginRoot? Root)> RootAsync(string? projectId, string? scope, CancellationToken cancellationToken)
    {
        var projectScope = string.Equals(scope, "Project", StringComparison.OrdinalIgnoreCase);
        if (!projectScope && !string.Equals(scope, "Global", StringComparison.OrdinalIgnoreCase)) return ("invalid", null);
        var project = await SettingsProjectScope.ResolveAsync(_projects!, projectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return (project.Status, null);
        if (projectScope && project.Root is null) return ("invalid", null);
        return ("ok", Roots(projectId, project.Root)[projectScope ? 1 : 0]);
    }

    private static bool? Configured(CodeAltaConfigDocument? document, string id)
        => document?.Plugins?.FirstOrDefault(plugin => string.Equals(plugin.Key, id, StringComparison.OrdinalIgnoreCase)).Value?.Enabled;

    private static bool ValidId(string? id)
        => id is { Length: > 0 and <= MaximumIdLength } && char.IsAsciiLetterOrDigit(id[0])
           && id.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    // The first heading or sentence of the package README, read with a hard limit.
    private static string? Describe(string? readmePath)
    {
        if (readmePath is null) return null;
        try
        {
            using var reader = new StreamReader(readmePath);
            var buffer = new char[4096];
            var text = new string(buffer, 0, reader.ReadBlock(buffer));
            foreach (var line in text.Split('\n'))
            {
                var summary = new string(line.Where(static character => !char.IsControl(character)).ToArray()).Trim().TrimStart('#').Trim();
                if (summary.Length > 0) return summary.Length <= MaximumDescriptionLength ? summary : summary[..MaximumDescriptionLength];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A README that cannot be read only leaves the description empty.
        }

        return null;
    }
}

internal sealed record PluginsListRequest(string? ExpectedEpoch, string? ProjectId);
internal sealed record PluginsListResponse(string Status, string? ProjectId, IReadOnlyList<PluginsEntry> Plugins, int Omitted);

/// <summary>
/// One plugin. The kind is <c>Source</c> (a discovered package) or <c>Config</c> (an id only named in configuration);
/// the configured values are null where that configuration has no override; the state is <c>Enabled</c>,
/// <c>Disabled</c>, <c>Failed</c> or <c>Configured</c>. <c>Runtime</c> says what the running host did with an
/// enabled source package (<c>running</c>, <c>unsupported</c>, <c>failed</c>, <c>stopped</c>) and is null when
/// that is not known; <c>RuntimeMessage</c> is its first error.
/// </summary>
/// <param name="Folder">For a source package, the id that names its folder to the code editor.</param>
/// <param name="Path">For a source package, the path of its folder.</param>
/// <param name="Loadable">Whether the running host loads the package: one of its own plugin folders holds it.</param>
/// <param name="Changed">Whether the source on disk is another one than the source the host runs or last built.</param>
/// <param name="Errors">The compiler errors of its last build when that build failed.</param>
internal sealed record PluginsEntry(string Id, string Name, string? Description, string Kind, string Scope, bool? EnabledGlobal,
    bool? EnabledProject, bool Enabled, string State, string? Runtime = null, string? RuntimeMessage = null,
    string? Folder = null, string? Path = null, bool Loadable = false, bool Changed = false, IReadOnlyList<string>? Errors = null);
internal sealed record PluginsSetEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Id, bool Enabled);

/// <summary>Names a source package: the scope is <c>Global</c> or <c>Project</c>, the project the one of a project package.</summary>
internal sealed record PluginsPackageRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Id);

/// <summary>Asks for a new source package; the description is the sentence its README and its plugin start with.</summary>
internal sealed record PluginsCreateRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Id, string? Description);

/// <summary>
/// The answer to a change. <c>Applied</c> says the running host already follows it; without it the change is read
/// the next time the host starts.
/// </summary>
internal sealed record PluginsMutationResponse(string Status, string? Message, bool Applied = false);

/// <summary>The package that was created: the id of its folder for the code editor, its path and its name.</summary>
internal sealed record PluginsCreateResponse(string Status, string? Message, string? Folder, string? Path, string? Name);
