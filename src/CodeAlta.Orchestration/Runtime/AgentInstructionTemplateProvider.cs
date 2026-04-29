using CodeAlta.Catalog;
using CodeAlta.Catalog.Roles;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using System.Text;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Produces optional orchestration instruction overrides for agent sessions.
/// </summary>
public sealed class AgentInstructionTemplateProvider
{
    private readonly SkillCatalog? _skillCatalog;
    private readonly CatalogOptions? _catalogOptions;
    private readonly ISystemPromptContentLocator _contentLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentInstructionTemplateProvider"/> class.
    /// </summary>
    /// <param name="skillCatalog">Optional skill catalog used to advertise available skills.</param>
    /// <param name="catalogOptions">Optional catalog options used to resolve user/global skill roots.</param>
    public AgentInstructionTemplateProvider(
        SkillCatalog? skillCatalog = null,
        CatalogOptions? catalogOptions = null,
        ISystemPromptContentLocator? contentLocator = null)
    {
        _skillCatalog = skillCatalog;
        _catalogOptions = catalogOptions;
        _contentLocator = contentLocator ?? new FileSystemPromptContentLocator();
    }

    /// <summary>
    /// Builds the instruction bundle for a coordinator session.
    /// </summary>
    /// <param name="thread">The active work thread.</param>
    /// <param name="project">The owning project, if any.</param>
    /// <param name="profile">The profile providing backend and role-specific defaults.</param>
    /// <returns>
    /// An instruction bundle containing no overrides so backend defaults remain active
    /// while orchestration-specific prompting is disabled.
    /// </returns>
    public AgentInstructionBundle BuildCoordinatorInstructions(
        WorkThreadDescriptor thread,
        ProjectDescriptor? project,
        RoleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(profile);

        return new AgentInstructionBundle
        {
            SystemMessage = LoadDefaultSystemPrompt(),
            DeveloperInstructions = BuildSkillsDeveloperInstructions(thread, project, profile),
        };
    }

    /// <summary>
    /// Builds the instruction bundle for a general scoped agent session.
    /// </summary>
    /// <param name="thread">The active work thread.</param>
    /// <param name="project">The owning project, if any.</param>
    /// <param name="profile">The profile providing backend and role-specific defaults.</param>
    /// <returns>
    /// An instruction bundle containing no overrides so backend defaults remain active
    /// while orchestration-specific prompting is disabled.
    /// </returns>
    public AgentInstructionBundle BuildGeneralInstructions(
        WorkThreadDescriptor thread,
        ProjectDescriptor? project,
        RoleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(profile);

        return new AgentInstructionBundle
        {
            SystemMessage = LoadDefaultSystemPrompt(),
            DeveloperInstructions = BuildSkillsDeveloperInstructions(thread, project, profile),
        };
    }

    private string? BuildSkillsDeveloperInstructions(
        WorkThreadDescriptor thread,
        ProjectDescriptor? project,
        RoleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(profile);

        if (_skillCatalog is null || _catalogOptions is null)
        {
            return null;
        }

        var descriptors = _skillCatalog.ListAsync(
                new SkillCatalogQuery
                {
                    Discovery = CreateDiscoveryContext(thread, project),
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

        var preferredSkills = new HashSet<string>(
            profile.Skills
                .Where(static skill => !string.IsNullOrWhiteSpace(skill))
                .Select(static skill => skill.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        builder.AppendLine("Filesystem skills are available for this session.");
        builder.AppendLine("Activate a skill only when it clearly matches the current task.");
        if (preferredSkills.Count > 0)
        {
            builder.AppendLine("Role-associated skills are marked as preferred hints, not mandatory prompt preloads.");
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

    private SkillDiscoveryContext CreateDiscoveryContext(WorkThreadDescriptor thread, ProjectDescriptor? project)
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

    private string LoadDefaultSystemPrompt()
    {
        var promptPath = _contentLocator.ResolveBuiltInPromptPath(Path.Combine("base", "default.system-prompt.md"));
        if (!File.Exists(promptPath))
        {
            throw new InvalidOperationException($"Built-in system prompt '{promptPath}' was not found.");
        }

        var prompt = File.ReadAllText(promptPath).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Built-in system prompt '{promptPath}' is empty.");
        }

        return prompt;
    }
}
