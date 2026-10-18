using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Produces optional orchestration instruction overrides for agent sessions.
/// </summary>
public sealed class AgentInstructionTemplateProvider
{
    private readonly SkillCatalog? _skillCatalog;
    private readonly CatalogOptions? _catalogOptions;
    private readonly CodeAltaConfigStore? _configStore;
    private readonly SystemPromptBuilder _promptBuilder;

    internal SessionDiscoveryScope? DiscoveryScope { get; }

    /// <summary>
    /// Gets or initializes whether the sessions work under the identity of their space, so that the Runtime Context
    /// says when its commits are signed off. False, the default: the sessions keep the user's usual settings.
    /// </summary>
    public bool AppliesSpaceIdentities { get; init; }

    /// <summary>
    /// Gets or initializes whether the agents can consult the decision providers of the configuration with
    /// <c>alta decide</c>, so that the Runtime Context names those that are enabled. False, the default.
    /// </summary>
    public bool ConsultsDecisionProviders { get; init; }

    /// <summary>
    /// Gets how the prompt lists the skills of a session of a project and a space: <c>full</c> (every skill with its
    /// description), <c>compact</c> (names and titles) or <c>auto</c> (full while there are few). Null, or a host whose
    /// agents cannot search the skills, keeps the full list: the short forms rely on <c>alta search</c>.
    /// </summary>
    public Func<ProjectDescriptor?, string?, string>? SkillPromptCatalog { get; init; }

    /// <summary>Beyond this number of skills, <c>auto</c> lists their names and titles only.</summary>
    public const int SkillTitlesThreshold = 12;

    /// <summary>Beyond this number of skills, the short forms give the count per scope and how to find them.</summary>
    public const int SkillCountsThreshold = 60;

    /// <summary>The line that tells agents how to find skills and tools by purpose.</summary>
    public const string SearchHint = "Find skills, tools, MCP tools and documentation by purpose with `alta search --kind skill,tool,mcp,doc <need>`.";

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentInstructionTemplateProvider"/> class.
    /// </summary>
    /// <param name="skillCatalog">Optional skill catalog used to advertise available skills.</param>
    /// <param name="catalogOptions">Optional catalog options used to resolve user/global skill and prompt roots.</param>
    /// <param name="contentLocator">Optional prompt content locator.</param>
    public AgentInstructionTemplateProvider(
        SkillCatalog? skillCatalog = null,
        CatalogOptions? catalogOptions = null,
        ISystemPromptContentLocator? contentLocator = null,
        CodeAltaConfigStore? configStore = null)
        : this(skillCatalog, catalogOptions, contentLocator, configStore, discoveryScope: null)
    {
    }

    /// <summary>Initializes instruction discovery with an optional explicit lexical scope.</summary>
    /// <param name="skillCatalog">Optional skill catalog.</param>
    /// <param name="catalogOptions">Optional catalog roots.</param>
    /// <param name="contentLocator">Optional prompt content locator.</param>
    /// <param name="configStore">Optional configuration store.</param>
    /// <param name="discoveryScope">Optional explicit discovery home and instruction ancestry boundary.</param>
    /// <remarks>Working and project paths are validated when building instructions; invalid scoped paths throw <see cref="ArgumentException"/> before prompt or skill discovery.</remarks>
    public AgentInstructionTemplateProvider(
        SkillCatalog? skillCatalog,
        CatalogOptions? catalogOptions,
        ISystemPromptContentLocator? contentLocator,
        CodeAltaConfigStore? configStore,
        SessionDiscoveryScope? discoveryScope)
    {
        DiscoveryScope = discoveryScope;
        _skillCatalog = skillCatalog;
        _catalogOptions = catalogOptions;
        _configStore = configStore ?? (catalogOptions is not null && !string.IsNullOrWhiteSpace(catalogOptions.GlobalRoot)
            ? new CodeAltaConfigStore(catalogOptions)
            : null);
        _promptBuilder = new SystemPromptBuilder(contentLocator);
    }

