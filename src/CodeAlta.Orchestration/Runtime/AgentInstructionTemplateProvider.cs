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

    // Kept for the life of the provider, so that the reads of each prompt reuse what they parsed: the store of the file
    // of the catalog options, which the tiers are read from, and the spaces of the catalog.
    private CodeAltaConfigStore? _modelSettingsStore;
    private SpaceCatalog? _spaceCatalog;

    internal SessionDiscoveryScope? DiscoveryScope { get; }

    /// <summary>
    /// Gets or initializes whether the sessions work under the identity of their space, so that the Runtime Context
    /// says when its commits are signed off. False, the default: the sessions keep the user's usual settings.
    /// </summary>
    public bool AppliesSpaceIdentities { get; init; }

    /// <summary>
    /// Gets or initializes the folder the Runtime Context names for the scratchpad files of the sessions; null, the
    /// default, for the <c>scratchpad</c> folder of the state root.
    /// </summary>
    public string? ScratchpadRoot { get; init; }

    /// <summary>
    /// Gets or initializes whether the prompt of a session lists the skills it can use. True, the default; false for a run
    /// that does without skills.
    /// </summary>
    public bool ListsSkills { get; init; } = true;

    /// <summary>
    /// Gets or initializes whether the sessions read what other tools keep in the home folder of the user: the skills of
    /// <c>~/.agents/skills</c> and <c>~/.copilot/skills</c>, the agents of <c>~/.copilot/agents</c> and
    /// <c>~/.copilot/copilot-instructions.md</c>. True, the default; false for a separate profile, which reads none of them.
    /// </summary>
    public bool ReadsUserHome { get; init; } = true;

    /// <summary>
    /// Gets the home folder whose resources of other tools the sessions read: the one of the discovery scope, else the one
    /// of the user; <see langword="null"/> when the sessions read none (<see cref="ReadsUserHome"/>).
    /// </summary>
    public string? UserHomeRoot => ReadsUserHome ? DiscoveryScope?.UserProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : null;

    // The patterns over tool names the runtime gives a session (SessionRuntimeService.SetDisabledToolPatterns), expanded
    // over the built-in tools into the tools the session does without; none without a runtime.
    internal Func<string?, IReadOnlyList<string>>? SessionToolPatterns { get; set; }

    /// <summary>
    /// Gets how the prompt lists the skills of a session of a project and a space: <c>full</c> (every skill with its
    /// description), <c>compact</c> (names and titles) or <c>auto</c> (full while there are few). Null, or a host whose
    /// agents cannot search the skills, keeps the full list: the short forms rely on <c>alta search</c>.
    /// </summary>
    public Func<ProjectDescriptor?, string?, string>? SkillPromptCatalog { get; init; }

    /// <summary>
    /// Gets the skills the prompt of a session lists first, with their whole description in every form of the list, and marks
    /// <c>preferred="true"</c>: the skills used most in the last days, in their order. A name that is not a skill the session can
    /// use is left out, and at most <see cref="MaxPreferredSkills"/> are taken. The prompt is built at each send and compared with the
    /// one the session runs with: the host gives a session the same answer as long as its prompt must stay the same, so that provider
    /// prompt caches survive. Null prefers none.
    /// </summary>
    public Func<PreferredSkillsRequest, IReadOnlyList<string>>? PreferredSkills { get; init; }

    /// <summary>The most skills the prompt lists as preferred.</summary>
    public const int MaxPreferredSkills = 8;

    /// <summary>The line that says what the preferred skills are.</summary>
    public const string PreferredHint = "Skills marked `preferred=\"true\"` are the ones used most in this project over the last days: consider them first when one fits the task.";

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
        // The store made here is the one of the catalog options: the tiers are read through it too.
        if (configStore is null) _modelSettingsStore = _configStore;
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
    /// Builds the instruction bundle for a coordinator session, as <see cref="BuildCoordinatorInstructions"/> does, waiting for
    /// the skills of the session without holding the thread.
    /// </summary>
    /// <param name="session">The active session view.</param>
    /// <param name="project">The owning project, if any.</param>
    /// <param name="model">The selected model id, if known.</param>
    /// <param name="selectedPromptName">The selected agent prompt name, if any.</param>
    /// <param name="cancellationToken">Cancels the listing of the skills.</param>
    /// <returns>The file-backed instruction bundle selected for the session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<AgentInstructionBundle> BuildCoordinatorInstructionsAsync(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        string? model,
        string? selectedPromptName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateDiscoveryPaths(session, project);
        var context = ResolvePromptContext(session, project);
        var skills = await ListSessionSkillsAsync(project, context.SpaceResources, cancellationToken).ConfigureAwait(false);
        var bundle = BuildPromptBundle(session, project, model, selectedPromptName, context, skills);
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
        var context = ResolvePromptContext(session, project);
        // The prompt is built synchronously here: the skills are listed on this thread, which their cache answers at once
        // while nothing changed in their folders.
        var skills = ListSessionSkillsAsync(project, context.SpaceResources, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        return BuildPromptBundle(session, project, model, selectedPromptName, context, skills);
    }

    // What the prompt of a session is built from beside the session itself: the tiers of the settings and its space.
    private sealed record PromptContext(ModelSettings Models, IReadOnlyList<SpaceDescriptor> Spaces, SpaceDescriptor Space, SpaceFilter SpaceResources);

    private PromptContext ResolvePromptContext(SessionViewDescriptor session, ProjectDescriptor? project)
    {
        var models = LoadModelSettings();
        var spaces = LoadSpaces();
        var spaceId = SpaceCatalog.ResolveSessionSpace(session.SpaceId, project, spaces);
        var spaceResources = SpaceFilter.Load(_configStore, spaceId);
        var space = spaces.First(space => string.Equals(space.Id, spaceId, StringComparison.Ordinal));
        return new PromptContext(models, spaces, space, spaceResources);
    }

    private SystemPromptBundle BuildPromptBundle(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        string? model,
        string? selectedPromptName,
        PromptContext context,
        IReadOnlyList<SkillDescriptor>? skills)
    {
        var projectRoots = string.IsNullOrWhiteSpace(project?.ProjectPath)
            ? Array.Empty<string>()
            : [project.ProjectPath];
        var (models, spaces, space, spaceResources) = context;
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
            UserProfileRoot = UserHomeRoot,
            ReadsUserHome = ReadsUserHome,
            UserCodeAltaRoot = _catalogOptions?.GlobalRoot,
            InstanceStateRoot = _catalogOptions?.StateRoot,
            ScratchpadRoot = ScratchpadRoot,
            DiscoveryScope = DiscoveryScope,
            AvailableSkillsMarkdown = BuildSkillsDeveloperInstructions(session, project, spaceResources, skills),
            SpaceResources = spaceResources,
            DisabledTools = WithSessionPatterns(HostDisabledTools(space), session.SessionId),
            // The Runtime Context names the space only when there is another one than the default, where it tells something.
            Space = spaces.Count < 2 ? null : space,
            GitIdentity = AppliesSpaceIdentities ? space.Settings.Identity?.Git : null,
            // On by default: a host without a configuration file asks for the line too.
            CommitCoAuthor = _configStore?.LoadGlobalCommitCoAuthor() ?? true,
            ModelTiers = ModelTierResolver.DescribeForAgents(tiers.Tiers, tiers.IsProviderAvailable),
            ModelFallback = tiers.Fallback.Enabled,
        });
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
            UserProfileRoot = UserHomeRoot,
        }, agentPromptId);
        disabled.UnionWith(prompt?.DisabledTools ?? []);
        disabled.UnionWith(SessionPatternTools(session.SessionId));
        return disabled;
    }

    // The tools the user turns off for every session, and those the space of the session does without.
    private IReadOnlySet<string> HostDisabledTools(SpaceDescriptor space)
    {
        var disabled = new HashSet<string>(_configStore?.LoadGlobalDisabledTools() ?? new HashSet<string>(), StringComparer.Ordinal);
        disabled.UnionWith(space.Settings.Tools?.Disabled ?? []);
        return disabled;
    }

    private IReadOnlySet<string> WithSessionPatterns(IReadOnlySet<string> disabled, string? sessionId)
    {
        var fromPatterns = SessionPatternTools(sessionId);
        if (fromPatterns.Count == 0) return disabled;
        var union = new HashSet<string>(disabled, StringComparer.Ordinal);
        union.UnionWith(fromPatterns);
        return union;
    }

    private IReadOnlySet<string> SessionPatternTools(string? sessionId)
        => SessionToolPatterns?.Invoke(sessionId) is { Count: > 0 } patterns
            ? CodeAlta.Agent.AgentToolPatterns.Expand(CodeAlta.Agent.AgentToolPatterns.KnownToolNames, patterns)
            : new HashSet<string>();

    // The spaces of the catalog; the default one alone while the catalog keeps no space.
    private IReadOnlyList<SpaceDescriptor> LoadSpaces()
    {
        if (_catalogOptions is null || string.IsNullOrWhiteSpace(_catalogOptions.GlobalRoot) || !Directory.Exists(_catalogOptions.SpacesRoot))
        {
            return [SpaceDescriptor.CreateDefault()];
        }

        // Read on this thread: the prompt is built synchronously, and waiting on asynchronous reads would hold a second one.
        var spaces = Volatile.Read(ref _spaceCatalog);
        if (spaces is null)
        {
            spaces = new SpaceCatalog(new ProjectCatalog(_catalogOptions));
            spaces = Interlocked.CompareExchange(ref _spaceCatalog, spaces, null) ?? spaces;
        }

        return spaces.Load();
    }

    // The tiers of the settings; a file that cannot be read, or whose tiers are not valid, gives none.
    private ModelSettings LoadModelSettings()
    {
        if (_catalogOptions is null) return ModelSettings.Default;
        var store = Volatile.Read(ref _modelSettingsStore);
        if (store is null)
        {
            // A root that is not valid throws here at each build, as before.
            store = new CodeAltaConfigStore(_catalogOptions);
            store = Interlocked.CompareExchange(ref _modelSettingsStore, store, null) ?? store;
        }

        try { return store.LoadGlobalModelSettings(); }
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

    // The skills the model of a session sees; null without a catalog, or when the prompt lists none.
    private async Task<IReadOnlyList<SkillDescriptor>?> ListSessionSkillsAsync(
        ProjectDescriptor? project,
        SpaceFilter space,
        CancellationToken cancellationToken)
    {
        if (!ListsSkills || _skillCatalog is null || _catalogOptions is null)
        {
            return null;
        }

        var query = SkillCatalogQuery.ForSession(
            _configStore,
            _catalogOptions.GlobalRoot,
            UserHomeRoot,
            project?.ProjectPath,
            otherProjectRoots: null,
            space);
        return await _skillCatalog.ListAsync(
                query with
                {
                    IncludeInvalid = false,
                    IncludeShadowed = false,
                    IncludeUntrusted = false,
                    ModelVisibleOnly = true,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private string? BuildSkillsDeveloperInstructions(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        SpaceFilter space,
        IReadOnlyList<SkillDescriptor>? descriptors)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (descriptors is null || descriptors.Count == 0)
        {
            return null;
        }
        // The rank of each preferred skill that the session can use; the others have none.
        var preferredRanks = PreferredRanks(session, project, space.SpaceId, descriptors);
        bool IsPreferred(SkillDescriptor skill) => preferredRanks.ContainsKey(skill.Name);
        // The skills of CodeAlta come first and are listed in full in every form, then the preferred ones: the short forms list
        // the others in one line, or count them.
        bool InFull(SkillDescriptor skill) => skill.SourceKind == SkillSourceKind.Builtin || IsPreferred(skill);
        var ordered = descriptors
            .OrderBy(static skill => skill.SourceKind == SkillSourceKind.Builtin ? 0 : 1)
            .ThenBy(skill => preferredRanks.TryGetValue(skill.Name, out var rank) ? rank : int.MaxValue)
            .ThenBy(static skill => skill.Precedence)
            .ThenBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("Filesystem skills are available for this session.");
        builder.AppendLine("Activate a skill only when it clearly matches the current task.");
        builder.AppendLine("When the `alta` live tool is available, inspect skills with `alta skill list`/`alta skill show` and activate with `alta skill activate <skill-name>`; prefer the singular `skill` group over compatibility aliases.");
        if (preferredRanks.Count > 0)
        {
            builder.AppendLine(PreferredHint);
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
            foreach (var descriptor in ordered.Where(InFull))
            {
                AppendSkill(builder, descriptor, IsPreferred(descriptor), descriptor.Description);
            }

            foreach (var group in descriptors.Where(skill => !InFull(skill)).GroupBy(static skill => ToLocationLabel(skill.SourceKind)).OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                builder.Append("  <scope location=\"").Append(EscapeXml(group.Key)).Append("\" count=\"")
                    .Append(group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine("\" />");
            }

            builder.Append("</available_skills>");
            return builder.ToString();
        }

        builder.AppendLine("<available_skills>");
        foreach (var descriptor in ordered)
        {
            AppendSkill(builder, descriptor, IsPreferred(descriptor), shortForm && !InFull(descriptor) ? ShortLine(descriptor) : descriptor.Description);
        }

        builder.Append("</available_skills>");
        return builder.ToString();
    }

    private static void AppendSkill(StringBuilder builder, SkillDescriptor descriptor, bool preferred, string text)
        => builder.Append("  <skill name=\"")
            .Append(EscapeXml(descriptor.Name))
            .Append("\" preferred=\"")
            .Append(preferred ? "true" : "false")
            .Append("\" location=\"")
            .Append(EscapeXml(ToLocationLabel(descriptor.SourceKind)))
            .Append("\" path=\"")
            .Append(EscapeXml(descriptor.SkillFilePath))
            .Append("\">")
            .Append(EscapeXml(text))
            .AppendLine("</skill>");

    // The preferred skills the host names, in its order, among the skills the session can use: at most MaxPreferredSkills.
    private Dictionary<string, int> PreferredRanks(SessionViewDescriptor session, ProjectDescriptor? project, string spaceId, IReadOnlyList<SkillDescriptor> descriptors)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (PreferredSkills is null)
        {
            return ranks;
        }

        var available = descriptors.Select(static skill => skill.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in PreferredSkills(new PreferredSkillsRequest(session.SessionId ?? string.Empty, project, spaceId)))
        {
            if (ranks.Count >= MaxPreferredSkills)
            {
                break;
            }

            if (available.Contains(name) && !ranks.ContainsKey(name))
            {
                ranks[name] = ranks.Count;
            }
        }

        return ranks;
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
