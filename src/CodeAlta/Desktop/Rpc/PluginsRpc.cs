using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;
using Tomlyn;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Lists the plugins of the Settings page from what is on disk (source packages under the global and
/// project plugin folders, plus plugin ids named in configuration) and saves plugin enablement in the
/// global or project configuration.
/// </summary>
/// <remarks>
/// The page lists the built-in plugins itself, so one is returned here only when configuration names
/// it. Enablement can be saved for any well-formed plugin id; the plugin runtime reads it when the host
/// starts.
/// </remarks>
[NeoRpcService("plugins", Version = 1)]
internal sealed class PluginsService
{
    /// <summary>Largest number of plugins returned by one listing.</summary>
    internal const int MaximumPlugins = 256;

    private const int MaximumIdLength = 128;
    private const int MaximumDescriptionLength = 512;
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
    /// <param name="runtime">The host's plugin runtime, which says what was started; null leaves that out.</param>
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

            var roots = new List<PluginRoot> { new() { RootPath = Path.Combine(_projects.Options.GlobalRoot, "plugins"), Scope = PluginScope.Global } };
            if (project.Root is not null)
            {
                roots.Add(new()
                {
                    RootPath = Path.Combine(project.Root, ".alta", "plugins"), Scope = PluginScope.Project,
                    ProjectId = request.ProjectId, ProjectPath = project.Root,
                });
            }

            // No built-in definitions: the page has their rows, whichever host runs them.
            var entries = _builder.Build([], _discovery.Discover(roots), global, local);
            var running = (_runtime?.ActivePlugins ?? []).Select(static plugin => plugin.SourcePackage?.PackageId).OfType<string>().ToArray();
            var diagnostics = _runtime?.Diagnostics ?? [];
            var plugins = new List<PluginsEntry>();
            foreach (var entry in entries)
            {
                if (plugins.Count == MaximumPlugins || !ValidId(entry.PluginId)) continue;
                var id = entry.PluginId!;
                var enabledGlobal = Configured(global, id);
                var enabledProject = Configured(local, id);
                var discovered = entry.State != PluginManagementState.UnknownConfig;
                var enabled = discovered ? entry.Enabled : enabledGlobal ?? enabledProject ?? true;
                var (runtime, message) = _runtime is not null && discovered && enabled ? RuntimeState(id, running, diagnostics) : (null, null);
                plugins.Add(new(id, entry.DisplayName, discovered ? Describe(entry.ReadmePath) : null, discovered ? "Source" : "Config",
                    entry.Scope.ToString(), enabledGlobal, enabledProject, enabled,
                    discovered ? entry.State.ToString() : enabled ? "Configured" : nameof(PluginManagementState.Disabled), runtime, message));
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

            return new("ok", null);
        }
        catch (Exception exception) when (exception is TomlException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new("config_invalid", null); // The file does not parse and is left as it is.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("write_failed", null);
        }
    }

    /// <summary>
    /// Says what the running host did with an enabled source package: <c>running</c> when one of its plugins is
    /// active, <c>unsupported</c> when none supports this application, <c>failed</c> with the first error when
    /// its build or start failed, and <c>stopped</c> when the host has not started it (enabled since the start).
    /// </summary>
    /// <param name="packageId">The source package.</param>
    /// <param name="running">The packages of the active plugins.</param>
    /// <param name="diagnostics">The diagnostics of the plugin runtime.</param>
    internal static (string State, string? Message) RuntimeState(string packageId, IReadOnlyCollection<string> running, IReadOnlyList<PluginRuntimeDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(diagnostics);
        bool Same(string? other) => string.Equals(other, packageId, StringComparison.OrdinalIgnoreCase);
        if (running.Any(Same)) return ("running", null);
        var own = diagnostics.Where(diagnostic => Same(diagnostic.PackageId)).ToArray();
        if (own.FirstOrDefault(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Error) is { } error)
        {
            var text = new string(error.Message.Where(static character => !char.IsControl(character)).ToArray()).Trim();
            return ("failed", text.Length <= MaximumDescriptionLength ? text : text[..MaximumDescriptionLength]);
        }

        return own.Any(static diagnostic => diagnostic.Metadata.ContainsKey(PluginRuntimeManager.UnsupportedFrontendMetadataKey)) ? ("unsupported", null) : ("stopped", null);
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
/// that is not known; <c>RuntimeMessage</c> is the error of a failed one.
/// </summary>
internal sealed record PluginsEntry(string Id, string Name, string? Description, string Kind, string Scope, bool? EnabledGlobal,
    bool? EnabledProject, bool Enabled, string State, string? Runtime = null, string? RuntimeMessage = null);
internal sealed record PluginsSetEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Id, bool Enabled);
internal sealed record PluginsMutationResponse(string Status, string? Message);
