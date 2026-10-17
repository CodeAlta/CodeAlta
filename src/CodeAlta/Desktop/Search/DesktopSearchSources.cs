using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Documentation;
using CodeAlta.Catalog.Skills;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Search;

/// <summary>
/// The catalog content of the window that search indexes besides the sessions: the user guide by section and the
/// plugin API, the skills, the tools (built-in, of plugins, of the window), the <c>alta</c> commands, the agent
/// prompts, the plugins, the MCP servers, the commands of the window, and the plans and tasks of each project.
/// </summary>
/// <remarks>
/// What only changes with a new version (the guide, the built-in tools, the <c>alta</c> commands, the plugin API) is
/// read once and kept; the hash of each text then decides what the index writes again. What the user edits (skills,
/// prompts, plans, MCP servers) is read at each check, which the indexer runs at start, every few minutes and before a
/// query of its kind.
/// </remarks>
internal sealed class DesktopSearchSources
{
    private static readonly AltaCallerIdentity Caller = new() { Kind = "mcp" };

    private readonly CodeAltaHost _host;
    private readonly DesktopSearchHost _searchHost;
    private readonly ShippedDocumentation? _documentation;
    private readonly string _profile;
    private readonly string? _mcpHome;
    private readonly IReadOnlyList<BuiltInPluginDefinition> _builtIns;
    private readonly Func<IReadOnlyList<(string Name, string Description)>>? _uiTools;
    private readonly Lock _gate = new();
    private readonly ISearchSource[] _fixed;
    private IReadOnlyList<SearchDocument>? _guide;
    private IReadOnlyList<SearchDocument>? _builtInTools;
    private IReadOnlyList<SearchDocument>? _commands;
    private IReadOnlyList<SearchDocument> _windowCommands = [];
    private readonly Dictionary<string, FolderSearchSource> _plans = new(StringComparer.Ordinal);

    public DesktopSearchSources(CodeAltaHost host, DesktopSearchHost searchHost, ShippedDocumentation? documentation, string profile,
        IReadOnlyList<BuiltInPluginDefinition> builtIns, Func<IReadOnlyList<(string Name, string Description)>>? uiTools, string? mcpHome = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _searchHost = searchHost ?? throw new ArgumentNullException(nameof(searchHost));
        _documentation = documentation;
        _profile = profile;
        // The MCP files of the user: those of the profile of the process unless the window runs on a home of its own.
        _mcpHome = mcpHome ?? profile;
        _builtIns = builtIns ?? [];
        _uiTools = uiTools;
        _fixed =
        [
            new ListSearchSource("docs", [SearchKind.Doc], _ => ValueTask.FromResult(Guide())),
            new ListSearchSource("skills", [SearchKind.Skill], ReadSkillsAsync),
            new ListSearchSource("tools", [SearchKind.Tool], ReadToolsAsync),
            new ListSearchSource("plugins", [SearchKind.Plugin], _ => ValueTask.FromResult(ReadPlugins())),
            new ListSearchSource("mcp", [SearchKind.Mcp], ReadMcpServersAsync),
            new ListSearchSource("commands", [SearchKind.Command], _ => ValueTask.FromResult(WindowCommands)),
        ];
        // A plugin built or reloaded changes its tools and its commands: they are read again.
        host.PluginRuntime.Changed += (_, _) =>
        {
            lock (_gate) _commands = null;
        };
    }

    /// <summary>Gets or sets the dispatcher of the <c>alta</c> commands, whose help is indexed; null until the window has it.</summary>
    public AltaCommandDispatcher? Dispatcher { get; set; }

    /// <summary>Gets or sets the commands of the window, as the page sends them.</summary>
    public IReadOnlyList<SearchDocument> WindowCommands
    {
        get { lock (_gate) return _windowCommands; }
        set { lock (_gate) _windowCommands = value ?? []; }
    }

