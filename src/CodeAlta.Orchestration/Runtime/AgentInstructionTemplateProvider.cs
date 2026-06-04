using System.Text;
using CodeAlta.Catalog;
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
    {
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
    public AgentInstructionBundle BuildCoordinatorInstructions(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        string? model = null,
        string? selectedPromptName = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bundle = BuildPromptBundle(session, project, model, selectedPromptName);
        session.AgentPromptId = bundle.Manifest.Template.InstructionName;
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
    public AgentInstructionBundle BuildGeneralInstructions(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        string? model = null,
        string? selectedPromptName = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bundle = BuildPromptBundle(session, project, model, selectedPromptName);
        session.AgentPromptId = bundle.Manifest.Template.InstructionName;
        return new AgentInstructionBundle
        {
            SystemMessage = bundle.SystemMessage,
            DeveloperInstructions = bundle.DeveloperInstructions,
            PromptBundle = bundle,
        };
    }

    private SystemPromptBundle BuildPromptBundle(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        string? model = null,
        string? selectedPromptName = null)
    {
        var projectRoots = string.IsNullOrWhiteSpace(project?.ProjectPath)
            ? Array.Empty<string>()
            : [project.ProjectPath];
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
            UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UserCodeAltaRoot = _catalogOptions?.GlobalRoot,
            AvailableSkillsMarkdown = BuildSkillsDeveloperInstructions(session, project),
        });
    }

    private string? BuildSkillsDeveloperInstructions(
        SessionViewDescriptor session,
        ProjectDescriptor? project,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_skillCatalog is null || _catalogOptions is null)
        {
            return null;
        }

        var descriptors = _skillCatalog.ListAsync(
                new SkillCatalogQuery
                {
                    Discovery = CreateDiscoveryContext(session, project),
                    GlobalDisabledSkillNames = _configStore?.LoadGlobalDisabledSkillNames() ?? (IReadOnlyCollection<string>)Array.Empty<string>(),
                    ProjectDisabledSkillNames = _configStore?.LoadProjectDisabledSkillNames(project?.ProjectPath) ?? (IReadOnlyCollection<string>)Array.Empty<string>(),
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
        builder.AppendLine("When the `alta` live tool is available, inspect skills with `alta skill list`/`alta skill show` and activate with `alta skill activate <skill-name> --session <session-id>`; prefer the singular `skill` group over compatibility aliases.");
        if (preferredSkills.Count > 0)
        {
            builder.AppendLine("");
        }

        builder.AppendLine("When a skill is activated, relative paths inside that skill resolve against the skill root.");
        builder.AppendLine();
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
                .Append(EscapeXml(descriptor.Description))
                .AppendLine("</skill>");
        }

        builder.Append("</available_skills>");
        return builder.ToString();
    }

    private SkillDiscoveryContext CreateDiscoveryContext(SessionViewDescriptor session, ProjectDescriptor? project)
    {
        var projectRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(project?.ProjectPath))
        {
            projectRoots.Add(project.ProjectPath);
        }

        return new SkillDiscoveryContext
        {
            ProjectRoots = projectRoots,
            UserCodeAltaRoot = _catalogOptions?.GlobalRoot,
            UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
    }

    private static string ToLocationLabel(SkillSourceKind sourceKind)
    {
        return sourceKind switch
        {
            SkillSourceKind.ProjectAlta => "project .alta/skills",
            SkillSourceKind.ProjectCommon => "project .agents/skills",
            SkillSourceKind.UserAlta => "user ~/.alta/skills",
            SkillSourceKind.UserCommon => "user ~/.agents/skills",
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
