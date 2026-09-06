namespace CodeAlta.Catalog.Skills;

/// <summary>Roots to include in a management listing; registered built-ins remain visible.</summary>
public enum SkillListingScope
{
    /// <summary>Project and user roots.</summary>
    Combined,
    /// <summary>Project roots only.</summary>
    Project,
    /// <summary>User roots only.</summary>
    User,
}

/// <summary>A writable CodeAlta authoring location. Built-in and plugin roots are not creation targets.</summary>
public enum SkillCreationTargetKind
{
    /// <summary>The explicit project's .alta/skills directory.</summary>
    ProjectCodeAlta,
    /// <summary>The explicit global root's skills directory.</summary>
    UserCodeAlta,
}

/// <summary>Configuration files to update for name-based skill enablement.</summary>
public enum SkillEnablementScope
{
    /// <summary>Global configuration.</summary>
    Global,
    /// <summary>Project configuration.</summary>
    Project,
    /// <summary>Global then project configuration; not a multi-file transaction.</summary>
    Both,
}

/// <summary>An authoring resource under scripts, references, or assets.</summary>
/// <param name="Category">Conventional folder name.</param>
/// <param name="RelativePath">Slash-separated path relative to the skill root.</param>
/// <param name="FullPath">Local absolute path, not a renderer access grant.</param>
public sealed record SkillRelatedFile(string Category, string RelativePath, string FullPath);

/// <summary>A successfully published skill scaffold.</summary>
/// <param name="Name">Normalized skill name.</param>
/// <param name="SkillRootPath">Absolute final directory.</param>
/// <param name="SkillFilePath">Absolute SKILL.md path.</param>
/// <param name="TargetKind">The requested authoring location.</param>
public sealed record SkillCreationResult(string Name, string SkillRootPath, string SkillFilePath, SkillCreationTargetKind TargetKind);

/// <summary>Number of changed skill names in each configuration file.</summary>
/// <param name="GlobalChanged">Global changes.</param>
/// <param name="ProjectChanged">Project changes.</param>
public readonly record struct SkillEnablementUpdateResult(int GlobalChanged, int ProjectChanged)
{
    /// <summary>Gets the total number of changes across configuration files.</summary>
    public int TotalChanged => GlobalChanged + ProjectChanged;
}