    /// <summary>Gives the sources now: the fixed ones, and the plans and tasks of each project the catalog had at the last read.</summary>
    public IReadOnlyList<ISearchSource> List()
    {
        var sources = new List<ISearchSource>(_fixed);
        lock (_gate)
        {
            foreach (var project in _searchHost.LastProjects)
            {
                var key = "plans:" + project.ProjectId;
                if (!_plans.TryGetValue(key, out var source))
                {
                    source = PlansOf(project);
                    _plans[key] = source;
                }

                sources.Add(source);
            }
        }

        return sources;
    }

    internal static FolderSearchSource PlansOf(SearchProjectInfo project)
    {
        var folder = Path.Combine(project.Path, ".alta");
        return new FolderSearchSource("plans:" + project.ProjectId,
            [(Path.Combine(folder, "plans"), SearchKind.Plan, "plan"), (Path.Combine(folder, "tasks"), SearchKind.Task, "task")],
            (path, kind, text) =>
            {
                var id = Path.GetFileNameWithoutExtension(path);
                var (title, body) = SplitMarkdown(text);
                var prefix = kind == SearchKind.Plan ? "plan" : "task";
                DateTimeOffset at;
                try { at = File.GetLastWriteTimeUtc(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { at = DateTimeOffset.UtcNow; }
                return new SearchDocument($"{prefix}:{project.ProjectId}/{id}", kind, title ?? id, body, at)
                {
                    ProjectId = project.ProjectId,
                    Extra = Extra(("id", id), ("project", project.Name)),
                    EmbeddingTitle = (title ?? id) + " · " + project.Name,
                };
            });
    }

    // The title of a Markdown file (its front matter, or its first heading) and its text without the front matter.
    internal static (string? Title, string Body) SplitMarkdown(string text)
    {
        string? title = null;
        var body = text;
        if (text.StartsWith("---", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in text[3..end].Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) title = trimmed[6..].Trim().Trim('"', '\'');
                }

                var after = text.IndexOf('\n', end + 4);
                body = after < 0 ? string.Empty : text[(after + 1)..];
            }
        }

        if (title is null)
        {
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    title = trimmed.TrimStart('#').Trim();
                    break;
                }
            }
        }

        return (string.IsNullOrWhiteSpace(title) ? null : title, body.Trim());
    }

    private IReadOnlyList<SearchDocument> Guide()
    {
        lock (_gate)
        {
            if (_guide is not null) return _guide;
        }

        var documents = new List<SearchDocument>();
        if (_documentation is { Available: true } documentation)
        {
            foreach (var page in documentation.ListPages())
            {
                var at = DateTimeOffset.UnixEpoch;
                if (documentation.GetPageFile(page.Path) is { } file)
                {
                    try { at = File.GetLastWriteTimeUtc(file); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }

                foreach (var section in documentation.ReadSections(page.Path))
                {
                    var title = section.Heading is null || section.Heading == section.PageTitle ? section.PageTitle : section.PageTitle + " › " + section.Heading;
                    var reference = section.Anchor is null ? "doc:" + section.Path : $"doc:{section.Path}#{section.Anchor}";
                    if (documents.Any(document => document.Ref == reference)) continue;
                    documents.Add(new SearchDocument(reference, SearchKind.Doc, title, section.Text, at)
                    {
                        Extra = section.Anchor is null ? Extra(("page", section.Path)) : Extra(("page", section.Path), ("anchor", section.Anchor)),
                    });
                }
            }
        }

        // The API of plugins, one item per type: what a plugin author looks up.
        try
        {
            foreach (var type in new PluginApiReference().Types)
            {
                var text = new StringBuilder().AppendLine(type.Declaration);
                if (type.Summary is not null) text.AppendLine(type.Summary);
                foreach (var member in type.Members)
                {
                    text.Append(member.Name);
                    if (member.Summary is not null) text.Append(": ").Append(member.Summary);
                    text.AppendLine();
                }

                documents.Add(new SearchDocument("doc:api/" + type.Name, SearchKind.Doc, type.Name + " (plugin API)", text.ToString(), DateTimeOffset.UnixEpoch)
                {
                    Extra = Extra(("api", type.Name)),
                });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Without the documentation files of the API the guide is indexed alone.
        }

        lock (_gate) _guide = documents;
        return documents;
    }

    private async ValueTask<IReadOnlyList<SearchDocument>> ReadSkillsAsync(CancellationToken cancellationToken)
    {
        var config = new CodeAltaConfigStore(_host.CatalogOptions);
        var documents = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);
        async Task AddAsync(SearchProjectInfo? project)
        {
            var query = SkillCatalogQuery.ForSession(config, _host.CatalogOptions.GlobalRoot, _profile, project?.Path, otherProjectRoots: null, space: null)
                with { IncludeInvalid = false, IncludeShadowed = false, IncludeUntrusted = true };
            IReadOnlyList<SkillDescriptor> skills;
            try { skills = await _host.SkillCatalog.ListAsync(query, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { return; }
            foreach (var skill in skills)
            {
                // The skills of the user come again with each project: they are indexed once, without a project.
                if ((skill.Scope == SkillScopeKind.Project) != (project is not null)) continue;
                var scope = skill.Scope switch
                {
                    SkillScopeKind.Project => project!.ProjectId,
                    SkillScopeKind.User => "user",
                    SkillScopeKind.Plugin => "plugin",
                    _ => "builtin",
                };
                var reference = $"skill:{scope}/{skill.NormalizedName}";
                if (documents.ContainsKey(reference)) continue;
                var body = string.Empty;
                try
                {
                    var info = new FileInfo(skill.SkillFilePath);
                    if (info.Exists && info.Length <= 64 * 1024) body = SplitMarkdown(await File.ReadAllTextAsync(info.FullName, cancellationToken).ConfigureAwait(false)).Body;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }

                documents[reference] = new SearchDocument(reference, SearchKind.Skill, skill.Name,
                    skill.Title + "\n" + skill.Description + (body.Length > 0 ? "\n\n" + body : string.Empty), DateTimeOffset.UnixEpoch)
                {
                    ProjectId = project?.ProjectId,
                    Extra = Extra(("name", skill.Name), ("path", skill.SkillFilePath), ("scope", scope)),
                    EmbeddingTitle = skill.Name + " · " + skill.Description,
                };
            }
        }

        await AddAsync(null).ConfigureAwait(false);
        foreach (var project in await _searchHost.GetProjectsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!project.Archived && Directory.Exists(project.Path)) await AddAsync(project).ConfigureAwait(false);
        }

        return [.. documents.Values];
    }

    private async ValueTask<IReadOnlyList<SearchDocument>> ReadToolsAsync(CancellationToken cancellationToken)
    {
        var documents = new List<SearchDocument>(BuiltInTools());
        // The tools of plugins, which a session has while the plugin runs.
        foreach (var plugin in _host.PluginRuntime.ActivePlugins)
        {
            var id = PluginContributionRegistry.PluginIdOf(plugin.Descriptor.RuntimeKey, plugin.SourcePackage?.PackageId);
            foreach (var registration in plugin.Contributions)
            {
                if (registration.Contribution is not PluginAgentToolContribution tool) continue;
                documents.Add(ToolDocument($"tool:plugin/{id}/{tool.Definition.Spec.Name}", tool.Definition.Spec.Name,
                    tool.Definition.Spec.Description, tool.Definition.Spec.InputSchema, Extra(("state", "loaded"), ("plugin", id))));
            }
        }

        // The tools of the window, which a session gets with `alta ui activate`.
        foreach (var (name, description) in _uiTools?.Invoke() ?? [])
        {
            documents.Add(new SearchDocument("tool:ui/" + name, SearchKind.Tool, name + " (window)", description, DateTimeOffset.UnixEpoch)
            {
                Extra = Extra(("activate", "alta ui activate")),
            });
        }

        documents.AddRange(await CommandsAsync(cancellationToken).ConfigureAwait(false));

        // The agent prompts a session can be given.
        try
        {
            foreach (var prompt in new AgentPromptCatalog().ListEffectivePrompts(new AgentPromptCatalogQuery
            {
                UserCodeAltaRoot = _host.CatalogOptions.GlobalRoot,
                UserProfileRoot = _profile,
            }))
            {
                documents.Add(new SearchDocument("prompt:" + prompt.PromptName, SearchKind.Tool, prompt.DisplayName + " (agent prompt)",
                    (prompt.Description ?? string.Empty) + "\n" + prompt.PromptName, DateTimeOffset.UnixEpoch)
                {
                    Extra = Extra(("prompt", prompt.PromptName)),
                });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }

        return documents;
    }

    private IReadOnlyList<SearchDocument> BuiltInTools()
    {
        lock (_gate)
        {
            if (_builtInTools is not null) return _builtInTools;
        }

        // The definitions only: nothing is run, so the handler of permissions refuses.
        var tools = AgentBuiltInToolFactory.CreateDefaultTools(new AgentBuiltInToolOptions
        {
            ProviderId = new ModelProviderId("search"),
            SessionId = "search",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            EnableUserInputTool = true,
        });
        var documents = tools.Select(tool => ToolDocument("tool:builtin/" + tool.Spec.Name, tool.Spec.Name, tool.Spec.Description, tool.Spec.InputSchema,
            Extra(("state", "loaded")))).ToList();
        lock (_gate) _builtInTools = documents;
        return documents;
    }

    // The `alta` commands, from their help: run once, then again after a plugin changed.
    private async ValueTask<IReadOnlyList<SearchDocument>> CommandsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_commands is not null) return _commands;
        }

        if (Dispatcher is not { } dispatcher) return [];
        var documents = new List<SearchDocument>();
        var listing = await dispatcher.InvokeAsync(["tool", "list", "--detailed"], caller: Caller, cancellationToken: cancellationToken).ConfigureAwait(false);
        var paths = new List<string>();
        foreach (var line in listing.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.TryGetProperty("type", out var type) && type.GetString() == "alta.tool.item"
                && (record.RootElement.TryGetProperty("path", out var path) || record.RootElement.TryGetProperty("Path", out path)) && path.GetString() is { Length: > 0 } value)
                paths.Add(value);
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var help = await dispatcher.InvokeAsync([.. path.Split(' ', StringSplitOptions.RemoveEmptyEntries), "--help"], caller: Caller, cancellationToken: cancellationToken).ConfigureAwait(false);
            var text = help.Stdout.Trim();
            if (text.Length == 0) continue;
            documents.Add(new SearchDocument("alta:" + path, SearchKind.Tool, "alta " + path, text, DateTimeOffset.UnixEpoch)
            {
                Extra = Extra(("path", path)),
                EmbeddingTitle = "alta " + path,
            });
        }

        lock (_gate) _commands = documents;
        return documents;
    }

    private IReadOnlyList<SearchDocument> ReadPlugins()
    {
        var documents = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);
        foreach (var plugin in _host.PluginRuntime.ActivePlugins)
        {
            var id = PluginContributionRegistry.PluginIdOf(plugin.Descriptor.RuntimeKey, plugin.SourcePackage?.PackageId);
            var text = new StringBuilder(plugin.Descriptor.Description ?? string.Empty).AppendLine();
            foreach (var registration in plugin.Contributions)
            {
                switch (registration.Contribution)
                {
                    case PluginCanvasContribution canvas:
                        text.Append("Canvas: ").Append(canvas.Title);
                        if (canvas.Description is not null) text.Append(" — ").Append(canvas.Description);
                        text.AppendLine();
                        break;
                    case PluginCommandContribution command:
                        text.Append("Command: ").Append(string.IsNullOrEmpty(command.Label) ? command.Name : command.Label);
                        if (command.Description.Length > 0) text.Append(" — ").Append(command.Description);
                        text.AppendLine();
                        break;
                    case PluginAgentToolContribution tool:
                        text.Append("Tool: ").Append(tool.Definition.Spec.Name).AppendLine();
                        break;
                }
            }

            documents["plugin:" + id] = new SearchDocument("plugin:" + id, SearchKind.Plugin, plugin.Descriptor.DisplayName ?? id, text.ToString(), DateTimeOffset.UnixEpoch)
            {
                Extra = Extra(("id", id)),
            };
        }

        // The plugins that ship with the window and do not run now.
        foreach (var definition in _builtIns)
        {
            documents.TryAdd("plugin:" + definition.Id, new SearchDocument("plugin:" + definition.Id, SearchKind.Plugin, definition.DisplayName,
                definition.Description ?? string.Empty, DateTimeOffset.UnixEpoch) { Extra = Extra(("id", definition.Id)) });
        }

        return [.. documents.Values];
    }

    private async ValueTask<IReadOnlyList<SearchDocument>> ReadMcpServersAsync(CancellationToken cancellationToken)
    {
        var documents = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);
        var toolLists = new McpToolListCache(McpToolListCache.FolderOf(_mcpHome));
        void Add(string? projectDirectory, string? projectId)
        {
            McpManagementSnapshot snapshot;
            try { snapshot = new McpManagementService().RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectDirectory, UserHomeDirectory = _mcpHome, ProbeWritability = false }); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { return; }
            foreach (var server in snapshot.Servers)
            {
                var ofProject = server.SourceScope == McpManagementScope.Project;
                if (ofProject != (projectId is not null) || server.State != McpManagementServerState.Configured) continue;
                var reference = "mcp:" + server.Key;
                if (projectId is not null) reference = $"mcp:{server.Key}@{projectId}";
                // The values of its settings (environment, headers, addresses with tokens) are never indexed.
                var text = $"{server.DisplayName}\n{server.Key}\n{server.Transport}\n{server.SourceName}";
                documents.TryAdd(reference, new SearchDocument(reference, SearchKind.Mcp, server.DisplayName, text, DateTimeOffset.UnixEpoch)
                {
                    ProjectId = projectId,
                    Extra = Extra(("server", server.Key)),
                });

                // The tools of the server as CodeAlta last listed them: found without starting it. A list made with
                // another definition of the server is stale and left out.
                var fingerprint = McpToolListCache.Fingerprint(server.Command, server.Args, server.Cwd, server.Url, server.Env.Keys);
                if (toolLists.Read(server.Key, fingerprint) is not { } list) continue;
                foreach (var tool in list.Tools)
                {
                    var toolReference = projectId is null ? $"mcp:{server.Key}/{tool.Name}" : $"mcp:{server.Key}@{projectId}/{tool.Name}";
                    var description = new StringBuilder();
                    if (tool.Title is not null) description.AppendLine(tool.Title);
                    if (tool.Description is not null) description.AppendLine(tool.Description);
                    foreach (var argument in tool.Arguments) description.Append(argument.Name).Append(argument.Description is null ? "" : ": " + argument.Description).AppendLine();
                    documents.TryAdd(toolReference, new SearchDocument(toolReference, SearchKind.Mcp, $"{tool.Name} ({server.DisplayName})", description.ToString(), list.ListedAt)
                    {
                        ProjectId = projectId,
                        Extra = Extra(("server", server.Key), ("tool", tool.Name), ("alias", tool.Alias), ("state", tool.Enabled ? "available" : "disabled")),
                        EmbeddingTitle = tool.Name,
                    });
                }
            }
        }

        Add(null, null);
        foreach (var project in await _searchHost.GetProjectsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!project.Archived && Directory.Exists(project.Path)) Add(project.Path, project.ProjectId);
        }

        return [.. documents.Values];
    }

    // A tool: its name, its description, and the names and descriptions of its arguments; never the schema itself.
    internal static SearchDocument ToolDocument(string reference, string name, string description, JsonElement schema, string extra)
    {
        var text = new StringBuilder(description).AppendLine();
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                text.Append(property.Name);
                if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("description", out var about) && about.ValueKind == JsonValueKind.String)
                    text.Append(": ").Append(about.GetString());
                text.AppendLine();
            }
        }

        return new SearchDocument(reference, SearchKind.Tool, name, text.ToString(), DateTimeOffset.UnixEpoch) { Extra = extra, EmbeddingTitle = name };
    }

    internal static string Extra(params ReadOnlySpan<(string Name, string Value)> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values) writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
