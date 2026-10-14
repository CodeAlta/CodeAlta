using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins;

namespace CodeAlta.Desktop;

/// <summary>What a session can use: the resources of its space that are turned on, by kind. Names only, never a secret.</summary>
/// <param name="SpaceId">The space of the session.</param>
/// <param name="McpServers">The MCP servers it can activate.</param>
/// <param name="Skills">The skills it is offered.</param>
/// <param name="Prompts">The agent prompts it can run with.</param>
/// <param name="Plugins">The plugins that add to it.</param>
internal sealed record SessionResources(string SpaceId, IReadOnlyList<string> McpServers, IReadOnlyList<string> Skills, IReadOnlyList<string> Prompts,
    IReadOnlyList<string> Plugins);

/// <summary>Reads what a session can use, for the panel that shows a session: the same filters as its runs.</summary>
internal sealed class DesktopSessionResources
{
    /// <summary>The most names one kind lists.</summary>
    internal const int MaximumNames = 128;

    private readonly CodeAltaHost _host;
    private readonly string? _home;
    private readonly IReadOnlyList<BuiltInPluginDefinition> _builtIns;

    /// <summary>Initializes a new instance of the <see cref="DesktopSessionResources"/> class.</summary>
    /// <param name="host">The host of the window.</param>
    /// <param name="home">The home folder the MCP servers of the user are read from; null for the one of the process.</param>
    /// <param name="builtIns">The plugins that ship with the window, which its settings list whether they run or not.</param>
    internal DesktopSessionResources(CodeAltaHost host, string? home, IReadOnlyList<BuiltInPluginDefinition>? builtIns = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        (_host, _home, _builtIns) = (host, home, builtIns ?? []);
    }

    /// <summary>Reads what a session can use.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the reading.</param>
    /// <returns>Its resources; null when no such session is known.</returns>
    /// <exception cref="InvalidDataException">A configuration file cannot be parsed.</exception>
    /// <exception cref="IOException">A configuration file cannot be read.</exception>
    internal async Task<SessionResources?> ReadAsync(string sessionId, CancellationToken cancellationToken)
    {
        var runtime = _host.RuntimeService;
        if (await runtime.GetSessionSpaceAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } record) return null;
        var project = string.IsNullOrWhiteSpace(record.ProjectId) ? null
            : await _host.ProjectCatalog.GetByIdAsync(record.ProjectId, cancellationToken).ConfigureAwait(false);
        var space = await runtime.GetSpaceFilterAsync(record.SpaceId, project, cancellationToken).ConfigureAwait(false);
        var profile = _home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var skillQuery = SkillCatalogQuery.ForSession(new CodeAltaConfigStore(_host.CatalogOptions), _host.CatalogOptions.GlobalRoot, profile,
            project?.ProjectPath, otherProjectRoots: null, space) with { IncludeInvalid = false, IncludeShadowed = false, IncludeUntrusted = false, ModelVisibleOnly = true };
        var skills = (await _host.SkillCatalog.ListAsync(skillQuery, cancellationToken).ConfigureAwait(false)).Select(static skill => skill.Name);

        var prompts = AgentPromptCatalog.InSpace(new AgentPromptCatalog().ListEffectivePrompts(new AgentPromptCatalogQuery
        {
            ProjectRoot = project?.ProjectPath,
            ProjectPromptResourcesTrusted = project is not null,
            UserCodeAltaRoot = _host.CatalogOptions.GlobalRoot,
            UserProfileRoot = profile,
        }), space).Select(static prompt => prompt.DisplayName);

        var mcp = new McpManagementService().RefreshSnapshot(new McpManagementRequest { ProjectDirectory = project?.ProjectPath, UserHomeDirectory = _home, ProbeWritability = false });
        var servers = mcp.Policy.Enabled
            ? mcp.Servers.Where(server => server.State == McpManagementServerState.Configured && server.PolicyEnabled != false
                && space.Allows(SpaceResourceKind.McpServer, server.Key)).Select(static server => server.Key)
            : [];

        // The plugins its space has that are turned on, as the settings list them: those that run, and those that ship
        // with the window and are enabled, which a window without plugins (explicit roots) does not start. A plugin
        // assigned to other spaces is not among them, whatever its configuration says.
        var adapter = _host.PluginRuntime.Adapter;
        var running = _host.PluginRuntime.ActivePlugins
            .Where(plugin => plugin.Instance is not null && plugin.RuntimeContext.IsValid
                && adapter.IsAvailableInSpace(PluginContributionRegistry.PluginIdOf(plugin.Descriptor.RuntimeKey, plugin.SourcePackage?.PackageId), plugin.RuntimeContext.Scope, space.SpaceId)
                && plugin.RuntimeContext.AppliesToProject(project?.Id, project?.ProjectPath))
            .ToArray();
        var global = new CodeAltaConfigStore(_host.CatalogOptions).LoadGlobal();
        var resolver = new PluginRuntimeConfigResolver();
        var shipped = _builtIns
            .Where(definition => !running.Any(plugin => string.Equals(plugin.Descriptor.RuntimeKey, "builtin:" + definition.Id, StringComparison.Ordinal))
                && resolver.ResolveBuiltInPlugin(definition, global).Enabled
                && space.Allows(SpaceResourceKind.Plugin, definition.Id))
            .Select(static definition => definition.DisplayName);
        var plugins = running.Select(static plugin => plugin.Descriptor.DisplayName ?? plugin.Descriptor.RuntimeKey).Concat(shipped);

        static IReadOnlyList<string> Names(IEnumerable<string> names)
            => [.. names.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).Take(MaximumNames)];
        return new(space.SpaceId, Names(servers), Names(skills), Names(prompts), Names(plugins));
    }
}
