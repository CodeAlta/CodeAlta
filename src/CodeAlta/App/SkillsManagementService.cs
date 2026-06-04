using System.Globalization;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.App;

internal sealed class SkillsManagementService
{
    private static readonly string[] RelatedFileDirectories = ["scripts", "references", "assets"];

    private readonly SkillCatalog _skillCatalog;
    private readonly CatalogOptions _catalogOptions;
    private readonly CodeAltaConfigStore _configStore;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;

    public SkillsManagementService(
        SkillCatalog skillCatalog,
        CatalogOptions catalogOptions,
        Func<ProjectDescriptor?> getSelectedProject,
        CodeAltaConfigStore? configStore = null)
    {
        ArgumentNullException.ThrowIfNull(skillCatalog);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getSelectedProject);

        _skillCatalog = skillCatalog;
        _catalogOptions = catalogOptions;
        _configStore = configStore ?? new CodeAltaConfigStore(catalogOptions);
        _getSelectedProject = getSelectedProject;
    }

    public bool HasSelectedProject
        => !string.IsNullOrWhiteSpace(_getSelectedProject()?.ProjectPath);

    public async Task<IReadOnlyList<SkillDescriptor>> LoadAsync(
        SkillsManagementScope scope,
        CancellationToken cancellationToken = default)
    {
        var selectedProject = _getSelectedProject();
        var query = new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext
            {
                ProjectRoots = scope is SkillsManagementScope.CurrentProject or SkillsManagementScope.Combined &&
                               !string.IsNullOrWhiteSpace(selectedProject?.ProjectPath)
                    ? [selectedProject.ProjectPath]
                    : [],
                UserCodeAltaRoot = scope is SkillsManagementScope.User or SkillsManagementScope.Combined
                    ? _catalogOptions.GlobalRoot
                    : null,
                UserProfileRoot = scope is SkillsManagementScope.User or SkillsManagementScope.Combined
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : null,
            },
            GlobalDisabledSkillNames = _configStore.LoadGlobalDisabledSkillNames(),
            ProjectDisabledSkillNames = _configStore.LoadProjectDisabledSkillNames(selectedProject?.ProjectPath),
            IncludeDisabled = true,
            IncludeInvalid = true,
            IncludeShadowed = true,
            IncludeUntrusted = true,
        };

        return await _skillCatalog.ListAsync(query, cancellationToken);
    }

    public SkillEnablementUpdateResult SetSkillEnabled(
        SkillEnablementScope scope,
        string skillName,
        bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        var normalizedName = NormalizeEnablementSkillName(skillName);
        return UpdateSkillEnablement(scope, [normalizedName], static (_, targetEnabled) => targetEnabled, enabled);
    }

    public SkillEnablementUpdateResult SetSkillsEnabled(
        SkillEnablementScope scope,
        IReadOnlyList<string> skillNames,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(skillNames);
        return UpdateSkillEnablement(scope, skillNames, static (_, targetEnabled) => targetEnabled, enabled);
    }

    public SkillEnablementUpdateResult InvertSkillsEnabled(
        SkillEnablementScope scope,
        IReadOnlyList<string> skillNames)
    {
        ArgumentNullException.ThrowIfNull(skillNames);
        return UpdateSkillEnablement(scope, skillNames, static (currentlyEnabled, _) => !currentlyEnabled, enabled: true);
    }

    public async Task<SkillCreationResult> CreateSkillAsync(
        SkillsManagementScope scope,
        string? name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeSkillName(name);
        var normalizedDescription = NormalizeDescription(description);
        var target = ResolveCreationTarget(scope);
        var skillRootPath = Path.Combine(target.RootPath, normalizedName);
        if (Directory.Exists(skillRootPath))
        {
            throw new InvalidOperationException($"Skill '{normalizedName}' already exists at '{skillRootPath}'.");
        }

        Directory.CreateDirectory(skillRootPath);
        Directory.CreateDirectory(Path.Combine(skillRootPath, "scripts"));
        Directory.CreateDirectory(Path.Combine(skillRootPath, "references"));
        Directory.CreateDirectory(Path.Combine(skillRootPath, "assets"));

        var skillFilePath = Path.Combine(skillRootPath, "SKILL.md");
        await File.WriteAllTextAsync(
                skillFilePath,
                BuildSkillTemplate(normalizedName, normalizedDescription),
                cancellationToken)
            ;

        return new SkillCreationResult(normalizedName, skillRootPath, skillFilePath, target.Kind);
    }

    public IReadOnlyList<SkillRelatedFile> ListRelatedFiles(
        SkillDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.SkillRootPath) ||
            !Directory.Exists(descriptor.SkillRootPath))
        {
            return [];
        }

        var skillRootPath = Path.GetFullPath(descriptor.SkillRootPath);
        var relatedFiles = new List<SkillRelatedFile>();
        foreach (var relativePath in _skillCatalog.ListFiles(descriptor, cancellationToken))
        {
            var category = GetRelatedFileCategory(relativePath);
            if (category is null)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(skillRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnderRoot(fullPath, skillRootPath))
            {
                continue;
            }

            relatedFiles.Add(new SkillRelatedFile(category, relativePath, fullPath));
        }

        return relatedFiles
            .OrderBy(static file => Array.IndexOf(RelatedFileDirectories, file.Category))
            .ThenBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToArray();
    }

    private SkillEnablementUpdateResult UpdateSkillEnablement(
        SkillEnablementScope scope,
        IReadOnlyList<string> skillNames,
        Func<bool, bool, bool> resolveEnabled,
        bool enabled)
    {
        var normalizedNames = skillNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeEnablementSkillName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedNames.Length == 0)
        {
            return new SkillEnablementUpdateResult(0, 0);
        }

        var globalChanged = 0;
        var projectChanged = 0;
        if (scope is SkillEnablementScope.Global or SkillEnablementScope.Both)
        {
            var disabled = new HashSet<string>(_configStore.LoadGlobalDisabledSkillNames(), StringComparer.OrdinalIgnoreCase);
            globalChanged = ApplyEnablementChanges(disabled, normalizedNames, resolveEnabled, enabled);
            if (globalChanged > 0)
            {
                _configStore.SaveGlobalDisabledSkillNames(disabled);
            }
        }

        if (scope is SkillEnablementScope.Project or SkillEnablementScope.Both)
        {
            var projectRoot = ResolveSelectedProjectRoot();
            var disabled = new HashSet<string>(_configStore.LoadProjectDisabledSkillNames(projectRoot), StringComparer.OrdinalIgnoreCase);
            projectChanged = ApplyEnablementChanges(disabled, normalizedNames, resolveEnabled, enabled);
            if (projectChanged > 0)
            {
                _configStore.SaveProjectDisabledSkillNames(projectRoot, disabled);
            }
        }

        return new SkillEnablementUpdateResult(globalChanged, projectChanged);
    }

    private static int ApplyEnablementChanges(
        HashSet<string> disabled,
        IReadOnlyList<string> skillNames,
        Func<bool, bool, bool> resolveEnabled,
        bool enabled)
    {
        var changed = 0;
        foreach (var skillName in skillNames)
        {
            var currentlyEnabled = !disabled.Contains(skillName);
            var targetEnabled = resolveEnabled(currentlyEnabled, enabled);
            if (targetEnabled == currentlyEnabled)
            {
                continue;
            }

            if (targetEnabled)
            {
                disabled.Remove(skillName);
            }
            else
            {
                disabled.Add(skillName);
            }

            changed++;
        }

        return changed;
    }

    private string ResolveSelectedProjectRoot()
    {
        var selectedProject = _getSelectedProject();
        if (string.IsNullOrWhiteSpace(selectedProject?.ProjectPath))
        {
            throw new InvalidOperationException("Select a project before changing project skill enablement.");
        }

        return selectedProject.ProjectPath;
    }

    private static string? GetRelatedFileCategory(string relativePath)
    {
        foreach (var directoryName in RelatedFileDirectories)
        {
            if (relativePath.StartsWith(directoryName + "/", StringComparison.OrdinalIgnoreCase))
            {
                return directoryName;
            }
        }

        return null;
    }

    private static bool IsUnderRoot(string candidatePath, string rootPath)
    {
        var normalizedCandidate = AppendDirectorySeparator(Path.GetFullPath(candidatePath));
        var normalizedRoot = AppendDirectorySeparator(Path.GetFullPath(rootPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return normalizedCandidate.StartsWith(normalizedRoot, comparison);
    }

    private static string AppendDirectorySeparator(string path)
        => path.Length > 0 && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private SkillCreationTarget ResolveCreationTarget(SkillsManagementScope scope)
    {
        var selectedProject = _getSelectedProject();
        if (scope is SkillsManagementScope.CurrentProject or SkillsManagementScope.Combined &&
            !string.IsNullOrWhiteSpace(selectedProject?.ProjectPath))
        {
            return new SkillCreationTarget(
                SkillCreationTargetKind.ProjectCodeAlta,
                Path.Combine(selectedProject.ProjectPath, ".alta", "skills"));
        }

        if (scope == SkillsManagementScope.CurrentProject)
        {
            throw new InvalidOperationException("Select a project before creating a project skill.");
        }

        if (string.IsNullOrWhiteSpace(_catalogOptions.GlobalRoot))
        {
            throw new InvalidOperationException("CodeAlta global root is not configured.");
        }

        return new SkillCreationTarget(
            SkillCreationTargetKind.UserCodeAlta,
            Path.Combine(_catalogOptions.GlobalRoot, "skills"));
    }

    private static string NormalizeSkillName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Skill name is required.", nameof(name));
        }

        var normalized = name.Trim();
        if (normalized.Length > 64)
        {
            throw new ArgumentException("Skill name must be 64 characters or fewer.", nameof(name));
        }

        if (normalized.StartsWith("-", StringComparison.Ordinal) ||
            normalized.EndsWith("-", StringComparison.Ordinal) ||
            normalized.Contains("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Skill name may not start or end with '-' and may not contain consecutive hyphens.", nameof(name));
        }

        foreach (var rune in normalized.EnumerateRunes())
        {
            if (rune.Value == '-')
            {
                continue;
            }

            if (!Rune.IsLetterOrDigit(rune) ||
                (Rune.IsLetter(rune) && Rune.ToLowerInvariant(rune) != rune))
            {
                throw new ArgumentException("Skill name must contain only lowercase Unicode alphanumeric characters and hyphens.", nameof(name));
            }
        }

        return normalized;
    }

    private static string NormalizeEnablementSkillName(string? name)
        => NormalizeSkillName(name?.ToLowerInvariant());

    private static string NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Skill description is required.", nameof(description));
        }

        var normalized = string.Join(
            ' ',
            description.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length > 1024)
        {
            throw new ArgumentException("Skill description must be 1024 characters or fewer.", nameof(description));
        }

        return normalized;
    }

    private static string BuildSkillTemplate(string name, string description)
    {
        var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('-', ' '));
        return
            $"""
            ---
            name: {name}
            description: '{EscapeYamlSingleQuoted(description)}'
            ---

            # {title}

            Use this skill when a task clearly matches this workflow.

            ## When to use

            - Describe the situations where this skill should be activated.

            ## Workflow

            1. Review the user's request and confirm this skill applies.
            2. Load any supporting files from `references/`, `scripts/`, or `assets/` only when needed.
            3. Follow normal CodeAlta approval and tool-use rules; do not execute scripts automatically.
            """;
    }

    private static string EscapeYamlSingleQuoted(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}

internal enum SkillsManagementScope
{
    Combined,
    CurrentProject,
    User,
}

internal sealed record SkillRelatedFile(string Category, string RelativePath, string FullPath);

internal sealed record SkillCreationResult(string Name, string SkillRootPath, string SkillFilePath, SkillCreationTargetKind TargetKind);

internal enum SkillCreationTargetKind
{
    ProjectCodeAlta,
    UserCodeAlta,
}

internal enum SkillEnablementScope
{
    Global,
    Project,
    Both,
}

internal readonly record struct SkillEnablementUpdateResult(int GlobalChanged, int ProjectChanged)
{
    public int TotalChanged => GlobalChanged + ProjectChanged;
}

internal sealed record SkillCreationTarget(SkillCreationTargetKind Kind, string RootPath);