    /// <summary>
    /// Builds the instruction bundle for a coordinator session.
    /// </summary>
    /// <param name="session">The active session view.</param>
    /// <param name="project">The owning project, if any.</param>
    /// <param name="model">The selected model id, if known.</param>
    /// <param name="selectedPromptName">The selected agent prompt name, if any.</param>
    /// <returns>The file-backed instruction bundle selected for the session.</returns>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public AgentInstructionBundle BuildCoordinatorInstructions(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        string? model = null,
        string? selectedPromptName = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bundle = BuildPromptBundle(session, project, model, selectedPromptName);
        session.AgentPromptId = bundle.Manifest.Composition.AgentPromptName;
        return new AgentInstructionBundle
        {
            SystemMessage = bundle.SystemMessage,
            DeveloperInstructions = bundle.DeveloperInstructions,
            PromptBundle = bundle,
        };
    }

    /// <summary>
    /// Builds the instruction bundle for a general scoped agent session.
    /// </summary>
    /// <param name="session">The active session view.</param>
    /// <param name="project">The owning project, if any.</param>
    /// <param name="model">The selected model id, if known.</param>
    /// <param name="selectedPromptName">The selected agent prompt name, if any.</param>
    /// <returns>The file-backed instruction bundle selected for the session.</returns>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public AgentInstructionBundle BuildGeneralInstructions(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        string? model = null,
        string? selectedPromptName = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bundle = BuildPromptBundle(session, project, model, selectedPromptName);
        session.AgentPromptId = bundle.Manifest.Composition.AgentPromptName;
        return new AgentInstructionBundle
        {
            SystemMessage = bundle.SystemMessage,
            DeveloperInstructions = bundle.DeveloperInstructions,
            PromptBundle = bundle,
        };
    }

    /// <summary>Validates scoped paths before composing skills and file-backed prompt content.</summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    private SystemPromptBundle BuildPromptBundle(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        string? model = null,
        string? selectedPromptName = null)
    {
        ValidateDiscoveryPaths(session, project);
        var projectRoots = string.IsNullOrWhiteSpace(project?.ProjectPath)
            ? Array.Empty<string>()
            : [project.ProjectPath];
        var models = LoadModelSettings();
        var spaces = LoadSpaces();
        var spaceId = SpaceCatalog.ResolveSessionSpace(session.SpaceId, project, spaces);
        var spaceResources = SpaceFilter.Load(_configStore, spaceId);
        var space = spaces.First(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
        // The tiers and the fallback of the space (its own, or the global ones), with the providers it has.
        var tiers = ModelTierScope.Of(models, space, spaceResources.Assignments);
        return _promptBuilder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = session.ResolvedProviderKey,
            ProviderType = session.ProviderId,
            ProtocolFamily = session.ProviderId,
            Model = model,
            Session = session,
            Project = project,
            WorkingDirectory = session.WorkingDirectory,
            ProjectRoots = projectRoots,
            SelectedPromptName = selectedPromptName ?? session.AgentPromptId,
            UserProfileRoot = DiscoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UserCodeAltaRoot = _catalogOptions?.GlobalRoot,
            InstanceStateRoot = _catalogOptions?.StateRoot,
            DiscoveryScope = DiscoveryScope,
            AvailableSkillsMarkdown = BuildSkillsDeveloperInstructions(session, project, spaceResources),
            SpaceResources = spaceResources,
            DisabledTools = HostDisabledTools(spaces.First(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal))),
            // The Runtime Context names the space only when there is another one than the default, where it tells something.
            Space = spaces.Count < 2 ? null : space,
            GitIdentity = AppliesSpaceIdentities ? space.Settings.Identity?.Git : null,
            ModelTiers = ModelTierResolver.DescribeForAgents(tiers.Tiers, tiers.IsProviderAvailable),
            ModelFallback = tiers.Fallback.Enabled,
            DecisionProviders = ConsultsDecisionProviders ? LoadDecisionProviders() : [],
        });
    }

    // The enabled decision providers, by key: a session consults them, it never runs on one.
    private IReadOnlyList<string> LoadDecisionProviders()
    {
        if (_catalogOptions is null) return [];
        try
        {
            return [.. new CodeAltaConfigStore(_catalogOptions).LoadGlobalProviderDefinitions()
                .Where(static definition => CodeAltaConfigStore.IsDecisionProviderType(definition.ProviderType))
                .Select(static definition => definition.ProviderKey)];
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Works out the built-in tools a session does without, as its instructions do: those the user turns off for
    /// every session, those its space does without and the <c>disabled_tools</c> of its agent prompt.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="project">Its project, or <see langword="null"/>.</param>
    /// <param name="agentPromptId">Its agent prompt; <see langword="null"/> for the default one.</param>
    /// <returns>The names of the tools.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The configuration cannot be parsed.</exception>
    public IReadOnlySet<string> ResolveDisabledTools(SessionViewDescriptor session, ProjectDescriptor? project, string? agentPromptId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var spaces = LoadSpaces();
        var spaceId = SpaceCatalog.ResolveSessionSpace(session.SpaceId, project, spaces);
        var disabled = new HashSet<string>(HostDisabledTools(spaces.First(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal))), StringComparer.Ordinal);
        var prompt = new AgentPromptCatalog().ResolvePrompt(new AgentPromptCatalogQuery
        {
            ProjectRoot = project?.ProjectPath,
            ProjectPromptResourcesTrusted = project is not null,
            UserCodeAltaRoot = _catalogOptions?.GlobalRoot,
            UserProfileRoot = DiscoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        }, agentPromptId);
        disabled.UnionWith(prompt?.DisabledTools ?? []);
        return disabled;
    }

    // The tools the user turns off for every session, and those the space of the session does without.
    private IReadOnlySet<string> HostDisabledTools(SpaceDescriptor space)
    {
        var disabled = new HashSet<string>(_configStore?.LoadGlobalDisabledTools() ?? new HashSet<string>(), StringComparer.Ordinal);
        disabled.UnionWith(space.Settings.Tools?.Disabled ?? []);
        return disabled;
    }

    // The spaces of the catalog; the default one alone while the catalog keeps no space.
    private IReadOnlyList<SpaceDescriptor> LoadSpaces()
    {
        if (_catalogOptions is null || string.IsNullOrWhiteSpace(_catalogOptions.GlobalRoot) || !Directory.Exists(_catalogOptions.SpacesRoot))
        {
            return [SpaceDescriptor.CreateDefault()];
        }

        return new SpaceCatalog(new ProjectCatalog(_catalogOptions)).LoadAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    // The tiers of the settings; a file that cannot be read, or whose tiers are not valid, gives none.
    private ModelSettings LoadModelSettings()
    {
        if (_catalogOptions is null) return ModelSettings.Default;
        try { return new CodeAltaConfigStore(_catalogOptions).LoadGlobalModelSettings(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ModelSettings.Default;
        }
    }

    private void ValidateDiscoveryPaths(SessionViewDescriptor session, ProjectDescriptor? project)
    {
        if (DiscoveryScope is null)
        {
            return;
        }

        if (session.WorkingDirectory is not null)
        {
            DiscoveryScope.ValidateProjectPath(session.WorkingDirectory, nameof(session.WorkingDirectory));
        }

        if (project?.ProjectPath is not null)
        {
            DiscoveryScope.ValidateProjectPath(project.ProjectPath, nameof(project.ProjectPath));
        }
    }

    private string? BuildSkillsDeveloperInstructions(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        SpaceFilter space)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_skillCatalog is null || _catalogOptions is null)
        {
            return null;
        }

        var query = SkillCatalogQuery.ForSession(
            _configStore,
            _catalogOptions.GlobalRoot,
            DiscoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            project?.ProjectPath,
            otherProjectRoots: null,
            space);
        var descriptors = _skillCatalog.ListAsync(
                query with
                {
                    IncludeInvalid = false,
                    IncludeShadowed = false,
                    IncludeUntrusted = false,
                    ModelVisibleOnly = true,
                })
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        if (descriptors.Count == 0)
        {
            return null;
        }
        var preferredSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        builder.AppendLine("Filesystem skills are available for this session.");
        builder.AppendLine("Activate a skill only when it clearly matches the current task.");
        builder.AppendLine("When the `alta` live tool is available, inspect skills with `alta skill list`/`alta skill show` and activate with `alta skill activate <skill-name>`; prefer the singular `skill` group over compatibility aliases.");
        if (preferredSkills.Count > 0)
        {
            builder.AppendLine("");
        }

        builder.AppendLine("When a skill is activated, relative paths inside that skill resolve against the skill root.");
        // The list depends only on what is installed and on the setting: it is the same from turn to turn, and
        // provider prompt caches survive.
        var mode = SkillPromptCatalog?.Invoke(project, space.SpaceId) ?? "full";
        var shortForm = mode == "compact" || (mode == "auto" && descriptors.Count > SkillTitlesThreshold);
        if (shortForm)
        {
            builder.AppendLine(SearchHint);
            builder.AppendLine("Read a skill with `alta skill show <skill-name>` before you activate it.");
        }

        builder.AppendLine();
        if (shortForm && descriptors.Count > SkillCountsThreshold)
        {
            builder.AppendLine("<available_skills>");
            foreach (var group in descriptors.GroupBy(static skill => ToLocationLabel(skill.SourceKind)).OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                builder.Append("  <scope location=\"").Append(EscapeXml(group.Key)).Append("\" count=\"")
                    .Append(group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine("\" />");
            }

            builder.Append("</available_skills>");
            return builder.ToString();
        }

        builder.AppendLine("<available_skills>");
        foreach (var descriptor in descriptors
                     .OrderByDescending(skill => preferredSkills.Contains(skill.Name))
                     .ThenBy(static skill => skill.Precedence)
                     .ThenBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("  <skill name=\"")
                .Append(EscapeXml(descriptor.Name))
                .Append("\" preferred=\"")
                .Append(preferredSkills.Contains(descriptor.Name) ? "true" : "false")
                .Append("\" location=\"")
                .Append(EscapeXml(ToLocationLabel(descriptor.SourceKind)))
                .Append("\" path=\"")
                .Append(EscapeXml(descriptor.SkillFilePath))
                .Append("\">")
                .Append(EscapeXml(shortForm ? ShortLine(descriptor) : descriptor.Description))
                .AppendLine("</skill>");
        }

        builder.Append("</available_skills>");
        return builder.ToString();
    }

    // The one line of a skill in the short form: its title, or the start of its description when the title is its name.
    internal static string ShortLine(SkillDescriptor skill)
    {
        if (!string.IsNullOrWhiteSpace(skill.Title) && !string.Equals(skill.Title.Trim(), skill.Name, StringComparison.OrdinalIgnoreCase)) return skill.Title.Trim();
        var description = skill.Description.ReplaceLineEndings(" ").Trim();
        var end = description.IndexOf(". ", StringComparison.Ordinal);
        if (end > 0) description = description[..(end + 1)];
        return description.Length <= 100 ? description : description[..99].TrimEnd() + "…";
    }

    private static string ToLocationLabel(SkillSourceKind sourceKind)
    {
        return sourceKind switch
        {
            SkillSourceKind.ProjectAlta => "project .alta/skills",
            SkillSourceKind.ProjectCommon => "project .agents/skills",
            SkillSourceKind.UserAlta => "user ~/.alta/skills",
            SkillSourceKind.UserCommon => "user ~/.agents/skills",
            SkillSourceKind.ProjectCopilot => "project .github/skills",
            SkillSourceKind.UserCopilot => "user ~/.copilot/skills",
            SkillSourceKind.Plugin => "plugin",
            SkillSourceKind.Builtin => "builtin",
            _ => "temporary",
        };
    }

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

}
